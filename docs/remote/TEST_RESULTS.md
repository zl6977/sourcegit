# Remote architecture validation

Status: testing in progress. Unexecuted checks are not passes.

## Environment investigation (2026-10-02)

- Windows .NET SDK 10.0.201, runtime 10.0.5, Git 2.53.0.windows.2.
- OpenSSH_for_Windows 9.5p2 verified using an approved escalated command. Sandboxed PATH intentionally resolves SSH to a deny wrapper; no bypass was attempted.
- Sandboxed WSL enumeration returned `Wsl/EnumerateDistros/Service/E_ACCESSDENIED`; this agent's initial elevated process creation also failed with OS error 5. The root agent subsequently successfully enumerated Ubuntu-24.04 (running, default WSL 2), NixOS and archlinux (stopped WSL 2). Therefore WSL is available, not absent.
- Initial cache contained Windows x64 runtime and Native AOT packages. Linux x64 runtime restoration and self-contained publication subsequently succeeded.
- Initial `global.json` requested invalid SDK feature band `10.0.0`; executor corrected it. This was an environment/configuration issue discovered before execution.

## Automated integration runner

`tests/SourceGit.Remote.Tests` is a dependency-free .NET console test runner. It exercises actual TCP RPC and Git in newly created test repositories, leaves fixtures for diagnosis, and exits nonzero on failure.

```powershell
dotnet build tests/SourceGit.Remote.Tests/SourceGit.Remote.Tests.csproj --nologo -p:NuGetAudit=false
dotnet run --no-build --project tests/SourceGit.Remote.Tests -- --server modules/SourceGit.Server/bin/Debug/net10.0/sourcegit-server.dll
```

Against a running WSL/SSH server, use a locally readable protected record file, a fresh remote root and optional forwarded port:

```powershell
dotnet run --no-build --project tests/SourceGit.Remote.Tests -- --record <record.json> --root /tmp/sourcegit-test-<unique-id> --port <forwarded-port>
```

Do not publish the record file: it contains the server connection token. The runner does not print the token.

## Results

2026-10-02, root agent reran the current Windows server DLL against real temporary repositories:

```powershell
dotnet run --project tests/SourceGit.Remote.Tests -- --server modules/SourceGit.Server/bin/Debug/net10.0/sourcegit-server.dll --slow
```

Result: **18 passed, 0 failed**, terminal exit 0. Tests cover malformed frame lengths, authentication, second owner rejection, repository init, binary transfer, watcher notifications, stage/commit/history/diff, read-only write rejection, deduplication with parameter mismatch, accepted write recovery after disconnect, unknown result, traversal rejection, actual Git editor helper, merge conflict/abort, worktree common-directory metadata, bare repositories, and server-scheduled fetch. The fetch test waited for the one-minute server timer, observed a fetched remote ref, and verified a disabled remote remained unfetched; the client issued no fetch request.

The GUI adapter runner also completed with terminal exit 0:

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT='1'
dotnet run --project tests/SourceGit.GuiRemote.Tests -p:NuGetAudit=false
```

**7 scenarios passed**: text read/write, existing status/count parsers including global Git options, remote Git directory/version, stream truncation, pathspec staging/commit using server temporary files, ZIP archive transfer to a local output, and final-subscription repository lease release. The client had no configured Git executable; a separate Windows server ran Git. These are adapter tests without an Avalonia renderer, not proof of visual GUI behavior or a physically Git-free Windows installation.

GUI and Server Debug builds succeeded with 0 warnings and 0 errors.

### Independent Linux/WSL and SSH evidence

The tester executed actual WSL automatic installation through `ConnectionManager` using Ubuntu-24.04 and `build/remote/sourcegit-server-linux-x64.tar.gz`. The connector reached Connecting → Installing → Starting → Synchronizing → Connected, created a fresh `/tmp` repository and ran Git status successfully. Linux reported kernel `6.18.33.1`, x64, Git `2.55.0`. This was a real self-contained Server process, not mocked transport.

An initial Linux suite encountered an outdated installed development build whose error responses stalled. The latest package was explicitly unpacked into an isolated temporary directory, avoiding reuse of the old `remote-1` process, and all 17 scenarios passed. Windows testing also identified a path-normalization failure and a Git editor helper quoting failure; both were reported to executor and corrected rather than suppressed in the tests.

The latest independent publication and matching runner were built with:

```powershell
& build/scripts/publish-remote.ps1 -Runtimes linux-x64 -Output build/test-remote -BuildId remote-test-20261002-latest
dotnet build tests/SourceGit.Remote.Tests -p:SourceGitBuildId=remote-test-20261002-latest -p:NuGetAudit=false --nologo
```

Both commands exited 0. The package was unpacked to `/tmp/sourcegit-current.XEPMhX`; its launcher executable was given user execute permission, then started detached with a protected record file. The launcher terminated before the Windows test client connected, demonstrating the Server did not depend on its launch channel remaining open.

```powershell
dotnet tests/SourceGit.Remote.Tests/bin/Debug/net10.0/SourceGit.Remote.Tests.dll --record artifacts/wsl-current-record.json --root /tmp/sourcegit-current-suite --slow
```

**18 passed, 0 failed**, terminal exit 0. Evidence: `artifacts/wsl-current-results.txt`. This includes the latest server-scheduled auto-fetch test on Linux, real helper process, merge conflict/abort, worktree metadata, bare repository, arbitrary binary content, Unicode/space paths, ownership, deduplication and accepted-write recovery.

For SSH, the existing configured host rejected available authentication; localhost also lacked usable credentials. The tester used the already installed WSL `sshd` as an isolated user-owned process bound to `127.0.0.1:42227`. Temporary host/client keys and authorized keys stayed under `/tmp/sourcegit-ssh.E4J9h5` and ignored test artifacts; no service was installed and no system or user SSH configuration was changed. Windows verified the exact test host key and authenticated with the temporary key. An actual OpenSSH `-L` tunnel forwarded local port 42229 to the Linux Server's loopback port. The runner waited for the tunnel to listen before testing.

```powershell
dotnet tests/SourceGit.Remote.Tests/bin/Debug/net10.0/SourceGit.Remote.Tests.dll --record artifacts/ssh-current-record.json --port 42229 --root /tmp/sourcegit-current-ssh-suite
```

**17 passed, 0 failed**, terminal exit 0. Evidence: `artifacts/ssh-current-results.txt`. This proves the protocol and Git workflow over genuine encrypted SSH forwarding to Linux. It does not prove the product SSH connector's automatic deployment, MFA, jump-host handling or an external network host. A preceding run started before the tunnel was ready and failed its first connection check; it was not counted as a passing run.

### Windows GUI adapters against the Linux Server

The GUI adapter runner now accepts `--record` and `--root`, so the same production `src/Commands` and `src/Remote` code can be checked against a real Linux process. Its build completed with 0 warnings and 0 errors, using the matching test build ID above.

```powershell
dotnet tests/SourceGit.GuiRemote.Tests/bin/Debug/net10.0/SourceGit.GuiRemote.Tests.dll --record artifacts/wsl-gui-record.json --root /tmp/sourcegit-current-gui-suite
```

**8 scenarios passed**, terminal exit 0. Evidence: `artifacts/wsl-gui-results.txt`. In addition to the seven Windows-server adapter scenarios, the runner rewrites a tracked file with identical content and verifies that GUI status preserves the index bytes, exercising `--no-optional-locks`. All repository paths in this run were Linux paths; the test client had no configured Git executable. Archive export explicitly wrote to a Windows temporary output. These are production adapter integrations, not rendered Avalonia GUI tests.

The isolated SSH daemon was stopped after the passing transport suite, and its copied Windows test private key was removed. No permanent SSH configuration was changed.

### Recovery revision

The tester republished the later owner/channel and helper-recovery changes with matching build ID `remote-test-20261002-recovery` under `build/test-remote-v2` and ran the actual Linux executable in `/tmp/sourcegit-recovery.Zl2gqB`.

- `artifacts/wsl-recovery-results.txt`: **20 passed, 0 failed**, exit 0, including `--slow` auto-fetch, same-owner simultaneous channels and real Git editor recovery after disconnection. The latter intentionally binds the interaction handler after reconnect replay, verifies cached delivery, rejects shutdown while the write waits, then confirms the original `ExecuteGitAsync` task resumes result polling and completes successfully.
- `artifacts/wsl-ack-results.txt`: **20 passed, 0 failed**, exit 0, without the optional slow scenario and with an added acknowledgement-loss test. A raw authenticated TCP channel sends a write and closes without reading acceptance or completion; a surviving channel queries the pre-generated operation ID and verifies the resulting commit. No write request is replayed.

These runs replace inference about helper recovery and response-loss handling with actual fault-injection evidence. The subsequent conditional-file-replacement regression is implemented in the runner and awaits its matching publication/run.

## R25/R32 code re-validation (2026-10-02 → 2026-10-03)

The working tree now includes R25 (client adapter carries `GitResult.Generation` via `GitExecution`/`GitProcess.Generation`), R32 (`Native.OS` open actions reject remote URIs; editor downloads a server temp file), and the R36/R38/R41 fixes. All runs below executed in this session from `c:\gitRepoWin\sourcegit`; code presence is not runtime acceptance — these are the executed runs.

- `dotnet build SourceGit.slnx -p:NuGetAudit=false`: **0 warnings, 0 errors** (after all the changes above).

- Windows server suite (Debug DLL, local mode, git 2.53.0.windows.2): **21 passed, 0 failed**, exit 0, 39.8 s:

```powershell
dotnet run --project tests/SourceGit.Remote.Tests -p:NuGetAudit=false -- --server modules/SourceGit.Server/bin/Debug/net10.0/sourcegit-server.dll
```

- Fresh linux-x64 publication with content-derived build ID `remote-4358e2702f8cc93654ba` (SHA256 over sorted `modules/`+`src/` sources), package + `.sha256` + `.manifest.json` emitted:

```powershell
& build/scripts/publish-remote.ps1 -Runtimes linux-x64 -Output build/test-remote3
```

- WSL connector against that package (Ubuntu-24.04, WSL2, kernel 6.18.33.1, git 2.55.0): environment probe → digest-verified install into `~/.sourcegit-server/versions/remote-4358e2702f8cc93654ba` → detached start → build-matched handshake → empty-repo Git query. **PASS**, exit 0:

```powershell
dotnet run --project tests/SourceGit.Remote.Tests -p:NuGetAudit=false -p:SourceGitBuildId=remote-4358e2702f8cc93654ba -- --wsl Ubuntu-24.04 --package build/test-remote3/sourcegit-server-linux-x64.tar.gz
```

  The runner client must be rebuilt with the package's build ID so its `Protocol.Build` matches. A Debug client (`remote-1`) instead skips the install, reuses a previously installed `remote-1` binary, and handshakes successfully against the older server (documented R35 debug residual) — this session's initial unstamped run behaved exactly so and was not counted as package acceptance.

- WSL full suite against the same published build running detached under `~/.sourcegit-server` (record copied from `instance-remote-4358e2702f8cc93654ba.json`; record contains the token and was not published): **21 passed, 0 failed**, exit 0, 11.7 s. Includes watcher events, linked worktree, merge conflict/abort, editor-helper reconnect with late subscription, ownership/grace, operation dedupe, accepted-write recovery:

```powershell
dotnet run --project tests/SourceGit.Remote.Tests -p:NuGetAudit=false -p:SourceGitBuildId=remote-4358e2702f8cc93654ba -- --record build/wsl-record.json
```

- GUI adapter suite (no Avalonia renderer; local WSL server auto-started): **13/13 passed**, exit 0, 23.2 s, including the two new regression lines `PASS Native open actions reject remote URIs with explicit actions` (R32) and `PASS Client adapter carries repository generation for stale query detection` (R25).

Harness fix this session: the full-suite `--record` mode previously defaulted `testRoot` to a client-local Windows path, which a Linux server resolved relative to its own CWD (literal `c:\...` directories, broken watcher and linked-worktree scenarios, working-tree pollution). The runner now derives the root under the server's home directory (`<serverHome>/sourcegit-remote-tests-<guid>`) when `--root` is not given and no local server is started, matching the explicit `--root /tmp/...` usage of earlier independent runs. The polluted fixture tree was removed from the working tree.

## R42 slices 1-4, handle lifecycle, and auto-connect (2026-10-03)

Windows Server suite (local mode, `--server modules/SourceGit.Server/bin/Debug/net10.0/sourcegit-server.dll`, git 2.53.0.windows.2): **29 passed, 0 failed**, exit 0, ~49 s, with eight new regression items this round:

- `PASS Semantic status snapshot carries branch, changes and generation` — GetStatus branch header (name/oid/no-upstream), worktree change `(" ", "M")`, staged change `("M", " ")`, generation strictly increases across stage+commit, committed change disappears. This also pins the v2 parsing against real git output (path with space + non-ASCII: `hello space 中文.txt`).
- `PASS Semantic status preserves multiple changes, renames and untracked files` — a repository with a plain worktree modification, an untracked file and a `git mv` rename reports exactly three entries: the plain change with no original path, the untracked entry, and the rename with `OriginalPath` set to the old name. Added while re-auditing the v2 parser against real git output (empirically verified that record type `2` is exactly the rename entries carrying the trailing original-path record; types `1`/`u` never do).
- `PASS Semantic status reports tracking upstream and ahead/behind` — after `push -u`, the branch header carries `Upstream == "origin/main"` with zero counts; one local commit ahead reports `Ahead == 1, Behind == 0` (pins the `# branch.upstream` / `# branch.ab` signed parsing).
- `PASS Semantic refs snapshot lists branches and detects detached head` — GetRefs current-branch marker/oid/committer date, new branch appears, `IsDetachedHead`+`DetachedOid` set by `checkout --detach` and cleared by `checkout main`.
- `PASS Shared repository handles release the watcher only after the last close` (R19) — double `OpenRepository` shares one server session (same ID, `References` bumped); first `CloseRepository` leaves the session queryable and the watcher firing; the last close expires the handle (`GetStatus` on it fails); a fresh open creates a new session.
- `PASS Semantic commit detail carries fields and decorations` (R42 slice 3) — GetCommit returns id/parents/subject/author/committer/times; HEAD carries `HEAD -> main` decoration; the root commit carries no HEAD decoration and has no parents; a bogus 40-zero revision fails with `IOException`.
- `PASS Semantic blame maps lines to commits` (R42 slice 4) — two-commit file: line 1 maps to the root commit and line 2 to HEAD, `IsFirstInGroup` per boundary, reconstructed `Content` equals the file, author/timestamp present; blaming at the root revision yields one line; a binary file returns `IsBinary` with no lines. Blame SHAs are compared after the boundary-commit short-SHA unification the GUI parser also performs.
- `PASS Server rejects malformed frames and keeps serving` — an oversized frame header (claims 5 MiB > 4 MiB `MaxFrame`) and a non-JSON payload with a well-formed header each produce a rejection response on their own connections; the established session still answers queries and a fresh `Probe` handshake succeeds on a new connection. Closes the malformed-network-frame evidence line of the acceptance matrix (client side is structurally bounded: 4 MiB read limit, 256 MiB chunk accumulation cap, 4096-event backpressure, all read-loop exceptions route to disconnect/reconnect).
`--slow` variant: **30 passed, 0 failed**, 110.72 s at this round (re-run with the later submodule item: **31 passed, 0 failed**, 127.71 s, above).

Client-side handle lifecycle (R19 transient pairs in `RemoteWorkspace`) verified by a standalone smoke (`build/smoke-transient`, references the built `SourceGit.dll` so it runs while the user's GUI holds `src/bin` locks): **4/4 passed**, 13.5 s — command-only open/release through `ExecuteGitAsync`; repeated cycles (previous close released the server session); a command running under a live subscription releases its own reference without disposing the shared session (watcher event still delivered); command-only cycles still work after unsubscribe.

GUI adapter suite (`tests/SourceGit.GuiRemote.Tests`, the real GUI adapters without an Avalonia renderer; built and run with `-p:SourceGitBuildId=remote-s2 -p:OutputPath=<absolute dir>` to bypass the user-GUI `src/bin` copy locks while the client stamp matches the local test server): **13/13 passed**, 66.82 s, after all of this session's changes (transient handles, generation discards, semantic status/refs/commit/blame routing, count off-by-one fix). It caught one regression before it could ship: the remote `QueryLocalChanges` branch (slice 1) dropped `--no-optional-locks`, so a read-only remote status refreshed the Git index (server `git status` has no `--no-optional-locks` CLI option; the fix routes the existing `GitRequest.NoOptionalLocks` → `GIT_OPTIONAL_LOCKS=0` environment path through `QueryRequest`). The item `PASS GUI status preserves remote index with --no-optional-locks` pins it.

Submodule support through the remote stack found and fixed two product bugs this round:

- Server `ConfigOverrides` allowlist blocked `protocol.file.allow`. The GUI `Submodule` command builds `submodule add` with a leading `-c protocol.file.allow=always`; the raw channel strips leading `-c` pairs into `GitRequest.ConfigOverrides`, but the server allowlist accepted only four keys, so every remote `submodule add` failed with `UnauthorizedAccessException: Unsupported command config override`. The allowlist now also accepts `protocol.file.allow` pinned to exactly `always` — a non-executable transport-permission flag with no injection surface (RepositoryService.cs).
- `Submodule.UpdateAsync` lacked the file-transport flag. Fresh-clone `submodule update --init` with a file-path submodule URL fails `fatal: transport 'file' not allowed` under git >= 2.38's security default (reproduced locally: fresh clone + update fails, deinit/re-update on the original succeeds because `.git/modules` is reused). The flag now matches `AddAsync` (Submodule.cs).

New evidence:

- `PASS Remote submodule add and concurrent fresh-clone init work through the server` — inner bare + seed push, outer base commit, `submodule add` with the `protocol.file.allow=always` override (proves the allowlist fix), `submodule status` registration, then a fresh clone of the outer where **two concurrent** `submodule update --init` calls through the server both succeed (the per-common-directory operation gate serializes them: first clones, second no-ops) and `inner/inner.txt` materializes. Without the gate this races: two direct git invocations on the same worktree were verified locally to fail one side with `fatal: destination path ... already exists`. Server suite total is now **30 passed, 0 failed**.
- `PASS GUI Submodule command adds and initializes remote submodules` (GUI adapter suite, now **14/14 passed**, 80.08 s) — the real `SourceGit.Commands.Submodule` class end-to-end: `AddAsync` on a remote repository (exercises `GitProcess` → raw-channel `-c` stripping → ConfigOverrides) and `UpdateAsync` with `--init` on a fresh clone.
- Environment note: content assertions normalize CRLF before comparing because this machine has `core.autocrlf=true` (system and global config), which converts LF to CRLF between commit and checkout; WSL keeps LF. The server must not override the user's EOL configuration — remote behavior matches local behavior.
- `--slow` re-run with the submodule item: **31 passed, 0 failed**, 140.17 s (includes the concurrent fresh-clone init item).
- WSL acceptance with fresh package `build/test-remote-s2e` (server Core changed for the allowlist; test instance `remote-s2` state removed first): digest-verified install → start → handshake → `PASS Wsl actual deployment, handshake, empty repository and Git query`, exit 0, 4.01 s.


WSL acceptance with a freshly published package (separate build ID `remote-s2` to avoid the owner lock held by the user's running GUI on `remote-1`; Ubuntu-24.04, WSL2, kernel 6.18.33.1, git 2.55.0): digest-verified install into `~/.sourcegit-server/versions/remote-s2` → start → build-matched handshake → connected → `PASS Wsl actual deployment, handshake, empty repository and Git query`, exit 0:

```powershell
& build/scripts/publish-remote.ps1 -Runtimes linux-x64 -BuildId remote-s2 -Output build/test-remote-s2d
dotnet run --project tests/SourceGit.Remote.Tests -p:NuGetAudit=false -p:SourceGitBuildId=remote-s2 --no-build -- --wsl Ubuntu-24.04 --package build/test-remote-s2d/sourcegit-server-linux-x64.tar.gz
```

This re-validates the server changes of this session (v2 status parse, refs extension, detached detection, commit/blame semantic queries, `GIT_OPTIONAL_LOCKS` status flag, handshake rejection logging, read-only-aware deletion) on the second platform/git version; final re-run after the `NoOptionalLocks` change with fresh package `remote-s2` (`build/test-remote-s2d`): **PASS** again.

Notes:

- The user's GUI (running during this session) holds locks on `src/bin/Debug/net10.0/*`, so `dotnet build` of the src project shows MSB3021/MSB3027 copy errors only (Csc succeeds, 0 CS errors). The GUI adapter suite was re-verified with an `OutputPath` bypass (13/13, above); only the final 0-warning/0-error solution build with the standard bin copy remains pending GUI shutdown.
- A mid-suite kill of a WSL test client (pipe SIGPIPE) leaves the instance owned by that client. Under the original design the next owner attempt was then rejected for the 300 s grace (`SOURCEGIT_REJECTED InvalidOperationException: Instance owned by another GUI` in the server log — the new handshake logging working as intended) and the instance had to be killed with its record removed to re-accept a fresh owner. Since the owner-takeover change ("Owner takeover and crash-restart behavior" section), the next owner's handshake succeeds immediately once the killed client's connection is closed, so that manual cleanup is no longer required.
- `deleteTree` (remote repository deletion) now clears the Windows read-only attribute recursively before deleting. Discovered this round: on this machine git creates loose objects read-only (environment behavior), so `Directory.Delete` failed with `Access to the path '<object file>' is denied` for any repository containing committed objects. Per-file probes isolated it to the read-only attribute (objects open fine with `FileShare.Delete`, only the objects dir was affected); `Remove-Item -Force` succeeds because it clears the attribute, .NET `Directory.Delete` does not. The new blame item's cleanup failed 100% before the fix, passes after.

## Product launcher verification, SSH and WSL (2026-10-03)

Both product launcher paths (`ConnectionManager` with `Kind="Ssh"` and `Kind="Wsl"`) were verified end-to-end against the real Ubuntu-24.04 WSL distro. The SSH path targeted the distro's persistent root `sshd` on port 22 (reachable from Windows through WSL 2 localhost forwarding) and exercises the plan's launcher requirements: OS/arch/git detection over SSH, self-contained package auto-deployment (local upload when the remote cannot download), detached start that survives tunnel disconnect, `ssh -N -L` loopback tunnel, token handshake, and instance-ID verification ("SSH 目标需稳定落在同一主机，握手必须核对实例 ID"). The WSL path uses `wsl.exe --distribution <target> --exec sh -c` for detection/install/start and connects directly over WSL localhost forwarding (no tunnel), the plan's "WSL 通信使用随机本地端口" case.

Setup (all temporary, fully removed afterwards; no system or standing user SSH configuration was changed):

- Ed25519 test key generated locally (`C:\tmp\sourcegit-ssh-test`), public key appended to the WSL user's `~/.ssh/authorized_keys` (line removed after the run).
- Temporary host alias `sourcegit-wsl-test` appended to the user's `~/.ssh/config` (`HostName 127.0.0.1`, `User lizh`, `IdentityFile`, `IdentitiesOnly yes`, `KexAlgorithms curve25519-sha256@libssh.org`, `StrictHostKeyChecking accept-new`); config and `known_hosts` restored byte-for-byte from backup after the run.
- Smoke program `build/smoke-sshdeploy` (references the built modules, stamped `remote-s2` so `Protocol.Build` matches the package): builds `ConnectionOptions(..., <kind>, <target>, PackagePath: <s2e tar.gz>)` and runs `ConnectAsync` → `GetEnvironmentAsync` → `git init` + `rev-parse` through the connection → `deleteTree` cleanup → dispose (kills the tunnel where present; the server keeps running by design). Same program for both kinds (`args[2]` selects `Ssh`/`Wsl`).

Results:

- **Full auto-deploy** (install removed first): `Environment → Installing → Starting → Opening tunnel → Synchronizing → Connected`, 7.7 s. The 35 MiB package is piped over SSH stdin, digest-verified with `sha256sum -c`, installed under `~/.sourcegit-server/versions/remote-s2` behind a `flock`, started detached with `nohup`; the tunnel forwards a random local port to the server's loopback port; handshake token and instance ID verified; git 2.55.0 answered through the tunnel. `PASS Product SSH connector auto-deploys, tunnels, handshakes and runs git remotely`.
- **Fast path** (package already installed): 2.1 s, same outcome — install skipped by the versioned-directory check.
- **Owner exclusion (superseded by the user directive, see "Owner takeover" section below)**: at the time of this run, a second process connecting within the 300 s grace of the previous owner's disconnect was rejected with `SOURCEGIT_REJECTED InvalidOperationException: Instance owned by another GUI` in the server log and a failed handshake on the client. A new GUI process has a new owner identity, so same-process reconnect worked while cross-process takeover had to wait out the grace period (or the instance being stopped). Cross-process takeover is now immediate once the previous owner has no live connection.
- **WSL launcher**: `Environment → Starting → Synchronizing → Connected` (no tunnel stage), 2.4 s on the installed fast path; git 2.55.0 answered over direct WSL localhost TCP. `PASS Product Wsl launcher auto-deploys, connects, handshakes and runs git remotely`. The fresh-install WSL variant is covered by the WSL acceptance run above (same `Bootstrap` code path, `wsl.exe` channel).

Environment findings:

- **KEX incompatibility**: Windows OpenSSH 9.5p2 (LibreSSL 3.8.2) and the Ubuntu 24.04 OpenSSH 9.6p1 server negotiate `sntrup761x25519-sha512@openssh.com`, which the Windows client then cannot execute (`choose_kex: unsupported KEX method`) — every connection to this server fails without a per-host `KexAlgorithms` pin. The plan delegates SSH options to the system OpenSSH and the user's configuration, so this is an environment fact to surface to users (pin `KexAlgorithms` for affected hosts), not a connector change.
- Authentication at first version was key/agent only (`BatchMode=yes` on both the launcher channel and the tunnel) with host-key verification preserved by the system OpenSSH. (Superseded: the "SSH password, terminal and per-connection options" section below adds stored-password and terminal-login modes and the "Remote user" field is now honored for SSH. The stale `CreateLoginTerminal` reference in this line never existed in code — the login terminal was removed in the round-5 review and stayed removed.)

## Product reconnect and resource lifecycle (2026-10-03)

Auditing the reconnect path of the product launcher (`ConnectionManager`) surfaced four defects, all fixed and verified by a new end-to-end smoke (`build/smoke-reconnect`): connect → second `ConnectAsync` (fast path) → kill the server process → wait for the automatic reconnect loop → assert Git works through the new channel → (SSH) assert exactly one live `-L` tunnel.

- **Tunnel replacement leak**: replacing an SSH tunnel called `Process.Dispose` on the previous `ssh -N` process, which does not terminate a running process — every SSH reconnect leaked an orphaned tunnel holding its `-L` forwarding. Fix: `Kill(true)` then `Dispose`, matching `DisposeAsync`'s existing cleanup. Verified: after a kill-and-reconnect, exactly one live `-L` tunnel for the target (filtered by host to exclude unrelated environment tunnels).
- **`ConnectAsync` concurrency**: the `OnDisconnected` reconnect loop and an external explicit `ConnectAsync` could run simultaneously, each building a tunnel/client and racing the `Client` swap. Fix: a per-manager `SemaphoreSlim(1)` serializes the whole connect body; a fast path after the record is obtained returns the current client when it is still connected to the same `InstanceId`, avoiding duplicate tunnel/channel churn. Verified: the second `ConnectAsync` returns the same live client instance.
- **Dead `PendingOperations` migration**: the client swap copied the old client's `PendingOperations` into the new client after disposing the old one, but nothing anywhere reads the migrated dictionary (it is only added/removed inside `RemoteRepositoryClient`). Removed.
- **Non-idempotent `DisposeAsync`**: both `ConnectionManager` and `RemoteRepositoryClient` called `Cancel()` on an already-disposed `CancellationTokenSource` on a second dispose (e.g. `await using` plus an explicit dispose), throwing `ObjectDisposedException`. `IAsyncDisposable` must be safe to call repeatedly. Fix: a one-shot `Interlocked.Exchange` guard in each. Verified: the smoke disposes cleanly (exit 0).

Smoke results, both kinds: `PASS Wsl reconnect: fast path, auto-reconnect to new instance, git, channel reuse` and `PASS Ssh reconnect: fast path, auto-reconnect to new instance, git, no tunnel leak`. Closes the plan's connection-matrix lines 断线重连 (disconnect/reconnect), Server 重启 (server restart — the killed server returns as a new instance and the client follows it), and 重复连接 (duplicate connection — the second `ConnectAsync` reuses the live channel). The WSL/SSH suites and the full `--server` suite were re-run green after these changes.

## Owner takeover and crash-restart behavior (2026-10-03)

User directive: when the GUI is reopened and attempts to connect to a remote, it must first connect to the existing server instead of starting a new one, unless the user intends to. This supersedes the round-5 owner-grace ruling (300 s grace after the previous owner's disconnect).

Server-side change (`SourceGit.Server/Program.cs` handshake): a different owner is accepted as soon as the previous owner has **no live channel at all** and **no operation is in flight** (`accepted`/`running`/`waitingInteraction`); the grace-elapsed condition is removed from that check. The same owner is still always accepted (recovery-token fast path unchanged). Rejection is split by cause: `Instance owned by another GUI` (a live connection exists) vs `Server has active operations; wait for them to finish`. `SOURCEGIT_GRACE_SECONDS` (300 s default) keeps its other roles: idle listener stop (server idle exit) and interaction-wait timeout.

Launcher behavior (unchanged, re-verified by reading the Bootstrap start script): a running same-build instance is reused whenever the record's pid is alive and its cmdline matches version+record; a new process is only started when nothing valid is running. A different BuildId starts its own server under its own version directory (documented upgrade path, old version idles out).

Evidence:

- Server suite (local `--server`, 31 items): **31 passed, 0 failed** (`artifacts/r5-server26.log`), including the new item `A new GUI owner can take over after the previous owner disconnects` (owner A disconnects → owner B's handshake accepted immediately → owner C rejected while B is live → B disconnects → the reconnected original owner accepted again) and the retained `Reject simultaneous second GUI owner`.
- WSL acceptance (matching build freshly deployed, Ubuntu-24.04): `PASS Wsl actual deployment, handshake, empty repository and Git query`, exit 0 (`artifacts/r5-wsl8.log`).
- The fixed linux-x64 self-contained binary was deployed to the user's `~/.sourcegit-server/versions/remote-1` (the user GUI's default BuildId is `remote-1`); the deployed binary was smoke-started and its record self-identified as `"build":"remote-1"` before cleanup, so the user's reopened GUI connects to the updated server.

## Interaction simulation: server alone, client alone, full interaction (2026-10-03)

Goal: simulate a complete interaction and verify the client and the server each respond correctly on their own, not only together.

Harness: `build/interaction-test` (kept as standing evidence, like the `smoke-*` runners), run against the user's live WSL instance (`Ubuntu-24.04`, BuildId `remote-1`, git 2.55.0):

    dotnet run --project build/interaction-test -p:NuGetAudit=false -- build/update-remote-1/sourcegit-server-linux-x64.tar.gz

Phases (one live WSL instance is shared; each phase disconnects before the next, and the next owner takes over through the verified takeover path):

- **Phase 0 (warmup, product flow)**: `ConnectionManager` (Wsl) brings the instance up if the server had idle-exited; reuses the running instance otherwise.
- **Phase A (server alone)**: raw wire protocol only — 4-byte big-endian length + JSON `Envelope`, no client library. One-shot `Probe` on its own connection (the server closes that connection after replying, by design), then a second connection: raw-owner `Handshake`, `Ping`, `GetEnvironment` (answers `Unix 5.x`, `git version 2.55.0`, capabilities `git/files/watcher/interaction`). 4/4 checks.
- **Phase B (client alone)**: `RemoteRepositoryClient` only, no product launcher. Handshake (instance matched), environment query, `File` mkdir verified by `stat` (mkdir itself answers without a state payload), `git init` and `git commit` as tracked operations, `GetStatus` clean on `main`, `GetHistory` shows exactly the one commit. 7/7 checks.
- **Phase C (full interaction)**: the product flow (`ConnectionManager` Wsl, same options the GUI uses) connects to the **existing** instance (no second start), performs a second commit, the server is then killed with `pkill`; the client's built-in reconnect loop recovers to a **new** instance and both commits are visible after the restart; cleanup via `deleteTree`. 6/6 checks, including the diagnostics sequence `Connecting → Environment → Starting → Synchronizing → Connected → Reconnecting → … → Connected`.

Result: **17/17 checks, 14.32 s, exit 0** (`artifacts/interaction-1.log`; run twice — the first run also exercised the cold-start warmup after the server had idle-exited, which is the designed lifecycle). A second user hang report (14:32) then led to the root-cause fix documented in the next section: the hang was a transient WSL 2 forwarding stall *amplified* by a missing per-attempt connect timeout in the client.

## Connection hang fix: bounded per-attempt TCP connect (2026-10-03)

The user reported the GUI stuck on "Connecting" a second time (14:32). Live inspection of that incident: the GUI (freshly built 14:31:51 from the current source) had started the server normally at 14:32 (record + `SOURCEGIT_READY`), no launcher `wsl.exe` was still running, the server was healthy, the port answered `Test-NetConnection` from Windows minutes later, no Windows process squatted the port, and the same product flow run from the console against that very instance passed in seconds. The deterministic code defect this exposed: `RemoteRepositoryClient.ConnectAsync` used `TcpClient.ConnectAsync(host, port, cancellation)` with **no per-attempt timeout**. When the SYN is black-holed — WSL 2's lazy localhost NAT forwarding right after a new listener is the observed trigger (the GUI is the first to hit the fresh port seconds after the server starts; later runs hit an already-established forward) — each attempt waits out the full OS retransmit timeout (~21 s), and the manager's 50-attempt retry loop degenerates into a 17-minute "Connecting" with zero feedback.

Fix: each connect attempt is now bounded at **5 s** (`CancellationTokenSource.CancelAfter` around the connect, same pattern the in-product `ProbeAsync` already used at 3 s); on timeout the client throws `SocketException(10060)` so the manager's existing retry loop recreates the client and keeps going, and each failed attempt logs `connect attempt N/50 failed: …` to the dialog diagnostics so the UI is visibly alive. Worst case is now 50 × ~5.1 s ≈ 4.3 min with progress feedback; a recovering forward self-heals within one 5 s attempt.

Connection dialog changes requested by the user in the same round:

- The WSL distribution selector is now an **inline editable dropdown** (`ComboBox`, populated from `wsl.exe --list --quiet`, visible only for the Wsl transport; the SSH host field is a plain text box visible only for Ssh). The separate "Choose WSL distribution" button + mini-dialog is gone.
- **Browse remote directories was restored** (the user confirmed it is the useful button; the previous simplification removed the wrong one). `RemoteDirectoryBrowser` was rewritten: directory list with `..` navigation starting from the initial path or the remote home, `Choose this folder`/`Cancel`, list items rendered without a data template (string items + parallel `FileEntry` list), which removes the `FuncDataTemplate` null-item crash surface entirely while keeping the null-tolerant label construction.
- The "Prepare SSH host / agent" terminal button stays removed.

Regression: full interaction simulation re-run with the fix (**17/17, 14.68 s**, `artifacts/interaction-2.log`), Windows `--server` suite **31/31** (87.73 s, `artifacts/r5-server28.log`), GUI adapter suite 14/14 (`artifacts/gui-adapter-rerun9.log`), full solution build 0 warnings / 0 errors.

## Remote directory browse freeze: UI-thread sync-over-async deadlock (2026-10-03)

The user reported that clicking "Browse remote directories" in the connection dialog froze the entire GUI immediately. The browse window's `Opened` handler sync-waits on a `ListDirectory` RPC (`RemoteWorkspace.List` → `GetAwaiter().GetResult()`), the same sync-over-async pattern the GUI file/adapter layer uses everywhere.

Root cause: the client library's async chain captured the caller's `SynchronizationContext`. The GUI connects from the UI thread, so every `await` in `RemoteRepositoryClient`/`Protocol` (request write completion in `Protocol.WriteAsync`, response delivery in the read loop) posted its continuation to the Avalonia UI dispatcher. While the browse window blocks the UI thread in `GetResult()`, the continuation that would deliver the response sits in the dispatcher's queue waiting for the UI thread to pump — a permanent deadlock. It is not reproducible from a console (no ambient SynchronizationContext), which is why the plain-thread repro passed; the defect is UI-thread-specific. An instrumented repro with a queue-based SynchronizationContext (pumped while connecting, never pumped during the sync RPC) hung with exactly one pending queue item before the fix and completed in 2.8 s after it (`build/ui-deadlock-test`, `artifacts/ui-deadlock-run4.log` / `run5.log`).

Fix: every `await` in the client library (`RemoteRepositoryClient`, `ConnectionManager`, `Protocol`) now ends in `.ConfigureAwait(false)` — library code must never capture the caller's context — and the read/event loops are additionally started via `Task.Run` (double safety for the two pattern-match awaits that cannot take a suffix). This clears the same deadlock surface in every sync adapter path (`RepositoryFile`, `GitProcess`, `QuerySingleCommit`, …), not just the browser.

Regression: new GUI adapter suite item "UI-thread sync-wait on RPC does not deadlock protocol loops" (installs a queue-based SynchronizationContext, connects a fresh client while pumping, then issues a sync `ListDirectory` with no pumping). Suite **15/15 passed**, exit 0 (`artifacts/gui-adapter-rerun13.log`); full interaction simulation re-run with the client change **17/17** (`artifacts/interaction-3.log`); Windows `--server` suite re-run after the shared `Protocol` change **31/31** (`artifacts/r5-server31.log`); full solution build 0 warnings / 0 errors.
## Zombie owner channel: silent peer pins the instance (2026-10-03)

The user's WSL session exhibited a stuck spinner with endless retries after the `..` fix. Live forensics of the running instance (server pid 589763, port 38581):

- The GUI process was running with **zero TCP connections** to the instance port (`Get-NetTCPConnection -LocalPort 38581` empty, checked twice ~5 min apart).
- The WSL side showed **two ESTABLISHED** server sockets (peers `127.0.0.1:49265` and `:58179`) with no corresponding Windows endpoint — the remnants of the two earlier app crashes (16:14:46, 16:16:01). WSL 2 NAT forwarding did not deliver their FIN/RST to the Linux side, so the sockets stayed ESTABLISHED forever.
- The server's per-connection read loop only ends on EOF/error; a silent-but-ESTABLISHED channel never ends it, so `_connection`/`_owner` stayed pinned. A one-shot `owner-probe` (`build/owner-probe`) proved the instance reachable, and a full fresh-owner `ConnectAsync` against the live instance failed with exactly **`Instance owned by another GUI`** — the same rejection the GUI's reconnect loop (`ConnectionManager.OnDisconnected`, 2 s period) received on every attempt: probe OK → handshake rejected → wait → repeat, indefinitely. The pinned owner also blocked the server's idle exit, so the stale instance never went away on its own.

Fix (server): a channel whose peer has sent nothing for 60 s is cut by the idle timer (`Client.Close()` faults the blocked read, the `finally` removes the channel and releases ownership). A live client pings every 10 s, so 60 s is a 6× margin; the threshold is overridable via `SOURCEGIT_CHANNEL_GRACE_SECONDS` (same pattern as `SOURCEGIT_GRACE_SECONDS`). The timer callback is wrapped in try/catch: an unhandled exception on a timer thread would otherwise crash the whole server (observed once as a mid-suite "forcibly closed by the remote host" failure that the guard plus this round's clean re-run 32/32 eliminated).

Fix (deployment): a package content change under an **unchanged build id** could never reach an already-deployed instance — the install fast path only checked `test -x` on the deployed binary, so `versions/<build>/` kept serving stale code forever. The install now writes a `.package-sha256` marker next to the deployed binary; the check requires the marker to equal the current package digest (missing marker = stale, covering pre-fix deployments). On mismatch the version directory is re-extracted and any running instance whose binaries were replaced out from under it is restarted before the startup step.

Verification: new Server suite item "A new owner can take over after a silent owner channel expires" — a dedicated server with a 3 s channel grace, a raw-socket handshake that then goes silent (the exact zombie shape), a live owner rejected while the silent channel is held, then takeover succeeds after expiry with a working environment query. Suite **32/32**, exit 0 (`artifacts/r8-server32.log`); GUI adapter suite **15/15** (`artifacts/gui-adapter-rerun14.log`); `remote-1` package republished for linux-x64/arm64 with the fix (x64 sha256 `3ffc0617…`, `build/remote/`).

## Remote directory picker: unbounded ListBox layout (2026-10-03)

The user reported the browse dialog could not be navigated: no mouse-wheel scrolling and no scrollbar with 84 entries in the home directory. Root cause: `RemoteDirectoryBrowser` laid its contents out in a `StackPanel`, which gives the `ListBox` unbounded height. The list rendered all entries, overflowed the 560×440 window, and because its content fit its (infinite) viewport the built-in `ScrollViewer` never engaged — no scrollbar, wheel dead.

Fix: `Grid` with rows `Auto, *, Auto` (header / list / button row) so the `ListBox` is height-constrained and its built-in `ScrollViewer` handles wheel and scrollbar. The Cancel button, dropped accidentally in the first pass, is in the right-aligned button row next to Choose.

Verification: compiles 0 errors (GUI project; copy errors only from the user's running instance locking the output directory — expected until F5). Headless visual verification of an Avalonia window is not available; the user's F5 is the visual acceptance.
## Refresh feedback loop: self-triggered RepositoryChanged storm (2026-10-03)

The user retested after the zombie-owner fix: the connection succeeded (19:11 reconnect started a fresh server) but the circular spinner kept spinning in the same WSL repository (`/home/lizh/gitRepo/test`, 448 K, 54 files, `main` with 3 local changes). With server-side full tracing enabled (`SOURCEGIT_DEBUG_LOG=1`, env-gated; the WSL launcher sources `~/.sourcegit-server/server-env-setup` on new server starts), the live session produced:

- **742 `RepositoryChanged` events and 742 `OperationCompleted` events — exact 1:1** (every completed operation followed by exactly one repository event, and vice versa);
- **33 `GetRefs` requests cancelled in flight** ("The operation was canceled", 257–356 ms each) — refresh cycles replacing in-flight branch queries;
- **28,122 `OpenRepository` vs 28,109 `CloseRepository`** — the command path's transient handle borrow per git call, churning at loop rate;
- **Zero filesystem changes** in a 15 s `find -newermt` watch of the worktree and `.git` internals while the log grew by ~40,000 lines — the repository was physically quiet;
- stable inotify watch counts on the server process (no watcher leak), event rate peaking near ~80/s against ~400 ms serialized per-repo git queries.

Root cause: the 1:1 event/operation pairing is the signature of `RepositoryService.ExecuteAsync`'s post-operation `Notify` (a non-readonly `ExecuteGit` completion emits `RepositoryChanged`), not of the file watcher (whose 200 ms debounce would not track operations 1:1, and whose trigger — real file changes — was absent). The GUI's idle refresh cycle runs `QueryBranches` (`git branch -l --all -v --format=…`), `QueryCurrentBranch` (`git branch --show-current`) and `QueryTags` (`git tag -l --format=…`); none of the `branch`/`tag` list forms were in `RemoteWorkspace.IsReadOnly`'s read-only verb list, so every refresh registered them as server operations with operation ids. Each completion notified the session, the GUI's 100 ms watcher tick re-queued branches/commits/tags, and the loop self-sustained. Separately, the user-visible stuck ring: `Repository.RefreshCommits` sets `Histories.IsLoading = true`, and a refresh superseded mid-flight hit the cancellation early-return without resetting it — with a continuous event stream the in-flight refresh is perpetually superseded, so the history ring stays lit.

Fix: (1) `RemoteWorkspace.IsReadOnly` now classifies the `branch` list forms (`-l/--list`, `-a/--all`, `--show-current`, `--points-at`, `--contains`, `--no-contains`, `--merged`, `--no-merged`, `--remotes/-r`, `--sort`, `--format`, `-v`, `--abbrev`, `--color/--no-color`), the `tag` list forms (`-l/--list`, `--contains`, `--no-contains`, `--points-at`, `--sort`, `-n`, `--format`, `--color/--no-color`) and `ls-remote` as read-only — no operation id, no post-operation notify; mutation forms (`branch <name>`, `-d/-D`, `-m/-M`, `-c/-C`, `--track`, `tag <name>`, `-d`, `-a`, `-s`, …) remain operations and notify exactly once, as designed for real user actions. (2) `Repository.RefreshCommits` wraps the query in try/catch: a non-cancellation exception now clears `IsLoading` on the UI thread (a superseded refresh hands the flag to its replacement, so cancellation semantics are unchanged), which removes the freeze-the-ring failure class for any future mid-refresh exception. (3) A second, quieter loop driver found while regression-testing: on Windows hosts `.git` sits inside the watched worktree, so any git call that refreshes the index rewrites `.git/index` and the worktree watcher debounces a `RepositoryChanged` of its own. The GUI's `QueryBranches` routes to the `GetRefs` RPC, whose internal `status --porcelain=v2 --branch` ran without `GIT_OPTIONAL_LOCKS=0` (the request's `noOptionalLocks` flag was false on that path) — on a churning worktree every branch query refreshed the index and re-triggered the watcher. `RepositoryQueries.RefsAsync` now forces `NoOptionalLocks` on that internal status call; the refs snapshot only needs the `# branch.head (detached)` header, so the index refresh bought nothing.

Regression: new GUI adapter suite item R26 — after a stable generation, `QueryBranches`/`QueryTags`/`QueryCurrentBranch` (the exact GUI remote command path through `RemoteWorkspace.ExecuteGitAsync`) must leave the server repository generation unchanged (pre-fix they bumped it, re-triggering the loop). The R26 generation probe itself must be non-mutating: a plain `git status` probe refreshes the index and bumps the generation under itself on any host where the watcher sees `.git` (always true for worktree-inclusive watchers, e.g. every Windows host), so the probe runs `--no-optional-locks status` behind a short settle wait that lets the previous test's commit debounce land first.

Verification: Windows `--server` suite **32/32**, exit 0 (`artifacts/r11-server32.log`); GUI adapter suite **16/16**, exit 0 (`artifacts/gui-adapter-r18.log`, including R26); WSL real deployment (marker redeploy of the running instance) **pass** (`artifacts/wsl-deploy-r9.log`, 7 s); interaction simulation **17/17**, exit 0 (`artifacts/interaction-r9.log`, 14 s). `remote-1` republished for linux-x64/arm64 (linux-x64 sha256 `3b3b8919…`).

Live confirmation (user retest): the fresh WSL session (883 log lines, post-redeploy instance) shows **6 `RepositoryChanged` events, all watcher debounce, zero `gen-source=operation`** — the loop is gone in the user's real session. Side finding from the same log: **4 `FAILED` requests, "Unsupported command config override"**, with payloads unlogged (the pre-existing logging gap) — the only GUI commands whose `-c` overrides are outside the server allowlist are the external `difftool`/`mergetool` invocations, so external diff/merge tools currently fail silently on remote repositories. The server now logs the request payload on `FAILED`; the failing calls will be positively identified in the next traced session before the tool allowlist changes.

## WSL repository UNC display path (2026-10-03)

The user reported the repository list showed the opaque `sourcegit-remote://<connection-id>/<host path>` URI for WSL repositories and asked for a `\\wsl.localhost\…` path instead; the Linux part should use forward slashes (Linux style) by default.

Design: display-only. The `sourcegit-remote://` URI stays the canonical stored and routing form (the invariant comment in `RepositoryPath` now says so explicitly); nothing that feeds routing, I/O or persistence ever receives the friendly form. `RemoteConnections.WslDisplayPath(path, options)` is a pure function — Windows host + saved connection `Kind=Wsl` + matching connection id + distro name without path separators → `\\wsl.localhost\<distro>/<host path>` (UNC header with backslashes per UNC syntax, Linux part with forward slashes per the user's Linux-style preference — Windows accepts both separators interchangeably after the UNC header); any other input → null → unchanged display. `RemoteConnections.DisplayPath(path)` wraps it with a connection-configuration read cached by file mtime (list rendering does no I/O after the first hit). The new converter `PathConverters.RemoteDisplayPath` (falling back to the existing home-relative behavior for local paths) replaced the raw/`RelativeToHome` bindings at all six display sites: Welcome repository list, launcher tab tooltip, command-palette entry and tooltip, delete-repository dialog, and the worktree-path info line.

Verification: GUI adapter suite **17/17**, exit 0 — backslash form `gui-adapter-r19.log`, final forward-slash form `gui-adapter-r20.log` (101 s) — including new item R27, which pins the display format (UNC header backslashes + Linux part forward slashes, spaces and unicode) plus the null cases (SSH transport, unknown connection id, local path). A throwaway probe against the user's real `remote-connections.json` (9 saved WSL connections) rendered `sourcegit-remote://…/home/lizh/gitRepo/test` → `\\wsl.localhost\Ubuntu-24.04/home/lizh/gitRepo/test` (probe deleted after use; the permanent regression is R27). GUI compiles 0 errors 0 warnings. Visual acceptance (the list itself) is the user's next F5.

## Open-history address handling: display unification, conservative normalization, connecting-popup dismiss (2026-10-03)

The user reported that entries in the open history (the repository list of a new "+" tab) whose displayed address contains backslashes "do not open", and asked for an internal mechanism that conservatively converts non-standard address forms to supported ones.

Findings (ground truth from the user's live GUI + click test):
- The history/node storage is already canonical: all persisted UNC entries are forward-slash form (`//wsl.localhost/…`), all remote entries are `sourcegit-remote://…` URIs. No stored address is non-standard; the backslashes the user saw are the display layer (this round's `WslDisplayPath` applied to remote URIs).
- Click test (user): a backslash-rendered (remote) entry **does open** — after a slow cold start of the idle-exited WSL server. The perceived "cannot open" = cold-start latency plus the "Connecting to WSL…" notification that never dismissed (no notifications have auto-expiry; it lingered on the reused page).
- Windows shortcut mechanism probe (throwaway WScript.Shell harness): `\\wsl.localhost\…` and the mixed display form save and resolve; an all-forward-slash `//wsl.localhost/…` target is **rejected by Windows itself** (TargetPath set throws, readback empty) — relevant if the user ever builds .lnk shortcuts; the mixed display form is copy-paste safe.

Changes:
1. Display unification: `WslDisplayPath` now also renders local `//wsl.localhost/<distro>/…` UNC paths (distro parsed in place, no saved connection needed) in the same `\\wsl.localhost\<distro>/<host path>` form as remote URIs — the two WSL populations no longer look different in the list.
2. Conservative normalization on the open path: `Launcher.TryOpenRepositoryFromPath` (the single entry for dialog/drag-drop/file-activation paths) normalizes non-remote user-supplied paths to the canonical forward-slash form (`\` → `/`, trim) before any I/O; remote URIs pass through untouched.
3. Missing-entry feedback: `RepositoryNode.Open` on a local entry whose path no longer exists was a silent no-op; it now raises the same "Repository does NOT exist any more. Please remove it." error notification the tab path uses.
4. Connecting-popup dismiss (previous round's fix, verified this round): `Notification.Send` returns the created instance; `Launcher.OpenRemoteRepositoryAsync` captures it and dismisses it in `finally` (by reference, across all open pages) on both success and failure — no global auto-expiry, no behavior change for other notifications.
5. WSL server idle grace: the deployed launcher env (`~/.sourcegit-server/server-env-setup`, sourced by the start script on every cold start) now sets `SOURCEGIT_GRACE_SECONDS=1800` (server code already reads it) — the server stays warm 30 min instead of 5, making the one-time cold start on history click rare.

Verification: GUI adapter suite **17/17**, exit 0 (`artifacts/gui-suite-r29.log`) — R27 extended with the local-UNC cases (same display form as the remote twin; backslash input, bare `//wsl.localhost/` and non-wsl.localhost shares all stay null). Windows `--server` suite **32/32**, exit 0 (`artifacts/server-suite-r28.log`). GUI compiles 0 errors 0 warnings. Visual acceptance is the user's next launch: the list shows one WSL address style, clicking a stale entry shows an explicit error, and "Connecting to WSL…" disappears when the open finishes.

## Right-click crash on disconnected remote history entries + connection keep-alive confirmation (2026-10-03)

The user reported that right-clicking **some** history entries crashes the GUI while others do not. Three crash logs (`crashes/2026-10-03_21-49-43.log`, `_22-09-54.log`, `_22-10-22.log`) all show the same stack: `Welcome.OnTreeNodeContextRequested` → `RepositoryDirectory.Exists(node.Id)` → `RemoteWorkspace.Stat` → `RemoteWorkspace.ForPath` throws `IOException("Reconnect to the remote environment before opening this repository")` — unhandled on the UI thread, app exits. The "some do not" split = whether that connection's workspace was still connected at click time.

Keep-alive audit (the user asked for a client heartbeat to keep the WSL server from idle-exiting): the mechanism already exists and the server log proves it — the client sends a `Ping` every 10 s (`RemoteRepositoryClient` heartbeat timer), the channel survives after the last remote tab closes (log: pings continue after `CloseRepository`), and the server's idle exit requires **zero** channels (`_connection == null` + grace), so a running GUI keeps the server alive indefinitely. The 5-minute auto-reconnect window (`ConnectionManager.OnDisconnected`) covers transient channel drops. Residual gap: after the GUI closes/crashes, the server exits after the grace period — raised from 5 to 30 and then 120 min via the deployed launcher env (`SOURCEGIT_GRACE_SECONDS=7200` in `~/.sourcegit-server/server-env-setup`; the server code already reads it, no server change needed).

Changes: (1) `RepositoryDirectory.Exists` and `RepositoryFile.Exists` now return `false` for a remote path whose workspace is not connected (`RemoteWorkspace.IsConnected`) instead of throwing — existence checks are UI-thread synchronous calls (context menus, node gates) and a throw is an app crash; "cannot verify right now" reads as absent, and the open path (`RepositoryNode.Open` → `OpenRemoteRepositoryAsync` → `RestoreAsync`) reconnects with the Connecting notification when the user actually opens. (2) The Welcome context-menu gate now matches the open gate: a remote node always gets the full menu (Open/Explore/Terminal/...), even while its connection is down — before, a disconnected remote node fell into the "does not exist" branch (Delete only). (3) Live verification pending: with the server killed, right-clicking a remote history entry must not crash and must show the full menu; clicking Open reconnects.
A second, distinct crash was found live in the same session (crash log `2026-10-03_22-22-23.log`): `WorkingCopy.StageChangesAsync` / `UnstageChangesAsync` wrote the git pathspec file with a raw `new StreamWriter(pathSpecFile)` where `pathSpecFile` is a `sourcegit-remote://…/tmp/sourcegit-…` URI for remote repositories — .NET resolved it as a relative local path (`C:\gitRepoWin\sourcegit\sourcegit-remote:\…`), threw `IOException`, and the app exited on the UI thread (Stage All button). Both call sites now build the line list and use the remote-aware `RepositoryFile.WriteAllLinesAsync`, the same pattern the neighboring already-remote-safe call sites (`StashChanges`, `UseTheirs/UseMine`, `Discard`, `Commit`) use; the command layer already converts `--pathspec-from-file=sourcegit-remote://…` arguments to server host paths (`RemoteWorkspace.ExecuteGitAsync`). The GUI suite already regression-pins the remote temp-file + `Add` + commit mechanism (item at `Program.cs:121-129`), which is exactly the pattern restored here.
Verification (so far): GUI compiles 0 errors 0 warnings; GUI adapter suite with new item R28 (disconnected-path `Exists` returns false for both directory and file forms — the exact crash call, regression-pinned) — result in `artifacts/gui-suite-r30.log`. Live keep-alive + right-click verification follows in the same session.

## WSL UNC repositories rejected by git's dubious-ownership check (2026-10-03)

User report: some WSL entries in the open history "do not open" — the tab opens but is completely blank. The live-identified entry: `\\wsl.localhost\Ubuntu-24.04\home\lizh\gitRepo\ddbot-benchmark` (a **local UNC** entry, not a remote URI). Ground truth probe (`build/check-unc-repos.sh`, system git 2.53): of 18 WSL UNC repositories, **16 fail** with `fatal: detected dubious ownership in repository` and only 2 (ComfyUI, test) pass — the WSLg 9P share reports WSL file ownership as foreign SIDs to the Windows git client, and git's safe-directory check rejects the repository outright. That matches "some open, some do not" exactly.

Fix: `RepositoryPath.IsWslUnc` + `RepositoryPath.SafeOwnershipArg` — local git launches for WSL UNC working directories carry `-c safe.directory=*` (verified honored by git via both `-c` and `GIT_CONFIG_*` env forms). Deliberately scoped: ordinary local paths keep the check (it guards against executing hooks from repositories you do not own), and remote URIs are untouched (the server-side git runs under the correct owner). All 5 local git launch sites covered: `Command.CreateGitStartInfo` (main path for every `Commands.*` call), `QueryFileContent` (×2), `SaveChangesAsPatch`, `SaveRevisionFile`, `UpdateIndexInfo`.

Verification: GUI compiles 0 errors 0 warnings; GUI adapter suite with new item R29 (WSL UNC recognition in both delimiter forms; override present for UNC, absent for ordinary local and remote paths) — result in `artifacts/gui-suite-r32.log`. Live acceptance is the user's retest of ddbot-benchmark and the other ddbot-* UNC entries with the new build.

## Open-history duplicates: connection profiles and cross-route WSL twins (2026-10-03)

User report: the open history contains duplicate records because nothing checks for duplicates when adding. Live data audit found two distinct duplicate classes:

1. **Remote connection profiles**: `remote-connections.json` held **9 entries, all identical** (`Ubuntu-24.04`, WSL, no user, same package path) differing only in Id. Cause: the Remote-repositories dialog minted a fresh GUID per "Connect and open" (`_newId`) and only compared against the currently *selected* profile; `RemoteConnections.Save` deduped by Id alone — a new Id never matches.
2. **Cross-route history nodes**: the same physical WSL repository reachable by two routes appears twice in the Welcome tree — `ComfyUI` and `test` each existed as both a local `//wsl.localhost/…` UNC node and a `sourcegit-remote://<connId>/…` node. Cause: node identity is the stored path string; the two routes produce two different strings for one repository, and `FindNode` compares exact strings only.

Fix (dedup at add time, all add paths converge on two seams):

- Connection identity: `RemoteConnections.SameIdentity(saved, kind, target, user)` — same transport + same target (WSL distro names and SSH host aliases resolve case-insensitively; compared `OrdinalIgnoreCase`) + same user (user names are case-sensitive; null == ""). `RemoteConnections.Save` now reuses the saved Id of an identity twin instead of appending a duplicate; `RemoteRepositories.GetOptions` matches against **all** saved profiles (not just the selected one) so the `ConnectionManager`/workspace attach to the existing connection.
- Node twin: `RemoteConnections.WslTwinId(path, options)` — pure mapping between the two WSL routes (remote URI ↔ `//wsl.localhost/<distro>/<path>` stored form, distro matched case-insensitively; null for non-WSL, unknown connection, bare distro, local paths). `Preferences.FindNode` now falls back to the twin lookup after the exact match, so every add/open path (`FindOrAddNodeByRepositoryPath` — Clone / Open-local / Remote dialogs — and both `Launcher` `FindNode ?? new` sites) resolves the existing node instead of creating a second one. The existing node's route wins (no migration of open tabs or workspace entries).
- One-time data cleanup of the user's files (GUI closed; backups in `AppData/Roaming/SourceGit/backup-dedup-20261003/`): connections 9 → 1 (first entry kept as canonical); node Ids referencing the 4 removed connection Ids rewritten to the canonical one (thunderbird_bridge, snake_demo kept; the ComfyUI/test remote-route copies deleted in favor of the UNC entries present in the active workspace); verified 0 stale references, 0 duplicate names, workspaces intact.

Verification: GUI compiles 0 errors 0 warnings; GUI adapter suite with new item R30 (identity matrix: case-insensitive target, null/empty user, user/transport/target negatives; twin matrix: remote→UNC, UNC→remote, distro case, SSH/unknown/bare/local/non-WSL-UNC nulls) — **21/21** in `artifacts/gui-suite-r33.log`. Live retest (user-confirmed): Welcome tree shows ComfyUI/test once each; the Remote dialog profile list holds a single Ubuntu-24.04; re-adding the same endpoint does not duplicate; snake_demo and thunderbird_bridge open after the Id rewrite. No new crash logs.

## No auto-refresh on WSL UNC repositories: dead file watcher, polling fallback (2026-10-03)

User report: after fetching new commits from a remote git repository, the GUI does not auto-refresh and does not show the fetched commits.

Investigation. The fetch→event chain was first proven intact in isolation (throwaway probe, fresh server + bare/clone fixture): a `git fetch` executed through the server (the exact GUI command path, `Commands.Fetch` = `fetch --progress --verbose --tags <remote>`) emits `RepositoryChanged` (operation notify + file-watcher debounce), the client receives it, and `RemoteWorkspace.Changed` routes it to the `Subscribe` callback the GUI `Watcher` registers — routed callback count 0 → 2 (server-side fetch) → 4 (external fetch). So the remote-URI route refreshes correctly.

The break was on the **other** route. Live audit of the user's `preference.json`: of 28 repository nodes, 21 are `//wsl.localhost/Ubuntu-24.04/…` **UNC** nodes — and they are exactly the population that *can* be fetched (the `sourcegit-remote://` nodes — test, thunderbird_bridge, snake_demo — have no upstream remotes at all; ComfyUI, GPT-SoVITS, ddbot-* etc. all have remotes and all are UNC). The GUI treats UNC paths as local repositories, so their `Watcher` builds a Windows `FileSystemWatcher` on the UNC path. A live probe (`FileSystemWatcher` on `\\wsl.localhost\Ubuntu-24.04\home\lizh\gitRepo\test`, then a WSL-side file rename/create): **zero events, one `Error` — `Win32Exception (1) Incorrect function`**. The WSLg share does not support `ReadDirectoryChangesW` at all. The `Watcher` constructor swallowed the setup failure silently (bare `catch` around `EnableRaisingEvents`, no `Error` handler), so every UNC tab had **no refresh trigger whatsoever** — after a fetch, after a file change, after a terminal commit: nothing ever refreshed. This is the user's exact symptom (their only fetchable GUI repositories are UNC).

Fix (GUI-only, `src/Models/Watcher.cs`; no server change, no republish, no WSL server restart): when file watching is unavailable, degrade to periodic polling instead of silent never-refresh.

- Three failure seams all mark the watcher set `_degraded`: (1) the FSW creation block is now inside `try/catch` — a path that cannot even be set up (missing/invalid for `ReadDirectoryChangesW`; on .NET 10 `FileSystemWatcher.Path` throws `ArgumentException` for a nonexistent directory) degrades instead of escaping the constructor; (2) each `FileSystemWatcher` gets an `Error` handler (the UNC case — the share rejects the watch once started); (3) the existing `EnableRaisingEvents` catch now also marks degraded.
- `Tick` (100 ms timer, unchanged): when `_degraded` and the poll deadline is due, it re-arms the deadline (`PollIntervalSeconds`, default **30 s**, first deadline computed lazily at the first degraded tick so the interval can be set after construction) and raises all five refresh flags at `now - 1` via a CAS `MaxFlag` (never lowers a fresher flag), so the normal flag-processing path below performs the full refresh — `RefreshBranches`/`RefreshWorktrees`/`RefreshWorkingCopyChanges`/`RefreshSubmodules`/`RefreshStashes`/`RefreshTags`/`RefreshCommits`. Local-disk repositories are untouched: their watchers work, `_degraded` stays 0, refresh stays event-driven and immediate.
- Cost profile: polling runs only for open tabs whose path the OS refuses to watch, one bounded refresh wave per 30 s (≈6–8 read-only git queries per tab over the UNC share); the fetch popup's `LockWatcher` window still suppresses ticks as before (the deadline simply passes and the next unlocked tick refreshes).

Verification: GUI compiles 0 errors 0 warnings; GUI adapter suite with new item R31 (a `Watcher` over a nonexistent directory — the constructor-throw failure mode — with a 1 s poll interval must perform repeated full refreshes through the real `Tick` path) — **22/22** in `artifacts/gui-suite-r36.log`. Live end-to-end on the user's actual UNC path (`Watcher` + stub repository over `\\wsl.localhost\Ubuntu-24.04\home\lizh\gitRepo\test`, 2 s interval): branches/commits/working-copy/tags/stashes all refreshed on the poll cadence — PASS. The earlier isolated fetch probe additionally proves the remote-URI route needed no change. Live acceptance is the user's retest: fetch in a WSL UNC repository (e.g. ComfyUI, which has an `origin`) and observe the branch list/history updating within ~30 s without manual refresh.







## WSL repositories migrated to the server route: event-level refresh for the whole WSL population (2026-10-04)

User decision after the round-8 polling fallback: the 30 s cadence is not acceptable — ALL WSL repositories should open through the server route (`sourcegit-remote://`) with event-level (server-side inotify) refresh, no polling.

Why the migration is safe. `Launcher.OpenRemoteRepositoryAsync` already auto-connects for remote-URI tabs (3 attempts, "Connecting to …" notification, `RemoteConnections.RestoreAsync` → full deploy + start of the WSL server if it is down). Node identity is the stored path string and every downstream consumer (git commands, file operations, `Watcher`) dispatches on `RepositoryPath.IsRemote`, so storing the remote-URI form as the node `Id` switches the whole stack with no per-call changes.

Changes (GUI-only, no server change, no republish, no WSL server restart):

- `RemoteConnections.PreferredRoute(path, options)`: WSL UNC path → remote-URI twin when a saved WSL connection covers the distro; already-remote, non-WSL and local paths unchanged.
- Add-time normalization at the two node-identity seams: `Preferences.FindOrAddNodeByRepositoryPath` (dialog, clone, drag-drop, startup argument) and `Launcher.OpenRepositoryInTab(string, …)` — newly added WSL repositories are born on the server route.
- Load-time one-shot migration in `Preferences` (`MigrateWslUncNodes`, pure planner `PlanWslRouteMigration`): every WSL UNC node with a saved connection for its distro is renamed to its remote-URI twin (a pre-existing twin node wins — the UNC copy is dropped, sub-nodes merged); workspace `Repositories` references are rewritten; a one-time backup `preference.json.bak-wslroute` is written before the first save. Idempotent: no UNC nodes → no work, no write. The migration retries in `SetCanModify()` because `Save()` is blocked while the instance is read-only (the instance getter runs before the app lifts read-only mode) — the read-only guard makes the getter-side call a no-op instead of a silently lost save.
- The round-8 polling fallback in `Watcher` is retained as the generic safety net for any unwatchable local path (network shares, OneDrive, deleted directories); after the migration no WSL repository triggers it.

Verification: GUI compiles 0 errors 0 warnings; GUI adapter suite with new item R32 (`PreferredRoute` matrix — UNC→twin, distro case-insensitivity, no matching connection, non-WSL UNC, local, already-remote — plus the `PlanWslRouteMigration` matrix, including nested sub-nodes and no-change cases) — **23/23** in `artifacts/gui-suite-r38.log`. Live data migration on the user's own profile (28 nodes, 19 WSL UNC, 2 remote, 7 local): first launch with the new build created `preference.json.bak-wslroute` and rewrote all 19 UNC node `Id`s to `sourcegit-remote://<connectionId>/…` plus the workspace references; node count and names unchanged; no duplicate nodes.

Live acceptance: user retest — open a migrated WSL tab (auto-connects, deploys/starts the WSL server if needed), fetch in a repository with an upstream (e.g. ComfyUI) and observe the branch list/history update **immediately** (event-driven, no 30 s wait). Welcome tree display is unchanged: remote WSL nodes still render their `\\wsl.localhost\…` display path (R29 behavior).


## Remote commit-signinfo: allowlist entry for the pinned signers override (2026-10-04)

Known open item from the round-7 audit: the GUI's commit-signinfo query (`QueryCommitSignInfo`) sends `-c gpg.ssh.allowedSignersFile=/dev/null` — without an explicit signers file git attempts network key retrieval for SSH signatures, so the GUI pins the empty file to keep verification local-only. Over the remote route the leading `-c` pairs are lifted client-side into the structured `ConfigOverrides` field, which the server validates against a closed allowlist that did not include this key — the server answered `Unsupported command config override`, the query failed, and the signature badge was silently absent on every remote repository.

Fix (server-side, `modules/SourceGit.Core/RepositoryService.cs`): `gpg.ssh.allowedSignersFile` added to the `ConfigOverrides` allowlist, value-pinned to exactly `gpg.ssh.allowedSignersFile=/dev/null` (same pinning pattern as `protocol.file.allow=always`): the key is read-only verification behavior with no command-execution surface, and pinning the value keeps the allowlist closed to the one string the GUI sends.

Verification: Windows server suite **33/33** with new item `Commit signinfo config override accepted, other values rejected` (pinned value accepted; `gpg.ssh.allowedSignersFile=<other>` rejected; unknown key `user.email=…` rejected; `artifacts/server-suite-r39.log`). Package republished to `build/remote/` under the same build id `remote-1` (new sha256 `c0b56ea9…`); the content-change marker (`versions/remote-1/.package-sha256`) forced an automatic in-place redeploy of the user's running WSL server on the next product connection (old instance `5e7ccb73…` replaced by `366416ca…`). Live probe through the user's upgraded server against a real migrated repository (ComfyUI): the exact GUI signinfo command (`show --no-show-signature --format=%G?%n%GS%n%GK -s <sha>` with the override) now exits 0 and returns the status triple (unsigned HEAD → `N`, matching local-repository behavior for the same commit); foreign signers values and unknown keys are rejected as pinned.


## SSH password, terminal and per-connection options (2026-10-04)

Goal: the SSH channel follows the same design pattern as the WSL route (VSCode-style). A user password-protected Linux target (`bgo-1962`, user `lizh`) could not connect: SSH ignored the dialog's "Remote user" field entirely (no `-l` was ever emitted; Windows OpenSSH falls back to `USERDOMAIN\USERNAME`, i.e. `norce\lizh`, for a missing user), authentication was hard-coded `BatchMode=yes` (key/agent only, no prompt path), and there was no per-connection hook for host-specific OpenSSH options (the KEX pin above had nowhere to live in the product).

Implementation (`modules/SourceGit.RemoteClient/ConnectionManager.cs`, `src/Views/RemoteRepositories.cs`):

- **User field honored**: both SSH commands (launcher `ssh -T … sh -c '<script>'` and tunnel `ssh -N … -L …`) now emit `-l <user>` before `--` when the field is set (`user@host` in the target still works; `-l` wins, standard OpenSSH semantics).
- **Per-connection `SshOptions`**: free-form OpenSSH arguments (whitespace tokens) appended to both commands, e.g. `-o KexAlgorithms=curve25519-sha256 -i C:\keys\id_ed25519 -o IdentitiesOnly=yes -p 2222`. Command-line `-o` outranks `~/.ssh/config`, so a pin can be stored per connection without touching system config; the system config still applies to everything else (ProxyJump, known hosts, etc.).
- **Stored password (silent)**: the dialog's password field is encrypted with Windows DPAPI (`CurrentUser` scope, `ConnectionCredential`) and persisted as the `SshPassword` ciphertext in the connection JSON — the plaintext never touches disk. At connect time the manager decrypts it, writes a per-connection askpass pair under `%TEMP%` (a side file for the password text plus a one-line `.bat` that `type`s it — a `.bat` built from the password would break on shell metacharacters `& % ^`) and sets `SSH_ASKPASS` + `SSH_ASKPASS_REQUIRE=force` on the ssh process environment (per-process, not global). Every ssh process of the connection (probe, install, start, tunnel, reconnects) signs in silently with one dialog entry; `NumberOfPasswordPrompts=1` keeps a wrong password to a single prompt per process (fail2ban-friendly). Stored-password mode also sets `StrictHostKeyChecking=accept-new`: with no terminal, OpenSSH's first host-key prompt would otherwise hang — the first key is accepted automatically (subsequent changes still fail hard). On non-Windows the dialog refuses to persist a password and falls back to terminal login.
- **Terminal login (fallback)**: `InteractiveSsh` (no stored password) drops `BatchMode` and gives the ssh processes a visible console (`CreateNoWindow=false`); OpenSSH's own UI shows the password prompt and first host-key confirmation. Without multiplexing each ssh process authenticates on its own, so the user may be prompted more than once per connection — the mode is labeled accordingly.
- **ControlMaster was prototyped and removed**: the initial design multiplexed launcher and tunnel through a per-connection control master (VSCode's recommended setup for password hosts). Windows OpenSSH (Win32-OpenSSH) does **not** implement connection multiplexing — any `ControlPath` fails at runtime with `getsockname failed: Not a socket` (reproduced locally on Windows OpenSSH 9.5 with both drive-letter and `\\.\pipe\` paths; matches vscode-remote-release#4253 / Win32-OpenSSH#405). The flags were removed from both commands; the askpass design makes multiplexing unnecessary (every process re-authenticates silently).
- **Actionable failure hint**: a key-mode failure whose stderr contains `Permission denied` or `Host key verification failed` now appends concrete remedies (set a password, enable terminal login, or trust the host from a terminal once) instead of a raw OpenSSH dump.
- Dialog: password field (masked `TextBox`, the codebase's existing secure-input convention), "Extra SSH options" field, terminal-login checkbox, all SSH-only visibility; saved profiles restore all new fields (stored password decrypted on Windows only).

Verification — new acceptance harness `build/ssh-interactive-smoke` (self-contained: root sshd on `127.0.0.1:42327` under WSL, dedicated `sgtest` user + home so the lizh production WSL server is never touched, per-scenario known-host files, KEX pin exercised through `SshOptions`):

- **A key auth (regression)**: product launcher with `-p/-i/-o IdentitiesOnly/KexAlgorithms/UserKnownHostsFile` all via `SshOptions` → deploy (35 MiB over ssh stdin) + tunnel + `git init`/open/`rev-parse` → PASS.
- **B stored password**: `SshPassword` DPAPI ciphertext + `PubkeyAuthentication=no` sshd → full deploy + tunnel + handshake with **zero console windows** (askpass injected per process) → PASS.
- **C reconnect**: `pkill` the sgtest server → automatic reconnect loop re-runs probe/install/start/tunnel and re-authenticates **silently** through the stored password → git works again → PASS.
- **D terminal login**: no stored password, `InteractiveSsh=true`; the harness askpass (process environment `SSH_ASKPASS_REQUIRE=force`) stands in for the human in the console, proving the no-`BatchMode` argument set authenticates → PASS.
- **E event-level refresh through the SSH tunnel**: open the repository through the client, subscribe to `RepositoryChanged`, create a file in the remote worktree (server-side watcher, 200 ms debounce) → the event arrives over the SSH tunnel and the repository's git state reflects the change → PASS. This closes the last WSL-design parity item: refresh is event-driven on the SSH route exactly as on the WSL route.
- GUI suite **23/23** after the change (dialog + record shape); GUI builds 0 errors / 0 warnings.

User retest pending: `bgo-1962` with host `bgo-1962`, user `lizh`, password stored → expected: first connection auto-accepts the host key, silent deploy + tunnel, event-level refresh as on the WSL route.


- Rendered GUI end-to-end interaction. Windows GUI adapters against the independently published Linux Server pass above.
- Product launcher automatic deployment/authentication (SSH and WSL) pass above (SSH: full auto-deploy 7.7 s, fast path 2.1 s, owner exclusion observed as designed, git through the tunnel; WSL: 2.4 s direct localhost TCP, git through the connection). The earlier raw SSH transport suite (isolated `sshd`, temporary keys) is superseded for the product path by the launcher verification.
- Windows AOT publish and arm64 execution require toolchains/targets unavailable on this host (no `nativeaot` workload, no Clang/MSVC linker — verified `dotnet workload list` empty). Windows **Release (non-AOT) build verified this round**: `dotnet build src/SourceGit.csproj -c Release` compiles with 0 errors (via `-o` bypass of the user-GUI bin locks). Linux x64 self-contained publish and execution are covered: the WSL acceptance and the SSH connector verification both run the exact published linux-x64 self-contained package on real Ubuntu-24.04 (packages `build/test-remote-s2e`).
- GUI end-to-end behavior, including remote URI handling. The "absence of accidental Windows repository I/O" half is now regression-pinned: the connector acceptance run (WSL/SSH modes) snapshots the client working directory before connecting and asserts zero new entries after deployment + repository + git operations (historical bug class: literal server-path directories created client-side — see the harness fix note above).
- Concurrent submodule installation races pass above (two concurrent fresh-clone `submodule update --init` through the server both succeed via the per-common-directory operation gate). Submodules on remote repositories pass above (add + fresh-clone init through the server and through the GUI command class). Editor helper timeout/rejection/interruption/recovery, cancellation, real acknowledgement loss, malformed network frames, merge conflict/abort and worktree/bare scenarios pass above.
- NAT/mirrored mode coverage, no-Git Windows, no-runtime Linux, performance and watcher overflow. Existing host tools cannot be removed merely to claim these conditions.
