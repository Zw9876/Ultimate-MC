using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// The machine-wide coordination that lets an update install itself without
    /// anyone agreeing to it.
    /// </summary>
    /// <remarks>
    /// Updates are mandatory because a LAN full of launchers on different versions is
    /// the problem this is here to end. That turns a swap that used to happen while
    /// the user watched into one that has to arrange its own conditions: Windows will
    /// not replace a running executable, and by then there may be a launcher window, a
    /// game watcher and this update watcher all running from the same file.
    ///
    /// So there are two named events. One asks every other copy of the launcher to
    /// exit; the other stops a second update watcher ever starting.
    /// </remarks>
    public static class UpdateEnforcement
    {
        /// <summary>
        /// Both names are scoped to the install folder, not just the machine.
        /// </summary>
        /// <remarks>
        /// A machine-wide name looks right until there are two launcher folders on one
        /// computer — a test rig, or a copy someone made. Then the second install never
        /// starts a watcher because the first holds the mutex, and worse, updating one
        /// install asks the *other* one's launcher to close. Both are about "this copy
        /// of the launcher", so both are keyed to where it lives.
        /// </remarks>
        private static string Scope
        {
            get
            {
                // Paths.BaseDir rather than LauncherPackage.Dir: identical in every
                // shipped path, but it follows the same override the rest of the
                // codebase uses, so two installs can actually be told apart in a test.
                byte[] hash = System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(
                        Paths.BaseDir.TrimEnd(System.IO.Path.DirectorySeparatorChar).ToLowerInvariant()));

                return Convert.ToHexString(hash)[..8];
            }
        }

        /// <summary>Asks a launcher window — and anything else holding the exe — to exit.</summary>
        public static string ExitForUpdateEventName =>
            $@"Local\MinecraftPortableLauncher.ExitForUpdate.{Scope}";

        /// <summary>Held for as long as one update watcher is running.</summary>
        public static string WatcherMutexName =>
            $@"Local\MinecraftPortableLauncher.UpdateWatcher.{Scope}";

        /// <summary>How often the background watcher looks for a new build.</summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

        /// <summary>Seconds shown before an update installs itself.</summary>
        public const int CountdownSeconds = 10;

        public static EventWaitHandle OpenExitForUpdateEvent() =>
            new(false, EventResetMode.ManualReset, ExitForUpdateEventName);

        /// <summary>
        /// Tells every other copy of the launcher on this machine to close so the file
        /// can be replaced.
        /// </summary>
        public static void AskEveryoneToExit()
        {
            try
            {
                using var exit = OpenExitForUpdateEvent();
                exit.Set();
            }
            catch
            {
                // Best effort; the swap script retries for a while regardless.
            }

            // Game watchers listen on their own event, not this one.
            SessionShutdown.SignalWatchersToExit();
        }

        /// <summary>Clears the exit request, so a relaunched launcher does not close again.</summary>
        public static void ClearExitRequest()
        {
            try
            {
                using var exit = OpenExitForUpdateEvent();
                exit.Reset();
            }
            catch { }
        }

        /// <summary>
        /// Waits until this is the only copy of the launcher left running, so the swap
        /// has a chance of succeeding rather than silently failing fifteen times.
        /// </summary>
        /// <returns>True when everyone else has gone.</returns>
        public static bool WaitForOthersToExit(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (OtherInstances().Length == 0) return true;
                Thread.Sleep(500);
            }

            return OtherInstances().Length == 0;
        }

        /// <summary>
        /// Every other process running from this same executable file.
        /// </summary>
        /// <remarks>
        /// Matched on the path, not just the process name: another launcher folder on
        /// the same machine holds a different file, so waiting for it would stall an
        /// update that its exit could never unblock.
        /// </remarks>
        public static Process[] OtherInstances()
        {
            try
            {
                int me = Environment.ProcessId;
                string? mine = Environment.ProcessPath;
                if (mine is null) return Array.Empty<Process>();

                return Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName)
                              .Where(p => p.Id != me && SameFile(p, mine))
                              .ToArray();
            }
            catch
            {
                return Array.Empty<Process>();
            }
        }

        private static bool SameFile(Process p, string path)
        {
            try
            {
                return string.Equals(p.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Access denied on a process we cannot inspect: assume it is not ours
                // rather than wait forever for something that will never exit.
                return false;
            }
        }
    }
}
