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
    /// Carrying a Fabric <b>server's</b> loader to a machine that cannot download it.
    /// </summary>
    /// <remarks>
    /// MAKE A PACK produced a client pack whatever you were looking at, so servers were
    /// never updated this way and drifted: the real server here had loaders 0.19.2 and
    /// 0.19.3 stacked up while its clients ran 0.19.5.
    ///
    /// The checks run against that real server, which is only ever read. Everything
    /// written goes to a sandbox, reached by pointing <c>Paths</c> at it — the same
    /// trick the client pack suite uses.
    ///
    /// No loader version is written into this file. Hardcoding one is what broke two
    /// suites the day the real loader moved, with failures that said nothing about the
    /// code they covered.
    /// </remarks>
    public static class FabricServerPackTests
    {
        private const string Mc = "26.1.2";

        public static async Task RunAsync()
        {
            if (!LocalInstall.Available) { Skip("fabric server packs", "real installs not reachable"); return; }
            if (!FabricServerLoader.Exists(Mc)) { Skip("fabric server packs", $"no Fabric server for {Mc}"); return; }

            var ct = new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token;
            var quiet = new Progress<string>(_ => { });

            string work = Path.Combine(AppContext.BaseDirectory, "serverpackwork");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            string zip = Path.Combine(work, "server-loader-pack.zip");

            try
            {
                string active = Reading();
                if (active.Length == 0) return;

                await Exporting(zip, quiet, ct);
                await Importing(zip, work, quiet, ct);
                await Refusals(zip, work, quiet, ct);
                Pruning(work);
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { }
            }
        }

        private static string Reading()
        {
            Section("what the server says it runs");

            string? active = FabricServerLoader.InstalledVersion(Mc);

            Check("the loader version comes off the server jar", active is not null,
                  "install.properties not readable");
            if (active is null) return "";

            Note($"server.jar reports Fabric loader {active}");

            Check("it looks like a version", active.Length > 0 && char.IsDigit(active[0]), active);

            // The authoritative answer must also be one the server actually has files
            // for, or the server would not start.
            string libs = Path.Combine(FabricServerLoader.Dir(Mc),
                                       "libraries", "net", "fabricmc", "fabric-loader", active);
            Check("the libraries for that version are present", Directory.Exists(libs), libs);

            var all = FabricServerLoader.InstalledLoaders(Mc);
            Note($"loaders with libraries here: {string.Join(", ", all)}");

            Check("the one in use is listed first", all.Count > 0 && all[0] == active,
                  all.Count > 0 ? all[0] : "none");

            // The bug this replaced: reporting whichever folder sorted newest rather
            // than the one the jar names.
            Check("Detect agrees with the jar",
                  LoaderVersions.Detect(Mc, server: true, "Fabric").Version == active,
                  LoaderVersions.Detect(Mc, server: true, "Fabric").Version ?? "null");

            return active;
        }

        private static async Task Exporting(string zip, IProgress<string> log, CancellationToken ct)
        {
            Section("making a server pack");

            var info = await FabricServerLoader.ExportAsync(Mc, zip, log, ct);

            Check("a pack was written", File.Exists(zip));
            Check("it is marked as a server pack", info.ForServer);
            Check("it names the server's loader", info.LoaderVersion == FabricServerLoader.InstalledVersion(Mc),
                  info.LoaderVersion);
            Check("it says so when described", info.Describe().Contains("server"), info.Describe());

            using var archive = ZipFile.OpenRead(zip);
            var names = archive.Entries.Select(e => e.FullName).ToList();

            Check("it carries server.jar, which is what selects the loader",
                  names.Contains("server.jar"));
            Check("it carries the libraries",
                  names.Any(n => n.StartsWith("libraries/", StringComparison.Ordinal)));
            Check("it carries a manifest", names.Contains(FabricLoaderUpdate.ManifestName));

            // Minecraft itself is not redistributable and does not change with the
            // loader, so a pack that swept it up would be both illegal to pass around
            // and needlessly enormous.
            Check("it leaves the Minecraft server jar out",
                  !names.Any(n => n.StartsWith("versions/", StringComparison.Ordinal)),
                  names.FirstOrDefault(n => n.StartsWith("versions/", StringComparison.Ordinal)) ?? "");

            // Neither are the worlds, the mods or the configs.
            foreach (string unwanted in new[] { "world/", "mods/", "config/", ".fabric/" })
                Check($"it leaves {unwanted} out",
                      !names.Any(n => n.StartsWith(unwanted, StringComparison.Ordinal)));
        }

        private static async Task Importing(string zip, string work, IProgress<string> log, CancellationToken ct)
        {
            Section("installing it somewhere else");

            string sandbox = Path.Combine(work, "target");
            string serverDir = Path.Combine(sandbox, "servers", $"fabric-{Mc}");
            Directory.CreateDirectory(serverDir);

            // A server that exists but runs something else, so the import has something
            // to replace rather than landing on empty ground.
            File.WriteAllText(Path.Combine(serverDir, "server.jar"), "the old launcher");
            File.WriteAllText(Path.Combine(serverDir, "server.properties"), "motd=leave me alone");

            string stale = Path.Combine(serverDir, "libraries", "net", "fabricmc", "fabric-loader", "0.0.1-stale");
            Directory.CreateDirectory(stale);
            File.WriteAllText(Path.Combine(stale, "old.jar"), "an older loader");

            using (LocalInstall.RedirectBase(sandbox))
            {
                var info = await FabricServerLoader.ImportAsync(Mc, zip, log, ct);

                Check("it reports the loader it installed", info.LoaderVersion.Length > 0, info.LoaderVersion);

                string jar = Path.Combine(serverDir, "server.jar");
                Check("server.jar was replaced", File.ReadAllText(jar) != "the old launcher");
                Check("it is a real jar now",
                      new FileInfo(jar).Length > 1000 && File.ReadAllBytes(jar)[0] == (byte)'P');

                Check("the loader libraries arrived",
                      Directory.Exists(Path.Combine(serverDir, "libraries", "net", "fabricmc",
                                                    "fabric-loader", info.LoaderVersion)));

                Check("the server now reports the new loader",
                      FabricServerLoader.InstalledVersion(Mc) == info.LoaderVersion,
                      FabricServerLoader.InstalledVersion(Mc) ?? "null");

                // Installing replaces. Leaving the old one is how a server ends up
                // with several and no way to tell which is live.
                Check("the loader it replaced was removed", !Directory.Exists(stale));

                // ...but only the loader.
                Check("server.properties was left alone",
                      File.ReadAllText(Path.Combine(serverDir, "server.properties")) == "motd=leave me alone");
            }
        }

        private static async Task Refusals(string serverZip, string work, IProgress<string> log, CancellationToken ct)
        {
            Section("packs it should refuse");

            string sandbox = Path.Combine(work, "refuse");
            string serverDir = Path.Combine(sandbox, "servers", $"fabric-{Mc}");
            Directory.CreateDirectory(serverDir);
            File.WriteAllText(Path.Combine(serverDir, "server.jar"), "placeholder");

            // A client pack made from the real client, to prove the two are not
            // interchangeable — unpacking one over the other leaves a server that
            // cannot start and no clue why.
            string clientLoader = FabricLoaderUpdate.InstalledLoaders(Mc).FirstOrDefault() ?? "";
            if (clientLoader.Length > 0)
            {
                string clientZip = Path.Combine(work, "client-pack.zip");
                await FabricLoaderUpdate.ExportAsync(Mc, clientLoader, clientZip, log, ct);

                Check("a client pack reads as a client pack",
                      FabricLoaderUpdate.Inspect(clientZip)?.ForServer == false);

                using (LocalInstall.RedirectBase(sandbox))
                {
                    await Refuses("a client pack is refused by the server",
                                  () => FabricServerLoader.ImportAsync(Mc, clientZip, log, ct));
                }

                await Refuses("a server pack is refused by the client",
                              () => FabricLoaderUpdate.ImportAsync(Mc, serverZip, log, ct));
            }
            else
            {
                Skip("cross-target refusals", "no client loader installed to make a pack from");
            }

            // A pack claiming another Minecraft version would install libraries that do
            // not match the game.
            string wrong = Path.Combine(work, "wrong-version.zip");
            using (var zip = ZipFile.Open(wrong, ZipArchiveMode.Create))
            {
                FabricLoaderUpdate.WriteManifest(zip, "1.20.1", "0.16.0", 1, forServer: true);
                zip.CreateEntry("server.jar");
            }

            using (LocalInstall.RedirectBase(sandbox))
            {
                await Refuses("a pack for another Minecraft version is refused",
                              () => FabricServerLoader.ImportAsync(Mc, wrong, log, ct));
            }

            // Nothing in a supplied file gets to choose where it lands.
            string escaping = Path.Combine(work, "escaping.zip");
            using (var zip = ZipFile.Open(escaping, ZipArchiveMode.Create))
            {
                FabricLoaderUpdate.WriteManifest(zip, Mc, "9.9.9", 1, forServer: true);
                zip.CreateEntry("../../pwned.txt");
            }

            using (LocalInstall.RedirectBase(sandbox))
            {
                await Refuses("a pack writing outside the server folder is refused",
                              () => FabricServerLoader.ImportAsync(Mc, escaping, log, ct));
            }

            Check("nothing escaped", !File.Exists(Path.Combine(work, "pwned.txt")) &&
                                     !File.Exists(Path.Combine(sandbox, "pwned.txt")));
        }

        private static void Pruning(string work)
        {
            Section("removing the loaders it replaced");

            string sandbox = Path.Combine(work, "prune");
            string loaderDir = Path.Combine(sandbox, "servers", $"fabric-{Mc}",
                                            "libraries", "net", "fabricmc", "fabric-loader");

            foreach (string v in new[] { "0.19.2", "0.19.3", "0.19.5" })
                Directory.CreateDirectory(Path.Combine(loaderDir, v));

            using (LocalInstall.RedirectBase(sandbox))
            {
                var removed = FabricServerLoader.RemoveOtherLoaders(Mc, "0.19.5", null);

                Check("the others are gone", removed.Count == 2, $"removed {removed.Count}");
                Check("the one in use is kept", Directory.Exists(Path.Combine(loaderDir, "0.19.5")));
                Check("0.19.2 went", !Directory.Exists(Path.Combine(loaderDir, "0.19.2")));

                // The client side once deleted the only loader present when asked to
                // keep one that was not installed. Nothing is removed until the
                // replacement is really there.
                var none = FabricServerLoader.RemoveOtherLoaders(Mc, "0.99.0-not-here", null);
                Check("it removes nothing when the version to keep is absent", none.Count == 0);
                Check("and the real one survives", Directory.Exists(Path.Combine(loaderDir, "0.19.5")));
            }
        }

        private static async Task Refuses(string what, Func<Task> action)
        {
            try
            {
                await action();
                Check(what, false, "it was accepted");
            }
            catch (InvalidDataException)
            {
                Check(what, true);
            }
            catch (Exception ex)
            {
                Check(what, false, $"threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
