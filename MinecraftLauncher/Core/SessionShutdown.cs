using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>A host's announcement that everyone should close Minecraft.</summary>
    /// <param name="Id">Unique per announcement, so a watcher acts on each one once.</param>
    /// <param name="Countdown">Seconds to count down before the game is closed.</param>
    public sealed record ShutdownNotice(string Id, int Countdown);

    /// <summary>
    /// Host side: holds the current "everyone close Minecraft" announcement.
    /// </summary>
    /// <remarks>
    /// Clients ask for it rather than being told. A launcher cannot push a message
    /// into another machine without Windows Firewall getting in the way — unsolicited
    /// inbound traffic is blocked by default, and allowing it needs an administrator
    /// on every client. Clients asking outward is the same traffic the skin system
    /// already relies on, so it simply works.
    /// </remarks>
    public sealed class ShutdownAnnouncer
    {
        private readonly object _lock = new();
        private string? _id;
        private int _countdown;
        private DateTime _announcedUtc;

        /// <summary>Starts a new announcement, replacing any earlier one.</summary>
        public void Announce(int countdownSeconds)
        {
            lock (_lock)
            {
                _id = Guid.NewGuid().ToString("N");
                _countdown = Math.Clamp(countdownSeconds, 1, 60);
                _announcedUtc = DateTime.UtcNow;
            }
        }

        public void Clear()
        {
            lock (_lock) _id = null;
        }

        /// <summary>
        /// The announcement as clients see it. Expires on the host after a minute, so a
        /// launcher started later in the evening is never closed by a shutdown that has
        /// already happened. Worked out here rather than on the client, which avoids
        /// trusting two machines' clocks to agree.
        /// </summary>
        public string ToJson()
        {
            lock (_lock)
            {
                bool live = _id is not null &&
                            DateTime.UtcNow - _announcedUtc < SessionShutdown.NoticeLifetime;

                return live
                    ? JsonSerializer.Serialize(new { shutdown = true, id = _id, countdown = _countdown })
                    : """{"shutdown":false}""";
            }
        }
    }

    /// <summary>
    /// Client side of "close Minecraft for everyone", plus the signal that tells the
    /// background game watchers to get out of the way of an update.
    /// </summary>
    public static class SessionShutdown
    {
        public const int DefaultCountdownSeconds = 5;
        public const string Endpoint = "/launcher/shutdown";

        /// <summary>How long an announcement stays live on the host.</summary>
        public static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Every background watcher opens this event and exits when it is set.
        /// </summary>
        /// <remarks>
        /// A watcher is a running copy of MinecraftLauncher.exe, so while one is alive
        /// Windows keeps the exe locked and a self-update cannot replace it. Someone who
        /// reopens the launcher mid-game to take an update is exactly that case, so the
        /// update signals this before it swaps the files. Losing a watcher only means
        /// that one game will not auto-close tonight — nothing else depends on it.
        /// </remarks>
        public const string WatcherExitEventName = @"Local\MinecraftPortableLauncher.WatcherExit";

        /// <summary>Opens (or creates) the shared watcher-exit event.</summary>
        public static EventWaitHandle OpenWatcherExitEvent() =>
            new(false, EventResetMode.ManualReset, WatcherExitEventName);

        /// <summary>Tells every background watcher on this machine to exit now.</summary>
        public static void SignalWatchersToExit()
        {
            try
            {
                using var exit = OpenWatcherExitEvent();
                exit.Set();
            }
            catch
            {
                // Best effort: at worst the update's swap retries until the lock goes.
            }
        }

        /// <summary>Parses the host's reply. Null when nothing is being announced.</summary>
        public static ShutdownNotice? Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("shutdown", out var flag) || !flag.GetBoolean())
                    return null;

                string? id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
                int countdown = root.TryGetProperty("countdown", out var c) && c.TryGetInt32(out int n)
                    ? n
                    : DefaultCountdownSeconds;

                return string.IsNullOrWhiteSpace(id)
                    ? null
                    : new ShutdownNotice(id, Math.Clamp(countdown, 1, 60));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The outcome of asking a host.</summary>
        public enum PollResult { Nothing, Announced, Unsupported, Unreachable }

        /// <summary>
        /// Asks a host whether it is shutting down.
        /// </summary>
        /// <remarks>
        /// <see cref="PollResult.Unsupported"/> means the host answered but has no such
        /// endpoint — it runs an older launcher. The caller should slow right down in
        /// that case, or it will fill that host's skin-server log with a request it does
        /// not recognise every second.
        /// </remarks>
        public static async Task<(PollResult Result, ShutdownNotice? Notice)> PollAsync(
            HttpClient http, string hostAddress, CancellationToken ct = default)
        {
            try
            {
                using var response = await http.GetAsync($"http://{hostAddress}{Endpoint}", ct);
                if (!response.IsSuccessStatusCode) return (PollResult.Unsupported, null);

                var notice = Parse(await response.Content.ReadAsStringAsync(ct));
                return notice is null ? (PollResult.Nothing, null) : (PollResult.Announced, notice);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return (PollResult.Unreachable, null);
            }
        }
    }
}
