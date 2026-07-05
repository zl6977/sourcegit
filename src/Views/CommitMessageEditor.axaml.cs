using System;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class CommitMessageEditor : ChromelessWindow
    {
        public string ConventionalTypesOverride
        {
            get;
            private set;
        } = string.Empty;

        public CommitMessageEditor()
        {
            InitializeComponent();
        }

        public void AsStandalone(string file)
        {
            ConventionalTypesOverride = ViewModels.CommitMessageOperations.LoadConventionalTypesOverride(file);
            _onSave = msg => ViewModels.CommitMessageOperations.WriteMessageFile(file, msg);
            _shouldExitApp = true;

            Editor.CommitMessage = ViewModels.CommitMessageOperations.ReadMessageFile(file);
        }

        public void AsBuiltin(string conventionalTypesOverride, string msg, Action<string> onSave)
        {
            ConventionalTypesOverride = conventionalTypesOverride;

            _onSave = onSave;
            _shouldExitApp = false;

            Editor.CommitMessage = msg;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (_shouldExitApp)
                App.Quit(_exitCode);
        }

        private void SaveAndClose(object _1, RoutedEventArgs _2)
        {
            _onSave?.Invoke(Editor.CommitMessage);
            Close();
        }

        private void CancelAndClose(object _1, RoutedEventArgs _2)
        {
            _exitCode = -1;
            Close();
        }

        private Action<string> _onSave = null;
        private bool _shouldExitApp = true;
        private int _exitCode = 0;
    }
}
