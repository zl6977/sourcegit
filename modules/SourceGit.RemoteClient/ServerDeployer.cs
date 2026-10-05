using System.Security.Cryptography;
using System.Text.Json;
using SourceGit.Contracts;

namespace SourceGit.RemoteClient;

internal sealed record ServerDeployment(ServerRecord Record, bool UpgradeRequired, string ProcessStart);

/// <summary>Installs immutable packages and starts or discovers an instance. Never terminates a server.</summary>
internal sealed class ServerDeployer(Func<string, byte[]?, CancellationToken, Task<string>> runRemote, string? baseDirectory = null)
{
    private readonly string _base = baseDirectory == null ? "\"$HOME/.sourcegit-server\"" : "'" + baseDirectory.Replace("'", "'\"'\"'") + "'";
    private string? _version;

    public async Task InstallAsync(string package, string? expectedHash, CancellationToken cancellation)
    {
        if (!File.Exists(package)) throw new FileNotFoundException("Matching self-contained Server package is required", package);
        var bytes = await File.ReadAllBytesAsync(package, cancellation).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (string.IsNullOrEmpty(expectedHash) && File.Exists(package + ".sha256"))
            expectedHash = (await File.ReadAllTextAsync(package + ".sha256", cancellation).ConfigureAwait(false)).Split(' ', '\r', '\n')[0];
        if (string.IsNullOrWhiteSpace(expectedHash) || !hash.Equals(expectedHash.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Server package digest missing or mismatched");
        _version = Protocol.Build + "/" + hash;
        var check = $$"""
            base={{_base}}; version="$base/versions/{{_version}}"
            test -x "$version/sourcegit-server" && [ "$(cat "$version/sourcegit-bootstrap.version" 2>/dev/null | tr -d '\r\n')" = '1' ] && [ "$(cat "$version/.package-sha256" 2>/dev/null)" = '{{hash}}' ] && printf INSTALLED || printf MISSING
            """;
        if ((await runRemote(check, null, cancellation).ConfigureAwait(false)).Trim() == "INSTALLED") return;
        var install = $$"""
            set -eu; umask 077
            base={{_base}}; mkdir -p "$base/versions/{{Protocol.Build}}"; chmod 700 "$base"
            archive="$base/upload-{{Guid.NewGuid():N}}.tar.gz"; temp=""
            trap 'rm -f "$archive"; if [ -n "$temp" ]; then rm -rf "$temp"; fi' EXIT
            cat > "$archive"
            printf '%s  %s\n' '{{hash}}' "$archive" | sha256sum -c - >/dev/null
            exec 9>"$base/install.lock"; flock 9
            version="$base/versions/{{_version}}"
            if [ ! -d "$version" ]; then
                temp="$version.tmp-$$"; mkdir "$temp"
                tar -xzf "$archive" -C "$temp"; test -f "$temp/sourcegit-server"; chmod 700 "$temp/sourcegit-server"
                [ "$(cat "$temp/sourcegit-bootstrap.version" 2>/dev/null | tr -d '\r\n')" = '1' ] || { printf 'Server package lacks the matching bootstrap capability\n' >&2; exit 1; }
                printf '%s\n' '{{hash}}' > "$temp/.package-sha256"
                mv "$temp" "$version"; temp=""
            fi
            test -x "$version/sourcegit-server" && [ "$(cat "$version/sourcegit-bootstrap.version" 2>/dev/null | tr -d '\r\n')" = '1' ] && [ "$(cat "$version/.package-sha256")" = '{{hash}}' ] || { printf 'Installed package is incomplete or corrupted\n' >&2; exit 1; }
            """;
        await runRemote(install, bytes, cancellation).ConfigureAwait(false);
    }

    public async Task<ServerDeployment> StartAsync(CancellationToken cancellation, ServerDeployment? exiting = null)
    {
        if (_version == null) throw new InvalidOperationException("Install a package before starting the server");
        if (exiting != null && (!long.TryParse(exiting.ProcessStart, out var start) || start <= 0))
            throw new IOException("Old server process identity is required for exit notification");
        var exitArguments = exiting == null ? "" : " --old-pid " + exiting.Record.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " --old-start " + exiting.ProcessStart;
        var script = $$"""
            set -eu; umask 077
            base={{_base}}; mkdir -p "$base"; chmod 700 "$base"
            exec 9>"$base/start.lock"; flock --timeout 60 9 || { printf 'Startup decision lock exceeded 60 seconds\n' >&2; exit 1; }
            record="$base/instance-{{Protocol.Build}}.json"; version="$base/versions/{{_version}}"
            [ "$(cat "$version/sourcegit-bootstrap.version" 2>/dev/null | tr -d '\r\n')" = '1' ] || { printf 'Installed server lacks bootstrap capability\n' >&2; exit 1; }
            if [ -f "$base/server-env-setup" ]; then . "$base/server-env-setup" >&2; fi
            exec "$version/sourcegit-server" bootstrap --record "$record"{{exitArguments}}
            """;
        var output = await runRemote(script, null, cancellation).ConfigureAwait(false);
        var lines = output.Split('\n');
        var record = lines.LastOrDefault(x => x.StartsWith("SOURCEGIT_RECORD ", StringComparison.Ordinal)) ?? throw new IOException("Missing Server startup record");
        return new(JsonSerializer.Deserialize(record[17..], RemoteJson.Default.ServerRecord) ?? throw new IOException("Invalid Server startup record"),
            lines.Any(x => x.Trim() == "SOURCEGIT_UPGRADE_REQUIRED"),
            (lines.LastOrDefault(x => x.StartsWith("SOURCEGIT_PROCESS_START ", StringComparison.Ordinal)) ?? throw new IOException("Missing Server process identity"))[24..].Trim());
    }

    internal async Task WaitForExitAsync(ServerDeployment deployment, CancellationToken cancellation)
    {
        if (_version == null) throw new InvalidOperationException("Install a package before waiting for exit");
        if (!long.TryParse(deployment.ProcessStart, out var start) || start <= 0 || deployment.Record.ProcessId <= 0)
            throw new IOException("Server process identity is required for exit notification");
        await runRemote($$"""
            set -eu; base={{_base}}; version="$base/versions/{{_version}}"
            exec "$version/sourcegit-server" bootstrap --record "$base/instance-{{Protocol.Build}}.json" --wait-only --old-pid {{deployment.Record.ProcessId}} --old-start {{deployment.ProcessStart}}
            """, null, cancellation).ConfigureAwait(false);
    }
}
