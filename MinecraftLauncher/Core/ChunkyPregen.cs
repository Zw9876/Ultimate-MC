using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Driving Chunky pre-generation from the launcher, and reading its progress
    /// back out of the server console.
    /// </summary>
    /// <remarks>
    /// Pre-generating is the single biggest thing left that decides how a session
    /// feels: with twenty people spreading out, an ungenerated world makes the
    /// server build terrain during ticks, which is the most expensive thing it does.
    /// The commands were always available by hand — this exists because the host has
    /// roughly ninety minutes and a queue of people waiting, and in that window
    /// nobody is going to remember that radius is a half-width while
    /// <c>worldborder set</c> takes a diameter.
    ///
    /// Every command emitted here is one verified against a real server (see
    /// HANDOFF section 14). <c>chunky trim</c> is deliberately absent: it deletes
    /// chunks outside the selection, and nothing in this launcher should be one
    /// mis-click away from that.
    /// </remarks>
    public static class ChunkyPregen
    {
        /// <summary>Chunky's own default, and the one that matches a vanilla border.</summary>
        public const string ShapeSquare = "square";
        public const string ShapeCircle = "circle";

        /// <summary>Seconds between Chunky's progress lines. Frequent enough to drive a bar.</summary>
        public const int QuietSeconds = 5;

        /// <summary>
        /// Chunks per second to assume before a task has reported a real rate.
        /// Measured on the dev box (i7-7700): 1521 chunks in 49 s, so ~31. The hosts
        /// are Xeon Gold 6244s and will beat this, which is the direction an estimate
        /// should err in.
        /// </summary>
        public const double AssumedRate = 31.0;

        /// <summary>Bytes a generated chunk costs on disk, measured at ~9 KB.</summary>
        public const long BytesPerChunk = 9 * 1024;

        /// <summary>
        /// The fastest rate still worth believing as a *generation* rate.
        /// </summary>
        /// <remarks>
        /// Chunky counts chunks it skipped because they already existed at the same
        /// speed as chunks it built, so a run over already-generated ground reports
        /// absurd figures — a real run across an existing spawn area reported 1950
        /// chunks/sec. Remembering that would tell the host a radius of 3000 takes a
        /// minute. Terrain generation is bound by CPU and never approaches this, so
        /// anything above it is measuring disk reads, not work.
        /// </remarks>
        public const double MaxCredibleRate = 150.0;

        /// <summary>The slowest rate worth believing; below this a task is still warming up.</summary>
        public const double MinCredibleRate = 2.0;

        /// <summary>
        /// Whether a reported rate says anything useful about how fast this machine
        /// generates terrain.
        /// </summary>
        public static bool IsCredibleRate(double chunksPerSecond) =>
            chunksPerSecond is >= MinCredibleRate and <= MaxCredibleRate;

        /// <summary>The Nether is 1:8, so covering an overworld radius costs an eighth.</summary>
        public const int NetherRatio = 8;

        public const string Overworld = "minecraft:overworld";
        public const string Nether    = "minecraft:the_nether";
        public const string End       = "minecraft:the_end";

        // "[Chunky] Task running for minecraft:overworld. Processed: 1105 chunks (72.65%), ETA: 0:00:12, Rate: 32.0 cps, Current: -18, -19"
        private static readonly Regex RxRunning = new(
            @"\[Chunky\]\s*Task running for (\S+?)\.\s*Processed:\s*(\d+) chunks \(([\d.]+)%\),\s*ETA:\s*(\d+:\d{2}:\d{2}),\s*Rate:\s*([\d.]+) cps",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "[Chunky] Task finished for minecraft:overworld. Processed: 1521 chunks (100.00%), Total time: 0:00:49"
        private static readonly Regex RxFinished = new(
            @"\[Chunky\]\s*Task finished for (\S+?)\.\s*Processed:\s*(\d+) chunks \(([\d.]+)%\),\s*Total time:\s*(\d+:\d{2}:\d{2})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "[Chunky] Task started in minecraft:overworld for the square region centered at 0, 0 with radius 300."
        private static readonly Regex RxStarted = new(
            @"\[Chunky\]\s*Task started in (\S+?) for the (\w+) region centered at (-?\d+), (-?\d+) with radius (\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "[Chunky] No tasks to pause." — also continue/cancel.
        private static readonly Regex RxNoTasks = new(
            @"\[Chunky\]\s*No tasks to (pause|continue|cancel)\.",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>A progress report parsed out of one console line.</summary>
        public sealed record Progress(
            string Dimension,
            int Chunks,
            double Percent,
            TimeSpan Eta,
            double Rate,
            bool Finished,
            TimeSpan Elapsed)
        {
            /// <summary>One line fit for a status label.</summary>
            public string Describe()
            {
                string where = FriendlyDimension(Dimension);

                if (Finished)
                    return $"{where}: finished — {Chunks:N0} chunks in {Human(Elapsed)}.";

                return $"{where}: {Percent:0.0}% — {Chunks:N0} chunks, " +
                       $"{Rate:0.#}/sec, about {Human(Eta)} left.";
            }
        }

        /// <summary>What a task announced when it began.</summary>
        public sealed record Started(string Dimension, string Shape, int CenterX, int CenterZ, int Radius);

        /// <summary>What a run will cost, before committing to it.</summary>
        public sealed record Estimate(long Chunks, long Bytes, TimeSpan Duration)
        {
            public string Describe() =>
                $"about {Chunks:N0} chunks, {HumanBytes(Bytes)} on disk, roughly {Human(Duration)}";
        }

        /// <summary>True when this line is Chunky reporting on a task.</summary>
        public static bool TryParseProgress(string line, out Progress progress)
        {
            progress = null!;
            if (string.IsNullOrEmpty(line)) return false;

            var m = RxRunning.Match(line);
            if (m.Success)
            {
                progress = new Progress(
                    m.Groups[1].Value,
                    ParseInt(m.Groups[2].Value),
                    ParseDouble(m.Groups[3].Value),
                    ParseClock(m.Groups[4].Value),
                    ParseDouble(m.Groups[5].Value),
                    Finished: false,
                    Elapsed: TimeSpan.Zero);
                return true;
            }

            m = RxFinished.Match(line);
            if (m.Success)
            {
                progress = new Progress(
                    m.Groups[1].Value,
                    ParseInt(m.Groups[2].Value),
                    ParseDouble(m.Groups[3].Value),
                    TimeSpan.Zero,
                    Rate: 0,
                    Finished: true,
                    Elapsed: ParseClock(m.Groups[4].Value));
                return true;
            }

            return false;
        }

        /// <summary>True when this line is Chunky announcing a task has begun.</summary>
        public static bool TryParseStarted(string line, out Started started)
        {
            started = null!;
            if (string.IsNullOrEmpty(line)) return false;

            var m = RxStarted.Match(line);
            if (!m.Success) return false;

            started = new Started(
                m.Groups[1].Value,
                m.Groups[2].Value.ToLowerInvariant(),
                ParseInt(m.Groups[3].Value),
                ParseInt(m.Groups[4].Value),
                ParseInt(m.Groups[5].Value));
            return true;
        }

        /// <summary>
        /// True when Chunky said there was nothing to pause, continue or cancel —
        /// worth noticing, because it means the launcher's idea of a running task is
        /// out of step with the server's.
        /// </summary>
        public static bool IsNoTasksNotice(string line) =>
            !string.IsNullOrEmpty(line) && RxNoTasks.IsMatch(line);

        /// <summary>
        /// How many chunks a radius covers. Chunky's radius is a half-width in
        /// blocks, and it rounds outward to whole chunks, so a radius of 300 is
        /// (2 * ceil(300/16) + 1)² = 39² = 1521 — which is exactly what a real run
        /// reported, and reproduces the figures in HANDOFF section 14.
        /// </summary>
        public static long ChunksFor(int radiusBlocks, string shape = ShapeSquare)
        {
            if (radiusBlocks <= 0) return 0;

            long perSide = 2L * (long)Math.Ceiling(radiusBlocks / 16.0) + 1;
            long square = perSide * perSide;

            // A circle inscribed in that square is pi/4 of its area.
            return shape == ShapeCircle
                ? (long)Math.Round(square * Math.PI / 4.0)
                : square;
        }

        /// <summary>What a radius will cost in chunks, disk and time.</summary>
        public static Estimate EstimateFor(int radiusBlocks, string shape = ShapeSquare,
                                           double chunksPerSecond = AssumedRate)
        {
            long chunks = ChunksFor(radiusBlocks, shape);
            if (chunksPerSecond <= 0) chunksPerSecond = AssumedRate;

            return new Estimate(
                chunks,
                chunks * BytesPerChunk,
                TimeSpan.FromSeconds(chunks / chunksPerSecond));
        }

        /// <summary>
        /// The radius to give the Nether so it covers the same ground as an overworld
        /// radius. Generating the Nether at the overworld's radius wastes hours.
        /// </summary>
        public static int NetherRadiusFor(int overworldRadius) =>
            Math.Max(16, overworldRadius / NetherRatio);

        /// <summary>
        /// The commands that start a run, in order. Each one is verified against a
        /// real server; the dimension is set explicitly so a previous run in another
        /// dimension cannot leak into this one.
        /// </summary>
        public static IReadOnlyList<string> StartCommands(
            int radiusBlocks, string shape = ShapeSquare, string dimension = Overworld,
            int quietSeconds = QuietSeconds)
        {
            if (radiusBlocks <= 0)
                throw new ArgumentOutOfRangeException(nameof(radiusBlocks), "Radius must be positive.");

            return new[]
            {
                $"chunky world {dimension}",
                $"chunky shape {(shape == ShapeCircle ? ShapeCircle : ShapeSquare)}",
                $"chunky radius {radiusBlocks}",
                $"chunky quiet {Math.Max(1, quietSeconds)}",
                "chunky start"
            };
        }

        public static string PauseCommand()    => "chunky pause";
        public static string ContinueCommand() => "chunky continue";
        public static string CancelCommand()   => "chunky cancel";
        public static string ProgressCommand() => "chunky progress";

        /// <summary>
        /// Sets the world border to match a Chunky radius. The border takes a
        /// <b>diameter</b> while Chunky takes a radius, which is the easiest thing in
        /// this whole area to get wrong, so the conversion lives here rather than in
        /// anyone's head.
        /// </summary>
        public static IReadOnlyList<string> BorderCommands(int radiusBlocks, bool includeNether)
        {
            var commands = new List<string> { $"worldborder set {radiusBlocks * 2L}" };

            if (includeNether)
            {
                long netherDiameter = NetherRadiusFor(radiusBlocks) * 2L;
                commands.Add($"execute in {Nether} run worldborder set {netherDiameter}");
            }

            return commands;
        }

        /// <summary>"minecraft:the_nether" reads badly in a status line.</summary>
        public static string FriendlyDimension(string dimension) => dimension switch
        {
            Overworld => "Overworld",
            Nether    => "Nether",
            End       => "The End",
            _         => dimension
        };

        /// <summary>Durations as a person would say them.</summary>
        public static string Human(TimeSpan t)
        {
            if (t.TotalSeconds < 1)  return "a moment";
            if (t.TotalMinutes < 1)  return $"{(int)t.TotalSeconds} sec";
            if (t.TotalHours   < 1)  return $"{(int)t.TotalMinutes} min";

            int hours = (int)t.TotalHours;
            int mins  = t.Minutes;
            return mins == 0 ? $"{hours} hr" : $"{hours} hr {mins} min";
        }

        public static string HumanBytes(long bytes)
        {
            const long gb = 1024L * 1024 * 1024;
            const long mb = 1024L * 1024;

            if (bytes >= gb) return $"{bytes / (double)gb:0.#} GB";
            if (bytes >= mb) return $"{bytes / (double)mb:0} MB";
            return $"{bytes / 1024.0:0} KB";
        }

        private static int ParseInt(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

        private static double ParseDouble(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

        /// <summary>Chunky prints h:mm:ss, which TimeSpan.Parse reads as days when hours exceed 24.</summary>
        private static TimeSpan ParseClock(string s)
        {
            var parts = s.Split(':');
            if (parts.Length != 3) return TimeSpan.Zero;

            return new TimeSpan(ParseInt(parts[0]), ParseInt(parts[1]), ParseInt(parts[2]));
        }
    }
}
