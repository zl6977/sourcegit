# Windows client and Linux server deployment

The Server runs as the selected WSL/SSH user. It requires Linux x64 or arm64, Git, POSIX sh, tar, sha256sum, flock and nohup, plus the native dependencies of a self-contained .NET 10 application (glibc, libstdc++, ICU, OpenSSL and zlib). It does not require a separately installed .NET runtime. Windows needs wsl.exe for WSL and OpenSSH for SSH.

## Matching release packages

```powershell
./build/scripts/publish-remote.ps1 -Runtimes linux-x64,linux-arm64 -IncludeClient
```

The script hashes the client/module sources into a build ID, injects that same ID into Contracts, Server and Client, publishes self-contained Linux servers, and writes a SHA-256 file and manifest for each tar archive. Client output includes matching Linux archives under `client/server/`. A Native AOT Windows publish requires the Visual Studio C++ desktop toolchain and Windows SDK/linker. An SDK Debug build does not prove Native AOT compatibility.

An explicit `-BuildId <release-id>` may be supplied by a release pipeline; release IDs must be unique to their source and dependency inputs. Never reuse a release ID for changed binaries. Client and Server reject a mismatched build or protocol. Development builds default to `remote-1`; tests that update binaries should use a fresh isolated installation/record or publish with a unique test build ID.

The connection manager chooses the x64 or arm64 package after inspecting the actual host architecture. A user supplied package requires a supplied digest or an adjacent `.sha256` file. Deployment uploads the local archive through the authenticated startup channel, verifies it remotely, then installs under `~/.sourcegit-server/versions/<build-id>/` while holding an installation lock. Network access from the remote host to a download site is not needed.

## WSL connection

Select a WSL 2 distribution and optional Linux user. The startup command explicitly selects both, uses noninteractive `sh`, and connects to the Server's random loopback port through WSL localhost forwarding. A failed localhost connection reports a networking error and does not enable a public listener. NAT and mirrored mode must each be validated on a host configured for that mode.

Optional environment initialization is read from `~/.sourcegit-server/server-env-setup`. This is a Bourne shell script, sourced before Server launch. It can export PATH, proxy variables and an existing SSH agent socket. It must not depend on interactive shell prompts. Startup logs are in `~/.sourcegit-server/server-<build-id>.log`; do not put secrets in that script's diagnostic output.

## SSH connection

Use an SSH Host alias from the Windows OpenSSH configuration (a plain `host` or `user@host` also works). Key/agent authentication is the default; ProxyJump and other host settings from `~/.ssh/config` still apply, and the per-connection **Extra SSH options** field appends command-line options (`-o KexAlgorithms=…`, `-p port`, `-i key`, …) that outrank the system config. The **Remote user** field is sent as `-l` (it wins over a user embedded in the target; on Windows a missing user would otherwise default to `DOMAIN\username`). Two password modes exist for hosts without keys: a **stored password** (encrypted with Windows DPAPI in the connection profile, plaintext never on disk; every ssh process of the connection signs in silently via an injected askpass, the first host key is accepted automatically, later changes still fail) and **terminal login** (OpenSSH's own prompt in a console window; each ssh process authenticates on its own, so expect a prompt per stage). Host key verification is retained in key mode. Repository Git authentication runs independently on the Linux host.

The Server binds loopback only. A separate `ssh -N -L` process forwards a local loopback port to it; startup command output is not the business protocol. The handshake verifies the expected instance ID so a load-balanced SSH alias cannot silently connect to a different host.

## Recovery and server lifetime

The GUI process has one owner identity and recovery secret. Multiple connections from that GUI may attach to the same Server; a different GUI is rejected while the owner is connected or within its recovery grace. The default grace is five minutes, configurable through `SOURCEGIT_GRACE_SECONDS` in the startup environment.

Git writes and filesystem mutations have client-generated operation IDs. Reconnection queries those IDs; it does not repeat writes automatically. An instance restart returns an unknown outcome for lost operation records, so repository state must be refreshed before a user retries. Editor/askpass waits use operation-specific helper credentials and expire at the configured grace. Pending editor/credential requests are cached until UI handlers are attached after reconnect.

Closing the GUI disconnects its channels. The Server completes outstanding writes, then exits after the idle grace. The authenticated `Shutdown` API refuses shutdown while writes or interactions are active. Source upgrades use a new version directory; rollback uses the matching older Client and its older Server package. Other version directories are retained, allowing rollback without replacing a running executable.

## Cleanup and diagnosis

Keep repositories intact. After all clients have disconnected, all Git operations have ended, and the Server has exited, an administrator may remove a specific unused `~/.sourcegit-server/versions/<build-id>/` directory. Complete uninstall removes only `~/.sourcegit-server/` after those conditions are met. Do not delete a running version, instance record or lock to force ownership takeover.

Instance records contain a connection token and are mode 0600 on Linux, inside the mode 0700 server directory. Do not attach those records to bug reports. Logs and connection-state messages can be shared after checking environment-script output for secrets. Include the build ID, WSL distribution or SSH alias, architecture, Git version and the failed connection stage.

Actual acceptance evidence belongs in `TEST_RESULTS.md` and `REVIEW.md`; this document describes the implementation and operator procedure, not a claim that every environment in the plan has been tested.
