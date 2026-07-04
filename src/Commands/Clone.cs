namespace SourceGit.Commands
{
    public class Clone : Command
    {
        public Clone(string ctx, string path, string url, string localName, string sshKey, string extraArgs)
        {
            Context = ctx;
            WorkingDirectory = path;
            SSHKey = sshKey;

            Args = ["clone", "--progress", "--verbose"];
            if (!string.IsNullOrWhiteSpace(extraArgs))
                Args.Add(extraArgs);
            Args.Add(url);
            if (!string.IsNullOrEmpty(localName))
                Args.Add(localName);
        }
    }
}
