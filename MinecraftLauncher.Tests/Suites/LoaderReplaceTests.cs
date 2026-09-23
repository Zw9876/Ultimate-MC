using System;
using System.IO;
using System.Linq;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Installing a Fabric loader must <b>replace</b> the one before it.
    /// </summary>
    /// <remarks>
    /// Leaving the old one behind was not merely untidy: the launch path picked a
    /// profile by file order, so the game could keep starting on the old loader while
    /// the launcher reported the new one as installed.
    ///
    /// The dangerous half is deleting libraries, because
    /// <c>versions/&lt;mc&gt;/libraries</c> also holds the vanilla game's — and most
    /// of a Fabric profile's list is shared with the loader it replaces. These run on
    /// a synthetic install so every one of those cases is present and nothing real is
    /// at risk.
    /// </remarks>
    public static class LoaderReplaceTests
    {
        private const string Mc = "1.99.9";

        private const string VanillaLib = "com.mojang:blocklist:1.0.10";
        private const string SharedLib  = "net.fabricmc:sponge-mixin:0.15.0";
        private const string OldOnlyLib = "net.fabricmc:fabric-loader:0.19.2";
        private const string NewOnlyLib = "net.fabricmc:fabric-loader:0.19.3";

        public static void Run()
        {
            string sandbox = Path.Combine(AppContext.BaseDirectory, "replacework");
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);

            try
            {
                using (LocalInstall.RedirectBase(sandbox))
                {
                    Build(sandbox);
                    PicksTheNewest();
                    Replaces(sandbox);
                }
            }
            finally
            {
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }

        /// <summary>A version folder with a vanilla profile and two Fabric loaders.</summary>
        private static void Build(string sandbox)
        {
            string versions = Path.Combine(sandbox, "versions", Mc, "versions");
            string libraries = Path.Combine(sandbox, "versions", Mc, "libraries");
            Directory.CreateDirectory(versions);

            Profile(Path.Combine(versions, $"{Mc}.json"), VanillaLib);
            Profile(Path.Combine(versions, $"fabric-loader-0.19.2-{Mc}.json"), OldOnlyLib, SharedLib);
            Profile(Path.Combine(versions, $"fabric-loader-0.19.3-{Mc}.json"), NewOnlyLib, SharedLib);

            foreach (string coordinate in new[] { VanillaLib, SharedLib, OldOnlyLib, NewOnlyLib })
            {
                string file = Path.Combine(libraries,
                    FabricMeta.MavenPath(coordinate).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "pretend jar for " + coordinate);
            }
        }

        private static void Profile(string path, params string[] coordinates)
        {
            string libs = string.Join(",", coordinates.Select(c => $"{{\"name\":\"{c}\"}}"));
            File.WriteAllText(path, $"{{\"id\":\"test\",\"libraries\":[{libs}]}}");
        }

        private static void PicksTheNewest()
        {
            Section("which loader the launcher will actually use");

            var installed = FabricLoaderUpdate.InstalledLoaders(Mc);
            Note($"installed: {string.Join(", ", installed)}");
            Expect("both loaders are seen", installed.Count, 2);

            // The bug: taking whichever file came first launched 0.19.2.
            string? newest = FabricLoaderUpdate.NewestProfile(Mc);
            Check("the newest profile is chosen, not the first",
                  newest is not null && Path.GetFileName(newest) == $"fabric-loader-0.19.3-{Mc}.json",
                  newest is null ? "(none)" : Path.GetFileName(newest));

            // And what the UI reports has to agree with what would launch.
            Expect("the reported loader agrees with it",
                   LoaderVersions.Detect(Mc, server: false, "Fabric").Version, "0.19.3");
        }

        private static void Replaces(string sandbox)
        {
            Section("installing 0.19.3 removes 0.19.2");

            string versions = Path.Combine(sandbox, "versions", Mc, "versions");
            string libraries = Path.Combine(sandbox, "versions", Mc, "libraries");

            string Lib(string coordinate) => Path.Combine(libraries,
                FabricMeta.MavenPath(coordinate).Replace('/', Path.DirectorySeparatorChar));

            var removed = FabricLoaderUpdate.RemoveOtherLoaders(Mc, "0.19.3", null);
            Note($"removed: {string.Join(", ", removed)}");

            Check("it reports removing the old profile", removed.Count == 1, string.Join(",", removed));

            Check("the old profile is gone",
                  !File.Exists(Path.Combine(versions, $"fabric-loader-0.19.2-{Mc}.json")));
            Check("the new profile is still there",
                  File.Exists(Path.Combine(versions, $"fabric-loader-0.19.3-{Mc}.json")));
            Expect("only one loader remains", FabricLoaderUpdate.InstalledLoaders(Mc).Count, 1);

            Check("the old loader's own jar is gone", !File.Exists(Lib(OldOnlyLib)));

            // The three that must survive. Getting any of these wrong breaks the game.
            Check("the new loader's jar is kept", File.Exists(Lib(NewOnlyLib)));
            Check("a library SHARED with the old loader is kept", File.Exists(Lib(SharedLib)));
            Check("the VANILLA game's library is untouched", File.Exists(Lib(VanillaLib)));

            Check("the emptied folder was pruned",
                  !Directory.Exists(Path.Combine(libraries, "net", "fabricmc", "fabric-loader", "0.19.2")));
            Check("but its parent survives, since the new loader lives there",
                  Directory.Exists(Path.Combine(libraries, "net", "fabricmc", "fabric-loader")));

            Section("running it again changes nothing");

            var second = FabricLoaderUpdate.RemoveOtherLoaders(Mc, "0.19.3", null);
            Expect("nothing left to remove", second.Count, 0);
            Check("everything that should exist still does",
                  File.Exists(Lib(NewOnlyLib)) && File.Exists(Lib(SharedLib)) && File.Exists(Lib(VanillaLib)));

            Section("keeping a loader that is not installed removes nothing");

            // Guards against a typo or a failed download wiping the only loader present.
            var reckless = FabricLoaderUpdate.RemoveOtherLoaders(Mc, "9.9.9", null);
            Note($"asked to keep a version that is not there: removed {reckless.Count}");
            Check("the installed loader is NOT deleted",
                  File.Exists(Path.Combine(versions, $"fabric-loader-0.19.3-{Mc}.json")),
                  "the last loader was removed");
        }
    }
}
