using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Obtains a runnable server.jar for a loader/version pair. Loader build
    /// numbers are resolved live rather than pinned.
    /// </summary>
    public static class ServerJarInstaller
    {
        public static async Task EnsureAsync(
            string version, string loaderType, string serverDir,
            IProgress<string>? log, CancellationToken ct)
        {
            Directory.CreateDirectory(serverDir);

            // Forge and NeoForge may install an argument file instead of a jar, so
            // "already installed" cannot just mean server.jar exists.
            if (ServerStartPlanner.IsInstalled(serverDir))
            {
                log?.Report("Server already installed.");
                return;
            }

            string serverJar = Path.Combine(serverDir, "server.jar");
            log?.Report($"Installing {loaderType} server for {version}…");

            switch (loaderType.ToUpperInvariant())
            {
                case "VANILLA":  await InstallVanillaAsync(version, serverJar, log, ct); break;
                case "FABRIC":   await InstallFabricAsync(version, serverJar, log, ct); break;
                case "PAPER":    await InstallPaperAsync(version, serverJar, log, ct); break;
                case "PURPUR":   await InstallPurpurAsync(version, serverJar, log, ct); break;
                case "FORGE":    await InstallForgeFamilyAsync(ForgeFlavor.Forge, version, serverDir, log, ct); break;
                case "NEOFORGE": await InstallForgeFamilyAsync(ForgeFlavor.NeoForge, version, serverDir, log, ct); break;
                default:
                    throw new NotSupportedException($"Unknown server type: {loaderType}");
            }

            if (!ServerStartPlanner.IsInstalled(serverDir))
            {
                string listing = string.Join(", ",
                    Directory.GetFiles(serverDir).Select(Path.GetFileName));
                throw new FileNotFoundException(
                    $"The install finished but produced nothing startable in {serverDir}.\nFiles: {listing}");
            }

            log?.Report("Server ready.");
        }

        private static async Task InstallVanillaAsync(
            string version, string serverJar, IProgress<string>? log, CancellationToken ct)
        {
            var entry = await MojangManifest.FindAsync(version, ct)
                ?? throw new InvalidOperationException($"Minecraft {version} is not in Mojang's manifest.");

            using var doc = JsonDocument.Parse(await Downloader.GetStringAsync(entry.Url, ct));

            if (!doc.RootElement.TryGetProperty("downloads", out var downloads) ||
                !downloads.TryGetProperty("server", out var server))
                throw new InvalidOperationException($"Mojang publishes no server jar for {version}.");

            string url = server.GetProperty("url").GetString()!;
            string sha1 = server.TryGetProperty("sha1", out var s) ? s.GetString() ?? "" : "";

            await Downloader.GetVerifiedAsync(url, serverJar, sha1, log, ct);
        }

        private static async Task InstallFabricAsync(
            string version, string serverJar, IProgress<string>? log, CancellationToken ct)
        {
            string loader = await FabricMeta.LatestLoaderAsync(version, ct);
            string installer = await FabricMeta.LatestInstallerAsync(ct);

            log?.Report($"Fabric loader {loader} (installer {installer}).");
            await Downloader.GetFileAsync(FabricMeta.ServerJarUrl(version, loader, installer), serverJar, ct);
        }

        private static async Task InstallPaperAsync(
            string version, string serverJar, IProgress<string>? log, CancellationToken ct)
        {
            string json = await Downloader.GetStringAsync(
                $"https://api.papermc.io/v2/projects/paper/versions/{version}/builds", ct);

            using var doc = JsonDocument.Parse(json);
            var builds = doc.RootElement.GetProperty("builds").EnumerateArray().ToList();
            if (builds.Count == 0)
                throw new InvalidOperationException($"Paper has no builds for {version}.");

            var newest = builds.MaxBy(b => b.GetProperty("build").GetInt32());
            int buildNumber = newest.GetProperty("build").GetInt32();
            string jarName = newest.GetProperty("downloads").GetProperty("application")
                                   .GetProperty("name").GetString()!;

            log?.Report($"Paper build #{buildNumber}.");
            await Downloader.GetFileAsync(
                $"https://api.papermc.io/v2/projects/paper/versions/{version}/builds/{buildNumber}/downloads/{jarName}",
                serverJar, ct);
        }

        private static Task InstallPurpurAsync(
            string version, string serverJar, IProgress<string>? log, CancellationToken ct)
        {
            log?.Report("Purpur latest build.");
            return Downloader.GetFileAsync(
                $"https://api.purpurmc.org/v2/purpur/{version}/latest/download", serverJar, ct);
        }

        private static async Task InstallForgeFamilyAsync(
            ForgeFlavor flavor, string version, string serverDir,
            IProgress<string>? log, CancellationToken ct)
        {
            string loaderVersion = await ForgeMeta.LatestVersionAsync(flavor, version, ct);
            log?.Report($"{ForgeMeta.DisplayName(flavor)} {loaderVersion}.");

            await ForgeInstaller.InstallServerAsync(
                flavor, version, loaderVersion, serverDir, log, ct);

            // Only pre-1.17 Forge needs this: it drops a runnable jar under its own
            // name, so rename it to the one name the launcher starts. Modern builds
            // are left alone — they install an argument file, and their "-shim.jar"
            // is a compatibility stub that must not be mistaken for the real server.
            if (ServerStartPlanner.FindArgumentFile(serverDir) is not null) return;

            string serverJar = Path.Combine(serverDir, "server.jar");
            if (File.Exists(serverJar)) return;

            string? produced = Directory
                .GetFiles(serverDir, "*.jar")
                .FirstOrDefault(f =>
                {
                    string name = Path.GetFileName(f);
                    return (name.StartsWith("forge-", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase)) &&
                           !name.Contains("installer", StringComparison.OrdinalIgnoreCase) &&
                           !name.Contains("shim", StringComparison.OrdinalIgnoreCase);
                });

            if (produced is not null)
            {
                File.Move(produced, serverJar, overwrite: true);
                log?.Report("Renamed the produced jar to server.jar.");
            }
        }
    }
}
