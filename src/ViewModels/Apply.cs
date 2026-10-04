using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading.Tasks;

using Avalonia.Input.Platform;

namespace SourceGit.ViewModels
{
    public class Apply : Popup
    {
        [CustomValidation(typeof(Apply), nameof(ValidatePatchFile))]
        public string PatchFile
        {
            get => _patchFile;
            set => SetProperty(ref _patchFile, value, true);
        }

        public bool IgnoreWhiteSpace
        {
            get => _ignoreWhiteSpace;
            set => SetProperty(ref _ignoreWhiteSpace, value);
        }

        public Models.ApplyWhiteSpaceMode SelectedWhiteSpaceMode
        {
            get;
            set;
        }

        public bool FromClipboard
        {
            get => _fromClipboard;
            set => SetProperty(ref _fromClipboard, value);
        }

        public bool ThreeWayMerge
        {
            get;
            set;
        }

        public IClipboard Clipboard
        {
            get;
            set;
        } = null;

        public Apply(Repository repo)
        {
            _repo = repo;
            SelectedWhiteSpaceMode = Models.ApplyWhiteSpaceMode.Supported[0];
        }

        public static ValidationResult ValidatePatchFile(string file, ValidationContext ctx)
        {
            if (ctx.ObjectInstance is not Apply apply)
                return new ValidationResult("Invalid object instance!!!");

            if (apply.FromClipboard)
                return ValidationResult.Success;

            if (string.IsNullOrEmpty(file))
                return new ValidationResult("Please select a patch file!!!");

            if (!SourceGit.Remote.RepositoryFile.Exists(file))
                return new ValidationResult($"File '{file}' can NOT be found!!!");

            return ValidationResult.Success;
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = "Apply patch...";

            var finalPatchFile = _patchFile;
            var temporaryPatch = false;
            if (_fromClipboard)
            {
                if (Clipboard == null)
                {
                    _repo.SendNotification("Clipboard service is not available!!!", true);
                    return false;
                }

                var content = await Clipboard.TryGetTextAsync();
                if (string.IsNullOrEmpty(content) || !content.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    _repo.SendNotification("There's no valid patch content in clipboard!!!", true);
                    return false;
                }

                finalPatchFile = SourceGit.Remote.RepositoryFile.GetTempFileName(_repo.FullPath);
                SourceGit.Remote.RepositoryFile.WriteAllText(finalPatchFile, content);
                temporaryPatch = true;
            }
            else if (SourceGit.Remote.RepositoryPath.IsRemote(_repo.FullPath) && !SourceGit.Remote.RepositoryPath.IsRemote(finalPatchFile))
            {
                var uploaded = SourceGit.Remote.RepositoryFile.GetTempFileName(_repo.FullPath);
                await Task.Run(() => SourceGit.Remote.RepositoryFile.Copy(finalPatchFile, uploaded, true));
                finalPatchFile = uploaded;
                temporaryPatch = true;
            }

            var log = _repo.CreateLog("Apply Patch");
            Use(log);

            var extra = ThreeWayMerge ? "--3way" : string.Empty;
            var succ = await new Commands.Apply(_repo.FullPath, finalPatchFile, _ignoreWhiteSpace, SelectedWhiteSpaceMode.Arg, extra)
                .Use(log)
                .ExecAsync();

            log.Complete();

            if (temporaryPatch)
                SourceGit.Remote.RepositoryFile.Delete(finalPatchFile);

            return succ;
        }

        private readonly Repository _repo = null;
        private string _patchFile = string.Empty;
        private bool _fromClipboard = false;
        private bool _ignoreWhiteSpace = true;
    }
}
