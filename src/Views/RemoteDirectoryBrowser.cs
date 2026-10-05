using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SourceGit.Contracts;
using SourceGit.Remote;

namespace SourceGit.Views
{
    public sealed class RemoteDirectoryBrowser : Window
    {
        private readonly RemoteWorkspace _workspace;
        private string _current;
        private readonly List<FileEntry> _entries = new();
        private readonly TextBlock _header = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        private readonly ListBox _list = new();
        private readonly Button _choose = new() { Content = "Choose this folder", HorizontalAlignment = HorizontalAlignment.Right };
        private CancellationTokenSource _loading;
        public RemoteDirectoryBrowser(RemoteWorkspace workspace, string path)
        {
            _workspace = workspace;
            _current = string.IsNullOrWhiteSpace(path) ? "" : NormalizePath(path);
            Title = "Browse remote directories"; Width = 560; Height = 440;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _list.SelectionChanged += async (_, _) =>
            {
                if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _entries.Count) return;
                var selected = _entries[_list.SelectedIndex];
                if (!selected.Directory) return;
                _current = NormalizePath(selected.Path);
                await Refresh();
            };
            var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right };
            _choose.Click += (_, _) => Close(_current);
            cancel.Click += (_, _) => Close(null);
            // A Grid (not a StackPanel) constrains the ListBox height so its built-in ScrollViewer
            // engages: with unbounded height it rendered every entry, overflowed the window and
            // neither the scrollbar nor mouse-wheel scrolling worked.
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.RowDefinitions.Add(new RowDefinition(new GridLength(1, GridUnitType.Star)));
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _header.Margin = new Thickness(0, 0, 0, 8);
            _list.Margin = new Thickness(0, 0, 0, 8);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            buttons.Children.Add(cancel); buttons.Children.Add(_choose);
            Grid.SetRow(_header, 0); Grid.SetRow(_list, 1); Grid.SetRow(buttons, 2);
            grid.Children.Add(_header); grid.Children.Add(_list); grid.Children.Add(buttons);
            Content = grid;
            Opened += async (_, _) => await Refresh();
            Closed += (_, _) => _loading?.Cancel();
        }
        private async Task Refresh()
        {
            _loading?.Cancel();
            using var loading = new CancellationTokenSource();
            _loading = loading;
            if (string.IsNullOrEmpty(_current)) _current = _workspace.Environment.Home;
            var path = _current;
            _list.IsEnabled = false;
            _choose.IsEnabled = false;
            _list.SelectedIndex = -1;
            _list.ItemsSource = null;
            _entries.Clear();
            if (_current != "/")
            {
                // POSIX parent: Path.GetDirectoryName is the Windows API and would emit backslashes on remote paths.
                var parent = _current.Length > 1 ? _current.Substring(0, _current.LastIndexOf('/')) : "/";
                if (parent.Length == 0) parent = "/";
                _entries.Add(new FileEntry("..", parent, true, 0));
            }
            _header.Text = path;
            try
            {
                await foreach (var page in _workspace.Client.DirectoryPagesAsync(path, loading.Token))
                {
                    // Return to the UI context before changing controls.
                    if (loading.IsCancellationRequested || _current != path) return;
                    _entries.AddRange(page);
                }
                _list.ItemsSource = _entries.Select(x => x.Name + (x.Directory ? "/" : "")).ToArray();
                _choose.IsEnabled = true;
            }
            catch (OperationCanceledException) when (loading.IsCancellationRequested) { }
            catch (TimeoutException error) { if (!loading.IsCancellationRequested) _header.Text = path + "\nDirectory listing timed out. Refresh to try again. " + error.Message; }
            catch (Exception error) { if (!loading.IsCancellationRequested) _header.Text = path + "\n" + error.Message; }
            finally
            {
                if (_loading == loading) { _loading = null; _list.IsEnabled = true; }
            }
        }
        private static string NormalizePath(string path) => path == "/" ? "/" : path.TrimEnd('/');
    }
}
