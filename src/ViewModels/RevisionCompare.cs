using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class RevisionCompare : ObservableObject
    {
        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        public object StartPoint
        {
            get => _startPoint;
            private set => SetProperty(ref _startPoint, value);
        }

        public object EndPoint
        {
            get => _endPoint;
            private set => SetProperty(ref _endPoint, value);
        }

        public string LeftSideDesc
        {
            get => GetDesc(StartPoint);
        }

        public string RightSideDesc
        {
            get => GetDesc(EndPoint);
        }

        public bool CanResetToLeft
        {
            get => !_repo.IsBare && _startPoint != null;
        }

        public bool CanResetToRight
        {
            get => !_repo.IsBare && _endPoint != null;
        }

        public bool CanSaveAsPatch
        {
            get => _startPoint != null && _endPoint != null;
        }

        public int TotalChanges
        {
            get => _totalChanges;
            private set => SetProperty(ref _totalChanges, value);
        }

        public List<Models.Change> VisibleChanges
        {
            get => _visibleChanges;
            private set => SetProperty(ref _visibleChanges, value);
        }

        public List<Models.Change> SelectedChanges
        {
            get => _selectedChanges;
            set
            {
                if (SetProperty(ref _selectedChanges, value))
                {
                    if (value is { Count: 1 })
                    {
                        var option = new Models.DiffOption(GetSHA(_startPoint), GetSHA(_endPoint), value[0]);
                        DiffContext = new DiffContext(_repo.FullPath, option, _diffContext);
                    }
                    else
                    {
                        DiffContext = null;
                    }
                }
            }
        }

        public string SearchFilter
        {
            get => _searchFilter;
            set
            {
                if (SetProperty(ref _searchFilter, value))
                    RefreshVisible();
            }
        }

        public DiffContext DiffContext
        {
            get => _diffContext;
            private set => SetProperty(ref _diffContext, value);
        }

        public RevisionCompare(Repository repo, Models.Commit startPoint, Models.Commit endPoint)
        {
            _repo = repo;
            _startPoint = (object)startPoint ?? new Models.Null();
            _endPoint = (object)endPoint ?? new Models.Null();
            Refresh();
        }

        public RevisionCompare Clone()
        {
            return new RevisionCompare(_repo, _startPoint as Models.Commit, _endPoint as Models.Commit);
        }

        public void SetTargets(Models.Commit l, Models.Commit r)
        {
            var hashes = new HashSet<string>();
            hashes.Add(l.SHA);
            hashes.Add(r.SHA);

            if (_startPoint is Models.Commit s &&
                _endPoint is Models.Commit e &&
                hashes.Contains(s.SHA) &&
                hashes.Contains(e.SHA))
                return;

            _startPoint = l;
            _endPoint = r;
            Refresh();
        }

        public void OpenChangeWithExternalDiffTool(Models.Change change)
        {
            var opt = new Models.DiffOption(GetSHA(_startPoint), GetSHA(_endPoint), change);
            new Commands.DiffTool(_repo.FullPath, opt).Open();
        }

        public void NavigateTo(string commitSHA)
        {
            _repo?.NavigateToCommit(commitSHA);
        }

        public void Swap()
        {
            (StartPoint, EndPoint) = (_endPoint, _startPoint);
            VisibleChanges = [];
            SelectedChanges = [];
            IsLoading = true;
            Refresh();
        }

        public string GetAbsPath(string path)
        {
            return Native.OS.GetAbsPath(_repo.FullPath, path);
        }

        public async Task ResetToLeftAsync(Models.Change change)
        {
            var sha = GetSHA(_startPoint);
            await RevisionFileOperations.ResetChangeToRevisionAsync(_repo, change, sha, GetDesc(_startPoint), RevisionFileOperations.Side.Left);
        }

        public async Task ResetToRightAsync(Models.Change change)
        {
            var sha = GetSHA(_endPoint);
            await RevisionFileOperations.ResetChangeToRevisionAsync(_repo, change, sha, GetDesc(_endPoint), RevisionFileOperations.Side.Right);
        }

        public async Task ResetMultipleToLeftAsync(List<Models.Change> changes)
        {
            var sha = GetSHA(_startPoint);
            await RevisionFileOperations.ResetChangesToRevisionAsync(_repo, changes, sha, GetDesc(_startPoint), RevisionFileOperations.Side.Left);
        }

        public async Task ResetMultipleToRightAsync(List<Models.Change> changes)
        {
            var sha = GetSHA(_endPoint);
            await RevisionFileOperations.ResetChangesToRevisionAsync(_repo, changes, sha, GetDesc(_endPoint), RevisionFileOperations.Side.Right);
        }

        public async Task SaveChangesAsPatchAsync(List<Models.Change> changes, string saveTo)
        {
            var succ = await Commands.SaveChangesAsPatch.ProcessRevisionCompareChangesAsync(_repo.FullPath, changes ?? _changes, GetSHA(_startPoint), GetSHA(_endPoint), saveTo);
            if (succ)
                _repo.SendNotification(App.Text("SaveAsPatchSuccess"));
        }

        public void ClearSearchFilter()
        {
            SearchFilter = string.Empty;
        }

        private void RefreshVisible()
        {
            if (_changes == null)
                return;

            if (string.IsNullOrEmpty(_searchFilter))
            {
                VisibleChanges = _changes;
            }
            else
            {
                var visible = new List<Models.Change>();
                foreach (var c in _changes)
                {
                    if (c.Path.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                        visible.Add(c);
                }

                VisibleChanges = visible;
            }
        }

        private void Refresh()
        {
            Task.Run(async () =>
            {
                _changes = await new Commands.CompareRevisions(_repo.FullPath, GetSHA(_startPoint), GetSHA(_endPoint))
                    .ReadAsync()
                    .ConfigureAwait(false);

                var visible = _changes;
                if (!string.IsNullOrWhiteSpace(_searchFilter))
                {
                    visible = [];
                    foreach (var c in _changes)
                    {
                        if (c.Path.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                            visible.Add(c);
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    TotalChanges = _changes.Count;
                    VisibleChanges = visible;
                    IsLoading = false;

                    if (VisibleChanges.Count > 0)
                        SelectedChanges = [VisibleChanges[0]];
                    else
                        SelectedChanges = [];
                });
            });
        }

        private string GetSHA(object obj)
        {
            return obj is Models.Commit commit ? commit.SHA : string.Empty;
        }

        private string GetDesc(object obj)
        {
            return obj is Models.Commit commit ? commit.GetFriendlyName() : App.Text("Worktree");
        }

        private Repository _repo;
        private bool _isLoading = true;
        private object _startPoint = null;
        private object _endPoint = null;
        private int _totalChanges = 0;
        private List<Models.Change> _changes = null;
        private List<Models.Change> _visibleChanges = null;
        private List<Models.Change> _selectedChanges = null;
        private string _searchFilter = string.Empty;
        private DiffContext _diffContext = null;
    }
}
