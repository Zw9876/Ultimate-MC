using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MinecraftLauncher.Core;

namespace MinecraftLauncher.UI
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Watcher mode: a hidden copy started at PLAY to close the game when the
            // host ends the session. No launcher window, and it exits with its game.
            if (GameWatcher.TryParse(e.Args, out int gamePid, out string? host))
            {
                // Never an error dialog in the middle of someone's game, and never a
                // watcher left hanging around holding the exe open: log it and go.
                DispatcherUnhandledException += (_, args) =>
                {
                    args.Handled = true;
                    Log(args.Exception);
                    Shutdown();
                };

                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                base.OnStartup(e);
                GameWatcher.Run(this, gamePid, host);
                return;
            }

            // Update-watcher mode: the hidden copy that keeps looking for a newer
            // build long after the launcher window has closed. Also windowless, and
            // for the same reasons it must never pop a dialog.
            if (UpdateWatcher.ShouldRun(e.Args))
            {
                DispatcherUnhandledException += (_, args) =>
                {
                    args.Handled = true;
                    Log(args.Exception);
                    Shutdown();
                };

                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                base.OnStartup(e);
                UpdateWatcher.Run(this);
                return;
            }

            base.OnStartup(e);

            // A relaunch after an update arrives with the exit request still set;
            // clearing it stops the new window closing itself immediately.
            UpdateEnforcement.ClearExitRequest();

            // Opened here rather than through StartupUri in App.xaml, because watcher
            // mode must start with no window at all — and WPF on .NET 10 throws if
            // StartupUri is cleared at run time, which killed every watcher silently.
            new MainWindow().Show();

            // Keeps looking for updates after this window closes, which is most of a
            // session — the launcher shuts itself down at PLAY.
            UpdateWatcher.SpawnIfMissing();

            // A portable launcher people double-click should report a problem, not
            // vanish. Anything unhandled on the UI thread becomes a dialog and a
            // line in launcher_errors.txt instead of a silent process exit.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            Log(e.Exception);

            MessageBox.Show(
                $"{e.Exception.Message}\n\nThe launcher is still running. Details were written to " +
                $"launcher_errors.txt next to the executable.",
                "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        internal static void Log(Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Paths.BaseDir, "launcher_errors.txt"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never be the thing that takes the app down.
            }
        }
    }
}
