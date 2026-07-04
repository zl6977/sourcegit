using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class QueryCommitSignInfo : Command
    {
        public QueryCommitSignInfo(string repo, string sha, bool useFakeSignersFile)
        {
            WorkingDirectory = repo;
            Context = repo;

            Args = [];
            if (useFakeSignersFile)
            {
                Args.Add("-c");
                Args.Add("gpg.ssh.allowedSignersFile=/dev/null");
            }
            Args.AddRange(["show", "--no-show-signature", "--format=%G?%n%GS%n%GK", "-s", sha]);
        }

        public async Task<Models.CommitSignInfo> GetResultAsync()
        {
            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (!rs.IsSuccess)
                return null;

            var raw = rs.StdOut.Trim().ReplaceLineEndings("\n");
            if (raw.Length <= 1)
                return null;

            var lines = raw.Split('\n');
            return new Models.CommitSignInfo()
            {
                VerifyResult = lines[0][0],
                Signer = lines[1],
                Key = lines[2]
            };
        }
    }
}
