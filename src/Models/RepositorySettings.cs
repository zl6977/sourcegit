using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Avalonia.Collections;

using SourceGit.Commands;

namespace SourceGit.Models
{
    public class RepositorySettings
    {
        public string DefaultRemote
        {
            get;
            set;
        } = string.Empty;

        public int PreferredMergeMode
        {
            get;
            set;
        } = 0;

        public string ConventionalTypesOverride
        {
            get;
            set;
        } = string.Empty;

        public bool EnableRecursiveWhenAutoUpdatingSubmodules
        {
            get;
            set;
        } = true;

        public bool AskBeforeAutoUpdatingSubmodules
        {
            get;
            set;
        } = false;

        public string PreferredOpenAIService
        {
            get;
            set;
        } = "---";

        public AvaloniaList<CommitTemplate> CommitTemplates
        {
            get;
            set;
        } = [];

        public AvaloniaList<string> CommitMessages
        {
            get;
            set;
        } = [];

        public AvaloniaList<CustomAction> CustomActions
        {
            get;
            set;
        } = [];

        public static RepositorySettings Get(string gitCommonDir, Commands.RepositoryController controller = null)
        {
            var fileName = "sourcegit.settings";
            var fullpath = GetSettingsFilePath(gitCommonDir, controller);
            if (_cache.TryGetValue(fullpath, out var setting))
                return setting;

            var exists = controller != null
                ? controller.GitCommonDirFileExists(fileName)
                : GitService.FileExists(Path.Combine(gitCommonDir, fileName));

            if (!exists)
            {
                setting = new();
            }
            else
            {
                try
                {
                    using var stream = controller != null
                        ? controller.OpenReadGitDirFile(fileName)
                        : (Stream)GitService.OpenRead(Path.Combine(gitCommonDir, fileName));
                    setting = JsonSerializer.Deserialize(stream, JsonCodeGen.Default.RepositorySettings);
                }
                catch
                {
                    setting = new();
                }
            }

            // Serialize setting again to make sure there are no unnecessary whitespaces.
            Task.Run(() =>
            {
                var formatted = JsonSerializer.Serialize(setting, JsonCodeGen.Default.RepositorySettings);
                setting._orgHash = HashContent(formatted);
            });

            setting._file = fullpath;
            setting._controller = controller;
            _cache.Add(fullpath, setting);
            return setting;
        }

        public void Save()
        {
            try
            {
                var content = JsonSerializer.Serialize(this, JsonCodeGen.Default.RepositorySettings);
                var hash = HashContent(content);
                if (!hash.Equals(_orgHash, StringComparison.Ordinal))
                {
                    if (_controller != null)
                    {
                        _controller.WriteGitCommonDirFileAndReplace(Path.GetFileName(_file), content);
                    }
                    else
                    {
                        GitService.WriteFileAndReplace(_file, content);
                    }
                    _orgHash = hash;
                }
            }
            catch
            {
                // Ignore save errors
            }
        }

        private static string GetSettingsFilePath(string gitCommonDir, Commands.RepositoryController controller)
        {
            var fileName = "sourcegit.settings";
            // For WSL paths, use a hash-based key for the cache since WSL paths don't map to real Windows paths
            if (controller != null)
                return $"{gitCommonDir}/{fileName}";
            return Path.Combine(gitCommonDir, fileName);
        }

        public void PushCommitMessage(string message)
        {
            message = message.Trim().ReplaceLineEndings("\n");
            var existIdx = CommitMessages.IndexOf(message);
            if (existIdx == 0)
                return;

            if (existIdx > 0)
            {
                CommitMessages.Move(existIdx, 0);
                return;
            }

            if (CommitMessages.Count > 9)
                CommitMessages.RemoveRange(9, CommitMessages.Count - 9);

            CommitMessages.Insert(0, message);
        }

        public CustomAction AddNewCustomAction()
        {
            var act = new CustomAction() { Name = "Unnamed Action" };
            CustomActions.Add(act);
            return act;
        }

        public void RemoveCustomAction(CustomAction act)
        {
            if (act != null)
                CustomActions.Remove(act);
        }

        public void MoveCustomActionUp(CustomAction act)
        {
            var idx = CustomActions.IndexOf(act);
            if (idx > 0)
                CustomActions.Move(idx - 1, idx);
        }

        public void MoveCustomActionDown(CustomAction act)
        {
            var idx = CustomActions.IndexOf(act);
            if (idx < CustomActions.Count - 1)
                CustomActions.Move(idx + 1, idx);
        }

        private static string HashContent(string source)
        {
            var hash = MD5.HashData(Encoding.Default.GetBytes(source));
            return Convert.ToHexStringLower(hash);
        }

        private static Dictionary<string, RepositorySettings> _cache = new();
        private string _file = string.Empty;
        private string _orgHash = string.Empty;
        private Commands.RepositoryController _controller = null;
    }
}
