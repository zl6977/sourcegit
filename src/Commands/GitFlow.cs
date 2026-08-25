using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class GitFlow : Command
    {
        public GitFlow(string repo)
        {
            WorkingDirectory = repo;
            Context = repo;
        }

        public async Task<bool> InitAsync(string production, string develop, string feature, string release, string hotfix, string tag)
        {
            if (Native.OS.GitFlowVersion == Models.GitFlowVersion.Next)
            {
                Args =
                [
                    "flow",
                    "init",
                    "--preset=classic",
                    $"--main={production}",
                    $"--develop={develop}",
                    $"--feature={feature}",
                    "--bugfix=bugfix/",
                    $"--release={release}",
                    $"--hotfix={hotfix}",
                    "--support=support/",
                ];

                if (!string.IsNullOrEmpty(tag))
                    Args.Add($"--tag={tag}");

                return await ExecAsync().ConfigureAwait(false);
            }

            var config = new Config(WorkingDirectory);
            await config.SetAsync("gitflow.branch.master", production).ConfigureAwait(false);
            await config.SetAsync("gitflow.branch.develop", develop).ConfigureAwait(false);
            await config.SetAsync("gitflow.prefix.feature", feature).ConfigureAwait(false);
            await config.SetAsync("gitflow.prefix.bugfix", "bugfix/").ConfigureAwait(false);
            await config.SetAsync("gitflow.prefix.release", release).ConfigureAwait(false);
            await config.SetAsync("gitflow.prefix.hotfix", hotfix).ConfigureAwait(false);
            await config.SetAsync("gitflow.prefix.support", "support/").ConfigureAwait(false);
            await config.SetAsync("gitflow.prefix.versiontag", tag, true).ConfigureAwait(false);

            Args = ["flow", "init", "-d"];
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> StartAsync(Models.GitFlowBranchType type, string name, Models.Branch based)
        {
            switch (type)
            {
                case Models.GitFlowBranchType.Feature:
                    Args = ["flow", "feature", "start", name];
                    break;
                case Models.GitFlowBranchType.Release:
                    Args = ["flow", "release", "start", name];
                    break;
                case Models.GitFlowBranchType.Hotfix:
                    Args = ["flow", "hotfix", "start", name];
                    break;
                default:
                    RaiseException("Bad git-flow branch type!!!");
                    return false;
            }

            if (based != null)
                Args.Add(based.Name);

            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> FinishAsync(Models.GitFlowBranchType type, string name, bool rebase, bool squash, bool keepBranch)
        {
            Args = ["flow"];

            switch (type)
            {
                case Models.GitFlowBranchType.Feature:
                    Args.Add("feature");
                    break;
                case Models.GitFlowBranchType.Release:
                    Args.Add("release");
                    break;
                case Models.GitFlowBranchType.Hotfix:
                    Args.Add("hotfix");
                    break;
                default:
                    RaiseException("Bad git-flow branch type!!!");
                    return false;
            }

            Args.Add("finish");
            if (rebase)
                Args.Add("--rebase");
            if (squash)
                Args.Add("--squash");
            if (keepBranch)
                Args.Add("--keep");
            Args.Add(name);

            return await ExecAsync().ConfigureAwait(false);
        }
    }
}
