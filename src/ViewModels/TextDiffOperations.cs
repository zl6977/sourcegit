using System.IO;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public static class TextDiffOperations
    {
        public enum ChunkAction
        {
            Stage,
            Unstage,
            Discard,
        }

        public static async Task<bool> ApplyChunkAsync(Repository repo, TextDiffContext context, ChunkAction action)
        {
            if (repo == null || context is not { SelectedChunk: { } chunk, Data: { } diff, Option: { } option })
                return false;

            if (!CanApply(option, action))
                return false;

            var reverse = action is ChunkAction.Unstage or ChunkAction.Discard;
            var patchText = GeneratePatchText(option, diff, chunk.StartIdx, chunk.EndIdx, chunk.Combined, chunk.IsOldSide, reverse);
            if (string.IsNullOrEmpty(patchText))
                return false;

            using var lockWatcher = repo.LockWatcher();
            using var patchFile = await Commands.BackendTempFile.CreateAsync(repo.FullPath, patchText, "sourcegit_chunk_patch");

            var args = action switch
            {
                ChunkAction.Stage => new System.Collections.Generic.List<string> { "--cache", "--index" },
                ChunkAction.Unstage => new System.Collections.Generic.List<string> { "--cache", "--index", "--reverse" },
                _ => new System.Collections.Generic.List<string> { "--reverse" },
            };

            var succeeded = await new Commands.Apply(repo.FullPath, patchFile.File, true, "nowarn", args).ExecAsync();
            if (!succeeded)
                return false;

            context.BlockNavigation.UpdateByChunk(chunk);
            repo.MarkWorkingCopyDirtyManually();
            return true;
        }

        public static string GeneratePatchText(Models.DiffOption option, Models.TextDiff diff, int startIdx, int endIdx, bool combined, bool isOldSide, bool reverse)
        {
            var patch = new Models.PatchGenerator(option, diff, startIdx, endIdx, combined, isOldSide);
            if (!patch.IsValid)
                return string.Empty;

            var tmpFile = Path.GetTempFileName();
            try
            {
                patch.Generate(tmpFile, reverse);
                return File.ReadAllText(tmpFile);
            }
            finally
            {
                File.Delete(tmpFile);
            }
        }

        private static bool CanApply(Models.DiffOption option, ChunkAction action)
        {
            if (option == null || !option.IsLocalChange)
                return false;

            return action switch
            {
                ChunkAction.Stage => option.IsUnstaged,
                ChunkAction.Unstage => !option.IsUnstaged,
                ChunkAction.Discard => option.IsUnstaged,
                _ => false,
            };
        }
    }
}
