using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SourceGit.Contracts;
using SourceGit.Remote;

namespace SourceGit.ViewModels
{
    public class RemoteDirectory : ObservableObject
    {
        public string FullPath { get; }
        public string DisplayPath => RepositoryPath.RemotePath(FullPath);
        public AsyncRelayCommand InitializeCommand { get; }
        public AsyncRelayCommand RefreshCommand { get; }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (!SetProperty(ref _isBusy, value)) return;
                InitializeCommand.NotifyCanExecuteChanged();
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }

        internal RemoteDirectory(Launcher launcher, RepositoryNode node, string path)
        {
            _launcher = launcher;
            _node = node;
            FullPath = path;
            InitializeCommand = new AsyncRelayCommand(() => OpenAsync(true), () => !IsBusy);
            RefreshCommand = new AsyncRelayCommand(() => OpenAsync(false), () => !IsBusy);
        }

        private async Task OpenAsync(bool initialize)
        {
            var page = _launcher.Pages.FirstOrDefault(p => ReferenceEquals(p.Data, this));
            if (page == null || IsBusy) return;
            IsBusy = true;
            try
            {
                if (initialize)
                {
                    var workspace = await (_launcher.RestoreRemoteWorkspace ?? RemoteConnections.RestoreAsync)(FullPath, default);
                    var location = await workspace.OpenLocationAsync(FullPath);
                    if (!_launcher.Pages.Contains(page) || !ReferenceEquals(page.Data, this)) return;
                    if (location.Repository == null)
                    {
                        var result = await workspace.Client.ExecuteGitAsync(new GitRequest("", ["init", "-q"], ReadOnly: false, WorkingDirectory: location.Directory));
                        if (result.ExitCode != 0) throw new System.IO.IOException(result.Error);
                    }
                }
                if (_launcher.Pages.Contains(page) && ReferenceEquals(page.Data, this))
                    await _launcher.OpenRemoteRepositoryAsync(_node, page);
            }
            catch (Exception error) { Models.Notification.Send(_node.Id, error.Message, true); }
            finally { IsBusy = false; }
        }

        private readonly Launcher _launcher;
        private readonly RepositoryNode _node;
        private bool _isBusy;
    }
}
