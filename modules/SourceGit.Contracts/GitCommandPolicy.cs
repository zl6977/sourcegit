namespace SourceGit.Contracts;

public static class GitCommandPolicy
{
    private static readonly HashSet<string> Reads = new(StringComparer.Ordinal) { "status", "log", "show", "diff", "diff-files", "diff-index", "ls-files", "ls-tree", "rev-parse", "rev-list", "for-each-ref", "show-ref", "describe", "blame", "cat-file", "check-ignore", "check-attr", "merge-base", "count-objects", "name-rev", "version", "ls-remote" };
    private static readonly HashSet<string> Writes = new(StringComparer.Ordinal) { "init", "clone", "add", "restore", "reset", "commit", "checkout", "switch", "branch", "tag", "fetch", "pull", "push", "merge", "rebase", "cherry-pick", "revert", "stash", "clean", "rm", "mv", "apply", "am", "format-patch", "archive", "submodule", "worktree", "bisect", "lfs", "flow", "remote", "config", "update-index", "update-ref", "notes", "repack", "gc" };
    public static void Validate(string[] args, bool readOnly)
    {
        if (args.Length == 0 || args.Any(x => x.Contains('\0'))) throw new ArgumentException("Invalid Git arguments");
        var verb = args[0];
        if (!Reads.Contains(verb) && !Writes.Contains(verb) && verb != "reflog") throw new UnauthorizedAccessException("Unsupported Git operation: " + verb);
        if (readOnly && !Reads.Contains(verb) && !IsQuery(args)) throw new UnauthorizedAccessException("Write operation must have an operation ID");
        foreach (var value in args.Skip(1))
            if (value is "--exec" or "-x" or "--ext-diff" or "--textconv" or "--upload-pack" or "--receive-pack" or "--config-env" || value.StartsWith("--exec=", StringComparison.Ordinal) || value.StartsWith("--upload-pack=", StringComparison.Ordinal) || value.StartsWith("--receive-pack=", StringComparison.Ordinal) || value.StartsWith("--config-env=", StringComparison.Ordinal) || value.StartsWith("--config=", StringComparison.Ordinal) || value == "--config" && verb != "config" || value == "-c" && verb is not ("commit" or "config"))
                throw new UnauthorizedAccessException("Executable override is not allowed");
        if (verb is "clone" or "fetch" or "pull" && args.Any(x => x == "-u" || x.StartsWith("-u", StringComparison.Ordinal) && x.Length > 2 || x.StartsWith("--upload-", StringComparison.Ordinal))) throw new UnauthorizedAccessException("Upload-pack overrides require a custom command");
        if (verb == "push" && args.Any(x => x.StartsWith("--receive-", StringComparison.Ordinal))) throw new UnauthorizedAccessException("Receive-pack overrides require a custom command");
        if (readOnly && args.Any(x => x == "--output" || x.StartsWith("--output="))) throw new UnauthorizedAccessException("Output file requires a write operation");
        if (verb == "config")
        {
            if (args.Any(x => x is "--global" or "--system" or "--file" or "-f" || x.StartsWith("--file="))) throw new UnauthorizedAccessException("Only repository config is supported");
            if (!IsQuery(args) && args.Any(IsExecutableConfiguration)) throw new UnauthorizedAccessException("Executable config changes require custom-command capability");
        }
        if (verb == "submodule" && args.Contains("foreach")) throw new UnauthorizedAccessException("Submodule foreach is a custom command");
    }
    private static bool IsExecutableConfiguration(string value)
    {
        var key = value.ToLowerInvariant();
        return key.StartsWith("alias.", StringComparison.Ordinal) || key is "core.editor" or "core.sshcommand" or "core.hookspath" or "core.pager" or "core.gitproxy" or "core.fsmonitor" or "sequence.editor" or "credential.helper" or "diff.external" or "gpg.program" or "include.path" ||
            key.StartsWith("includeif.", StringComparison.Ordinal) && key.EndsWith(".path", StringComparison.Ordinal) ||
            key.StartsWith("credential.", StringComparison.Ordinal) && key.EndsWith(".helper", StringComparison.Ordinal) ||
            key.StartsWith("filter.", StringComparison.Ordinal) && (key.EndsWith(".process", StringComparison.Ordinal) || key.EndsWith(".clean", StringComparison.Ordinal) || key.EndsWith(".smudge", StringComparison.Ordinal)) ||
            key.StartsWith("diff.", StringComparison.Ordinal) && (key.EndsWith(".command", StringComparison.Ordinal) || key.EndsWith(".textconv", StringComparison.Ordinal)) ||
            key.StartsWith("merge.", StringComparison.Ordinal) && key.EndsWith(".driver", StringComparison.Ordinal) ||
            key.StartsWith("gpg.", StringComparison.Ordinal) && key.EndsWith(".program", StringComparison.Ordinal) ||
            key.StartsWith("remote.", StringComparison.Ordinal) && (key.EndsWith(".uploadpack", StringComparison.Ordinal) || key.EndsWith(".receivepack", StringComparison.Ordinal) || key.EndsWith(".proxy", StringComparison.Ordinal));
    }
    public static bool IsQuery(string[] args)
    {
        if (args.Length == 0) return false;
        if (Reads.Contains(args[0])) return !args.Any(x => x == "--output" || x.StartsWith("--output="));
        return args[0] switch
        {
            "reflog" => args.Length == 1 || args[1] is "show" or "exists" || args[1].StartsWith("-"),
            "config" => args.Any(x => x is "--get" or "--get-all" or "--get-regexp" or "--list" or "-l") && !args.Any(x => x is "--add" or "--replace-all" or "--unset" or "--unset-all" or "--remove-section" or "--rename-section" || x.StartsWith("--add=")),
            "branch" => (args.Length == 1 || args.Any(x => x is "--list" or "--show-current" or "-l" or "--all" or "-a" or "--remotes" or "-r")) && !args.Any(x => x is "-D" or "-d" or "--delete" or "-m" or "-M" or "--move" or "-c" or "-C" or "--copy" or "--set-upstream-to" or "--unset-upstream" || x.StartsWith("--set-upstream-to=")),
            "remote" => args.Length == 1 || args[1] is "-v" or "get-url",
            "worktree" => args.Length > 1 && args[1] == "list",
            "stash" => args.Length > 1 && args[1] is "list" or "show",
            "submodule" => args.Length == 1 || args[1] is "status" or "summary",
            "tag" => args.Any(x => x is "--list" or "-l") && !args.Any(x => x is "-d" or "--delete" or "-a" or "-s" or "-f"),
            _ => false
        };
    }
}
