using System;
using System.Linq;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Working out who is on a server from its console output.
    /// </summary>
    /// <remarks>
    /// Every server line below is taken from the archived logs in
    /// <c>servers/fabric-26.1.2/logs</c> — real sessions, with the player names replaced.
    /// The awkward details are real: the <c>list</c> reply has a trailing space
    /// when nobody is on, and <c>lost connection</c> fires alongside <c>left the game</c>.
    /// </remarks>
    public static class PlayerRosterTests
    {
        private const string JoinAlex = "[21:19:42] [Server thread/INFO]: Alex joined the game";
        private const string LeaveAlex = "[21:20:01] [Server thread/INFO]: Alex left the game";
        private const string JoinSteve = "[22:28:23] [Server thread/INFO]: Steve joined the game";
        private const string LeaveSteve = "[22:36:43] [Server thread/INFO]: Steve left the game";

        private const string ListEmpty =
            "[23:11:53] [Server thread/INFO]: There are 0 of a max of 25 players online: ";
        private const string ListThree =
            "[22:30:00] [Server thread/INFO]: There are 3 of a max of 25 players online: Steve, Alex, Sam";

        public static void Run()
        {
            Lines();
            Noise();
            ListReplies();
            OverASession();
            Corrections();
            Playtimes();
        }

        private static void Lines()
        {
            Section("real join and leave lines");

            Check("a real join parses",
                  PlayerRoster.TryParseJoin(JoinAlex, out string a) && a == "Alex", a);
            Check("a real leave parses",
                  PlayerRoster.TryParseLeave(LeaveSteve, out string b) && b == "Steve", b);
            Check("a join is not read as a leave", !PlayerRoster.TryParseLeave(JoinAlex, out _));
            Check("a leave is not read as a join", !PlayerRoster.TryParseJoin(LeaveAlex, out _));
        }

        private static void Noise()
        {
            Section("lines that must not move the roster");

            foreach (string line in new[]
            {
                "[21:19:42] [Server thread/INFO]: Alex[/127.0.0.1:62826] logged in with entity id 1 at (-40.5, 76.0, 41.5)",
                "[21:20:01] [Server thread/INFO]: Alex lost connection: Disconnected",
                // Chat is the trap: a player typing the exact words being matched.
                "[21:30:00] [Server thread/INFO]: <Steve> haha I joined the game",
                "[21:30:05] [Server thread/INFO]: <Sam> Steve left the game lol",
                "[16:23:48] [Server thread/INFO]: [Chunky] Task finished for minecraft:overworld. Processed: 1521 chunks (100.00%), Total time: 0:00:49",
                ""
            })
            {
                bool moved = PlayerRoster.TryParseJoin(line, out _) ||
                             PlayerRoster.TryParseLeave(line, out _);
                string shown = line.Length > 62 ? line[..62] + "..." : line;
                Check($"ignored: {shown}", !moved);
            }
        }

        private static void ListReplies()
        {
            Section("the real 'list' reply");

            Check("an empty list parses", PlayerRoster.TryParseList(ListEmpty, out var empty, out int max25));
            Expect("  nobody in it", empty.Count, 0);
            Expect("  max players read", max25, 25);

            Check("the older max of 10 parses",
                  PlayerRoster.TryParseList(
                      "[21:13:14] [Server thread/INFO]: There are 0 of a max of 10 players online: ",
                      out _, out int max10) && max10 == 10, max10.ToString());

            Check("a populated list parses", PlayerRoster.TryParseList(ListThree, out var three, out _));
            Check("  all three names", three.Count == 3 && three.Contains("Steve") && three.Contains("Sam"),
                  string.Join("/", three));
        }

        private static void OverASession()
        {
            Section("the roster over a session");

            var roster = new PlayerRoster.Roster();
            Expect("starts empty", roster.Count, 0);
            Expect("says so plainly", roster.Describe(), "Nobody on yet.");

            Check("a join is a change", roster.Observe(JoinAlex));
            Expect("  one player on", roster.Count, 1);
            Check("the same join again is not", !roster.Observe(JoinAlex), "a duplicate was counted");
            Expect("  still one player", roster.Count, 1);

            Check("a second join is a change", roster.Observe(JoinSteve));
            Expect("  two players on", roster.Count, 2);
            Note(roster.Describe());
            Check("names are listed alphabetically",
                  roster.Players.Select(p => p.Name).SequenceEqual(new[] { "Alex", "Steve" }),
                  string.Join(",", roster.Players.Select(p => p.Name)));

            Check("chat does not add a player",
                  !roster.Observe("[21:30:00] [Server thread/INFO]: <Sam> I joined the game"));
            Expect("  still two", roster.Count, 2);

            Check("a leave is a change", roster.Observe(LeaveAlex));
            Expect("  back to one", roster.Count, 1);
            Check("leaving twice is not a change", !roster.Observe(LeaveAlex));
            Check("someone unknown leaving is not a change",
                  !roster.Observe("[21:40:00] [Server thread/INFO]: Nobody left the game"));
        }

        private static void Corrections()
        {
            Section("a 'list' reply corrects what was missed");

            // Learning that nobody is on is itself a change: the header has to move
            // from saying nothing to saying the server is empty.
            var quiet = new PlayerRoster.Roster();
            Check("the first reply counts as a change even with nobody on",
                  quiet.Observe(ListEmpty), "reported no change, so nothing would refresh");
            Check("  a second identical reply does not", !quiet.Observe(ListEmpty));
            Expect("  it reads as empty, not unknown", quiet.Describe(), "Nobody on the server.");

            // The launcher only sees output from when it attached, so people who joined
            // earlier are invisible until something asks.
            var late = new PlayerRoster.Roster();
            Check("not synced before asking", !late.Synced);
            Check("a list reply is a change", late.Observe(ListThree));
            Expect("  it picked up all three", late.Count, 3);
            Check("  and is now synced", late.Synced);
            Expect("  and knows the capacity", late.MaxPlayers, 25);
            Note(late.Describe());

            var before = late.Players.First(p => p.Name == "Steve").Since;
            System.Threading.Thread.Sleep(40);
            late.Observe(ListThree);
            Check("asking again does not restart playtime",
                  before == late.Players.First(p => p.Name == "Steve").Since, "the clock was reset");

            Check("a list that drops someone is a change", late.Observe(
                "[22:40:00] [Server thread/INFO]: There are 2 of a max of 25 players online: Steve, Sam"));
            Expect("  the roster shrank", late.Count, 2);
            Check("an identical list is not a change", !late.Observe(
                "[22:41:00] [Server thread/INFO]: There are 2 of a max of 25 players online: Steve, Sam"));

            late.Clear();
            Check("clearing resets everything",
                  late.Count == 0 && !late.Synced && late.MaxPlayers == 0,
                  $"count={late.Count} synced={late.Synced} max={late.MaxPlayers}");
        }

        private static void Playtimes()
        {
            Section("playtime wording");

            Expect("a new arrival reads as just joined",
                   new PlayerRoster.Player("Steve", DateTime.Now).PlaytimeText, "just joined");
            Expect("95 minutes reads as 1 hr 35 min",
                   new PlayerRoster.Player("Steve", DateTime.Now.AddMinutes(-95)).PlaytimeText, "1 hr 35 min");
            Expect("7 minutes reads as 7 min",
                   new PlayerRoster.Player("Steve", DateTime.Now.AddMinutes(-7)).PlaytimeText, "7 min");
        }
    }
}
