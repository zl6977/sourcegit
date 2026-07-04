using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Tag : Command
    {
        public Tag(string repo, string name)
        {
            WorkingDirectory = repo;
            Context = repo;
            Operation = "tag";
            _name = name;
        }

        public async Task<bool> AddAsync(string basedOn)
        {
            Args = ["tag", "--no-sign", _name, basedOn];
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> AddAsync(string basedOn, string message, bool sign)
        {
            Args = ["tag", sign ? "--sign" : "--no-sign", "-a", _name, basedOn];

            if (!string.IsNullOrEmpty(message))
            {
                using var messageFile = await BackendTempFile.CreateAsync(WorkingDirectory, message, "sourcegit_tag_message").ConfigureAwait(false);
                Args.Add("-F");
                Args.Add(messageFile.File);
                return await ExecAsync().ConfigureAwait(false);
            }

            Args.Add("-m");
            Args.Add(_name);
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> DeleteAsync()
        {
            Args = ["tag", "--delete", _name];
            return await ExecAsync().ConfigureAwait(false);
        }

        private readonly string _name;
    }
}
