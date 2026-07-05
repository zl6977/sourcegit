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

        public static async Task SetGlobalIfChangedAsync(Dictionary<string, string> cached, string key, string value, string defaultValue)
        {
            bool changed = false;
            if (cached.TryGetValue(key, out var old))
                changed = old != value;
            else if (!string.IsNullOrEmpty(value) && value != defaultValue)
                changed = true;

            if (changed)
                await SetGlobalAsync(key, value);
        }
    }
}
