using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SourceGit.Models
{
    public enum InteractiveRebaseAction
    {
        Pick,
        Edit,
        Reword,
        Squash,
        Fixup,
        Drop,
    }

    public enum InteractiveRebasePendingType
    {
        None = 0,
        Target,
        Pending,
        Ignore,
        Last,
    }

    public class InteractiveCommit
    {
        public Commit Commit { get; set; } = new Commit();
        public string Message { get; set; } = string.Empty;
    }

    public class InteractiveRebaseJob
    {
        public string SHA { get; set; } = string.Empty;
        public InteractiveRebaseAction Action { get; set; } = InteractiveRebaseAction.Pick;
        public string Message { get; set; } = string.Empty;
    }

    public partial class InteractiveRebaseJobCollection
    {
        public string OrigHead { get; set; } = string.Empty;
        public string Onto { get; set; } = string.Empty;
        public List<InteractiveRebaseJob> Jobs { get; set; } = new List<InteractiveRebaseJob>();

        public string BuildTodoList()
        {
            var lines = new List<string>(Jobs.Count);
            foreach (var job in Jobs)
            {
                var code = job.Action switch
                {
                    InteractiveRebaseAction.Pick => 'p',
                    InteractiveRebaseAction.Edit => 'e',
                    InteractiveRebaseAction.Reword => 'r',
                    InteractiveRebaseAction.Squash => 's',
                    InteractiveRebaseAction.Fixup => 'f',
                    _ => 'd'
                };
                lines.Add($"{code} {job.SHA}");
            }

            return string.Join("\n", lines) + "\n";
        }

        public string GetCommitMessage(string doneContent)
        {
            var done = doneContent.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (done.Length == 0)
                return string.Empty;

            var current = done[^1].Trim();
            var match = REG_REBASE_TODO().Match(current);
            if (!match.Success)
                return string.Empty;

            var sha = match.Groups[1].Value;
            foreach (var job in Jobs)
            {
                if (job.SHA.StartsWith(sha))
                {
                    return job.Message;
                }
            }

            return string.Empty;
        }

        [GeneratedRegex(@"^[a-z]+\s+([a-fA-F0-9]{4,64})(\s+.*)?$")]
        private static partial Regex REG_REBASE_TODO();
    }
}
