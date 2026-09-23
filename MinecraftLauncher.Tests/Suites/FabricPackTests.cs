using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Carrying a Fabric loader to a machine that cannot download it.
    /// </summary>
    /// <remarks>
    /// Exports the real loader installed for 26.1.2, then imports it into a throwaway
    /// copy of that version. The real install is only ever read; everything written
    /// goes to a sandbox beside the test binary.
    /// </remarks>
    public static class FabricPackTests
    {
        private const string Mc = "26.1.2";
        private const string Loader = "0.19.3";

        public static async Task RunAsync()
        {
            if (!LocalInstall.Available) { Skip("fabric packs", "real installs not reachable"); return; }

            var ct = new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token;
            var quiet = new Progress<string>(_ => { });

            string work = Path.Combine(AppContext.BaseDirectory, "packwork");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            string zip = Path.Combine(work, "loader-pack.zip");

            Installed();
            var info = await Export(zip, quiet, ct);
            if (info is null) { Directory.Delete(work, true); return; }

            Inspecting(zip, work);
            await Importing(zip, work, quiet, ct);
            await Refusals(zip, work, quiet, ct);

            try { Directory.Delete(work, true); } catch { }
        }

        private static void Installed()
        {
            Section("what is installed");

            var loaders = FabricLoaderUpdate.InstalledLoaders(Mc);
            Note($"loaders present for {Mc}: {string.Join(", ", loaders)}");

            Check("the installed loader is listed", loaders.Contains(Loader), string.Join(",", loaders));
            Expect("nothing is listed for a version that is not installed",
                   FabricLoaderUpdate.InstalledLoaders("9.9.9").Count, 0);
        }

        private static async Task<FabricLoaderUpdate.PackInfo?> Export(
            string zip, IProgress<string> log, CancellationToken ct)
        {
            Section("making a pack from the real install");

            FabricLoaderUpdate.PackInfo info;
            try
            {
                info = await FabricLoaderUpdate.ExportAsync(Mc, Loader, zip, log, ct);
            }
            catch (Exception ex)
            {
                Check("a pack was written", false, ex.Message);
                return null;
            }

            Note(info.Describe());
            Check("a pack was written", File.Exists(zip));
            Expect("  it names the Minecraft version", info.MinecraftVersion, Mc);
            Expect("  it names the loader version", info.LoaderVersion, Loader);
            Check("  it holds more than just the profile", info.FileCount > 1, info.FileCount.ToString());

            using var check = ZipFile.OpenRead(zip);
            var names = check.Entries.Select(e => e.FullName).ToList();

            Check("  the profile is in it",
                  names.Contains($"versions/fabric-loader-{Loader}-{Mc}.json"),
                  string.Join(", ", names.Take(3)));
            Check("  libraries are in it", names.Any(n => n.StartsWith("libraries/")));
            Check("  a manifest marks it as ours", names.Contains(FabricLoaderUpdate.ManifestName));

            // A pack that swept in worlds or mods would be enormous and would overwrite
            // things on the receiving machine.
            Check("  no mods, worlds or game files were swept in",
                  names.All(n => n.StartsWith("versions/") || n.StartsWith("libraries/") ||
                                 n == FabricLoaderUpdate.ManifestName),
                  string.Join(", ", names.Where(n => !n.StartsWith("versions/") &&
                                                     !n.StartsWith("libraries/"))));
            return info;
        }

        private static void Inspecting(string zip, string work)
        {
            Section("reading a pack without unpacking it");

            var read = FabricLoaderUpdate.Inspect(zip);
            Check("a pack describes itself", read is not null);
            Expect("  same Minecraft version", read!.MinecraftVersion, Mc);
            Expect("  same loader version", read.LoaderVersion, Loader);

            string stranger = Path.Combine(work, "not-a-pack.zip");
            using (var z = ZipFile.Open(stranger, ZipArchiveMode.Create)) z.CreateEntry("hello.txt");
            Check("a stranger's zip is refused", FabricLoaderUpdate.Inspect(stranger) is null);

            string plain = Path.Combine(work, "plain.txt");
            File.WriteAllText(plain, "not a zip at all");
            Check("a file that is not a zip is refused", FabricLoaderUpdate.Inspect(plain) is null);
        }

        private static async Task Importing(
            string zip, string work, IProgress<string> log, CancellationToken ct)
        {
            Section("importing into a machine that does not have it");

            // A version with the game but no loader — the state an offline machine is in.
            string sandbox = Path.Combine(work, "sandbox");
            Directory.CreateDirectory(Path.Combine(sandbox, "versions", Mc, "versions"));
            File.WriteAllText(Path.Combine(sandbox, "versions", Mc, "versions", $"{Mc}.json"), "{}");

            using (LocalInstall.RedirectBase(sandbox))
            {
                Expect("the sandbox starts with no Fabric loader",
                       FabricLoaderUpdate.InstalledLoaders(Mc).Count, 0);
                Check("  and the detector agrees",
                      !LoaderVersions.Detect(Mc, false, "Fabric").Known);

                var imported = await FabricLoaderUpdate.ImportAsync(Mc, zip, log, ct);
                Expect("the import reports the loader it installed", imported.LoaderVersion, Loader);

                Check("the loader is now installed there",
                      FabricLoaderUpdate.InstalledLoaders(Mc).Contains(Loader),
                      string.Join(",", FabricLoaderUpdate.InstalledLoaders(Mc)));
                Expect("and the detector finds it",
                       LoaderVersions.Detect(Mc, false, "Fabric").Version, Loader);
            }

            // Byte-for-byte, or the loader will not run.
            string realProfile = LocalInstall.At("versions", Mc, "versions", $"fabric-loader-{Loader}-{Mc}.json");
            string copiedProfile = Path.Combine(sandbox, "versions", Mc, "versions", $"fabric-loader-{Loader}-{Mc}.json");
            Check("the profile arrived byte-for-byte",
                  File.ReadAllBytes(realProfile).SequenceEqual(File.ReadAllBytes(copiedProfile)));

            string copiedLibs = Path.Combine(sandbox, "versions", Mc, "libraries");
            var jars = Directory.Exists(copiedLibs)
                ? Directory.GetFiles(copiedLibs, "*.jar", SearchOption.AllDirectories)
                : Array.Empty<string>();

            Note($"libraries carried across: {jars.Length}");
            Check("the libraries came too", jars.Length > 0, jars.Length.ToString());

            int mismatched = jars.Count(copied =>
            {
                string relative = Path.GetRelativePath(copiedLibs, copied);
                string original = LocalInstall.At("versions", Mc, "libraries", relative);
                return !File.Exists(original) ||
                       !File.ReadAllBytes(original).SequenceEqual(File.ReadAllBytes(copied));
            });

            Check("every library matches the original byte-for-byte", mismatched == 0,
                  $"{mismatched} differ");
        }

        private static async Task Refusals(
            string zip, string work, IProgress<string> log, CancellationToken ct)
        {
            Section("refusals");

            string sandbox = Path.Combine(work, "sandbox");

            using (LocalInstall.RedirectBase(sandbox))
            {
                try
                {
                    await FabricLoaderUpdate.ImportAsync("1.20.1", zip, log, ct);
                    Check("a pack for the wrong Minecraft version is refused", false, "it was accepted");
                }
                catch (InvalidDataException ex)
                {
                    Check("a pack for the wrong Minecraft version is refused", true);
                    Note($"\"{ex.Message}\"");
                }
                catch (DirectoryNotFoundException)
                {
                    // Also a refusal, for the same reason: that version is not here.
                    Check("a pack for the wrong Minecraft version is refused", true);
                }

                string stranger = Path.Combine(work, "not-a-pack.zip");
                try
                {
                    await FabricLoaderUpdate.ImportAsync(Mc, stranger, log, ct);
                    Check("a stranger's zip is refused on import", false, "it was accepted");
                }
                catch (InvalidDataException) { Check("a stranger's zip is refused on import", true); }

                try
                {
                    await FabricLoaderUpdate.ExportAsync(Mc, "0.0.0-nope", zip + ".x", log, ct);
                    Check("exporting a loader that is not installed is refused", false, "it was accepted");
                }
                catch (FileNotFoundException)
                {
                    Check("exporting a loader that is not installed is refused", true);
                }

                // Nothing in a supplied file gets to choose where it lands.
                string evil = Path.Combine(work, "evil.zip");
                using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
                {
                    var manifest = z.CreateEntry(FabricLoaderUpdate.ManifestName);
                    using (var w = new StreamWriter(manifest.Open()))
                        w.Write($"{{\"minecraft\":\"{Mc}\",\"loader\":\"9.9.9\",\"files\":1}}");

                    var bad = z.CreateEntry("../../../escaped.txt");
                    using (var w = new StreamWriter(bad.Open())) w.Write("should never be written");
                }

                string escaped = Path.GetFullPath(Path.Combine(sandbox, "..", "..", "..", "escaped.txt"));
                if (File.Exists(escaped)) File.Delete(escaped);

                await FabricLoaderUpdate.ImportAsync(Mc, evil, log, ct);
                Check("a pack cannot write outside the version folder", !File.Exists(escaped),
                      "a traversing entry escaped");
            }
        }
    }
}
