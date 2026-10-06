using System;
using System.IO;
using System.Text;

using Avalonia.Collections;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class Launcher : ObservableObject
    {
        public string Title
        {
            get => _title;
            private set => SetProperty(ref _title, value);
        }

        public AvaloniaList<LauncherPage> Pages
        {
            get;
            private set;
        }

        public Workspace ActiveWorkspace
        {
            get => _activeWorkspace;
            private set => SetProperty(ref _activeWorkspace, value);
        }

        public LauncherPage ActivePage
        {
            get => _activePage;
            set
            {
                if (SetProperty(ref _activePage, value))
                    PostActivePageChanged();
            }
        }

        public ICommandPalette CommandPalette
        {
            get => _commandPalette;
            set => SetProperty(ref _commandPalette, value);
        }

        public Models.Version NewVersion
        {
            get => _newVersion;
            set => SetProperty(ref _newVersion, value);
        }

        public Launcher(string startupRepo)
        {
            Models.Notification.Raised += DispatchNotification;
            _ignoreIndexChange = true;

            ActiveWorkspace = Preferences.Instance.GetActiveWorkspace();
            Pages = new AvaloniaList<LauncherPage>();
            AddNewTab();

            var repos = ActiveWorkspace.Repositories.ToArray();
            foreach (var repo in repos)
                OpenRepositoryInTab(repo, null);

            _ignoreIndexChange = false;

            if (!TryOpenRepositoryFromPath(startupRepo))
            {
                var activeIdx = ActiveWorkspace.ActiveIdx;
                if (activeIdx > 0 && activeIdx < Pages.Count)
                    ActivePage = Pages[activeIdx];
                else
                    ActivePage = Pages[0];
            }

            PostActivePageChanged();
        }

        public bool TryOpenRepositoryFromPath(string repo)
        {
            if (SourceGit.Remote.RepositoryPath.IsRemote(repo))
            {
                OpenRepositoryInTab(repo, null);
                return true;
            }

            if (!string.IsNullOrEmpty(repo) && !SourceGit.Remote.RepositoryPath.IsRemote(repo))
                // Conservative normalization: user-supplied paths (dialog, drag-drop, activation) arrive in
                // mixed slash forms; canonical local form is forward slashes. Remote URIs stay untouched.
                repo = repo.Replace('\\', '/').TrimEnd('/').Trim();
            if (!string.IsNullOrEmpty(repo) && SourceGit.Remote.RepositoryDirectory.Exists(repo))
            {
                var isBare = new Commands.IsBareRepository(repo).GetResult();
                if (isBare)
                {
                    var node = Preferences.Instance.FindOrAddNodeByRepositoryPath(repo, null, false);
                    Welcome.Instance.Refresh();
                    OpenRepositoryInTab(node, null);
                    return true;
                }

                var test = new Commands.QueryRepositoryRootPath(repo).GetResult();
                if (test.IsSuccess && !string.IsNullOrEmpty(test.StdOut))
                {
                    var node = Preferences.Instance.FindOrAddNodeByRepositoryPath(test.StdOut.Trim(), null, false);
                    Welcome.Instance.Refresh();
                    OpenRepositoryInTab(node, null);
                    return true;
                }
                else
                {
                    if (ActivePage is not { Data: Welcome { }, Popup: null })
                        AddNewTab();

                    if (Models.SafeDirectories.IsUntrustedRepository(test.StdErr) &&
                        Models.SafeDirectories.TryGetSafeDirectoryValue(repo, test.StdErr, out var safeDirectory))
                        ActivePage.Popup = new TrustRepository(ActivePage.Node.Id, repo, test.StdErr, safeDirectory, null, false, true, 0);
                    else
                        ActivePage.Popup = new Init(ActivePage.Node.Id, repo, null, 0, test.StdErr ?? "Unknown error occurred while opening the repository.");

                    return true;
                }
            }

            return false;
        }

        public void CloseAll()
        {
            _ignoreIndexChange = true;

            foreach (var one in Pages)
                CloseRepositoryInTab(one, false);

            _ignoreIndexChange = false;
        }

        public void SwitchWorkspace(Workspace to)
        {
            if (to == null || to.IsActive)
                return;

            _ignoreIndexChange = true;

            var pref = Preferences.Instance;
            foreach (var w in pref.Workspaces)
                w.IsActive = false;

            ActiveWorkspace = to;
            to.IsActive = true;

            foreach (var one in Pages)
                CloseRepositoryInTab(one, false);

            Pages.Clear();
            AddNewTab();

            var repos = to.Repositories.ToArray();
            foreach (var repo in repos)
                OpenRepositoryInTab(repo, null);

            var activeIdx = to.ActiveIdx;
            if (activeIdx >= 0 && activeIdx < Pages.Count)
                ActivePage = Pages[activeIdx];
            else
                ActivePage = Pages[0];

            _ignoreIndexChange = false;
            PostActivePageChanged();
            Preferences.Instance.Save();
            GC.Collect();
        }

        public void AddNewTab()
        {
            var page = new LauncherPage();
            Pages.Add(page);
            ActivePage = page;
        }

        public void MoveTab(LauncherPage from, LauncherPage to)
        {
            _ignoreIndexChange = true;

            var fromIdx = Pages.IndexOf(from);
            var toIdx = Pages.IndexOf(to);
            Pages.Move(fromIdx, toIdx);

            _activeWorkspace.Repositories.Clear();
            foreach (var p in Pages)
            {
                if (p.Data is Repository r)
                    _activeWorkspace.Repositories.Add(r.FullPath);
                else if (p.Data is RemoteDirectory directory)
                    _activeWorkspace.Repositories.Add(directory.FullPath);
            }

            _ignoreIndexChange = false;
            ActivePage = from;
        }

        public void GotoNextTab()
        {
            if (Pages.Count == 1)
                return;

            var activeIdx = Pages.IndexOf(_activePage);
            var nextIdx = (activeIdx + 1) % Pages.Count;
            ActivePage = Pages[nextIdx];
        }

        public void GotoPrevTab()
        {
            if (Pages.Count == 1)
                return;

            var activeIdx = Pages.IndexOf(_activePage);
            var prevIdx = activeIdx == 0 ? Pages.Count - 1 : activeIdx - 1;
            ActivePage = Pages[prevIdx];
        }

        public void CloseTab(LauncherPage page)
        {
            if (Pages.Count == 1)
            {
                var last = Pages[0];
                if (last.Data is Repository || last.Data is RemoteDirectory)
                {
                    _activeWorkspace.Repositories.Clear();
                    _activeWorkspace.ActiveIdx = 0;

                    if (last.Data is Repository repo)
                    {
                        if (last.Node.IsUnmanaged)
                            last.Node.SaveMinimalInfo(repo.GitDir);
                        repo.Close();
                    }

                    Welcome.Instance.ClearSearchFilter();
                    last.Node = new RepositoryNode() { Id = Guid.NewGuid().ToString() };
                    last.Data = Welcome.Instance;
                    last.Popup?.Cleanup();
                    last.Popup = null;

                    PostActivePageChanged();
                    GC.Collect();
                }
                else
                {
                    App.Quit(0);
                }

                return;
            }

            page ??= _activePage;

            var removeIdx = Pages.IndexOf(page);
            var activeIdx = Pages.IndexOf(_activePage);
            if (removeIdx == activeIdx)
                ActivePage = Pages[removeIdx > 0 ? removeIdx - 1 : removeIdx + 1];

            CloseRepositoryInTab(page);
            Pages.RemoveAt(removeIdx);
            GC.Collect();
        }

        public void CloseOtherTabs()
        {
            if (Pages.Count == 1)
                return;

            _ignoreIndexChange = true;

            var id = ActivePage.Node.Id;
            foreach (var one in Pages)
            {
                if (one.Node.Id != id)
                    CloseRepositoryInTab(one);
            }

            Pages = new AvaloniaList<LauncherPage> { ActivePage };
            OnPropertyChanged(nameof(Pages));

            _activeWorkspace.ActiveIdx = 0;
            _ignoreIndexChange = false;
            GC.Collect();
        }

        public void CloseRightTabs()
        {
            _ignoreIndexChange = true;

            var endIdx = Pages.IndexOf(ActivePage);
            for (var i = Pages.Count - 1; i > endIdx; i--)
            {
                CloseRepositoryInTab(Pages[i]);
                Pages.Remove(Pages[i]);
            }

            _ignoreIndexChange = false;
            GC.Collect();
        }

        public void OpenRepositoryInTab(string repo, LauncherPage page)
        {
            var normalizedPath = SourceGit.Remote.RemoteConnections.PreferredRoute(SourceGit.Remote.RepositoryPath.StoredPath(repo), SourceGit.Remote.RemoteConnections.LoadCached());
            var node = Preferences.Instance.FindNode(normalizedPath) ?? new RepositoryNode
            {
                Id = normalizedPath,
                Name = SourceGit.Remote.RepositoryPath.GetFileName(normalizedPath),
                Bookmark = 0,
                IsRepository = true,
                IsUnmanaged = true
            };

            OpenRepositoryInTab(node, null);
        }

        public void OpenRepositoryInTab(RepositoryNode node, LauncherPage page)
        {
            foreach (var one in Pages)
            {
                if (one.Node.Id == node.Id && one != page)
                {
                    ActivePage = one;
                    return;
                }
            }

            if (SourceGit.Remote.RepositoryPath.IsRemote(node.Id))
            {
                _ = OpenRemoteRepositoryAsync(node, page);
                return;
            }

            if (!SourceGit.Remote.RepositoryDirectory.Exists(node.Id))
            {
                ActivePage.Notifications.Add(new Models.Notification
                {
                    Group = node.Id,
                    Message = "Repository does NOT exist any more. Please remove it.",
                    IsError = true,
                });
                return;
            }

            var isBare = new Commands.IsBareRepository(node.Id).GetResult();
            var gitDir = isBare ? node.Id : GetRepositoryGitDir(node.Id);
            if (string.IsNullOrEmpty(gitDir))
            {
                ActivePage.Notifications.Add(new Models.Notification
                {
                    Group = node.Id,
                    Message = "Given path is not a valid git repository!",
                    IsError = true,
                });
                return;
            }

            if (node.IsUnmanaged)
                node.LoadMinimalInfo(gitDir);

            var repo = new Repository(isBare, node.Id, gitDir);
            repo.Open();

            AttachRepository(node, page, repo);
        }

        internal async System.Threading.Tasks.Task OpenRemoteRepositoryAsync(RepositoryNode node, LauncherPage page)
        {
            var previousData = page?.Data;
            lock (_remoteOpeningGate)
            {
                if (!_remoteOpening.Add(node.Id)) return;
            }

            Models.Notification connecting = null;
            try
            {
                using var cancellation = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90), RemoteOpenClock);
                string name = null;
                foreach (var one in SourceGit.Remote.RemoteConnections.Load())
                {
                    if (one.Id == SourceGit.Remote.RepositoryPath.ConnectionId(node.Id)) { name = one.Name; break; }
                }
                if (name != null) connecting = Models.Notification.Send(node.Id, "Connecting to " + name + "…", false);
                var workspace = await (RestoreRemoteWorkspace ?? SourceGit.Remote.RemoteConnections.RestoreAsync)(node.Id, cancellation.Token);
                var location = await workspace.OpenLocationAsync(node.Id, cancellation.Token);
                if (page != null && (!Pages.Contains(page) || !ReferenceEquals(page.Data, previousData))) return;
                foreach (var existing in Pages)
                {
                    if (existing != page && existing.Node.Id == node.Id) { ActivePage = existing; return; }
                }
                if (location.Repository == null)
                {
                    AttachPage(node, page, new RemoteDirectory(this, node, workspace.Resource(location.Directory)));
                    return;
                }
                var info = location.Repository;
                var repo = await System.Threading.Tasks.Task.Run(() => new Repository(info.Bare, workspace.Resource(info.WorktreePath), workspace.Resource(info.GitDirectory)));
                if (page != null && (!Pages.Contains(page) || !ReferenceEquals(page.Data, previousData))) return;
                repo.Open();
                AttachRepository(node, page, repo);
            }
            catch (Exception error) { Models.Notification.Send(node.Id, error.Message, true); }
            finally
            {
                DismissNotification(connecting);
                lock (_remoteOpeningGate) { _remoteOpening.Remove(node.Id); }
            }
        }

        private void AttachRepository(RepositoryNode node, LauncherPage page, Repository repo)
            => AttachPage(node, page, repo);

        private void AttachPage(RepositoryNode node, LauncherPage page, object data)
        {

            if (page == null)
            {
                if (_activePage == null || _activePage.Node.IsRepository)
                {
                    page = new LauncherPage(node, null) { Data = data };
                    Pages.Add(page);
                }
                else
                {
                    page = _activePage;
                    page.Node = node;
                    page.Data = data;
                }
            }
            else
            {
                page.Node = node;
                page.Data = data;
            }

            _activeWorkspace.Repositories.Clear();
            foreach (var p in Pages)
            {
                if (p.Data is Repository r)
                    _activeWorkspace.Repositories.Add(r.FullPath);
                else if (p.Data is RemoteDirectory directory)
                    _activeWorkspace.Repositories.Add(directory.FullPath);
            }

            if (_activePage == page)
                PostActivePageChanged();
            else
                ActivePage = page;
        }

        public void OpenSubRepository(LauncherPage ownerPage, string fullpath)
        {
            if (SourceGit.Remote.RepositoryPath.IsRemote(fullpath))
            {
                OpenRepositoryInTab(fullpath, null);
                return;
            }
            var normalizedPath = SourceGit.Remote.RepositoryPath.StoredPath(fullpath);

            // Check if the sub-repository is already open in any of the tabs
            foreach (var one in Pages)
            {
                if (one.Node.Id.Equals(normalizedPath, StringComparison.Ordinal))
                {
                    ActivePage = one;
                    return;
                }
            }

            // Make sure the target directory exists
            if (!SourceGit.Remote.RepositoryDirectory.Exists(normalizedPath))
            {
                ownerPage.Notifications.Add(new Models.Notification
                {
                    Group = ownerPage.Node.Id,
                    Message = "Repository path does NOT exist. Please check the path.",
                    IsError = true,
                });

                return;
            }

            // Check if the target directory is a valid git repository
            var gitDir = GetRepositoryGitDir(normalizedPath);
            if (string.IsNullOrEmpty(gitDir))
            {
                ownerPage.Notifications.Add(new Models.Notification
                {
                    Group = ownerPage.Node.Id,
                    Message = "Given path is not a valid git repository!",
                    IsError = true,
                });
                return;
            }

            // Get the owner repository's name and extract the pure owner name (without any prefix)
            var ownerName = ownerPage.Node.Name;
            var colonIdx = ownerName.LastIndexOf(':');
            var pureOwnerName = (colonIdx >= 0 && colonIdx < ownerName.Length - 1) ? ownerName.Substring(colonIdx + 1).Trim() : ownerName;

            // Find the sub-repository node in the preferences or create a new one if it doesn't exist
            var node = Preferences.Instance.FindNode(normalizedPath) ?? new RepositoryNode
            {
                Id = normalizedPath,
                Name = $"{pureOwnerName}: {SourceGit.Remote.RepositoryPath.GetFileName(normalizedPath)}",
                Bookmark = 0,
                IsRepository = true,
                IsUnmanaged = true
            };
            node.LoadMinimalInfo(gitDir);

            var repo = new Repository(false, node.Id, gitDir);
            repo.Open();

            var page = new LauncherPage(node, repo);
            var idxOfOwner = Pages.IndexOf(ownerPage);
            Pages.Insert(idxOfOwner + 1, page);
            _activeWorkspace.Repositories.Insert(idxOfOwner + 1, normalizedPath);
            ActivePage = page;
        }

        private void DispatchNotification(Models.Notification notification)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Invoke(() => DispatchNotification(notification));
                return;
            }

            if (string.IsNullOrEmpty(notification.Group))
            {
                _activePage?.Notifications.Add(notification);
                return;
            }

            foreach (var page in Pages)
            {
                var id = SourceGit.Remote.RepositoryPath.StoredPath(page.Node.Id);
                if (id.Equals(notification.Group, StringComparison.OrdinalIgnoreCase))
                {
                    page.Notifications.Add(notification);
                    return;
                }
            }

            _activePage?.Notifications.Add(notification);
        }

        // Removes a previously sent notification (by reference) from whatever page it landed on.
        private void DismissNotification(Models.Notification notification)
        {
            if (notification == null) return;
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Invoke(() => DismissNotification(notification));
                return;
            }

            foreach (var page in Pages)
            {
                page.Notifications.Remove(notification);
            }
        }

        private string GetRepositoryGitDir(string repo)
        {
            var fullpath = SourceGit.Remote.RepositoryPath.Combine(repo, ".git");
            if (SourceGit.Remote.RepositoryDirectory.Exists(fullpath))
            {
                if (SourceGit.Remote.RepositoryDirectory.Exists(SourceGit.Remote.RepositoryPath.Combine(fullpath, "refs")) &&
                    SourceGit.Remote.RepositoryDirectory.Exists(SourceGit.Remote.RepositoryPath.Combine(fullpath, "objects")) &&
                    SourceGit.Remote.RepositoryFile.Exists(SourceGit.Remote.RepositoryPath.Combine(fullpath, "HEAD")))
                    return fullpath;

                return null;
            }

            if (SourceGit.Remote.RepositoryFile.Exists(fullpath))
            {
                var redirect = SourceGit.Remote.RepositoryFile.ReadAllText(fullpath).Trim();
                if (redirect.StartsWith("gitdir: ", StringComparison.Ordinal))
                    redirect = redirect.Substring(8);

                if (!SourceGit.Remote.RepositoryPath.IsPathRooted(redirect))
                    redirect = SourceGit.Remote.RepositoryPath.GetFullPath(SourceGit.Remote.RepositoryPath.Combine(repo, redirect));

                if (SourceGit.Remote.RepositoryDirectory.Exists(redirect))
                    return redirect;

                return null;
            }

            return new Commands.QueryGitDir(repo).GetResult();
        }

        private void CloseRepositoryInTab(LauncherPage page, bool removeFromWorkspace = true)
        {
            if (page.Data is Repository repo)
            {
                if (removeFromWorkspace)
                    _activeWorkspace.Repositories.Remove(repo.FullPath);

                if (page.Node.IsUnmanaged)
                    page.Node.SaveMinimalInfo(repo.GitDir);

                repo.Close();
            }
            else if (removeFromWorkspace && page.Data is RemoteDirectory directory)
            {
                _activeWorkspace.Repositories.Remove(directory.FullPath);
            }

            page.Popup?.Cleanup();
            page.Popup = null;
            page.Data = null;
        }

        private void PostActivePageChanged()
        {
            if (_ignoreIndexChange)
                return;

            if (_activePage is { Data: Repository repo })
                _activeWorkspace.ActiveIdx = _activeWorkspace.Repositories.IndexOf(repo.FullPath);
            else if (_activePage is { Data: RemoteDirectory directory })
                _activeWorkspace.ActiveIdx = _activeWorkspace.Repositories.IndexOf(directory.FullPath);

            var builder = new StringBuilder(512);
            builder.Append(string.IsNullOrEmpty(_activePage.Node.Name) ? "Repositories" : _activePage.Node.Name);

            var workspaces = Preferences.Instance.Workspaces;
            if (workspaces.Count == 0 || workspaces.Count > 1 || workspaces[0] != _activeWorkspace)
                builder.Append(" - ").Append(_activeWorkspace.Name);

            Title = builder.ToString();
            CommandPalette = null;
        }

        private Workspace _activeWorkspace;
        private LauncherPage _activePage;
        private bool _ignoreIndexChange;
        private readonly System.Collections.Generic.HashSet<string> _remoteOpening = new();
        private readonly object _remoteOpeningGate = new();
        internal TimeProvider RemoteOpenClock { get; set; } = TimeProvider.System;
        internal Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<SourceGit.Remote.RemoteWorkspace>> RestoreRemoteWorkspace { get; set; }
        private string _title = string.Empty;
        private ICommandPalette _commandPalette;
        private Models.Version _newVersion = null;
    }
}
