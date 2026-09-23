using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MinecraftLauncher.Core;

namespace MinecraftLauncher.UI
{
    /// <summary>
    /// A hidden copy of the launcher that keeps looking for a newer build and
    /// installs it without being asked.
    /// </summary>
    /// <remarks>
    /// The launcher closes when someone presses PLAY, so for most of a session there
    /// is nothing running that could notice an update — which is how a room full of
    /// machines ends up on different versions. This process outlives the launcher
    /// window and keeps checking.
    ///
    /// One per machine, enforced with a named mutex. It owns the whole update: the
    /// launcher window no longer installs anything itself, because two things racing
    /// to replace the same file is worse than one thing doing it slightly later.
    /// </remarks>
    internal static class UpdateWatcher
    {
        private const string Flag = "--watch-updates";

        /// <summary>Set while this process is the one doing the swapping.</summary>
        private static bool _installing;

        /// <summary>
        /// Held for the life of the process, which is the whole point of it.
        /// </summary>
        /// <remarks>
        /// A field rather than a local, because <see cref="Run"/> returns as soon as
        /// the timer is set up and WPF's message loop takes over. As a <c>using</c>
        /// local the mutex was released immediately, every launcher happily spawned
        /// another watcher, and they accumulated.
        /// </remarks>
        private static Mutex? _single;

        public static bool ShouldRun(string[] args) => Array.IndexOf(args, Flag) >= 0;

        /// <summary>
        /// Starts the watcher if one is not already running. Never throws: failing to
        /// start it must not stop anyone playing.
        /// </summary>
        public static void SpawnIfMissing()
        {
            try
            {
                // Cheap existence check. The watcher itself holds the real mutex; this
                // only avoids spawning a process that would immediately exit.
                using (var probe = new Mutex(false, UpdateEnforcement.WatcherMutexName, out bool created))
                {
                    if (!created) return;       // someone else holds it
                }

                string? exe = Environment.ProcessPath;
                if (exe is null) return;

                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(Flag);
                Process.Start(psi);
            }
            catch
            {
                // Updates will still happen next time a launcher opens.
            }
        }

        /// <summary>Entry point for watcher mode. Returns when the process should end.</summary>
        public static void Run(Application app)
        {
            // If another watcher already holds this, there is nothing for us to do.
            _single = new Mutex(false, UpdateEnforcement.WatcherMutexName, out bool created);
            if (!created)
            {
                _single.Dispose();
                _single = null;
                app.Shutdown();
                return;
            }

            // If a launcher window installs an update itself, it asks everyone to
            // leave. Obeying that is what stops two swap scripts running at once and
            // relaunching two launchers.
            try
            {
                var exitRequest = UpdateEnforcement.OpenExitForUpdateEvent();
                ThreadPool.RegisterWaitForSingleObject(exitRequest, (_, _) =>
                {
                    if (_installing) return;
                    app.Dispatcher.BeginInvoke(() => app.Shutdown());
                }, null, Timeout.Infinite, executeOnlyOnce: true);
            }
            catch { /* the swap script still retries for a while */ }

            var timer = new DispatcherTimer { Interval = UpdateEnforcement.PollInterval };
            bool busy = false;

            timer.Tick += async (_, _) =>
            {
                if (busy) return;
                busy = true;

                try { await CheckOnceAsync(app); }
                catch (Exception ex) { Note("check failed: " + ex.Message); App.Log(ex); }
                finally { busy = false; }
            };

            timer.Start();

            Note($"watcher started, version {LauncherPackage.CurrentVersion}, " +
                 $"checking every {UpdateEnforcement.PollInterval.TotalMinutes:0} min");

            // Look immediately as well, so a machine that opens the launcher late in a
            // session does not wait a full interval to catch up.
            //
            // Awaited inside the lambda rather than handed to InvokeAsync as a Task:
            // an exception in an async delegate passed to InvokeAsync is captured in a
            // Task nobody observes, so a failure here would vanish without trace.
            _ = app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                busy = true;
                try { await CheckOnceAsync(app); }
                catch (Exception ex) { Note("check failed: " + ex.Message); App.Log(ex); }
                finally { busy = false; }
            }));
        }

        /// <summary>
        /// A short log of what each check concluded.
        /// </summary>
        /// <remarks>
        /// These machines are not ones anyone can look at remotely, and "it did not
        /// update" has several very different causes — nobody hosting, a host that
        /// cannot serve, a host on the same build. Without this the only way to tell
        /// them apart is to guess.
        /// </remarks>
        private static void Note(string line)
        {
            try
            {
                string path = Path.Combine(Paths.BaseDir, "update-watcher.log");

                // Keep it small; this runs all session, every session.
                if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024)
                {
                    var keep = File.ReadAllLines(path).TakeLast(200).ToArray();
                    File.WriteAllLines(path, keep);
                }

                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never be the reason an update does not happen.
            }
        }

        private static async Task CheckOnceAsync(Application app)
        {
            var config = AppConfig.Load();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            // LookupAsync rather than CheckLanAsync: it reports which of the several
            // "nothing happened" cases this was.
            var lookup = await LauncherUpdate.LookupAsync(config, cts.Token);

            if (lookup.Outcome != UpdateOutcome.UpdateAvailable)
            {
                Note($"{lookup.Outcome} (local {lookup.LocalVersion}" +
                     (lookup.HostAddress is { Length: > 0 } h ? $", host {h} on {lookup.RemoteVersion}" : "") + ")");
                return;
            }

            var check = lookup.Check;
            if (check is null || !check.UpdateAvailable) return;

            Note($"UpdateAvailable: {lookup.LocalVersion} -> {check.RemoteVersion} " +
                 $"from {check.HostAddress}, {check.Outdated.Count} file(s), " +
                 $"{check.TotalBytes / 1024.0 / 1024:N0} MB");

            // Download before showing anything. A countdown that ends in a two-minute
            // transfer is a countdown that lied, and a failed download should pass
            // unnoticed rather than interrupt someone for nothing.
            try
            {
                await LauncherUpdate.DownloadAsync(check, null, cts.Token);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                LauncherUpdate.DiscardStaged();
                return;
            }

            Install(app, check.RemoteVersion?.ToString() ?? "a newer build");
        }

        /// <summary>
        /// Shows the countdown, then closes everything else and swaps the files.
        /// </summary>
        private static void Install(Application app, string version)
        {
            var window = new UpdateCountdownWindow(UpdateEnforcement.CountdownSeconds, version);

            window.Finished += () =>
            {
                try
                {
                    _installing = true;
                    window.Close();

                    // Windows will not replace a running executable, and by now there
                    // may be a launcher window and a game watcher holding this one.
                    UpdateEnforcement.AskEveryoneToExit();
                    UpdateEnforcement.WaitForOthersToExit(TimeSpan.FromSeconds(20));

                    // The swap script waits for this process, then relaunches the
                    // launcher, which starts a fresh watcher.
                    LauncherUpdate.Apply();
                }
                catch (Exception ex)
                {
                    App.Log(ex);
                }
                finally
                {
                    app.Shutdown();
                }
            };

            window.Show();
        }
    }
}
