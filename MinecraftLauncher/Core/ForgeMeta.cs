using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MinecraftLauncher.Core
{
    /// <summary>Which of the two Forge-family loaders to install.</summary>
    public enum ForgeFlavor
    {
        Forge,
        NeoForge
    }

    /// <summary>
    /// Resolves Forge and NeoForge build numbers and installer URLs. Builds are
    /// looked up live; nothing is pinned.
    /// </summary>
    public static class ForgeMeta
    {
        private const string ForgePromotions =
            "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
        private const string ForgeMavenMeta =
            "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
        private const string NeoForgeMavenMeta =
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

        public static string DisplayName(ForgeFlavor flavor) =>
            flavor == ForgeFlavor.NeoForge ? "NeoForge" : "Forge";

        /// <summary>Newest build for a Minecraft version, preferring stable over beta.</summary>
        public static async Task<string> LatestVersionAsync(
            ForgeFlavor flavor, string mcVersion, CancellationToken ct)
        {
            var versions = await VersionsAsync(flavor, mcVersion, ct);
            if (versions.Count == 0)
                throw new InvalidOperationException(
                    $"No {DisplayName(flavor)} build exists for Minecraft {mcVersion}.");

            return versions[0];
        }

        /// <summary>All known builds for a Minecraft version, newest first.</summary>
        public static Task<IReadOnlyList<string>> VersionsAsync(
            ForgeFlavor flavor, string mcVersion, CancellationToken ct) =>
            flavor == ForgeFlavor.NeoForge
                ? NeoForgeVersionsAsync(mcVersion, ct)
                : ForgeVersionsAsync(mcVersion, ct);

        public static string InstallerUrl(ForgeFlavor flavor, string mcVersion, string loaderVersion) =>
            flavor == ForgeFlavor.NeoForge
                ? $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/" +
                  $"neoforge-{loaderVersion}-installer.jar"
                : $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{loaderVersion}/" +
                  $"forge-{mcVersion}-{loaderVersion}-installer.jar";

        // ── Forge ────────────────────────────────────────────────────
        private static async Task<IReadOnlyList<string>> ForgeVersionsAsync(
            string mcVersion, CancellationToken ct)
        {
            var found = new List<string>();

            // The promotions file is the maintainers' own pick, so it leads.
            try
            {
                using var doc = JsonDocument.Parse(await Downloader.GetStringAsync(ForgePromotions, ct));
                if (doc.RootElement.TryGetProperty("promos", out var promos))
                {
                    foreach (string key in new[] { $"{mcVersion}-recommended", $"{mcVersion}-latest" })
                        if (promos.TryGetProperty(key, out var v) &&
                            v.GetString() is string s && !found.Contains(s))
                            found.Add(s);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Maven listing below still gives an answer.
            }

            try
            {
                string xml = await Downloader.GetStringAsync(ForgeMavenMeta, ct);
                string prefix = mcVersion + "-";

                var fromMaven = XDocument.Parse(xml).Descendants("version")
                    .Select(e => e.Value)
                    .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(v => v[prefix.Length..])
                    .OrderByDescending(v => v, MavenVersion.Comparer);

                foreach (string v in fromMaven)
                    if (!found.Contains(v)) found.Add(v);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Promotions may already have produced something usable.
            }

            return found;
        }

        // ── NeoForge ─────────────────────────────────────────────────
        private static async Task<IReadOnlyList<string>> NeoForgeVersionsAsync(
            string mcVersion, CancellationToken ct)
        {
            string prefix = NeoForgeBase(mcVersion) + ".";

            string xml = await Downloader.GetStringAsync(NeoForgeMavenMeta, ct);
            var all = XDocument.Parse(xml).Descendants("version")
                .Select(e => e.Value)
                .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            // Betas are real builds but should never outrank a stable one.
            var stable = all.Where(v => !IsPrerelease(v))
                            .OrderByDescending(v => v, MavenVersion.Comparer);
            var beta = all.Where(IsPrerelease)
                          .OrderByDescending(v => v, MavenVersion.Comparer);

            return stable.Concat(beta).ToList();
        }

        private static bool IsPrerelease(string version) =>
            version.Contains("-beta", StringComparison.OrdinalIgnoreCase) ||
            version.Contains("-alpha", StringComparison.OrdinalIgnoreCase) ||
            version.Contains("-rc", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The NeoForge version prefix for a Minecraft version. NeoForge encodes the
        /// game version in its own number rather than carrying it separately, and the
        /// encoding differs between Minecraft's old and new version schemes:
        /// <list type="bullet">
        /// <item><description>1.21.1 → 21.1 (builds look like 21.1.248)</description></item>
        /// <item><description>1.21 → 21.0 (builds look like 21.0.167)</description></item>
        /// <item><description>26.1.2 → 26.1.2 (builds look like 26.1.2.95)</description></item>
        /// <item><description>26.2 → 26.2.0 (builds look like 26.2.0.59)</description></item>
        /// </list>
        /// </summary>
        public static string NeoForgeBase(string mcVersion)
        {
            if (mcVersion.StartsWith("1.", StringComparison.Ordinal))
            {
                var rest = mcVersion[2..].Split('.');
                string major = rest.Length > 0 ? rest[0] : "0";
                string minor = rest.Length > 1 ? rest[1] : "0";
                return $"{major}.{minor}";
            }

            var parts = mcVersion.Split('.').ToList();
            while (parts.Count < 3) parts.Add("0");
            return string.Join('.', parts.Take(3));
        }
    }
}
