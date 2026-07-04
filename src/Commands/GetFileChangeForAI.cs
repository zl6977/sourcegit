using System;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class GetFileChangeForAI : Command
    {
        public GetFileChangeForAI(string repo, string file, string originalFile)
        {
            WorkingDirectory = repo;
            Context = repo;

            Args = ["diff", "--no-color", "--no-ext-diff", "--diff-algorithm=minimal", "--cached", "--"];
            if (!string.IsNullOrEmpty(originalFile) && !file.Equals(originalFile, StringComparison.Ordinal))
                Args.Add(originalFile);
            Args.Add(file);
        }

        public async Task<Result> ReadAsync()
        {
            return await ReadToEndAsync().ConfigureAwait(false);
        }
    }
}
