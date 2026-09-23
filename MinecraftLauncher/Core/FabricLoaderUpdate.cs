using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Updating the Fabric loader of a version that is already installed, without
    /// reinstalling Minecraft.
    /// </summary>
    /// <remarks>
    /// Two routes, because the machines differ. One that can reach the internet pulls
    /// the new loader straight from Fabric. One that cannot — which is most of them —
    /// takes a **pack**: a zip produced by a machine that could, holding the profile
    /// JSON and the libraries it names, and nothing else.
    ///
    /// Only the loader is touched. The game, its assets, the worlds and the mods are
    /// left exactly where they are, which is the whole point: re-downloading Minecraft
    /// is what this avoids.
    /// </remarks>
    public static class FabricLoaderUpdate
    {
        /// <summary>Marks a zip as one of ours, and says what it is for.</summary>
        public const string ManifestName = "fabric-loader-pack.json";

        /// <summary>What a pack contains, read from its manifest.</summary>
        public sealed record PackInfo(
            string MinecraftVersion,
            string LoaderVersion,
            int FileCount,
            long Bytes)
        {
            public string Describe() =>
                $"Fabric loader {LoaderVersion} for Minecraft {MinecraftVersion} " +
                $"— {FileCount} files, {Bytes / 1024.0 / 1024.0:0.#} MB";
        }

        /// <summary>Loader versions Fabric offers for this Minecraft version, newest first.</summary>
        public static Task<List<string>> AvailableAsync(string mcVersion, CancellationToken ct) =>
            FabricMeta.LoaderVersionsAsync(mcVersion, ct);

        /// <summary>Downloads and installs a loader version. Needs internet.</summary>
        public static Task InstallOnlineAsync(
            string mcVersion, string loaderVersion, IProgress<string>? log, CancellationToken ct) =>
            FabricMeta.InstallAsync(mcVersion, loaderVersion, log, ct);

        /// <summary>
        /// Builds a pack from a loader already installed here, for carrying to machines
        /// that cannot download it themselves.
        /// </summary>
        public static async Task<PackInfo> ExportAsync(
            string mcVersion, string loaderVersion, string zipPath,
            IProgress<string>? log, CancellationToken ct)
        {
            string versionDir = Paths.VersionDir(mcVersion);
            string profileName = $"fabric-loader-{loaderVersion}-{mcVersion}.json";
            string profilePath = Path.Combine(versionDir, "versions", profileName);

            if (!File.Exists(profilePath))
                throw new FileNotFoundException(
                    $"Fabric loader {loaderVersion} is not installed for {mcVersion}, so there is " +
                    "nothing to export.", profilePath);

            var libraries = LibrariesOf(profilePath);
            log?.Report($"Profile names {libraries.Count} libraries.");

            string temp = zipPath + ".building";
            if (File.Exists(temp)) File.Delete(temp);

            long bytes = 0;
            int count = 0;

            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(temp, ZipArchiveMode.Create);

                zip.CreateEntryFromFile(profilePath, $"versions/{profileName}");
                bytes += new FileInfo(profilePath).Length;
                count++;

                foreach (string relative in libraries)
                {
                    ct.ThrowIfCancellationRequested();

                    string source = Path.Combine(versionDir, "libraries",
                        relative.Replace('/', Path.DirectorySeparatorChar));

                    // A library the profile names but this machine never fetched is
                    // reported rather than silently dropped, or the pack is incomplete
                    // in a way the receiving machine cannot detect.
                    if (!File.Exists(source))
                    {
                        log?.Report($"[MISSING] {relative}");
                        continue;
                    }

                    zip.CreateEntryFromFile(source, $"libraries/{relative}");
                    bytes += new FileInfo(source).Length;
                    count++;
                }

                var manifest = JsonSerializer.Serialize(new
                {
                    minecraft = mcVersion,
                    loader = loaderVersion,
                    files = count,
                    created = DateTime.UtcNow.ToString("u")
                }, new JsonSerializerOptions { WriteIndented = true });

                var entry = zip.CreateEntry(ManifestName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(manifest);
            }, ct);

            File.Move(temp, zipPath, overwrite: true);
            log?.Report($"Wrote {Path.GetFileName(zipPath)}.");

            return new PackInfo(mcVersion, loaderVersion, count, bytes);
        }

        /// <summary>
        /// Reads a pack's manifest without unpacking it, so the user can be told what
        /// they picked before anything is written.
        /// </summary>
        public static PackInfo? Inspect(string zipPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);

                var manifest = zip.GetEntry(ManifestName);
                if (manifest is null) return null;

                using var reader = new StreamReader(manifest.Open());
                using var doc = JsonDocument.Parse(reader.ReadToEnd());
                var root = doc.RootElement;

                return new PackInfo(
                    root.TryGetProperty("minecraft", out var mc) ? mc.GetString() ?? "" : "",
                    root.TryGetProperty("loader", out var l) ? l.GetString() ?? "" : "",
                    zip.Entries.Count(e => e.Name.Length > 0) - 1,
                    zip.Entries.Sum(e => e.Length));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Unpacks a pack into an installed version. Refuses a pack built for a
        /// different Minecraft version, because its libraries would not match.
        /// </summary>
        public static async Task<PackInfo> ImportAsync(
            string mcVersion, string zipPath, IProgress<string>? log, CancellationToken ct)
        {
            var info = Inspect(zipPath)
                ?? throw new InvalidDataException(
                    "That file is not a Fabric loader pack made by this launcher.");

            if (!info.MinecraftVersion.Equals(mcVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"That pack is for Minecraft {info.MinecraftVersion}, but {mcVersion} is selected.");

            string versionDir = Paths.VersionDir(mcVersion);
            if (!Directory.Exists(versionDir))
                throw new DirectoryNotFoundException($"Minecraft {mcVersion} is not installed here.");

            await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(zipPath);

                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    if (entry.Name.Length == 0) continue;               // a directory
                    if (entry.FullName == ManifestName) continue;

                    // Nothing in a supplied file gets to choose where it lands.
                    string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string destination = Path.GetFullPath(Path.Combine(versionDir, relative));

                    if (!destination.StartsWith(Path.GetFullPath(versionDir), StringComparison.OrdinalIgnoreCase))
                    {
                        log?.Report($"[REFUSED] {entry.FullName} tried to escape the version folder");
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                    log?.Report($"[OK] {entry.FullName}");
                }
            }, ct);

            log?.Report($"Fabric loader {info.LoaderVersion} installed for {mcVersion}.");
            return info;
        }

        /// <summary>The library paths a profile names, as Maven-style relative paths.</summary>
        private static List<string> LibrariesOf(string profilePath)
        {
            var paths = new List<string>();

            using var doc = JsonDocument.Parse(File.ReadAllText(profilePath));
            if (!doc.RootElement.TryGetProperty("libraries", out var libs) ||
                libs.ValueKind != JsonValueKind.Array)
                return paths;

            foreach (var lib in libs.EnumerateArray())
            {
                string name = lib.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Length == 0) continue;

                try { paths.Add(FabricMeta.MavenPath(name)); }
                catch (FormatException) { /* not a coordinate; nothing to carry */ }
            }

            return paths;
        }

        /// <summary>Every Fabric loader installed for a version, newest first.</summary>
        public static List<string> InstalledLoaders(string mcVersion)
        {
            string dir = Path.Combine(Paths.VersionDir(mcVersion), "versions");
            if (!Directory.Exists(dir)) return new List<string>();

            var found = new List<string>();

            foreach (string file in Directory.GetFiles(dir, "fabric-loader*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                string prefix = "fabric-loader-";
                string suffix = "-" + mcVersion;

                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(name[prefix.Length..^suffix.Length]);
                }
            }

            found.Sort((a, b) => MavenVersion.Compare(b, a));
            return found;
        }
    }
}
