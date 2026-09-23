using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Downloads a full playable client into versions/&lt;version&gt;/: the version
    /// JSON, client jar, Windows libraries and natives, the asset index and every
    /// asset object.
    /// </summary>
    public static class VersionInstaller
    {
        private const string ResourcesBase = "https://resources.download.minecraft.net";
        private const int AssetParallelism = 8;

        public static async Task InstallVanillaAsync(
            string mcVersion,
            string versionUrl,
            IProgress<string>? log,
            IProgress<double>? percent,
            CancellationToken ct)
        {
            string versionDir = Paths.VersionDir(mcVersion);
            string versionsSub = Path.Combine(versionDir, "versions");
            string libsDir     = Path.Combine(versionDir, "libraries");
            string nativesDir  = Path.Combine(versionDir, "natives");
            string assetsDir   = Path.Combine(versionDir, "assets");

            Directory.CreateDirectory(versionsSub);
            Directory.CreateDirectory(libsDir);
            Directory.CreateDirectory(nativesDir);

            log?.Report($"Fetching version manifest for {mcVersion}…");
            string versionJsonPath = Path.Combine(versionsSub, $"{mcVersion}.json");
            await Downloader.GetFileAsync(versionUrl, versionJsonPath, ct);
            percent?.Report(2);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(versionJsonPath, ct));
            var root = doc.RootElement;

            // ── Client jar ──
            if (root.TryGetProperty("downloads", out var downloads) &&
                downloads.TryGetProperty("client", out var client))
            {
                string url = client.GetProperty("url").GetString()!;
                string sha1 = client.TryGetProperty("sha1", out var s) ? s.GetString()! : "";
                string dest = Path.Combine(versionsSub, $"{mcVersion}-client.jar");

                log?.Report("Downloading client jar…");
                await Downloader.GetVerifiedAsync(url, dest, sha1, log, ct);
            }
            percent?.Report(10);

            // ── Libraries + Windows natives ──
            if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
            {
                var libArray = libs.EnumerateArray().ToList();
                int i = 0;

                foreach (var lib in libArray)
                {
                    ct.ThrowIfCancellationRequested();
                    i++;
                    percent?.Report(10 + 20d * i / Math.Max(1, libArray.Count));

                    if (!IsAllowedOnWindows(lib)) continue;

                    if (lib.TryGetProperty("downloads", out var ld))
                    {
                        if (ld.TryGetProperty("artifact", out var artifact))
                            await DownloadArtifactAsync(artifact, libsDir, log, ct);

                        string? nativeKey = WindowsNativeKey(lib);
                        if (nativeKey is not null &&
                            ld.TryGetProperty("classifiers", out var classifiers) &&
                            classifiers.TryGetProperty(nativeKey, out var nativeArtifact))
                        {
                            await DownloadArtifactAsync(nativeArtifact, libsDir, log, ct);
                        }
                    }
                }
                log?.Report($"Libraries complete ({libArray.Count} entries).");
            }
            percent?.Report(30);

            // ── Asset index + objects ──
            if (root.TryGetProperty("assetIndex", out var assetIndex))
            {
                string indexId = assetIndex.GetProperty("id").GetString()!;
                string indexUrl = assetIndex.GetProperty("url").GetString()!;
                string indexSha = assetIndex.TryGetProperty("sha1", out var s) ? s.GetString()! : "";
                string indexPath = Path.Combine(assetsDir, "indexes", $"{indexId}.json");

                log?.Report($"Downloading asset index {indexId}…");
                await Downloader.GetVerifiedAsync(indexUrl, indexPath, indexSha, log, ct);

                await DownloadAssetsAsync(indexPath, assetsDir, log, percent, ct);
            }

            percent?.Report(100);
            log?.Report($"{mcVersion} installed.");
        }

        private static async Task DownloadAssetsAsync(
            string indexPath, string assetsDir,
            IProgress<string>? log, IProgress<double>? percent, CancellationToken ct)
        {
            using var index = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath, ct));
            if (!index.RootElement.TryGetProperty("objects", out var objects)) return;

            var hashes = objects.EnumerateObject()
                .Select(p => p.Value.GetProperty("hash").GetString()!)
                .Distinct()
                .ToList();

            string objectsDir = Path.Combine(assetsDir, "objects");
            log?.Report($"Downloading {hashes.Count} assets…");

            int done = 0;
            using var gate = new SemaphoreSlim(AssetParallelism);

            var tasks = hashes.Select(async hash =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    string sub = hash[..2];
                    string dest = Path.Combine(objectsDir, sub, hash);

                    // The asset's name is its SHA-1, so verification costs nothing
                    // extra and lets a corrupted cached asset repair itself.
                    await Downloader.GetVerifiedAsync($"{ResourcesBase}/{sub}/{hash}", dest, hash, null, ct);

                    int n = Interlocked.Increment(ref done);
                    if (n % 100 == 0)
                    {
                        log?.Report($"Assets: {n} / {hashes.Count}");
                        percent?.Report(30 + 70d * n / hashes.Count);
                    }
                }
                finally { gate.Release(); }
            });

            await Task.WhenAll(tasks);
            log?.Report($"Assets complete ({hashes.Count}).");
        }

        private static async Task DownloadArtifactAsync(
            JsonElement artifact, string libsDir, IProgress<string>? log, CancellationToken ct)
        {
            if (!artifact.TryGetProperty("path", out var pathEl)) return;
            if (!artifact.TryGetProperty("url", out var urlEl)) return;

            string url = urlEl.GetString() ?? "";
            if (url.Length == 0) return;

            string relative = (pathEl.GetString() ?? "").Replace('/', Path.DirectorySeparatorChar);
            string sha1 = artifact.TryGetProperty("sha1", out var s) ? s.GetString() ?? "" : "";

            await Downloader.GetVerifiedAsync(url, Path.Combine(libsDir, relative), sha1, log, ct);
        }

        /// <summary>
        /// Evaluates a library's rules for Windows. Last matching rule wins, and a
        /// library with rules but no matching allow is excluded.
        /// </summary>
        private static bool IsAllowedOnWindows(JsonElement lib)
        {
            if (!lib.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
                return true;

            bool allowed = false;
            foreach (var rule in rules.EnumerateArray())
            {
                string action = rule.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";

                bool applies = true;
                if (rule.TryGetProperty("os", out var os) && os.TryGetProperty("name", out var osName))
                    applies = (osName.GetString() ?? "") == "windows";

                if (applies) allowed = action == "allow";
            }
            return allowed;
        }

        private static string? WindowsNativeKey(JsonElement lib)
        {
            if (!lib.TryGetProperty("natives", out var natives)) return null;
            if (!natives.TryGetProperty("windows", out var win)) return null;

            // ${arch} is 64 here; the launcher only ships and supports x64 runtimes.
            return (win.GetString() ?? "").Replace("${arch}", "64");
        }
    }
}
