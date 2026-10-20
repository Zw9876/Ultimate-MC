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

            /// <summary>
            /// Windows' own identifier for this installation, so the host has something
            /// to block that survives renaming the computer. Absent from launchers older
            /// than this field, which remain blockable by name.
            /// </summary>
            [JsonPropertyName("guid")] public string? Guid { get; set; }

            /// <summary>The Minecraft version they last started, when they started one.</summary>
            [JsonPropertyName("minecraft")] public string? Minecraft { get; set; }
            [JsonPropertyName("loader")]    public string? Loader { get; set; }

            /// <summary>
            /// Whether this report came from somebody actually starting a game, as
            /// opposed to just opening the launcher.
            /// </summary>
            [JsonPropertyName("playing")] public bool Playing { get; set; }

            /// <summary>
            /// Whether <c>admin.flag</c> sits beside this machine's launcher.
            /// </summary>
            /// <remarks>
            /// Reported because a machine holding that file is EXEMPT from every pause
            /// and block by design - <see cref="PlayBlocks.PauseOnThisMachine"/> returns
            /// null for it - and until now there was no way for the host to know. On a
            /// fleet distributed by copying the launcher folder, one stray copy of the
            /// file makes a machine quietly unpausable and unblockable, and also unlocks
            /// the Admin tab on it.
            ///
            /// Nullable on purpose: null means "did not say", which is what a launcher
            /// older than this field reports, and that is a different thing from a
            /// machine that said no.
            /// </remarks>
            [JsonPropertyName("admin")] public bool? Admin { get; set; }
        }

        // ── What the host keeps ──────────────────────────────────────

        public sealed class Machine
        {
            [JsonPropertyName("machine")]  public string Name { get; set; } = "";
            [JsonPropertyName("username")] public string? Username { get; set; }
            [JsonPropertyName("launcher")] public string? Launcher { get; set; }
            [JsonPropertyName("guid")]     public string? Guid { get; set; }
            [JsonPropertyName("minecraft")] public string? Minecraft { get; set; }
            [JsonPropertyName("loader")]   public string? Loader { get; set; }
            [JsonPropertyName("address")]  public string? Address { get; set; }

            /// <summary>Last contact of any kind, UTC.</summary>
            [JsonPropertyName("seen")] public DateTime Seen { get; set; }

            /// <summary>Last time they actually started a game, UTC. Null if never.</summary>
            [JsonPropertyName("played")] public DateTime? Played { get; set; }

            /// <summary><inheritdoc cref="CheckIn.Admin"/></summary>
            [JsonPropertyName("admin")] public bool? Admin { get; set; }

            /// <summary>
            /// True only when the machine actually said it holds admin.flag. A machine
            /// that has not said is not treated as exempt, because guessing either way
            /// would be worse than showing it as unknown.
            /// </summary>
            [JsonIgnore]
            public bool IsExempt => Admin == true;

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

            /// <summary>The admin.flag column, worded so the bad case reads as bad.</summary>
            [JsonIgnore]
            public string AdminText => Admin switch
            {
                true  => "EXEMPT",
                false => "no",
                _     => "?"
            };

            [JsonIgnore]
            public string PlayingText => string.IsNullOrWhiteSpace(Minecraft)
                ? "—"
                : string.IsNullOrWhiteSpace(Loader) ? Minecraft! : $"{Minecraft} {Loader}";

            [JsonIgnore]
            public string SeenText => Describe(DateTime.UtcNow - Seen);

            /// <summary>
            /// Whether playing is turned off here, filled in by the Admin tab.
            /// </summary>
            /// <remarks>
            /// Settable and not serialized, rather than computed from a block list. The
            /// roster records what machines reported about themselves; whether one is
            /// blocked is the host's own decision and lives in <see cref="PlayBlocks"/>.
            /// Keeping the two apart means neither file can contradict the other.
            /// </remarks>
            [JsonIgnore]
            public string BlockText { get; set; } = "";
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

            if (!string.IsNullOrWhiteSpace(report.Guid))      machine.Guid      = report.Guid!.Trim();
            if (!string.IsNullOrWhiteSpace(report.Username))  machine.Username  = report.Username!.Trim();
            if (!string.IsNullOrWhiteSpace(report.Launcher))  machine.Launcher  = report.Launcher!.Trim();
            if (!string.IsNullOrWhiteSpace(report.Minecraft)) machine.Minecraft = report.Minecraft!.Trim();
            if (!string.IsNullOrWhiteSpace(report.Loader))    machine.Loader    = report.Loader!.Trim();
            if (!string.IsNullOrWhiteSpace(address))          machine.Address   = address;

            // Only when the report carries it, like every field above: a launcher too
            // old to send it must not be able to clear what a newer one established.
            if (report.Admin.HasValue) machine.Admin = report.Admin;

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
        /// Only the machines seen since a given moment — in practice, since this
        /// launcher opened.
        /// </summary>
        /// <remarks>
        /// <para>This exists because the roster accumulates. A machine last seen five
        /// days ago on an old build still counted toward "2 of 5 are too old to obey a
        /// pause", which made the one warning the host relies on untrustworthy: it was
        /// answering a question about last week.</para>
        ///
        /// <para><b>Filtered rather than wiped, deliberately.</b> Clearing the file on
        /// startup is the obvious move and it has a sharp edge: a client checks in at
        /// exactly two moments — when its launcher opens, and when a game starts — and
        /// never again. The host's own launcher restarts itself on every mandatory
        /// update, so a wipe part-way through an evening would drop everybody who was
        /// already playing and make the fleet look <i>smaller</i> than it is, which is
        /// the same class of wrong answer in the other direction. Keeping the records
        /// and scoping the question costs nothing and cannot do that.</para>
        /// </remarks>
        public static Listing Since(Listing listing, DateTime sinceUtc) =>
            new()
            {
                Machines = listing.Machines.Where(m => m.Seen >= sinceUtc).ToList()
            };

        /// <summary>
        /// Every machine that has reported playing under a given name, most recent first.
        /// </summary>
        /// <remarks>
        /// The roster records the name somebody last played under, so this is the way
        /// round from "who did it" to "which computer" — which is how a person actually
        /// thinks about it. Nobody knows their colleagues' computer names.
        ///
        /// More than one machine can have been used under one name, and the list is
        /// returned whole rather than reduced, so the caller can say so instead of
        /// silently picking.
        /// </remarks>
        public static List<Machine> WhoPlayedAs(Listing listing, string? username)
        {
            if (string.IsNullOrWhiteSpace(username)) return new List<Machine>();

            string want = username.Trim();

            return listing.Machines
                .Where(m => !string.IsNullOrWhiteSpace(m.Username) &&
                            string.Equals(m.Username!.Trim(), want, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Seen)
                .ToList();
        }

        /// <summary>
        /// The machine somebody is on now, or was on most recently. Null when no machine
        /// has ever reported that name.
        /// </summary>
        public static Machine? LastPlayedOn(Listing listing, string? username) =>
            WhoPlayedAs(listing, username).FirstOrDefault();

        /// <summary>Every name the roster has seen, for offering them rather than typing.</summary>
        public static List<string> KnownNames(Listing listing) =>
            listing.Machines
                .Where(m => !string.IsNullOrWhiteSpace(m.Username))
                .OrderByDescending(m => m.Seen)
                .Select(m => m.Username!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>Forgets every machine. The hard reset, for when the list is junk.</summary>
        public static bool ForgetAll()
        {
            var empty = new Listing();
            return Save(empty);
        }

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
        /// Machines that reported holding <c>admin.flag</c>, other than this one.
        /// </summary>
        /// <remarks>
        /// This machine is excluded because the host is MEANT to hold it - that is what
        /// unlocks the tab this list is shown on. Anywhere else it is a problem: that
        /// machine cannot be paused or blocked, and whoever uses it can pause everybody
        /// else.
        /// </remarks>
        public static List<Machine> ExemptElsewhere(Listing listing) =>
            listing.Machines
                   .Where(m => m.IsExempt &&
                               !string.Equals(m.Name, ThisMachine, StringComparison.OrdinalIgnoreCase))
                   .ToList();

        /// <summary>
        /// What to say above the list about admin.flag, or null when there is nothing
        /// worth saying.
        /// </summary>
        public static string? ExemptWarning(Listing listing)
        {
            var others = ExemptElsewhere(listing);
            int silent = listing.Machines.Count(m => m.Admin is null);

            if (others.Count == 0 && silent == 0) return null;

            var parts = new List<string>();

            if (others.Count > 0)
            {
                var names = others.Select(m =>
                    string.IsNullOrWhiteSpace(m.Username) ? m.Name : $"{m.Name} ({m.Username})");

                parts.Add(
                    $"{others.Count} machine{(others.Count == 1 ? " has" : "s have")} admin.flag beside " +
                    $"the launcher and {(others.Count == 1 ? "is" : "are")} therefore EXEMPT from every " +
                    $"pause and block, and can pause everyone else: {string.Join(", ", names)}. " +
                    "Delete that file there.");
            }

            if (silent > 0)
            {
                parts.Add(
                    $"{silent} {(silent == 1 ? "machine has" : "machines have")} not reported whether " +
                    "they hold it, which means a launcher from before this was recorded - they will " +
                    "say once they check in again.");
            }

            return string.Join("  ", parts);
        }

        /// <summary>
        /// A one-line answer to "is the fleet current?", which is the question this
        /// whole feature exists to stop guessing at.
        /// </summary>
        public static string Summarise(Listing listing, Version hostVersion)
        {
            int total = listing.Machines.Count;
            if (total == 0)
                return "Nothing has checked in since this launcher opened. Machines report " +
                       "themselves when somebody opens their launcher or presses PLAY, so " +
                       "give it until people start using them.";

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
                Guid      = PlayBlocks.ThisMachineGuid,
                Username  = string.IsNullOrWhiteSpace(config.Username) ? null : config.Username.Trim(),
                Launcher  = LauncherPackage.CurrentVersion.ToString(),
                Minecraft = string.IsNullOrWhiteSpace(minecraft) ? null : minecraft,
                Loader    = string.IsNullOrWhiteSpace(loader) ? null : loader,
                Playing   = playing,
                Admin     = RequiredMods.AdminEnabled
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
