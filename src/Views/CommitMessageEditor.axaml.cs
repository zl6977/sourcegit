using System;
using System.IO;
using System.Text.Json;

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
            var gitDir = new Commands.QueryGitDir(SourceGit.Remote.RepositoryPath.GetDirectoryName(file)).GetResult();
            if (!string.IsNullOrEmpty(gitDir))
            {
                var settingsFile = SourceGit.Remote.RepositoryPath.Combine(gitDir, "sourcegit.settings");
                if (SourceGit.Remote.RepositoryFile.Exists(settingsFile))
                {
                    try
                    {
                        using var stream = SourceGit.Remote.RepositoryFile.OpenRead(settingsFile);
                        var settings = JsonSerializer.Deserialize(stream, JsonCodeGen.Default.RepositorySettings);
                        ConventionalTypesOverride = settings.ConventionalTypesOverride;
                    }
                    catch
                    {
                        // Ignore errors
                    }
                }
            }

            _onSave = msg => SourceGit.Remote.RepositoryFile.WriteAllText(file, msg);
            _shouldExitApp = true;

            Editor.CommitMessage = SourceGit.Remote.RepositoryFile.ReadAllText(file).ReplaceLineEndings("\n").Trim();
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
