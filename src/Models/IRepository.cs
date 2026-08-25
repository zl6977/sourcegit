namespace SourceGit.Models
{
    public interface IRepository
    {
        bool MayHaveSubmodules();

        bool IsActive();

        void RefreshBranches();
        void RefreshWorktrees();
        void RefreshTags();
        void RefreshCommits();
        void RefreshSubmodules();
        void RefreshWorkingCopyChanges();

        void RefreshStashes();
    }
}
