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
    /// pack carried in by hand — for the client or for the server.
    /// </summary>
    /// <remarks>
    /// Reinstalling a whole Minecraft version to get a newer loader is a long download
    /// these machines cannot make, and it would take the mods and worlds with it. Only
    /// the loader profile and its libraries change here.
    ///
    /// Client and server are genuinely different jobs, not one job with two paths: a
    /// client's loader lives in a profile JSON, a server's is recorded inside
    /// <c>server.jar</c>. This window used to do only the client half, so MAKE A PACK
    /// silently produced a client pack however you got here, and the servers drifted —
    /// this one had accumulated loaders 0.19.2 and 0.19.3 while its clients ran 0.19.5.
    /// </remarks>
    public partial class LoaderUpdateWindow : Window
    {
        private readonly string _mcVersion;
        private readonly bool _hasServer;
        private CancellationTokenSource? _work;
        private bool _ready;

        /// <summary>True when the installed loader changed, so the caller can refresh.</summary>
        public bool Changed { get; private set; }

        /// <summary>Whether the server, rather than the client, is being updated.</summary>
        private bool Server => ServerRadio.IsChecked == true;

        public LoaderUpdateWindow(string mcVersion)
        {
            InitializeComponent();

            _mcVersion = mcVersion;
            _hasServer = FabricServerLoader.Exists(mcVersion);

            if (!_hasServer)
            {
                ServerRadio.IsEnabled = false;
                ServerRadio.ToolTip = $"There is no Fabric server for {mcVersion} on this machine.";
            }

            _ready = true;
            ShowCurrent();

            Loaded += (_, _) => Log($"Minecraft {_mcVersion}. Nothing has been changed yet.");
        }

        private void Target_Changed(object sender, RoutedEventArgs e)
        {
            // Fires once from InitializeComponent, before anything exists to update.
            if (!_ready) return;

            // The version list was fetched for the other target's benefit; the builds on
            // offer are the same, but leaving it selected invites installing to the
            // thing that was not being looked at.
            OnlineVersionCombo.ItemsSource = null;
            InstallOnlineButton.IsEnabled = false;

            ShowCurrent();
            StatusLabel.Text = "";
            Log(Server ? "Now updating the SERVER." : "Now updating the CLIENT.");
        }

        private void ShowCurrent()
        {
            var all = Server
                ? FabricServerLoader.InstalledLoaders(_mcVersion)
                : FabricLoaderUpdate.InstalledLoaders(_mcVersion);

            string what = Server ? "server" : "client";

            var installed = LoaderVersions.Detect(_mcVersion, Server, "Fabric");

            CurrentLabel.Text = installed.Known
                ? $"The {what} for Minecraft {_mcVersion} is running Fabric loader {installed.Version}." +
                  (all.Count > 1 ? $"  Also present: {string.Join(", ", all.Skip(1))}." : "")
                : $"The {what} for Minecraft {_mcVersion} has no Fabric loader installed yet.";

            TargetNote.Text = _hasServer
                ? (Server ? "servers\\fabric-" + _mcVersion : "versions\\" + _mcVersion)
                : "No Fabric server for this version on this machine.";

            ExportButton.ToolTip = Server
                ? "Packages this server's loader — server.jar and its libraries"
                : "Packages this client's loader — the profile and the libraries it names";
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

            bool server = Server;
            string what = server ? "server" : "client";

            var existing = (server
                    ? FabricServerLoader.InstalledLoaders(_mcVersion)
                    : FabricLoaderUpdate.InstalledLoaders(_mcVersion))
                .Where(v => v != chosen).ToList();

            var answer = MessageBox.Show(
                $"Install Fabric loader {chosen} for the {what} on Minecraft {_mcVersion}?\n\n" +
                (existing.Count > 0
                    ? $"This REPLACES the loader you have now ({string.Join(", ", existing)}), " +
                      "which is removed once the new one is in place.\n\n"
                    : "") +
                (server
                    ? "The server fetches the new loader's libraries the next time it starts, " +
                      "so it needs internet once more after this.\n\n"
                    : "") +
                "Only the loader changes. The game, your worlds and your mods are " +
                "left exactly as they are.",
                "Update Fabric loader", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            Busy(true, $"Installing Fabric loader {chosen} for the {what}...");

            try
            {
                _work = new CancellationTokenSource(TimeSpan.FromMinutes(10));

                if (server)
                    await FabricServerLoader.InstallOnlineAsync(_mcVersion, chosen, Reporter, _work.Token);
                else
                    await FabricLoaderUpdate.InstallOnlineAsync(_mcVersion, chosen, Reporter, _work.Token);

                Changed = true;
                ShowCurrent();

                var left = server
                    ? FabricServerLoader.InstalledLoaders(_mcVersion)
                    : FabricLoaderUpdate.InstalledLoaders(_mcVersion);

                StatusLabel.Text = left.Count == 1
                    ? $"Fabric loader {chosen} installed for the {what}, and it is now the only one."
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
            bool server = Server;
            string what = server ? "server" : "client";

            var installed = server
                ? FabricServerLoader.InstalledLoaders(_mcVersion)
                : FabricLoaderUpdate.InstalledLoaders(_mcVersion);

            if (installed.Count == 0)
            {
                MessageBox.Show(
                    $"There is no Fabric loader installed for the {what} on {_mcVersion} to package.",
                    "Make a pack", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string loader = installed[0];

            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"Save the {what} loader pack",
                // The kind is in the name because the two are not interchangeable and
                // both end up on a USB stick together.
                FileName = $"fabric-loader-{loader}-{_mcVersion}-{what}.zip",
                Filter = "Loader pack (*.zip)|*.zip"
            };
            if (save.ShowDialog(this) != true) return;

            Busy(true, $"Packaging the {what}'s Fabric loader {loader}...");

            try
            {
                _work = new CancellationTokenSource(TimeSpan.FromMinutes(10));

                var info = server
                    ? await FabricServerLoader.ExportAsync(
                        _mcVersion, save.FileName, Reporter, _work.Token)
                    : await FabricLoaderUpdate.ExportAsync(
                        _mcVersion, loader, save.FileName, Reporter, _work.Token);

                StatusLabel.Text = $"Made a pack: {info.Describe()}";
                Log($"Copy that file to the other machines and use OPEN A PACK there, " +
                    $"with {what.ToUpperInvariant()} selected.");
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
            bool server = Server;

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

            // Caught here as well as in Core so the message can name the fix rather
            // than just the fault.
            if (info.ForServer != server)
            {
                MessageBox.Show(
                    $"That is a {(info.ForServer ? "server" : "client")} pack, but " +
                    $"{(server ? "SERVER" : "CLIENT")} is selected.\n\n" +
                    "The two hold different files and cannot be swapped. Either choose " +
                    $"{(info.ForServer ? "SERVER" : "CLIENT")} above, or open the other pack.",
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

                var done = server
                    ? await FabricServerLoader.ImportAsync(
                        _mcVersion, open.FileName, Reporter, _work.Token)
                    : await FabricLoaderUpdate.ImportAsync(
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

            ClientRadio.IsEnabled = !busy;
            ServerRadio.IsEnabled = !busy && _hasServer;

            Cursor = busy ? Cursors.Wait : null;
            if (message is not null) StatusLabel.Text = message;
        }
    }
}
