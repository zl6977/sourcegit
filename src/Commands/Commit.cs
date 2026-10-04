using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Commit : Command
    {
        public Commit(string repo, string message, bool signOff, bool noVerify, bool amend, bool resetAuthor)
        {
            _tmpFile = SourceGit.Remote.RepositoryFile.GetTempFileName(repo);
            _message = message;

            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder();
            builder.Append("commit --allow-empty --file=");
            builder.Append(_tmpFile.Quoted());
            builder.Append(' ');

            if (signOff)
                builder.Append("--signoff ");

            if (noVerify)
                builder.Append("--no-verify ");

            if (amend)
            {
                builder.Append("--amend ");
                if (resetAuthor)
                    builder.Append("--reset-author ");
                builder.Append("--no-edit");
            }

            Args = builder.ToString();
        }

        public async Task<bool> RunAsync()
        {
            try
            {
                await SourceGit.Remote.RepositoryFile.WriteAllTextAsync(_tmpFile, _message).ConfigureAwait(false);
                var succ = await ExecAsync().ConfigureAwait(false);
                SourceGit.Remote.RepositoryFile.Delete(_tmpFile);
                return succ;
            }
            catch
            {
                return false;
            }
        }

        private readonly string _tmpFile;
        private readonly string _message;
    }
}
