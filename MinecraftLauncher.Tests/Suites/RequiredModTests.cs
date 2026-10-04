using System;
using System.IO;
using System.Linq;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// The list of mods the host asks everyone to have.
    /// </summary>
    /// <remarks>
    /// Two things here matter more than the rest. A malformed list must never be able
    /// to stand between somebody and the PLAY button, so parsing returns an empty list
    /// rather than throwing. And a mod somebody switched off counts as present: the
    /// launcher does not re-enable it behind their back, because that undoes a
    /// deliberate decision.
    ///
    /// The "already installed" checks run against the real 26.1.2 mods folder, which
    /// is only ever read.
    /// </remarks>
    public static class RequiredModTests
    {
        private const string Mc = "26.1.2";

        public static void Run()
        {
            Parsing();
            Selecting();
            Fingerprinting();
            Detecting();
            Declining();
        }

        private static RequiredMods.Entry Mod(
            string modid, string mc = Mc, string loader = "Fabric",
            string source = "modrinth", string? project = "proj", string? versionId = "v1",
            string? file = null, string? sha1 = null) =>
            new()
            {
                Minecraft = mc, Loader = loader, ModId = modid, Name = modid,
                SourceName = source, Project = project, VersionId = versionId,
                File = file, Sha1 = sha1
            };

        private static void Parsing()
        {
            Section("reading a published list");

            string json = """
                {
                  "entries": [
                    { "minecraft": "26.1.2", "loader": "Fabric", "modid": "sodium",
                      "name": "Sodium", "source": "modrinth", "project": "AANobbMI",
                      "version": "mc26.1.2-0.6.0", "why": "performance" },
                    { "minecraft": "26.1.2", "loader": "Fabric", "modid": "mymod",
                      "name": "My Mod", "source": "host", "file": "mymod-1.0.jar",
                      "sha1": "0123456789abcdef0123456789abcdef01234567" }
                  ]
                }
                """;

            var list = RequiredMods.Parse(json);
            Expect("both entries load", list.Entries.Count, 2);

            var sodium = list.Entries[0];
            Expect("the mod id is read", sodium.ModId, "sodium");
            Expect("the pinned version is read", sodium.VersionId, "mc26.1.2-0.6.0");
            Check("it reads as a Modrinth entry", sodium.From == RequiredMods.Source.Modrinth);
            Check("it is usable", sodium.Usable);
            Expect("the reason survives for the prompt", sodium.Why, "performance");

            var mine = list.Entries[1];
            Check("a host entry reads as host", mine.From == RequiredMods.Source.Host);
            Check("and is usable with a file and a hash", mine.Usable);

            // A list nobody can parse must not be able to stop someone playing.
            Check("rubbish parses to an empty list", RequiredMods.Parse("not json at all").Entries.Count == 0);
            Check("empty parses to an empty list", RequiredMods.Parse("").Entries.Count == 0);
            Check("null parses to an empty list", RequiredMods.Parse(null).Entries.Count == 0);
            Check("an unknown field is ignored rather than fatal",
                  RequiredMods.Parse("""{"entries":[],"somethingNew":42}""").Entries.Count == 0);

            Check("a round trip survives", RequiredMods.Parse(RequiredMods.ToJson(list)).Entries.Count == 2);
        }

        private static void Selecting()
        {
            Section("which entries apply");

            var list = new RequiredMods.Listing
            {
                Entries = new()
                {
                    Mod("sodium"),
                    Mod("lithium"),
                    Mod("createforge", loader: "NeoForge"),
                    Mod("oldmod", mc: "1.20.1"),
                    Mod(modid: ""),                                          // unusable
                    Mod("noproject", project: null),                         // unusable
                    Mod("hostnohash", source: "host", file: "x.jar", sha1: null, project: null)
                }
            };

            var fabric = list.For(Mc, "Fabric");
            Expect("only this version and loader", fabric.Count, 2);
            Check("and the right two",
                  fabric.Select(e => e.ModId).OrderBy(s => s).SequenceEqual(new[] { "lithium", "sodium" }),
                  string.Join(", ", fabric.Select(e => e.ModId)));

            // The reason the format is keyed on both: one mods folder is shared by
            // every loader on a Minecraft version.
            Check("a NeoForge entry is not handed to Fabric",
                  fabric.All(e => e.ModId != "createforge"));
            Check("another version's entry is not either",
                  fabric.All(e => e.ModId != "oldmod"));

            Expect("the NeoForge side sees its own", list.For(Mc, "NeoForge").Count, 1);
            Check("matching ignores case", list.For("26.1.2", "fabric").Count == 2);

            // A typo in the list is skipped, not thrown in front of the PLAY button.
            Check("an entry with no mod id is dropped", fabric.All(e => e.ModId.Length > 0));
            Check("a Modrinth entry with no project is dropped",
                  list.Entries.Single(e => e.ModId == "noproject").Usable == false);
            Check("a host entry with no hash is dropped",
                  list.Entries.Single(e => e.ModId == "hostnohash").Usable == false);

            Check("the versions named are listed for the admin view",
                  list.Versions().SequenceEqual(new[] { Mc, "1.20.1" }),
                  string.Join(", ", list.Versions()));
        }

        private static void Fingerprinting()
        {
            Section("knowing when the ask has changed");

            var a = new RequiredMods.Listing { Entries = new() { Mod("sodium"), Mod("lithium") } };
            var b = new RequiredMods.Listing { Entries = new() { Mod("lithium"), Mod("sodium") } };

            Expect("order does not change it", a.Fingerprint(Mc, "Fabric"), b.Fingerprint(Mc, "Fabric"));

            var pinnedDifferently = new RequiredMods.Listing
            {
                Entries = new() { Mod("sodium", versionId: "mc26.1.2-0.6.1"), Mod("lithium") }
            };
            Check("pinning a different build does change it",
                  a.Fingerprint(Mc, "Fabric") != pinnedDifferently.Fingerprint(Mc, "Fabric"));

            var oneMore = new RequiredMods.Listing
            {
                Entries = new() { Mod("sodium"), Mod("lithium"), Mod("ferritecore") }
            };
            Check("adding a mod changes it", a.Fingerprint(Mc, "Fabric") != oneMore.Fingerprint(Mc, "Fabric"));

            // The point of scoping it: editing one version must not re-ask everyone
            // who already said no about a different one.
            var unrelated = new RequiredMods.Listing
            {
                Entries = new() { Mod("sodium"), Mod("lithium"), Mod("something", mc: "1.20.1") }
            };
            Expect("another version's edits do not change it",
                   a.Fingerprint(Mc, "Fabric"), unrelated.Fingerprint(Mc, "Fabric"));

            Check("a different loader has its own fingerprint",
                  a.Fingerprint(Mc, "Fabric") != a.Fingerprint(Mc, "NeoForge"));

            Check("an empty ask still fingerprints", a.Fingerprint("no-such", "Fabric").Length > 0);
        }

        private static void Detecting()
        {
            Section("what is already installed");

            if (!LocalInstall.Available)
            {
                Skip("required mods: detection", "real installs not reachable");
                return;
            }

            string real = LocalInstall.At("versions", Mc, "mods");
            if (!Directory.Exists(real)) { Skip("required mods: detection", "no real mods folder"); return; }

            var ids = RequiredMods.ModIdsIn(real);
            Note($"{ids.Count} mod ids read out of the real {Mc} folder");
            Check("the real folder yields mod ids", ids.Count > 10, $"{ids.Count}");

            // Ids come from inside the jar, so they are not file names.
            Check("they are ids, not file names",
                  ids.All(i => !i.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)),
                  string.Join(", ", ids.Take(3)));

            // A copy of two real jars, one of them turned off, in a sandbox.
            string work = Path.Combine(Path.GetTempPath(), "mc-required-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);

            try
            {
                var jars = Directory.GetFiles(real, "*.jar").Take(2).ToList();
                if (jars.Count < 2) { Skip("required mods: disabled handling", "need two real jars"); return; }

                string onName = Path.GetFileName(jars[0]);
                string offName = Path.GetFileName(jars[1]) + ".disabled";
                File.Copy(jars[0], Path.Combine(work, onName));
                File.Copy(jars[1], Path.Combine(work, offName));

                string? onId = ModInspector.ModIdOf(jars[0]);
                string? offId = ModInspector.ModIdOf(jars[1]);
                if (onId is null || offId is null) { Skip("required mods: disabled handling", "jars have no id"); return; }

                var found = RequiredMods.ModIdsIn(work);
                Check("an enabled jar is seen", found.Contains(onId), onId);

                // The decision: a mod somebody switched off is still a mod they have,
                // and the launcher must not quietly turn it back on.
                Check("a turned-off jar is seen too", found.Contains(offId), offId);

                var wanted = new[] { Mod(onId), Mod(offId), Mod("definitely-not-installed") };
                var missing = RequiredMods.MissingIn(wanted, work);

                Expect("only the absent one is missing", missing.Count, 1);
                Expect("and it is the right one", missing[0].ModId, "definitely-not-installed");

                Check("nothing was renamed or removed",
                      File.Exists(Path.Combine(work, onName)) && File.Exists(Path.Combine(work, offName)));

                Expect("an empty folder makes everything missing",
                       RequiredMods.MissingIn(wanted, Path.Combine(work, "nope")).Count, 3);
                Expect("wanting nothing is never missing anything",
                       RequiredMods.MissingIn(Array.Empty<RequiredMods.Entry>(), work).Count, 0);

                // A host-served entry is the downgraded-mod case: a repack carries the
                // ORIGINAL mod's id, so matching on the id would see the upstream build
                // sitting there and call the requirement met. The hash is what decides.
                string onPath = Path.Combine(work, onName);
                string realSha1;
                using (var s = File.OpenRead(onPath))
                    realSha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(s));

                var hashes = RequiredMods.Sha1sIn(work);
                Check("the folder's hashes are read", hashes.Contains(realSha1), realSha1[..12]);

                var pinnedToThisJar = Mod(onId, source: "host", project: null,
                                          file: "whatever.jar", sha1: realSha1);
                Expect("a host entry matching the installed hash is satisfied",
                       RequiredMods.MissingIn(new[] { pinnedToThisJar }, work).Count, 0);

                var pinnedToAnother = Mod(onId, source: "host", project: null,
                                          file: "whatever.jar",
                                          sha1: new string('a', 40));
                Expect("the same mod id with a DIFFERENT hash is still missing",
                       RequiredMods.MissingIn(new[] { pinnedToAnother }, work).Count, 1);

                // The distinction that matters: Modrinth entries keep matching on id,
                // so somebody who updated a mod themselves is not fought with.
                Expect("a Modrinth entry is satisfied by any build of that mod",
                       RequiredMods.MissingIn(new[] { Mod(onId) }, work).Count, 0);
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }

        private static void Declining()
        {
            Section("remembering a no");

            string sandbox = Path.Combine(Path.GetTempPath(), "mc-decline-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);

            try
            {
                using (LocalInstall.RedirectBase(sandbox))
                {
                    Check("nothing is declined to begin with",
                          !RequiredMods.WasDeclined(Mc, "Fabric", "abc123"));

                    RequiredMods.RememberDecline(Mc, "Fabric", "abc123");
                    Check("a no is remembered", RequiredMods.WasDeclined(Mc, "Fabric", "abc123"));

                    // The whole reason the fingerprint exists.
                    Check("a changed ask is asked again",
                          !RequiredMods.WasDeclined(Mc, "Fabric", "different"));

                    Check("another loader is unaffected",
                          !RequiredMods.WasDeclined(Mc, "NeoForge", "abc123"));
                    Check("another version is unaffected",
                          !RequiredMods.WasDeclined("1.20.1", "Fabric", "abc123"));

                    // One answer per version+loader, not a growing log of every refusal.
                    RequiredMods.RememberDecline(Mc, "Fabric", "newer");
                    Check("the newer answer is held", RequiredMods.WasDeclined(Mc, "Fabric", "newer"));
                    Check("and replaces the old one", !RequiredMods.WasDeclined(Mc, "Fabric", "abc123"));

                    RequiredMods.ForgetDecline(Mc, "Fabric");
                    Check("forgetting clears it", !RequiredMods.WasDeclined(Mc, "Fabric", "newer"));

                    // It must survive being asked about when the file does not exist.
                    RequiredMods.ForgetDecline("never-declined", "Fabric");
                    Check("forgetting something unknown is harmless", true);

                    Check("the answer is kept beside the launcher, not in config.txt",
                          !File.Exists(Path.Combine(sandbox, "config.txt")));
                }

                Check("and it is scoped to the install",
                      !RequiredMods.WasDeclined(Mc, "Fabric", "newer"));
            }
            finally
            {
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }
    }
}
