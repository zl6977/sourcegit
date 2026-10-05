using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public class CommitTimeTextBlock : TextBlock
    {
        public static readonly DirectProperty<CommitTimeTextBlock, bool> ShowAsDateTimeProperty =
            AvaloniaProperty.RegisterDirect<CommitTimeTextBlock, bool>(
                nameof(ShowAsDateTime),
                static o => o.ShowAsDateTime,
                static (o, v) => o.ShowAsDateTime = v);

        public bool ShowAsDateTime
        {
            get => _showAsDateTime;
            set => SetAndRaise(ShowAsDateTimeProperty, ref _showAsDateTime, value);
        }

        public static readonly DirectProperty<CommitTimeTextBlock, bool> Use24HoursProperty =
            AvaloniaProperty.RegisterDirect<CommitTimeTextBlock, bool>(
                nameof(Use24Hours),
                static o => o.Use24Hours,
                static (o, v) => o.Use24Hours = v);

        public bool Use24Hours
        {
            get => _use24Hours;
            set => SetAndRaise(Use24HoursProperty, ref _use24Hours, value);
        }

        public static readonly DirectProperty<CommitTimeTextBlock, int> DateTimeFormatProperty =
            AvaloniaProperty.RegisterDirect<CommitTimeTextBlock, int>(
                nameof(DateTimeFormat),
                static o => o.DateTimeFormat,
                static (o, v) => o.DateTimeFormat = v);

        public int DateTimeFormat
        {
            get => _dateTimeFormat;
            set => SetAndRaise(DateTimeFormatProperty, ref _dateTimeFormat, value);
        }

        public static readonly DirectProperty<CommitTimeTextBlock, ulong> TimestampProperty =
            AvaloniaProperty.RegisterDirect<CommitTimeTextBlock, ulong>(
                nameof(Timestamp),
                static o => o.Timestamp,
                static (o, v) => o.Timestamp = v);

        public ulong Timestamp
        {
            get => _timestamp;
            set => SetAndRaise(TimestampProperty, ref _timestamp, value);
        }

        protected override Type StyleKeyOverride => typeof(TextBlock);
        internal bool IsRefreshing => _refreshTimer?.IsEnabled == true;

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == TimestampProperty)
            {
                SetCurrentValue(TextProperty, GetDisplayText());
            }
            else if (change.Property == ShowAsDateTimeProperty)
            {
                SetCurrentValue(TextProperty, GetDisplayText());

                if (ShowAsDateTime)
                {
                    UpdateTimer();
                    HorizontalAlignment = HorizontalAlignment.Left;
                }
                else
                {
                    UpdateTimer();
                    HorizontalAlignment = HorizontalAlignment.Center;
                }
            }
            else if (change.Property == DateTimeFormatProperty || change.Property == Use24HoursProperty)
            {
                if (ShowAsDateTime)
                    SetCurrentValue(TextProperty, GetDisplayText());
            }
            else if (change.Property == IsVisibleProperty) UpdateTimer();
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);
            _loaded = true;
            _visibility?.Dispose();
            _visibility = new TimeDisplayVisibility(this, UpdateTimer);
            if (_refreshTimer == null)
            {
                _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                _refreshTimer.Tick += static (o, _) =>
                {
                    if (o is DispatcherTimer { Tag: CommitTimeTextBlock textBlock } && textBlock._loaded && textBlock._visibility?.IsVisible == true)
                    {
                        var text = textBlock.GetDisplayText();
                        if (!text.Equals(textBlock.Text, StringComparison.Ordinal)) textBlock.Text = text;
                    }
                };
            }
            _refreshTimer.Tag = this;
            SetCurrentValue(TextProperty, GetDisplayText());
            UpdateTimer();
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            _loaded = false;
            _visibility?.Dispose();
            _visibility = null;
            if (_refreshTimer != null) { _refreshTimer.Tag = null; _refreshTimer.Stop(); }

            base.OnUnloaded(e);
        }

        private void UpdateTimer()
        {
            if (_refreshTimer == null) return;
            var enabled = _loaded && _visibility?.IsVisible == true && !ShowAsDateTime;
            if (enabled && !_refreshTimer.IsEnabled) SetCurrentValue(TextProperty, GetDisplayText());
            _refreshTimer.IsEnabled = enabled;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            SetCurrentValue(TextProperty, GetDisplayText());
        }

        private string GetDisplayText()
        {
            var timestamp = Timestamp;
            if (ShowAsDateTime)
                return Models.DateTimeFormat.Format(timestamp);

            var now = DateTime.Now;
            var localTime = DateTime.UnixEpoch.AddSeconds(timestamp).ToLocalTime();
            var span = now - localTime;
            if (span.TotalMinutes < 1)
                return App.Text("Period.JustNow");

            if (span.TotalHours < 1)
                return App.Text("Period.MinutesAgo", (int)span.TotalMinutes);

            if (span.TotalDays < 1)
            {
                var hours = (int)span.TotalHours;
                return hours == 1 ? App.Text("Period.HourAgo") : App.Text("Period.HoursAgo", hours);
            }

            var lastDay = now.AddDays(-1).Date;
            if (localTime >= lastDay)
                return App.Text("Period.Yesterday");

            if ((localTime.Year == now.Year && localTime.Month == now.Month) || span.TotalDays < 28)
            {
                var diffDay = now.Date - localTime.Date;
                return App.Text("Period.DaysAgo", (int)diffDay.TotalDays);
            }

            var lastMonth = now.AddMonths(-1).Date;
            if (localTime.Year == lastMonth.Year && localTime.Month == lastMonth.Month)
                return App.Text("Period.LastMonth");

            if (localTime.Year == now.Year || localTime > now.AddMonths(-11))
            {
                var diffMonth = (12 + now.Month - localTime.Month) % 12;
                return App.Text("Period.MonthsAgo", diffMonth);
            }

            var diffYear = now.Year - localTime.Year;
            if (diffYear == 1)
                return App.Text("Period.LastYear");

            return App.Text("Period.YearsAgo", diffYear);
        }

        private bool _showAsDateTime = true;
        private bool _use24Hours = true;
        private int _dateTimeFormat = 0;
        private ulong _timestamp = 0;
        private DispatcherTimer _refreshTimer = null;
        private bool _loaded;
        private TimeDisplayVisibility _visibility;
    }
}
