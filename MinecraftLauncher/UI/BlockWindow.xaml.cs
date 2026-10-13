using System;
using System.Collections.Generic;
using System.Windows;
using MinecraftLauncher.Core;

namespace MinecraftLauncher.UI
{
    /// <summary>
    /// Collects who to turn playing off for, for how long, and why.
    /// </summary>
    /// <remarks>
    /// The fields are editable rather than fixed labels on purpose: it means the same
    /// window handles a machine picked from the fleet list and a machine that has never
    /// checked in, without a second way of doing it.
    /// </remarks>
    public partial class BlockWindow : Window
    {
        /// <summary>What the host chose. Null until the window is accepted.</summary>
        public sealed record Choice(
            string Machine, string? Guid, string? Username,
            string? Reason, TimeSpan? Duration, bool AlsoBanOnServer);

        public Choice? Result { get; private set; }

        private readonly string? _guid;

        private static readonly (string Label, TimeSpan? Span)[] Durations =
        {
            ("1 hour",                      TimeSpan.FromHours(1)),
            ("3 hours",                     TimeSpan.FromHours(3)),
            ("The rest of today (8 hours)",  TimeSpan.FromHours(8)),
            ("1 day",                       TimeSpan.FromDays(1)),
            ("1 week",                      TimeSpan.FromDays(7)),
            ("Until I turn it back on",     null)
        };

        public BlockWindow(string? machine, string? guid, string? username, bool serverRunning)
        {
            InitializeComponent();

            _guid = guid;

            MachineBox.Text = machine ?? "";
            UsernameBox.Text = username ?? "";

            foreach (var (label, _) in Durations) DurationCombo.Items.Add(label);
            DurationCombo.SelectedIndex = 0;

            // Said plainly, because a block that matches only on a name is weaker and
            // the person deciding should know which one they are getting.
            IdentityNote.Text = string.IsNullOrWhiteSpace(_guid)
                ? "This machine has not reported a Windows identifier — either it runs a " +
                  "launcher older than this feature, or it has never checked in. The block " +
                  "will match on the computer name only, so renaming the computer would " +
                  "escape it."
                : $"Windows identifier on record: {_guid}. The block matches on this as well " +
                  "as the name, so renaming the computer does not escape it.";

            // Only offered when there is a server to issue it to. Ticked by default
            // because it is the only part that cannot be bypassed.
            ServerBanCheck.IsEnabled = serverRunning;
            ServerBanCheck.IsChecked = serverRunning;

            if (!serverRunning)
                ServerBanCheck.Content =
                    "Also ban them on the running server — no server is running, so there is " +
                    "nothing to ban them on yet";

            MachineBox.Focus();
        }

        private void Block_Click(object sender, RoutedEventArgs e)
        {
            string machine = MachineBox.Text.Trim();

            if (machine.Length == 0)
            {
                StatusLabel.Text = "A computer name is needed — that is what the block matches on.";
                MachineBox.Focus();
                return;
            }

            // The rail that matters most: nothing here should be able to stop the host
            // using its own launcher. PlayBlocks refuses it at enforcement time too,
            // but saying so now is better than letting somebody write a block that
            // silently does nothing.
            if (string.Equals(machine, FleetRoster.ThisMachine, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "That is this computer.\n\nThe machine holding admin.flag is never blocked, " +
                    "so this would have no effect — and it is the rail that stops a misclick " +
                    "locking you out of your own launcher.",
                    "Not this computer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TimeSpan? span = DurationCombo.SelectedIndex >= 0
                ? Durations[DurationCombo.SelectedIndex].Span
                : TimeSpan.FromHours(1);

            string reason = ReasonBox.Text.Trim();
            string username = UsernameBox.Text.Trim();

            string when = span is null
                ? "until you turn it back on"
                : $"for {Durations[DurationCombo.SelectedIndex].Label.ToLowerInvariant()}";

            var lines = new List<string>
            {
                $"Turn playing off on {machine}, {when}?",
                ""
            };

            if (username.Length > 0) lines.Add($"Last played as: {username}");
            lines.Add(reason.Length > 0 ? $"Reason they will see: {reason}" : "No reason given — they will just be told it is off.");

            if (ServerBanCheck.IsChecked == true)
            {
                lines.Add("");
                lines.Add("They will also be banned on the running server by address" +
                          (username.Length > 0 ? " and by name." : "."));
            }

            var answer = MessageBox.Show(
                string.Join(Environment.NewLine, lines), "Turn playing off",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return;

            Result = new Choice(
                machine,
                _guid,
                username.Length == 0 ? null : username,
                reason.Length == 0 ? null : reason,
                span,
                ServerBanCheck.IsChecked == true);

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
