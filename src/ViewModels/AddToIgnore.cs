using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class AddToIgnore : Popup
    {
        public List<Models.GitIgnoreFile> StorageFiles
        {
            get;
        }

        [Required(ErrorMessage = "Ignore pattern is required!")]
        public string Pattern
        {
            get => _pattern;
            set => SetProperty(ref _pattern, value, true);
        }

        [Required(ErrorMessage = "Storage file is required!!!")]
        public Models.GitIgnoreFile SelectedStorageFile
        {
            get => _selectedStorageFile;
            set
            {
                if (SetProperty(ref _selectedStorageFile, value, true))
                    Pattern = value.Pattern;
            }
        }

        public AddToIgnore(Repository repo, string pattern)
        {
            _repo = repo;
            _pattern = pattern;

            StorageFiles = Models.GitIgnoreFile.GetSupported(repo.FullPath, repo.GitDir, pattern);
            SelectedStorageFile = StorageFiles[0];
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = "Adding Ignored File(s) ...";

            var file = _selectedStorageFile.FullPath;
            if (!SourceGit.Remote.RepositoryFile.Exists(file))
            {
                await SourceGit.Remote.RepositoryFile.WriteAllLinesAsync(file!, [_pattern]);
            }
            else
            {
                var org = await SourceGit.Remote.RepositoryFile.ReadAllTextAsync(file);
                if (!org.EndsWith('\n'))
                    await SourceGit.Remote.RepositoryFile.AppendAllLinesAsync(file, ["", _pattern]);
                else
                    await SourceGit.Remote.RepositoryFile.AppendAllLinesAsync(file, [_pattern]);
            }

            _repo.MarkWorkingCopyDirtyManually();
            return true;
        }

        private readonly Repository _repo;
        private string _pattern;
        private Models.GitIgnoreFile _selectedStorageFile;
    }
}
