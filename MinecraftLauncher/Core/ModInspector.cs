using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace MinecraftLauncher.Core
{
    /// <summary>Which loaders a mod jar declares itself for. A jar may name several.</summary>
    [Flags]
    public enum ModLoaders
    {
        None     = 0,
        Fabric   = 1,
        Quilt    = 2,
        Forge    = 4,
        NeoForge = 8,
        Bukkit   = 16
    }

    /// <summary>
    /// Works out which loader a mod jar is built for by looking inside it.
    /// </summary>
    /// <remarks>
    /// Necessary because <c>versions/&lt;version&gt;/mods</c> is per-version, not
    /// per-loader: Fabric and NeoForge on the same Minecraft version share one folder,
    /// and a loader handed the other one's jars fails at startup with a stack trace
    /// that says nothing about the real cause. Reading the jar is the only reliable
    /// answer — file names lie often enough to be useless, and plenty of correct ones
    /// carry no loader in the name at all.
    ///
    /// Every check is an entry lookup in a zip, so this works offline and touches
    /// nothing.
    /// </remarks>
    public static class ModInspector
    {
        /// <summary>Marker files each loader requires at the root of a mod jar.</summary>
        private static readonly (string Entry, ModLoaders Loader)[] Markers =
        {
            ("fabric.mod.json",               ModLoaders.Fabric),
            ("quilt.mod.json",                ModLoaders.Quilt),
            ("META-INF/neoforge.mods.toml",   ModLoaders.NeoForge),
            ("plugin.yml",                    ModLoaders.Bukkit),
            ("paper-plugin.yml",              ModLoaders.Bukkit),
        };

        /// <summary>
        /// Forge's descriptor, which NeoForge also used up to 1.20.1 before moving to
        /// its own. A jar carrying only this one cannot be pinned to either, so it is
        /// reported as both rather than guessed at — crying wolf on a working mod
        /// would be worse than staying quiet.
        /// </summary>
        private const string AmbiguousForgeEntry = "META-INF/mods.toml";

        /// <summary>Forge before 1.13 used this instead.</summary>
        private const string LegacyForgeEntry = "mcmod.info";

        /// <summary>
        /// What this jar declares itself for, or <see cref="ModLoaders.None"/> when it
        /// says nothing — a library jar, a resource pack dropped in by mistake, or a
        /// file that is not a zip at all.
        /// </summary>
        public static ModLoaders Detect(string jarPath)
        {
            if (string.IsNullOrWhiteSpace(jarPath) || !File.Exists(jarPath))
                return ModLoaders.None;

            try
            {
                using var zip = ZipFile.OpenRead(jarPath);
                var found = ModLoaders.None;

                foreach (var (entry, loader) in Markers)
                    if (zip.GetEntry(entry) is not null)
                        found |= loader;

                if (zip.GetEntry(AmbiguousForgeEntry) is not null ||
                    zip.GetEntry(LegacyForgeEntry) is not null)
                {
                    // Only widen to "either" when nothing more specific was found;
                    // a jar with neoforge.mods.toml AND mods.toml is a NeoForge jar
                    // keeping compatibility, not an ambiguous one.
                    if (!found.HasFlag(ModLoaders.NeoForge))
                        found |= ModLoaders.Forge | ModLoaders.NeoForge;
                    else
                        found |= ModLoaders.Forge;
                }

                return found;
            }
            catch
            {
                // Not a readable zip. Nothing useful to say about it.
                return ModLoaders.None;
            }
        }

        /// <summary>
        /// The loader version a jar says it needs, in that loader's own syntax.
        /// </summary>
        /// <param name="Requirement">e.g. "&gt;=0.18.4" for Fabric, "[46,)" for Forge.</param>
        /// <param name="IsMavenRange">True for the Forge/NeoForge TOML syntax.</param>
        public sealed record LoaderNeed(string Requirement, bool IsMavenRange);

        // Fabric: "depends": { "fabricloader": ">=0.18.4", ... }
        private static readonly Regex RxFabricLoaderDep = new(
            @"""fabricloader""\s*:\s*""(?<need>[^""]+)""", RegexOptions.Compiled);

        // Forge/NeoForge TOML: a dependency block naming the loader, then its range.
        private static readonly Regex RxTomlLoaderDep = new(
            @"modId\s*=\s*""(?:neo)?forge""(?<between>(?:(?!modId\s*=)[\s\S])*?)versionRange\s*=\s*""(?<need>[^""]*)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// What loader version this jar requires, or null when it does not say.
        /// </summary>
        /// <remarks>
        /// This is the thing Modrinth cannot tell us: its API knows a mod is "for
        /// Fabric" but not that it needs Fabric Loader 0.18.4 or newer. The jar does
        /// know, so it is read here — the same zip that was already opened to work out
        /// which loader it belongs to.
        /// </remarks>
        public static LoaderNeed? RequiredLoaderVersion(string jarPath)
        {
            if (string.IsNullOrWhiteSpace(jarPath) || !File.Exists(jarPath)) return null;

            try
            {
                using var zip = ZipFile.OpenRead(jarPath);

                if (zip.GetEntry("fabric.mod.json") is ZipArchiveEntry fabric)
                {
                    string json = ReadAll(fabric);
                    var m = RxFabricLoaderDep.Match(json);

                    // JSON escapes the operators: ">=0.18.4" arrives as >=…
                    if (m.Success) return new LoaderNeed(Unescape(m.Groups["need"].Value), IsMavenRange: false);
                }

                foreach (string name in new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml" })
                {
                    if (zip.GetEntry(name) is not ZipArchiveEntry toml) continue;

                    var m = RxTomlLoaderDep.Match(ReadAll(toml));
                    if (m.Success) return new LoaderNeed(m.Groups["need"].Value.Trim(), IsMavenRange: true);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        // Fabric: the top-level "id" of the mod itself.
        private static readonly Regex RxFabricId = new(
            @"""id""\s*:\s*""(?<id>[A-Za-z0-9_\-]+)""", RegexOptions.Compiled);

        // Forge/NeoForge: the first modId in [[mods]], not the ones in dependencies.
        private static readonly Regex RxTomlModId = new(
            @"\[\[mods\]\][\s\S]*?modId\s*=\s*""(?<id>[A-Za-z0-9_\-]+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// The mod's own identifier, which is what a loader refuses to see twice.
        /// </summary>
        /// <remarks>
        /// File names cannot answer this. Two builds of one mod have different names
        /// (<c>sodium-fabric-0.5.12…</c> and <c>sodium-fabric-0.5.13…</c>) but the same
        /// id, and a loader handed both refuses to start. Identity has to come from
        /// inside the jar.
        /// </remarks>
        public static string? ModIdOf(string jarPath)
        {
            if (string.IsNullOrWhiteSpace(jarPath) || !File.Exists(jarPath)) return null;

            try
            {
                using var zip = ZipFile.OpenRead(jarPath);

                if (zip.GetEntry("fabric.mod.json") is ZipArchiveEntry fabric)
                {
                    var m = RxFabricId.Match(ReadAll(fabric));
                    if (m.Success) return m.Groups["id"].Value;
                }

                foreach (string name in new[] { "META-INF/neoforge.mods.toml", "META-INF/mods.toml" })
                {
                    if (zip.GetEntry(name) is not ZipArchiveEntry toml) continue;

                    var m = RxTomlModId.Match(ReadAll(toml));
                    if (m.Success) return m.Groups["id"].Value;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Other jars in a folder that are the same mod as <paramref name="jarPath"/>,
        /// whether enabled or already turned off.
        /// </summary>
        public static List<string> OtherCopiesOf(string jarPath, string folder)
        {
            var matches = new List<string>();

            string? id = ModIdOf(jarPath);
            if (id is null || !Directory.Exists(folder)) return matches;

            foreach (string candidate in Directory.EnumerateFiles(folder))
            {
                if (string.Equals(candidate, jarPath, StringComparison.OrdinalIgnoreCase)) continue;

                string name = Path.GetFileName(candidate);
                if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".jar" + ".disabled", StringComparison.OrdinalIgnoreCase)) continue;

                if (string.Equals(ModIdOf(candidate), id, StringComparison.OrdinalIgnoreCase))
                    matches.Add(candidate);
            }

            return matches;
        }

        /// <summary>
        /// A warning about the same mod being installed twice, or null when it is not.
        /// </summary>
        /// <remarks>
        /// Happens whenever a newer build is added without the old one going away —
        /// by hand, or by a downloader that matches on file name. The loader will not
        /// start, and its error names a mod id rather than the two files.
        /// </remarks>
        public static string? WarnAboutDuplicates(IEnumerable<ModEntry> mods)
        {
            var duplicated = mods
                .Where(m => m.Enabled)
                .Select(m => (m.DisplayName, Id: ModIdOf(m.FullPath)))
                .Where(x => x.Id is not null)
                .GroupBy(x => x.Id!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToList();

            if (duplicated.Count == 0) return null;

            string detail = string.Join("; ",
                duplicated.Take(3).Select(g => $"{g.Key} ({g.Count()} copies)"));

            return $"The same mod is installed more than once: {detail}" +
                   (duplicated.Count > 3 ? $", and {duplicated.Count - 3} more" : "") +
                   ". A loader refuses to start when it sees one mod twice, and its error " +
                   "names the mod rather than the files. Turn off all but the newest.";
        }

        /// <summary>
        /// Whether an installed loader version satisfies what a jar asked for.
        /// Null means the question could not be answered, and nothing should be said.
        /// </summary>
        public static bool? IsLoaderVersionOk(LoaderNeed? need, string? installedVersion)
        {
            if (need is null || string.IsNullOrWhiteSpace(installedVersion)) return null;

            return need.IsMavenRange
                ? VersionRange.SatisfiesMaven(installedVersion, need.Requirement)
                : VersionRange.SatisfiesFabric(installedVersion, need.Requirement);
        }

        private static string ReadAll(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>Turns the \uXXXX escapes a fabric.mod.json uses back into characters.</summary>
        private static string Unescape(string value) =>
            Regex.Replace(value, @"\\u(?<code>[0-9a-fA-F]{4})",
                m => ((char)Convert.ToInt32(m.Groups["code"].Value, 16)).ToString());

        /// <summary>The loaders a Server/Client tab selection can actually run.</summary>
        public static ModLoaders AcceptedBy(string loaderType) =>
            (loaderType ?? "").ToUpperInvariant() switch
            {
                // Quilt jars are Fabric-compatible in the direction that matters here.
                "FABRIC"   => ModLoaders.Fabric | ModLoaders.Quilt,
                "FORGE"    => ModLoaders.Forge,
                "NEOFORGE" => ModLoaders.NeoForge,
                "PAPER" or "PURPUR" or "SPIGOT" or "BUKKIT" => ModLoaders.Bukkit,
                _ => ModLoaders.None      // vanilla runs no mods at all
            };

        /// <summary>
        /// Whether a jar is safe to leave enabled for this loader. A jar that declares
        /// nothing is left alone: plenty of legitimate library jars carry no descriptor,
        /// and disabling those would break working setups.
        /// </summary>
        public static bool IsCompatible(ModLoaders jar, string loaderType)
        {
            if (jar == ModLoaders.None) return true;

            return (jar & AcceptedBy(loaderType)) != ModLoaders.None;
        }

        /// <summary>Short label for the Mods list, e.g. "Fabric" or "Forge/NeoForge".</summary>
        public static string Describe(ModLoaders loaders)
        {
            if (loaders == ModLoaders.None) return "—";

            // The common ambiguous case reads better as one label than two.
            if (loaders == (ModLoaders.Forge | ModLoaders.NeoForge)) return "Forge/NeoForge";

            var names = new List<string>();
            if (loaders.HasFlag(ModLoaders.Fabric))   names.Add("Fabric");
            if (loaders.HasFlag(ModLoaders.Quilt))    names.Add("Quilt");
            if (loaders.HasFlag(ModLoaders.NeoForge)) names.Add("NeoForge");
            if (loaders.HasFlag(ModLoaders.Forge) &&
                !loaders.HasFlag(ModLoaders.NeoForge)) names.Add("Forge");
            if (loaders.HasFlag(ModLoaders.Bukkit))   names.Add("Plugin");

            return string.Join(" + ", names);
        }

        /// <summary>
        /// A warning about mods in this folder that the chosen loader cannot run, or
        /// null when there is nothing to say.
        /// </summary>
        public static string? WarnAbout(IEnumerable<ModEntry> mods, string loaderType)
        {
            var wrong = mods.Where(m => m.Enabled && !IsCompatible(m.Loaders, loaderType)).ToList();
            if (wrong.Count == 0) return null;

            string which = wrong.Count == 1
                ? $"\"{wrong[0].DisplayName}\" is"
                : $"{wrong.Count} enabled mods are";

            return $"{which} not built for {loaderType}. " +
                   "This folder is shared by every loader on this Minecraft version, so " +
                   "they are probably left over from another one. The game will most " +
                   "likely crash on startup until they are turned off.";
        }

        /// <summary>
        /// A warning about mods that need a newer loader than the one installed, or
        /// null when there is nothing to say.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="WarnAbout"/> because the fix is different: these
        /// mods are for the right loader, they just need it updating. Turning them off
        /// works, but upgrading the loader is what the user actually wants.
        /// </remarks>
        public static string? WarnAboutLoaderVersion(
            IEnumerable<ModEntry> mods, string loaderType, string? installedVersion)
        {
            if (string.IsNullOrWhiteSpace(installedVersion)) return null;

            var tooNew = mods
                .Where(m => m.Enabled)
                .Select(m => (m.DisplayName, Need: RequiredLoaderVersion(m.FullPath)))
                .Where(x => IsLoaderVersionOk(x.Need, installedVersion) == false)
                .ToList();

            if (tooNew.Count == 0) return null;

            string worst = tooNew
                .Select(x => x.Need!.Requirement)
                .OrderByDescending(r => r, StringComparer.Ordinal)
                .First();

            string which = tooNew.Count == 1
                ? $"\"{tooNew[0].DisplayName}\" needs"
                : $"{tooNew.Count} enabled mods need";

            return $"{which} a newer {loaderType} loader than the {installedVersion} installed " +
                   $"(one asks for {worst}). They will fail at startup with an error about a " +
                   "missing dependency rather than about the loader. Update the loader, or turn them off.";
        }
    }
}
