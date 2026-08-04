using System;
using System.IO;
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
        private const string Probe = "MCSKINSERVER_DISCOVER";

        /// <summary>
        /// Resolves the skin server address ("ip:port") or null if none found.
        /// Checks the manual override first, then broadcasts for discovery.
        /// </summary>
        public static string? Resolve(AppConfig config)
        {
            if (!string.IsNullOrWhiteSpace(config.SkinServer))
                return config.SkinServer!.Trim();

            try
            {
                using var udp = new UdpClient { EnableBroadcast = true };
                udp.Client.ReceiveTimeout = 1500;

                byte[] probe = Encoding.ASCII.GetBytes(Probe);
                udp.Send(probe, probe.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

                var remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] reply = udp.Receive(ref remote);   // throws on timeout
                string msg = Encoding.ASCII.GetString(reply);

                var m = Regex.Match(msg, @"^MCSKINSERVER:(\d+)$");
                if (m.Success)
                    return $"{remote.Address}:{m.Groups[1].Value}";
            }
            catch
            {
                // No server answered within the timeout — that's fine.
            }
            return null;
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
