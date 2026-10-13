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
    /// Machines the host has turned playing off for, and the check the PLAY button makes.
    /// </summary>
    /// <remarks>
    /// <para><b>Be clear about what this is.</b> This half runs on a computer the person
    /// physically controls, so it is a deterrent and not a lock: the cache can be
    /// deleted, the folder copied, or the official launcher installed. What it buys is
    /// that getting round it is a deliberate, visible act rather than typing a different
    /// name — and in a room of people who know each other that is most of the
    /// enforcement. The part that genuinely cannot be bypassed is the server-side
    /// <c>ban-ip</c> the same action issues, because the server enforces that one. See
    /// <c>MainWindow.BlockMachine</c>.</para>
    ///
    /// <para><b>Why a machine and not a username.</b> A Minecraft ban is by name, and
    /// anybody can pick another one — which is the whole reason this exists. So a block
    /// is matched on the computer's name <i>and</i> its Windows <c>MachineGuid</c>, on
    /// either, which survives a rename, a launcher reinstall and a copy of the folder
    /// elsewhere. Defeating it needs local admin. A deliberately rejected option was a
    /// hardware fingerprint from the SMBIOS: it would survive even a Windows reinstall,
    /// and it is more collection than this problem justifies.</para>
    ///
    /// <para><b>The host's own machine is never blockable.</b> Checked at the point of
    /// enforcement rather than when the list is written, so a list carried onto the host
    /// from somewhere else cannot lock it out of its own launcher either.</para>
    /// </remarks>
    public static class PlayBlocks
    {
        /// <summary>Where a client asks whether it is blocked.</summary>
        public const string Endpoint = "/launcher/blocks";

        /// <summary>The host's master copy, beside the launcher.</summary>
        public static string FilePath => Path.Combine(Paths.BaseDir, "play-blocks.json");

        /// <summary>
        /// The last answer this machine was given.
        /// </summary>
        /// <remarks>
        /// The reason this exists: without it, pulling out the network cable defeats the
        /// whole feature, because an unreachable host would have to mean "allowed".
        /// Failing closed instead is not an option — it would stop everybody playing
        /// single-player whenever the host is switched off.
        ///
        /// So the verdict is remembered. An unblock applies the next time the machine
        /// can see the host, which is also exactly when somebody wants to play on it.
        /// </remarks>
        public static string CachePath => Path.Combine(Paths.BaseDir, "play-blocks-cache.json");

        // ── The format ───────────────────────────────────────────────

        public sealed class Block
        {
            /// <summary>Computer name, as the fleet roster records it.</summary>
            [JsonPropertyName("machine")] public string Machine { get; set; } = "";

            /// <summary>Windows' own machine identifier, when the client reported one.</summary>
            [JsonPropertyName("guid")] public string? Guid { get; set; }

            /// <summary>Whoever was last playing there. For the message, not for matching.</summary>
            [JsonPropertyName("username")] public string? Username { get; set; }

            /// <summary>Shown to the person. Worth filling in.</summary>
            [JsonPropertyName("reason")] public string? Reason { get; set; }

            [JsonPropertyName("since")] public DateTime Since { get; set; }

            /// <summary>When it lifts itself, UTC. Null means it stays until removed.</summary>
            [JsonPropertyName("until")] public DateTime? Until { get; set; }

            /// <summary>Which machine set it, so a list is traceable.</summary>
            [JsonPropertyName("by")] public string? By { get; set; }

            [JsonIgnore]
            public bool IsTimed => Until is not null;

            [JsonIgnore]
            public string WhoText => string.IsNullOrWhiteSpace(Username)
                ? Machine
                : $"{Username} ({Machine})";

            [JsonIgnore]
            public string UntilText => Until is null
                ? "until removed"
                : Until.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Playing turned off for everyone at once, for maintenance.
        /// </summary>
        /// <remarks>
        /// Deliberately not expressed as a <see cref="Block"/> matching everybody.
        /// <see cref="Matches"/> refuses an entry with no machine and no GUID precisely
        /// so a malformed line cannot stop a whole room, and a wildcard entry would mean
        /// fighting that rail rather than keeping it.
        ///
        /// It is also a different thing from "shut down for everyone". That is an event
        /// with a countdown somebody can decline — right for "we are done for the
        /// evening". This is a gate: running games are closed without a countdown and
        /// nobody can decline, because the point is to get Minecraft out of the way of
        /// something, and an option to keep playing defeats it.
        /// </remarks>
        public sealed class Pause
        {
            /// <summary>New every time it is turned on, so a watcher acts on each once.</summary>
            [JsonPropertyName("id")] public string Id { get; set; } = "";

            [JsonPropertyName("reason")] public string? Reason { get; set; }
            [JsonPropertyName("since")]  public DateTime Since { get; set; }

            /// <summary>When it lifts itself, UTC. Null means it stays until turned off.</summary>
            [JsonPropertyName("until")] public DateTime? Until { get; set; }

            [JsonPropertyName("by")] public string? By { get; set; }

            [JsonIgnore]
            public string UntilText => Until is null
                ? "until it is turned back on"
                : Until.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        public sealed class Listing
        {
            [JsonPropertyName("blocks")] public List<Block> Blocks { get; set; } = new();

            /// <summary>Present and unexpired means nobody plays. Null means normal.</summary>
            [JsonPropertyName("pause")] public Pause? Pause { get; set; }
        }

        // ── Matching ─────────────────────────────────────────────────

        public static bool HasExpired(Block block, DateTime nowUtc) =>
            block.Until is not null && block.Until.Value <= nowUtc;

        /// <summary>
        /// Whether a block names this machine.
        /// </summary>
        /// <remarks>
        /// Either identifier is enough, which is the point — renaming the computer leaves
        /// the GUID, and a fresh Windows install leaves the name. An entry with no GUID
        /// still matches by name, so a machine running a launcher too old to report one
        /// is not immune.
        ///
        /// Empty is never a match. A block with no machine and no GUID would otherwise
        /// match every computer on the network at once.
        /// </remarks>
        public static bool Matches(Block block, string? machine, string? guid)
        {
            bool byName = !string.IsNullOrWhiteSpace(block.Machine) &&
                          !string.IsNullOrWhiteSpace(machine) &&
                          string.Equals(block.Machine.Trim(), machine!.Trim(),
                                        StringComparison.OrdinalIgnoreCase);

            bool byGuid = !string.IsNullOrWhiteSpace(block.Guid) &&
                          !string.IsNullOrWhiteSpace(guid) &&
                          string.Equals(block.Guid.Trim(), guid!.Trim(),
                                        StringComparison.OrdinalIgnoreCase);

            return byName || byGuid;
        }

        /// <summary>
        /// The block in force for a machine, or null. Expired ones are ignored.
        /// </summary>
        public static Block? InForce(Listing listing, string? machine, string? guid, DateTime nowUtc) =>
            listing.Blocks.FirstOrDefault(b => !HasExpired(b, nowUtc) && Matches(b, machine, guid));

        /// <summary>
        /// Whether this computer may start a game, and why not when it may not.
        /// </summary>
        /// <remarks>
        /// The admin exemption lives here, at the point of enforcement, so no list from
        /// anywhere can stop the host using its own launcher.
        /// </remarks>
        public static Block? BlockOnThisMachine(Listing listing, DateTime nowUtc)
        {
            if (RequiredMods.AdminEnabled) return null;
            return InForce(listing, FleetRoster.ThisMachine, ThisMachineGuid, nowUtc);
        }

        /// <summary>
        /// The pause as it applies to this computer. Null on the host's own machine.
        /// </summary>
        /// <remarks>
        /// The same exemption as individual blocks, and for a sharper reason: the whole
        /// point of pausing everyone is usually to do something to the launchers, and
        /// being locked out of your own while doing it would be absurd.
        /// </remarks>
        public static Pause? PauseOnThisMachine(Listing listing, DateTime nowUtc)
        {
            if (RequiredMods.AdminEnabled) return null;
            return PauseInForce(listing, nowUtc);
        }

        /// <summary>What to tell the person, in plain words.</summary>
        public static string Explain(Block block)
        {
            var lines = new List<string>
            {
                "Playing has been turned off on this computer by whoever is hosting."
            };

            if (!string.IsNullOrWhiteSpace(block.Reason))
            {
                lines.Add("");
                lines.Add($"Reason given: {block.Reason!.Trim()}");
            }

            lines.Add("");
            lines.Add(block.Until is null
                ? "It stays until they lift it. Go and talk to them."
                : $"It lifts by itself at {block.UntilText}. You do not need to do anything.");

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// Removes blocks that have run out, and the pause if it has. Says how many went,
        /// counting the pause as one.
        /// </summary>
        public static int DropExpired(Listing listing, DateTime nowUtc)
        {
            int gone = listing.Blocks.RemoveAll(b => HasExpired(b, nowUtc));

            if (listing.Pause is { Until: not null } p && p.Until!.Value <= nowUtc)
            {
                listing.Pause = null;
                gone++;
            }

            return gone;
        }

        /// <summary>The pause if one is in force, or null.</summary>
        public static Pause? PauseInForce(Listing listing, DateTime nowUtc)
        {
            var pause = listing.Pause;
            if (pause is null) return null;
            return pause.Until is not null && pause.Until.Value <= nowUtc ? null : pause;
        }

        /// <summary>
        /// Turns playing off for everyone. A fresh id each time, so a watcher that
        /// already acted on an earlier pause acts on this one too.
        /// </summary>
        public static Pause SetPause(
            Listing listing, string? reason, TimeSpan? duration, string? by, DateTime nowUtc)
        {
            listing.Pause = new Pause
            {
                Id     = Guid.NewGuid().ToString("N"),
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason!.Trim(),
                Since  = nowUtc,
                Until  = duration is null ? null : nowUtc + duration.Value,
                By     = by
            };

            return listing.Pause;
        }

        public static bool ClearPause(Listing listing)
        {
            if (listing.Pause is null) return false;
            listing.Pause = null;
            return true;
        }

        /// <summary>What to tell somebody who pressed PLAY while everything is paused.</summary>
        public static string ExplainPause(Pause pause)
        {
            var lines = new List<string>
            {
                "Playing is paused for everyone at the moment.",
                "",
                "Whoever is hosting is doing something that needs Minecraft closed — " +
                "updating the launchers, or working on the server."
            };

            if (!string.IsNullOrWhiteSpace(pause.Reason))
            {
                lines.Add("");
                lines.Add($"They said: {pause.Reason!.Trim()}");
            }

            lines.Add("");
            lines.Add(pause.Until is null
                ? "Try again in a few minutes."
                : $"It lifts at about {pause.UntilText}.");

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>Adds or replaces the block for a machine.</summary>
        public static Block Set(
            Listing listing, string machine, string? guid, string? username,
            string? reason, TimeSpan? duration, string? by, DateTime nowUtc)
        {
            listing.Blocks.RemoveAll(b => Matches(b, machine, guid));

            var block = new Block
            {
                Machine  = machine.Trim(),
                Guid     = string.IsNullOrWhiteSpace(guid) ? null : guid!.Trim(),
                Username = string.IsNullOrWhiteSpace(username) ? null : username!.Trim(),
                Reason   = string.IsNullOrWhiteSpace(reason) ? null : reason!.Trim(),
                Since    = nowUtc,
                Until    = duration is null ? null : nowUtc + duration.Value,
                By       = by
            };

            listing.Blocks.Add(block);
            return block;
        }

        public static bool Remove(Listing listing, string? machine, string? guid) =>
            listing.Blocks.RemoveAll(b => Matches(b, machine, guid)) > 0;

        // ── This machine ─────────────────────────────────────────────

        private static string? _guid;
        private static bool _guidRead;

        /// <summary>
        /// Windows' own identifier for this installation.
        /// </summary>
        /// <remarks>
        /// <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c>, written when Windows
        /// is installed. Read-only here and needs no admin to read. Chosen over the
        /// launcher's own <c>computer_uuid.dat</c> because that one is a file beside the
        /// exe: deleting it regenerates a new identity, which would make a block a
        /// one-click problem.
        ///
        /// Null when it cannot be read, and the caller must cope — matching then falls
        /// back to the computer name.
        /// </remarks>
        public static string? ThisMachineGuid
        {
            get
            {
                if (_guidRead) return _guid;
                _guidRead = true;

                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Cryptography");

                    _guid = key?.GetValue("MachineGuid") as string;
                    if (string.IsNullOrWhiteSpace(_guid)) _guid = null;
                }
                catch (Exception) { _guid = null; }

                return _guid;
            }
        }

        // ── Storage ──────────────────────────────────────────────────

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string ToJson(Listing listing) => JsonSerializer.Serialize(listing, Options);

        public static Listing Parse(string json)
        {
            try { return JsonSerializer.Deserialize<Listing>(json) ?? new Listing(); }
            catch (JsonException) { return new Listing(); }
        }

        /// <summary>
        /// The host's list. Unreadable reads as empty — a corrupt file must not be able
        /// to block a whole room.
        /// </summary>
        public static Listing LoadLocal()
        {
            try
            {
                return File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath)) : new Listing();
            }
            catch (Exception) { return new Listing(); }
        }

        public static bool SaveLocal(Listing listing)
        {
            try
            {
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, ToJson(listing));
                File.Move(temp, FilePath, overwrite: true);
                return true;
            }
            catch (Exception) { return false; }
        }

        public static Listing LoadCache()
        {
            try
            {
                return File.Exists(CachePath) ? Parse(File.ReadAllText(CachePath)) : new Listing();
            }
            catch (Exception) { return new Listing(); }
        }

        /// <summary>
        /// Remembers the host's answer.
        /// </summary>
        /// <remarks>
        /// Only the entries that concern this machine are kept. There is no reason for
        /// one person's computer to hold a copy of who else is blocked, and the file sits
        /// where they can read it.
        /// </remarks>
        public static bool SaveCache(Listing listing)
        {
            try
            {
                string machine = FleetRoster.ThisMachine;
                string? guid = ThisMachineGuid;

                var mine = new Listing
                {
                    Blocks = listing.Blocks.Where(b => Matches(b, machine, guid)).ToList(),

                    // The pause is cached as well, and has to be: it applies to this
                    // machine like anyone else's, so leaving it out would mean pulling
                    // out the network cable defeats a maintenance pause.
                    Pause = listing.Pause
                };

                string temp = CachePath + ".tmp";
                File.WriteAllText(temp, ToJson(mine));
                File.Move(temp, CachePath, overwrite: true);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
