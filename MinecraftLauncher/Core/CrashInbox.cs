using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Crash reports from the other machines, collected on the host.
    /// </summary>
    /// <remarks>
    /// The crash viewer reads reports well, but it reads them on the machine that
    /// crashed — and the person who fixes a crash is the one hosting. So a crash
    /// currently means walking across the room, or it means nobody ever looks.
    ///
    /// A report is stored as two files: the crash text exactly as the game wrote it,
    /// and a small JSON sidecar saying which machine and which person it came from.
    /// Keeping them apart matters — the text has to stay byte-for-byte what the game
    /// produced so <see cref="CrashReports.Parse"/> reads it here the same way it reads
    /// it there, and so anything pasted into a mod's issue tracker is the real thing.
    /// Stuffing a header into the top of the file would have broken both.
    ///
    /// Reports arriving from other machines are untrusted input: the file name is
    /// generated here rather than taken from the sender, the size is capped, and the
    /// text is only ever parsed and displayed, never run.
    /// </remarks>
    public static class CrashInbox
    {
        /// <summary>Where a client posts a crash report.</summary>
        public const string Endpoint = "/launcher/crash";

        /// <summary>The host's collection, beside the launcher.</summary>
        public static string Folder => Path.Combine(Paths.BaseDir, "crash-inbox");

        /// <summary>
        /// A crash report is text. One megabyte is already a long one, and this is the
        /// only place a client can put bytes on the host's disk unprompted.
        /// </summary>
        public const int MaxBytes = 1024 * 1024;

        /// <summary>How many to keep before the oldest are dropped.</summary>
        public const int Keep = 60;

        // ── The sidecar ──────────────────────────────────────────────

        public sealed class Sender
        {
            [JsonPropertyName("machine")]  public string Machine { get; set; } = "";
            [JsonPropertyName("username")] public string? Username { get; set; }
            [JsonPropertyName("minecraft")] public string? Minecraft { get; set; }

            /// <summary>The crash report's original file name on the machine that crashed.</summary>
            [JsonPropertyName("file")] public string? File { get; set; }

            /// <summary>When the host received it, UTC.</summary>
            [JsonPropertyName("received")] public DateTime Received { get; set; }

            /// <summary>SHA-1 of the report text, which is how a duplicate is recognised.</summary>
            [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
        }

        /// <summary>A received report: who sent it, and the parsed crash itself.</summary>
        public sealed record Arrived(Sender From, CrashReports.Report? Report, string TextPath)
        {
            public string Who => string.IsNullOrWhiteSpace(From.Username)
                ? From.Machine
                : $"{From.Username} ({From.Machine})";

            public string Headline => Report?.Headline ?? "Crash (could not be read)";

            public string WhenText =>
                From.Received.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            public string VersionText => From.Minecraft ?? Report?.MinecraftVersion ?? "—";
        }

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // ── Receiving (host) ─────────────────────────────────────────

        /// <summary>
        /// Stores a report that arrived from another machine.
        /// </summary>
        /// <remarks>
        /// Returns null when the report is a duplicate or cannot be stored. A duplicate
        /// is not an error worth reporting: a client that crashed, was turned off before
        /// it could say so, and sends the same report tomorrow is behaving correctly.
        /// </remarks>
        public static Arrived? Receive(Sender from, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            string sha1 = Sha1Of(text);
            from.Sha1 = sha1;
            if (from.Received == default) from.Received = DateTime.UtcNow;

            try
            {
                Directory.CreateDirectory(Folder);

                // Already have it. Checked by content hash rather than by file name,
                // because the name is ours and the content is what identifies a crash.
                if (All().Any(a => string.Equals(a.From.Sha1, sha1, StringComparison.OrdinalIgnoreCase)))
                    return null;

                string stem = Path.Combine(Folder, StemFor(from, sha1));

                File.WriteAllText(stem + ".txt", text);
                File.WriteAllText(stem + ".json", JsonSerializer.Serialize(from, Options));

                Prune();

                return new Arrived(from, CrashReports.TryParse(stem + ".txt"), stem + ".txt");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// The file name, built here from the sender's details rather than taken from
        /// them. Nothing a client sends reaches the filesystem as a path.
        /// </summary>
        private static string StemFor(Sender from, string sha1)
        {
            string who = WorldBackups.Sanitise(
                string.IsNullOrWhiteSpace(from.Username) ? from.Machine : from.Username!);

            return $"{from.Received:yyyy-MM-dd-HHmm}-{who}-{sha1[..8]}";
        }

        /// <summary>Everything received, newest first.</summary>
        public static List<Arrived> All()
        {
            if (!Directory.Exists(Folder)) return new List<Arrived>();

            var found = new List<Arrived>();

            foreach (string sidecar in Directory.GetFiles(Folder, "*.json"))
            {
                string text = sidecar[..^5] + ".txt";
                if (!File.Exists(text)) continue;

                Sender? from = null;
                try { from = JsonSerializer.Deserialize<Sender>(File.ReadAllText(sidecar)); }
                catch (Exception) { }

                if (from is null) continue;

                found.Add(new Arrived(from, CrashReports.TryParse(text), text));
            }

            return found.OrderByDescending(a => a.From.Received).ToList();
        }

        /// <summary>Drops the oldest reports beyond <see cref="Keep"/>.</summary>
        public static int Prune(int keep = Keep)
        {
            if (keep < 1) keep = 1;

            int removed = 0;

            foreach (var old in All().Skip(keep))
            {
                try
                {
                    File.Delete(old.TextPath);
                    string sidecar = old.TextPath[..^4] + ".json";
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            return removed;
        }

        /// <summary>Removes one report and its sidecar.</summary>
        public static bool Remove(Arrived report)
        {
            try
            {
                if (File.Exists(report.TextPath)) File.Delete(report.TextPath);
                string sidecar = report.TextPath[..^4] + ".json";
                if (File.Exists(sidecar)) File.Delete(sidecar);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // ── Sending (client) ─────────────────────────────────────────

        /// <summary>
        /// The machine's note of which reports it has already sent.
        /// </summary>
        /// <remarks>
        /// Beside the launcher and not in <c>config.txt</c>, for the same reason the
        /// required-mods declines are kept separately: config.txt is rewritten whenever
        /// somebody changes a setting, and a growing list of hashes in it would be one
        /// bad write away from losing their username and memory setting too.
        /// </remarks>
        public static string SentFilePath => Path.Combine(Paths.BaseDir, "crash-sent.txt");

        public static HashSet<string> AlreadySent()
        {
            var sent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (File.Exists(SentFilePath))
                    foreach (string line in File.ReadAllLines(SentFilePath))
                        if (line.Trim() is { Length: > 0 } hash && !hash.StartsWith('#'))
                            sent.Add(hash);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            return sent;
        }

        public static void RememberSent(string sha1)
        {
            try { File.AppendAllText(SentFilePath, sha1 + Environment.NewLine); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>
        /// The reports for a version that this machine has not sent yet, newest first.
        /// </summary>
        /// <remarks>
        /// Capped, and newest first, so a machine that has been crashing for a month does
        /// not spend a minute uploading its history the first time this feature runs.
        /// </remarks>
        public static List<(CrashReports.Report Report, string Sha1)> Unsent(string version, int most = 3)
        {
            var sent = AlreadySent();
            var found = new List<(CrashReports.Report, string)>();

            foreach (var report in CrashReports.List(version))
            {
                string? sha1 = TrySha1OfFile(report.FullPath);
                if (sha1 is null || sent.Contains(sha1)) continue;

                found.Add((report, sha1));
                if (found.Count >= most) break;
            }

            return found;
        }

        public static string Sha1Of(string text) =>
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        private static string? TrySha1OfFile(string path)
        {
            try { return Sha1Of(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
