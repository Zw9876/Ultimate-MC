using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Client-side skin server discovery + auto-upload. Mirrors the PowerShell
    /// launcher: try config override, then UDP broadcast on 25568, and if a
    /// server is found, push this player's local skin to it.
    /// </summary>
    public static class SkinDiscovery
    {
        private const int DiscoveryPort = 25568;
        private const string ProbeMessage = "MCSKINSERVER_DISCOVER";

        /// <summary>Total time to wait when nothing has answered yet.</summary>
        private static readonly TimeSpan ProbeWindow = TimeSpan.FromMilliseconds(1500);

        /// <summary>
        /// Extra grace after a *local* reply, to give a real host on the LAN — which
        /// is a network round trip behind loopback — a chance to answer too.
        /// </summary>
        private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(400);

        private static readonly Regex RxReply = new(@"^MCSKINSERVER:(\d+)$", RegexOptions.Compiled);

        /// <summary>
        /// The second, richer announcement. Sent ALONGSIDE the legacy line above and
        /// never instead of it.
        /// </summary>
        /// <remarks>
        /// The legacy reply is matched by every launcher already in the fleet with an
        /// anchored regex, so appending fields to that line would make all of them see
        /// no skin server whatever. Hence a separate datagram, which those launchers
        /// fail to match and skip. Key=value pairs rather than positional fields for
        /// the same reason, one layer on: the next field to be added must not break
        /// this parser the way adding one to the first line would have.
        /// </remarks>
        private const string ReplyV2Prefix = "MCSKINSERVER2:";

        /// <summary>What a responder said about itself.</summary>
        private sealed record Announcement(
            int Port, string? Machine, bool IsAdmin, string? Version, string? Loader);

        private static string? OrNull(string s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        /// <summary>Reads either announcement form, or null if it is neither.</summary>
        internal static bool TryParseAnnouncement(
            string text, out int port, out string? machine,
            out bool admin, out string? version, out string? loader)
        {
            var a = ParseAnnouncement(text);
            port = a?.Port ?? 0;
            machine = a?.Machine;
            admin = a?.IsAdmin ?? false;
            version = a?.Version;
            loader = a?.Loader;
            return a is not null;
        }

        private static Announcement? ParseAnnouncement(string text)
        {
            text = (text ?? string.Empty).Trim();

            var m = RxReply.Match(text);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int legacy))
                return new Announcement(legacy, null, false, null, null);

            if (!text.StartsWith(ReplyV2Prefix, StringComparison.Ordinal)) return null;

            int port = 0;
            string? machine = null, version = null, loader = null;
            bool admin = false;

            foreach (string pair in text[ReplyV2Prefix.Length..]
                                   .Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;

                string key = pair[..eq].Trim().ToLowerInvariant();
                string val = pair[(eq + 1)..];

                switch (key)
                {
                    case "port":    int.TryParse(val.Trim(), out port); break;
                    case "machine": machine = OrNull(val); break;
                    case "admin":   admin = val.Trim() is "1" or "true"; break;
                    case "mc":      version = OrNull(val); break;
                    case "loader":  loader = OrNull(val); break;
                    // Unknown keys ignored on purpose, so a newer host can announce
                    // more without this build treating the whole reply as rubbish.
                }
            }

            return port > 0 ? new Announcement(port, machine, admin, version, loader) : null;
        }

        private static bool Same(string? a, string? b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Records a responder, keeping the more informative of two replies from the
        /// same address.
        /// </summary>
        /// <remarks>
        /// An up-to-date server sends BOTH announcements, so one address arrives twice
        /// and the richer reply has to win regardless of which order they turn up in.
        /// The original code kept a HashSet of addresses and skipped the repeat, which
        /// here would silently discard everything the second datagram exists to carry
        /// whenever the legacy line happened to arrive first - and it usually would,
        /// being sent first.
        /// </remarks>
        internal static void Merge(IDictionary<string, Found> byAddress, Found found)
        {
            if (byAddress.TryGetValue(found.Address, out var already) &&
                already.Describes && !found.Describes)
                return;

            byAddress[found.Address] = found;
        }

        /// <summary>
        /// The responder allowed to say whether anyone may play, out of those found.
        /// </summary>
        /// <remarks>
        /// Never falls back to a non-admin responder. Returning "some machine" when no
        /// admin host answered is precisely the behaviour that let a peer tell clients
        /// that play was allowed; a null here means "ask nobody", and the caller then
        /// uses its cached answer rather than assuming the best.
        /// </remarks>
        internal static Found? PickAdmin(IReadOnlyList<Found> found) =>
            found.FirstOrDefault(f => f.IsAdmin && !f.IsLocal)
            ?? found.FirstOrDefault(f => f.IsAdmin);

        /// <summary>A skin server that answered a discovery broadcast.</summary>
        /// <param name="Address">"ip:port", ready to use as a base address.</param>
        /// <param name="IsLocal">True when it is running on this machine.</param>
        /// <param name="Machine">Computer name, when the responder said.</param>
        /// <param name="IsAdmin">
        /// The responder holds <c>admin.flag</c>. Only such a machine may decide
        /// whether anyone is allowed to play.
        /// </param>
        /// <param name="Version">The Minecraft version this skin server is for.</param>
        /// <param name="Loader">Its loader, where one was named.</param>
        public sealed record Found(
            string Address, IPAddress Ip, int Port, bool IsLocal,
            string? Machine = null, bool IsAdmin = false,
            string? Version = null, string? Loader = null)
        {
            /// <summary>
            /// True when this responder told us anything beyond its port, i.e. it is
            /// new enough to send the second announcement.
            /// </summary>
            public bool Describes => Machine is not null || Version is not null || IsAdmin;
        }

        /// <summary>
        /// Resolves the skin server address ("ip:port") or null if none found.
        /// Checks the manual override first, then broadcasts for discovery.
        /// </summary>
        /// <remarks>
        /// A server running on this machine answers the broadcast far sooner than one
        /// across the LAN, so taking the first reply used to silently bind a player to
        /// their own stray skin server. That server only knows their skin, so everyone
        /// else showed up with default skins and nothing reported an error. Remote
        /// wins; local is only a fallback for when this machine really is the host.
        /// </remarks>
        public static string? Resolve(AppConfig config)
        {
            if (!string.IsNullOrWhiteSpace(config.SkinServer))
                return config.SkinServer!.Trim();

            var found = Discover();
            return (found.FirstOrDefault(f => !f.IsLocal) ?? found.FirstOrDefault())?.Address;
        }

        /// <summary>
        /// The first skin server found on another machine, or null. Used before
        /// starting one here, so a LAN never ends up with two competing servers.
        /// </summary>
        public static Found? FindRemote() => Discover().FirstOrDefault(f => !f.IsLocal);

        /// <summary>
        /// The skin server to use for a particular game, or null if none answered.
        /// </summary>
        /// <remarks>
        /// Skins belong with the Minecraft server being played, so a responder hosting
        /// that version beats one that merely answered the broadcast first. Several
        /// skin servers on one LAN is a normal arrangement rather than a mistake: each
        /// belongs to the Minecraft server on its machine.
        /// </remarks>
        public static string? ResolveForSkins(AppConfig config, string? version, string? loader)
        {
            if (!string.IsNullOrWhiteSpace(config.SkinServer))
                return config.SkinServer!.Trim();

            return Select(Discover(), version, loader)?.Address;
        }

        /// <summary>Picks the best responder for a given game. Separated out to be tested.</summary>
        internal static Found? Select(IReadOnlyList<Found> found, string? version, string? loader)
        {
            if (found.Count == 0) return null;

            // Remote still beats local, and for the original reason: binding a player
            // to a stray server on their own machine means everyone else turns up with
            // a default skin and nothing reports an error. Version matching happens
            // WITHIN that preference, not instead of it.
            var remote = found.Where(f => !f.IsLocal).ToList();
            var pool = remote.Count > 0 ? remote : found.ToList();

            if (!string.IsNullOrWhiteSpace(version))
            {
                var exact = pool.FirstOrDefault(
                    f => Same(f.Version, version) && Same(f.Loader, loader));
                if (exact is not null) return exact;

                // Loader unstated or different: the version alone is enough to be the
                // right skin server, and the manual picker only asks for a version.
                var sameVersion = pool.FirstOrDefault(f => Same(f.Version, version));
                if (sameVersion is not null) return sameVersion;
            }

            return pool[0];
        }

        /// <summary>
        /// The machine allowed to decide whether anyone may play, or null if none
        /// answered.
        /// </summary>
        /// <remarks>
        /// Pausing, blocking, session shutdown and check-in all resolve through here
        /// and NOT through skin discovery. Every launcher serves those endpoints, and a
        /// launcher that is not the admin host answers "nothing happening" perfectly
        /// truthfully from its own empty play-blocks.json - so a client that bound to
        /// whichever server answered first would be told it could play. That is exactly
        /// why a pause once reached only some of the fleet.
        ///
        /// config.SkinServer is deliberately NOT honoured: that override exists to
        /// point skins at a chosen machine, and it must not be able to make an
        /// arbitrary machine the authority on who is allowed to play.
        /// </remarks>
        public static Found? FindAdmin() => PickAdmin(Discover());

        /// <summary>
        /// Broadcasts for skin servers and collects every distinct reply. Returns
        /// early once a remote server answers, since that is already the preferred
        /// result and waiting longer only delays the launch.
        /// </summary>
        public static IReadOnlyList<Found> Discover()
        {
            var byAddress = new Dictionary<string, Found>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var udp = new UdpClient { EnableBroadcast = true };
                byte[] probe = Encoding.ASCII.GetBytes(ProbeMessage);
                udp.Send(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

                var local = LocalAddresses();
                DateTime deadline = DateTime.UtcNow + ProbeWindow;

                while (true)
                {
                    int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remaining <= 0) break;

                    udp.Client.ReceiveTimeout = remaining;
                    var remote = new IPEndPoint(IPAddress.Any, 0);

                    byte[] reply;
                    try { reply = udp.Receive(ref remote); }
                    catch (SocketException) { break; }   // nothing more arrived

                    if (!TryParseAnnouncement(
                            Encoding.ASCII.GetString(reply),
                            out int port, out string? machine,
                            out bool admin, out string? version, out string? loader))
                        continue;

                    string address = $"{remote.Address}:{port}";
                    bool isLocal = local.Contains(remote.Address);

                    var found = new Found(
                        address, remote.Address, port, isLocal,
                        machine, admin, version, loader);

                    Merge(byAddress, found);

                    // Stop only for a remote host that claims admin: it answers both
                    // questions a launch asks - whose skins, and who says whether play
                    // is allowed - so there is nothing better left to wait for.
                    if (!isLocal && (found.IsAdmin || byAddress[address].IsAdmin)) break;

                    // Otherwise something answered but is not authoritative. Wait a
                    // little longer in case the admin host answers too, but not the
                    // full window - paying that on every launch is what the original
                    // early break was avoiding.
                    var settle = DateTime.UtcNow + SettleWindow;
                    if (settle < deadline) deadline = settle;
                }
            }
            catch
            {
                // Discovery is best-effort; callers treat "none" as "not hosted".
            }
            return byAddress.Values.ToList();
        }

        /// <summary>Every IPv4 address this machine answers on, including loopback.</summary>
        private static HashSet<IPAddress> LocalAddresses()
        {
            var set = new HashSet<IPAddress> { IPAddress.Loopback };
            try
            {
                foreach (var a in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (a.AddressFamily == AddressFamily.InterNetwork)
                        set.Add(a);
            }
            catch
            {
                // Loopback alone still catches the common same-machine case.
            }
            return set;
        }

        /// <summary>
        /// Best-effort upload of the player's local skin PNG to the host.
        /// Never throws; failure just means skins won't sync this session.
        /// </summary>
        public static void UploadSkin(string serverAddr, string username, string model)
        {
            try
            {
                string skinPath = Path.Combine(Paths.Skins, $"{username}.png");
                if (!File.Exists(skinPath)) return;

                byte[] bytes = File.ReadAllBytes(skinPath);

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var content = new ByteArrayContent(bytes);
                content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

                string url = $"http://{serverAddr}/upload/{Uri.EscapeDataString(username)}?model={model}";
                http.PostAsync(url, content).GetAwaiter().GetResult();
            }
            catch
            {
                // best-effort
            }
        }
    }
}
