using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Which machine a client talks to, for skins and for permission to play.
    /// </summary>
    /// <remarks>
    /// This exists because of a real failure: pausing everyone was switched on with the
    /// whole fleet up to date, and only some machines stopped playing. Nothing was
    /// broken in the pause itself. Several machines were running skin servers — which
    /// is normal, one starts whenever somebody hosts a Minecraft server — every
    /// launcher serves <c>/launcher/blocks</c> and <c>/launcher/shutdown</c>, and
    /// discovery bound a client to whichever answered the broadcast first. A peer
    /// answered "nothing happening", perfectly truthfully, out of its own empty
    /// play-blocks.json.
    ///
    /// So the properties worth holding on to are:
    ///
    /// <list type="bullet">
    /// <item><b>A non-admin responder is never the authority.</b> No admin host must
    /// mean "ask nobody" and fall back to the cache, never "ask this one instead".</item>
    /// <item><b>Skins follow the version being played</b>, since several skin servers
    /// on one LAN is the intended arrangement rather than a mistake.</item>
    /// <item><b>The legacy announcement is unchanged.</b> Every launcher in the field
    /// matches it with an anchored regex, so a tidier single-line format would make all
    /// of them see no skin server at all.</item>
    /// </list>
    /// </remarks>
    public static class SkinDiscoveryTests
    {
        /// <summary>
        /// A frozen copy of the regex used by launchers ALREADY DEPLOYED.
        /// </summary>
        /// <remarks>
        /// Deliberately duplicated rather than referenced. It is not meant to track
        /// whatever this build happens to do; it records what the machines out there
        /// already do, and the whole point is to fail if the two stop agreeing.
        /// </remarks>
        private static readonly Regex DeployedRegex =
            new(@"^MCSKINSERVER:(\d+)$", RegexOptions.Compiled);

        public static void Run()
        {
            string sandbox = Path.Combine(
                Path.GetTempPath(), "mc-discovery-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);

            try
            {
                using var scope = LocalInstall.RedirectBase(sandbox);

                Announcing(sandbox);
                Parsing();
                Merging();
                Choosing();
                Authority();
            }
            finally
            {
                try { Directory.Delete(sandbox, recursive: true); } catch { }
            }
        }

        // ── the real thing, over a real socket ───────────────────────────────
        private static void Announcing(string sandbox)
        {
            // A high port, so a launcher or server already running on this machine is
            // not disturbed and does not disturb this.
            const int Port = 35567;

            var server = new SkinServer(Port)
            {
                HostedVersion = "26.1.2",
                HostedLoader = "fabric"
            };

            string flag = Path.Combine(sandbox, "admin.flag");
            File.WriteAllText(flag, "");

            List<string> replies;
            try
            {
                server.Start();
                replies = Probe(server.DiscoveryPort);
            }
            finally
            {
                try { server.Stop(); } catch { }
            }

            Check("a probe is answered at all", replies.Count > 0);
            Check("BOTH announcements are sent", replies.Count == 2);

            string? legacy = replies.FirstOrDefault(r => DeployedRegex.IsMatch(r.Trim()));
            Check("the deployed regex still matches an announcement", legacy is not null);
            Check("and it carries the port", legacy?.Trim() == $"MCSKINSERVER:{Port}");

            string? v2 = replies.FirstOrDefault(r => r.StartsWith("MCSKINSERVER2:", StringComparison.Ordinal));
            Check("a second, richer announcement is sent", v2 is not null);

            // The compatibility property, stated as plainly as it can be: an old
            // launcher must skip the new line rather than choke on it.
            Check("the richer one does NOT match the deployed regex",
                v2 is not null && !DeployedRegex.IsMatch(v2.Trim()));

            Check("the richer one parses",
                SkinDiscovery.TryParseAnnouncement(
                    v2 ?? "", out int port, out string? machine,
                    out bool admin, out string? version, out string? loader));
            SkinDiscovery.TryParseAnnouncement(
                v2 ?? "", out port, out machine, out admin, out version, out loader);

            Check("the port is announced", port == Port);
            Check("the machine names itself", machine == Environment.MachineName);
            Check("the hosted version is announced", version == "26.1.2");
            Check("so is its loader", loader == "fabric");
            Check("admin.flag beside the launcher is declared", admin);

            // And without the flag, it must stop claiming authority — read per request
            // rather than cached at startup, which is why this can be checked by
            // deleting the file under a running server.
            File.Delete(flag);
            var server2 = new SkinServer(Port) { HostedVersion = "1.20.1" };
            List<string> after;
            try
            {
                server2.Start();
                after = Probe(server2.DiscoveryPort);
            }
            finally
            {
                try { server2.Stop(); } catch { }
            }

            string? v2b = after.FirstOrDefault(r => r.StartsWith("MCSKINSERVER2:", StringComparison.Ordinal));
            SkinDiscovery.TryParseAnnouncement(
                v2b ?? "", out _, out _, out bool admin2, out string? version2, out string? loader2);
            Check("with no admin.flag, authority is not claimed", !admin2);
            Check("the version still follows the server", version2 == "1.20.1");
            Check("an unstated loader is announced as nothing at all", loader2 is null);
        }

        private static List<string> Probe(int discoveryPort)
        {
            var got = new List<string>();
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 1500;

            byte[] probe = Encoding.ASCII.GetBytes("MCSKINSERVER_DISCOVER");
            udp.Send(probe, probe.Length, new IPEndPoint(IPAddress.Loopback, discoveryPort));

            // Two are expected; the timeout is what ends it if fewer arrive.
            for (int i = 0; i < 2; i++)
            {
                var from = new IPEndPoint(IPAddress.Any, 0);
                try { got.Add(Encoding.ASCII.GetString(udp.Receive(ref from))); }
                catch (SocketException) { break; }
            }
            return got;
        }

        // ── parsing, including the shapes that must be rejected ──────────────
        private static void Parsing()
        {
            Check("the legacy form parses",
                SkinDiscovery.TryParseAnnouncement(
                    "MCSKINSERVER:25567", out int p, out string? m, out bool a, out _, out _)
                && p == 25567 && m is null && !a);

            Check("a captured announcement parses whole",
                SkinDiscovery.TryParseAnnouncement(
                    "MCSKINSERVER2:port=25567;machine=DESKTOP-EGIESHL;admin=1;mc=26.1.2;loader=fabric",
                    out int p2, out string? m2, out bool a2, out string? v2, out string? l2)
                && p2 == 25567 && m2 == "DESKTOP-EGIESHL" && a2 && v2 == "26.1.2" && l2 == "fabric");

            // The reason for key=value rather than positional fields: a newer host must
            // be able to say more without older clients rejecting the whole reply.
            Check("an unknown field is ignored, not fatal",
                SkinDiscovery.TryParseAnnouncement(
                    "MCSKINSERVER2:port=25567;somethingnew=yes;mc=1.20.1",
                    out int p3, out _, out _, out string? v3, out _)
                && p3 == 25567 && v3 == "1.20.1");

            Check("field order does not matter",
                SkinDiscovery.TryParseAnnouncement(
                    "MCSKINSERVER2:mc=1.20.1;admin=1;port=25567",
                    out int p4, out _, out bool a4, out string? v4, out _)
                && p4 == 25567 && a4 && v4 == "1.20.1");

            Check("admin=0 is not authority",
                SkinDiscovery.TryParseAnnouncement(
                    "MCSKINSERVER2:port=25567;admin=0", out _, out _, out bool a5, out _, out _)
                && !a5);

            Check("a missing admin field is not authority either",
                SkinDiscovery.TryParseAnnouncement(
                    "MCSKINSERVER2:port=25567", out _, out _, out bool a6, out _, out _)
                && !a6);

            Check("no port means no responder",
                !SkinDiscovery.TryParseAnnouncement(
                    "MCSKINSERVER2:machine=X;admin=1", out _, out _, out _, out _, out _));

            Check("a blank field reads as absent, not empty",
                SkinDiscovery.TryParseAnnouncement(
                    "MCSKINSERVER2:port=25567;mc=", out _, out _, out _, out string? v7, out _)
                && v7 is null);

            foreach (string junk in new[]
                     { "", "   ", "MCSKINSERVER:", "MCSKINSERVER:abc", "HELLO", "MCSKINSERVER3:port=1" })
                Check($"junk is rejected: \"{junk}\"",
                    !SkinDiscovery.TryParseAnnouncement(junk, out _, out _, out _, out _, out _));
        }

        // ── two datagrams, one address ───────────────────────────────────────
        private static void Merging()
        {
            var ip = IPAddress.Parse("192.168.1.50");
            var bare = new SkinDiscovery.Found("192.168.1.50:25567", ip, 25567, false);
            var rich = new SkinDiscovery.Found(
                "192.168.1.50:25567", ip, 25567, false, "BENCH-01", true, "26.1.2", "fabric");

            Check("a bare reply describes nothing", !bare.Describes);
            Check("a rich one does", rich.Describes);

            // The legacy line is sent first, so this is the order that actually happens
            // and the one the old HashSet-of-addresses code got wrong.
            var d1 = new Dictionary<string, SkinDiscovery.Found>(StringComparer.OrdinalIgnoreCase);
            SkinDiscovery.Merge(d1, bare);
            SkinDiscovery.Merge(d1, rich);
            Check("legacy then rich keeps the rich one", d1.Count == 1 && d1.Values.Single().IsAdmin);

            var d2 = new Dictionary<string, SkinDiscovery.Found>(StringComparer.OrdinalIgnoreCase);
            SkinDiscovery.Merge(d2, rich);
            SkinDiscovery.Merge(d2, bare);
            Check("rich then legacy keeps the rich one too", d2.Count == 1 && d2.Values.Single().IsAdmin);
        }

        private static SkinDiscovery.Found At(
            string ip, bool local = false, bool admin = false,
            string? version = null, string? loader = null, string? machine = "M") =>
            new($"{ip}:25567", IPAddress.Parse(ip), 25567, local, machine, admin, version, loader);

        // ── skins follow the Minecraft server ────────────────────────────────
        private static void Choosing()
        {
            var first = At("192.168.1.10", version: "1.20.1", loader: "forge");
            var wanted = At("192.168.1.11", version: "26.1.2", loader: "fabric");
            var pool = new[] { first, wanted };

            Check("the server hosting this version wins over the first to answer",
                SkinDiscovery.Select(pool, "26.1.2", "fabric")?.Address == wanted.Address);

            Check("the version alone is enough when the loader differs",
                SkinDiscovery.Select(pool, "26.1.2", "neoforge")?.Address == wanted.Address);

            Check("and when no loader is given at all",
                SkinDiscovery.Select(pool, "26.1.2", null)?.Address == wanted.Address);

            var fabric = At("192.168.1.12", version: "26.1.2", loader: "fabric");
            var forge = At("192.168.1.13", version: "26.1.2", loader: "forge");
            Check("with two on one version, the matching loader wins",
                SkinDiscovery.Select(new[] { forge, fabric }, "26.1.2", "fabric")?.Address == fabric.Address);

            Check("an unmatched version falls back rather than finding nothing",
                SkinDiscovery.Select(pool, "1.7.10", "vanilla")?.Address == first.Address);

            Check("nothing answering means nothing chosen",
                SkinDiscovery.Select(Array.Empty<SkinDiscovery.Found>(), "26.1.2", "fabric") is null);

            // The rule that predates all of this, and still holds: binding a player to
            // a stray server on their OWN machine means everybody else turns up with a
            // default skin and nothing reports an error.
            var localMatch = At("127.0.0.1", local: true, version: "26.1.2", loader: "fabric");
            var remoteNoMatch = At("192.168.1.20", version: "1.20.1");
            Check("a remote server still beats a local one that matches the version",
                SkinDiscovery.Select(new[] { localMatch, remoteNoMatch }, "26.1.2", "fabric")?.Address
                    == remoteNoMatch.Address);

            Check("local is used when it is all there is",
                SkinDiscovery.Select(new[] { localMatch }, "26.1.2", "fabric")?.Address
                    == localMatch.Address);

            // Old hosts announce no version, and must keep working exactly as before.
            var oldA = At("192.168.1.30");
            var oldB = At("192.168.1.31");
            Check("with nothing declaring a version, the first remote is used as before",
                SkinDiscovery.Select(new[] { oldA, oldB }, "26.1.2", "fabric")?.Address == oldA.Address);
        }

        // ── who may say whether anyone plays ────────────────────────────────
        private static void Authority()
        {
            var peer = At("192.168.1.10", version: "1.20.1");
            var peer2 = At("192.168.1.11", version: "26.1.2");
            var host = At("192.168.1.50", admin: true, version: "26.1.2", machine: "HOST");

            Check("the admin host is chosen even when it answered last",
                SkinDiscovery.PickAdmin(new[] { peer, peer2, host })?.Address == host.Address);

            // THE one that matters. A peer hosting a Minecraft server is not allowed to
            // become the authority just because no admin host answered.
            Check("no admin host means nobody, NOT the first responder",
                SkinDiscovery.PickAdmin(new[] { peer, peer2 }) is null);

            Check("an empty LAN means nobody", SkinDiscovery.PickAdmin(Array.Empty<SkinDiscovery.Found>()) is null);

            var localAdmin = At("127.0.0.1", local: true, admin: true);
            Check("the host's own launcher finds itself",
                SkinDiscovery.PickAdmin(new[] { peer, localAdmin })?.Address == localAdmin.Address);

            // Two admin hosts should not happen, but if it does, a real machine is a
            // better answer than loopback for the clients that have to reach it.
            Check("a remote admin host beats a local one",
                SkinDiscovery.PickAdmin(new[] { localAdmin, host })?.Address == host.Address);
        }
    }
}
