using System;
using System.IO;
using System.Linq;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Turning playing off for one machine, and the check PLAY makes.
    /// </summary>
    /// <remarks>
    /// This feature exists because a Minecraft ban is by username and anybody can pick
    /// another one. So the checks that matter are the ones about identity surviving an
    /// evasion — a renamed computer, a reinstalled launcher — and about the two failure
    /// modes that would make the whole thing pointless or intolerable:
    ///
    /// <list type="bullet">
    /// <item>An <b>unreachable host must not mean "allowed"</b>, or unplugging the
    /// network defeats it.</item>
    /// <item>It must <b>never fail closed</b>, or everybody loses single-player
    /// whenever the host is switched off.</item>
    /// </list>
    ///
    /// And the one that protects the person using it: <b>the host's own machine can
    /// never be blocked</b>, whatever any list says.
    /// </remarks>
    public static class PlayBlockTests
    {
        private static readonly DateTime Now = new(2026, 10, 12, 20, 0, 0, DateTimeKind.Utc);

        public static void Run()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "mc-blocks-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);

            try
            {
                using var scope = LocalInstall.RedirectBase(sandbox);

                Identity();
                Expiry();
                Editing();
                Storing();
                ThisMachine(sandbox);
                Caching(sandbox);
            }
            finally
            {
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }

        private static PlayBlocks.Listing With(params PlayBlocks.Block[] blocks) =>
            new() { Blocks = blocks.ToList() };

        private static PlayBlocks.Block Block(
            string machine = "DESKTOP-A", string? guid = "11111111-2222-3333-4444-555555555555",
            string? user = "Steve", string? reason = "griefing spawn", DateTime? until = null) =>
            new() { Machine = machine, Guid = guid, Username = user, Reason = reason, Since = Now, Until = until };

        private static void Identity()
        {
            Section("who a block names");

            var block = Block();

            Check("the named computer is matched", PlayBlocks.Matches(block, "DESKTOP-A", null));
            Check("case does not matter", PlayBlocks.Matches(block, "desktop-a", null));
            Check("another computer is not", !PlayBlocks.Matches(block, "DESKTOP-B", null));

            // The evasion this is built against: rename the computer, keep the GUID.
            Check("renaming the computer does not escape it",
                  PlayBlocks.Matches(block, "RENAMED-PC", "11111111-2222-3333-4444-555555555555"));

            // The other direction: a fresh Windows install changes the GUID, not the name.
            Check("a new Windows install does not either",
                  PlayBlocks.Matches(block, "DESKTOP-A", "99999999-9999-9999-9999-999999999999"));

            Check("neither matching means no block",
                  !PlayBlocks.Matches(block, "DESKTOP-B", "99999999-9999-9999-9999-999999999999"));

            // A launcher too old to report a GUID must still be blockable by name.
            var nameOnly = Block(guid: null);
            Check("an entry with no GUID still matches by name",
                  PlayBlocks.Matches(nameOnly, "DESKTOP-A", null));
            Check("and a client that reports no GUID is still caught",
                  PlayBlocks.Matches(block, "DESKTOP-A", null));

            // The failure that would block an entire room at once.
            var empty = new PlayBlocks.Block();
            Check("a block naming nothing matches nobody",
                  !PlayBlocks.Matches(empty, "DESKTOP-A", "11111111-2222-3333-4444-555555555555"));
            Check("not even a machine with no name of its own",
                  !PlayBlocks.Matches(empty, "", null));

            Check("whitespace is not an identity",
                  !PlayBlocks.Matches(new PlayBlocks.Block { Machine = "   " }, "   ", null));
        }

        private static void Expiry()
        {
            Section("blocks that lift themselves");

            var indefinite = Block();
            Check("one with no end does not expire", !PlayBlocks.HasExpired(indefinite, Now));
            Check("and reads as such", indefinite.UntilText == "until removed", indefinite.UntilText);
            Check("it is not timed", !indefinite.IsTimed);

            var hour = Block(until: Now.AddHours(1));
            Check("a timed one is still in force before its time", !PlayBlocks.HasExpired(hour, Now));
            Check("and expired after it", PlayBlocks.HasExpired(hour, Now.AddHours(1).AddSeconds(1)));
            Check("exactly at its time it is done", PlayBlocks.HasExpired(hour, Now.AddHours(1)));
            Check("it is timed", hour.IsTimed);

            var listing = With(Block(machine: "A", guid: null, until: Now.AddHours(-1)),
                               Block(machine: "B", guid: null),
                               Block(machine: "C", guid: null, until: Now.AddHours(2)));

            Check("an expired block stops being in force",
                  PlayBlocks.InForce(listing, "A", null, Now) is null);
            Check("an open-ended one is in force",
                  PlayBlocks.InForce(listing, "B", null, Now) is not null);
            Check("a future-dated one is in force",
                  PlayBlocks.InForce(listing, "C", null, Now) is not null);

            Expect("tidying up removes only the expired", PlayBlocks.DropExpired(listing, Now), 1);
            Expect("leaving the rest", listing.Blocks.Count, 2);
            Check("and A is gone", listing.Blocks.All(b => b.Machine != "A"));
        }

        private static void Editing()
        {
            Section("setting and lifting");

            var listing = new PlayBlocks.Listing();

            var set = PlayBlocks.Set(listing, "DESKTOP-A", "guid-a", "Steve",
                                     "griefing spawn", TimeSpan.FromHours(2), "HOST-PC", Now);

            Expect("one block is recorded", listing.Blocks.Count, 1);
            Expect("with the reason", set.Reason, "griefing spawn");
            Expect("the person", set.Username, "Steve");
            Expect("who set it", set.By, "HOST-PC");
            Expect("and when it lifts", set.Until, Now.AddHours(2));
            Check("described for a person", set.WhoText == "Steve (DESKTOP-A)", set.WhoText);

            // Blocking the same machine again replaces rather than stacks, or lifting
            // one would leave an older one silently in force.
            PlayBlocks.Set(listing, "DESKTOP-A", "guid-a", "Steve", "again", null, "HOST-PC", Now);
            Expect("blocking the same machine twice leaves one entry", listing.Blocks.Count, 1);
            Expect("the newer reason wins", listing.Blocks[0].Reason, "again");
            Check("and it is now open-ended", listing.Blocks[0].Until is null);

            // Matched on either identifier, so a renamed machine is replaced too.
            PlayBlocks.Set(listing, "RENAMED", "guid-a", "Steve", "third", null, "HOST-PC", Now);
            Expect("replacing by GUID does not leave a duplicate", listing.Blocks.Count, 1);
            Expect("and the name is updated", listing.Blocks[0].Machine, "RENAMED");

            PlayBlocks.Set(listing, "DESKTOP-B", "guid-b", "Alex", null, null, "HOST-PC", Now);
            Expect("a second machine is its own entry", listing.Blocks.Count, 2);
            Check("a block with no reason is allowed", listing.Blocks.Any(b => b.Reason is null));

            Check("lifting one works", PlayBlocks.Remove(listing, "RENAMED", null));
            Expect("and only that one goes", listing.Blocks.Count, 1);
            Check("lifting one that is not there is not an error",
                  !PlayBlocks.Remove(listing, "NOBODY", null));

            // What the person actually reads.
            var timed = PlayBlocks.Set(new PlayBlocks.Listing(), "DESKTOP-C", null, "Alex",
                                       "left the server on fire", TimeSpan.FromHours(1), "HOST-PC", Now);
            string explained = PlayBlocks.Explain(timed);
            Note(explained.Replace(Environment.NewLine, " / "));

            Check("the explanation says who turned it off",
                  explained.Contains("hosting"), explained);
            Check("gives the reason", explained.Contains("left the server on fire"), explained);
            Check("and says it lifts by itself", explained.Contains("lifts by itself"), explained);
            Check("not to go and ask", !explained.Contains("talk to them"), explained);

            var open = PlayBlocks.Set(new PlayBlocks.Listing(), "DESKTOP-C", null, "Alex",
                                      null, null, "HOST-PC", Now);
            string openText = PlayBlocks.Explain(open);
            Check("an open-ended one tells them to go and ask",
                  openText.Contains("talk to them"), openText);
            Check("and does not invent a reason", !openText.Contains("Reason given"), openText);
        }

        private static void Storing()
        {
            Section("on disk");

            var listing = With(Block(), Block(machine: "DESKTOP-B", guid: "guid-b", user: "Alex"));

            Check("it saves", PlayBlocks.SaveLocal(listing));
            Check("beside the launcher", File.Exists(PlayBlocks.FilePath), PlayBlocks.FilePath);
            Check("with nothing half-written", !File.Exists(PlayBlocks.FilePath + ".tmp"));

            var back = PlayBlocks.LoadLocal();
            Expect("both come back", back.Blocks.Count, 2);
            Expect("with the GUID", back.Blocks[0].Guid, "11111111-2222-3333-4444-555555555555");
            Expect("and the reason", back.Blocks[0].Reason, "griefing spawn");

            // A corrupt list must not be able to stop a whole room playing.
            File.WriteAllText(PlayBlocks.FilePath, "{ not json at all");
            Expect("a corrupt list reads as empty", PlayBlocks.LoadLocal().Blocks.Count, 0);

            File.Delete(PlayBlocks.FilePath);
            Expect("so does a missing one", PlayBlocks.LoadLocal().Blocks.Count, 0);

            Check("rubbish parses to empty rather than throwing",
                  PlayBlocks.Parse("[]").Blocks.Count == 0);
        }

        private static void ThisMachine(string sandbox)
        {
            Section("this computer");

            string? guid = PlayBlocks.ThisMachineGuid;
            Note($"MachineGuid: {guid ?? "could not be read"}");

            // Read-only, no admin. If this cannot be read the feature degrades to
            // matching on the computer name, which is why nothing asserts it exists.
            if (guid is not null)
            {
                Check("it looks like a GUID", Guid.TryParse(guid, out _), guid);
                Check("and is stable across reads", PlayBlocks.ThisMachineGuid == guid);
            }

            string me = FleetRoster.ThisMachine;
            Check("this machine knows its own name", me.Length > 0, me);

            // Blocking this very machine, by name.
            var listing = With(new PlayBlocks.Block { Machine = me, Reason = "test" });

            string flag = Path.Combine(sandbox, "admin.flag");
            bool hadFlag = File.Exists(flag);
            if (hadFlag) File.Delete(flag);

            try
            {
                Check("a block naming this computer is in force",
                      PlayBlocks.BlockOnThisMachine(listing, Now) is not null);

                // The rail that stops a misclick locking the host out of its own
                // launcher. Checked at enforcement, so a list carried in from
                // elsewhere cannot do it either.
                File.WriteAllText(flag, "");
                Check("but never on the machine holding admin.flag",
                      PlayBlocks.BlockOnThisMachine(listing, Now) is null);

                File.Delete(flag);
                Check("and it applies again once that is gone",
                      PlayBlocks.BlockOnThisMachine(listing, Now) is not null);

                Expect("an empty list blocks nobody",
                       PlayBlocks.BlockOnThisMachine(new PlayBlocks.Listing(), Now)?.Machine, null);
            }
            finally
            {
                try { if (File.Exists(flag)) File.Delete(flag); } catch { }
                if (hadFlag) File.WriteAllText(flag, "");
            }
        }

        private static void Caching(string sandbox)
        {
            Section("remembering the answer");

            string me = FleetRoster.ThisMachine;

            var fromHost = With(
                new PlayBlocks.Block { Machine = me, Reason = "mine", Since = Now },
                new PlayBlocks.Block { Machine = "SOMEONE-ELSE", Reason = "theirs", Since = Now });

            Check("the answer is remembered", PlayBlocks.SaveCache(fromHost));
            Check("beside the launcher", File.Exists(PlayBlocks.CachePath));

            var cached = PlayBlocks.LoadCache();

            // Only this machine's own entries. There is no reason for one person's
            // computer to carry a readable list of who else is blocked.
            Expect("only this machine's own entry is kept", cached.Blocks.Count, 1);
            Expect("and it is the right one", cached.Blocks[0].Reason, "mine");
            Check("someone else's is not written down",
                  cached.Blocks.All(b => b.Machine != "SOMEONE-ELSE"));

            // The whole point of the cache: no host does not mean allowed.
            var verdict = PlayBlocksClient.CheckAsync(null).GetAwaiter().GetResult();
            Check("with no host, the remembered block still applies", verdict.Blocked,
                  verdict.Describe());
            Expect("and it says where that came from", verdict.From, PlayBlocksClient.Origin.Cache);
            Check("naming the reason", verdict.Block!.Reason == "mine", verdict.Block.Reason);

            // But it must never fail closed - an unreachable host with nothing
            // remembered has to let people play.
            File.Delete(PlayBlocks.CachePath);
            var open = PlayBlocksClient.CheckAsync(null).GetAwaiter().GetResult();
            Check("with no host and nothing remembered, playing is allowed", !open.Blocked);
            Expect("and it says so", open.From, PlayBlocksClient.Origin.Nowhere);

            var unreachable = PlayBlocksClient.CheckAsync("127.0.0.1:1").GetAwaiter().GetResult();
            Check("a host that will not answer also allows playing", !unreachable.Blocked,
                  unreachable.Describe());

            // An expired block in the cache is spent, so a timed block ends even on a
            // machine that never sees the host again.
            PlayBlocks.SaveCache(With(new PlayBlocks.Block
            {
                Machine = me, Reason = "old", Since = Now.AddDays(-2), Until = DateTime.UtcNow.AddHours(-1)
            }));

            var spent = PlayBlocksClient.CheckAsync(null).GetAwaiter().GetResult();
            Check("an expired cached block has stopped applying", !spent.Blocked, spent.Describe());

            // And the host itself is never blocked by its own cache.
            string flag = Path.Combine(sandbox, "admin.flag");
            bool hadFlag = File.Exists(flag);
            PlayBlocks.SaveCache(With(new PlayBlocks.Block { Machine = me, Reason = "mine", Since = Now }));

            try
            {
                File.WriteAllText(flag, "");
                var host = PlayBlocksClient.CheckAsync(null).GetAwaiter().GetResult();
                Check("the admin machine is never blocked by a cache either", !host.Blocked);
            }
            finally
            {
                try { if (File.Exists(flag)) File.Delete(flag); } catch { }
                if (hadFlag) File.WriteAllText(flag, "");
            }
        }
    }
}
