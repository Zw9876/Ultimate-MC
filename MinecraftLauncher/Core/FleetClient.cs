using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Telling the host who this machine is, and handing over its crash reports.
    /// </summary>
    /// <remarks>
    /// Both of these are favours to the host and neither is worth a second of anybody's
    /// time. Every method here swallows its failures and returns a bool: there is no
    /// path through this file that can stop a launcher starting, stop a game starting,
    /// or put a dialog in front of somebody who just wants to play.
    ///
    /// The host does not check itself in. Its own discovery deliberately prefers a
    /// *remote* skin server, so on the host there is nothing to find and the check-in
    /// simply does not happen — which is right, since the host already knows its own
    /// build and can see its own crash reports on the Client tab.
    /// </remarks>
    public static class FleetClient
    {
        /// <summary>
        /// Short on purpose. This runs while somebody is waiting for a game to start.
        /// </summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

        /// <summary>
        /// Reports this machine to a host.
        /// </summary>
        public static async Task<bool> CheckInAsync(
            string hostAddress, FleetRoster.CheckIn report, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(hostAddress)) return false;

            try
            {
                using var http = new HttpClient { Timeout = Timeout };
                using var content = new StringContent(
                    FleetRoster.ToJson(report), Encoding.UTF8, "application/json");

                using var response = await http.PostAsync(
                    $"http://{hostAddress}{FleetRoster.Endpoint}", content, ct);

                return response.IsSuccessStatusCode;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Finds the host and reports this machine, doing nothing if there is no host.
        /// </summary>
        /// <remarks>
        /// Takes an address when the caller already has one — the game watcher has
        /// resolved it, and the launcher resolves one at startup for the update check.
        /// Discovering again would mean another 1500 ms UDP wait for no reason, which is
        /// already the bulk of the delay on PLAY (handoff section 10, item 7).
        /// </remarks>
        public static async Task<bool> CheckInAsync(
            AppConfig config, string? hostAddress, string? minecraft, string? loader,
            bool playing, CancellationToken ct = default)
        {
            string? host = hostAddress;

            if (string.IsNullOrWhiteSpace(host))
            {
                host = config.SkinServer?.Trim();

                if (string.IsNullOrWhiteSpace(host))
                {
                    try { host = (await Task.Run(SkinDiscovery.FindRemote, ct))?.Address; }
                    catch (Exception) { return false; }
                }
            }

            if (string.IsNullOrWhiteSpace(host)) return false;

            return await CheckInAsync(
                host, FleetRoster.Describe(config, minecraft, loader, playing), ct);
        }

        /// <summary>
        /// Sends one crash report to the host.
        /// </summary>
        /// <remarks>
        /// The details travel as query parameters and the report text as the body, so
        /// the host never has to pick a sender's text apart to find out who sent it.
        /// </remarks>
        public static async Task<bool> SendCrashAsync(
            string hostAddress, AppConfig config, string? minecraft,
            string fileName, string text, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(hostAddress) || string.IsNullOrWhiteSpace(text))
                return false;

            try
            {
                using var http = new HttpClient { Timeout = Timeout };
                using var content = new StringContent(text, Encoding.UTF8, "text/plain");

                string query =
                    $"?machine={Uri.EscapeDataString(FleetRoster.ThisMachine)}" +
                    $"&username={Uri.EscapeDataString(config.Username ?? "")}" +
                    $"&minecraft={Uri.EscapeDataString(minecraft ?? "")}" +
                    $"&file={Uri.EscapeDataString(fileName)}";

                using var response = await http.PostAsync(
                    $"http://{hostAddress}{CrashInbox.Endpoint}{query}", content, ct);

                return response.IsSuccessStatusCode;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// Hands the host whatever crash reports this machine has not sent yet.
        /// </summary>
        /// <remarks>
        /// A report is marked sent only when the host confirms it, so a host that was
        /// switched off gets the report next time instead of it being lost. The host
        /// drops duplicates by content hash, so a report sent twice costs nothing.
        /// </remarks>
        public static async Task<int> SendUnsentCrashesAsync(
            string hostAddress, AppConfig config, string version, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(hostAddress) || string.IsNullOrWhiteSpace(version))
                return 0;

            int sent = 0;

            foreach (var (report, sha1) in CrashInbox.Unsent(version))
            {
                ct.ThrowIfCancellationRequested();

                string text;
                try { text = File.ReadAllText(report.FullPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                if (await SendCrashAsync(hostAddress, config, version, report.FileName, text, ct))
                {
                    CrashInbox.RememberSent(sha1);
                    sent++;
                }
            }

            return sent;
        }
    }
}
