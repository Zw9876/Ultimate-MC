using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Shared HTTP access and SHA-1 verified file downloads.
    /// </summary>
    public static class Downloader
    {
        public static HttpClient Client { get; } = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MinecraftPortableLauncher/1.0");
            return client;
        }

        public static Task<string> GetStringAsync(string url, CancellationToken ct) =>
            Client.GetStringAsync(url, ct);

        public static async Task GetFileAsync(string url, string outFile, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

            string temp = outFile + ".part";
            using (var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(temp);
                await src.CopyToAsync(dst, ct);
            }

            File.Move(temp, outFile, overwrite: true);
        }

        /// <summary>
        /// Downloads to <paramref name="outFile"/> unless a file with the expected
        /// hash is already there. A file whose hash does not match is re-downloaded.
        /// Returns false if the result still fails verification.
        /// </summary>
        public static async Task<bool> GetVerifiedAsync(
            string url, string outFile, string? sha1, IProgress<string>? log, CancellationToken ct)
        {
            if (File.Exists(outFile))
            {
                if (sha1 is null) return true;                       // nothing to check against
                if (HashMatches(outFile, sha1)) return true;

                log?.Report($"[REDOWNLOAD] {Path.GetFileName(outFile)} (hash mismatch)");
                try { File.Delete(outFile); } catch { }
            }

            await GetFileAsync(url, outFile, ct);

            if (sha1 is not null && !HashMatches(outFile, sha1))
            {
                log?.Report($"[ERROR] {Path.GetFileName(outFile)} failed SHA-1 verification");

                // Deleted, not left behind. A file that failed its hash used to stay
                // on disk under the right name, so anything that only checked it
                // existed - the game, a later launch - treated a filter's web page
                // as a library jar.
                try { File.Delete(outFile); } catch { }
                return false;
            }
            return true;
        }

        /// <summary>
        /// As <see cref="GetVerifiedAsync"/>, but a failure is an exception instead of
        /// a <c>false</c> nobody reads.
        /// </summary>
        /// <remarks>
        /// The installers called the bool-returning version at seven places and
        /// ignored the result, so an install whose every file had been replaced by a
        /// content filter's page reported "Done." and failed later somewhere
        /// unrelated. Those callers use this instead.
        ///
        /// A filter's page is recognised even where there is no hash to check against
        /// - FabricMeta downloads some libraries that way - because a jar never starts
        /// like a web page.
        /// </remarks>
        public static async Task GetVerifiedOrThrowAsync(
            string url, string outFile, string? sha1, IProgress<string>? log, CancellationToken ct)
        {
            if (File.Exists(outFile) && sha1 is not null && HashMatches(outFile, sha1))
                return;

            if (File.Exists(outFile))
            {
                if (sha1 is not null)
                    log?.Report($"[REDOWNLOAD] {Path.GetFileName(outFile)} (hash mismatch)");
                try { File.Delete(outFile); } catch { }
            }

            await GetFileAsync(url, outFile, ct);

            if (NetworkBlock.FileIsFilterPage(outFile, out string snippet))
            {
                try { File.Delete(outFile); } catch { }
                throw new NetworkBlockedException(url, snippet.Trim());
            }

            if (sha1 is not null && !HashMatches(outFile, sha1))
            {
                try { File.Delete(outFile); } catch { }
                throw new IOException(
                    $"{Path.GetFileName(outFile)} from {NetworkBlock.HostOf(url)} failed its " +
                    "SHA-1 check, so what arrived is not the file that was published. It has " +
                    "been deleted rather than kept.");
            }
        }

        public static bool HashMatches(string file, string expectedSha1)
        {
            try
            {
                return string.Equals(Sha1(file), expectedSha1, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string Sha1(string file)
        {
            using var stream = File.OpenRead(file);
            return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
        }
    }
}
