using System.IO;
using System.Linq;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Which loader version is installed, and whether the mods present can run on it.
    /// </summary>
    /// <remarks>
    /// Filtering by Minecraft version and loader family is not enough: a mod also
    /// declares the loader version it needs, and Modrinth's API does not expose that.
    /// The jar does. Both halves are checked here against the real installs.
    /// </remarks>
    public static class LoaderVersionTests
    {
        public static void Run()
        {
            Installed();
            Requirements();
            Sweep();
        }

        private static void Installed()
        {
            Section("what is actually installed here");

            if (!LocalInstall.Available) { Skip("loader detection", "real installs not reachable"); return; }

            void Detect(string label, string version, bool server, string loader, string expected)
            {
                var found = LoaderVersions.Detect(version, server, loader);
                Note($"{label,-24} {found.Version ?? "(none)",-10} from {found.Source}");
                Expect($"{label} is found", found.Version, expected);
            }

            // Not a literal: the client loader gets updated, and a test that has to be
            // edited every time that happens tells you nothing when it fails. The real
            // invariant is that the detector agrees with what is on disk — two separate
            // code paths reading the same folder.
            string installed = FabricLoaderUpdate.InstalledLoaders("26.1.2").FirstOrDefault() ?? "(none)";
            Detect("client 26.1.2 fabric", "26.1.2", false, "Fabric", installed);
            Detect("server 26.1.2 fabric", "26.1.2", true, "Fabric", "0.19.3");
            Detect("client 1.20.1 forge", "1.20.1", false, "Forge", "47.4.10");

            // The server's folder is "<mc>-<forge>"; only the second half is the loader.
            Detect("server 1.20.1 forge", "1.20.1", true, "Forge", "47.4.10");
            Detect("server 1.21.1 neoforge", "1.21.1", true, "NeoForge", "21.1.248");

            Check("vanilla reports nothing rather than guessing",
                  !LoaderVersions.Detect("1.20.1", false, "Vanilla").Known);
            Check("a version that is not installed reports nothing",
                  !LoaderVersions.Detect("9.9.9", false, "Fabric").Known);
        }

        private static void Requirements()
        {
            Section("what real mods ask for");

            string clientMods = Path.Combine(LocalInstall.Root, @"versions\26.1.2\mods");
            string forgeMods = Path.Combine(LocalInstall.Root, @"servers\forge-1.20.1\mods");
            string neoMods = Path.Combine(LocalInstall.Root, @"servers\neoforge-1.21.1\mods");

            string? fabricApi = Directory.Exists(clientMods)
                ? Directory.GetFiles(clientMods, "fabric-api-*.jar").FirstOrDefault() : null;

            if (fabricApi is null) { Skip("fabric requirement", "no fabric-api jar present"); }
            else
            {
                var need = ModInspector.RequiredLoaderVersion(fabricApi);
                Note($"fabric-api requires {need?.Requirement}");

                Check("its requirement is read", need is not null);
                Check("  the JSON escapes are decoded to >=", need!.Requirement.StartsWith(">="),
                      need.Requirement);
                Check("  it is not treated as a maven range", !need.IsMavenRange);
                Check("0.19.3 satisfies it", ModInspector.IsLoaderVersionOk(need, "0.19.3") == true);
                Check("0.18.4 satisfies it at the boundary",
                      ModInspector.IsLoaderVersionOk(need, "0.18.4") == true);
                Check("0.16.0 does NOT — the case this exists for",
                      ModInspector.IsLoaderVersionOk(need, "0.16.0") == false);
                Check("an unknown installed version says nothing",
                      ModInspector.IsLoaderVersionOk(need, null) is null);
            }

            string chunkyForge = Path.Combine(forgeMods, "Chunky-1.3.146.jar");
            if (!File.Exists(chunkyForge)) Skip("forge requirement", "Chunky not present");
            else
            {
                var need = ModInspector.RequiredLoaderVersion(chunkyForge);
                Note($"Chunky (Forge) requires {need?.Requirement}");
                Check("a Forge TOML range is read", need is not null && need.IsMavenRange,
                      need?.Requirement);
                Check("  Forge 47.4.10 satisfies it",
                      ModInspector.IsLoaderVersionOk(need, "47.4.10") == true);
                Check("  Forge 45 does not", ModInspector.IsLoaderVersionOk(need, "45") == false);
            }

            string chunkyNeo = Path.Combine(neoMods, "Chunky-NeoForge-1.4.23.jar");
            if (!File.Exists(chunkyNeo)) Skip("neoforge requirement", "Chunky not present");
            else
            {
                var need = ModInspector.RequiredLoaderVersion(chunkyNeo);
                Note($"Chunky (NeoForge) requires {need?.Requirement}");
                Check("a NeoForge TOML range is read", need is not null && need.IsMavenRange,
                      need?.Requirement);
                Check("  NeoForge 21.1.248 satisfies it",
                      ModInspector.IsLoaderVersionOk(need, "21.1.248") == true);
                Check("  NeoForge 20.4.0 does not",
                      ModInspector.IsLoaderVersionOk(need, "20.4.0") == false);
            }
        }

        private static void Sweep()
        {
            Section("every installed mod against its own installed loader");

            if (!LocalInstall.Available) { Skip("sweep", "real installs not reachable"); return; }

            void Check1(string label, string relative, string version, bool server, string loader)
            {
                string folder = Path.Combine(LocalInstall.Root, relative);
                if (!Directory.Exists(folder)) { Skip(label, "folder not present"); return; }

                var installed = LoaderVersions.Detect(version, server, loader);
                var mods = ModManager.List(folder);

                int ok = 0, tooOld = 0, silent = 0;
                foreach (var mod in mods)
                {
                    var need = ModInspector.RequiredLoaderVersion(mod.FullPath);
                    switch (ModInspector.IsLoaderVersionOk(need, installed.Version))
                    {
                        case true: ok++; break;
                        case false:
                            tooOld++;
                            Note($"NEEDS NEWER: {mod.DisplayName} wants {need!.Requirement}");
                            break;
                        default: silent++; break;
                    }
                }

                Note($"{label,-24} loader {installed.Version,-10} " +
                     $"{ok} ok, {tooOld} need newer, {silent} say nothing  (of {mods.Count})");

                Check($"{label}: nothing needs a newer loader than is installed",
                      tooOld == 0, $"{tooOld} would fail at startup");

                // The warning is the user-facing half of the same question.
                Check($"{label}: no loader-version warning is raised",
                      ModInspector.WarnAboutLoaderVersion(mods, loader, installed.Version) is null,
                      ModInspector.WarnAboutLoaderVersion(mods, loader, installed.Version));
            }

            Check1("client 26.1.2 fabric", @"versions\26.1.2\mods", "26.1.2", false, "Fabric");
            Check1("server 26.1.2 fabric", @"servers\fabric-26.1.2\mods", "26.1.2", true, "Fabric");
            Check1("client 1.20.1 forge", @"versions\1.20.1\mods", "1.20.1", false, "Forge");
            Check1("server 1.20.1 forge", @"servers\forge-1.20.1\mods", "1.20.1", true, "Forge");
            Check1("server 1.21.1 neoforge", @"servers\neoforge-1.21.1\mods", "1.21.1", true, "NeoForge");
        }
    }
}
