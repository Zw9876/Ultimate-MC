using System;
using System.Linq;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Reading Chunky's console output, and predicting what a pre-generation run costs.
    /// </summary>
    /// <remarks>
    /// Every input line below is copied verbatim from a real Chunky 1.5.3 run captured
    /// from the Fabric 26.1.2 server. Written against that capture rather than from
    /// memory, because a parser built on a guessed format fails silently.
    /// </remarks>
    public static class ChunkyPregenTests
    {
        private const string RunningLine =
            "[16:23:38] [Server thread/INFO]: [Chunky] Task running for minecraft:overworld. " +
            "Processed: 1105 chunks (72.65%), ETA: 0:00:12, Rate: 32.0 cps, Current: -18, -19";

        private const string FinishedLine =
            "[16:23:48] [Server thread/INFO]: [Chunky] Task finished for minecraft:overworld. " +
            "Processed: 1521 chunks (100.00%), Total time: 0:00:49";

        private const string StartedLine =
            "[16:22:59] [Server thread/INFO]: [Chunky] Task started in minecraft:overworld " +
            "for the square region centered at 0, 0 with radius 300.";

        public static void Run()
        {
            Parsing();
            Noise();
            ChunkCounts();
            Estimates();
            Commands();
        }

        private static void Parsing()
        {
            Section("parsing real Chunky output");

            Check("a running line parses", ChunkyPregen.TryParseProgress(RunningLine, out var run));
            if (run is not null)
            {
                Expect("  dimension", run.Dimension, "minecraft:overworld");
                Expect("  chunks", run.Chunks, 1105);
                Check("  percent", Math.Abs(run.Percent - 72.65) < 0.001, run.Percent.ToString());
                Expect("  eta", run.Eta, TimeSpan.FromSeconds(12));
                Check("  rate", Math.Abs(run.Rate - 32.0) < 0.001, run.Rate.ToString());
                Check("  not finished", !run.Finished);
            }

            Check("a finished line parses", ChunkyPregen.TryParseProgress(FinishedLine, out var done));
            if (done is not null)
            {
                Check("  finished flag", done.Finished);
                Expect("  chunks", done.Chunks, 1521);
                Expect("  elapsed", done.Elapsed, TimeSpan.FromSeconds(49));
                Check("  reads sensibly",
                      done.Describe().Contains("Overworld") && done.Describe().Contains("49 sec"),
                      done.Describe());
            }

            Check("a started line parses", ChunkyPregen.TryParseStarted(StartedLine, out var started));
            if (started is not null)
            {
                Expect("  shape", started.Shape, "square");
                Expect("  radius", started.Radius, 300);
                Check("  centre", started is { CenterX: 0, CenterZ: 0 });
            }

            Check("'No tasks to pause.' is recognised", ChunkyPregen.IsNoTasksNotice(
                "[16:23:56] [Server thread/INFO]: [Chunky] No tasks to pause."));
            Check("'No tasks to cancel.' is recognised", ChunkyPregen.IsNoTasksNotice(
                "[16:24:05] [Server thread/INFO]: [Chunky] No tasks to cancel."));
        }

        private static void Noise()
        {
            Section("lines that must not be mistaken for progress");

            foreach (string line in new[]
            {
                "[16:22:53] [Server thread/INFO]: [Chunky] Shape changed to square.",
                "[16:22:55] [Server thread/INFO]: [Chunky] Radius changed to 300.",
                "[16:22:50] [Server thread/INFO]: Preparing spawn area: 100%",
                "[16:17:41] [Server thread/WARN]: Can't keep up! Is the server overloaded? " +
                "Running 2051ms or 41 ticks behind",
                ""
            })
            {
                string shown = line.Length > 58 ? line[..58] + "..." : line;
                Check($"ignored: {shown}", !ChunkyPregen.TryParseProgress(line, out _));
            }
        }

        private static void ChunkCounts()
        {
            Section("chunk counts against measured reality");

            // The real run reported exactly this for radius 300.
            Expect("radius 300 -> 1521 chunks (the real run)", ChunkyPregen.ChunksFor(300), 1521L);

            long r3000 = ChunkyPregen.ChunksFor(3000);
            long r5000 = ChunkyPregen.ChunksFor(5000);
            long r10000 = ChunkyPregen.ChunksFor(10000);

            Note($"3000 -> {r3000:N0}   5000 -> {r5000:N0}   10000 -> {r10000:N0}");

            // HANDOFF section 14's measured table.
            Check("radius 3000 lands near the measured 141k", Math.Abs(r3000 - 141_000) < 2_000);
            Check("radius 5000 lands near the measured 391k", Math.Abs(r5000 - 391_000) < 4_000);
            Check("radius 10000 lands near the measured 1.56M", Math.Abs(r10000 - 1_560_000) < 12_000);

            Check("a circle is smaller than a square",
                  ChunkyPregen.ChunksFor(3000, ChunkyPregen.ShapeCircle) < r3000);
            Check("a circle is about 79% of a square",
                  Math.Abs(ChunkyPregen.ChunksFor(3000, ChunkyPregen.ShapeCircle) / (double)r3000 - 0.785) < 0.01);
            Expect("radius 0 is nothing", ChunkyPregen.ChunksFor(0), 0L);
        }

        private static void Estimates()
        {
            Section("estimates");

            var est3000 = ChunkyPregen.EstimateFor(3000);
            Note($"radius 3000: {est3000.Describe()}");
            Check("3000 lands near the measured 1.2 GB",
                  est3000.Bytes > 1.1 * 1024 * 1024 * 1024 && est3000.Bytes < 1.4 * 1024L * 1024 * 1024,
                  ChunkyPregen.HumanBytes(est3000.Bytes));
            Check("3000 is roughly an hour on the dev box",
                  est3000.Duration.TotalMinutes is > 55 and < 95, est3000.Duration.ToString());

            var est10000 = ChunkyPregen.EstimateFor(10000);
            Note($"radius 10000: {est10000.Describe()}");
            Check("10000 is the long job section 14 describes",
                  est10000.Duration.TotalHours is > 6 and < 16,
                  est10000.Duration.TotalHours.ToString("0.0"));

            Check("a faster machine is predicted as faster",
                  ChunkyPregen.EstimateFor(3000, chunksPerSecond: 60).Duration <
                  ChunkyPregen.EstimateFor(3000, chunksPerSecond: 30).Duration);

            // A run over already-generated ground reported 1950 cps, and believing it
            // made radius 3000 look like a one-minute job.
            Check("1950 cps (skipping existing chunks) is not believed",
                  !ChunkyPregen.IsCredibleRate(1950));
            Check("0.8 cps (a task warming up) is not believed", !ChunkyPregen.IsCredibleRate(0.8));
            Check("32 cps (the real sustained rate) is believed", ChunkyPregen.IsCredibleRate(32.0));
            Check("a fast host at 120 cps is still believed", ChunkyPregen.IsCredibleRate(120));
        }

        private static void Commands()
        {
            Section("commands");

            var cmds = ChunkyPregen.StartCommands(3000);
            foreach (string c in cmds) Note(c);

            Expect("the dimension is set first", cmds[0], "chunky world minecraft:overworld");
            Expect("start is last", cmds[^1], "chunky start");
            Check("the radius is passed through", cmds.Any(c => c == "chunky radius 3000"));
            Check("square by default", cmds.Any(c => c == "chunky shape square"));

            // chunky trim deletes chunks outside the selection. Nothing here should be
            // one mis-click away from that.
            Check("nothing here trims",
                  !cmds.Any(c => c.Contains("trim", StringComparison.OrdinalIgnoreCase)));

            Expect("a Nether run targets the Nether",
                   ChunkyPregen.StartCommands(375, dimension: ChunkyPregen.Nether)[0],
                   "chunky world minecraft:the_nether");
            Expect("the Nether radius is an eighth", ChunkyPregen.NetherRadiusFor(3000), 375);
            Check("the Nether radius never collapses to zero", ChunkyPregen.NetherRadiusFor(8) >= 16);

            var border = ChunkyPregen.BorderCommands(3000, includeNether: true);
            foreach (string c in border) Note(c);

            // The border takes a diameter while Chunky takes a radius, which is the
            // easiest thing in this area to get wrong.
            Expect("the border is a DIAMETER, so double the radius", border[0], "worldborder set 6000");
            Expect("the Nether border is doubled too, at an eighth", border[1],
                   "execute in minecraft:the_nether run worldborder set 750");
            Expect("the border alone skips the Nether",
                   ChunkyPregen.BorderCommands(3000, includeNether: false).Count, 1);

            Throws<ArgumentOutOfRangeException>("a zero radius is refused",
                () => ChunkyPregen.StartCommands(0));
        }
    }
}
