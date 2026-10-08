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
    /// Backing a world up into a zip.
    /// </summary>
    /// <remarks>
    /// The worlds are the only thing in this install that cannot be rebuilt from a
    /// download, and until now nothing backed them up at all. The checks that matter
    /// most are the ones about a world that is *in use*, because that is when somebody
    /// will actually press the button: a server running, region files open, and
    /// <c>session.lock</c> held by the game. A backup that throws on the first locked
    /// file, or that restores a stale lock, is worse than useless — it looks like it
    /// worked.
    ///
    /// Everything here runs in a sandbox reached through <c>Paths</c>, so the product
    /// code under test is the same code that runs against the real folders.
    /// </remarks>
    public static class WorldBackupTests
    {
        public static void Run()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "mc-backup-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);

            try
            {
                using (LocalInstall.RedirectBase(sandbox))
                {
                    Naming();
                    Finding(sandbox);
                    Making(sandbox);
                    LockedFiles(sandbox);
                    Pruning(sandbox);
                    Sizing();
                }

                RealWorlds();
            }
            finally
            {
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }

        /// <summary>A world is recognised by its level.dat, so every fake needs one.</summary>
        private static string FakeWorld(string parent, string name, int regions = 2)
        {
            string dir = Path.Combine(parent, name);
            Directory.CreateDirectory(Path.Combine(dir, "region"));
            Directory.CreateDirectory(Path.Combine(dir, "playerdata"));

            File.WriteAllText(Path.Combine(dir, "level.dat"), "not really nbt, but present");
            File.WriteAllText(Path.Combine(dir, WorldBackups.LockFile), "\u221E\u2593");

            for (int i = 0; i < regions; i++)
                File.WriteAllText(Path.Combine(dir, "region", $"r.0.{i}.mca"), new string('x', 2048));

            File.WriteAllText(Path.Combine(dir, "playerdata", "someone.dat"), "player");

            return dir;
        }

        private static void Naming()
        {
            Section("naming a backup");

            var target = new WorldBackups.Target(
                WorldBackups.Kind.ServerWorld, "fabric-26.1.2", "world", @"C:\nowhere");

            var when = new DateTime(2026, 10, 7, 14, 32, 59);
            Expect("the name carries the world, the owner and the minute",
                   WorldBackups.NameFor(target, when), "fabric-26.1.2-world-2026-10-07-1432.zip");

            // Two worlds both called "world" is the normal case here, not a corner one:
            // every server folder has one. The owner in the name is what keeps them apart.
            var other = new WorldBackups.Target(
                WorldBackups.Kind.ServerWorld, "neoforge-1.21.1", "world", @"C:\nowhere");

            Check("two worlds called the same thing do not collide",
                  WorldBackups.NameFor(target, when) != WorldBackups.NameFor(other, when));

            Expect("awkward characters are replaced", WorldBackups.Sanitise("my world: 2!"), "my_world_2");
            Expect("a name of nothing but rubbish still yields something",
                   WorldBackups.Sanitise("///"), "world");

            Expect("bytes are described for a person", WorldBackups.Describe(0), "0 bytes");
            Expect("in kilobytes", WorldBackups.Describe(4096), "4 KB");
            Expect("in megabytes", WorldBackups.Describe(111L * 1024 * 1024), "111.0 MB");
            Expect("and in gigabytes", WorldBackups.Describe(3L * 1024 * 1024 * 1024), "3.0 GB");
        }

        private static void Finding(string sandbox)
        {
            Section("finding what can be backed up");

            // A client's game directory is its version folder.
            string saves = Path.Combine(sandbox, "versions", "26.1.2", "saves");
            Directory.CreateDirectory(saves);
            FakeWorld(saves, "New World");
            FakeWorld(saves, "Creative Testing");

            // Not a world: no level.dat. The screenshots folder sits beside saves in a
            // real install and must not be offered as something to back up.
            Directory.CreateDirectory(Path.Combine(saves, "not-a-world"));

            var client = WorldBackups.ClientTargets("26.1.2");
            Expect("both single-player worlds are found", client.Count, 2);
            Check("a folder with no level.dat is not a world",
                  client.All(t => t.Name != "not-a-world"));
            Check("they are listed in a stable order", client[0].Name == "Creative Testing");
            Check("and labelled so a person knows which is which",
                  client[0].Label.Contains("single-player") && client[0].Label.Contains("26.1.2"),
                  client[0].Label);

            Expect("a version with no saves folder offers nothing",
                   WorldBackups.ClientTargets("1.20.1").Count, 0);

            // Servers. level-name is deliberately left unset by the launcher, so the
            // default of "world" has to be assumed rather than read.
            string server = Path.Combine(sandbox, "servers", "fabric-26.1.2");
            Directory.CreateDirectory(server);
            File.WriteAllText(Path.Combine(server, "server.properties"),
                              "#Minecraft server properties\nmotd=hello\ndifficulty=normal\n");
            FakeWorld(server, "world");

            var found = WorldBackups.ServerTargets("fabric", "26.1.2");
            Expect("an unset level-name means the server default", found.Count, 1);
            Expect("which is 'world'", found[0].Name, "world");
            Check("owned by the server folder", found[0].Owner == "fabric-26.1.2", found[0].Owner);

            // A server that does name its world.
            string named = Path.Combine(sandbox, "servers", "forge-1.20.1");
            Directory.CreateDirectory(named);
            File.WriteAllText(Path.Combine(named, "server.properties"), "level-name=survival\n");
            FakeWorld(named, "survival");
            FakeWorld(named, "survival_nether");

            var namedWorlds = WorldBackups.ServerTargets("forge", "1.20.1");
            Expect("level-name is honoured when it is set", namedWorlds[0].Name, "survival");
            Check("and a split-out Nether comes with it",
                  namedWorlds.Any(t => t.Name == "survival_nether"),
                  string.Join(", ", namedWorlds.Select(t => t.Name)));

            Expect("a server folder that does not exist offers nothing",
                   WorldBackups.ServerTargets("fabric", "9.9.9").Count, 0);

            // Everything at once, which is what the backup window lists.
            var all = WorldBackups.All();
            Check("every world on the machine is found together", all.Count >= 5, $"{all.Count}");
            Check("client and server worlds are both in there",
                  all.Any(t => t.Of == WorldBackups.Kind.ClientSave) &&
                  all.Any(t => t.Of == WorldBackups.Kind.ServerWorld));
        }

        private static void Making(string sandbox)
        {
            Section("making one");

            string saves = Path.Combine(sandbox, "versions", "26.1.2", "saves");
            var target = WorldBackups.ClientTargets("26.1.2").First(t => t.Name == "New World");

            var result = WorldBackups.CreateAsync(target).GetAwaiter().GetResult();

            Check("it reports success", result.Ok, result.Message);
            Check("the zip is where it said", result.Path is not null && File.Exists(result.Path!));
            Check("it lands in the backups folder beside the launcher",
                  result.Path!.StartsWith(WorldBackups.Folder, StringComparison.OrdinalIgnoreCase),
                  result.Path);
            Note(result.Message);

            using (var zip = ZipFile.OpenRead(result.Path!))
            {
                var names = zip.Entries.Select(e => e.FullName).ToList();

                Check("level.dat is in it", names.Contains("level.dat"), string.Join(", ", names));
                Check("so are the region files", names.Any(n => n.StartsWith("region/")));
                Check("and nested folders keep their shape",
                      names.Contains("playerdata/someone.dat"), string.Join(", ", names));

                // The one exclusion, and the reason for it: the game rewrites this on
                // load, and restoring a stale one is how "someone else is playing in
                // this world" appears from nowhere.
                Check("session.lock is left out",
                      !names.Any(n => n.EndsWith(WorldBackups.LockFile, StringComparison.OrdinalIgnoreCase)),
                      string.Join(", ", names));

                Check("entries use forward slashes, as a zip must",
                      names.All(n => !n.Contains('\\')), string.Join(", ", names));

                // Round-trip the contents, not just the names.
                var dat = zip.GetEntry("level.dat")!;
                using var reader = new StreamReader(dat.Open());
                Expect("and the bytes survive", reader.ReadToEnd(), "not really nbt, but present");
            }

            Expect("the skipped file is counted", result.Skipped, 1);
            Check("the rest were written", result.Files >= 4, $"{result.Files}");

            // A second backup in the same minute must not overwrite the first.
            var again = WorldBackups.CreateAsync(target).GetAwaiter().GetResult();
            Check("a second backup in the same minute gets its own file",
                  again.Ok && again.Path != result.Path, again.Path);

            Expect("both are listed", WorldBackups.List(target).Count, 2);
            Check("newest first", WorldBackups.List(target)[0].When >= WorldBackups.List(target)[1].When);

            // A world that is not there.
            var absent = new WorldBackups.Target(
                WorldBackups.Kind.ClientSave, "26.1.2", "gone", Path.Combine(saves, "gone"));

            var failed = WorldBackups.CreateAsync(absent).GetAwaiter().GetResult();
            Check("a missing world fails rather than writing an empty zip", !failed.Ok);
            Check("and says so in plain words", failed.Message.Contains("no world"), failed.Message);
            Check("leaving nothing behind", failed.Path is null);
        }

        private static void LockedFiles(string sandbox)
        {
            Section("a world that is in use");

            string saves = Path.Combine(sandbox, "versions", "26.1.2", "saves");
            string world = FakeWorld(saves, "Live World");

            // This is the real situation: a running server or game holds its region
            // files open. CreateEntryFromFile cannot read these at all, which is why
            // the product opens them shared instead.
            string region = Path.Combine(world, "region", "r.0.0.mca");
            string lockFile = Path.Combine(world, WorldBackups.LockFile);

            using (var held = new FileStream(region, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            using (var heldLock = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                held.Write(Encoding.UTF8.GetBytes("written while open"));
                held.Flush();

                var target = WorldBackups.ClientTargets("26.1.2").First(t => t.Name == "Live World");
                var result = WorldBackups.CreateAsync(target).GetAwaiter().GetResult();

                Check("a world with files open still backs up", result.Ok, result.Message);
                Note(result.Message);

                using var zip = ZipFile.OpenRead(result.Path!);
                var names = zip.Entries.Select(e => e.FullName).ToList();

                Check("the open region file is included anyway",
                      names.Contains("region/r.0.0.mca"), string.Join(", ", names));
                Check("and the data written to it is there",
                      new StreamReader(zip.GetEntry("region/r.0.0.mca")!.Open())
                          .ReadToEnd().Contains("written while open"));

                // session.lock is opened with FileShare.Read here, so a shared *read*
                // would succeed — it is excluded by name, not by luck.
                Check("the lock file is still left out",
                      !names.Any(n => n.Contains(WorldBackups.LockFile)), string.Join(", ", names));
            }
        }

        private static void Pruning(string sandbox)
        {
            Section("keeping only the last few");

            var target = new WorldBackups.Target(
                WorldBackups.Kind.ServerWorld, "fabric-26.1.2", "prunable", @"C:\nowhere");

            Directory.CreateDirectory(WorldBackups.Folder);

            // Seven backups, each a minute apart, oldest first.
            var start = new DateTime(2026, 10, 7, 9, 0, 0);
            for (int i = 0; i < 7; i++)
            {
                string path = Path.Combine(WorldBackups.Folder, WorldBackups.NameFor(target, start.AddMinutes(i)));
                File.WriteAllText(path, "pretend zip");
                File.SetLastWriteTime(path, start.AddMinutes(i));
            }

            Expect("all seven are listed", WorldBackups.List(target).Count, 7);

            // Pruning is destructive, so the deletion itself is swapped out: the check
            // is which files it chose, not whether File.Delete works.
            var deleted = new System.Collections.Generic.List<string>();
            var real = WorldBackups.DeleteFile;
            WorldBackups.DeleteFile = p => deleted.Add(Path.GetFileName(p));

            try
            {
                int removed = WorldBackups.Prune(target, keep: 3);

                Expect("four are dropped when three are kept", removed, 4);
                Check("the oldest go",
                      deleted.Contains(WorldBackups.NameFor(target, start)) &&
                      deleted.Contains(WorldBackups.NameFor(target, start.AddMinutes(3))),
                      string.Join(", ", deleted));
                Check("the newest are kept",
                      !deleted.Contains(WorldBackups.NameFor(target, start.AddMinutes(6))) &&
                      !deleted.Contains(WorldBackups.NameFor(target, start.AddMinutes(4))),
                      string.Join(", ", deleted));

                deleted.Clear();
                Expect("asking to keep none still keeps one", WorldBackups.Prune(target, keep: 0), 6);

                // Another world's backups are not this world's business.
                var other = new WorldBackups.Target(
                    WorldBackups.Kind.ServerWorld, "fabric-26.1.2", "other", @"C:\nowhere");
                File.WriteAllText(
                    Path.Combine(WorldBackups.Folder, WorldBackups.NameFor(other, start)), "zip");

                deleted.Clear();
                WorldBackups.Prune(target, keep: 1);
                Check("pruning one world never touches another",
                      !deleted.Any(d => d.Contains("-other-")), string.Join(", ", deleted));

                Expect("and that one is still listed", WorldBackups.List(other).Count, 1);
            }
            finally
            {
                WorldBackups.DeleteFile = real;
            }
        }

        private static void Sizing()
        {
            Section("room on the disk");

            Check("free space is readable for the backups folder",
                  WorldBackups.FreeBytesFor(WorldBackups.Folder) is > 0);

            Check("a byte fits", WorldBackups.HasRoomFor(1) == true);

            // Checked against the uncompressed size on purpose: region files usually
            // halve, but "usually" is not worth betting a world on.
            Check("something larger than the disk does not",
                  WorldBackups.HasRoomFor(long.MaxValue) == false);

            Expect("a folder that does not exist measures zero",
                   WorldBackups.SizeOf(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid())), 0L);

            Check("free space on a nonsense path is unknown rather than zero",
                  WorldBackups.FreeBytesFor("") is null);
        }

        private static void RealWorlds()
        {
            Section("against the real install");

            if (!LocalInstall.Available)
            {
                Note("no versions/ folder here — skipped.");
                return;
            }

            var all = WorldBackups.All();
            Note($"{all.Count} world(s): {string.Join(", ", all.Select(t => t.Label))}");

            // Read-only. The point is that the finders agree with what is actually on
            // disk, not that a 111 MB world can be zipped in a test run.
            foreach (var target in all)
            {
                Check($"{target.Name} is where it says it is", target.Exists, target.Path);
                Check($"{target.Name} has a level.dat",
                      File.Exists(Path.Combine(target.Path, "level.dat")));
            }

            var server = all.Where(t => t.Of == WorldBackups.Kind.ServerWorld).ToList();
            if (server.Count == 0)
            {
                Note("no server worlds on this machine.");
                return;
            }

            Check("every server world is sized", server.All(t => WorldBackups.SizeOf(t.Path) > 0));

            foreach (var target in server)
                Note($"  {target.Owner}/{target.Name}: {WorldBackups.Describe(WorldBackups.SizeOf(target.Path))}");

            Check("and there is room for the largest of them",
                  WorldBackups.HasRoomFor(server.Max(t => WorldBackups.SizeOf(t.Path))) != false);
        }
    }
}
