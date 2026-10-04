using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class CheckoutRemoteBranchAsWorktree : Popup
    {
        public Models.Branch RemoteBranch
        {
            get => _remoteBranch;
        }

        [CustomValidation(typeof(CheckoutRemoteBranchAsWorktree), nameof(ValidateWorktreePath))]
        public string WorktreePath
        {
            get => _worktreePath;
            set => SetProperty(ref _worktreePath, value, true);
        }

        [CustomValidation(typeof(CheckoutRemoteBranchAsWorktree), nameof(ValidateBranchName))]
        public string BranchName
        {
            get => _branchName;
            set => SetProperty(ref _branchName, value, true);
        }

        public bool Tracking
        {
            get => _tracking;
            set => SetProperty(ref _tracking, value);
        }

        public CheckoutRemoteBranchAsWorktree(Repository repo, Models.Branch remoteBranch)
        {
            _repo = repo;
            _remoteBranch = remoteBranch;
            _worktreePath = SourceGit.Remote.RepositoryPath.Combine(repo.GetRecommandedWorktreeDir(), remoteBranch.Name.Replace('/', '-').Replace('\\', '-'));
            _branchName = remoteBranch.Name;
            _tracking = true;
        }

        public static ValidationResult ValidateWorktreePath(string path, ValidationContext ctx)
        {
            if (ctx.ObjectInstance is not CheckoutRemoteBranchAsWorktree creator)
                return new ValidationResult("Missing runtime context to checkout branch as worktree!");

            if (string.IsNullOrEmpty(path))
                return new ValidationResult("Worktree path is required!");

            var fullPath = SourceGit.Remote.RepositoryPath.IsPathRooted(path) ? path : SourceGit.Remote.RepositoryPath.Combine(creator._repo.FullPath, path);
            var info = new SourceGit.Remote.RepositoryDirectoryInfo(fullPath);
            if (info.Exists)
            {
                var files = info.GetFiles();
                if (files.Length > 0)
                    return new ValidationResult("Given path is not empty!!!");

                var folders = info.GetDirectories();
                if (folders.Length > 0)
                    return new ValidationResult("Given path is not empty!!!");
            }

            return ValidationResult.Success;
        }

        public static ValidationResult ValidateBranchName(string name, ValidationContext ctx)
        {
            if (ctx.ObjectInstance is not CheckoutRemoteBranchAsWorktree creator)
                return new ValidationResult("Missing runtime context to checkout branch as worktree!");

            var test = !string.IsNullOrEmpty(name) ? name : SourceGit.Remote.RepositoryPath.GetFileName(creator._worktreePath.TrimEnd('/').TrimEnd('\\'));
            if (!Models.RefName.IsValidBranchName(test))
                return new ValidationResult($"{test} is not a valid branch name!");

            foreach (var b in creator._repo.Branches)
            {
                if (b.IsLocal && b.Name.Equals(test, StringComparison.Ordinal))
                    return new ValidationResult($"A branch with name '{test}' already exists!");
            }

            return ValidationResult.Success;
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = $"Checkout branch '{_remoteBranch.FriendlyName}' as worktree ...";

            var log = _repo.CreateLog("Add Worktree");
            Use(log);

            var succ = await new Commands.Worktree(_repo.FullPath)
                .Use(log)
                .AddWithRemoteBranchAsync(_worktreePath, _branchName, _remoteBranch.FriendlyName, _tracking);

            log.Complete();
            return succ;
        }

        private readonly Repository _repo;
        private readonly Models.Branch _remoteBranch;
        private string _worktreePath;
        private string _branchName;
        private bool _tracking;
    }
}
