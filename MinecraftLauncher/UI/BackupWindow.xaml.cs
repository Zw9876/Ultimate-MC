using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MinecraftLauncher.Core;

namespace MinecraftLauncher.UI
{
    /// <summary>
    /// Copying a world into a dated zip, and managing the ones already kept.
    /// </summary>
    /// <remarks>
    /// Opened from the Client tab for single-player worlds and from the Server tab for
    /// a server's own, with the list of candidates passed in — this window does not go
    /// looking, so the Client tab can show only the version somebody has selected.
    ///
    /// <para><b>Backing up a running server.</b> The caller may pass a
    /// <paramref name="quiesce"/> pair. The first call asks the server to flush
    /// everything to disk and stop writing; the second lets it carry on. Without that,
    /// a backup of a live world can catch a region file halfway through a write — the
    /// copy still succeeds, which is the dangerous part, and the damage only shows up
    /// when somebody loads it. With it, the zip is a consistent moment.</para>
    ///
    /// The server is always let go again, including when the backup throws, because the
    /// alternative is a server that silently stops saving for the rest of the evening.
    /// </remarks>
    public partial class BackupWindow : Window
    {
        private readonly List<WorldBackups.Target> _targets;
        private readonly Func<Task>? _hold;
        private readonly Func<Task>? _release;
        private bool _busy;

        public BackupWindow(
            string intro, List<WorldBackups.Target> targets,
            Func<Task>? hold = null, Func<Task>? release = null)
        {
            InitializeComponent();

            _targets = targets;
            _hold    = hold;
            _release = release;

            IntroLabel.Text = intro;

            if (_targets.Count == 0)
            {
                WorldCombo.IsEnabled = false;
                BackupButton.IsEnabled = false;
                DeleteButton.IsEnabled = false;
                WorldDetail.Text = "Nothing here has a world in it yet.";
                return;
            }

            foreach (var target in _targets)
                WorldCombo.Items.Add(target.Label);

            WorldCombo.SelectedIndex = 0;
        }

        private WorldBackups.Target? Selected =>
            WorldCombo.SelectedIndex >= 0 && WorldCombo.SelectedIndex < _targets.Count
                ? _targets[WorldCombo.SelectedIndex]
                : null;

        private void World_Changed(object sender, RoutedEventArgs e) => Refresh();

        private void Refresh()
        {
            var target = Selected;
            if (target is null) return;

            long size = WorldBackups.SizeOf(target.Path);
            long? free = WorldBackups.FreeBytesFor(WorldBackups.Folder);

            var kept = WorldBackups.List(target);
            BackupsList.ItemsSource = kept;

            // Said plainly, because the machines this runs on are not spacious and a
            // 111 MB world against a few gigabytes free is worth seeing before the
            // button is pressed rather than after.
            string room = free is null
                ? "free space could not be read"
                : $"{WorldBackups.Describe(free.Value)} free on the drive";

            WorldDetail.Text =
                $"{target.Path}\n{WorldBackups.Describe(size)} on disk · {room} · " +
                $"{kept.Count} backup{(kept.Count == 1 ? "" : "s")} kept" +
                (kept.Count > 0 ? $", most recent {kept[0].WhenText}" : "");

            DeleteButton.IsEnabled = kept.Count > 0;

            if (WorldBackups.HasRoomFor(size) == false)
                StatusLabel.Text =
                    "There is not enough room on the drive for this world. Delete a " +
                    "backup or two first.";
        }

        private async void BackUp_Click(object sender, RoutedEventArgs e)
        {
            var target = Selected;
            if (target is null || _busy) return;

            _busy = true;
            BackupButton.IsEnabled = false;
            DeleteButton.IsEnabled = false;
            CloseButton.IsEnabled = false;
            StatusLabel.Text = $"Backing up {target.Name}…";

            var log = new Progress<string>(line => StatusLabel.Text = line);
            bool held = false;

            try
            {
                if (_hold is not null)
                {
                    StatusLabel.Text = "Asking the server to save everything first…";
                    await _hold();
                    held = true;
                }

                var result = await WorldBackups.CreateAsync(target, log);

                StatusLabel.Text = result.Ok
                    ? result.Message + $"  Kept in {WorldBackups.Folder}."
                    : result.Message;

                if (result.Ok)
                {
                    int pruned = WorldBackups.Prune(target);
                    if (pruned > 0)
                        StatusLabel.Text +=
                            $"  Removed {pruned} older backup{(pruned == 1 ? "" : "s")}, " +
                            $"keeping the last {WorldBackups.DefaultKeep}.";
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Backup failed: {ex.Message}";
            }
            finally
            {
                // Always, including after a failure: a server left with saving turned
                // off would lose the whole evening.
                if (held && _release is not null)
                {
                    try { await _release(); }
                    catch (Exception ex)
                    {
                        StatusLabel.Text +=
                            $"  The server could not be told to resume saving ({ex.Message}) — " +
                            "type save-on in the console.";
                    }
                }

                _busy = false;
                BackupButton.IsEnabled = true;
                CloseButton.IsEnabled = true;
                Refresh();
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (BackupsList.SelectedItem is not WorldBackups.Existing chosen) return;

            var answer = MessageBox.Show(
                $"Delete this backup for good?\n\n{chosen.Name}\n{chosen.SizeText}, taken {chosen.WhenText}" +
                "\n\nIt is not sent to the Recycle Bin.",
                "Delete backup", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return;

            try
            {
                File.Delete(chosen.Path);
                StatusLabel.Text = $"Deleted {chosen.Name}.";
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Could not delete it: {ex.Message}";
            }

            Refresh();
        }

        private void Folder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(WorldBackups.Folder);
                Process.Start(new ProcessStartInfo("explorer.exe", WorldBackups.Folder));
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Could not open the folder: {ex.Message}";
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
