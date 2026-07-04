using System.Collections.Generic;

namespace SourceGit.Commands
{
    public class Apply : Command
    {
        public Apply(string repo, string file, bool ignoreWhitespace, string whitespaceMode, List<string> extra = null)
        {
            WorkingDirectory = repo;
            Context = repo;
            Operation = "apply";
            Args = ["apply"];

            if (ignoreWhitespace)
                Args.Add("--ignore-whitespace");
            else
                Args.Add($"--whitespace={whitespaceMode}");

            if (extra is { Count: > 0 })
                Args.AddRange(extra);

            Args.Add(file);
        }
    }
}
