using System;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QuerySingleCommit : Command
    {
        private readonly string _sha;
        public QuerySingleCommit(string repo, string sha)
        {
            WorkingDirectory = repo;
            Context = repo;
            _sha = sha;
            Args = $"show --no-show-signature --decorate=full --format=%H%n%P%n%D%n%aN±%aE%n%at%n%cN±%cE%n%ct%n%s -s {sha}";
        }

        public Models.Commit GetResult()
        {
            if (SourceGit.Remote.RepositoryPath.IsRemote(WorkingDirectory)) return GetResultAsync().GetAwaiter().GetResult();
            var rs = ReadToEnd();
            return Parse(rs);
        }

        public async Task<Models.Commit> GetResultAsync()
        {
            if (SourceGit.Remote.RepositoryPath.IsRemote(WorkingDirectory))
            {
                try
                {
                    var workspace = SourceGit.Remote.RemoteWorkspace.ForPath(WorkingDirectory);
                    var client = workspace.Client;
                    var handle = await client.OpenRepositoryAsync(workspace.ExecutionPath(WorkingDirectory), CancellationToken).ConfigureAwait(false);
                    try
                    {
                        var info = await client.GetCommitAsync(handle.Id, _sha, CancellationToken).ConfigureAwait(false);
                        return Map(info);
                    }
                    finally { await workspace.ReleaseTransientAsync(handle, client).ConfigureAwait(false); }
                }
                catch { return null; }
            }
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            return Parse(rs);
        }

        private static Models.Commit Map(SourceGit.Contracts.CommitInfo info)
        {
            var commit = new Models.Commit();
            commit.SHA = info.Id;
            commit.Parents.AddRange(info.Parents);
            if (info.Decorations != null) commit.ParseDecorators(info.Decorations);
            commit.Author = Models.User.FindOrAdd(info.Author + "±" + info.AuthorEmail);
            commit.AuthorTime = (ulong)info.AuthorTime;
            commit.Committer = Models.User.FindOrAdd(info.Committer + "±" + info.CommitterEmail);
            commit.CommitterTime = (ulong)info.CommitTime;
            commit.Subject = info.Subject;
            return commit;
        }

        private Models.Commit Parse(Result rs)
        {
            if (!rs.IsSuccess || string.IsNullOrEmpty(rs.StdOut))
                return null;

            var commit = new Models.Commit();
            var lines = rs.StdOut.Split('\n');
            if (lines.Length < 8)
                return null;

            commit.SHA = lines[0];
            if (!string.IsNullOrEmpty(lines[1]))
                commit.Parents.AddRange(lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (!string.IsNullOrEmpty(lines[2]))
                commit.ParseDecorators(lines[2]);
            commit.Author = Models.User.FindOrAdd(lines[3]);
            commit.AuthorTime = ulong.Parse(lines[4]);
            commit.Committer = Models.User.FindOrAdd(lines[5]);
            commit.CommitterTime = ulong.Parse(lines[6]);
            commit.Subject = lines[7];

            return commit;
        }
    }
}
