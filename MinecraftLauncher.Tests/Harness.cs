using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// The smallest thing that counts as a test runner: a way to assert, a way to
    /// group, and an exit code.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than pulled from a package because this launcher has to
    /// build on a machine with no NuGet feed configured, and every dependency is one
    /// more thing that can stop a build working offline. The whole runner is one file.
    /// </remarks>
    public static class Harness
    {
        private static readonly List<string> Failures = new();

        public static int Passed { get; private set; }
        public static int Failed => Failures.Count;

        /// <summary>The suite being run, for failure messages.</summary>
        public static string CurrentSuite { get; set; } = "";

        public static void Section(string title)
        {
            Console.WriteLine();
            Write($"== {title} ==", ConsoleColor.Yellow);
        }

        /// <summary>Prints a fact rather than asserting one — context for the reader.</summary>
        public static void Note(string text) => Console.WriteLine($"     {text}");

        public static void Check(string what, bool ok, string? detail = null)
        {
            if (ok)
            {
                Passed++;
                Write($"  ok    {what}", ConsoleColor.Green);
                return;
            }

            Failures.Add($"[{CurrentSuite}] {what}" + (detail is null ? "" : $"  -> {detail}"));
            Write($"  FAIL  {what}{(detail is null ? "" : $"  -> {detail}")}", ConsoleColor.Red);
        }

        /// <summary>For a check whose subject could not be reached at all.</summary>
        public static void Skip(string what, string why)
        {
            Write($"  skip  {what}  ({why})", ConsoleColor.DarkGray);
        }

        public static void Expect<T>(string what, T actual, T expected) =>
            Check(what, EqualityComparer<T>.Default.Equals(actual, expected), $"got {actual}, wanted {expected}");

        /// <summary>Runs an action and reports whether it threw the expected exception.</summary>
        public static void Throws<TException>(string what, Action action) where TException : Exception
        {
            try
            {
                action();
                Check(what, false, "nothing was thrown");
            }
            catch (TException)
            {
                Check(what, true);
            }
            catch (Exception ex)
            {
                Check(what, false, $"threw {ex.GetType().Name} instead");
            }
        }

        public static int Summarise(TimeSpan elapsed)
        {
            Console.WriteLine();
            if (Failed > 0)
            {
                Write("FAILURES", ConsoleColor.Red);
                foreach (string failure in Failures) Write("  " + failure, ConsoleColor.Red);
                Console.WriteLine();
            }

            Write($"{Passed} passed, {Failed} failed  ({elapsed.TotalSeconds:0.#}s)",
                  Failed == 0 ? ConsoleColor.Green : ConsoleColor.Red);

            return Failed == 0 ? 0 : 1;
        }

        private static void Write(string text, ConsoleColor colour)
        {
            var was = Console.ForegroundColor;
            Console.ForegroundColor = colour;
            Console.WriteLine(text);
            Console.ForegroundColor = was;
        }
    }

    /// <summary>
    /// Points <see cref="Core.Paths"/> at the real launcher folder.
    /// </summary>
    /// <remarks>
    /// <c>Paths.BaseDir</c> is normally the running executable's own folder, which for
    /// a test binary is its bin directory. Setting the override is all this takes —
    /// nothing is copied, nothing is linked, and nothing the tests create sits next to
    /// the real installs.
    /// </remarks>
    public static class LocalInstall
    {
        /// <summary>
        /// The launcher folder, found by walking up from the test binary until the
        /// repository is recognised.
        /// </summary>
        /// <remarks>
        /// Derived rather than hardcoded so the suites run wherever the repo is
        /// checked out. When the game folders are absent — a fresh clone, since
        /// <c>versions/</c> and <c>servers/</c> are gitignored — the suites that read
        /// them skip instead of failing.
        /// </remarks>
        public static string Root { get; } = FindRepoRoot();

        /// <summary>True once the real installs are reachable through Paths.</summary>
        public static bool Available { get; private set; }

        public static void Link()
        {
            Core.Paths.BaseDirOverride = Root;
            Available = Directory.Exists(Path.Combine(Root, "versions"));
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "MinecraftLauncher", "MinecraftLauncher.csproj")))
                    return dir.FullName;

                dir = dir.Parent;
            }

            // Nothing recognisable above us; the suites will report as unavailable.
            return AppContext.BaseDirectory;
        }

        /// <summary>A path inside the real launcher folder.</summary>
        public static string At(params string[] parts) =>
            Path.Combine(new[] { Root }.Concat(parts).ToArray());

        /// <summary>
        /// Temporarily resolves paths against a different folder, so a suite can write
        /// into a sandbox through the same <c>Paths</c> the product uses.
        /// </summary>
        /// <remarks>
        /// Scoped on purpose: leaving it pointed at a sandbox would quietly make every
        /// later suite inspect the sandbox instead of the real installs, and pass for
        /// the wrong reason.
        /// </remarks>
        public static IDisposable RedirectBase(string target) => new Redirect(target);

        private sealed class Redirect : IDisposable
        {
            private readonly string? _was = Core.Paths.BaseDirOverride;

            public Redirect(string target) => Core.Paths.BaseDirOverride = target;

            public void Dispose() => Core.Paths.BaseDirOverride = _was;
        }
    }
}
