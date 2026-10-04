using System.IO;

namespace SourceGit.Commands
{
    public class QueryGitDir : Command
    {
        public QueryGitDir(string workDir)
        {
            WorkingDirectory = workDir;
            Args = "rev-parse --git-dir";
        }

        public string GetResult()
        {
            return Parse(ReadToEnd());
        }

        private string Parse(Result rs)
        {
            if (!rs.IsSuccess)
                return null;

            var stdout = rs.StdOut.Trim();
            if (string.IsNullOrEmpty(stdout))
                return null;

            if (SourceGit.Remote.RepositoryPath.IsRemote(WorkingDirectory))
                return SourceGit.Remote.RepositoryPath.Combine(WorkingDirectory, stdout);

            return SourceGit.Remote.RepositoryPath.IsPathRooted(stdout) ? stdout : SourceGit.Remote.RepositoryPath.GetFullPath(SourceGit.Remote.RepositoryPath.Combine(WorkingDirectory, stdout));
        }
    }
}
