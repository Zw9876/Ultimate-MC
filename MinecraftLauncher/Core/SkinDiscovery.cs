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

        /// <summary>A skin server that answered a discovery broadcast.</summary>
        /// <param name="Address">"ip:port", ready to use as a base address.</param>
        /// <param name="IsLocal">True when it is running on this machine.</param>
        public sealed record Found(string Address, IPAddress Ip, int Port, bool IsLocal);

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
        /// Broadcasts for skin servers and collects every distinct reply. Returns
        /// early once a remote server answers, since that is already the preferred
        /// result and waiting longer only delays the launch.
        /// </summary>
        public static IReadOnlyList<Found> Discover()
        {
            var results = new List<Found>();
            try
            {
                using var udp = new UdpClient { EnableBroadcast = true };
                byte[] probe = Encoding.ASCII.GetBytes(ProbeMessage);
                udp.Send(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

                var local = LocalAddresses();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

                    var m = RxReply.Match(Encoding.ASCII.GetString(reply).Trim());
                    if (!m.Success || !int.TryParse(m.Groups[1].Value, out int port)) continue;

                    string address = $"{remote.Address}:{port}";
                    if (!seen.Add(address)) continue;

                    bool isLocal = local.Contains(remote.Address);
                    results.Add(new Found(address, remote.Address, port, isLocal));

                    // A remote host is what we want; stop rather than stall the launch.
                    if (!isLocal) break;

                    // Only ours answered so far — wait a little longer, but not the
                    // full window, or every offline launch pays for it.
                    var settle = DateTime.UtcNow + SettleWindow;
                    if (settle < deadline) deadline = settle;
                }
            }
            catch
            {
                // Discovery is best-effort; callers treat "none" as "not hosted".
            }
            return results;
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
