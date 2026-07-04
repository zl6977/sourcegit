namespace SourceGit.Commands
{
    public class QueryGitDir : Command
    {
        public QueryGitDir(string workDir)
        {
            WorkingDirectory = workDir;
            Args = ["rev-parse", "--absolute-git-dir"];
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

            return GitService.NormalizeRepositoryPath(stdout, WorkingDirectory);
        }
    }
}
