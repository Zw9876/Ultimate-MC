using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Runs the regression suites.
    /// </summary>
    /// <remarks>
    ///   dotnet run                    every suite
    ///   dotnet run -- --offline       skip the ones that need the internet
    ///   dotnet run -- loader mods     only suites whose name contains these
    ///   dotnet run -- --list          names only
    ///   dotnet run -- --verbose       a line per check even when piped
    ///   dotnet run -- --quiet         failures and the summary only
    ///
    /// These were rebuilt from scratch in every session before this one, which is the
    /// whole reason the project exists. What they cover is listed in HANDOFF.md
    /// section 13; what they must never do is change anything under versions/ or
    /// servers/ — the real installs are read, never written.
    /// </remarks>
    public static class Program
    {
        private sealed record Suite(string Name, bool NeedsInternet, Func<Task> Run);

        public static async Task<int> Main(string[] args)
        {
            var suites = new List<Suite>
            {
                new("versions",   false, () => Task.Run(VersionAndRangeTests.Run)),
                new("mods",       false, () => Task.Run(ModInspectorTests.Run)),
                new("chunky",     false, () => Task.Run(ChunkyPregenTests.Run)),
                new("players",    false, () => Task.Run(PlayerRosterTests.Run)),
                new("loader",     false, () => Task.Run(LoaderVersionTests.Run)),
                new("replace",    false, () => Task.Run(LoaderReplaceTests.Run)),
                new("enforce",    false, () => Task.Run(UpdateEnforcementTests.Run)),
                new("swap",       false, () => Task.Run(SwapScriptTests.Run)),
                new("crashes",    false, () => Task.Run(CrashReportTests.Run)),
                new("cleanup",    false, () => Task.Run(ModCleanupTests.Run)),
                new("packs",      false, FabricPackTests.RunAsync),
                new("serverpacks",false, FabricServerPackTests.RunAsync),
                new("modrinth",   true,  ModrinthTests.RunAsync)
            };

            if (args.Contains("--list"))
            {
                foreach (var s in suites)
                    Console.WriteLine($"{s.Name}{(s.NeedsInternet ? "  (needs internet)" : "")}");
                return 0;
            }

            bool offline = args.Contains("--offline");
            var wanted = args.Where(a => !a.StartsWith("--")).ToList();

            // Left alone, this follows whether anything is reading the output; see
            // Harness.Quiet. Asking for both is not worth an error — verbose wins,
            // because someone passing it wants to see more, not less.
            if (args.Contains("--quiet")) Harness.Quiet = true;
            if (args.Contains("--verbose")) Harness.Quiet = false;

            var chosen = suites
                .Where(s => wanted.Count == 0 || wanted.Any(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (chosen.Count == 0)
            {
                Console.WriteLine($"No suite matches {string.Join(", ", wanted)}. Try --list.");
                return 2;
            }

            LocalInstall.Link();
            if (!LocalInstall.Available)
                Console.WriteLine("NOTE: the real installs are not reachable; suites that read them will skip.\n");

            var clock = Stopwatch.StartNew();
            var ran = new List<string>();
            var skipped = new List<string>();

            foreach (var suite in chosen)
            {
                if (offline && suite.NeedsInternet)
                {
                    skipped.Add(suite.Name);
                    if (!Harness.Quiet) Console.WriteLine($"\n### {suite.Name} — skipped (--offline)");
                    continue;
                }

                ran.Add(suite.Name);
                if (!Harness.Quiet) Console.WriteLine($"\n################ {suite.Name} ################");
                Harness.CurrentSuite = suite.Name;

                try
                {
                    await suite.Run();
                }
                catch (Exception ex)
                {
                    // A suite that throws is a failure, not a crash of the run: the
                    // others still have something to say.
                    Harness.Check($"the {suite.Name} suite ran to the end", false,
                                  $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            // Quiet mode prints no per-suite banner, so without this a clean run could
            // not be told apart from one where a filter matched nothing much.
            if (Harness.Quiet)
                Console.WriteLine($"\nran: {string.Join(", ", ran)}" +
                                  (skipped.Count == 0 ? "" : $"   skipped: {string.Join(", ", skipped)}"));

            return Harness.Summarise(clock.Elapsed);
        }
    }
}
