using System.Diagnostics;

namespace SourceGit.Commands
{
    public static class GitService
    {
        public static ProcessStartInfo CreateStartInfo(Command command, bool redirect)
        {
            var spec = new GitCommandSpec()
            {
                WorkingDirectory = command.WorkingDirectory,
                Editor = command.Editor,
                SSHKey = command.SSHKey,
                Operation = command.Operation,
                Args = command.Args,
            };

            return GetBackend(command.WorkingDirectory).CreateStartInfo(spec, redirect);
        }

        public static string NormalizeRepositoryPath(string path, string baseRepositoryPath)
        {
            return GetBackend(baseRepositoryPath).NormalizeRepositoryPath(path, baseRepositoryPath);
        }

        public static bool DirectoryExists(string path, string baseRepositoryPath = null)
        {
            return GetBackend(baseRepositoryPath ?? path).DirectoryExists(path);
        }

        public static bool FileExists(string path, string baseRepositoryPath = null)
        {
            return GetBackend(baseRepositoryPath ?? path).FileExists(path);
        }

        public static long GetFileSize(string path, string baseRepositoryPath = null)
        {
            return GetBackend(baseRepositoryPath ?? path).GetFileSize(path);
        }


        public static string ReadFile(string path, string baseRepositoryPath = null)
        {
            return GetBackend(baseRepositoryPath ?? path).ReadFile(path);
        }

        public static System.IO.Stream OpenRead(string path, string baseRepositoryPath = null)
        {
            return GetBackend(baseRepositoryPath ?? path).OpenRead(path);
        }

        public static System.IO.Stream OpenWrite(string path, string baseRepositoryPath = null)
        {
            return GetBackend(baseRepositoryPath ?? path).OpenWrite(path);
        }

        public static void WriteFile(string path, string content, string baseRepositoryPath = null)
        {
            GetBackend(baseRepositoryPath ?? path).WriteFile(path, content);
        }

        public static void WriteFileAndReplace(string path, string content, string baseRepositoryPath = null)
        {
            GetBackend(baseRepositoryPath ?? path).WriteFileAndReplace(path, content);
        }

        public static bool DirectoryContainsFile(string directory, string fileName)
        {
            return GetBackend(directory).DirectoryContainsFile(directory, fileName);
        }

        public static bool PathExists(string path, string baseRepositoryPath = null)
        {
            return GetBackend(baseRepositoryPath ?? path).PathExists(path);
        }

        public static void MoveFile(string source, string destination)
        {
            GetBackend(source).MoveFile(source, destination);
        }
        public static System.IO.FileInfo[] GetFiles(string directory)
        {
            return GetBackend(directory).GetFiles(directory);
        }
        public static void DeleteFile(string path, string baseRepositoryPath = null)
        {
            GetBackend(baseRepositoryPath ?? path).DeleteFile(path);
        }

        public static void DeleteDirectory(string path, string baseRepositoryPath = null)
        {
            GetBackend(baseRepositoryPath ?? path).DeleteDirectory(path);
        }

        public static bool RequiresGitBareRepositoryProbe(string path)
        {
            return Models.WslRepositoryPath.TryParse(path, out _);
        }

        public static Models.WslRepositoryPath GetWslPath(string path)
        {
            if (Models.WslRepositoryPath.TryParse(path, out var wslPath))
                return wslPath;
            return null;
        }

        public static GitBackend GetBackend(string workingDirectory)
        {
            if (Models.WslRepositoryPath.TryParse(workingDirectory, out var wslPath))
                return new WslGitBackend(wslPath);

            return new WindowsGitBackend();
        }
    }
}
