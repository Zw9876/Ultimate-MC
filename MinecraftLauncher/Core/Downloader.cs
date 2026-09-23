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
                return false;
            }
            return true;
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
