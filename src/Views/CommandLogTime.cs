using System;
using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public class CommandLogTime : TextBlock
    {
        protected override Type StyleKeyOverride => typeof(TextBlock);
        internal bool IsRefreshing => _refreshTimer?.IsEnabled == true;

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            _loaded = true;
            _visibility?.Dispose();
            _visibility = new TimeDisplayVisibility(this, UpdateTimer);
            BindLog(DataContext as ViewModels.CommandLog);
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);
            _loaded = false;
            _visibility?.Dispose();
            _visibility = null;
            BindLog(null);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            BindLog(DataContext as ViewModels.CommandLog);
        }

        protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty) UpdateTimer();
        }

        private void BindLog(ViewModels.CommandLog log)
        {
            StopTimer();
            if (_log != null) _log.PropertyChanged -= OnLogChanged;
            _log = log;
            Text = log == null ? string.Empty : GetDisplayText(log);
            if (_loaded && log != null) log.PropertyChanged += OnLogChanged;
            UpdateTimer();
        }

        private void OnLogChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ViewModels.CommandLog.IsComplete) || sender != _log || !_loaded) return;
            Text = GetDisplayText(_log);
            UpdateTimer();
        }

        private void UpdateTimer()
        {
            if (!_loaded || _visibility?.IsVisible != true || _log == null || _log.IsComplete)
            {
                StopTimer();
                return;
            }
            if (_refreshTimer != null) return;
            Text = GetDisplayText(_log);
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _refreshTimer.Tick += (_, _) =>
            {
                if (_loaded && _visibility?.IsVisible == true && _log != null)
                {
                    Text = GetDisplayText(_log);
                }
            };
            _refreshTimer.Start();
        }

        private void StopTimer()
        {
            if (_refreshTimer is not null)
            {
                _refreshTimer.Stop();
                _refreshTimer = null;
            }
        }

        private static string GetDisplayText(ViewModels.CommandLog log)
        {
            var endTime = log.IsComplete ? log.EndTime : DateTime.Now;
            var duration = endTime - log.StartTime;

            if (duration.TotalMinutes >= 1)
                return $"{duration.TotalMinutes:G3} min";

            if (duration.TotalSeconds >= 1)
                return $"{duration.TotalSeconds:G3} s";

            return $"{duration.TotalMilliseconds:G3} ms";
        }

        private DispatcherTimer _refreshTimer = null;
        private ViewModels.CommandLog _log;
        private bool _loaded;
        private TimeDisplayVisibility _visibility;
    }
}
