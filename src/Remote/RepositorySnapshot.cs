namespace SourceGit.Remote
{
    public sealed record RepositorySnapshot(string InstanceId, string RepositoryId, long Generation);

    // Generations are ordered only inside one server repository session.
    public sealed class RepositorySnapshotTracker
    {
        private RepositorySnapshot _latest;

        public bool TryAccept(RepositorySnapshot snapshot, string currentInstance, string currentRepository)
        {
            if (snapshot == null || snapshot.InstanceId != currentInstance || snapshot.RepositoryId != currentRepository)
                return false;
            if (_latest != null && _latest.InstanceId == snapshot.InstanceId &&
                _latest.RepositoryId == snapshot.RepositoryId && snapshot.Generation < _latest.Generation)
                return false;
            _latest = snapshot;
            return true;
        }
    }
}
