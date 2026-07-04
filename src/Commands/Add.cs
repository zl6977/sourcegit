using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Add : Command
    {

        public static async Task<bool> ProcessAsync(string repo, List<string> paths, Models.ICommandLog log = null)
        {
            if (paths.Count == 0)
                return true;

            for (var i = 0; i < paths.Count; i += 32)
            {
                var count = System.Math.Min(32, paths.Count - i);
                var add = new Command()
                {
                    WorkingDirectory = repo,
                    Context = repo,
                    Operation = "add",
                    Args = ["add", "--force", "--verbose", "--"],
                }.Use(log);

                for (var j = 0; j < count; j++)
                    add.Args.Add(paths[i + j]);

                if (!await add.ExecAsync().ConfigureAwait(false))
                    return false;
            }

            return true;
        }
    }
}
