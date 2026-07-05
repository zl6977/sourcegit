using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    public partial class OpenLocalRepository : UserControl
    {
        public OpenLocalRepository()
        {
            InitializeComponent();
        }

        private async void OnSelectRepositoryFolder(object _1, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.OpenLocalRepository vm)
                return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
                return;

            var preference = ViewModels.Preferences.Instance;
            var workspace = preference.GetActiveWorkspace();
            var initDir = workspace.DefaultCloneDir;
            if (string.IsNullOrEmpty(initDir) || !ViewModels.RepositoryFileService.DirectoryExists(initDir))
                initDir = preference.GitDefaultCloneDir;

            var options = new FolderPickerOpenOptions() { AllowMultiple = false };
            if (Directory.Exists(initDir))
            {
                var folder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(initDir);
                options.SuggestedStartLocation = folder;
            }

            try
            {
                var selected = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                if (selected.Count == 1)
                {
                    var folder = selected[0];
                    vm.RepoPath = folder is { Path: { IsAbsoluteUri: true } path } ? path.LocalPath : folder?.Path.ToString();
                }
            }
            catch (Exception exception)
            {
                Models.Notification.Send(null, $"Failed to open repository: {exception.Message}", true);
            }

            e.Handled = true;
        }

        private async void OnUseWslHome(object _, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.OpenLocalRepository vm)
                return;

            if (!OperatingSystem.IsWindows())
            {
                Models.Notification.Send(null, "WSL paths are only available on Windows.", true);
                return;
            }

            try
            {
                var distro = await QueryDefaultWslDistroAsync();
                if (string.IsNullOrWhiteSpace(distro))
                {
                    Models.Notification.Send(null, "No WSL distribution was found.", true);
                    return;
                }

                var home = await QueryWslHomeAsync(distro);
                if (string.IsNullOrWhiteSpace(home))
                    home = "/home/";

                vm.RepoPath = $@"\\wsl$\{distro}{home.Replace('/', '\\')}";
            }
            catch (Exception ex)
            {
                Models.Notification.Send(null, $"Failed to detect WSL path: {ex.Message}", true);
            }

            e.Handled = true;
        }

        private static async Task<string> QueryDefaultWslDistroAsync()
        {
            var output = await ReadWslOutputAsync(["-l", "-q"]);
            var lines = output.Replace("\0", string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 ? lines[0].Trim() : string.Empty;
        }

        private static async Task<string> QueryWslHomeAsync(string distro)
        {
            var output = await ReadWslOutputAsync(["-d", distro, "--cd", "~", "--exec", "pwd"]);
            return output.Replace("\0", string.Empty).Trim();
        }

        private static async Task<string> ReadWslOutputAsync(string[] args)
        {
            var start = new ProcessStartInfo()
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            foreach (var arg in args)
                start.ArgumentList.Add(arg);

            Process proc = null;
            try
            {
                proc = Process.Start(start);
                if (proc == null)
                    return string.Empty;

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                var stdout = await stdoutTask;
                await stderrTask;
                await proc.WaitForExitAsync();

                return proc.ExitCode == 0 ? stdout : string.Empty;
            }
            catch
            {
                if (proc != null && !proc.HasExited) proc.Kill();
                return string.Empty;
            }
            finally
            {
                proc?.Dispose();
            }
        }
    }
}
