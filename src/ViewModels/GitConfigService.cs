using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public static class GitConfigService
    {
        public static Dictionary<string, string> ReadGlobal()
        {
            return new Commands.Config(null).ReadAll();
        }

        public static Task<Dictionary<string, string>> ReadGlobalAsync()
        {
            return new Commands.Config(null).ReadAllAsync();
        }

        public static Task<string> GetBranchDescriptionAsync(string repo, string branch)
        {
            return new Commands.Config(repo).GetAsync($"branch.{branch}.description");
        }

        public static Task<string> GetRepositoryValueAsync(string repo, string key)
        {
            return new Commands.Config(repo).GetAsync(key);
        }

        public static Task SetGlobalAsync(string key, string value)
        {
            return new Commands.Config(null).SetAsync(key, value);
        }

    }
}
