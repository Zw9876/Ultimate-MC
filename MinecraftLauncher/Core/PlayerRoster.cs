using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Who is on the server, read from its console output.
    /// </summary>
    /// <remarks>
    /// The server never reports its player list unprompted, so this is assembled from
    /// the join and leave lines it does print, and corrected against the reply to a
    /// <c>list</c> command. The correction matters: the launcher only sees output from
    /// the moment it attached, so anyone who joined before that is invisible until
    /// something asks.
    ///
    /// Chat is the trap here. A player typing "I joined the game" produces a line
    /// containing exactly the words being matched, so the message is anchored: a real
    /// join line is the whole message, while chat always arrives wrapped in angle
    /// brackets.
    /// </remarks>
    public static class PlayerRoster
    {
        /// <summary>Minecraft usernames: 3-16 of letters, digits and underscore.</summary>
        private const string Name = @"[A-Za-z0-9_]{3,16}";

        /// <summary>Everything after the "[HH:mm:ss] [Server thread/INFO]: " preamble.</summary>
        private static readonly Regex RxMessage = new(
            @"^(?:\[[^\]]*\]\s*)*:?\s*(?<msg>.*?)\s*$", RegexOptions.Compiled);

        private static readonly Regex RxJoined = new(
            $@"^(?<name>{Name}) joined the game$", RegexOptions.Compiled);

        private static readonly Regex RxLeft = new(
            $@"^(?<name>{Name}) left the game$", RegexOptions.Compiled);

        // "There are 3 of a max of 25 players online: Zach, Sam, Alex"
        private static readonly Regex RxList = new(
            @"There are (?<online>\d+) of a max of (?<max>\d+) players online:\s*(?<names>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The command whose reply <see cref="TryParseList"/> reads.</summary>
        public const string ListCommand = "list";

        /// <summary>Strips the timestamp and thread prefix a server line carries.</summary>
        internal static string MessageOf(string line)
        {
            if (string.IsNullOrEmpty(line)) return "";

            // Take everything after the last "]: ", which is where the message starts on
            // every loader. Lines without that shape are used whole.
            int marker = line.LastIndexOf("]: ", StringComparison.Ordinal);
            string message = marker >= 0 ? line[(marker + 3)..] : line;

            return message.Trim();
        }

        public static bool TryParseJoin(string line, out string name)
        {
            var m = RxJoined.Match(MessageOf(line));
            name = m.Success ? m.Groups["name"].Value : "";
            return m.Success;
        }

        public static bool TryParseLeave(string line, out string name)
        {
            var m = RxLeft.Match(MessageOf(line));
            name = m.Success ? m.Groups["name"].Value : "";
            return m.Success;
        }

        /// <summary>Reads the reply to <c>list</c>, which is the authoritative roster.</summary>
        public static bool TryParseList(string line, out List<string> names, out int max)
        {
            names = new List<string>();
            max = 0;

            var m = RxList.Match(MessageOf(line));
            if (!m.Success) return false;

            max = int.TryParse(m.Groups["max"].Value, out int parsedMax) ? parsedMax : 0;

            names = m.Groups["names"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(n => Regex.IsMatch(n, $"^{Name}$"))
                .ToList();

            return true;
        }

        /// <summary>One player currently on the server.</summary>
        public sealed record Player(string Name, DateTime Since)
        {
            /// <summary>How long they have been on, as far as this launcher saw.</summary>
            public TimeSpan Playtime => DateTime.Now - Since;

            public string PlaytimeText
            {
                get
                {
                    var t = Playtime;
                    if (t.TotalMinutes < 1) return "just joined";
                    if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
                    return $"{(int)t.TotalHours} hr {t.Minutes} min";
                }
            }
        }

        /// <summary>Live roster, fed one console line at a time.</summary>
        public sealed class Roster
        {
            private readonly Dictionary<string, DateTime> _since = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Max players, once a <c>list</c> reply has said so. Zero until then.</summary>
            public int MaxPlayers { get; private set; }

            /// <summary>True once a <c>list</c> reply has been seen, so the roster is trustworthy.</summary>
            public bool Synced { get; private set; }

            public int Count => _since.Count;

            public IReadOnlyList<Player> Players =>
                _since.Select(kv => new Player(kv.Key, kv.Value))
                      .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                      .ToList();

            public void Clear()
            {
                _since.Clear();
                Synced = false;
                MaxPlayers = 0;
            }

            /// <summary>Feeds one console line in. True when the roster changed.</summary>
            public bool Observe(string line)
            {
                if (TryParseJoin(line, out string joined))
                {
                    // Keep the original time if a stale entry is still there, so a
                    // duplicate join line cannot reset someone's playtime.
                    if (_since.ContainsKey(joined)) return false;
                    _since[joined] = DateTime.Now;
                    return true;
                }

                if (TryParseLeave(line, out string left))
                    return _since.Remove(left);

                if (TryParseList(line, out var names, out int max))
                {
                    // Learning that nobody is on is itself a change: before the first
                    // reply the roster only knew it had not been told, and the header
                    // has to move from saying nothing to saying the server is empty.
                    bool firstReply = !Synced;

                    MaxPlayers = max;
                    Synced = true;

                    return Resync(names) || firstReply;
                }

                return false;
            }

            /// <summary>
            /// Replaces the roster with the authoritative list, keeping join times for
            /// anyone already known — their playtime should not restart just because
            /// something asked who was online.
            /// </summary>
            private bool Resync(List<string> names)
            {
                var replacement = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

                foreach (string name in names)
                    replacement[name] = _since.TryGetValue(name, out var since) ? since : DateTime.Now;

                bool changed = replacement.Count != _since.Count ||
                               !replacement.Keys.All(_since.ContainsKey);

                _since.Clear();
                foreach (var kv in replacement) _since[kv.Key] = kv.Value;

                return changed;
            }

            /// <summary>A line for the console header, e.g. "3 of 25 online: Zach, Sam, Alex".</summary>
            public string Describe()
            {
                if (Count == 0)
                    return Synced ? "Nobody on the server." : "Nobody on yet.";

                string who = string.Join(", ", Players.Select(p => p.Name));
                string capacity = MaxPlayers > 0 ? $"{Count} of {MaxPlayers}" : $"{Count}";

                return $"{capacity} online: {who}";
            }
        }
    }
}
