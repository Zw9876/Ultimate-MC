using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// What the host knows about the other machines: which build each one runs, and
    /// who was last playing on it.
    /// </summary>
    /// <remarks>
    /// This exists because of a specific, repeated failure. Five builds in a row went
    /// out with "published here, never confirmed deployed" written next to them, and
    /// every handoff had to admit the fleet state was simply unknown — the rollout zip
    /// kept being eaten on download and nobody could say which machine was on what.
    /// That is not a deployment problem; it is a visibility one, and guessing was the
    /// only tool available.
    ///
    /// So each launcher tells the host who it is. Machines are keyed by computer name,
    /// which is the one identifier that does not move: a person can change their
    /// Minecraft username between sessions, and the launcher's own version changes by
    /// design. The username is carried anyway, because the question actually being
    /// asked in the room is "whose machine is that?" and a computer name like
    /// <c>DESKTOP-7F3K2A1</c> does not answer it.
    ///
    /// Everything here is advisory. A machine that never checks in is simply absent from
    /// the list, not reported as a problem — it may be switched off, and a roster that
    /// cried wolf about every powered-down computer would be ignored within a week.
    /// </remarks>
    public static class FleetRoster
    {
        /// <summary>Where a client posts its own details.</summary>
        public const string Endpoint = "/launcher/checkin";

        /// <summary>The host's copy, beside the launcher. Never shipped in a pack.</summary>
        public static string FilePath => Path.Combine(Paths.BaseDir, "fleet.json");

        /// <summary>
        /// A machine is called stale once it has not been seen for this long. Only ever
        /// used to sort and to grey a row out.
        /// </summary>
        public static readonly TimeSpan Stale = TimeSpan.FromDays(14);

        // ── What a client sends ──────────────────────────────────────

        /// <summary>
        /// One machine's report of itself.
        /// </summary>
        /// <remarks>
        /// Deliberately small, and deliberately nothing but facts the host already has a
        /// reason to know. It travels over a LAN between machines in one room.
        /// </remarks>
        public sealed class CheckIn
        {
            [JsonPropertyName("machine")]  public string Machine { get; set; } = "";
            [JsonPropertyName("username")] public string? Username { get; set; }
            [JsonPropertyName("launcher")] public string? Launcher { get; set; }

            /// <summary>The Minecraft version they last started, when they started one.</summary>
            [JsonPropertyName("minecraft")] public string? Minecraft { get; set; }
            [JsonPropertyName("loader")]    public string? Loader { get; set; }

            /// <summary>
            /// Whether this report came from somebody actually starting a game, as
            /// opposed to just opening the launcher.
            /// </summary>
            [JsonPropertyName("playing")] public bool Playing { get; set; }
        }

        // ── What the host keeps ──────────────────────────────────────

        public sealed class Machine
        {
            [JsonPropertyName("machine")]  public string Name { get; set; } = "";
            [JsonPropertyName("username")] public string? Username { get; set; }
            [JsonPropertyName("launcher")] public string? Launcher { get; set; }
            [JsonPropertyName("minecraft")] public string? Minecraft { get; set; }
            [JsonPropertyName("loader")]   public string? Loader { get; set; }
            [JsonPropertyName("address")]  public string? Address { get; set; }

            /// <summary>Last contact of any kind, UTC.</summary>
            [JsonPropertyName("seen")] public DateTime Seen { get; set; }

            /// <summary>Last time they actually started a game, UTC. Null if never.</summary>
            [JsonPropertyName("played")] public DateTime? Played { get; set; }

            [JsonIgnore]
            public Version Version =>
                System.Version.TryParse(Launcher, out var v) ? v : new Version(0, 0, 0, 0);

            [JsonIgnore]
            public bool IsStale => DateTime.UtcNow - Seen > Stale;

            /// <summary>Who to look for in the room, which is the point of the column.</summary>
            [JsonIgnore]
            public string Who => string.IsNullOrWhiteSpace(Username) ? "—" : Username!;

            [JsonIgnore]
            public string LauncherText =>
                string.IsNullOrWhiteSpace(Launcher) ? "unknown" : Launcher!;

            [JsonIgnore]
            public string PlayingText => string.IsNullOrWhiteSpace(Minecraft)
                ? "—"
                : string.IsNullOrWhiteSpace(Loader) ? Minecraft! : $"{Minecraft} {Loader}";

            [JsonIgnore]
            public string SeenText => Describe(DateTime.UtcNow - Seen);
        }

        public sealed class Listing
        {
            [JsonPropertyName("machines")]
            public List<Machine> Machines { get; set; } = new();
        }

        /// <summary>How long ago, in the way a person would say it.</summary>
        public static string Describe(TimeSpan ago)
        {
            if (ago < TimeSpan.Zero)            return "just now";
            if (ago.TotalSeconds < 90)          return "just now";
            if (ago.TotalMinutes < 60)          return $"{(int)ago.TotalMinutes} min ago";
            if (ago.TotalHours   < 24)          return $"{(int)ago.TotalHours} h ago";
            if (ago.TotalDays    < 2)           return "yesterday";
            return $"{(int)ago.TotalDays} days ago";
        }

        // ── Merging ──────────────────────────────────────────────────

        /// <summary>
        /// Folds one check-in into the roster, in place.
        /// </summary>
        /// <remarks>
        /// Two rules worth stating because they are the difference between a useful list
        /// and a misleading one:
        ///
        /// <list type="bullet">
        /// <item>A field is only overwritten when the new report actually carries it. A
        /// launcher reporting at startup knows the username but not which version is
        /// about to be played, and must not blank out what last night's session
        /// recorded.</item>
        /// <item><c>Played</c> moves only for a report from somebody starting a game.
        /// Otherwise merely opening the launcher would look like playing, and the column
        /// people would use to see who is on tonight would be wrong.</item>
        /// </list>
        /// </remarks>
        public static Machine Merge(Listing listing, CheckIn report, string? address, DateTime nowUtc)
        {
            string name = (report.Machine ?? "").Trim();
            if (name.Length == 0) name = address ?? "unknown";

            var machine = listing.Machines.FirstOrDefault(
                m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

            if (machine is null)
            {
                machine = new Machine { Name = name };
                listing.Machines.Add(machine);
            }

            if (!string.IsNullOrWhiteSpace(report.Username))  machine.Username  = report.Username!.Trim();
            if (!string.IsNullOrWhiteSpace(report.Launcher))  machine.Launcher  = report.Launcher!.Trim();
            if (!string.IsNullOrWhiteSpace(report.Minecraft)) machine.Minecraft = report.Minecraft!.Trim();
            if (!string.IsNullOrWhiteSpace(report.Loader))    machine.Loader    = report.Loader!.Trim();
            if (!string.IsNullOrWhiteSpace(address))          machine.Address   = address;

            machine.Seen = nowUtc;
            if (report.Playing) machine.Played = nowUtc;

            return machine;
        }

        /// <summary>
        /// The roster in the order a person wants to read it: seen most recently first.
        /// </summary>
        public static List<Machine> Sorted(Listing listing) =>
            listing.Machines.OrderByDescending(m => m.Seen).ToList();

        /// <summary>
        /// How many machines are not yet on the host's build.
        /// </summary>
        /// <remarks>
        /// Strictly older only. A machine reporting a *newer* build than the host is not
        /// behind — it happens whenever a build is tested somewhere else first — and
        /// counting it as behind would make the one number anybody looks at wrong.
        /// </remarks>
        public static int Behind(Listing listing, Version hostVersion) =>
            listing.Machines.Count(m => m.Version > new Version(0, 0, 0, 0) &&
                                        m.Version < hostVersion);

        /// <summary>
        /// A one-line answer to "is the fleet current?", which is the question this
        /// whole feature exists to stop guessing at.
        /// </summary>
        public static string Summarise(Listing listing, Version hostVersion)
        {
            int total = listing.Machines.Count;
            if (total == 0)
                return "No machines have checked in yet. They report themselves when " +
                       "somebody opens the launcher or presses PLAY while this host is running.";

            int behind  = Behind(listing, hostVersion);
            int unknown = listing.Machines.Count(m => m.Version == new Version(0, 0, 0, 0));
            int stale   = listing.Machines.Count(m => m.IsStale);

            var parts = new List<string> { $"{total} machine{(total == 1 ? "" : "s")} seen" };

            parts.Add(behind == 0
                ? $"all on {hostVersion} or newer"
                : $"{behind} behind {hostVersion}");

            if (unknown > 0) parts.Add($"{unknown} did not say which build");
            if (stale > 0)   parts.Add($"{stale} not seen in {Stale.TotalDays:0} days");

            return string.Join(", ", parts) + ".";
        }

        // ── Storage ──────────────────────────────────────────────────

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Reads the host's roster. A missing or unreadable file reads as empty: this is
        /// a convenience list, and losing it must never stop the launcher starting.
        /// </summary>
        public static Listing Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new Listing();
                return Parse(File.ReadAllText(FilePath));
            }
            catch (Exception) { return new Listing(); }
        }

        public static Listing Parse(string json)
        {
            try
            {
                return JsonSerializer.Deserialize<Listing>(json) ?? new Listing();
            }
            catch (JsonException) { return new Listing(); }
        }

        public static string ToJson(Listing listing) => JsonSerializer.Serialize(listing, Options);

        /// <summary>
        /// Writes the roster, via a temporary file so an interrupted write cannot leave
        /// a half-written one behind.
        /// </summary>
        public static bool Save(Listing listing)
        {
            try
            {
                Directory.CreateDirectory(Paths.BaseDir);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, ToJson(listing));
                File.Move(temp, FilePath, overwrite: true);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Forgets one machine — a computer that has left the room for good.</summary>
        public static bool Forget(string machineName)
        {
            var listing = Load();
            int removed = listing.Machines.RemoveAll(
                m => string.Equals(m.Name, machineName, StringComparison.OrdinalIgnoreCase));

            return removed > 0 && Save(listing);
        }

        // ── This machine ─────────────────────────────────────────────

        /// <summary>What this computer calls itself.</summary>
        public static string ThisMachine
        {
            get
            {
                try
                {
                    string name = Environment.MachineName;
                    return string.IsNullOrWhiteSpace(name) ? "unknown" : name;
                }
                catch (Exception) { return "unknown"; }
            }
        }

        /// <summary>
        /// This machine's own report.
        /// </summary>
        /// <remarks>
        /// The username comes from the launcher's config, which is the name the person
        /// actually plays under — it is what the launcher passes to the game and what the
        /// skin server keys skins by.
        /// </remarks>
        public static CheckIn Describe(AppConfig config, string? minecraft, string? loader, bool playing) =>
            new()
            {
                Machine   = ThisMachine,
                Username  = string.IsNullOrWhiteSpace(config.Username) ? null : config.Username.Trim(),
                Launcher  = LauncherPackage.CurrentVersion.ToString(),
                Minecraft = string.IsNullOrWhiteSpace(minecraft) ? null : minecraft,
                Loader    = string.IsNullOrWhiteSpace(loader) ? null : loader,
                Playing   = playing
            };

        public static string ToJson(CheckIn report) => JsonSerializer.Serialize(report, Options);

        public static CheckIn? ParseCheckIn(string json)
        {
            try
            {
                var report = JsonSerializer.Deserialize<CheckIn>(json);
                return report is null || string.IsNullOrWhiteSpace(report.Machine) ? null : report;
            }
            catch (JsonException) { return null; }
        }
    }
}
