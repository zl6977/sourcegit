using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SourceGit.Contracts;

namespace SourceGit.Remote
{
    public static class RemoteExternalTools
    {
        public static async Task<bool> DiffAsync(string repository, Models.DiffOption option, Models.DiffMergeTool tool)
        {
            var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "sourcegit-tools-" + Guid.NewGuid().ToString("N"))).FullName;
            var extension = SafeExtension(option.Path);
            var left = Path.Combine(directory, "left" + extension);
            var right = Path.Combine(directory, "right" + extension);
            try
            {
                await File.WriteAllBytesAsync(left, await ReadSideAsync(repository, option, true).ConfigureAwait(false)).ConfigureAwait(false);
                await File.WriteAllBytesAsync(right, await ReadSideAsync(repository, option, false).ConfigureAwait(false)).ConfigureAwait(false);
                File.SetAttributes(left, FileAttributes.ReadOnly); File.SetAttributes(right, FileAttributes.ReadOnly);
                return await RunAsync(tool, new Dictionary<string, string> { ["$LOCAL"] = left, ["$REMOTE"] = right, ["$BASE"] = left, ["$MERGED"] = right }).ConfigureAwait(false);
            }
            finally { Cleanup(directory, left, right); }
        }
        public static async Task<bool> MergeAsync(string repository, string file, Models.DiffMergeTool tool)
        {
            if (string.IsNullOrEmpty(file)) throw new ArgumentException("Choose a conflicted file before opening the merge tool");
            var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "sourcegit-tools-" + Guid.NewGuid().ToString("N"))).FullName;
            var extension = SafeExtension(file);
            var mine = Path.Combine(directory, "ours" + extension);
            var theirs = Path.Combine(directory, "theirs" + extension);
            var baseline = Path.Combine(directory, "base" + extension);
            var merged = Path.Combine(directory, "merged" + extension);
            var target = RepositoryPath.Combine(repository, file);
            string uploaded = null;
            try
            {
                var exists = (await RemoteWorkspace.FileAsync(target, "stat").ConfigureAwait(false)).Exists;
                var hash = exists ? (await RemoteWorkspace.FileAsync(target, "hash").ConfigureAwait(false)).Hash : "";
                await File.WriteAllBytesAsync(mine, await ReadBlobAsync(repository, ":2:" + file).ConfigureAwait(false)).ConfigureAwait(false);
                await File.WriteAllBytesAsync(theirs, await ReadBlobAsync(repository, ":3:" + file).ConfigureAwait(false)).ConfigureAwait(false);
                await File.WriteAllBytesAsync(baseline, await ReadBlobAsync(repository, ":1:" + file).ConfigureAwait(false)).ConfigureAwait(false);
                await Task.Run(() => { if (exists) RepositoryFile.Copy(target, merged, true); else File.WriteAllBytes(merged, []); }).ConfigureAwait(false);
                if (!await RunAsync(tool, new Dictionary<string, string> { ["$LOCAL"] = mine, ["$REMOTE"] = theirs, ["$BASE"] = baseline, ["$MERGED"] = merged }).ConfigureAwait(false)) return false;
                uploaded = RepositoryFile.GetTempFileName(repository);
                await Task.Run(() => RepositoryFile.Copy(merged, uploaded, true)).ConfigureAwait(false);
                await RemoteWorkspace.FileAsync(target, "replace", destination: uploaded, expectedHash: hash).ConfigureAwait(false);
                var specification = RepositoryFile.GetTempFileName(repository);
                try
                {
                    await RepositoryFile.WriteAllLinesAsync(specification, [file]).ConfigureAwait(false);
                    return await new Commands.Add(repository, specification).ExecAsync().ConfigureAwait(false);
                }
                finally { RepositoryFile.Delete(specification); }
            }
            finally
            {
                if (uploaded != null) RepositoryFile.Delete(uploaded);
                Cleanup(directory, mine, theirs, baseline, merged);
            }
        }
        private static async Task<byte[]> ReadSideAsync(string repository, Models.DiffOption option, bool old)
        {
            var file = old && !string.IsNullOrEmpty(option.OrgPath) ? option.OrgPath : option.Path;
            if (file == "/dev/null") return [];
            if (option.Revisions.Count == 2)
            {
                var revision = option.Revisions[old ? 0 : 1];
                if (revision == "-R" || string.IsNullOrEmpty(revision)) return await ReadWorkingAsync(repository, file).ConfigureAwait(false);
                var expression = option.IgnorePaths ? RemoteWorkspace.Tokenize(revision).Single() : revision + ":" + file;
                return await ReadBlobAsync(repository, expression).ConfigureAwait(false);
            }
            if (old) return await ReadBlobAsync(repository, (option.IsUnstaged ? "" : option.IndexBaseRevision) + ":" + file).ConfigureAwait(false);
            return option.IsUnstaged ? await ReadWorkingAsync(repository, file).ConfigureAwait(false) : await ReadBlobAsync(repository, ":" + file).ConfigureAwait(false);
        }
        private static Task<byte[]> ReadWorkingAsync(string repository, string file) => Task.Run(() =>
        {
            var path = RepositoryPath.Combine(repository, file);
            if (!RepositoryFile.Exists(path)) return Array.Empty<byte>();
            using var input = RepositoryFile.OpenRead(path);
            using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
        });
        private static async Task<byte[]> ReadBlobAsync(string repository, string expression)
        {
            var workspace = RemoteWorkspace.ForPath(repository);
            var client = workspace.Client;
            var info = await client.OpenRepositoryAsync(workspace.ExecutionPath(repository)).ConfigureAwait(false);
            try
            {
                var exists = await client.ExecuteGitAsync(new GitRequest(info.Id, ["cat-file", "-e", expression])).ConfigureAwait(false);
                if (exists.ExitCode != 0) return [];
                var result = await client.ExecuteGitAsync(new GitRequest(info.Id, ["cat-file", "blob", expression])).ConfigureAwait(false);
                if (result.ExitCode != 0) throw new IOException(result.Error);
                return Convert.FromBase64String(result.OutputBase64);
            }
            finally { await workspace.ReleaseTransientAsync(info, client).ConfigureAwait(false); }
        }
        private static async Task<bool> RunAsync(Models.DiffMergeTool tool, Dictionary<string, string> files)
        {
            var arguments = tool.Cmd;
            foreach (var item in files) arguments = arguments.Replace(item.Key, item.Value, StringComparison.Ordinal);
            using var process = Process.Start(new ProcessStartInfo(tool.Exec) { Arguments = arguments, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(files["$LOCAL"]), CreateNoWindow = true }) ?? throw new IOException("Cannot start external tool");
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        private static string SafeExtension(string path)
        {
            var extension = RepositoryPath.GetExtension(RepositoryPath.Remote("filename", path));
            return new string(extension.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        }
        private static void Cleanup(string directory, params string[] paths)
        {
            foreach (var path in paths)
                try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(directory); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
