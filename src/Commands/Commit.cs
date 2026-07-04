using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Commit : Command
    {
        public Commit(string repo, string message, bool signOff, bool noVerify, bool amend, bool resetAuthor)
        {
            _repo = repo;
            _message = message;

            WorkingDirectory = repo;
            Context = repo;
            Operation = "commit";
            Args = ["commit", "--allow-empty"];

            if (signOff)
                Args.Add("--signoff");

            if (noVerify)
                Args.Add("--no-verify");

            if (amend)
            {
                Args.Add("--amend");
                if (resetAuthor)
                    Args.Add("--reset-author");
                Args.Add("--no-edit");
            }
        }

        public async Task<bool> RunAsync()
        {
            try
            {
                using var messageFile = await BackendTempFile.CreateAsync(_repo, _message, "sourcegit_commit_message").ConfigureAwait(false);
                Args.Add($"--file={messageFile.File}");
                return await ExecAsync().ConfigureAwait(false);
            }
            catch
            {
                return false;
            }
        }

        private readonly string _repo;
        private readonly string _message;
    }
}
