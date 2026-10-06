using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using SourceGit.Remote;
using SourceGit.RemoteClient;

namespace SourceGit.Views
{
    public sealed class RemoteRepositories : Window
    {
        private readonly ComboBox _profiles = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ComboBox _kind = new() { ItemsSource = new[] { "Wsl", "Ssh" }, SelectedIndex = 0 };
        private readonly ComboBox _wsl = new() { IsEditable = true, Text = "Ubuntu-24.04", HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBox _target = new() { Watermark = "SSH host (alias, or user@host)" };
        private readonly TextBox _user = new() { Watermark = "Remote user (optional)" };
        private readonly TextBox _sshOptions = new() { Watermark = "Extra SSH options, e.g. -o KexAlgorithms=curve25519-sha256" };
        private readonly TextBox _sshPassword = new() { Watermark = "Password (optional; stored for this machine via Windows DPAPI)", PasswordChar = '●' };
        private readonly CheckBox _interactive = new() { Content = "Terminal login (OpenSSH prompts in a window; used when no password is stored)", VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBox _path = new() { Watermark = "/home/user/repository" };
        private readonly TextBox _package = new() { Watermark = "Packaged Server is used automatically" };
        private readonly Button _connect = new() { Content = "Connect and open", HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Button _cancel = new() { Content = "Cancel connection", IsEnabled = false };
        private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, MinHeight = 100, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        private CancellationTokenSource _attempt;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly ConnectionOptions[] _saved;
        public RemoteRepositories()
        {
            Title = "Remote repositories"; Width = 660; Height = 610;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _saved = RemoteConnections.Load();
            _profiles.ItemsSource = new[] { "New connection" }.Concat(_saved.Select(x => x.Name)).ToArray();
            _profiles.SelectedIndex = 0;
            _profiles.SelectionChanged += (_, _) =>
            {
                if (_profiles.SelectedIndex < 1) return;
                var value = _saved[_profiles.SelectedIndex - 1];
                _kind.SelectedIndex = value.Kind.Equals("Ssh", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (value.Kind.Equals("Ssh", StringComparison.OrdinalIgnoreCase)) _target.Text = value.Target; else _wsl.Text = value.Target;
                _user.Text = value.User; _package.Text = value.PackagePath; _sshOptions.Text = value.SshOptions; _interactive.IsChecked = value.InteractiveSsh;
                if (value.Kind.Equals("Ssh", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value.SshPassword) && OperatingSystem.IsWindows()) { try { _sshPassword.Text = ConnectionCredential.Decrypt(value.SshPassword); } catch (CryptographicException) { } }
            };
            var packaged = Path.Combine(AppContext.BaseDirectory, "server", "sourcegit-server-linux-x64.tar.gz");
            var developerPackage = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../build/remote/sourcegit-server-linux-x64.tar.gz"));
            if (File.Exists(packaged)) _package.Text = packaged;
            else if (File.Exists(developerPackage)) _package.Text = developerPackage;
            var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "The repository and Git run on the selected host.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            Add(panel, "Connection", _profiles); Add(panel, "Transport", _kind);
            var wslLabel = new TextBlock { Text = "WSL distribution" };
            var sshLabel = new TextBlock { Text = "SSH host alias" };
            void SyncTransport()
            {
                var isWsl = _kind.SelectedIndex != 1;
                wslLabel.IsVisible = _wsl.IsVisible = isWsl;
                sshLabel.IsVisible = _target.IsVisible = _sshOptions.IsVisible = _sshPassword.IsVisible = _interactive.IsVisible = !isWsl;
            }
            SyncTransport();
            _kind.SelectionChanged += (_, _) => SyncTransport();
            panel.Children.Add(wslLabel); panel.Children.Add(_wsl);
            panel.Children.Add(sshLabel); panel.Children.Add(_target);
            Add(panel, "Remote user (optional)", _user);
            Add(panel, "Password (optional)", _sshPassword);
            Add(panel, "Extra SSH options (optional)", _sshOptions); panel.Children.Add(_interactive);
            Add(panel, "Repository path on the host", _path);
            var browse = new Button { Content = "Browse remote directories" };
            panel.Children.Add(browse);
            panel.Children.Add(new TextBlock { Text = "SSH authentication: key/agent by default (BatchMode); a stored password signs in silently on every reconnect (first host key accepted automatically); terminal login shows OpenSSH's own prompt in a window when no password is stored.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            Add(panel, "Server package (optional)", _package); panel.Children.Add(_log);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(_cancel); actions.Children.Add(_connect); panel.Children.Add(actions);
            Content = new ScrollViewer { Content = panel };
            _ = LoadDistributions();
            browse.Click += async (_, _) =>
            {
                browse.IsEnabled = false;
                try
                {
                    var workspace = await RemoteConnections.ConnectAsync(GetOptions(), diagnostic => Dispatcher.UIThread.Post(() => _log.Text += diagnostic.Stage + ": " + diagnostic.Message + "\n"), _lifetime.Token, manualRetry: true);
                    var path = await new RemoteDirectoryBrowser(workspace, _path.Text).ShowDialog<string>(this);
                    if (!string.IsNullOrEmpty(path)) _path.Text = path;
                }
                catch (Exception error) { _log.Text += error.Message + "\n"; }
                finally { browse.IsEnabled = true; }
            };
            _cancel.Click += (_, _) => _attempt?.Cancel();
            _connect.Click += async (_, _) =>
            {
                var attempt = new CancellationTokenSource();
                _attempt = attempt;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token, _lifetime.Token);
                _connect.IsEnabled = false; _cancel.IsEnabled = true;
                try
                {
                    var options = GetOptions();
                    RemoteConnections.Save(options);
                    var workspace = await RemoteConnections.ConnectAsync(options, diagnostic => Dispatcher.UIThread.Post(() => _log.Text += diagnostic.Stage + ": " + diagnostic.Message + "\n"), linked.Token, manualRetry: true);
                    var location = await workspace.OpenLocationAsync(_path.Text.Trim(), linked.Token);
                    var resource = workspace.Resource(location.Directory);
                    var node = ViewModels.Preferences.Instance.FindOrAddNodeByRepositoryPath(resource, null, false);
                    ViewModels.Preferences.Instance.Save(); ViewModels.Welcome.Instance.Refresh();
                    App.GetLauncher().OpenRepositoryInTab(node, null);
                    Close();
                }
                catch (OperationCanceledException) { _log.Text += "Connection cancelled.\n"; }
                catch (Exception error) { _log.Text += error.Message + "\n"; }
                finally { _connect.IsEnabled = true; _cancel.IsEnabled = false; _attempt = null; attempt.Dispose(); }
            };
            Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
        }
        private async System.Threading.Tasks.Task LoadDistributions()
        {
            try
            {
                var start = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.Unicode };
                start.ArgumentList.Add("--list"); start.ArgumentList.Add("--quiet");
                using var process = Process.Start(start) ?? throw new IOException("Cannot enumerate WSL distributions");
                var output = process.StandardOutput.ReadToEndAsync(_lifetime.Token);
                var error = process.StandardError.ReadToEndAsync(_lifetime.Token);
                await process.WaitForExitAsync(_lifetime.Token);
                if (process.ExitCode != 0) return;
                var names = (await output).Replace("\0", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Dispatcher.UIThread.Invoke(() =>
                {
                    _wsl.ItemsSource = names;
                    _wsl.SelectedItem = names.Contains("Ubuntu-24.04") ? "Ubuntu-24.04" : (names.Length > 0 ? names[0] : null);
                });
            }
            catch { /* Without WSL the transport list stays empty; the field remains editable. */ }
        }
        private readonly string _newId = Guid.NewGuid().ToString("N");
        private ConnectionOptions GetOptions()
        {
            var isWsl = _kind.SelectedIndex != 1;
            var target = (isWsl ? _wsl.Text : _target.Text)?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException(isWsl ? "Select a WSL distribution" : "Enter a connection target");
            var kind = isWsl ? "Wsl" : "Ssh";
            var user = string.IsNullOrWhiteSpace(_user.Text) ? null : _user.Text.Trim();
            // Reuse the saved Id of an existing connection to the same endpoint so "Connect and open"
            // attaches to the existing manager/workspace instead of minting a duplicate profile.
            var match = _saved.FirstOrDefault(x => RemoteConnections.SameIdentity(x, kind, target, user));
            var sshOptions = isWsl || string.IsNullOrWhiteSpace(_sshOptions.Text) ? null : _sshOptions.Text.Trim();
            var hasPassword = !isWsl && !string.IsNullOrEmpty(_sshPassword.Text);
            var interactive = !isWsl && (_interactive.IsChecked == true || (hasPassword && !OperatingSystem.IsWindows()));
            var sshPassword = hasPassword && OperatingSystem.IsWindows() ? ConnectionCredential.Encrypt(_sshPassword.Text) : null;
            return new ConnectionOptions(match?.Id ?? _newId, target, kind, target, user, string.IsNullOrWhiteSpace(_package.Text) ? null : _package.Text.Trim(), null, null, sshOptions, interactive, sshPassword);
        }
        private static void Add(StackPanel panel, string label, Control control)
        {
            panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(control);
        }
    }
}
