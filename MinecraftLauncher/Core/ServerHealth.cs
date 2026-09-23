using System;
using System.Text.RegularExpressions;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Reads the server's own complaints out of its console output.
    /// </summary>
    /// <remarks>
    /// Every Minecraft server logs "Can't keep up!" when a tick takes longer than it
    /// should, on every loader and version. That makes it the one honest, universal
    /// answer to "is the server actually struggling, or is it my computer?" — worth far
    /// more than guessing from how the game feels.
    /// </remarks>
    public static class ServerHealth
    {
        // e.g. "Can't keep up! Is the server overloaded? Running 2051ms or 41 ticks behind"
        private static readonly Regex RxBehind = new(
            @"Can't keep up!.*?Running (\d+)\s*ms or (\d+) ticks behind",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>True when this console line is the server saying it fell behind.</summary>
        public static bool TryParseBehind(string line, out int milliseconds, out int ticks)
        {
            milliseconds = 0;
            ticks = 0;

            var m = RxBehind.Match(line ?? "");
            if (!m.Success) return false;

            return int.TryParse(m.Groups[1].Value, out milliseconds)
                 & int.TryParse(m.Groups[2].Value, out ticks);
        }

        /// <summary>Running tally of how badly a server is keeping up.</summary>
        public sealed class Tally
        {
            public int Count { get; private set; }
            public int WorstMs { get; private set; }
            public DateTime LastAt { get; private set; }

            public void Reset()
            {
                Count = 0;
                WorstMs = 0;
            }

            /// <summary>Feeds one console line in. True if it was a "fell behind" line.</summary>
            public bool Observe(string line)
            {
                if (!TryParseBehind(line, out int ms, out _)) return false;

                Count++;
                LastAt = DateTime.Now;
                if (ms > WorstMs) WorstMs = ms;
                return true;
            }

            /// <summary>A plain-language summary, or null while the server is coping.</summary>
            public string? Describe()
            {
                if (Count == 0) return null;

                string worst = WorstMs >= 1000
                    ? $"{WorstMs / 1000.0:0.#} seconds"
                    : $"{WorstMs} ms";

                return $"Fell behind {Count} time{(Count == 1 ? "" : "s")} this session " +
                       $"(worst: {worst} at {LastAt:HH:mm}).";
            }
        }
    }
}
