using System.IO;
using Avalonia.Data.Converters;

namespace SourceGit.Converters
{
    public static class PathConverters
    {
        public static readonly FuncValueConverter<string, string> PureFileName =
            new(v => SourceGit.Remote.RepositoryPath.GetFileName(v) ?? "");

        public static readonly FuncValueConverter<string, string> PureDirectoryName =
            new(v => SourceGit.Remote.RepositoryPath.GetDirectoryName(v) ?? "");

        public static readonly FuncValueConverter<string, string> RelativeToHome =
            new(Native.OS.GetRelativePathToHome);

        // Display-only: WSL remote resources render as \\wsl.localhost\<distro>\... ; everything else falls back to home-relative.
        public static readonly FuncValueConverter<string, string> RemoteDisplayPath =
            new(v => Native.OS.GetRelativePathToHome(SourceGit.Remote.RemoteConnections.DisplayPath(v) ?? v));
    }
}
