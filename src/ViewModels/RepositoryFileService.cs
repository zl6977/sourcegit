namespace SourceGit.ViewModels
{
    public static class RepositoryFileService
    {
        public static string ReadFile(string path, string repository = null)
        {
            return Commands.GitService.ReadFile(path, repository);
        }

        public static System.IO.Stream OpenRead(string path, string repository = null)
        {
            return Commands.GitService.OpenRead(path, repository);
        }

        public static void WriteFile(string path, string content, string repository = null)
        {
            Commands.GitService.WriteFile(path, content, repository);
        }

        public static bool FileExists(string path, string repository = null)
        {
            return Commands.GitService.FileExists(path, repository);
        }

        public static bool DirectoryExists(string path, string repository = null)
        {
            return Commands.GitService.DirectoryExists(path, repository);
        }

        public static bool PathExists(string path, string repository = null)
        {
            return Commands.GitService.PathExists(path, repository);
        }
    }
}
