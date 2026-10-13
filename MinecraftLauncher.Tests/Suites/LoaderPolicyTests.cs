using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Keeping every machine's Fabric loader on the version the host pinned.
    /// </summary>
    /// <remarks>
    /// Three rules carry this feature, and all three are ways it could do harm rather
    /// than good:
    ///
    /// <list type="bullet">
    /// <item><b>Never add Fabric to a version that has none.</b> Mods for one Minecraft
    /// version share a folder whatever loader they are for, so a Fabric profile
    /// appearing next to somebody's Forge mods is a crash waiting for whoever picks
    /// it.</item>
    /// <item><b>Never downgrade.</b> A machine ahead of a stale pin must be left alone;
    /// pushing somebody backwards is worse than a mismatch. And the comparison has to
    /// be numeric — "0.19.10" is newer than "0.19.5" and sorts before it.</item>
    /// <item><b>Do nothing unless it is switched on.</b> Publishing a build must not be
    /// able to start installing loaders on twenty machines because a default was
    /// wrong.</item>
    /// </list>
    /// </remarks>
    public static class LoaderPolicyTests
    {
        public static void Run()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "mc-loader-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);

            try
            {
                // The sandbox redirect is scoped, NOT held for the whole method: with
                // `using var` it would still be in force inside RealInstall, which
                // would then inspect the sandbox while claiming to inspect the real
                // install -- passing, and proving nothing.
                using (LocalInstall.RedirectBase(sandbox))
                {
                    Build(sandbox);
                    Finding();
                    Comparing();
                    Storing();
                }

                RealInstall();
            }
            finally
            {
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }

        /// <summary>A sandbox shaped like a real install, with mixed loaders.</summary>
        private static void Build(string sandbox)
        {
            Section("a sandbox with mixed loaders");

            // 26.1.2: vanilla + Fabric 0.19.5, the real shape of this machine.
            string v26 = Path.Combine(sandbox, "versions", "26.1.2", "versions");
            Directory.CreateDirectory(v26);
            File.WriteAllText(Path.Combine(v26, "26.1.2.json"), "{}");
            File.WriteAllText(Path.Combine(v26, "fabric-loader-0.19.5-26.1.2.json"), "{}");

            // 1.20.1: vanilla + Forge, NO Fabric. The one that must be left alone.
            string v120 = Path.Combine(sandbox, "versions", "1.20.1", "versions");
            Directory.CreateDirectory(v120);
            File.WriteAllText(Path.Combine(v120, "1.20.1.json"), "{}");
            File.WriteAllText(Path.Combine(v120, "1.20.1-forge-47.4.10.json"), "{}");

            // A Fabric server on 26.1.2. Its loader lives inside server.jar, which is
            // the authoritative answer - the libraries folder keeps every loader the
            // server has ever had.
            string s26 = Path.Combine(sandbox, "servers", "fabric-26.1.2");
            Directory.CreateDirectory(s26);
            FakeServerJar(Path.Combine(s26, "server.jar"), "0.19.3");

            // A Forge server, which is none of this feature's business.
            Directory.CreateDirectory(Path.Combine(sandbox, "servers", "forge-1.20.1"));

            Expect("the client with Fabric is found", LoaderPolicy.FabricClients().Count, 1);
            Check("and it is 26.1.2 on 0.19.5",
                  LoaderPolicy.FabricClients().TryGetValue("26.1.2", out var c) && c == "0.19.5", c);

            // The rule this whole file exists for.
            Check("the Forge-only version is NOT reported as having Fabric",
                  !LoaderPolicy.FabricClients().ContainsKey("1.20.1"));

            Expect("the Fabric server is found", LoaderPolicy.FabricServers().Count, 1);
            Check("reading its loader from inside server.jar",
                  LoaderPolicy.FabricServers().TryGetValue("26.1.2", out var sv) && sv == "0.19.3", sv);
            Check("and the Forge server is ignored",
                  !LoaderPolicy.FabricServers().ContainsKey("1.20.1"));
        }

        /// <summary>
        /// A server jar carries its loader version in <c>install.properties</c>, which
        /// is where <c>FabricServerLoader</c> reads it from.
        /// </summary>
        private static void FakeServerJar(string path, string loaderVersion)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("install.properties");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write($"fabric-loader-version={loaderVersion}\ngame-version=26.1.2\n");
        }

        private static LoaderPolicy.Listing Policy(
            bool enabled, string mc, string loader, bool client = true, bool server = true) =>
            new()
            {
                Enabled = enabled,
                Targets = new()
                {
                    new LoaderPolicy.Target
                    {
                        Minecraft = mc, Loader = loader,
                        Client = client, Server = server, Pinned = DateTime.UtcNow
                    }
                }
            };

        private static void Finding()
        {
            Section("what this machine is behind on");

            // Switched off must mean switched off.
            Check("nothing happens while it is turned off",
                  LoaderPolicy.WhatIsBehind(Policy(false, "26.1.2", "0.20.0")).Count == 0);

            var behind = LoaderPolicy.WhatIsBehind(Policy(true, "26.1.2", "0.20.0"));
            Note(string.Join("; ", behind.Select(b => b.Describe())));

            Expect("both the client and the server are behind", behind.Count, 2);
            Check("the client is listed",
                  behind.Any(b => !b.Server && b.Have == "0.19.5" && b.Want == "0.20.0"));
            Check("and the server, which was further behind",
                  behind.Any(b => b.Server && b.Have == "0.19.3" && b.Want == "0.20.0"));
            Check("each says which it is", behind.All(b => b.Describe().Contains("client") ||
                                                           b.Describe().Contains("server")));

            // Already current.
            Check("nothing to do when already on the pinned version",
                  LoaderPolicy.WhatIsBehind(Policy(true, "26.1.2", "0.19.5"))
                              .All(b => b.Server));

            // A target for a version with no Fabric must install nothing. This is the
            // rule that keeps a Fabric profile out of somebody's Forge version.
            var forgeTarget = Policy(true, "1.20.1", "0.20.0");
            Expect("a target for a version with no Fabric does nothing",
                   LoaderPolicy.WhatIsBehind(forgeTarget).Count, 0);

            // A target naming only the server must not touch the client, and vice versa.
            var serverOnly = Policy(true, "26.1.2", "0.20.0", client: false, server: true);
            var fromServerOnly = LoaderPolicy.WhatIsBehind(serverOnly);
            Expect("a server-only target touches only the server", fromServerOnly.Count, 1);
            Check("and it is the server", fromServerOnly[0].Server);

            var clientOnly = Policy(true, "26.1.2", "0.20.0", client: true, server: false);
            var fromClientOnly = LoaderPolicy.WhatIsBehind(clientOnly);
            Expect("a client-only target touches only the client", fromClientOnly.Count, 1);
            Check("and it is the client", !fromClientOnly[0].Server);

            // A version nobody pinned is left alone.
            Expect("an unpinned version is untouched",
                   LoaderPolicy.WhatIsBehind(new LoaderPolicy.Listing { Enabled = true }).Count, 0);
        }

        private static void Comparing()
        {
            Section("never going backwards");

            // The pin is older than what is installed. Pushing somebody back is worse
            // than a mismatch, so this must find nothing to do for the client.
            var stale = LoaderPolicy.WhatIsBehind(Policy(true, "26.1.2", "0.19.1"));
            Check("a machine ahead of the pin is left alone",
                  stale.All(b => !b.Server), string.Join("; ", stale.Select(b => b.Describe())));

            // Numeric, not lexicographic. "0.19.10" sorts BEFORE "0.19.5" as text,
            // which would read a newer loader as older and trigger a downgrade -
            // the same class of bug as the version sort recorded in section 11.
            Check("0.19.10 is understood as newer than 0.19.5",
                  MavenVersion.Compare("0.19.10", "0.19.5") > 0);
            Check("so a pin of 0.19.10 is an upgrade from 0.19.5",
                  LoaderPolicy.WhatIsBehind(Policy(true, "26.1.2", "0.19.10"))
                              .Any(b => !b.Server));
            Check("and a pin of 0.19.5 is NOT an upgrade from 0.19.10",
                  MavenVersion.Compare("0.19.5", "0.19.10") < 0);
        }

        private static void Storing()
        {
            Section("on disk, and over the wire");

            var policy = Policy(true, "26.1.2", "0.19.5");
            policy.By = "HOST-PC";

            Check("it saves", LoaderPolicy.SaveLocal(policy));
            Check("beside the launcher", File.Exists(LoaderPolicy.FilePath));
            Check("with nothing half-written", !File.Exists(LoaderPolicy.FilePath + ".tmp"));

            var back = LoaderPolicy.LoadLocal();
            Check("it comes back on", back.Enabled);
            Expect("with its target", back.Targets.Count, 1);
            Expect("the loader version", back.For("26.1.2")?.Loader, "0.19.5");
            Expect("and who pinned it", back.By, "HOST-PC");
            Check("a version lookup is case-insensitive", back.For("26.1.2") is not null);
            Check("an unknown version has no target", back.For("1.17.1") is null);

            // A corrupt policy must not be able to start installing loaders, so it has
            // to read as disabled rather than as anything else.
            File.WriteAllText(LoaderPolicy.FilePath, "{ not json");
            var broken = LoaderPolicy.LoadLocal();
            Check("a corrupt policy reads as disabled", !broken.Enabled);
            Expect("with no targets", broken.Targets.Count, 0);
            Expect("so nothing is behind", LoaderPolicy.WhatIsBehind(broken).Count, 0);

            File.Delete(LoaderPolicy.FilePath);
            Check("a missing one is disabled too", !LoaderPolicy.LoadLocal().Enabled);

            // Off by default: a new file must never start changing people's loaders.
            Check("a brand new policy is off", !new LoaderPolicy.Listing().Enabled);

            // The cache, used when the host has gone away mid-evening.
            LoaderPolicy.SaveCache(policy);
            Check("the cache keeps the policy", LoaderPolicy.LoadCache().For("26.1.2") is not null);

            string summary = LoaderPolicy.Summarise(policy);
            Note(summary);
            Check("the summary names the version and the loader",
                  summary.Contains("26.1.2") && summary.Contains("0.19.5"), summary);

            var offPolicy = Policy(false, "26.1.2", "0.19.5");
            Check("and says plainly when it is off",
                  LoaderPolicy.Summarise(offPolicy).StartsWith("Turned off"),
                  LoaderPolicy.Summarise(offPolicy));
            Check("an empty one says what to press",
                  LoaderPolicy.Summarise(new LoaderPolicy.Listing()).Contains("CHECK FABRIC"));

            Check("rubbish parses to something harmless",
                  !LoaderPolicy.Parse("[]").Enabled);
        }

        private static void RealInstall()
        {
            Section("against the real install");

            if (!LocalInstall.Available)
            {
                Note("no versions/ folder here - skipped.");
                return;
            }

            // Read-only. The point is that the finders agree with what is on disk.
            var clients = LoaderPolicy.FabricClients();
            var servers = LoaderPolicy.FabricServers();

            Note("clients with Fabric: " + (clients.Count == 0 ? "none"
                 : string.Join(", ", clients.Select(c => $"{c.Key} on {c.Value}"))));
            Note("servers with Fabric: " + (servers.Count == 0 ? "none"
                 : string.Join(", ", servers.Select(s => $"{s.Key} on {s.Value}"))));

            foreach (var (mc, loader) in clients)
            {
                Check($"client {mc} reports a real loader version",
                      !string.IsNullOrWhiteSpace(loader) && loader.Contains('.'), loader);
                Check($"and it matches a profile on disk",
                      File.Exists(Path.Combine(Paths.VersionDir(mc), "versions",
                                               $"fabric-loader-{loader}-{mc}.json")));
            }

            foreach (var (mc, loader) in servers)
            {
                Check($"server {mc} reports a real loader version",
                      !string.IsNullOrWhiteSpace(loader) && loader.Contains('.'), loader);
                Check($"and its server.jar is there", FabricServerLoader.Exists(mc));
            }

            // Nothing here should ever claim a Forge or Vanilla version has Fabric.
            foreach (string dir in Directory.GetDirectories(Paths.Versions))
            {
                string mc = Path.GetFileName(dir);
                bool hasProfile = Directory
                    .GetFiles(Path.Combine(dir, "versions"), "fabric-loader*.json")
                    .Length > 0;

                Check($"{mc}: reported as Fabric only if a profile exists",
                      clients.ContainsKey(mc) == hasProfile);
            }
        }
    }
}
