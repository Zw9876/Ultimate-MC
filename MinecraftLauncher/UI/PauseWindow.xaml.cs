using System;
using System.Windows;

namespace MinecraftLauncher.UI
{
    /// <summary>
    /// Asks how long to turn playing off for everyone, and why.
    /// </summary>
    /// <remarks>
    /// Two clicks rather than one, on purpose. This closes everybody's game with no
    /// warning they can decline, and a reason is what separates that from looking like
    /// a fault — somebody whose Minecraft vanishes with no explanation goes looking for
    /// a crash report. The confirm button is the default, so Enter is still enough when
    /// it is wanted in a hurry.
    /// </remarks>
    public partial class PauseWindow : Window
    {
        public sealed record Choice(string? Reason, TimeSpan? Duration);

        public Choice? Result { get; private set; }

        private static readonly (string Label, TimeSpan? Span)[] Durations =
        {
            ("15 minutes",                   TimeSpan.FromMinutes(15)),
            ("30 minutes",                   TimeSpan.FromMinutes(30)),
            ("1 hour",                       TimeSpan.FromHours(1)),
            ("Until I let everyone back on", null)
        };

        public PauseWindow(bool serving)
        {
            InitializeComponent();

            foreach (var (label, _) in Durations) DurationCombo.Items.Add(label);

            // 30 minutes: long enough to update the launchers, short enough that
            // forgetting about it is not a problem for anybody.
            DurationCombo.SelectedIndex = 1;

            // The honest caveat. Without something serving, the pause is written down
            // and will gate PLAY, but no running game hears about it — the watchers
            // poll this host, and there is nothing here to poll.
            ServingNote.Text = serving
                ? "This host is serving, so games already running will close within a " +
                  "second or two."
                : "Nothing is being served from this machine right now, so no game that " +
                  "is already running will hear about this. It will still stop anyone " +
                  "STARTING Minecraft. Start the skin server (or a server) first if you " +
                  "need the games that are already open to close.";

            ReasonBox.Focus();
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            int index = DurationCombo.SelectedIndex >= 0 ? DurationCombo.SelectedIndex : 1;
            var (label, span) = Durations[index];

            string reason = ReasonBox.Text.Trim();

            string when = span is null
                ? "until you let everyone back on"
                : $"for {label.ToLowerInvariant()}";

            var answer = MessageBox.Show(
                $"Close everyone's Minecraft now and keep it shut {when}?\n\n" +
                (reason.Length > 0
                    ? $"They will be told: {reason}"
                    : "No reason given — they will just be told it is paused.") +
                "\n\nNobody gets a countdown and nobody can decline.",
                "Pause everyone", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return;

            Result = new Choice(reason.Length == 0 ? null : reason, span);
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
