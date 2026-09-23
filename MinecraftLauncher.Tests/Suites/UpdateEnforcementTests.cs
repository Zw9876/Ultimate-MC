using System;
using System.IO;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// The machine-wide names that let a mandatory update arrange its own conditions.
    /// </summary>
    /// <remarks>
    /// Both names used to be fixed strings, which looked right until two launcher
    /// folders existed on one machine. Then the second install never started a
    /// watcher — the first held the mutex — and updating one install asked the
    /// *other* one's launcher to close. Found by a two-install test that simply
    /// stopped working; these checks are what stop it coming back.
    /// </remarks>
    public static class UpdateEnforcementTests
    {
        public static void Run()
        {
            Scoping();
            Shape();
            Settings();
        }

        private static void Scoping()
        {
            Section("the names are scoped to the install, not the machine");

            string a = Path.Combine(Path.GetTempPath(), "launcher-copy-a");
            string b = Path.Combine(Path.GetTempPath(), "launcher-copy-b");

            string mutexA, mutexB, eventA, eventB;

            using (LocalInstall.RedirectBase(a))
            {
                mutexA = UpdateEnforcement.WatcherMutexName;
                eventA = UpdateEnforcement.ExitForUpdateEventName;
            }

            using (LocalInstall.RedirectBase(b))
            {
                mutexB = UpdateEnforcement.WatcherMutexName;
                eventB = UpdateEnforcement.ExitForUpdateEventName;
            }

            Note(mutexA);
            Note(mutexB);

            Check("two installs get different watcher mutexes", mutexA != mutexB, mutexA);
            Check("two installs get different exit events", eventA != eventB, eventA);

            // Same folder must always give the same name, or a watcher would not find
            // the mutex it took out a moment ago.
            using (LocalInstall.RedirectBase(a))
            {
                Expect("the same install gives the same mutex every time",
                       UpdateEnforcement.WatcherMutexName, mutexA);
                Expect("and the same event", UpdateEnforcement.ExitForUpdateEventName, eventA);
            }

            // Windows paths are case-insensitive; two spellings of one folder are one
            // install and must not end up with two watchers.
            using (LocalInstall.RedirectBase(a.ToUpperInvariant()))
            {
                Expect("case does not make it a different install",
                       UpdateEnforcement.WatcherMutexName, mutexA);
            }
        }

        private static void Shape()
        {
            Section("the names are usable as Windows object names");

            string mutex = UpdateEnforcement.WatcherMutexName;
            string ev = UpdateEnforcement.ExitForUpdateEventName;

            foreach (var (label, name) in new[] { ("mutex", mutex), ("event", ev) })
            {
                Check($"the {label} is session-scoped", name.StartsWith(@"Local\"), name);
                Check($"the {label} names the launcher", name.Contains("MinecraftPortableLauncher"), name);

                // A backslash after the prefix would be read as a namespace separator.
                Check($"the {label} has no stray separators",
                      name[@"Local\".Length..].IndexOf('\\') < 0, name);

                Check($"the {label} is not absurdly long", name.Length < 200, name.Length.ToString());
            }

            Check("the two names are different from each other", mutex != ev);
        }

        private static void Settings()
        {
            Section("the settings people will feel");

            // Long enough that a machine joining late catches up within a session,
            // short enough that it is not the reason versions drift.
            Check("it polls often enough to matter",
                  UpdateEnforcement.PollInterval.TotalMinutes is > 0 and <= 5,
                  UpdateEnforcement.PollInterval.ToString());

            // Long enough to read, short enough that nobody waits around. Read into a
            // local first: CountdownSeconds is a const, so testing it directly folds at
            // compile time and warns that the pattern always matches.
            int countdown = UpdateEnforcement.CountdownSeconds;
            Check("the countdown is a sensible length",
                  countdown is >= 5 and <= 30,
                  countdown.ToString());
        }
    }
}
