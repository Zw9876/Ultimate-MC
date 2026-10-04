using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Reading what a mod says it needs, and checking it against a folder.
    /// </summary>
    /// <remarks>
    /// The metadata below is the real <c>fabric.mod.json</c> out of a downgraded
    /// Immersive Portals build that was about to be required on twenty machines,
    /// trimmed only of the entrypoints and mixin lists. It is worth having verbatim
    /// because it contains every case that matters at once: a repack carrying the
    /// original mod's id, three <c>provides</c> aliases, a dependency on
    /// <c>fabric-api &gt;= 0.154.2</c> that the machines did not meet, dependencies on
    /// the game and the JVM that are not mods at all, and a <c>dimlib</c> dependency
    /// satisfied by a nested jar rather than by anything installable.
    ///
    /// The near-miss this is for: every machine had fabric-api 0.152.1. A
    /// present-or-absent check says "fabric-api? yes" and the game then fails to start
    /// on all of them.
    /// </remarks>
    public static class ModDependencyTests
    {
        private const string RealPortalsMeta = """
            {"schemaVersion":1,"id":"immersive_portals","provides":["iportal","imm_ptl_core","immersive_portals_core"],
             "version":"6.1.0-beta.7+mc26.1.2-b6","name":"Immersively Vibed Portals",
             "description":"Unofficial port of Immersive Portals backported to Minecraft 26.1.2.",
             "license":"Apache-2.0","environment":"*",
             "depends":{"fabricloader":">=0.19.3","fabric-api":">=0.154.2","minecraft":"26.1.2","java":">=25","dimlib":"*"},
             "breaks":{"optifabric":"*","pehkui":"<3.4.1","viafabric-mc119":"*","resolutioncontrol":"*","canvas":"*","cardboard":"*","gravity_api":"*","vmp":"*"}}
            """;

        public static void Run()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mc-deps-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            try
            {
                Reading();
                Checking(dir);
                Bundled(dir);
                RealJar();
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>A jar that is nothing but its metadata — enough for every reader here.</summary>
        private static string FakeJar(string folder, string fileName, string id, string version,
                                      string? provides = null, string? depends = null)
        {
            string path = Path.Combine(folder, fileName);

            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("fabric.mod.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));

            writer.Write($$"""
                {"schemaVersion":1,"id":"{{id}}","version":"{{version}}"
                {{(provides is null ? "" : $",\"provides\":[{provides}]")}}
                {{(depends is null ? "" : $",\"depends\":{{{depends}}}")}}}
                """);

            return path;
        }

        private static void Reading()
        {
            Section("what the real portals jar declares");

            var jar = ModDependencies.Parse(RealPortalsMeta, Array.Empty<string>());

            Expect("the mod id", jar.Id, "immersive_portals");
            Expect("its own version", jar.Version, "6.1.0-beta.7+mc26.1.2-b6");
            Expect("the display name", jar.Name, "Immersively Vibed Portals");

            // A repack carries the original's id. This is why a host-served entry is
            // satisfied by hash and not by id.
            Check("the id is the ORIGINAL mod's, not the repack's",
                  jar.Id == "immersive_portals" && jar.Name!.Contains("Vibed"));

            Expect("it provides three aliases", jar.Provides.Count, 3);
            Check("including imm_ptl_core", jar.Provides.Contains("imm_ptl_core"));

            Expect("five dependencies are declared", jar.Depends.Count, 5);

            var api = jar.Depends.FirstOrDefault(d => d.ModId == "fabric-api");
            Check("fabric-api is one of them", api is not null);
            Expect("with its predicate", api!.Predicate, ">=0.154.2");
            Expect("described for a person", api.Describe(), "fabric-api >=0.154.2");

            var dim = jar.Depends.FirstOrDefault(d => d.ModId == "dimlib");
            Check("dimlib is wanted with any version", dim?.Predicate == "*");
            Expect("an unconstrained need reads as just the id", dim!.Describe(), "dimlib");

            Expect("eight conflicts are declared", jar.Breaks.Count, 8);
            Check("including vmp", jar.Breaks.Contains("vmp"));

            Check("rubbish metadata yields nothing rather than throwing",
                  ModDependencies.Parse("{ not json", Array.Empty<string>()).Id is null);
        }

        private static void Checking(string root)
        {
            Section("against a folder");

            var jar = ModDependencies.Parse(RealPortalsMeta, new[] { "dimlib" });

            // The exact situation on the real machines: fabric-api present, too old.
            string tooOld = Path.Combine(root, "too-old");
            Directory.CreateDirectory(tooOld);
            FakeJar(tooOld, "fabric-api-0.152.1.jar", "fabric-api", "0.152.1+26.1.2");

            var verdicts = ModDependencies.Check(jar, tooOld);
            Note(string.Join("; ", verdicts.Select(v => v.Describe())));

            var api = verdicts.Single(v => v.Need.ModId == "fabric-api");
            Check("an installed but too-old dependency is caught",
                  api.Standing == ModDependencies.Standing.TooOld, api.Standing.ToString());
            Expect("and it reports what is there", api.Installed, "0.152.1+26.1.2");
            Check("which needs doing something about", api.NeedsAction);
            Check("and says so plainly", api.Describe().Contains("you have 0.152.1"), api.Describe());

            // The failure this whole file exists to prevent.
            Check("presence alone would have passed it",
                  ModDependencies.InstalledIds(tooOld).ContainsKey("fabric-api"));

            // Not mods, and must never be reported as missing ones.
            foreach (string notAMod in new[] { "minecraft", "java", "fabricloader" })
                Check($"{notAMod} is not treated as a mod",
                      verdicts.All(v => v.Need.ModId != notAMod));

            // Bundled inside the jar, so already met.
            Check("a bundled dependency is not reported missing",
                  verdicts.All(v => v.Need.ModId != "dimlib"));

            // Now a folder that does meet it.
            string fine = Path.Combine(root, "fine");
            Directory.CreateDirectory(fine);
            FakeJar(fine, "fabric-api-0.155.3.jar", "fabric-api", "0.155.3+26.1.2");

            var okVerdicts = ModDependencies.Check(jar, fine);
            Check("a new enough dependency is met",
                  okVerdicts.Single(v => v.Need.ModId == "fabric-api").Standing == ModDependencies.Standing.Met);
            Check("so nothing needs doing", okVerdicts.All(v => !v.NeedsAction));

            // And one that has nothing at all.
            string empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            var none = ModDependencies.Check(jar, empty);
            Check("an absent dependency reads as missing",
                  none.Single(v => v.Need.ModId == "fabric-api").Standing == ModDependencies.Standing.Missing);

            // Conflicts.
            string clashing = Path.Combine(root, "clash");
            Directory.CreateDirectory(clashing);
            FakeJar(clashing, "vmp.jar", "vmp", "0.2.0");
            FakeJar(clashing, "sodium.jar", "sodium", "0.6.0");

            var conflicts = ModDependencies.ConflictsIn(jar, clashing);
            Check("a declared conflict that is installed is found", conflicts.Contains("vmp"), string.Join(", ", conflicts));
            Check("an unrelated mod is not called a conflict", !conflicts.Contains("sodium"));
        }

        private static void Bundled(string root)
        {
            Section("aliases and nested jars");

            string folder = Path.Combine(root, "aliases");
            Directory.CreateDirectory(folder);

            // One mod satisfying a dependency under another name.
            FakeJar(folder, "provider.jar", "the_real_mod", "1.0.0", provides: "\"an_alias\"");

            var ids = ModDependencies.InstalledIds(folder);
            Check("the mod's own id is found", ids.ContainsKey("the_real_mod"));
            Check("and the alias it provides", ids.ContainsKey("an_alias"));
            Expect("the alias carries the provider's version", ids["an_alias"], "1.0.0");

            var wantsAlias = ModDependencies.Parse(
                """{"id":"consumer","version":"1","depends":{"an_alias":">=1.0.0"}}""",
                Array.Empty<string>());

            Check("a dependency met by an alias is satisfied",
                  ModDependencies.Check(wantsAlias, folder)
                                 .Single().Standing == ModDependencies.Standing.Met);

            // A mod does not need to depend on itself to be satisfied.
            var selfish = ModDependencies.Parse(
                """{"id":"me","version":"1","provides":["also_me"],"depends":{"me":"*","also_me":"*"}}""",
                Array.Empty<string>());
            Expect("a mod's own id and aliases are skipped",
                   ModDependencies.Check(selfish, folder).Count, 0);
        }

        private static void RealJar()
        {
            Section("the real jar on disk, if it is still there");

            string jar = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "immersively-vibed-portals-6.1.0-beta.7+mc26.1.2-b6-mc26.1.2-fabric.jar");

            if (!File.Exists(jar))
            {
                Skip("the real portals jar", "not in Downloads any more");
                return;
            }

            var facts = ModDependencies.Read(jar);
            Check("it reads as a Fabric mod", facts is not null);
            if (facts is null) return;

            Expect("the id matches the captured metadata", facts.Id, "immersive_portals");
            Note($"bundles: {string.Join(", ", facts.BundledIds)}");

            // The reason bundled jars have to be read: DimLib has no 26.1.2 build on
            // Modrinth at all, so without this the mod looks impossible to satisfy.
            Check("dimlib is found inside the jar",
                  facts.BundledIds.Contains("dimlib", StringComparer.OrdinalIgnoreCase),
                  string.Join(", ", facts.BundledIds));

            Check("so it is not reported as a missing dependency",
                  ModDependencies.Check(facts, Path.GetTempPath())
                                 .All(v => v.Need.ModId != "dimlib"));
        }
    }
}
