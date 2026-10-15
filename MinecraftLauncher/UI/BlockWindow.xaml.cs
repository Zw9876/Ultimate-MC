using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MinecraftLauncher.Core;

namespace MinecraftLauncher.UI
{
    /// <summary>
    /// Collects who to turn playing off for, for how long, and why.
    /// </summary>
    /// <remarks>
    /// <para><b>Asks for the player's name, not their computer's.</b> That is how
    /// anybody actually thinks about it — nobody knows their colleagues' computer
    /// names, and the first version asked for one. The computer is looked up from the
    /// name in the fleet roster, and the block is still applied to the <i>computer</i>,
    /// so changing the name afterwards does not escape it.</para>
    ///
    /// <para>Which computer was resolved is shown as the name is typed, because a block
    /// on the wrong machine is worse than none and this is the only moment it can be
    /// caught. When no machine has ever reported that name there is nothing to block,
    /// and it says so rather than appearing to work.</para>
    /// </remarks>
    public partial class BlockWindow : Window
    {
        /// <param name="Machine">
        /// Null when no machine has reported that name. The launcher block is then
        /// impossible and only a server-side ban by name can be issued.
        /// </param>
        public sealed record Choice(
            string Username, string? Machine, string? Guid,
            string? Reason, TimeSpan? Duration, bool AlsoBanOnServer);

        public Choice? Result { get; private set; }

        private readonly FleetRoster.Listing _roster;
        private readonly bool _serverRunning;
        private FleetRoster.Machine? _resolved;

        private static readonly (string Label, TimeSpan? Span)[] Durations =
        {
            ("1 hour",                      TimeSpan.FromHours(1)),
            ("3 hours",                     TimeSpan.FromHours(3)),
            ("The rest of today (8 hours)", TimeSpan.FromHours(8)),
            ("1 day",                       TimeSpan.FromDays(1)),
            ("1 week",                      TimeSpan.FromDays(7)),
            ("Until I turn it back on",     null)
        };

        public BlockWindow(FleetRoster.Listing roster, string? username, bool serverRunning)
        {
            InitializeComponent();

            _roster = roster;
            _serverRunning = serverRunning;

            // Every name the roster has seen, so this is usually a pick rather than a
            // typed guess — and a typo is visible immediately as "no computer".
            foreach (string known in FleetRoster.KnownNames(roster))
                KnownNamesCombo.Items.Add(known);

            if (KnownNamesCombo.Items.Count == 0)
                KnownNamesCombo.IsEnabled = false;

            PlayerNameBox.Text = username ?? "";

            foreach (var (label, _) in Durations) DurationCombo.Items.Add(label);
            DurationCombo.SelectedIndex = 0;

            ServerBanCheck.IsEnabled = serverRunning;
            ServerBanCheck.IsChecked = serverRunning;

            if (!serverRunning)
                ServerBanCheck.Content =
                    "Also ban them on the running server — no server is running, so there is " +
                    "nothing to ban them on yet";

            Resolve();
            PlayerNameBox.Focus();
        }

        private void PlayerName_Changed(object sender, RoutedEventArgs e) => Resolve();

        /// <summary>Picking a seen name fills the box, which then resolves as normal.</summary>
        private void KnownName_Picked(object sender, RoutedEventArgs e)
        {
            if (KnownNamesCombo.SelectedItem is string picked)
                PlayerNameBox.Text = picked;
        }

        /// <summary>
        /// Works out which computer the typed name belongs to, and says so.
        /// </summary>
        private void Resolve()
        {
            if (WhoNote is null) return;

            string typed = (PlayerNameBox.Text ?? "").Trim();

            if (typed.Length == 0)
            {
                _resolved = null;
                WhoNote.Text = "Type the name they play under, or pick one that has been seen.";
                BlockButton.IsEnabled = false;
                return;
            }

            BlockButton.IsEnabled = true;

            var matches = FleetRoster.WhoPlayedAs(_roster, typed);
            _resolved = matches.FirstOrDefault();

            if (_resolved is null)
            {
                // Said plainly, because this is the case where the main part of the
                // feature cannot work at all.
                WhoNote.Text =
                    $"No computer has reported playing as “{typed}”, so there is " +
                    "nothing to stop their launcher with. " +
                    (_serverRunning
                        ? "They can still be banned on the running server by that name below — " +
                          "but changing their name gets round that, which is the whole reason " +
                          "this blocks a computer instead."
                        : "Nothing can be done from here until that name has been seen. A " +
                          "machine reports itself when somebody opens their launcher or " +
                          "presses PLAY while this host is serving.");
                return;
            }

            string ago = FleetRoster.Describe(DateTime.UtcNow - _resolved.Seen);

            var lines = new List<string>
            {
                $"That is {_resolved.Name}, seen {ago}, on launcher {_resolved.LauncherText}."
            };

            lines.Add(string.IsNullOrWhiteSpace(_resolved.Guid)
                ? "It has not reported a Windows identifier, so the block matches on the " +
                  "computer name only — renaming the computer would escape it."
                : "The block matches the computer name and its Windows identifier, so " +
                  "renaming it does not escape it.");

            // More than one machine under one name is worth saying out loud rather than
            // silently picking the newest.
            if (matches.Count > 1)
            {
                lines.Add($"{matches.Count} computers have played as that name " +
                          $"({string.Join(", ", matches.Select(m => m.Name))}). The most " +
                          "recent is the one being blocked.");
            }

            // The rail that stops the host locking itself out, surfaced early.
            if (string.Equals(_resolved.Name, FleetRoster.ThisMachine, StringComparison.OrdinalIgnoreCase))
                lines.Add("That is this computer, which is never blocked — the machine " +
                          "holding admin.flag is exempt, so this would have no effect.");

            if (_resolved.Version > new Version(0, 0, 0, 0) && _resolved.Version < PlayBlocks.ControlFloor)
                lines.Add($"Its launcher ({_resolved.LauncherText}) is older than " +
                          $"{PlayBlocks.ControlFloor} and may not act on being blocked, so " +
                          "it will carry on until it updates. A game already running keeps its " +
                          "old watcher even after that, until it is restarted.");

            WhoNote.Text = string.Join(" ", lines);
        }

        private void Block_Click(object sender, RoutedEventArgs e)
        {
            string username = (PlayerNameBox.Text ?? "").Trim();

            if (username.Length == 0)
            {
                StatusLabel.Text = "A name is needed.";
                PlayerNameBox.Focus();
                return;
            }

            if (_resolved is not null &&
                string.Equals(_resolved.Name, FleetRoster.ThisMachine, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    $"{username} last played on this computer.\n\nThe machine holding " +
                    "admin.flag is never blocked, so this would have no effect — and that " +
                    "is the rail which stops a misclick locking you out of your own launcher.",
                    "Not this computer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_resolved is null && !(_serverRunning && ServerBanCheck.IsChecked == true))
            {
                MessageBox.Show(
                    $"No computer has reported playing as “{username}”.\n\n" +
                    "There is nothing to stop their launcher with, and no server is running " +
                    "to ban them on, so pressing this would do nothing at all.",
                    "Nothing to block", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TimeSpan? span = DurationCombo.SelectedIndex >= 0
                ? Durations[DurationCombo.SelectedIndex].Span
                : TimeSpan.FromHours(1);

            string reason = ReasonBox.Text.Trim();

            string when = span is null
                ? "until you turn it back on"
                : $"for {Durations[DurationCombo.SelectedIndex].Label.ToLowerInvariant()}";

            var lines = new List<string>
            {
                _resolved is null
                    ? $"Ban {username} on the server, {when}?"
                    : $"Turn playing off for {username} on {_resolved.Name}, {when}?",
                ""
            };

            lines.Add(reason.Length > 0
                ? $"Reason they will see: {reason}"
                : "No reason given — they will just be told it is off.");

            if (ServerBanCheck.IsChecked == true)
            {
                lines.Add("");
                lines.Add(_resolved?.Address is { Length: > 0 }
                    ? $"They will also be banned on the running server by address and by name."
                    : "They will be banned on the running server by name. No address is on " +
                      "record, so a rename would get round it.");
            }

            var answer = MessageBox.Show(
                string.Join(Environment.NewLine, lines), "Turn playing off",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return;

            Result = new Choice(
                username,
                _resolved?.Name,
                _resolved?.Guid,
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
