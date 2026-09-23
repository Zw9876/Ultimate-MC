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
    /// Fabric loader metadata and installation. Loader and installer versions are
    /// always queried live — the original launcher pinned them to constants that
    /// went stale.
    /// </summary>
    public static class FabricMeta
    {
        private const string Base = "https://meta.fabricmc.net/v2/versions";
        private const string DefaultLibraryRepo = "https://maven.fabricmc.net";

        public static async Task<List<string>> LoaderVersionsAsync(string mcVersion, CancellationToken ct)
        {
            string json = await Downloader.GetStringAsync($"{Base}/loader/{mcVersion}", ct);
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("loader").GetProperty("version").GetString() ?? "")
                .Where(v => v.Length > 0)
                .ToList();
        }

        public static async Task<string> LatestLoaderAsync(string mcVersion, CancellationToken ct)
        {
            var versions = await LoaderVersionsAsync(mcVersion, ct);
            if (versions.Count == 0)
                throw new InvalidOperationException($"Fabric has no loader builds for Minecraft {mcVersion}.");
            return versions[0];   // the API returns newest first
        }

        public static async Task<string> LatestInstallerAsync(CancellationToken ct)
        {
            string json = await Downloader.GetStringAsync($"{Base}/installer", ct);
            using var doc = JsonDocument.Parse(json);

            var entries = doc.RootElement.EnumerateArray().ToList();
            var stable = entries.FirstOrDefault(e =>
                e.TryGetProperty("stable", out var s) && s.ValueKind == JsonValueKind.True);

            var chosen = stable.ValueKind == JsonValueKind.Object ? stable : entries.FirstOrDefault();
            if (chosen.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Fabric installer metadata was empty.");

            return chosen.GetProperty("version").GetString()!;
        }

        public static string ServerJarUrl(string mcVersion, string loaderVersion, string installerVersion) =>
            $"{Base}/loader/{mcVersion}/{loaderVersion}/{installerVersion}/server/jar";

        /// <summary>
        /// Installs the Fabric profile JSON and its libraries alongside an existing
        /// vanilla install of the same Minecraft version.
        /// </summary>
        public static async Task InstallAsync(
            string mcVersion, string loaderVersion,
            IProgress<string>? log, CancellationToken ct)
        {
            string versionsSub = Path.Combine(Paths.VersionDir(mcVersion), "versions");
            string libsDir = Path.Combine(Paths.VersionDir(mcVersion), "libraries");
            Directory.CreateDirectory(versionsSub);
            Directory.CreateDirectory(libsDir);

            log?.Report($"Downloading Fabric loader {loaderVersion} profile…");
            string profilePath = Path.Combine(versionsSub, $"fabric-loader-{loaderVersion}-{mcVersion}.json");
            await Downloader.GetFileAsync($"{Base}/loader/{mcVersion}/{loaderVersion}/profile/json", profilePath, ct);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(profilePath, ct));
            if (!doc.RootElement.TryGetProperty("libraries", out var libs) ||
                libs.ValueKind != JsonValueKind.Array)
            {
                log?.Report("Fabric profile listed no libraries.");
                return;
            }

            foreach (var lib in libs.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();

                string name = lib.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Length == 0) continue;

                string relative = MavenPath(name);
                string dest = Path.Combine(libsDir, relative.Replace('/', Path.DirectorySeparatorChar));

                if (lib.TryGetProperty("downloads", out var d) &&
                    d.TryGetProperty("artifact", out var artifact) &&
                    artifact.TryGetProperty("url", out var artUrl))
                {
                    string sha1 = artifact.TryGetProperty("sha1", out var s) ? s.GetString() ?? "" : "";
                    await Downloader.GetVerifiedAsync(artUrl.GetString()!, dest, sha1, log, ct);
                }
                else
                {
                    string repo = lib.TryGetProperty("url", out var u)
                        ? (u.GetString() ?? DefaultLibraryRepo).TrimEnd('/')
                        : DefaultLibraryRepo;

                    await Downloader.GetVerifiedAsync($"{repo}/{relative}", dest, null, log, ct);
                }
            }

            log?.Report($"Fabric loader {loaderVersion} installed.");
        }

        /// <summary>group:artifact:version[:classifier] → group/path/artifact/version/artifact-version[-classifier].jar</summary>
        public static string MavenPath(string coordinate)
        {
            var parts = coordinate.Split(':');
            if (parts.Length < 3)
                throw new FormatException($"Not a Maven coordinate: {coordinate}");

            string group = parts[0].Replace('.', '/');
            string artifact = parts[1];
            string version = parts[2];
            string? classifier = parts.Length > 3 ? parts[3] : null;

            string jar = classifier is null
                ? $"{artifact}-{version}.jar"
                : $"{artifact}-{version}-{classifier}.jar";

            return $"{group}/{artifact}/{version}/{jar}";
        }
    }
}
