using System.IO;
using System.Text.Json;

namespace SourceGit.ViewModels
{
    public static class CommitMessageOperations
    {
        public static string LoadConventionalTypesOverride(string messageFile)
        {
            var repo = Path.GetDirectoryName(messageFile);
            var gitDir = new Commands.QueryGitDir(repo).GetResult();
            if (string.IsNullOrEmpty(gitDir))
                return string.Empty;

            var settingsFile = Path.Combine(gitDir, "sourcegit.settings");
            if (!RepositoryFileService.FileExists(settingsFile))
                return string.Empty;

            try
            {
                using var stream = RepositoryFileService.OpenRead(settingsFile);
                var settings = JsonSerializer.Deserialize(stream, JsonCodeGen.Default.RepositorySettings);
                return settings.ConventionalTypesOverride;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string ReadMessageFile(string file)
        {
            return RepositoryFileService.ReadFile(file).ReplaceLineEndings("\n").Trim();
        }

        public static void WriteMessageFile(string file, string message)
        {
            RepositoryFileService.WriteFile(file, message);
        }
    }
}
