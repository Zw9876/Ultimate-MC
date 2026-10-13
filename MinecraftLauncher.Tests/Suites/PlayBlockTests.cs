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
                Pausing(sandbox);
                PauseOnTheWire();
                WhoCanObey();
            }
            finally
            {
                try { Directory.Delete(sandbox, true); } catch { }
            }
        }

        private static PlayBlocks.Listing With(params PlayBlocks.Block[] blocks) =>
            new() { Blocks = blocks.ToList() };

        private static PlayBlocks.Listing WithPause(string reason)
        {
            var listing = new PlayBlocks.Listing();
            PlayBlocks.SetPause(listing, reason, null, "HOST-PC", Now);
            return listing;
        }

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

        private static void Pausing(string sandbox)
        {
            Section("turning playing off for everyone");

            var listing = new PlayBlocks.Listing();
            Check("nothing is paused to begin with",
                  PlayBlocks.PauseInForce(listing, Now) is null);

            var pause = PlayBlocks.SetPause(listing, "updating the launchers",
                                            TimeSpan.FromMinutes(30), "HOST-PC", Now);

            Check("a pause is in force", PlayBlocks.PauseInForce(listing, Now) is not null);
            Expect("with the reason", pause.Reason, "updating the launchers");
            Expect("and who set it", pause.By, "HOST-PC");
            Expect("ending when it should", pause.Until, Now.AddMinutes(30));
            Check("it has an id", pause.Id.Length > 0);

            // A fresh id each time, or a watcher that already acted on an earlier
            // pause would ignore the next one.
            var again = PlayBlocks.SetPause(listing, null, null, "HOST-PC", Now);
            Check("turning it on again gets a new id", again.Id != pause.Id);
            Check("and an open-ended one does not expire", again.Until is null);
            Expect("with no end it reads as such", again.UntilText, "until it is turned back on");

            // A timed pause must lift itself: a forgotten one would strand everybody.
            PlayBlocks.SetPause(listing, null, TimeSpan.FromMinutes(5), null, Now);
            Check("a timed pause is in force before its time",
                  PlayBlocks.PauseInForce(listing, Now) is not null);
            Check("and gone after it",
                  PlayBlocks.PauseInForce(listing, Now.AddMinutes(6)) is null);

            Expect("tidying up removes an expired pause",
                   PlayBlocks.DropExpired(listing, Now.AddMinutes(6)), 1);
            Check("and it is really gone", listing.Pause is null);

            PlayBlocks.SetPause(listing, null, null, null, Now);
            Check("clearing an active pause works", PlayBlocks.ClearPause(listing));
            Check("clearing nothing is not an error", !PlayBlocks.ClearPause(listing));

            // It must survive a restart - the whole reason it is on disk rather than
            // only in the running server's memory.
            PlayBlocks.SetPause(listing, "server maintenance", null, "HOST-PC", Now);
            PlayBlocks.SaveLocal(listing);

            var reloaded = PlayBlocks.LoadLocal();
            Check("a pause survives being written and read back",
                  PlayBlocks.PauseInForce(reloaded, Now) is not null);
            Expect("with its reason", reloaded.Pause!.Reason, "server maintenance");

            // What the person pressing PLAY reads.
            string text = PlayBlocks.ExplainPause(reloaded.Pause!);
            Note(text.Replace(Environment.NewLine, " / "));
            Check("it says everyone, not just them", text.Contains("for everyone"), text);
            Check("gives the reason", text.Contains("server maintenance"), text);
            Check("and does not read as a fault",
                  !text.Contains("error") && !text.Contains("crash"), text);

            // Blocks and the pause are independent.
            PlayBlocks.Set(reloaded, "DESKTOP-Z", null, "Zed", "separate", null, "HOST-PC", Now);
            Check("a block still applies while paused",
                  PlayBlocks.InForce(reloaded, "DESKTOP-Z", null, Now) is not null);
            PlayBlocks.ClearPause(reloaded);
            Check("and survives the pause being lifted",
                  PlayBlocks.InForce(reloaded, "DESKTOP-Z", null, Now) is not null);

            // The host is not locked out by a pause either - especially since pausing
            // is usually in order to work on the launchers.
            string flag = Path.Combine(sandbox, "admin.flag");
            bool hadFlag = File.Exists(flag);
            var global = new PlayBlocks.Listing();
            PlayBlocks.SetPause(global, "maintenance", null, "HOST-PC", Now);

            try
            {
                if (hadFlag) File.Delete(flag);
                Check("a pause applies to an ordinary machine",
                      PlayBlocks.PauseOnThisMachine(global, Now) is not null);

                File.WriteAllText(flag, "");
                Check("but never to the machine holding admin.flag",
                      PlayBlocks.PauseOnThisMachine(global, Now) is null);
            }
            finally
            {
                try { if (File.Exists(flag)) File.Delete(flag); } catch { }
                if (hadFlag) File.WriteAllText(flag, "");
            }

            File.Delete(PlayBlocks.FilePath);
        }

        private static void PauseOnTheWire()
        {
            Section("how a pause reaches a running game");

            var announcer = new ShutdownAnnouncer();

            Check("nothing announced to begin with",
                  SessionShutdown.ParsePause(announcer.ToJson()) is null);
            Check("and no shutdown either",
                  SessionShutdown.Parse(announcer.ToJson()) is null);

            announcer.SetPause(new PlayBlocks.Pause
            {
                Id = "abc123", Reason = "updating the launchers", Since = DateTime.UtcNow
            });

            string json = announcer.ToJson();
            Note(json);

            var notice = SessionShutdown.ParsePause(json);
            Check("a pause reaches the client", notice is not null);
            Expect("with its id, so a watcher acts once", notice!.Id, "abc123");
            Expect("and the reason to show them", notice.Reason, "updating the launchers");

            // It rides the channel the watchers already poll, so a shutdown and a
            // pause have to travel together without confusing each other.
            announcer.Announce(10);
            string both = announcer.ToJson();
            Check("a shutdown still parses alongside it",
                  SessionShutdown.Parse(both) is not null);
            Check("and so does the pause", SessionShutdown.ParsePause(both) is not null);

            announcer.SetPause(null);
            Check("clearing it stops it being announced",
                  SessionShutdown.ParsePause(announcer.ToJson()) is null);
            Check("without disturbing the shutdown",
                  SessionShutdown.Parse(announcer.ToJson()) is not null);

            // A host too old to know about pauses answers only the shutdown half, and
            // that has to read as "not paused" rather than as a broken reply.
            Check("an older host's reply reads as not paused",
                  SessionShutdown.ParsePause("""{"shutdown":false}""") is null);
            Check("and its shutdown still works",
                  SessionShutdown.Parse("""{"shutdown":true,"id":"x","countdown":5}""") is not null);

            Check("rubbish is not a pause", SessionShutdown.ParsePause("{ not json") is null);
            Check("nor is one with no id", SessionShutdown.ParsePause("""{"paused":true}""") is null);

            // A pause whose time has passed must not still be announced, worked out on
            // the host so two machines' clocks never have to agree.
            var spent = new ShutdownAnnouncer();
            spent.SetPause(new PlayBlocks.Pause
            {
                Id = "old", Since = DateTime.UtcNow.AddHours(-2), Until = DateTime.UtcNow.AddHours(-1)
            });
            Check("an expired pause is not announced",
                  SessionShutdown.ParsePause(spent.ToJson()) is null);
            Check("and reads as cleared on the host too", spent.CurrentPause is null);
        }

        /// <summary>A check-in, for building a roster to ask questions of.</summary>
        private static FleetRoster.CheckIn Checked(string machine, string? launcher = null) =>
            new() { Machine = machine, Launcher = launcher };

        private static void WhoCanObey()
        {
            Section("telling the host who cannot obey");

            // The gap this closes: a pause was turned on in earnest and somebody kept
            // playing, because their launcher predated the feature. Measured since,
            // side by side against one paused host: a 1.2.285.217 watcher closes its
            // game, a 1.2.280.291 watcher ignores the same pause entirely. The
            // mechanism was never the problem; the host having no way to know was.
            var roster = new FleetRoster.Listing();
            var now = DateTime.UtcNow;

            FleetRoster.Merge(roster, Checked("CURRENT-1", "1.2.285.217"), null, now);
            FleetRoster.Merge(roster, Checked("CURRENT-2", "1.2.285.145"), null, now);
            FleetRoster.Merge(roster, Checked("OLD-ONE",   "1.2.280.291"), null, now);
            FleetRoster.Merge(roster, Checked("ANCIENT",   "1.2.265.376"), null, now);
            FleetRoster.Merge(roster, Checked("SILENT"),                   null, now);

            var reach = PlayBlocks.CanObey(roster, PlayBlocks.PauseFloor);

            Expect("every checked-in machine is counted", reach.Total, 5);
            Expect("two are too old for a pause", reach.TooOld, 2);
            Expect("one never said", reach.Unknown, 1);
            Check("the old ones are named, so they can be chased",
                  reach.Names.Contains("OLD-ONE") && reach.Names.Contains("ANCIENT"),
                  string.Join(", ", reach.Names));
            Check("and the current ones are not", !reach.Names.Contains("CURRENT-1"));

            // Exactly on the floor counts as able: that is the build it landed in.
            Check("a machine exactly on the floor can obey", !reach.Names.Contains("CURRENT-2"));

            string? warn = reach.Warn("a pause");
            Check("there is something to warn about", warn is not null);
            Note(warn ?? "(nothing)");
            Check("the warning says how many and of how many",
                  warn!.Contains("2 of 5"), warn);
            Check("names them", warn.Contains("OLD-ONE"), warn);
            Check("counts the one that did not say, separately",
                  warn.Contains("never said"), warn);
            Check("and explains that a running game keeps its old watcher",
                  warn.Contains("restarted"), warn);

            // Blocks arrived a build before pausing, so the floors differ and a
            // machine can obey one and not the other.
            var blockReach = PlayBlocks.CanObey(roster, PlayBlocks.BlockFloor);
            Check("the block floor is earlier than the pause floor",
                  PlayBlocks.BlockFloor < PlayBlocks.PauseFloor);
            Expect("so the same roster has fewer too old to be blocked", blockReach.TooOld, 2);

            // Nothing to warn about when everybody is current - the warning must be
            // absent rather than reassuring noise.
            var allGood = new FleetRoster.Listing();
            FleetRoster.Merge(allGood, Checked("A", "1.2.285.217"), null, now);
            var fine = PlayBlocks.CanObey(allGood, PlayBlocks.PauseFloor);
            Check("a current fleet reaches everyone", fine.Everyone);
            Check("and says nothing at all", fine.Warn("a pause") is null);

            // An empty roster is not the same as a current one, and must not read as
            // success: it is the state the host was actually in.
            var empty = PlayBlocks.CanObey(new FleetRoster.Listing(), PlayBlocks.PauseFloor);
            Check("an empty roster does not read as everyone being fine",
                  empty.Warn("a pause") is not null);
            Check("it says no machines have checked in",
                  empty.Warn("a pause")!.Contains("checked in"), empty.Warn("a pause"));
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

            // A pause has to be cached too, or unplugging the network would defeat a
            // maintenance pause just as it would an individual block.
            PlayBlocks.SaveCache(WithPause("mid-update"));
            var pausedVerdict = PlayBlocksClient.CheckAsync(null).GetAwaiter().GetResult();
            Check("a remembered pause still applies with no host", pausedVerdict.Blocked,
                  pausedVerdict.Describe());
            Check("and is reported as a pause, not an individual block",
                  pausedVerdict.Pause is not null && pausedVerdict.Block is null);
            Check("with the right wording", pausedVerdict.Title == "Playing is paused",
                  pausedVerdict.Title);
            Check("naming the reason", pausedVerdict.Explain().Contains("mid-update"),
                  pausedVerdict.Explain());

            // When both apply, the specific answer is the useful one: being told the
            // general message would have them back the moment the pause lifted.
            var bothCached = WithPause("everyone");
            bothCached.Blocks.Add(new PlayBlocks.Block
            {
                Machine = me, Reason = "you in particular", Since = Now
            });
            PlayBlocks.SaveCache(bothCached);

            var mixed = PlayBlocksClient.CheckAsync(null).GetAwaiter().GetResult();
            Check("an individual block wins over a pause", mixed.Block is not null);
            Check("and that is what they are told",
                  mixed.Explain().Contains("you in particular"), mixed.Explain());

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
