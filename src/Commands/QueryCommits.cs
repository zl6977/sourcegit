using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryCommits : Command
    {
        public QueryCommits(string repo, List<string> limits, bool markMerged = true)
        {
            WorkingDirectory = repo;
            Context = repo;
            Args = ["log", "--no-show-signature", "--decorate=full", "--format=%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s"];
            if (limits is { Count: > 0 })
                Args.AddRange(limits);
            _markMerged = markMerged;
        }

        public QueryCommits(string repo, string filter, Models.CommitSearchMethod method, bool onlyCurrentBranch)
        {
            Args = ["log", "-1000", "--date-order", "--no-show-signature", "--decorate=full", "--format=%H%x00%P%x00%D%x00%aN±%aE%x00%at%x00%cN±%cE%x00%ct%x00%s"];

            if (!onlyCurrentBranch)
            {
                Args.Add("--branches");
                Args.Add("--remotes");
            }

            if (method == Models.CommitSearchMethod.ByAuthor)
            {
                Args.Add("-i");
                Args.Add($"--author={filter}");
            }
            else if (method == Models.CommitSearchMethod.ByMessage)
            {
                var words = filter.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var word in words)
                    Args.Add($"--grep={word.Trim()}");
                Args.Add("--all-match");
                Args.Add("-i");
            }
            else if (method == Models.CommitSearchMethod.ByPath)
            {
                Args.Add("--");
                Args.Add(filter);
            }
            else
            {
                Args.Add("-G");
                Args.Add(filter);
            }

            WorkingDirectory = repo;
            Context = repo;
            _markMerged = false;
        }

        public async Task<List<Models.Commit>> GetResultAsync()
        {
            var commits = new List<Models.Commit>();
            var stderr = string.Empty;
            var exitCode = 0;
            Process proc = null;
            try
            {
                proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                proc.Start();

                var stderrTask = proc.StandardError.ReadToEndAsync();
                var findHead = false;
                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    var parts = line.Split('\0');
                    if (parts.Length != 8)
                        continue;

                    var commit = new Models.Commit() { SHA = parts[0] };
                    commit.ParseParents(parts[1]);
                    commit.ParseDecorators(parts[2]);
                    commit.Author = Models.User.FindOrAdd(parts[3]);
                    commit.AuthorTime = ulong.Parse(parts[4]);
                    commit.Committer = Models.User.FindOrAdd(parts[5]);
                    commit.CommitterTime = ulong.Parse(parts[6]);
                    commit.Subject = parts[7];
                    commits.Add(commit);

                    if (!findHead && commit.IsMerged)
                        findHead = true;
                }

                stderr = await stderrTask.ConfigureAwait(false);
                if (!proc.HasExited) proc.Kill();
                await proc.WaitForExitAsync().ConfigureAwait(false);
                exitCode = proc.ExitCode;

                if (exitCode != 0)
                {
                    // Empty repo: HEAD doesn't exist. Retry without HEAD.
                    if (Args.Contains("HEAD") && stderr.Contains("HEAD"))
                    {
                        Args.Remove("HEAD");
                        proc.Dispose();
                        return await GetResultAsync();
                    }

                    var reason = string.IsNullOrWhiteSpace(stderr) ? $"git exited with code {exitCode}" : stderr.Trim();
                    RaiseException($"Failed to query commits. Reason: {reason}");
                    return commits;
                }

                if (_markMerged && !findHead && commits.Count > 0)
                {
                    var set = await new QueryCurrentBranchCommitHashes(WorkingDirectory, commits[^1].CommitterTime)
                        .GetResultAsync()
                        .ConfigureAwait(false);

                    foreach (var c in commits)
                    {
                        if (set.Contains(c.SHA))
                        {
                            c.IsMerged = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (proc != null && !proc.HasExited) proc.Kill();
                RaiseException($"Failed to query commits. Reason: {e.Message}");
            }
            finally
            {
                proc?.Dispose();
            }

            return commits;
        }

        private bool _markMerged = false;
    }
}
