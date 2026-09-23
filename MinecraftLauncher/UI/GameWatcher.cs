using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MinecraftLauncher.Core;

namespace MinecraftLauncher.UI
{
    /// <summary>
    /// A hidden copy of the launcher that lives alongside one running game and closes
    /// it when the host announces the evening is over.
    /// </summary>
    /// <remarks>
    /// It has to be a separate process because the launcher closes itself after PLAY,
    /// so on a client machine nothing else is left running to hear the host. It has
    /// no window of its own and exits as soon as its game does, so it never outlives
    /// the thing it is watching.
    /// </remarks>
    internal sealed class GameWatcher
    {
        public const string Flag = "--watch-game";
        private const string HostFlag = "--host";

        private static readonly TimeSpan PollInterval        = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan UnreachableInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan UnsupportedInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RediscoverInterval  = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How long a game gets to close itself before it is ended. Long, because a
        /// singleplayer world is saved on the way out and a big one takes a while.
        /// </summary>
        private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(60);

        private readonly Application _app;
        private readonly Process _game;
        private string? _host;
        private readonly HashSet<string> _handled = new();
        private bool _busy;

        private GameWatcher(Application app, Process game, string? host)
        {
            _app = app;
            _game = game;
            _host = host;
        }

        /// <summary>Recognises the command line a watcher is started with.</summary>
        public static bool TryParse(string[] args, out int gamePid, out string? host)
        {
            gamePid = 0;
            host = null;

            int at = Array.IndexOf(args, Flag);
            if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], out gamePid))
                return false;

            int h = Array.IndexOf(args, HostFlag);
            if (h >= 0 && h + 1 < args.Length && !string.IsNullOrWhiteSpace(args[h + 1]))
                host = args[h + 1];

            return true;
        }

        /// <summary>
        /// Starts a watcher for a game this launcher has just started. Never throws —
        /// failing to start one must not get in the way of playing.
        /// </summary>
        public static void Spawn(int gamePid, string? host)
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (exe is null) return;

                var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                psi.ArgumentList.Add(Flag);
                psi.ArgumentList.Add(gamePid.ToString());
                if (!string.IsNullOrWhiteSpace(host))
                {
                    psi.ArgumentList.Add(HostFlag);
                    psi.ArgumentList.Add(host);
                }

                Process.Start(psi);
            }
            catch
            {
                // The game still runs; it just will not close with everyone else's.
            }
        }

        /// <summary>Entry point for watcher mode.</summary>
        public static void Run(Application app, int gamePid, string? host)
        {
            Process game;
            try
            {
                game = Process.GetProcessById(gamePid);
            }
            catch
            {
                app.Shutdown();   // the game is already gone
                return;
            }

            new GameWatcher(app, game, host).Loop();
        }

        private async void Loop()
        {
            try
            {
                using var exitSignal = SessionShutdown.OpenWatcherExitEvent();
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                DateTime nextDiscovery = DateTime.MinValue;

                while (true)
                {
                    if (GameHasExited() || exitSignal.WaitOne(0)) break;

                    TimeSpan wait = PollInterval;

                    if (_busy)
                    {
                        // A countdown or a close is in progress; just keep an eye on
                        // whether the game has gone.
                    }
                    else if (_host is null)
                    {
                        // No skin server was running when the game started. The host may
                        // start one later in the evening, so keep looking — slowly.
                        if (DateTime.UtcNow >= nextDiscovery)
                        {
                            _host = await Task.Run(() => SkinDiscovery.Resolve(AppConfig.Load()));
                            nextDiscovery = DateTime.UtcNow + RediscoverInterval;
                        }
                    }
                    else
                    {
                        var (result, notice) = await SessionShutdown.PollAsync(http, _host);
                        switch (result)
                        {
                            case SessionShutdown.PollResult.Announced when notice is not null:
                                if (_handled.Add(notice.Id)) BeginCountdown(notice);
                                break;

                            case SessionShutdown.PollResult.Unsupported:
                                // An older launcher is hosting. Asking every second would
                                // fill its log with requests it does not recognise.
                                wait = UnsupportedInterval;
                                break;

                            case SessionShutdown.PollResult.Unreachable:
                                wait = UnreachableInterval;
                                break;
                        }
                    }

                    await Task.Delay(wait);
                }
            }
            catch (Exception ex)
            {
                // Recorded, because a watcher dying quietly looks exactly like one that
                // never started. Then fall through: a watcher that stops working must
                // still exit, or it would hold the launcher's exe open and block updates.
                App.Log(ex);
            }

            _app.Shutdown();
        }

        private bool GameHasExited()
        {
            try { return _game.HasExited; }
            catch { return true; }
        }

        private void BeginCountdown(ShutdownNotice notice)
        {
            _busy = true;

            var window = new CountdownWindow(notice.Countdown);
            window.Finished += async () => await CloseGameAsync();
            window.Cancelled += () => _busy = false;
            window.Show();
            window.Activate();
        }

        /// <summary>
        /// Closes the game the way clicking its X does, so Minecraft runs its normal
        /// shutdown: it leaves the server cleanly, and a singleplayer world is saved.
        /// Only a game that ignores that for a full minute is ended outright.
        /// </summary>
        private async Task CloseGameAsync()
        {
            try
            {
                if (!AskGameToClose())
                {
                    // No window yet usually means it is still starting; give it a moment.
                    await Task.Delay(2000);
                    if (!AskGameToClose())
                    {
                        // Still nothing to ask, so nothing is loaded that could be lost.
                        KillGame();
                        return;
                    }
                }

                bool exited = await Task.Run(() => _game.WaitForExit(GracefulExitTimeout));
                if (!exited) KillGame();
            }
            catch
            {
                KillGame();
            }
            finally
            {
                _app.Shutdown();
            }
        }

        private bool AskGameToClose()
        {
            try
            {
                if (_game.HasExited) return true;
                _game.Refresh();
                return _game.CloseMainWindow();
            }
            catch { return false; }
        }

        private void KillGame()
        {
            try { if (!_game.HasExited) _game.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
    }

    /// <summary>
    /// The notice a player sees before their game closes. Built in code rather than
    /// XAML because it is the only thing watcher mode ever shows.
    /// </summary>
    internal sealed class CountdownWindow : Window
    {
        public event Action? Finished;
        public event Action? Cancelled;

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly TextBlock _count;
        private int _remaining;
        private bool _settled;

        public CountdownWindow(int seconds)
        {
            _remaining = seconds;

            Title = "Minecraft is closing";
            Width = 440;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ShowInTaskbar = true;
            Background = Brush("BgBrush", Colors.Black);

            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/minecraft.ico")); }
            catch { /* the default icon will do */ }

            var heading = new TextBlock
            {
                Text = "THE HOST IS SHUTTING DOWN",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("AccentBrush", Colors.LightGreen)
            };

            _count = new TextBlock
            {
                FontSize = 28,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 8),
                Foreground = Brush("TextBrush", Colors.White)
            };

            var detail = new TextBlock
            {
                Text = "Minecraft is closed the normal way, as if you clicked its X, " +
                       "so nothing you were doing is lost.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = Brush("MutedBrush", Colors.Gray)
            };

            var keepPlaying = new Button
            {
                Content = "Keep playing",
                Width = 130,
                Height = 30,
                Margin = new Thickness(0, 18, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            if (Application.Current?.TryFindResource("PlainButton") is Style style)
                keepPlaying.Style = style;
            keepPlaying.Click += (_, _) => Settle(cancelled: true);

            Content = new StackPanel
            {
                Margin = new Thickness(24, 20, 24, 20),
                Children = { heading, _count, detail, keepPlaying }
            };

            ShowCount();
            _timer.Tick += (_, _) =>
            {
                _remaining--;
                if (_remaining <= 0) Settle(cancelled: false);
                else ShowCount();
            };
            _timer.Start();

            // Closing the notice with its own X is read as "not now", same as the
            // button — dismissing a warning should never be what closes the game.
            Closed += (_, _) => Settle(cancelled: true);
        }

        private void ShowCount() =>
            _count.Text = $"Minecraft closes in {_remaining}";

        private void Settle(bool cancelled)
        {
            if (_settled) return;
            _settled = true;
            _timer.Stop();

            if (IsVisible) Close();

            if (cancelled) Cancelled?.Invoke();
            else Finished?.Invoke();
        }

        private static Brush Brush(string key, Color fallback) =>
            Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    }
}
