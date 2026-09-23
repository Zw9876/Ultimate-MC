using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using MinecraftLauncher.Core;

namespace MinecraftLauncher.UI
{
    /// <summary>
    /// Updating a version's Fabric loader in place, either from the internet or from a
    /// pack carried in by hand.
    /// </summary>
    /// <remarks>
    /// Reinstalling a whole Minecraft version to get a newer loader is a long download
    /// these machines cannot make, and it would take the mods and worlds with it. Only
    /// the loader profile and its libraries change here.
    /// </remarks>
    public partial class LoaderUpdateWindow : Window
    {
        private readonly string _mcVersion;
        private CancellationTokenSource? _work;

        /// <summary>True when the installed loader changed, so the caller can refresh.</summary>
        public bool Changed { get; private set; }

        public LoaderUpdateWindow(string mcVersion)
        {
            InitializeComponent();

            _mcVersion = mcVersion;
            ShowCurrent();

            Loaded += (_, _) => Log($"Minecraft {_mcVersion}. Nothing has been changed yet.");
        }

        private void ShowCurrent()
        {
            var installed = LoaderVersions.Detect(_mcVersion, server: false, "Fabric");
            var all = FabricLoaderUpdate.InstalledLoaders(_mcVersion);

            CurrentLabel.Text = installed.Known
                ? $"Minecraft {_mcVersion} is running Fabric loader {installed.Version}." +
                  (all.Count > 1 ? $"  Also present: {string.Join(", ", all.Skip(1))}." : "")
                : $"Minecraft {_mcVersion} has no Fabric loader installed yet.";
        }

        private void Log(string line) =>
            Dispatcher.Invoke(() =>
            {
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.ScrollToEnd();
            });

        private IProgress<string> Reporter => new Progress<string>(Log);

        // ── online ──

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Busy(true, "Asking Fabric what it has...");

            try
            {
                _work = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var versions = await FabricLoaderUpdate.AvailableAsync(_mcVersion, _work.Token);

                OnlineVersionCombo.ItemsSource = versions;
                OnlineVersionCombo.SelectedIndex = 0;
                InstallOnlineButton.IsEnabled = versions.Count > 0;

                Log($"Fabric offers {versions.Count} loader versions for {_mcVersion}.");
                StatusLabel.Text = versions.Count > 0
                    ? $"Newest is {versions[0]}."
                    : "Fabric has no loader builds for this version.";
            }
            catch (Exception ex)
            {
                // The expected case on the offline machines, so say what to do instead.
                Log("Could not reach Fabric: " + ex.Message);
                StatusLabel.Text = "No internet here — use OPEN A PACK instead.";
            }
            finally
            {
                Busy(false);
            }
        }

        private async void InstallOnline_Click(object sender, RoutedEventArgs e)
        {
            if (OnlineVersionCombo.SelectedItem is not string chosen) return;

            var existing = FabricLoaderUpdate.InstalledLoaders(_mcVersion)
                .Where(v => v != chosen).ToList();

            var answer = MessageBox.Show(
                $"Install Fabric loader {chosen} for Minecraft {_mcVersion}?\n\n" +
                (existing.Count > 0
                    ? $"This REPLACES the loader you have now ({string.Join(", ", existing)}), " +
                      "which is removed once the new one is in place.\n\n"
                    : "") +
                "Only the loader changes. The game, your worlds and your mods are " +
                "left exactly as they are.",
                "Update Fabric loader", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            Busy(true, $"Installing Fabric loader {chosen}...");

            try
            {
                _work = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                await FabricLoaderUpdate.InstallOnlineAsync(_mcVersion, chosen, Reporter, _work.Token);

                Changed = true;
                ShowCurrent();

                var left = FabricLoaderUpdate.InstalledLoaders(_mcVersion);
                StatusLabel.Text = left.Count == 1
                    ? $"Fabric loader {chosen} installed, and it is now the only one."
                    : $"Fabric loader {chosen} installed. Still present: {string.Join(", ", left)}.";
            }
            catch (Exception ex)
            {
                Log("Install failed: " + ex.Message);
                StatusLabel.Text = "Install failed — nothing was changed.";
            }
            finally
            {
                Busy(false);
            }
        }

        // ── packs ──

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            var installed = FabricLoaderUpdate.InstalledLoaders(_mcVersion);
            if (installed.Count == 0)
            {
                MessageBox.Show(
                    $"There is no Fabric loader installed for {_mcVersion} to package.",
                    "Make a pack", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string loader = installed[0];

            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the loader pack",
                FileName = $"fabric-loader-{loader}-{_mcVersion}.zip",
                Filter = "Loader pack (*.zip)|*.zip"
            };
            if (save.ShowDialog(this) != true) return;

            Busy(true, $"Packaging Fabric loader {loader}...");

            try
            {
                _work = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var info = await FabricLoaderUpdate.ExportAsync(
                    _mcVersion, loader, save.FileName, Reporter, _work.Token);

                StatusLabel.Text = $"Made a pack: {info.Describe()}";
                Log("Copy that file to the other machines and use OPEN A PACK there.");
            }
            catch (Exception ex)
            {
                Log("Could not make a pack: " + ex.Message);
                StatusLabel.Text = "Nothing was written.";
            }
            finally
            {
                Busy(false);
            }
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            var open = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open a Fabric loader pack",
                Filter = "Loader pack (*.zip)|*.zip|All files (*.*)|*.*"
            };
            if (open.ShowDialog(this) != true) return;

            // Say what the file is before writing anything, so a wrong pick is caught
            // by the person rather than by the game later.
            var info = FabricLoaderUpdate.Inspect(open.FileName);
            if (info is null)
            {
                MessageBox.Show(
                    "That file is not a Fabric loader pack made by this launcher.\n\n" +
                    "On a computer with internet, open this window and press MAKE A PACK.",
                    "Open a pack", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var answer = MessageBox.Show(
                $"{info.Describe()}\n\nInstall it for Minecraft {_mcVersion}?\n\n" +
                "Only the loader is written. Your worlds and mods are left alone.",
                "Open a pack", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            Busy(true, "Installing from the pack...");

            try
            {
                _work = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var done = await FabricLoaderUpdate.ImportAsync(
                    _mcVersion, open.FileName, Reporter, _work.Token);

                Changed = true;
                ShowCurrent();
                StatusLabel.Text = $"Fabric loader {done.LoaderVersion} installed from the pack.";
            }
            catch (Exception ex)
            {
                Log("Could not install: " + ex.Message);
                StatusLabel.Text = "Nothing was changed.";
            }
            finally
            {
                Busy(false);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _work?.Cancel();
            Close();
        }

        private void Busy(bool busy, string? message = null)
        {
            RefreshButton.IsEnabled = !busy;
            ImportButton.IsEnabled = !busy;
            ExportButton.IsEnabled = !busy;
            InstallOnlineButton.IsEnabled = !busy && OnlineVersionCombo.SelectedItem is string;

            Cursor = busy ? Cursors.Wait : null;
            if (message is not null) StatusLabel.Text = message;
        }
    }
}
