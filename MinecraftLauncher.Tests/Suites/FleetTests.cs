using System;
using System.IO;
using System.Linq;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// The host's view of the other machines, and the crash reports they send it.
    /// </summary>
    /// <remarks>
    /// This exists because of a documented, repeated failure rather than a hypothetical
    /// one: five builds in a row were published and never confirmed deployed, and every
    /// handoff had to write down that the fleet state was unknown. The checks below are
    /// mostly about not making that worse — a roster that claims a machine is behind
    /// when it is not, or that forgets last night's username because somebody opened
    /// the launcher this morning, would be consulted once and then ignored.
    /// </remarks>
    public static class FleetTests
    {
        public static void Run()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "mc-fleet-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);

            try
            {
                using var scope = LocalInstall.RedirectBase(sandbox);

                Reporting();
                Merging();
                Counting();
                Storing();
                Inbox();
                SendingOnce(sandbox);
            }
            finally
            {
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }

        private static FleetRoster.CheckIn Report(
            string machine, string? user = null, string? launcher = null,
            string? mc = null, string? loader = null, bool playing = false) =>
            new()
            {
                Machine = machine, Username = user, Launcher = launcher,
                Minecraft = mc, Loader = loader, Playing = playing
            };

        private static void Reporting()
        {
            Section("what a machine says about itself");

            var config = new AppConfig { Username = "Zach" };
            var report = FleetRoster.Describe(config, "26.1.2", "Fabric", playing: true);

            Check("the machine names itself", report.Machine == FleetRoster.ThisMachine &&
                                              report.Machine.Length > 0, report.Machine);
            Expect("the username is the one they play under", report.Username, "Zach");
            Check("the launcher version is its own", report.Launcher ==
                  LauncherPackage.CurrentVersion.ToString(), report.Launcher);
            Expect("the version being started", report.Minecraft, "26.1.2");
            Check("and that this was a real launch", report.Playing);

            // Nobody has typed a username yet. An empty string would show as a blank
            // column; null means "did not say", which is what Merge leaves alone.
            var blank = FleetRoster.Describe(new AppConfig { Username = "  " }, null, null, false);
            Check("an unset username is absent rather than empty", blank.Username is null);
            Check("so is an unset version", blank.Minecraft is null);

            string json = FleetRoster.ToJson(report);
            var back = FleetRoster.ParseCheckIn(json);
            Check("a report survives the round trip", back is not null);
            Expect("with the username", back!.Username, "Zach");
            Check("and the playing flag", back.Playing);

            Check("rubbish is not a check-in", FleetRoster.ParseCheckIn("{ not json") is null);
            Check("neither is a report with no machine",
                  FleetRoster.ParseCheckIn("""{"username":"Zach"}""") is null);
        }

        private static void Merging()
        {
            Section("folding reports into the roster");

            var listing = new FleetRoster.Listing();
            var monday = new DateTime(2026, 10, 5, 19, 0, 0, DateTimeKind.Utc);

            FleetRoster.Merge(listing,
                Report("DESKTOP-A", "Zach", "1.2.276.353", "26.1.2", "Fabric", playing: true),
                "192.168.1.20", monday);

            Expect("the machine is added", listing.Machines.Count, 1);
            var machine = listing.Machines[0];
            Expect("under its computer name", machine.Name, "DESKTOP-A");
            Expect("with who was playing", machine.Username, "Zach");
            Expect("and where it was", machine.Address, "192.168.1.20");
            Expect("and when it played", machine.Played, monday);

            // The same computer, next morning, launcher opened but no game started.
            var tuesday = monday.AddHours(14);
            FleetRoster.Merge(listing, Report("DESKTOP-A", "Zach", "1.2.276.353"), "192.168.1.20", tuesday);

            Expect("the same machine is not duplicated", listing.Machines.Count, 1);
            Expect("it was seen this morning", machine.Seen, tuesday);

            // The two rules that make the list trustworthy.
            Expect("but opening the launcher is not playing", machine.Played, monday);
            Expect("and last night's version is not forgotten", machine.Minecraft, "26.1.2");
            Expect("nor the loader", machine.Loader, "Fabric");

            // A different person sits down at the same computer. The username moves,
            // because "whose machine is that" is the question the column answers.
            FleetRoster.Merge(listing,
                Report("DESKTOP-A", "Steve", "1.2.276.353", "26.1.2", "Fabric", playing: true),
                "192.168.1.20", tuesday.AddHours(2));

            Expect("a new person at the same machine replaces the name", machine.Username, "Steve");
            Expect("still one machine", listing.Machines.Count, 1);

            // Case should not create a second row: Windows does not distinguish them.
            FleetRoster.Merge(listing, Report("desktop-a", launcher: "1.2.276.353"), null, tuesday);
            Expect("machine names are matched without case", listing.Machines.Count, 1);

            // A report with no machine name at all still has to go somewhere.
            FleetRoster.Merge(listing, Report("", launcher: "1.2.265.376"), "192.168.1.33", tuesday);
            Check("a nameless report falls back to its address",
                  listing.Machines.Any(m => m.Name == "192.168.1.33"));

            Check("the roster reads newest-seen first",
                  FleetRoster.Sorted(listing)[0].Name == "DESKTOP-A");
        }

        private static void Counting()
        {
            Section("is the fleet current?");

            var host = new Version(1, 2, 276, 353);
            var listing = new FleetRoster.Listing();
            var now = DateTime.UtcNow;

            FleetRoster.Merge(listing, Report("ON-CURRENT", "A", "1.2.276.353"), null, now);
            FleetRoster.Merge(listing, Report("BEHIND",     "B", "1.2.265.376"), null, now);
            FleetRoster.Merge(listing, Report("ANCIENT",    "C", "1.2.261.100"), null, now);
            FleetRoster.Merge(listing, Report("SILENT",     "D"),                null, now);

            Expect("two machines are behind", FleetRoster.Behind(listing, host), 2);

            // A machine on a newer build is not behind. This happens whenever a build
            // is tried somewhere before the host gets it, and counting it would make
            // the one number anybody reads wrong.
            FleetRoster.Merge(listing, Report("AHEAD", "E", "1.2.277.1"), null, now);
            Expect("one on a newer build is not counted as behind",
                   FleetRoster.Behind(listing, host), 2);

            // Nor is one that did not say — unknown is not the same as old.
            Check("a machine that did not report a version is not called behind",
                  listing.Machines.Single(m => m.Name == "SILENT").Version == new Version(0, 0, 0, 0));

            string summary = FleetRoster.Summarise(listing, host);
            Note(summary);
            Check("the summary counts the machines", summary.Contains("5 machines"), summary);
            Check("and says how many are behind", summary.Contains("2 behind"), summary);
            Check("and admits what it does not know", summary.Contains("did not say"), summary);

            var allGood = new FleetRoster.Listing();
            FleetRoster.Merge(allGood, Report("A", "A", "1.2.276.353"), null, now);
            Check("a current fleet says so plainly",
                  FleetRoster.Summarise(allGood, host).Contains("all on"),
                  FleetRoster.Summarise(allGood, host));

            Check("an empty roster explains itself rather than claiming success",
                  FleetRoster.Summarise(new FleetRoster.Listing(), host).Contains("checked in"),
                  FleetRoster.Summarise(new FleetRoster.Listing(), host));

            // Staleness only ever greys a row; it is never an alarm, because a
            // switched-off computer is not a problem.
            var old = new FleetRoster.Listing();
            FleetRoster.Merge(old, Report("GONE", "X", "1.2.276.353"), null, now - TimeSpan.FromDays(30));
            Check("a machine not seen for a month reads as stale", old.Machines[0].IsStale);
            Check("but is still not counted as behind", FleetRoster.Behind(old, host) == 0);

            Expect("how long ago is said in words", FleetRoster.Describe(TimeSpan.FromSeconds(5)), "just now");
            Expect("in minutes", FleetRoster.Describe(TimeSpan.FromMinutes(12)), "12 min ago");
            Expect("in hours", FleetRoster.Describe(TimeSpan.FromHours(5)), "5 h ago");
            Expect("yesterday", FleetRoster.Describe(TimeSpan.FromHours(30)), "yesterday");
            Expect("and in days", FleetRoster.Describe(TimeSpan.FromDays(9)), "9 days ago");
        }

        private static void Storing()
        {
            Section("the roster on disk");

            var listing = new FleetRoster.Listing();
            var now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
            FleetRoster.Merge(listing, Report("DESKTOP-B", "Alex", "1.2.276.353", "26.1.2", "Fabric"), "10.0.0.5", now);

            Check("it saves", FleetRoster.Save(listing));
            Check("beside the launcher", File.Exists(FleetRoster.FilePath), FleetRoster.FilePath);
            Check("and nothing is left half-written", !File.Exists(FleetRoster.FilePath + ".tmp"));

            var loaded = FleetRoster.Load();
            Expect("it loads back", loaded.Machines.Count, 1);
            Expect("with the username", loaded.Machines[0].Username, "Alex");
            Expect("the build", loaded.Machines[0].Launcher, "1.2.276.353");
            Expect("and the time it was seen", loaded.Machines[0].Seen, now);

            Check("a machine can be forgotten", FleetRoster.Forget("DESKTOP-B"));
            Expect("and is gone", FleetRoster.Load().Machines.Count, 0);
            Check("forgetting one that was never there is not an error", !FleetRoster.Forget("NOBODY"));

            // Losing this file must never stop a launcher starting, so corruption reads
            // as empty rather than throwing.
            File.WriteAllText(FleetRoster.FilePath, "{ this is not json");
            Expect("a corrupt roster reads as empty", FleetRoster.Load().Machines.Count, 0);

            File.Delete(FleetRoster.FilePath);
            Expect("so does a missing one", FleetRoster.Load().Machines.Count, 0);
        }

        private static void Inbox()
        {
            Section("crash reports arriving from other machines");

            // Shortened from a real Fabric crash report; the parser this feeds is
            // covered against captured output in the crashes suite.
            const string Crash = """
                ---- Minecraft Crash Report ----
                // Who set us up the TNT?

                Time: 2026-10-07 20:14:03
                Description: Initializing game

                java.lang.RuntimeException: Could not execute entrypoint
                	at net.fabricmc.loader.impl.FabricLoaderImpl.invokeEntrypoints(FabricLoaderImpl.java:120)

                -- MOD immersive_portals --
                Details:
                	Mod File: /mods/immersively-vibed-portals-6.1.0.jar
                	Failure message: Mod resolution failed
                		Requires fabric-api 0.154.2 or later

                -- System Details --
                Details:
                	Minecraft Version: 26.1.2
                	Java Version: 25.0.1, Oracle Corporation
                """;

            var from = new CrashInbox.Sender
            {
                Machine = "DESKTOP-C", Username = "Steve",
                Minecraft = "26.1.2", File = "crash-2026-10-07_20.14.03-client.txt"
            };

            var arrived = CrashInbox.Receive(from, Crash);

            Check("the report is stored", arrived is not null);
            Check("the text lands on disk", File.Exists(arrived!.TextPath));
            Check("with a sidecar saying who sent it",
                  File.Exists(arrived.TextPath[..^4] + ".json"));

            // Byte-for-byte, so it reads here exactly as it read there and so anything
            // pasted into a mod's issue tracker is the real thing.
            Expect("stored exactly as the game wrote it", File.ReadAllText(arrived.TextPath), Crash);

            Check("the host knows who to ask about it", arrived.Who.Contains("Steve") &&
                                                        arrived.Who.Contains("DESKTOP-C"), arrived.Who);
            Check("and it is parsed the same way a local one is",
                  arrived.Report?.MinecraftVersion == "26.1.2", arrived.Report?.MinecraftVersion);
            Check("including which mod did it",
                  arrived.Report!.Mods.Any(m => m.Id == "immersive_portals"),
                  string.Join(", ", arrived.Report.Mods.Select(m => m.Id)));

            // Nothing the sender provides becomes a path. This is the one route that
            // lets another machine put bytes on the host's disk unasked.
            var hostile = new CrashInbox.Sender
            {
                Machine = @"..\..\Windows", Username = @"../../../etc/passwd", File = @"C:\boot.ini"
            };

            var stored = CrashInbox.Receive(hostile, Crash + "\n// different content");
            Check("a sender cannot choose where their report is written", stored is not null);
            Check("it stays inside the inbox",
                  Path.GetFullPath(stored!.TextPath)
                      .StartsWith(Path.GetFullPath(CrashInbox.Folder), StringComparison.OrdinalIgnoreCase),
                  stored.TextPath);
            Check("and the name has no traversal left in it",
                  !Path.GetFileName(stored.TextPath).Contains(".."),
                  Path.GetFileName(stored.TextPath));

            // A client that crashed, was switched off before it could say so, and
            // reports the same crash tomorrow is behaving correctly.
            Check("the same report twice is stored once", CrashInbox.Receive(from, Crash) is null);
            Expect("so the inbox holds two", CrashInbox.All().Count, 2);

            Check("empty reports are refused", CrashInbox.Receive(from, "   ") is null);
            Check("the inbox reads newest first",
                  CrashInbox.All()[0].From.Received >= CrashInbox.All()[1].From.Received);

            // Pruning, so a long evening of crashes cannot fill the host's disk.
            for (int i = 0; i < 8; i++)
                CrashInbox.Receive(
                    new CrashInbox.Sender { Machine = $"M{i}", Received = DateTime.UtcNow.AddMinutes(i) },
                    Crash + $"\n// crash number {i}");

            Expect("ten reports are there", CrashInbox.All().Count, 10);
            Expect("pruning to four drops six", CrashInbox.Prune(keep: 4), 6);
            Expect("and four remain", CrashInbox.All().Count, 4);
            Check("with no orphaned sidecars",
                  Directory.GetFiles(CrashInbox.Folder, "*.json").Length == 4,
                  $"{Directory.GetFiles(CrashInbox.Folder, "*.json").Length}");

            var one = CrashInbox.All()[0];
            Check("one can be removed by hand", CrashInbox.Remove(one));
            Check("and both its files go", !File.Exists(one.TextPath) &&
                                           !File.Exists(one.TextPath[..^4] + ".json"));
        }

        private static void SendingOnce(string sandbox)
        {
            Section("a machine does not send the same crash twice");

            string version = "26.1.2";
            string folder = CrashReports.FolderFor(version);
            Directory.CreateDirectory(folder);

            const string Head = "---- Minecraft Crash Report ----\n\nDescription: Ticking entity\n\n";

            for (int i = 0; i < 4; i++)
            {
                string path = Path.Combine(folder, $"crash-2026-10-07_1{i}.00.00-client.txt");
                File.WriteAllText(path, Head + $"java.lang.NullPointerException: number {i}\n");
                File.SetLastWriteTime(path, new DateTime(2026, 10, 7, 10 + i, 0, 0));
            }

            var unsent = CrashInbox.Unsent(version);

            // Capped, so the first run on a machine that has been crashing for a month
            // does not spend a minute uploading its history.
            Expect("only the newest few are offered at a time", unsent.Count, 3);
            Check("newest first", unsent[0].Report.FileName.Contains("13.00.00"),
                  unsent[0].Report.FileName);
            Check("each has a hash to remember it by",
                  unsent.All(u => u.Sha1.Length == 40), unsent[0].Sha1);

            CrashInbox.RememberSent(unsent[0].Sha1);
            var after = CrashInbox.Unsent(version);
            Check("one that has been sent is not offered again",
                  after.All(u => u.Sha1 != unsent[0].Sha1));
            Expect("and the next one moves up", after.Count, 3);

            Check("the record is kept beside the launcher, not in config.txt",
                  File.Exists(CrashInbox.SentFilePath) &&
                  !CrashInbox.SentFilePath.EndsWith("config.txt"), CrashInbox.SentFilePath);

            foreach (var item in after) CrashInbox.RememberSent(item.Sha1);
            Expect("once all four are sent, nothing is left", CrashInbox.Unsent(version).Count, 0);

            Expect("a version with no crash reports offers nothing",
                   CrashInbox.Unsent("1.20.1").Count, 0);

            // A client with no host reachable must not mark anything as sent.
            int sent = FleetClient.SendUnsentCrashesAsync(
                "", new AppConfig(), version).GetAwaiter().GetResult();
            Expect("no host means nothing is sent", sent, 0);

            Check("and a check-in with no host simply does not happen",
                  !FleetClient.CheckInAsync("", Report("X")).GetAwaiter().GetResult());
        }
    }
}
