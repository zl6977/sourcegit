using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public class DeleteSubmodule : RepositoryActionPopup
    {
        public string Submodule
        {
            get;
            private set;
        }

        public DeleteSubmodule(Repository repo, string submodule) : base(repo)
        {
            _repo = repo;
            Submodule = submodule;
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _repo.LockWatcher();
            ProgressDescription = "Deleting submodule ...";

            var log = _repo.CreateLog("Delete Submodule");
            Use(log);

            var succ = await new Commands.Submodule(_repo.FullPath)
                .Use(log)
                .DeleteAsync(Submodule);

            log.Complete();
            return succ;
        }

        private readonly Repository _repo = null;
    }
}
