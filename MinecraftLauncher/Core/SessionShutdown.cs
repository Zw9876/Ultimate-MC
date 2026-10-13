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
    /// A host's announcement that nobody is to be playing right now.
    /// </summary>
    /// <remarks>
    /// Carried on the same channel as <see cref="ShutdownNotice"/> because every game
    /// watcher already polls it once a second. A second endpoint would mean twenty
    /// machines making forty requests a second instead of twenty, for information the
    /// first request could have carried.
    ///
    /// Unlike a shutdown this has <b>no countdown and cannot be declined</b>. A
    /// shutdown is "we are finishing for the evening", so declining it is reasonable.
    /// This is "Minecraft needs to be shut so I can do something", and an option to
    /// keep playing would defeat the only thing it is for.
    /// </remarks>
    public sealed record PauseNotice(string Id, string? Reason);

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
        private PlayBlocks.Pause? _pause;

        /// <summary>
        /// The "nobody plays right now" state, as clients polling this will be told.
        /// </summary>
        /// <remarks>
        /// Held in memory because this endpoint is polled once a second by every
        /// client, and re-reading <c>play-blocks.json</c> twenty times a second to
        /// answer it would be wasteful for a value that changes twice an evening.
        /// <c>play-blocks.json</c> remains the source of truth — the skin server loads
        /// this from it on start, so a restart does not forget a live pause, and the
        /// PLAY gate reads the file rather than this.
        /// </remarks>
        public void SetPause(PlayBlocks.Pause? pause)
        {
            lock (_lock) _pause = pause;
        }

        public PlayBlocks.Pause? CurrentPause
        {
            get { lock (_lock) return Live(_pause); }
        }

        /// <summary>A pause that has reached its own end time is no longer one.</summary>
        private static PlayBlocks.Pause? Live(PlayBlocks.Pause? pause) =>
            pause is not null && pause.Until is not null && pause.Until.Value <= DateTime.UtcNow
                ? null
                : pause;

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

                var pause = Live(_pause);

                // Both fields always present, and an older client simply ignores the
                // one it does not know about.
                return JsonSerializer.Serialize(new
                {
                    shutdown = live,
                    id = live ? _id : null,
                    countdown = live ? _countdown : 0,
                    paused = pause is not null,
                    pauseId = pause?.Id,
                    pauseReason = pause?.Reason
                });
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

        /// <summary>
        /// Parses the "nobody plays right now" half of the host's reply.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Parse"/> and tolerant of the fields being absent,
        /// because a host on an older build answers with only the shutdown half and
        /// must read as "not paused" rather than as a malformed reply.
        /// </remarks>
        public static PauseNotice? ParsePause(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("paused", out var flag) ||
                    flag.ValueKind != JsonValueKind.True)
                    return null;

                string? id = root.TryGetProperty("pauseId", out var i) ? i.GetString() : null;
                string? reason = root.TryGetProperty("pauseReason", out var r) ? r.GetString() : null;

                // No id means a watcher could not tell one pause from the next, so it
                // would either act repeatedly or not at all. Treat it as nothing.
                return string.IsNullOrWhiteSpace(id) ? null : new PauseNotice(id!, reason);
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
        public static async Task<(PollResult Result, ShutdownNotice? Notice, PauseNotice? Pause)> PollAsync(
            HttpClient http, string hostAddress, CancellationToken ct = default)
        {
            try
            {
                using var response = await http.GetAsync($"http://{hostAddress}{Endpoint}", ct);
                if (!response.IsSuccessStatusCode) return (PollResult.Unsupported, null, null);

                string body = await response.Content.ReadAsStringAsync(ct);

                var notice = Parse(body);
                var pause = ParsePause(body);

                // Announced when either applies, so the caller keeps polling at the
                // normal rate rather than backing off as if nothing were happening.
                return notice is null && pause is null
                    ? (PollResult.Nothing, null, null)
                    : (PollResult.Announced, notice, pause);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return (PollResult.Unreachable, null, null);
            }
        }
    }
}
