using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// What a mod jar says it needs, and whether a folder actually provides it.
    /// </summary>
    /// <remarks>
    /// Exists because of a real near-miss. A downgraded Immersive Portals build was
    /// about to be required on twenty machines; it declares
    /// <c>fabric-api &gt;= 0.154.2</c> and every machine had 0.152.1 installed. A
    /// presence check says "fabric-api? yes, got it" and the game then fails to start
    /// on all of them. **A dependency is not present-or-absent, it is present at a good
    /// enough version**, and that distinction is the whole point of this file.
    ///
    /// Three things have to be read out of the jar, not guessed:
    /// <list type="bullet">
    /// <item><c>depends</c> — the ids and version predicates.</item>
    /// <item><c>provides</c> — a mod can satisfy somebody else's dependency under
    /// another name. That portals build provides <c>iportal</c>,
    /// <c>imm_ptl_core</c> and <c>immersive_portals_core</c>.</item>
    /// <item><c>META-INF/jars/*.jar</c> — Fabric loads nested jars, so a dependency
    /// shipped inside the mod is already met. The same jar bundles
    /// <c>dimlib-1.1.0+mc26.1.2</c>, which does not exist for 26.1.2 on Modrinth at
    /// all; treating it as missing would have made the mod look impossible to
    /// require.</item>
    /// </list>
    /// </remarks>
    public static class ModDependencies
    {
        /// <summary>
        /// Dependency ids that are not mods and can never be installed as one.
        /// </summary>
        /// <remarks>
        /// The game, the JVM and the loader. They are real requirements — and the Mods
        /// tab already warns about the loader one separately — but nothing is going to
        /// fetch them from Modrinth, so they must not be reported as missing mods.
        /// </remarks>
        private static readonly HashSet<string> NotMods =
            new(StringComparer.OrdinalIgnoreCase)
            { "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "neoforge", "forge" };

        /// <summary>One declared dependency.</summary>
        public sealed record Need(string ModId, string? Predicate)
        {
            public string Describe() =>
                string.IsNullOrWhiteSpace(Predicate) || Predicate == "*"
                    ? ModId
                    : $"{ModId} {Predicate}";
        }

        /// <summary>What a jar says about itself.</summary>
        public sealed record JarFacts(
            string? Id,
            string? Version,
            string? Name,
            IReadOnlyList<string> Provides,
            IReadOnlyList<Need> Depends,
            IReadOnlyList<string> Breaks,
            IReadOnlyList<string> BundledIds);

        /// <summary>Reads a jar's own declarations, or null when it is not a Fabric mod.</summary>
        public static JarFacts? Read(string jarPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(jarPath);

                var entry = zip.GetEntry("fabric.mod.json");
                if (entry is null) return null;

                string text;
                using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();

                // Nested ids come from the jars Fabric will load alongside this one.
                var bundled = new List<string>();
                foreach (var nested in zip.Entries.Where(e =>
                             e.FullName.StartsWith("META-INF/jars/", StringComparison.OrdinalIgnoreCase) &&
                             e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (string id in IdsInNested(nested)) bundled.Add(id);
                }

                return Parse(text, bundled);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return null;
            }
        }

        /// <summary>The ids a nested jar supplies — its own, plus whatever it provides.</summary>
        private static IEnumerable<string> IdsInNested(ZipArchiveEntry nested)
        {
            var ids = new List<string>();

            try
            {
                // Copied out first: a ZipArchive over a non-seekable entry stream cannot
                // read a central directory.
                using var memory = new MemoryStream();
                using (var source = nested.Open()) source.CopyTo(memory);
                memory.Position = 0;

                using var inner = new ZipArchive(memory, ZipArchiveMode.Read);
                var meta = inner.GetEntry("fabric.mod.json");
                if (meta is null) return ids;

                string text;
                using (var reader = new StreamReader(meta.Open())) text = reader.ReadToEnd();

                var facts = Parse(text, Array.Empty<string>());
                if (facts.Id is { Length: > 0 }) ids.Add(facts.Id);
                ids.AddRange(facts.Provides);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                // A nested jar we cannot read just does not contribute ids.
            }

            return ids;
        }

        /// <summary>Parses a <c>fabric.mod.json</c>, separated out so it can be tested.</summary>
        public static JarFacts Parse(string json, IReadOnlyList<string> bundledIds)
        {
            string? id = null, version = null, name = null;
            var provides = new List<string>();
            var depends = new List<Need>();
            var breaks = new List<string>();

            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

                var r = doc.RootElement;

                if (r.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String) id = i.GetString();
                if (r.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String) version = v.GetString();
                if (r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();

                if (r.TryGetProperty("provides", out var p) && p.ValueKind == JsonValueKind.Array)
                    foreach (var x in p.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s)
                            provides.Add(s);

                if (r.TryGetProperty("depends", out var d) && d.ValueKind == JsonValueKind.Object)
                    foreach (var prop in d.EnumerateObject())
                        depends.Add(new Need(prop.Name, PredicateOf(prop.Value)));

                if (r.TryGetProperty("breaks", out var b) && b.ValueKind == JsonValueKind.Object)
                    foreach (var prop in b.EnumerateObject())
                        breaks.Add(prop.Name);
            }
            catch (JsonException)
            {
                // A jar whose metadata will not parse tells us nothing; it does not get
                // to stop the caller.
            }

            return new JarFacts(id, version, name, provides, depends, breaks, bundledIds);
        }

        /// <summary>
        /// A dependency's version predicate. It is a string, or an array of them when a
        /// mod accepts several ranges.
        /// </summary>
        private static string? PredicateOf(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),

            // Any of them being acceptable is the loader's rule; keeping the first is
            // enough to report, and SatisfiesFabric answers "cannot tell" on anything
            // it does not understand rather than guessing.
            JsonValueKind.Array => value.EnumerateArray()
                                        .Where(x => x.ValueKind == JsonValueKind.String)
                                        .Select(x => x.GetString())
                                        .FirstOrDefault(),

            _ => null
        };

        /// <summary>Every mod id a folder supplies, mapped to the version supplying it.</summary>
        /// <remarks>
        /// Includes <c>provides</c> aliases and the ids inside nested jars, because all
        /// three are things the loader will consider satisfied. Disabled jars are left
        /// out here, unlike elsewhere: a turned-off mod genuinely cannot satisfy a
        /// dependency at run time.
        /// </remarks>
        public static Dictionary<string, string?> InstalledIds(string modsFolder)
        {
            var found = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(modsFolder) || !Directory.Exists(modsFolder)) return found;

            foreach (string path in Directory.EnumerateFiles(modsFolder, "*.jar"))
            {
                var facts = Read(path);
                if (facts is null) continue;

                if (facts.Id is { Length: > 0 }) found[facts.Id] = facts.Version;

                // An alias is satisfied by whatever provides it, at that mod's version.
                foreach (string alias in facts.Provides) found.TryAdd(alias, facts.Version);

                // Nested jars are loaded too, but their versions are not known from here.
                foreach (string nested in facts.BundledIds) found.TryAdd(nested, null);
            }

            return found;
        }

        /// <summary>How a single dependency stands against what is installed.</summary>
        public enum Standing
        {
            /// <summary>Installed, and the version is acceptable (or unconstrained).</summary>
            Met,

            /// <summary>Nothing supplies that id.</summary>
            Missing,

            /// <summary>Supplied, but at a version the mod says it cannot use.</summary>
            TooOld,

            /// <summary>Supplied, but the predicate could not be understood.</summary>
            Unknown
        }

        public sealed record Verdict(Need Need, Standing Standing, string? Installed)
        {
            public bool NeedsAction => Standing is Standing.Missing or Standing.TooOld;

            public string Describe() => Standing switch
            {
                Standing.Missing => $"{Need.Describe()} — not installed",
                Standing.TooOld  => $"{Need.Describe()} — you have {Installed}",
                Standing.Unknown => $"{Need.Describe()} — cannot tell from {Installed}",
                _                => Need.Describe()
            };
        }

        /// <summary>
        /// Checks a jar's declared dependencies against a mods folder.
        /// </summary>
        /// <remarks>
        /// Skips the game, the JVM and the loader, and skips anything the jar bundles
        /// itself. What is left is the set somebody actually has to go and get.
        /// </remarks>
        public static List<Verdict> Check(JarFacts jar, string modsFolder)
        {
            var installed = InstalledIds(modsFolder);
            var verdicts = new List<Verdict>();

            foreach (var need in jar.Depends)
            {
                if (NotMods.Contains(need.ModId)) continue;
                if (jar.BundledIds.Contains(need.ModId, StringComparer.OrdinalIgnoreCase)) continue;

                // Its own id and aliases are satisfied by itself.
                if (need.ModId.Equals(jar.Id, StringComparison.OrdinalIgnoreCase)) continue;
                if (jar.Provides.Contains(need.ModId, StringComparer.OrdinalIgnoreCase)) continue;

                if (!installed.TryGetValue(need.ModId, out string? have))
                {
                    verdicts.Add(new Verdict(need, Standing.Missing, null));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(need.Predicate) || need.Predicate == "*")
                {
                    verdicts.Add(new Verdict(need, Standing.Met, have));
                    continue;
                }

                bool? ok = VersionRange.SatisfiesFabric(have, need.Predicate);

                verdicts.Add(new Verdict(need,
                    ok switch { true => Standing.Met, false => Standing.TooOld, _ => Standing.Unknown },
                    have));
            }

            return verdicts;
        }

        /// <summary>Installed mods this jar says it cannot run alongside.</summary>
        public static List<string> ConflictsIn(JarFacts jar, string modsFolder)
        {
            var installed = InstalledIds(modsFolder);
            return jar.Breaks.Where(installed.ContainsKey).ToList();
        }
    }
}
