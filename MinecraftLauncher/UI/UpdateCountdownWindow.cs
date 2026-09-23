using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MinecraftLauncher.UI
{
    /// <summary>
    /// Says an update is about to install, counts down, and installs it.
    /// </summary>
    /// <remarks>
    /// Deliberately has no way to refuse. Updates are mandatory because the machines
    /// drifting onto different versions is the thing this is meant to stop, and an
    /// update everyone can postpone is one nobody installs. It explains what is
    /// happening instead of asking, and says plainly that the game is not affected.
    /// </remarks>
    internal sealed class UpdateCountdownWindow : Window
    {
        public event Action? Finished;

        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly TextBlock _count;
        private int _remaining;
        private bool _done;

        public UpdateCountdownWindow(int seconds, string version)
        {
            _remaining = seconds;

            Title = "Updating the launcher";
            Width = 460;
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
                Text = "A NEWER LAUNCHER IS AVAILABLE",
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
                Text = $"Version {version} has already been downloaded and will be " +
                       "installed now, so every machine stays on the same version.\n\n" +
                       "Minecraft is not affected and keeps running. The launcher " +
                       "reopens by itself once it is done.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = Brush("MutedBrush", Colors.LightGray)
            };

            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel { Children = { heading, _count, detail } }
            };

            Tick();
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        private void Tick()
        {
            if (_done) return;

            _count.Text = _remaining > 0
                ? $"Installing in {_remaining}…"
                : "Installing…";

            if (_remaining-- > 0) return;

            _done = true;
            _timer.Stop();
            Finished?.Invoke();
        }

        /// <summary>Closing it is not a way out; it installs anyway.</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_done)
            {
                e.Cancel = true;
                return;
            }

            base.OnClosing(e);
        }

        private static Brush Brush(string key, Color fallback)
        {
            if (Application.Current?.TryFindResource(key) is Brush found) return found;
            return new SolidColorBrush(fallback);
        }
    }
}
