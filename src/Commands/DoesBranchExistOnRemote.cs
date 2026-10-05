using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class DoesBranchExistOnRemote : Command
    {
        public DoesBranchExistOnRemote(string repo, Models.Remote remote, Models.Branch branch)
        {
            WorkingDirectory = repo;
            Context = repo;
            SSHKey = remote.PrivateSSHKey;
            Args = $"ls-remote {remote.Name} {branch.Name}";
        }

        public async Task<bool?> GetResultAsync()
        {
            try
            {
                var rs = await ReadToEndAsync();
                if (rs.IsSuccess) return rs.StdOut.Trim().Length > 0;
                Log?.AppendLine(rs.StdErr);
                if (RaiseError) RaiseException(rs.StdErr);
            }
            catch (System.Exception error)
            {
                Log?.AppendLine(error.Message);
                if (RaiseError) RaiseException(error.Message);
            }
            return null;
        }
    }
}
