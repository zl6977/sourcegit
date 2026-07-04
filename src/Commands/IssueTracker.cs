using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class IssueTracker : Command
    {
        public IssueTracker(string repo, bool isShared)
        {
            WorkingDirectory = repo;
            Context = repo;

            if (isShared)
            {
                var storage = $"{repo}/.issuetracker";
                _isStorageFileExists = File.Exists(storage);
                _baseArgs = ["config", "-f", storage];
            }
            else
            {
                _isStorageFileExists = true;
                _baseArgs = ["config", "--local"];
            }
        }

        public async Task ReadAllAsync(List<Models.IssueTracker> outs, bool isShared)
        {
            if (!_isStorageFileExists)
                return;

            Args = NewArgs("-l");

            var rs = await ReadToEndAsync().ConfigureAwait(false);
            if (rs.IsSuccess)
            {
                var lines = rs.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length < 2)
                        continue;

                    var key = parts[0];
                    var value = parts[1];

                    if (!key.StartsWith("issuetracker.", StringComparison.Ordinal))
                        continue;

                    if (key.EndsWith(".regex", StringComparison.Ordinal))
                    {
                        var prefixLen = "issuetracker.".Length;
                        var suffixLen = ".regex".Length;
                        var ruleName = key.Substring(prefixLen, key.Length - prefixLen - suffixLen);
                        FindOrAdd(outs, ruleName, isShared).RegexString = value;
                    }
                    else if (key.EndsWith(".url", StringComparison.Ordinal))
                    {
                        var prefixLen = "issuetracker.".Length;
                        var suffixLen = ".url".Length;
                        var ruleName = key.Substring(prefixLen, key.Length - prefixLen - suffixLen);
                        FindOrAdd(outs, ruleName, isShared).URLTemplate = value;
                    }
                }
            }
        }

        public async Task<bool> AddAsync(Models.IssueTracker rule)
        {
            Args = NewArgs($"issuetracker.{rule.Name}.regex", rule.RegexString);

            var succ = await ExecAsync().ConfigureAwait(false);
            if (succ)
            {
                Args = NewArgs($"issuetracker.{rule.Name}.url", rule.URLTemplate);
                return await ExecAsync().ConfigureAwait(false);
            }

            return false;
        }

        public async Task<bool> UpdateRegexAsync(Models.IssueTracker rule)
        {
            Args = NewArgs($"issuetracker.{rule.Name}.regex", rule.RegexString);
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> UpdateURLTemplateAsync(Models.IssueTracker rule)
        {
            Args = NewArgs($"issuetracker.{rule.Name}.url", rule.URLTemplate);
            return await ExecAsync().ConfigureAwait(false);
        }

        public async Task<bool> RemoveAsync(string name)
        {
            if (!_isStorageFileExists)
                return true;

            Args = NewArgs("--remove-section", $"issuetracker.{name}");
            return await ExecAsync().ConfigureAwait(false);
        }

        private List<string> NewArgs(params string[] args)
        {
            var rs = new List<string>(_baseArgs);
            rs.AddRange(args);
            return rs;
        }

        private Models.IssueTracker FindOrAdd(List<Models.IssueTracker> rules, string ruleName, bool isShared)
        {
            var rule = rules.Find(x => x.Name.Equals(ruleName, StringComparison.Ordinal));
            if (rule != null)
                return rule;

            rule = new Models.IssueTracker() { IsShared = isShared, Name = ruleName };
            rules.Add(rule);
            return rule;
        }

        private readonly bool _isStorageFileExists;
        private readonly List<string> _baseArgs;
    }
}
