using System;
using System.IO;
using System.Linq;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Working out what a mod jar is for, and catching the two ways a mods folder
    /// becomes unstartable: the wrong loader's jars, and the same mod twice.
    /// </summary>
    /// <remarks>
    /// Run against the real mod folders on this machine rather than fixtures. Anything
    /// that changes files works on a temp copy — the real folders are read, never
    /// written.
    /// </remarks>
    public static class ModInspectorTests
    {
        private static readonly (string Label, string Folder, string Loader, ModLoaders Flag)[] RealFolders =
        {
            ("client 26.1.2 fabric",  @"versions\26.1.2\mods",          "FABRIC",   ModLoaders.Fabric),
            ("server 26.1.2 fabric",  @"servers\fabric-26.1.2\mods",    "FABRIC",   ModLoaders.Fabric),
            ("client 1.20.1 forge",   @"versions\1.20.1\mods",          "FORGE",    ModLoaders.Forge),
            ("server 1.20.1 forge",   @"servers\forge-1.20.1\mods",     "FORGE",    ModLoaders.Forge),
            ("server 1.21.1 neo",     @"servers\neoforge-1.21.1\mods",  "NEOFORGE", ModLoaders.NeoForge)
        };

        public static void Run()
        {
            Surveys();
            TheCrash();
            Rules();
            Labels();
            Junk();
            ModIds();
            SwitchingLoaders();
        }

        private static void Surveys()
        {
            Section("every real mod folder, against its own loader");

            foreach (var (label, relative, loader, flag) in RealFolders)
            {
                string folder = Path.Combine(LocalInstall.Root, relative);
                if (!Directory.Exists(folder)) { Skip(label, "folder not present"); continue; }

                var mods = ModManager.List(folder);
                int matching = mods.Count(m => (m.Loaders & flag) != ModLoaders.None);
                int unknown = mods.Count(m => m.Loaders == ModLoaders.None);
                int wrong = mods.Count(m => !ModInspector.IsCompatible(m.Loaders, loader));

                Note($"{label,-24} {mods.Count,3} jars — {matching} identify as {loader}, " +
                     $"{unknown} declare nothing, {wrong} incompatible");

                Check($"{label}: most jars identify as {loader}",
                      mods.Count > 0 && matching >= mods.Count * 0.7, $"{matching} of {mods.Count}");

                // The important half: no false alarm on a folder that is actually fine.
                Check($"{label}: no warning is raised",
                      ModInspector.WarnAbout(mods, loader) is null,
                      ModInspector.WarnAbout(mods, loader));
            }
        }

        private static void TheCrash()
        {
            Section("the crash this exists to prevent");

            string fabricClient = Path.Combine(LocalInstall.Root, @"versions\26.1.2\mods");
            string forgeServer = Path.Combine(LocalInstall.Root, @"servers\forge-1.20.1\mods");

            if (!Directory.Exists(fabricClient)) { Skip("wrong-loader warning", "no real mods"); return; }

            var fabricMods = ModManager.List(fabricClient);
            string? underNeoForge = ModInspector.WarnAbout(fabricMods, "NEOFORGE");

            Check("Fabric mods under NeoForge are flagged", underNeoForge is not null);
            if (underNeoForge is not null) Note($"\"{underNeoForge}\"");

            Check("the same mods under Fabric are not flagged",
                  ModInspector.WarnAbout(fabricMods, "FABRIC") is null,
                  ModInspector.WarnAbout(fabricMods, "FABRIC"));

            if (Directory.Exists(forgeServer))
            {
                var forgeMods = ModManager.List(forgeServer);
                Check("Forge mods under Fabric are flagged",
                      ModInspector.WarnAbout(forgeMods, "FABRIC") is not null);
                Check("Forge mods under Forge are not flagged",
                      ModInspector.WarnAbout(forgeMods, "FORGE") is null,
                      ModInspector.WarnAbout(forgeMods, "FORGE"));
            }
        }

        private static void Rules()
        {
            Section("compatibility rules");

            Check("vanilla accepts no mods", ModInspector.AcceptedBy("VANILLA") == ModLoaders.None);
            Check("fabric accepts quilt", ModInspector.IsCompatible(ModLoaders.Quilt, "FABRIC"));
            Check("fabric rejects forge", !ModInspector.IsCompatible(ModLoaders.Forge, "FABRIC"));
            Check("neoforge rejects a fabric-only jar",
                  !ModInspector.IsCompatible(ModLoaders.Fabric, "NEOFORGE"));
            Check("paper accepts plugins", ModInspector.IsCompatible(ModLoaders.Bukkit, "PAPER"));

            // NeoForge used mods.toml up to 1.20.1, so a jar carrying only that one
            // cannot be pinned to either and must not be guessed at.
            Check("an ambiguous mods.toml jar is allowed on Forge",
                  ModInspector.IsCompatible(ModLoaders.Forge | ModLoaders.NeoForge, "FORGE"));
            Check("an ambiguous mods.toml jar is allowed on NeoForge",
                  ModInspector.IsCompatible(ModLoaders.Forge | ModLoaders.NeoForge, "NEOFORGE"));

            // Plenty of legitimate library jars carry no descriptor; disabling those
            // would break working setups.
            Check("a jar declaring nothing is left alone",
                  ModInspector.IsCompatible(ModLoaders.None, "NEOFORGE"));
            Check("a multi-loader jar suits both",
                  ModInspector.IsCompatible(ModLoaders.Fabric | ModLoaders.NeoForge, "FABRIC") &&
                  ModInspector.IsCompatible(ModLoaders.Fabric | ModLoaders.NeoForge, "NEOFORGE"));
        }

        private static void Labels()
        {
            Section("labels");

            Expect("ambiguous reads as one label",
                   ModInspector.Describe(ModLoaders.Forge | ModLoaders.NeoForge), "Forge/NeoForge");
            Expect("nothing reads as a dash", ModInspector.Describe(ModLoaders.None), "—");
            Expect("fabric reads as Fabric", ModInspector.Describe(ModLoaders.Fabric), "Fabric");
        }

        private static void Junk()
        {
            Section("junk is survived, not crashed on");

            string tmp = Path.Combine(Path.GetTempPath(), "modinspector-junk");
            Directory.CreateDirectory(tmp);
            string fake = Path.Combine(tmp, "not-a-zip.jar");
            File.WriteAllText(fake, "this is plainly not a zip file");

            Check("a non-zip .jar is None, not an exception",
                  ModInspector.Detect(fake) == ModLoaders.None);
            Check("  and it has no mod id", ModInspector.ModIdOf(fake) is null);
            Check("  and no loader requirement", ModInspector.RequiredLoaderVersion(fake) is null);
            Check("a missing file is None",
                  ModInspector.Detect(Path.Combine(tmp, "nope.jar")) == ModLoaders.None);
            Check("an empty path is None", ModInspector.Detect("") == ModLoaders.None);

            Directory.Delete(tmp, true);
        }

        private static void ModIds()
        {
            Section("mod identity comes from inside the jar");

            string folder = Path.Combine(LocalInstall.Root, @"versions\26.1.2\mods");
            if (!Directory.Exists(folder)) { Skip("mod ids", "no real mods"); return; }

            var jars = Directory.GetFiles(folder, "*.jar");
            int named = jars.Count(j => ModInspector.ModIdOf(j) is not null);
            Note($"{named} of {jars.Length} jars state a mod id");
            Check("most real jars state a mod id", named >= jars.Length * 0.8, $"{named}/{jars.Length}");

            string? fabricApi = jars.FirstOrDefault(j => Path.GetFileName(j).StartsWith("fabric-api"));
            if (fabricApi is not null)
                Expect("fabric-api identifies itself", ModInspector.ModIdOf(fabricApi), "fabric-api");

            // Nothing in a healthy folder should look like a duplicate.
            Check("no duplicates in the real client folder",
                  ModInspector.WarnAboutDuplicates(ModManager.List(folder)) is null,
                  ModInspector.WarnAboutDuplicates(ModManager.List(folder)));
        }

        private static void SwitchingLoaders()
        {
            Section("turning the wrong ones off, and back on again");

            string fabric = Path.Combine(LocalInstall.Root, @"versions\26.1.2\mods");
            string neo = Path.Combine(LocalInstall.Root, @"servers\neoforge-1.21.1\mods");
            if (!Directory.Exists(fabric) || !Directory.Exists(neo))
            {
                Skip("loader switching", "no real mods to copy");
                return;
            }

            // A mixed folder, which is what the shared folder produces when someone
            // plays both loaders on one Minecraft version. Built from copies.
            string mixed = Path.Combine(Path.GetTempPath(), "modinspector-mixed");
            if (Directory.Exists(mixed)) Directory.Delete(mixed, true);
            Directory.CreateDirectory(mixed);

            foreach (string f in Directory.GetFiles(fabric, "*.jar").Take(6))
                File.Copy(f, Path.Combine(mixed, Path.GetFileName(f)));
            foreach (string f in Directory.GetFiles(neo, "*.jar").Take(5))
                File.Copy(f, Path.Combine(mixed, Path.GetFileName(f)));

            int total = ModManager.List(mixed).Count;
            Note($"{total} jars in the mixed folder");

            Check("the mixed folder warns under Fabric",
                  ModInspector.WarnAbout(ModManager.List(mixed), "FABRIC") is not null);

            var offForFabric = ModManager.DisableIncompatible(mixed, "FABRIC");
            Note($"turned off {offForFabric.Count} for Fabric");
            Check("something was turned off", offForFabric.Count > 0);
            Expect("nothing was deleted", ModManager.List(mixed).Count, total);
            Check("no warning remains under Fabric",
                  ModInspector.WarnAbout(ModManager.List(mixed), "FABRIC") is null,
                  ModInspector.WarnAbout(ModManager.List(mixed), "FABRIC"));
            Check("the Fabric ones are still enabled",
                  ModManager.List(mixed).Any(m => m.Enabled && m.Loaders.HasFlag(ModLoaders.Fabric)));

            var offForNeo = ModManager.DisableIncompatible(mixed, "NEOFORGE");
            var onForNeo = ModManager.EnableCompatible(mixed, "NEOFORGE");
            Note($"switching to NeoForge: {offForNeo.Count} off, {onForNeo.Count} back on");
            Check("switching turned the NeoForge ones back on", onForNeo.Count > 0);
            Expect("still nothing deleted", ModManager.List(mixed).Count, total);
            Check("no warning under NeoForge either",
                  ModInspector.WarnAbout(ModManager.List(mixed), "NEOFORGE") is null,
                  ModInspector.WarnAbout(ModManager.List(mixed), "NEOFORGE"));
            Check("and the Fabric-only ones are now off",
                  !ModManager.List(mixed).Any(m => m.Enabled && m.Loaders == ModLoaders.Fabric));

            ModManager.DisableIncompatible(mixed, "FABRIC");
            var backOn = ModManager.EnableCompatible(mixed, "FABRIC");
            Check("switching back restores the Fabric set", backOn.Count > 0, backOn.Count.ToString());
            Expect("every file survived the round trip", ModManager.List(mixed).Count, total);

            Directory.Delete(mixed, true);
        }
    }
}
