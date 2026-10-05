namespace SourceGit.Contracts;

public static class OperationState
{
    public static bool IsActive(string state) => state is "accepted" or "running" or "waitingInteraction" or "terminating";
}
