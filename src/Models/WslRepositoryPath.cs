using System;

namespace SourceGit.Models
{
    public class WslRepositoryPath
    {
        public string Distro { get; private set; } = string.Empty;
        public string LinuxPath { get; private set; } = string.Empty;

        public string ToWindowsPath(string linuxPath)
        {
            if (string.IsNullOrWhiteSpace(linuxPath))
                return linuxPath;

            var normalized = linuxPath.Replace('\\', '/');
            if (!normalized.StartsWith('/'))
                normalized = $"{LinuxPath.TrimEnd('/')}/{normalized}";

            return $"//wsl$/{Distro}{normalized}";
        }

        public string ToNativeWindowsPath(string linuxPath)
        {
            return ToWindowsPath(linuxPath)?.Replace('/', '\\');
        }

        public string ToNativeWindowsPath()
        {
            return ToNativeWindowsPath(LinuxPath);
        }

        public static string ToLinuxPath(string windowsPath)
        {
            if (string.IsNullOrWhiteSpace(windowsPath))
                return windowsPath;

            var normalized = windowsPath.Replace('\\', '/');
            const string wslPrefix = "//wsl$/";
            const string wslLocalhostPrefix = "//wsl.localhost/";
            var prefixLength = 0;
            if (normalized.StartsWith(wslPrefix, StringComparison.OrdinalIgnoreCase))
                prefixLength = wslPrefix.Length;
            else if (normalized.StartsWith(wslLocalhostPrefix, StringComparison.OrdinalIgnoreCase))
                prefixLength = wslLocalhostPrefix.Length;

            if (prefixLength > 0)
            {
                var nextSeparator = normalized.IndexOf('/', prefixLength);
                return nextSeparator < 0 ? "/" : normalized.Substring(nextSeparator);
            }

            if (normalized.Length > 2 && normalized[1] == ':' && normalized[2] == '/')
                return $"/mnt/{char.ToLowerInvariant(normalized[0])}{normalized.Substring(2)}";

            return normalized;
        }

        public static bool TryParse(string path, out WslRepositoryPath result)
        {
            result = null;

            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
                return false;

            var normalized = path.Replace('\\', '/');
            const string wslPrefix = "//wsl$/";
            const string wslLocalhostPrefix = "//wsl.localhost/";

            var prefixLength = 0;
            if (normalized.StartsWith(wslPrefix, StringComparison.OrdinalIgnoreCase))
                prefixLength = wslPrefix.Length;
            else if (normalized.StartsWith(wslLocalhostPrefix, StringComparison.OrdinalIgnoreCase))
                prefixLength = wslLocalhostPrefix.Length;
            else
                return false;

            var nextSeparator = normalized.IndexOf('/', prefixLength);
            if (nextSeparator == prefixLength)
                return false;

            var distro = nextSeparator < 0 ? normalized.Substring(prefixLength) : normalized.Substring(prefixLength, nextSeparator - prefixLength);
            var linuxPath = nextSeparator < 0 ? "/" : normalized.Substring(nextSeparator);

            if (string.IsNullOrWhiteSpace(distro))
                return false;

            result = new WslRepositoryPath()
            {
                Distro = distro,
                LinuxPath = linuxPath,
            };
            return true;
        }
    }
}
