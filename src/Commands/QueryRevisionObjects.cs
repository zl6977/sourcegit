using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public partial class QueryRevisionObjects : Command
    {
        [GeneratedRegex(@"^\d+\s+(\w+)\s+([0-9a-f]+)\s+(.*)$")]
        private static partial Regex REG_FORMAT();

        public QueryRevisionObjects(string repo, string sha, string parentFolder)
        {
            WorkingDirectory = repo;
            Context = repo;

            Args = ["ls-tree", sha];
            if (!string.IsNullOrEmpty(parentFolder))
            {
                Args.Add("--");
                Args.Add(parentFolder);
            }
        }

        public async Task<List<Models.Object>> GetResultAsync()
        {
            var outs = new List<Models.Object>();

            Process proc = null;

            try
            {
                proc = new Process();
                proc.StartInfo = CreateGitStartInfo(true);
                proc.Start();

_ = proc.StandardError.ReadToEndAsync();

                while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    var match = REG_FORMAT().Match(line);
                    if (!match.Success)
                        continue;

                    var obj = new Models.Object();
                    obj.SHA = match.Groups[2].Value;
                    obj.Type = Models.ObjectType.Blob;
                    obj.Path = match.Groups[3].Value;

                    obj.Type = match.Groups[1].Value switch
                    {
                        "blob" => Models.ObjectType.Blob,
                        "tree" => Models.ObjectType.Tree,
                        "tag" => Models.ObjectType.Tag,
                        "commit" => Models.ObjectType.Commit,
                        _ => obj.Type,
                    };

                    outs.Add(obj);
                }

                if (!proc.HasExited) proc.Kill();
                await proc.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                if (proc != null && !proc.HasExited) proc.Kill();
            }
            finally
            {
                proc?.Dispose();
            }

            return outs;
        }
    }
}
