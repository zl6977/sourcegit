using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    // Loaded time displays observe visibility changes; hidden pages/windows
    // never need a timer to discover that they should stop refreshing.
    internal sealed class TimeDisplayVisibility : IDisposable
    {
        private readonly Visual _owner;
        private readonly Visual[] _ancestors;
        private readonly Window _window;
        private readonly Action _changed;
        private bool _disposed;

        internal TimeDisplayVisibility(Control owner, Action changed)
        {
            _owner = owner;
            _ancestors = owner.GetVisualAncestors().ToArray();
            _window = TopLevel.GetTopLevel(owner) as Window;
            _changed = changed;
            foreach (var ancestor in _ancestors)
                ancestor.PropertyChanged += OnChanged;
        }

        internal bool IsVisible => !_disposed && _owner.IsVisible &&
            _ancestors.All(ancestor => ancestor.IsVisible) && _window?.WindowState != WindowState.Minimized;

        private void OnChanged(object sender, AvaloniaPropertyChangedEventArgs change)
        {
            if (!_disposed && (change.Property == Visual.IsVisibleProperty || change.Property == Window.WindowStateProperty))
                _changed();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var ancestor in _ancestors)
                ancestor.PropertyChanged -= OnChanged;
        }
    }
}
