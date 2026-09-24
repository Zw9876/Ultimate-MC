using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MinecraftLauncher.Core;

namespace MinecraftLauncher.UI
{
    public partial class MainWindow : Window
    {
        private static readonly string[] ServerTypes =
            { "Vanilla", "Fabric", "Forge", "NeoForge", "Paper", "Purpur" };

        /// <summary>What the Mods tab offers for a server: things that load something.</summary>
        /// <remarks>
        /// Vanilla is deliberately absent. It reads neither a mods folder nor a plugins
        /// folder, so choosing it could only ever produce an empty list and a warning
        /// saying it runs no mods. Paper and Purpur stay because they do load plugins —
        /// <see cref="ModManager.UsesPlugins"/> already sends them to <c>plugins/</c>.
        /// </remarks>
        private static readonly string[] ServerModLoaders =
            { "Fabric", "Forge", "NeoForge", "Paper", "Purpur" };

        /// <summary>What the Mods tab offers for a client.</summary>
        /// <remarks>
        /// Shorter than the server list for the same reason it exists: Paper and Purpur
        /// are server software, so a client cannot be either, and offering them invited
        /// a choice that describes nothing.
        /// </remarks>
        private static readonly string[] ClientModLoaders = { "Fabric", "Forge", "NeoForge" };

        /// <summary>Client-side loaders offered on the Setup tab.</summary>
        private static readonly string[] SetupLoaders = { "None", "Fabric", "Forge", "NeoForge" };

        private AppConfig _config = new();
        private IReadOnlyList<MojangVersion> _manifest = Array.Empty<MojangVersion>();
        private CancellationTokenSource? _setupCts;
        private string _modsFolder = "";

        /// <summary>
        /// Why closing matters while hosting: the skin server lives inside this
        /// process, unlike the PowerShell launcher which detached it.
        /// </summary>
        private const string HostingWarning =
            "This launcher is hosting the skin server. Closing it will drop custom " +
            "skins for everyone on the server.\n\n" +
            "Minimize the window instead to keep skins working.";

        private SkinServer? _skinServer;

        /// <summary>True when the skin server came up on its own to serve a Minecraft
        /// server we are hosting, rather than from the Start button on this tab.
        /// Users reported it "starting by itself"; it says so now.</summary>
        private bool _skinServerForHosting;

        /// <summary>"ip:port" of another machine already hosting a skin server. We
        /// use theirs instead of starting a second one, because two servers on one
        /// LAN means clients pick between them arbitrarily.</summary>
        private string? _adoptedSkinServer;

        /// <summary>
        /// The one running Minecraft server. Deliberately a single field rather than a
        /// list: two servers on one machine is a good way to run it out of memory, and
        /// nobody here wants that.
        /// </summary>
        private ServerSession? _serverSession;

        /// <summary>Counts the server saying it could not keep up, so "is it lagging?"
        /// has an answer that is not someone's impression.</summary>
        private readonly ServerHealth.Tally _behind = new();
        private string _consoleHeader = "";

        /// <summary>True between starting a pre-generation run and Chunky reporting it
        /// finished or the user cancelling it.</summary>
        private bool _pregenRunning;

        /// <summary>True while a run is paused, so one button can do both jobs.</summary>
        private bool _pregenPaused;

        /// <summary>
        /// The Nether radius to run once the overworld finishes, or null when the
        /// Nether was not asked for. Chunky takes one task at a time, so the second
        /// dimension has to wait for the first to report it is done.
        /// </summary>
        private int? _pregenNetherRadius;

        /// <summary>
        /// Chunks per second this machine actually managed, once a run has reported it.
        /// The built-in figure is from the slower dev box, so the first real rate is
        /// worth keeping — it makes every later estimate this session honest.
        /// </summary>
        private double _pregenObservedRate;

        /// <summary>Who is on the server, built from its own join and leave lines.</summary>
        private readonly PlayerRoster.Roster _roster = new();

        /// <summary>Re-reads playtimes so the list ages while it is open.</summary>
        private DispatcherTimer? _playersTimer;

        /// <summary>A newer launcher found on the LAN, waiting for the user to accept.</summary>
        private UpdateCheck? _pendingUpdate;

        /// <summary>
        /// Re-checks for a launcher update. The first check happens at startup, but the
        /// host's skin server usually is not up yet then — it starts with their
        /// Minecraft server, long after everyone else has opened their launcher. Without
        /// this, everyone would miss the window and think the feature was broken.
        /// </summary>
        private DispatcherTimer? _updateTimer;

        /// <summary>Set while shutting down to install an update, so the "you are
        /// hosting" close warning does not fire on a close the user already agreed to.</summary>
        private bool _applyingUpdate;
        private SkinPreviewHost? _preview;
        private bool _previewAttempted;
        private string? _webViewError;
        private SkinEntry? _pendingPreview;

        public MainWindow()
        {
            InitializeComponent();

            // Cards are always present, as in the PowerShell launcher, so the
            // wallpaper needs no dimming and the layout looks the same with or
            // without a background.png.
            AppBackground.TryApply(this);

            LoadEverything();
            Closing += MainWindow_Closing;
            Closed += (_, _) => _skinServer?.Dispose();

            // Deliberately not awaited: the machines this runs on have no internet,
            // so this asks the LAN, and the answer must never hold up the window.
            _ = CheckForUpdateAsync();
            StartUpdateWatch();
            ListenForUpdateExit();
        }

        /// <summary>
        /// Keeps asking, because the answer changes. The host's skin server comes up
        /// when they start their Minecraft server, which is typically well after
        /// everyone else has opened their launcher — so a single check at startup
        /// would miss it for everybody.
        /// </summary>
        /// <summary>
        /// Closes this window when something else needs to replace the executable.
        /// </summary>
        /// <remarks>
        /// Windows will not overwrite a running exe, so the background update watcher
        /// asks every other copy to leave before it swaps. Ignoring that would make
        /// the swap fail silently fifteen times and then start a second launcher.
        /// </remarks>
        private void ListenForUpdateExit()
        {
            try
            {
                var request = UpdateEnforcement.OpenExitForUpdateEvent();

                ThreadPool.RegisterWaitForSingleObject(request, (_, _) =>
                    Dispatcher.BeginInvoke(() =>
                    {
                        // Not if this window is the one installing; it closes itself.
                        if (_applyingUpdate) return;

                        // Reusing the flag that suppresses the "you are hosting"
                        // warning: this close was not the user's doing.
                        _applyingUpdate = true;
                        Close();
                    }),
                    null, Timeout.Infinite, executeOnlyOnce: true);
            }
            catch
            {
                // The swap script retries for a while; it may still get through.
            }
        }

        private void StartUpdateWatch()
        {
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
            _updateTimer.Tick += async (_, _) =>
            {
                if (_pendingUpdate is not null) { StopUpdateWatch(); return; }
                await CheckForUpdateAsync();
                if (_pendingUpdate is not null) StopUpdateWatch();
            };
            _updateTimer.Start();
        }

        private void StopUpdateWatch()
        {
            _updateTimer?.Stop();
            _updateTimer = null;
        }

        private void LoadEverything()
        {
            _config = AppConfig.Load();

            // Shown at all times: without it there is no way to tell which build a
            // machine is on, which is exactly what you need to know when working out
            // why an update did or did not appear.
            VersionLabel.Text = $"Launcher {LauncherPackage.CurrentVersion}";

            // The client can only launch what is installed, but hosting and
            // server-side mods also apply to versions that only exist under servers/.
            var versions = VersionScanner.InstalledVersions();
            var hostable = VersionScanner.HostableVersions();

            VersionCombo.ItemsSource = versions;
            SvVersionCombo.ItemsSource = hostable;
            ModVersionCombo.ItemsSource = hostable;

            SvTypeCombo.ItemsSource = ServerTypes;
            SvTypeCombo.SelectedIndex = 0;

            SetupLoaderCombo.ItemsSource = SetupLoaders;
            SetupLoaderCombo.SelectedIndex = 0;
            SvGamemodeCombo.ItemsSource = new[] { "survival", "creative", "adventure", "spectator", "hardcore" };
            SvGamemodeCombo.SelectedIndex = 0;
            SvDifficultyCombo.ItemsSource = new[] { "peaceful", "easy", "normal", "hard" };
            SvDifficultyCombo.SelectedIndex = 2;

            // Square first: it is Chunky's default and it matches vanilla's border,
            // which is square whatever shape the generated area is.
            PregenShapeCombo.ItemsSource = new[] { ChunkyPregen.ShapeSquare, ChunkyPregen.ShapeCircle };
            PregenShapeCombo.SelectedIndex = 0;

            // The tab opens on Client; ModTarget_Changed swaps the list when it moves.
            ModLoaderCombo.ItemsSource = ClientModLoaders;
            ModLoaderCombo.SelectedIndex = 0;

            SetupTypeCombo.ItemsSource = new[] { "Releases Only", "Snapshots Only", "All Versions" };
            SetupTypeCombo.SelectedIndex = 0;

            UsernameBox.Text = _config.Username;
            // Provisional; RestoreServerSettings re-sizes both sliders to the machine
            // straight after this and clamps the value into range.
            MemSlider.Value = _config.Memory > 0
                ? _config.Memory
                : JvmTuning.RecommendedClientHeapGb();
            MemLabel.Text = $"{(int)MemSlider.Value} GB";

            if (!string.IsNullOrEmpty(_config.LastVersion) && versions.Contains(_config.LastVersion))
                VersionCombo.SelectedItem = _config.LastVersion;
            else if (versions.Count > 0)
                VersionCombo.SelectedIndex = 0;

            RestoreServerSettings(hostable);

            if (hostable.Count > 0)
            {
                if (SvVersionCombo.SelectedIndex < 0) SvVersionCombo.SelectedIndex = 0;
                if (ModVersionCombo.SelectedIndex < 0) ModVersionCombo.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// Puts the Server tab back how it was left.
        /// </summary>
        /// <remarks>
        /// This is not just convenience. The memory slider used to start at 2 GB every
        /// single launch, so a modded server with a dozen players was being started on
        /// 2 GB however many times someone had moved the slider — and nothing said so.
        /// </remarks>
        private void RestoreServerSettings(IReadOnlyList<string> hostable)
        {
            if (!string.IsNullOrEmpty(_config.SvVersion) && hostable.Contains(_config.SvVersion))
                SvVersionCombo.SelectedItem = _config.SvVersion;

            if (!string.IsNullOrEmpty(_config.SvLoader) && ServerTypes.Contains(_config.SvLoader))
                SvTypeCombo.SelectedItem = _config.SvLoader;

            // The sliders are built at a fixed 16 GB, which is both unreachable on a
            // 16 GB machine and far too low on a workstation. Size them to the machine
            // before putting a value in one.
            int ceiling = JvmTuning.MaxHeapGb;
            SvMemSlider.Maximum = ceiling;
            MemSlider.Maximum = ceiling;

            // Nothing saved yet: pick something suited to the machine rather than the
            // 2 GB the slider happened to be built with.
            int memory = _config.SvMemory > 0
                ? _config.SvMemory
                : JvmTuning.RecommendedHeapGb(ceiling);

            SvMemSlider.Value = Math.Clamp(memory, (int)SvMemSlider.Minimum, ceiling);
            SvMemLabel.Text = $"{(int)SvMemSlider.Value} GB";

            // The client slider may have been left above the new ceiling by an older
            // build or a different machine.
            MemSlider.Value = Math.Clamp(MemSlider.Value, MemSlider.Minimum, ceiling);
            MemLabel.Text = $"{(int)MemSlider.Value} GB";
            UpdateMemoryAdvice();

            int suggested = JvmTuning.RecommendedHeapGb(ceiling);
            SvMachineInfo.Text =
                $"{JvmTuning.DescribeMachine()}.  Suggested for this machine: {suggested} GB." +
                (JvmTuning.LogicalCores <= 4
                    ? "  Minecraft's tick loop is single-threaded, so clock speed matters more than core count."
                    : "") +
                // Only ever shown when something really is holding the processor back.
                (MachinePower.Advice() is string warning ? $"\n{warning}" : "");

            SvPortBox.Text = _config.SvPort.ToString();
            SvMaxBox.Text  = _config.SvMaxPlayers.ToString();
            SvViewBox.Text = _config.SvView.ToString();
            SvSimBox.Text  = _config.SvSimulation.ToString();
            SvPvpCheck.IsChecked = _config.SvPvp;

            if (SvGamemodeCombo.ItemsSource is IEnumerable<string> modes && modes.Contains(_config.SvGamemode))
                SvGamemodeCombo.SelectedItem = _config.SvGamemode;
            if (SvDifficultyCombo.ItemsSource is IEnumerable<string> diffs && diffs.Contains(_config.SvDifficulty))
                SvDifficultyCombo.SelectedItem = _config.SvDifficulty;
        }

        private void RefreshInstalledVersions()
        {
            var versions = VersionScanner.InstalledVersions();
            var hostable = VersionScanner.HostableVersions();

            string? client = VersionCombo.SelectedItem as string;
            string? server = SvVersionCombo.SelectedItem as string;
            string? mods = ModVersionCombo.SelectedItem as string;

            VersionCombo.ItemsSource = versions;
            SvVersionCombo.ItemsSource = hostable;
            ModVersionCombo.ItemsSource = hostable;

            if (client is not null && versions.Contains(client)) VersionCombo.SelectedItem = client;
            if (server is not null && hostable.Contains(server)) SvVersionCombo.SelectedItem = server;
            if (mods is not null && hostable.Contains(mods)) ModVersionCombo.SelectedItem = mods;
        }

        /// <summary>
        /// Warns before closing while the skin server is running. Launching the game
        /// does not reach here — that path leaves the window open instead of closing
        /// it — so this only fires when someone closes the window themselves.
        /// </summary>
        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // An update closes the launcher on purpose, and the user has already been
            // told what that costs. Asking again would be a dead end — declining here
            // would leave the swap script waiting for a process that never exits.
            if (_applyingUpdate) return;

            // The server is a child process with its console piped in here. Closing the
            // launcher would leave it running with nobody able to see it or stop it, so
            // this asks first and shuts it down properly.
            if (_serverSession?.IsRunning == true)
            {
                var stopIt = MessageBox.Show(
                    $"{_serverSession.LoaderType} {_serverSession.Version} is still running.\n\n" +
                    "Closing the launcher will stop the server and disconnect anyone " +
                    "playing on it. The world is saved first.\n\nStop the server and close?",
                    "Server is running",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

                if (stopIt != MessageBoxResult.Yes) { e.Cancel = true; return; }

                // Blocking here is deliberate: the window must not go away until the
                // world is on disk, and a half-saved world is a real loss.
                _serverSession.StopAsync(TimeSpan.FromSeconds(45)).GetAwaiter().GetResult();
                _serverSession.Dispose();
                _serverSession = null;
            }

            if (_skinServer?.IsRunning != true) return;

            var answer = MessageBox.Show(
                $"{HostingWarning}\n\nClose anyway?",
                "Skin server is running",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            // Default is No, so a stray Enter keeps the server up.
            if (answer != MessageBoxResult.Yes) e.Cancel = true;
        }

        // ── Navigation ──
        private void ShowPanel(UIElement panel)
        {
            if (ClientPanel is null) return;   // still loading XAML

            ClientPanel.Visibility        = Visibility.Collapsed;
            ServerPanel.Visibility        = Visibility.Collapsed;
            ServerConsolePanel.Visibility = Visibility.Collapsed;
            ModsPanel.Visibility          = Visibility.Collapsed;
            SkinsPanel.Visibility         = Visibility.Collapsed;
            SetupPanel.Visibility         = Visibility.Collapsed;
            panel.Visibility = Visibility.Visible;
        }

        private void NavClient_Checked(object sender, RoutedEventArgs e) => ShowPanel(ClientPanel);
        /// <summary>
        /// The Server tab shows its settings or the live console, depending on whether
        /// a server is running — only one server runs at a time, so there is never a
        /// question of which one.
        /// </summary>
        private void NavServer_Checked(object sender, RoutedEventArgs e) =>
            ShowPanel(_serverSession?.IsRunning == true ? ServerConsolePanel : ServerPanel);

        private void NavMods_Checked(object sender, RoutedEventArgs e)
        {
            ShowPanel(ModsPanel);
            RefreshMods();
        }

        private void NavSkins_Checked(object sender, RoutedEventArgs e)
        {
            ShowPanel(SkinsPanel);
            RefreshSkins();
            UpdatePreview(SkinsList?.SelectedItem as SkinEntry);
        }

        private async void NavSetup_Checked(object sender, RoutedEventArgs e)
        {
            ShowPanel(SetupPanel);
            if (_manifest.Count == 0) await LoadManifestAsync(false);
        }

        // ── Client tab ──
        private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VersionCombo.SelectedItem is not string version) return;

            var loaders = VersionScanner.AvailableLoaders(version);
            LoaderCombo.ItemsSource = loaders;

            if (loaders.Count > 0)
            {
                int idx = loaders.IndexOf(_config.LastLoader);
                LoaderCombo.SelectedIndex = idx >= 0 ? idx : 0;
            }

            ShowCrashHint(version);
        }

        /// <summary>
        /// Says whether this version has crashed, beside the button that explains it.
        /// </summary>
        /// <remarks>
        /// The reports have always been written; the problem was that nobody knew they
        /// existed. Saying nothing when there are none is the point — a permanent
        /// mention of crashes on a tab people open to press PLAY would read as a
        /// warning about the version they just chose.
        /// </remarks>
        private void ShowCrashHint(string version)
        {
            if (CrashHintLabel is null) return;

            try
            {
                var reports = CrashReports.List(version);

                CrashHintLabel.Text = reports.Count == 0
                    ? ""
                    : reports.Count == 1
                        ? $"1 crash report — most recently {reports[0].WhenText}."
                        : $"{reports.Count} crash reports — most recently {reports[0].WhenText}.";
            }
            catch (Exception)
            {
                // A hint is not worth failing a tab over.
                CrashHintLabel.Text = "";
            }
        }

        private void CrashReports_Click(object sender, RoutedEventArgs e)
        {
            if (VersionCombo.SelectedItem is not string version)
            {
                MessageBox.Show("Pick a version first.", "Crash reports",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            new CrashReportWindow(version) { Owner = this }.ShowDialog();

            // A report may have been deleted from the folder while the window was open.
            ShowCrashHint(version);
        }

        private void MemSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MemLabel != null) MemLabel.Text = $"{(int)e.NewValue} GB";
            UpdateMemoryAdvice();
        }

        /// <summary>
        /// Says what this machine suits, and speaks up when the slider is well above it.
        /// </summary>
        /// <remarks>
        /// Turning client memory up is everyone's first instinct when the game stutters,
        /// and it is the wrong move: the extra heap goes untouched while every
        /// collection has more ground to cover, so the pauses get longer. Worth saying
        /// on the screen where the mistake is made.
        /// </remarks>
        private void UpdateMemoryAdvice()
        {
            if (MemAdvice is null) return;

            int suggested = JvmTuning.RecommendedClientHeapGb((int)MemSlider.Maximum);
            int current = (int)MemSlider.Value;
            int ram = JvmTuning.TotalRamGb;

            string machine = ram > 0 ? $"{ram} GB RAM in this machine.  " : "";
            string advice = $"{machine}Suggested for the game: {suggested} GB.";

            if (current > suggested + 1)
                advice += $"  More than that is not faster — the extra memory goes unused " +
                          "while every pause has more to sweep, so the stutter gets worse.";

            MemAdvice.Text = advice;
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (VersionCombo.SelectedItem is not string version)
            {
                ClientStatus.Text = "Select a version first.";
                return;
            }
            string loader = LoaderCombo.SelectedItem as string ?? "VANILLA";
            string username = UsernameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                ClientStatus.Text = "Enter a username.";
                return;
            }
            int memory = (int)MemSlider.Value;

            _config.Username = username;
            _config.Memory = memory;
            _config.LastVersion = version;
            _config.LastLoader = loader;
            _config.Save();

            try
            {
                ClientStatus.Text = "Launching…";
                var launched = ClientLauncher.Launch(version, loader, username, memory);

                // Something has to stay behind to close this game when the host ends
                // the session, because the launcher itself is about to close.
                GameWatcher.Spawn(launched.Game.Id, launched.SkinServerAddress);

                // The game is a separate process, so the launcher closes and gets
                // out of the way — matching the PowerShell version.
                //
                // Unless this machine is hosting: the skin server runs inside the
                // launcher, so closing would drop everyone's skins mid-session. The
                // PowerShell version could always close because it spawned its skin
                // server as a detached process.
                if (_skinServer?.IsRunning == true)
                {
                    ClientStatus.Text =
                        "Game launched. The launcher is staying open because it is " +
                        "hosting the skin server — closing it would drop custom skins " +
                        "for everyone on the server. Minimize it instead.";
                    return;
                }

                Close();
            }
            catch (Exception ex)
            {
                ClientStatus.Text = "Error: " + ex.Message;
                MessageBox.Show(ex.Message, "Launch failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Server tab ──
        private void SvVersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private void SvMemSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SvMemLabel != null) SvMemLabel.Text = $"{(int)e.NewValue} GB";
        }

        private async void StartServerButton_Click(object sender, RoutedEventArgs e)
        {
            // The box is editable, so take the typed text — SelectedItem stays null
            // for a version the user entered by hand.
            string version = (SvVersionCombo.Text ?? "").Trim();
            if (version.Length == 0)
            {
                ServerStatus.Text = "Pick a version, or type one to host.";
                return;
            }
            string loaderType = SvTypeCombo.SelectedItem as string ?? "Vanilla";

            // One server at a time, on purpose: a second one competes for memory and
            // CPU with the first, and on these machines that means both run badly.
            if (_serverSession?.IsRunning == true)
            {
                bool sameServer = _serverSession.Key
                    .Equals($"{loaderType}-{version}", StringComparison.OrdinalIgnoreCase);

                MessageBox.Show(
                    sameServer
                        ? $"{loaderType} {version} is already running.\n\n" +
                          "You cannot start the same server twice. Use the Server tab's " +
                          "console to manage it, or stop it first."
                        : $"{_serverSession.LoaderType} {_serverSession.Version} is already " +
                          "running.\n\nOnly one server runs at a time, so that it gets the " +
                          "whole machine. Stop that one before starting this one.",
                    "Server already running",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                ShowPanel(ServerConsolePanel);
                return;
            }

            if (!int.TryParse(SvPortBox.Text.Trim(), out int port)) port = 25565;
            if (!int.TryParse(SvMaxBox.Text.Trim(), out int maxPlayers)) maxPlayers = 10;

            // Catches a server left running by a previous launcher session, which the
            // check above cannot see. Without this the new server starts, fails to bind
            // the port, and dies — while the launcher reports success.
            if (ServerLauncher.IsPortInUse(port))
            {
                MessageBox.Show(
                    $"Something is already using port {port} on this computer.\n\n" +
                    "That is usually a Minecraft server still running from earlier — " +
                    "check for a java window, or restart the computer if you cannot " +
                    "find it. You can also host on a different port.",
                    "Port already in use",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Minecraft rejects anything outside these, and a typo here would stop the
            // server booting with a message nobody would connect to this box.
            if (!int.TryParse(SvViewBox.Text.Trim(), out int viewDistance)) viewDistance = 10;
            if (!int.TryParse(SvSimBox.Text.Trim(), out int simDistance)) simDistance = 6;
            viewDistance = Math.Clamp(viewDistance, 3, 32);
            simDistance  = Math.Clamp(simDistance, 3, 32);

            var settings = new ServerSettings
            {
                Port = port,
                Gamemode = SvGamemodeCombo.SelectedItem as string ?? "survival",
                Difficulty = SvDifficultyCombo.SelectedItem as string ?? "normal",
                MaxPlayers = maxPlayers,
                Pvp = SvPvpCheck.IsChecked == true,
                Memory = (int)SvMemSlider.Value,
                ViewDistance = viewDistance,
                SimulationDistance = simDistance
            };

            // Saved before the launch rather than after, so the settings survive even
            // if starting the server fails.
            _config.SvVersion    = version;
            _config.SvLoader     = loaderType;
            _config.SvMemory     = settings.Memory;
            _config.SvPort       = settings.Port;
            _config.SvMaxPlayers = settings.MaxPlayers;
            _config.SvGamemode   = settings.Gamemode;
            _config.SvDifficulty = settings.Difficulty;
            _config.SvPvp        = settings.Pvp;
            _config.SvView       = settings.ViewDistance;
            _config.SvSimulation = settings.SimulationDistance;
            _config.Save();

            // Clamping may have corrected what was typed; show what will be used.
            SvViewBox.Text = viewDistance.ToString();
            SvSimBox.Text  = simDistance.ToString();

            StartServerButton.IsEnabled = false;
            try
            {
                var progress = new Progress<string>(m => ServerStatus.Text = m);
                string serverDir = Paths.ServerDir(loaderType, version);

                await ServerJarInstaller.EnsureAsync(
                    version, loaderType, serverDir, progress, CancellationToken.None);

                // Two skin servers on one LAN is worse than one: clients broadcast and
                // take whichever answers first, so players would split across them and
                // see different skins. If another machine is already hosting one, point
                // Skin Restorer at theirs rather than starting a competing server.
                ServerStatus.Text = "Checking for an existing skin server...";
                var remote = await AdoptRemoteSkinServerAsync();

                string skinHost, skinNote;
                int skinPort;
                if (remote is not null)
                {
                    skinHost = remote.Ip.ToString();
                    skinPort = remote.Port;
                    skinNote = $"Using the skin server already running on {remote.Address}.";
                }
                else
                {
                    StartSkinServer(forHosting: true);
                    skinHost = "127.0.0.1";
                    skinPort = SkinServer.DefaultPort;
                    skinNote = $"Skin server started on {_skinServer?.HostIp}:{skinPort}.";
                }

                // Skin Restorer reads its config once at startup, so this has to be
                // written before the server process launches.
                SkinRestorerConfig.Write(serverDir, skinHost, skinPort);

                var result = ServerLauncher.Launch(version, loaderType, settings);
                ServerStatus.Text =
                    $"Server started on port {result.Port}.\n" +
                    skinNote + "\n" +
                    $"Folder: {result.ServerDir}";

                AttachServerSession(result.Session!, skinNote);

                // A newly created server folder makes that version hostable and
                // moddable, so refresh the lists that read from servers/.
                RefreshInstalledVersions();
                SvVersionCombo.Text = version;
            }
            catch (Exception ex)
            {
                ServerStatus.Text = "Error: " + ex.Message;
                MessageBox.Show(ex.Message, "Server failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                StartServerButton.IsEnabled = true;
            }
        }

        // ── Server console ──
        /// <summary>Lines kept in the console. A server left up all evening produces
        /// far more than anyone will scroll back through, and an unbounded TextBox
        /// eventually costs real memory.</summary>
        private const int ConsoleMaxLines = 2000;

        private void AttachServerSession(ServerSession session, string skinNote)
        {
            _serverSession = session;

            ConsoleOutput.Clear();
            _behind.Reset();
            _roster.Clear();
            RefreshPlayersList();

            // Open, not collapsed. Who is on is the thing a host looks at most, and
            // behind a toggle it was easy to forget the panel existed at all. The
            // header carries the count either way; this is the list and the playtimes.
            PlayersCard.Visibility = Visibility.Visible;
            PlayersToggleButton.Content = "HIDE";

            // Deliberately not asking the server who is on here. This runs as the
            // server is starting, so there is nobody to report yet and the command
            // would go to a process that cannot answer it. An empty roster is the
            // true answer for a server that has only just been launched; REFRESH is
            // there for the case where that is ever in doubt.
            SetPregenRunning(false);
            PregenCard.Visibility = Visibility.Collapsed;
            PregenToggleButton.Content = "PRE-GENERATE";
            PregenBar.Value = 0;
            PregenStatus.Text = "Not running.";
            _consoleHeader =
                $"{session.LoaderType} {session.Version} running on port {session.Port}.  " +
                $"{skinNote}\nFolder: {session.ServerDir}";
            ConsoleHeader.Text = _consoleHeader;

            AppendConsole($"--- starting {session.LoaderType} {session.Version} ---");

            session.Output += line => Dispatcher.BeginInvoke(() => AppendConsole(line));
            session.Exited += () => Dispatcher.BeginInvoke(OnServerExited);

            SetConsoleControlsEnabled(true);
            StopServerButton.Content = "STOP SERVER";
            ShowPanel(ServerConsolePanel);
            UpdateShutdownButton();
        }

        private void OnServerExited()
        {
            AppendConsole("--- server stopped ---");
            SetConsoleControlsEnabled(false);
            ConsoleHeader.Text = "The server has stopped. The last of its output is below.";

            _serverSession?.Dispose();
            _serverSession = null;
            StartServerButton.IsEnabled = true;

            // The console stays up so anyone can read why it stopped, which leaves the
            // one button that can get back to the settings — re-picking the Server tab
            // does nothing when it is already the selected one.
            StopServerButton.Content = "BACK TO SETTINGS";
            StopServerButton.IsEnabled = true;
            UpdateShutdownButton();
        }

        private void SetConsoleControlsEnabled(bool enabled)
        {
            ConsoleInput.IsEnabled      = enabled;
            ConsoleSendButton.IsEnabled = enabled;
            StopServerButton.IsEnabled  = enabled;

            PregenToggleButton.IsEnabled = enabled;
            PregenBorderButton.IsEnabled = enabled;
            PlayersToggleButton.IsEnabled = enabled;
            PlayersRefreshButton.IsEnabled = enabled;

            if (!enabled) _playersTimer?.Stop();

            // Nothing can be sent to a server that has gone, so leave the panel
            // showing its final state rather than offering dead buttons.
            if (!enabled)
            {
                PregenStartButton.IsEnabled  = false;
                PregenPauseButton.IsEnabled  = false;
                PregenCancelButton.IsEnabled = false;
                _pregenRunning = false;
            }
            else if (!_pregenRunning)
            {
                PregenStartButton.IsEnabled = true;
            }
        }

        private void AppendConsole(string line)
        {
            // The server's own verdict on whether it is coping, kept in the header so
            // it is not lost in the scroll.
            bool headerChanged = _behind.Observe(line);

            // Join and leave lines come through the same stream, so the roster follows
            // the server whether or not the players panel is open.
            if (_roster.Observe(line))
            {
                headerChanged = true;
                RefreshPlayersList();
            }

            if (headerChanged) UpdateConsoleHeader();

            // Chunky reports into the same stream, so the pre-generation panel follows
            // a run started from the command box or by hand, not only its own button.
            ObservePregen(line);

            // Stay pinned to the newest output, unless the reader has scrolled up to
            // look at something — in which case yanking them back is infuriating.
            bool atBottom = ConsoleOutput.VerticalOffset + ConsoleOutput.ViewportHeight
                            >= ConsoleOutput.ExtentHeight - 4;

            ConsoleOutput.AppendText(line + Environment.NewLine);

            if (ConsoleOutput.LineCount > ConsoleMaxLines)
            {
                int cut = ConsoleOutput.GetCharacterIndexFromLineIndex(
                    ConsoleOutput.LineCount - ConsoleMaxLines);
                if (cut > 0) ConsoleOutput.Text = ConsoleOutput.Text[cut..];
            }

            if (atBottom) ConsoleOutput.ScrollToEnd();
        }

        private void ConsoleSend_Click(object sender, RoutedEventArgs e) => SendConsoleCommand();

        private void ConsoleInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SendConsoleCommand();
        }

        private void SendConsoleCommand()
        {
            string command = ConsoleInput.Text.Trim();
            if (command.Length == 0 || _serverSession is null) return;

            // Echoed locally because the server does not repeat what it was told,
            // so without this the console gives no sign the command was sent.
            AppendConsole("> " + command);

            if (!_serverSession.SendCommand(command))
                AppendConsole("--- the server is not running, so that was not sent ---");

            ConsoleInput.Clear();
        }

        /// <summary>
        /// Rebuilds the console header from every part that contributes to it. Kept in
        /// one place because the lag tally and the roster both write here, and whichever
        /// wrote last used to erase the other.
        /// </summary>
        private void UpdateConsoleHeader()
        {
            string text = _consoleHeader;

            if (_roster.Count > 0 || _roster.Synced)
                text += "\n" + _roster.Describe();

            if (_behind.Describe() is string behind)
                text += "\n" + behind;

            ConsoleHeader.Text = text;
        }

        // ── Players ──

        private void PlayersToggle_Click(object sender, RoutedEventArgs e)
        {
            bool showing = PlayersCard.Visibility == Visibility.Visible;
            PlayersCard.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
            PlayersToggleButton.Content = showing ? "PLAYERS" : "HIDE";

            if (showing)
            {
                _playersTimer?.Stop();
                return;
            }

            // Ask straight away: anyone who joined before this console opened is not in
            // the roster yet, and opening the panel is exactly when that shows.
            if (!_roster.Synced) AskWhoIsOn();
            RefreshPlayersList();

            // Playtimes are computed on read, so the list has to be re-read to age.
            _playersTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _playersTimer.Tick -= PlayersTimer_Tick;
            _playersTimer.Tick += PlayersTimer_Tick;
            _playersTimer.Start();
        }

        private void PlayersTimer_Tick(object? sender, EventArgs e) => RefreshPlayersList();

        private void PlayersRefresh_Click(object sender, RoutedEventArgs e) => AskWhoIsOn();

        /// <summary>
        /// Sends <c>list</c> so the server itself says who is on. Not echoed to the
        /// console: this can be pressed repeatedly, and it is the launcher asking, not
        /// the user typing.
        /// </summary>
        private void AskWhoIsOn() => _serverSession?.SendCommand(PlayerRoster.ListCommand);

        private void RefreshPlayersList()
        {
            if (PlayersList is null) return;

            PlayersList.ItemsSource = _roster.Players;
            PlayersSummary.Text = _roster.Count == 0
                ? "WHO IS ON"
                : $"WHO IS ON — {_roster.Describe()}";
        }

        // ── Pre-generation ──

        private void PregenToggle_Click(object sender, RoutedEventArgs e)
        {
            bool showing = PregenCard.Visibility == Visibility.Visible;
            PregenCard.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
            PregenToggleButton.Content = showing ? "PRE-GENERATE" : "HIDE";

            if (!showing) UpdatePregenEstimate();
        }

        private void PregenRadius_Changed(object sender, TextChangedEventArgs e) => UpdatePregenEstimate();
        private void PregenShape_Changed(object sender, SelectionChangedEventArgs e) => UpdatePregenEstimate();
        private void PregenNether_Changed(object sender, RoutedEventArgs e) => UpdatePregenEstimate();

        /// <summary>The radius in the box, or null when it is not a usable number.</summary>
        private int? PregenRadius()
        {
            // Not yet built during XAML load, when TextChanged fires for the first time.
            if (PregenRadiusBox is null) return null;

            return int.TryParse(PregenRadiusBox.Text.Trim(), out int r) && r > 0 ? r : null;
        }

        private string PregenShape() =>
            PregenShapeCombo?.SelectedItem as string ?? ChunkyPregen.ShapeSquare;

        private void UpdatePregenEstimate()
        {
            if (PregenEstimate is null) return;

            int? radius = PregenRadius();
            if (radius is null)
            {
                PregenEstimate.Text = "Enter a radius in blocks.";
                return;
            }

            double rate = _pregenObservedRate > 0 ? _pregenObservedRate : ChunkyPregen.AssumedRate;
            var overworld = ChunkyPregen.EstimateFor(radius.Value, PregenShape(), rate);

            string text = $"Overworld {radius.Value * 2:N0} x {radius.Value * 2:N0} blocks — {overworld.Describe()}.";

            if (PregenNetherCheck?.IsChecked == true)
            {
                int netherRadius = ChunkyPregen.NetherRadiusFor(radius.Value);
                var nether = ChunkyPregen.EstimateFor(netherRadius, PregenShape(), rate);
                text += $"\nNether radius {netherRadius:N0} — {nether.Describe()}.";
            }

            text += _pregenObservedRate > 0
                ? $"\nBased on the {_pregenObservedRate:0.#} chunks/sec this machine actually managed."
                : "\nTimes are a rough guide from a slower test machine; this one should beat them.";

            PregenEstimate.Text = text;
        }

        private void PregenStart_Click(object sender, RoutedEventArgs e)
        {
            if (_serverSession is null) return;

            int? radius = PregenRadius();
            if (radius is null)
            {
                MessageBox.Show("Enter a radius in blocks, for example 3000.",
                    "Pre-generate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double rate = _pregenObservedRate > 0 ? _pregenObservedRate : ChunkyPregen.AssumedRate;
            var estimate = ChunkyPregen.EstimateFor(radius.Value, PregenShape(), rate);
            bool nether = PregenNetherCheck.IsChecked == true;

            var answer = MessageBox.Show(
                $"Pre-generate the overworld to a radius of {radius.Value:N0} blocks?\n\n" +
                $"That is {estimate.Chunks:N0} chunks, {ChunkyPregen.HumanBytes(estimate.Bytes)} on disk, " +
                $"roughly {ChunkyPregen.Human(estimate.Duration)}.\n" +
                (nether ? $"The Nether follows at radius {ChunkyPregen.NetherRadiusFor(radius.Value):N0}.\n" : "") +
                "\nPeople can keep playing while this runs. You can pause or stop it at any time, " +
                "and it picks up where it left off.",
                "Pre-generate world", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            _pregenNetherRadius = nether ? ChunkyPregen.NetherRadiusFor(radius.Value) : null;
            RunPregen(radius.Value, ChunkyPregen.Overworld);
        }

        /// <summary>Sends one dimension's worth of commands and puts the UI into the running state.</summary>
        private void RunPregen(int radius, string dimension)
        {
            if (_serverSession is null) return;

            foreach (string command in ChunkyPregen.StartCommands(radius, PregenShape(), dimension))
            {
                AppendConsole("> " + command);
                if (!_serverSession.SendCommand(command))
                {
                    AppendConsole("--- the server is not running, so that was not sent ---");
                    SetPregenRunning(false);
                    return;
                }
            }

            SetPregenRunning(true);
            PregenBar.Value = 0;
            PregenStatus.Text = $"Starting in the {ChunkyPregen.FriendlyDimension(dimension)}...";
        }

        private void SetPregenRunning(bool running)
        {
            _pregenRunning = running;
            _pregenPaused = false;

            PregenStartButton.IsEnabled  = !running;
            PregenPauseButton.IsEnabled  = running;
            PregenCancelButton.IsEnabled = running;
            PregenPauseButton.Content    = "PAUSE";

            if (!running)
            {
                _pregenNetherRadius = null;
                PregenRadiusBox.IsEnabled  = true;
                PregenShapeCombo.IsEnabled = true;
                PregenNetherCheck.IsEnabled = true;
            }
            else
            {
                // Changing these mid-run would describe something other than what is
                // actually happening.
                PregenRadiusBox.IsEnabled  = false;
                PregenShapeCombo.IsEnabled = false;
                PregenNetherCheck.IsEnabled = false;
            }
        }

        private void PregenPause_Click(object sender, RoutedEventArgs e)
        {
            if (_serverSession is null) return;

            string command = _pregenPaused
                ? ChunkyPregen.ContinueCommand()
                : ChunkyPregen.PauseCommand();

            AppendConsole("> " + command);
            _serverSession.SendCommand(command);

            _pregenPaused = !_pregenPaused;
            PregenPauseButton.Content = _pregenPaused ? "RESUME" : "PAUSE";
            PregenStatus.Text = _pregenPaused
                ? "Paused. The work so far is kept — resume whenever."
                : PregenStatus.Text;
        }

        private void PregenCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_serverSession is null) return;

            var answer = MessageBox.Show(
                "Stop pre-generating?\n\nChunks already built stay built — nothing is deleted. " +
                "Starting again later carries on from here.",
                "Stop pre-generating", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            AppendConsole("> " + ChunkyPregen.CancelCommand());
            _serverSession.SendCommand(ChunkyPregen.CancelCommand());

            SetPregenRunning(false);
            PregenStatus.Text = "Stopped. What was generated is still there.";
        }

        private void PregenBorder_Click(object sender, RoutedEventArgs e)
        {
            if (_serverSession is null) return;

            int? radius = PregenRadius();
            if (radius is null)
            {
                MessageBox.Show("Enter a radius in blocks first.",
                    "World border", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool nether = PregenNetherCheck.IsChecked == true;
            var commands = ChunkyPregen.BorderCommands(radius.Value, nether);

            // The radius-to-diameter conversion and the instant shrink are the two ways
            // this goes wrong, so both are spelled out rather than assumed.
            var answer = MessageBox.Show(
                $"Set the world border to match a radius of {radius.Value:N0} blocks?\n\n" +
                string.Join("\n", commands) + "\n\n" +
                "A border that shrinks takes effect immediately: anyone outside it is pushed " +
                "in and takes damage. Check where people have built before doing this.",
                "Set world border", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return;

            foreach (string command in commands)
            {
                AppendConsole("> " + command);
                _serverSession.SendCommand(command);
            }
        }

        /// <summary>
        /// Watches the console for Chunky's own reports. Chunky is the authority on
        /// progress and ETA, so the panel repeats what it says rather than counting
        /// anything itself — and that also keeps a run started by hand visible here.
        /// </summary>
        private void ObservePregen(string line)
        {
            if (ChunkyPregen.TryParseStarted(line, out var started))
            {
                SetPregenRunning(true);
                PregenBar.Value = 0;
                PregenStatus.Text =
                    $"Running in the {ChunkyPregen.FriendlyDimension(started.Dimension)} — " +
                    $"{started.Shape}, radius {started.Radius:N0}.";
                return;
            }

            if (!ChunkyPregen.TryParseProgress(line, out var progress)) return;

            PregenBar.Value = Math.Clamp(progress.Percent, 0, 100);
            PregenStatus.Text = progress.Describe();

            if (progress.Rate > 0)
            {
                // Only a believable generation rate is worth keeping — see
                // ChunkyPregen.MaxCredibleRate for why a run over existing ground lies.
                if (ChunkyPregen.IsCredibleRate(progress.Rate))
                    _pregenObservedRate = progress.Rate;

                if (!_pregenRunning) SetPregenRunning(true);
            }

            if (!progress.Finished) return;

            // Chunky runs one task at a time, so the Nether can only start now.
            if (_pregenNetherRadius is int netherRadius)
            {
                _pregenNetherRadius = null;
                AppendConsole("--- overworld done, starting the Nether ---");
                RunPregen(netherRadius, ChunkyPregen.Nether);
                return;
            }

            SetPregenRunning(false);
            PregenBar.Value = 100;
            UpdatePregenEstimate();
        }

        private async void StopServerButton_Click(object sender, RoutedEventArgs e)
        {
            // Doubles as the way back once the server has gone — see OnServerExited.
            if (_serverSession is null)
            {
                StopServerButton.Content = "STOP SERVER";
                ShowPanel(ServerPanel);
                return;
            }

            SetConsoleControlsEnabled(false);
            AppendConsole("--- stopping, waiting for the world to save ---");

            bool clean = await _serverSession.StopAsync(TimeSpan.FromSeconds(45));
            if (!clean)
                AppendConsole("--- it did not stop on its own and had to be closed ---");
        }

        // ── Shut down everything ──
        /// <summary>
        /// Shows the sidebar shutdown button only while this launcher is actually
        /// hosting something. A skin server adopted from another machine does not count:
        /// it is theirs, and stopping it is not this launcher's call.
        /// </summary>
        private void UpdateShutdownButton()
        {
            if (ShutdownAllButton is null) return;

            bool hosting = _serverSession?.IsRunning == true || _skinServer?.IsRunning == true;
            ShutdownAllButton.Visibility = hosting ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Ends the session for everyone: closes Minecraft on every machine using this
        /// skin server after a short countdown, then stops the Minecraft server and the
        /// skin server.
        /// </summary>
        /// <remarks>
        /// Nothing here is killed. Games are closed as if their X was clicked, and the
        /// server is stopped with its own <c>stop</c> command so it saves every world and
        /// player first. That matters more since <c>sync-chunk-writes</c> was turned off:
        /// more chunk data is held in memory, and a hard kill would throw it away. An
        /// accidental press therefore costs a disconnect and nothing else, and every
        /// player has a Keep playing button on their countdown.
        /// </remarks>
        private async void ShutdownAllButton_Click(object sender, RoutedEventArgs e)
        {
            var server = _serverSession?.IsRunning == true ? _serverSession : null;
            bool skins = _skinServer?.IsRunning == true;

            if (server is null && !skins)
            {
                UpdateShutdownButton();
                return;
            }

            int countdown = SessionShutdown.DefaultCountdownSeconds;

            var what = new List<string>();
            if (server is not null) what.Add($"the {server.LoaderType} {server.Version} server");
            if (skins) what.Add("the skin server");

            var answer = MessageBox.Show(
                $"Shut down {string.Join(" and ", what)}?\n\n" +
                (skins
                    ? $"Minecraft will also close on everyone's computer after a {countdown}-second " +
                      "countdown. Anyone who needs longer can press Keep playing.\n\n"
                    : "") +
                (server is not null
                    ? "The world is saved before the server stops, so nothing is lost.\n\n"
                    : "") +
                "The launcher stays open.",
                "Shut down for everyone",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes) return;

            ShutdownAllButton.IsEnabled = false;
            try
            {
                if (skins) _skinServer!.Shutdown.Announce(countdown);

                // Every game polls once a second, so they start their countdowns within a
                // second of this and run in step with it. The chat lines are for anyone
                // playing fullscreen, where the countdown window cannot draw over the game.
                server?.SendCommand(
                    $"say The server is shutting down. Minecraft will close in {countdown} seconds.");
                await Task.Delay(1000);

                for (int i = countdown; i >= 1; i--)
                {
                    ShutdownAllButton.Content = $"CLOSING IN {i}...";
                    server?.SendCommand($"say {i}...");
                    await Task.Delay(1000);
                }

                // Let the games finish leaving before the server goes, so they disconnect
                // cleanly rather than being dropped mid-save.
                ShutdownAllButton.Content = "SHUTTING DOWN...";
                await Task.Delay(3000);

                if (server is not null && server.IsRunning)
                {
                    SetConsoleControlsEnabled(false);
                    AppendConsole("--- shutting everything down, waiting for the world to save ---");

                    bool clean = await server.StopAsync(TimeSpan.FromSeconds(45));
                    if (!clean)
                        AppendConsole("--- it did not stop on its own and had to be closed ---");
                }

                // Skin server last: until the Minecraft server has finished saving,
                // players are still connected and their skins still need serving.
                if (_skinServer?.IsRunning == true) StopSkinServer();
            }
            finally
            {
                ShutdownAllButton.Content = "SHUT DOWN FOR EVERYONE";
                ShutdownAllButton.IsEnabled = true;
                UpdateShutdownButton();
            }
        }

        // ── Mods tab ──
        private void ModTarget_Changed(object sender, RoutedEventArgs e)
        {
            // Both, not just the combo: this fires from a RadioButton's Checked during
            // InitializeComponent, and which named fields exist by then depends on the
            // order they appear in the XAML.
            if (ModLoaderCombo is null || ModServerRadio is null) return;

            bool server = ModServerRadio.IsChecked == true;
            string? was = ModLoaderCombo.SelectedItem as string;

            // Enabled for clients too. It picks the server folder when hosting, but for
            // a client it says which loader these mods are meant for — without that the
            // client always read as "Vanilla", which runs no mods, so every mod in a
            // perfectly good Fabric folder looked wrong.
            ModLoaderCombo.IsEnabled = true;

            string[] offered = server ? ServerModLoaders : ClientModLoaders;
            if (!ReferenceEquals(ModLoaderCombo.ItemsSource, offered))
                ModLoaderCombo.ItemsSource = offered;

            // Keep the choice when it still applies — moving Client to Server should not
            // silently retarget someone's Fabric folder. Otherwise follow the Client tab,
            // which is the loader that will actually read this folder.
            int match = Array.FindIndex(offered, t => t.Equals(was, StringComparison.OrdinalIgnoreCase));

            if (match < 0 && !server && LoaderCombo?.SelectedItem is string clientLoader)
                match = Array.FindIndex(offered, t => t.Equals(clientLoader, StringComparison.OrdinalIgnoreCase));

            ModLoaderCombo.SelectedIndex = match >= 0 ? match : 0;

            RefreshMods();
        }

        private void ModSelector_Changed(object sender, SelectionChangedEventArgs e) => RefreshMods();

        /// <summary>The loader the Mods tab is working with.</summary>
        /// <remarks>
        /// The fallback used to be "Vanilla", which then had to be special-cased
        /// downstream as the one answer that runs no mods. Nothing in either list runs
        /// none, so the first entry is a safe default rather than a trap.
        /// </remarks>
        private string ModLoaderType =>
            ModLoaderCombo?.SelectedItem as string
            ?? (ModServerRadio?.IsChecked == true ? ServerModLoaders : ClientModLoaders)[0];

        private void RefreshMods()
        {
            if (ModsList is null) return;

            if (ModVersionCombo.SelectedItem is not string version)
            {
                ModsList.ItemsSource = null;
                ModFolderLabel.Text = "No installed versions yet — use the Setup tab first.";
                ModStatus.Text = "";
                _modsFolder = "";
                return;
            }

            bool server = ModServerRadio.IsChecked == true;
            string loaderType = ModLoaderType;

            try
            {
                _modsFolder = ModManager.FolderFor(version, server, loaderType);
                var mods = ModManager.List(_modsFolder);

                ModsList.ItemsSource = mods;
                ModLoaderLabel.Text = server ? "Server Type" : "Loader these mods are for";
                ModFolderLabel.Text = server
                    ? $"→ {_modsFolder}"
                    : $"→ {_modsFolder}   (shared by every loader on {version})";

                string noun = ModManager.Noun(server, loaderType).ToLowerInvariant();
                ModStatus.Text = $"{mods.Count} {noun}(s) · {mods.Count(m => m.Enabled)} enabled";

                ShowModMismatch(mods, loaderType);
            }
            catch (Exception ex)
            {
                ModsList.ItemsSource = null;
                ModFolderLabel.Text = "Error: " + ex.Message;
                ModStatus.Text = "";
                _modsFolder = "";
                ModMismatchCard.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Warns when the folder holds mods the chosen loader cannot run — the one
        /// failure this tab can see coming, because the client mods folder is shared
        /// by every loader on a Minecraft version.
        /// </summary>
        private void ShowModMismatch(System.Collections.Generic.List<ModEntry> mods, string loaderType)
        {
            bool server = ModServerRadio.IsChecked == true;
            string version = ModVersionCombo.SelectedItem as string ?? "";
            var installed = LoaderVersions.Detect(version, server, loaderType);

            // Two different problems, and the fixes differ: mods for the wrong loader
            // should be turned off, while mods for a newer loader want the loader
            // updating. Both are worth saying, and the wrong-loader one first.
            string? wrongLoader = ModInspector.WarnAbout(mods, loaderType);
            string? oldLoader = ModInspector.WarnAboutLoaderVersion(mods, loaderType, installed.Version);
            string? duplicates = ModInspector.WarnAboutDuplicates(mods);

            if (installed.Known)
                ModFolderLabel.Text += $"   ·   {installed.Describe()}";

            string? warning = string.Join("\n\n",
                new[] { wrongLoader, duplicates, oldLoader }.Where(w => w is not null));

            if (warning.Length == 0) warning = null;

            if (warning is null)
            {
                ModMismatchCard.Visibility = Visibility.Collapsed;
                return;
            }

            // The button only turns off wrong-loader mods; offering it for a loader
            // that merely needs updating would suggest the wrong fix.
            ModFixButton.Visibility = wrongLoader is not null ? Visibility.Visible : Visibility.Collapsed;

            ModMismatchText.Text = warning;
            ModMismatchCard.Visibility = Visibility.Visible;
        }

        private void ModGetOnline_Click(object sender, RoutedEventArgs e)
        {
            if (_modsFolder.Length == 0) return;

            if (ModVersionCombo.SelectedItem is not string version)
            {
                MessageBox.Show("Pick a version first.", "Get mods online",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string loaderType = ModLoaderType;

            if (ModrinthApi.LoaderFacet(loaderType) is null)
            {
                MessageBox.Show(
                    $"{loaderType} does not run mods, so there is nothing to browse for.\n\n" +
                    "Choose the loader you are actually going to play — Fabric, Forge or " +
                    "NeoForge — in the box above the list.",
                    "Get mods online", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var browser = new ModBrowserWindow(
                _modsFolder, version, loaderType, ModServerRadio.IsChecked == true) { Owner = this };
            browser.ShowDialog();

            // Anything installed has to show up in the list, and go through the same
            // loader check as a jar copied in by hand.
            if (browser.DownloadedSomething) RefreshMods();
        }

        private void ModLoaderUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (ModVersionCombo.SelectedItem is not string version)
            {
                MessageBox.Show("Pick a version first.", "Update loader",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Fabric only for now: Forge and NeoForge cannot have their loader swapped
            // under an existing install the way Fabric can — theirs is baked into the
            // profile the installer generates.
            var updater = new LoaderUpdateWindow(version) { Owner = this };
            updater.ShowDialog();

            if (updater.Changed) RefreshMods();
        }

        private async void ModCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (ModVersionCombo.SelectedItem is not string version)
            {
                MessageBox.Show("Pick a version first.", "Check for updates",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string loaderType = ModLoaderType;
            if (ModrinthApi.LoaderFacet(loaderType) is null)
            {
                MessageBox.Show($"{loaderType} does not take mods from Modrinth.",
                    "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ModsList.ItemsSource is not List<ModEntry> rows || rows.Count == 0)
            {
                ModStatus.Text = "Nothing here to check.";
                return;
            }

            // Disabled jars are deliberately left out. A turned-off mod is not running,
            // so "newer build available" is noise, and updating it would quietly bring
            // back something that was switched off on purpose.
            var enabled = rows.Where(m => m.Enabled).ToList();
            if (enabled.Count == 0)
            {
                ModStatus.Text = "Nothing enabled here to check.";
                return;
            }

            ModCheckUpdatesButton.IsEnabled = false;
            ModStatus.Text = $"Asking Modrinth about {enabled.Count} mod(s)...";

            try
            {
                using var work = new CancellationTokenSource(TimeSpan.FromMinutes(3));

                var found = await ModrinthApi.CheckForUpdatesAsync(
                    enabled.Select(m => (m.FileName, m.FullPath)),
                    version, loaderType, null, work.Token);

                var byPath = found.ToDictionary(r => r.FullPath, StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows)
                    row.UpdateText = byPath.TryGetValue(row.FullPath, out var r) ? r.Text : "";

                ModsList.Items.Refresh();

                var updatable = found.Where(r => r.HasUpdate).ToList();
                int unknown = found.Count(r => !r.OnModrinth);
                string aside = unknown > 0 ? $"  {unknown} not on Modrinth." : "";

                ModStatus.Text = updatable.Count == 0
                    ? $"Everything is up to date.{aside}"
                    : $"{updatable.Count} can be updated.{aside}";

                if (updatable.Count > 0) await OfferModUpdates(updatable);
            }
            catch (OperationCanceledException)
            {
                ModStatus.Text = "The update check timed out.";
            }
            catch (Exception ex)
            {
                // The usual case on the offline machines, so name the cause.
                ModStatus.Text = "Could not reach Modrinth: " + ex.Message;
            }
            finally
            {
                ModCheckUpdatesButton.IsEnabled = true;
            }
        }

        private async Task OfferModUpdates(List<ModrinthApi.ModUpdateStatus> updatable)
        {
            const int show = 12;

            string list = string.Join(Environment.NewLine, updatable.Take(show)
                .Select(u => $"    {u.Title}:  {u.Installed!.VersionNumber} → {u.Latest!.VersionNumber}"));

            if (updatable.Count > show)
                list += $"{Environment.NewLine}    ...and {updatable.Count - show} more";

            var answer = MessageBox.Show(
                $"{updatable.Count} mod(s) have a newer build for this version:{Environment.NewLine}{Environment.NewLine}" +
                list + Environment.NewLine + Environment.NewLine +
                "Install them now?" + Environment.NewLine + Environment.NewLine +
                "The build you have is turned off rather than deleted, so going back is one click.",
                "Update mods", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            int done = 0, failed = 0;
            using var work = new CancellationTokenSource(TimeSpan.FromMinutes(20));

            foreach (var u in updatable)
            {
                if (u.Latest?.File is null) { failed++; continue; }

                ModStatus.Text = $"Updating {u.Title}...";

                try
                {
                    var result = await ModrinthApi.InstallAsync(u.Latest.File, _modsFolder, work.Token);
                    if (result.Status == ModrinthApi.InstallStatus.Failed) failed++; else done++;
                }
                catch (Exception)
                {
                    // One mod failing must not abandon the rest.
                    failed++;
                }
            }

            RefreshMods();
            ModStatus.Text = failed == 0
                ? $"Updated {done} mod(s). Check for updates again to confirm."
                : $"Updated {done}, {failed} failed.";
        }

        private void ModFix_Click(object sender, RoutedEventArgs e)
        {
            if (_modsFolder.Length == 0) return;

            string loaderType = ModLoaderType;
            var wrong = ModManager.List(_modsFolder)
                .Where(m => m.Enabled && !ModInspector.IsCompatible(m.Loaders, loaderType))
                .ToList();

            if (wrong.Count == 0) { RefreshMods(); return; }

            var answer = MessageBox.Show(
                $"Turn off {wrong.Count} mod(s) that are not built for {loaderType}?\n\n" +
                string.Join("\n", wrong.Take(12).Select(m => "  " + m.DisplayName)) +
                (wrong.Count > 12 ? $"\n  ...and {wrong.Count - 12} more" : "") +
                "\n\nNothing is deleted — they are renamed to .disabled. Switching this " +
                "back to the other loader and pressing the same button turns them on again.",
                "Turn off mods for another loader", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            var turnedOff = ModManager.DisableIncompatible(_modsFolder, loaderType);

            // The other half: whatever was disabled for the previous loader and does
            // suit this one should come back, or switching loaders leaves you with an
            // empty mods folder and no clue why.
            var turnedOn = ModManager.EnableCompatible(_modsFolder, loaderType);

            RefreshMods();

            string message = $"Turned off {turnedOff.Count}.";
            if (turnedOn.Count > 0) message += $" Turned on {turnedOn.Count} that suit {loaderType}.";
            ModStatus.Text = message;
        }

        private void ModAdd_Click(object sender, RoutedEventArgs e)
        {
            if (_modsFolder.Length == 0) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Mod jars (*.jar)|*.jar",
                Multiselect = true,
                Title = "Select mod jars"
            };
            if (dialog.ShowDialog() != true) return;

            var result = ModManager.Add(_modsFolder, dialog.FileNames);
            RefreshMods();

            if (result.Skipped.Count > 0)
                MessageBox.Show(
                    $"Added {result.Added}.\n\nSkipped (already present): {string.Join(", ", result.Skipped)}",
                    "Add mods", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ModToggle_Click(object sender, RoutedEventArgs e)
        {
            var selected = ModsList.SelectedItems.OfType<ModEntry>().ToList();
            if (selected.Count == 0) return;

            foreach (var mod in selected)
                ModManager.SetEnabled(_modsFolder, mod.FileName, !mod.Enabled);

            RefreshMods();
        }

        private void ModRemove_Click(object sender, RoutedEventArgs e)
        {
            var selected = ModsList.SelectedItems.OfType<ModEntry>().ToList();
            if (selected.Count == 0) return;

            string names = string.Join("\n", selected.Select(m => m.DisplayName));
            if (MessageBox.Show($"Delete these files?\n\n{names}", "Remove",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            foreach (var mod in selected)
                ModManager.Remove(_modsFolder, mod.FileName);

            RefreshMods();
        }

        private void ModPurgeDisabled_Click(object sender, RoutedEventArgs e)
        {
            if (_modsFolder.Length == 0) return;

            var disabled = ModManager.DisabledIn(_modsFolder);
            string noun = ModManager.Noun(ModServerRadio.IsChecked == true, ModLoaderType).ToLowerInvariant();

            if (disabled.Count == 0)
            {
                ModStatus.Text = $"There are no turned-off {noun}s here to clear out.";
                return;
            }

            // Named, not counted. This is the one button here that removes something,
            // and seeing the list is what catches "that one is off on purpose".
            const int show = 15;
            string names = string.Join(Environment.NewLine,
                disabled.Take(show).Select(m => "    " + m.DisplayName));

            if (disabled.Count > show)
                names += $"{Environment.NewLine}    ...and {disabled.Count - show} more";

            var answer = MessageBox.Show(
                $"Delete {disabled.Count} turned-off {noun}(s)?{Environment.NewLine}{Environment.NewLine}" +
                names + Environment.NewLine + Environment.NewLine +
                "They go to the Recycle Bin, so this can be undone." + Environment.NewLine +
                $"Nothing that is switched on is touched.",
                $"Delete disabled {noun}s", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return;

            var result = ModManager.RemoveDisabled(_modsFolder);
            RefreshMods();

            ModStatus.Text = result.Failed.Count == 0
                ? $"Deleted {result.Removed.Count} turned-off {noun}(s) — they are in the Recycle Bin."
                : $"Deleted {result.Removed.Count}; {result.Failed.Count} could not be removed " +
                  "(the game may still be open).";
        }

        private void ModOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_modsFolder.Length == 0) return;
            ModManager.OpenFolder(_modsFolder);
        }

        // ── Download versions ──
        private void DownloadVersions_Click(object sender, RoutedEventArgs e)
        {
            // Give the user something to edit the first time they open this.
            VersionLinks.EnsureFileExists();
            ShowVersionLinks();
        }

        private void ShowVersionLinks()
        {
            var links = VersionLinks.Load();

            var window = new Window
            {
                Title = "Download Versions",
                Width = 620,
                Height = 460,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (System.Windows.Media.Brush)Application.Current.Resources["BgBrush"]
            };

            var layout = new Grid { Margin = new Thickness(16) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var intro = new TextBlock
            {
                Text = links.Count > 0
                    ? "Click a version to open its download page. Extract the download into the "
                      + "launcher's versions folder, then reopen the launcher."
                    : $"No links yet. Choose \"Edit list\" and add them to {VersionLinks.FileName}, "
                      + "one per line, as:  Name | https://…",
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MutedBrush"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(intro, 0);
            layout.Children.Add(intro);

            var list = new ItemsControl
            {
                ItemsSource = links,
                Margin = new Thickness(0, 0, 0, 12)
            };
            var scroller = new ScrollViewer
            {
                Content = list,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroller, 1);
            layout.Children.Add(scroller);

            // Each row is a button so it is keyboard reachable, not just clickable.
            var itemTemplate = new DataTemplate();
            var buttonFactory = new FrameworkElementFactory(typeof(Button));
            buttonFactory.SetValue(StyleProperty, Application.Current.Resources["LinkButton"]);
            buttonFactory.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding("Label"));
            buttonFactory.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Url"));
            buttonFactory.SetBinding(FrameworkElement.TagProperty, new System.Windows.Data.Binding("Url"));
            buttonFactory.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                new RoutedEventHandler(VersionLink_Click));
            itemTemplate.VisualTree = buttonFactory;
            list.ItemTemplate = itemTemplate;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var edit = new Button
            {
                Content = "Edit list",
                Width = 100,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)Application.Current.Resources["PlainButton"]
            };
            edit.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = VersionLinks.FilePath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Could not open the file",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };

            var reload = new Button
            {
                Content = "Reload",
                Width = 90,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)Application.Current.Resources["PlainButton"]
            };
            reload.Click += (_, _) => { window.Close(); ShowVersionLinks(); };

            var close = new Button
            {
                Content = "Close",
                Width = 90,
                Height = 32,
                Style = (Style)Application.Current.Resources["PlainButton"],
                IsCancel = true
            };
            close.Click += (_, _) => window.Close();

            buttons.Children.Add(edit);
            buttons.Children.Add(reload);
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 2);
            layout.Children.Add(buttons);

            window.Content = layout;
            window.ShowDialog();
        }

        private void VersionLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string url }) return;

            // Re-check here as well: the list is a plain text file a user edits, so
            // never hand the shell anything that is not an http(s) address.
            if (!VersionLinks.IsSafeWebUrl(url))
            {
                MessageBox.Show($"Not a web address:\n{url}", "Cannot open",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Could not open the link",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ── Skins tab ──
        private void RefreshSkins()
        {
            if (SkinsList is null) return;

            string? selected = (SkinsList.SelectedItem as SkinEntry)?.Username;
            var skins = SkinStore.List();
            SkinsList.ItemsSource = skins;

            if (selected is not null)
                SkinsList.SelectedItem = skins.FirstOrDefault(s => s.Username == selected);

            // Land on something so the preview has a subject; otherwise the tab
            // opens on "select a skin" even when skins exist.
            if (SkinsList.SelectedItem is null && skins.Count > 0)
                SkinsList.SelectedIndex = 0;

            UpdateSkinServerUi();
        }

        private void SkinsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            UpdatePreview(SkinsList.SelectedItem as SkinEntry);

        private void SkinAdd_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "PNG skins (*.png)|*.png",
                Title = "Select a skin PNG"
            };
            if (dialog.ShowDialog() != true) return;

            string suggested = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
            string? username = PromptForUsername(suggested);
            if (username is null) return;

            try
            {
                SkinStore.Add(dialog.FileName, username);
                RefreshSkins();
                SkinsList.SelectedItem = SkinStore.List().FirstOrDefault(s => s.Username == username);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Could not add skin", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SkinToggleModel_Click(object sender, RoutedEventArgs e)
        {
            if (SkinsList.SelectedItem is not SkinEntry skin) return;

            SkinStore.SetModel(skin.Username, skin.Model == "alex" ? "steve" : "alex");
            RefreshSkins();
            UpdatePreview(SkinsList.SelectedItem as SkinEntry);
        }

        private void SkinRemove_Click(object sender, RoutedEventArgs e)
        {
            if (SkinsList.SelectedItem is not SkinEntry skin) return;

            if (MessageBox.Show($"Delete the skin for {skin.Username}?", "Remove skin",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            SkinStore.Remove(skin.Username);
            RefreshSkins();
            UpdatePreview(null);
        }

        private void SkinOpenFolder_Click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo { FileName = SkinStore.Dir, UseShellExecute = true });

        private void SkinOpenInBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (SkinsList.SelectedItem is not SkinEntry skin) return;

            try
            {
                string page = SkinPreviewHtml.WriteStandalone(skin.FullPath, skin.Model, skin.Username);
                Process.Start(new ProcessStartInfo { FileName = page, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Preview failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string? PromptForUsername(string suggested)
        {
            var dialog = new Window
            {
                Title = "Username for skin",
                Width = 340, Height = 160,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Background = (System.Windows.Media.Brush)Application.Current.Resources["BgBrush"]
            };

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "Which Minecraft username does this skin belong to?",
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextBrush"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var box = new TextBox
            {
                Text = suggested,
                Height = 28,
                Style = (Style)Application.Current.Resources["FieldBox"]
            };
            panel.Children.Add(box);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            string? result = null;
            var ok = new Button
            {
                Content = "OK", Width = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)Application.Current.Resources["PlainButton"], IsDefault = true
            };
            ok.Click += (_, _) => { result = box.Text.Trim(); dialog.Close(); };

            var cancel = new Button
            {
                Content = "Cancel", Width = 80, Height = 28,
                Style = (Style)Application.Current.Resources["PlainButton"], IsCancel = true
            };
            cancel.Click += (_, _) => dialog.Close();

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            dialog.Content = panel;
            box.Focus();
            box.SelectAll();
            dialog.ShowDialog();

            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        // ── Skin server control ──
        private async void SkinServerToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_skinServer?.IsRunning == true) { StopSkinServer(); return; }

            SkinServerToggle.IsEnabled = false;
            try
            {
                if (await AdoptRemoteSkinServerAsync() is { } remote)
                {
                    MessageBox.Show(
                        $"A skin server is already running on {remote.Address}.\n\n" +
                        "This launcher will use that one. Starting a second server on the " +
                        "same network would make players pick between them at random, so " +
                        "only one machine should host it.",
                        "Skin server already running",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                StartSkinServer(forHosting: false);
            }
            finally { SkinServerToggle.IsEnabled = true; }
        }

        /// <summary>
        /// Looks for a skin server on another machine and records it if one answers.
        /// The probe blocks for up to a second and a half, so it runs off the UI thread.
        /// </summary>
        private async Task<SkinDiscovery.Found?> AdoptRemoteSkinServerAsync()
        {
            var remote = await Task.Run(SkinDiscovery.FindRemote);
            _adoptedSkinServer = remote?.Address;
            UpdateSkinServerUi();
            return remote;
        }

        private void StartSkinServer(bool forHosting)
        {
            if (_skinServer?.IsRunning == true) return;

            try
            {
                if (_skinServer is null)
                {
                    _skinServer = new SkinServer();
                    _skinServer.Log += message =>
                        Dispatcher.BeginInvoke(() => SkinServerDetail.Text = message);
                }
                _skinServer.Start();
                _skinServerForHosting = forHosting;
                _adoptedSkinServer = null;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not start the skin server.\n\n{ex.Message}\n\n" +
                    $"Port {SkinServer.DefaultPort} may already be in use.",
                    "Skin server", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            UpdateSkinServerUi();
        }

        private void StopSkinServer()
        {
            _skinServer?.Stop();
            _skinServerForHosting = false;
            UpdateSkinServerUi();
        }

        /// <summary>
        /// Reports not just whether the skin server is up but *why*, because the
        /// Server tab starts it automatically and people reasonably read an
        /// unexplained "running" as the launcher acting on its own.
        /// </summary>
        private void UpdateSkinServerUi()
        {
            if (SkinServerStatus is null) return;

            if (_skinServer?.IsRunning == true)
            {
                SkinServerStatus.Text =
                    $"Skin server: running on {_skinServer.HostIp}:{_skinServer.Port}" +
                    (_skinServerForHosting
                        ? " — started automatically for the Minecraft server you are hosting."
                        : " — started from this tab.");
                SkinServerToggle.Content = "Stop";
            }
            else if (_adoptedSkinServer is not null)
            {
                SkinServerStatus.Text =
                    $"Skin server: running on {_adoptedSkinServer} — hosted by another " +
                    "machine, and this launcher is using theirs.";
                SkinServerToggle.Content = "Start";
            }
            else
            {
                SkinServerStatus.Text = "Skin server: stopped";
                SkinServerToggle.Content = "Start";
            }

            UpdateShutdownButton();
        }

        // ── Launcher self-update ──
        /// <summary>
        /// Asks the LAN whether anyone is running a newer launcher. Silent unless the
        /// answer is yes: these machines are offline, so "no host found" is the normal
        /// case and is not worth telling anyone about.
        /// </summary>
        private async Task CheckForUpdateAsync()
        {
            try
            {
                // Hashing a 126 MB exe is real work, and the discovery probe blocks —
                // neither belongs on the UI thread.
                var check = await Task.Run(() => LauncherUpdate.CheckLanAsync(_config));
                if (check?.UpdateAvailable != true) return;

                _pendingUpdate = check;
                UpdateDetail.Text =
                    $"Version {check.RemoteVersion} is available from {check.HostAddress}. " +
                    $"You have {check.LocalVersion}.";
                UpdateCard.Visibility = Visibility.Visible;
            }
            catch
            {
                // A background check must never disturb startup. If it fails the card
                // simply stays hidden and everything else works as before.
            }
        }

        /// <summary>
        /// The same search as the background one, but it always says what it found.
        /// This exists because "I clicked it and nothing happened" is not something
        /// anyone can act on, least of all from another building.
        /// </summary>
        private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateButton.IsEnabled = false;
            UpdateCheckResult.Text = "Looking on the network...";

            try
            {
                var lookup = await Task.Run(() => LauncherUpdate.LookupAsync(_config));

                switch (lookup.Outcome)
                {
                    case UpdateOutcome.UpdateAvailable:
                        _pendingUpdate = lookup.Check;
                        UpdateDetail.Text =
                            $"Version {lookup.RemoteVersion} is available from {lookup.HostAddress}. " +
                            $"You have {lookup.LocalVersion}.";
                        UpdateCard.Visibility = Visibility.Visible;
                        UpdateCheckResult.Text = $"Update found on {lookup.HostAddress}.";
                        StopUpdateWatch();
                        break;

                    case UpdateOutcome.SameVersion:
                        UpdateCheckResult.Text =
                            $"{lookup.HostAddress} is running {lookup.RemoteVersion} too, " +
                            "so there is nothing to update to.";
                        break;

                    case UpdateOutcome.HostIsOlder:
                        UpdateCheckResult.Text =
                            $"{lookup.HostAddress} is running {lookup.RemoteVersion}, which is " +
                            $"older than this one ({lookup.LocalVersion}). That machine should " +
                            "be updated from this one instead.";
                        break;

                    case UpdateOutcome.HostCannotServe:
                        UpdateCheckResult.Text =
                            $"Found a skin server on {lookup.HostAddress}, but it cannot offer " +
                            "updates — that machine is on a launcher older than 1.1.0 and has to " +
                            "be updated by hand once.";
                        break;

                    default:
                        UpdateCheckResult.Text =
                            "No other launcher found. The host has to have their launcher open " +
                            "with its skin server running — that starts when they start a server, " +
                            "or from the Start button on the Skins tab.";
                        break;
                }
            }
            catch (Exception ex)
            {
                UpdateCheckResult.Text = "Could not check: " + ex.Message;
            }
            finally
            {
                CheckUpdateButton.IsEnabled = true;
            }
        }

        private async void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingUpdate is null) return;
            var check = _pendingUpdate;

            // No confirmation: updates are mandatory, and this button only brings
            // forward what the background watcher would do within a couple of minutes.
            UpdateButton.IsEnabled = false;
            try
            {
                var progress = new Progress<string>(m => UpdateDetail.Text = m);
                await LauncherUpdate.DownloadAsync(check, progress);

                // Claim the swap before anyone else can start one, then clear the way:
                // Windows will not replace a running exe, and the background watcher
                // and any game watcher are running from this same file.
                _applyingUpdate = true;
                UpdateDetail.Text = "Closing the other launcher processes…";

                UpdateEnforcement.AskEveryoneToExit();
                UpdateEnforcement.WaitForOthersToExit(TimeSpan.FromSeconds(20));

                // Everything is verified on disk before anything is replaced, so a
                // failure above leaves the installed launcher exactly as it was.
                LauncherUpdate.Apply();
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                LauncherUpdate.DiscardStaged();
                UpdateDetail.Text = "Update failed — nothing was changed.";
                UpdateButton.IsEnabled = true;

                MessageBox.Show(
                    $"The update could not be installed.\n\n{ex.Message}\n\n" +
                    "Nothing has been changed and the launcher still works as before.",
                    "Update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ── 3D preview ──
        /// <summary>
        /// Brings up the 3D preview if WebView2 is present. This method must not
        /// mention any WebView2 type — see <see cref="SkinPreviewHost"/> for why.
        /// </summary>
        private async Task EnsureWebViewAsync()
        {
            if (_previewAttempted) return;
            _previewAttempted = true;

            // The DLLs live in runtime\webview2\, not beside the exe. Checking here,
            // before anything can touch a WebView2 type, is what keeps a missing
            // runtime folder to a message in the preview pane instead of a crash.
            if (!WebView2Resolver.Available)
            {
                _webViewError = WebView2Resolver.Error;
                UpdatePreview(SkinsList?.SelectedItem as SkinEntry);
                return;
            }

            var host = new SkinPreviewHost();
            await host.InitializeAsync(SkinViewerHost);

            if (host.Ready)
            {
                _preview = host;
                var pending = _pendingPreview;
                _pendingPreview = null;
                UpdatePreview(pending ?? SkinsList?.SelectedItem as SkinEntry);
            }
            else
            {
                _webViewError = host.Error;
                UpdatePreview(SkinsList?.SelectedItem as SkinEntry);
            }
        }

        private void UpdatePreview(SkinEntry? skin)
        {
            if (SkinPreviewPlaceholder is null) return;

            if (skin is null || !File.Exists(skin.FullPath))
            {
                SkinViewerHost.Visibility = Visibility.Collapsed;
                SkinPreviewPlaceholder.Visibility = Visibility.Visible;
                SkinPreviewPlaceholder.Text = "Select a skin\nto preview";
                SkinBrowserButton.IsEnabled = false;
                return;
            }

            SkinBrowserButton.IsEnabled = SkinPreviewHtml.BundleAvailable;

            if (!SkinPreviewHtml.BundleAvailable)
            {
                ShowPreviewMessage($"skinview3d.bundle.js not found.\nExpected at:\n{SkinPreviewHtml.BundlePath}");
                return;
            }

            if (_webViewError is not null)
            {
                ShowPreviewMessage($"3D preview unavailable:\n{_webViewError}\n\nUse \"Open in Browser\" instead.");
                return;
            }

            if (_preview is null)
            {
                _pendingPreview = skin;

                // Already initializing: leave the visual tree alone. Collapsing the
                // container here would stall EnsureCoreWebView2Async forever.
                if (_previewAttempted) return;

                // Reveal the container before starting, for the same reason.
                SkinPreviewPlaceholder.Visibility = Visibility.Collapsed;
                SkinViewerHost.Visibility = Visibility.Visible;
                _ = EnsureWebViewAsync();
                return;
            }

            try
            {
                SkinPreviewPlaceholder.Visibility = Visibility.Collapsed;
                SkinViewerHost.Visibility = Visibility.Visible;

                if (!_preview.Navigate(SkinPreviewHtml.Build(skin.FullPath, skin.Model)))
                    ShowPreviewMessage($"Preview failed:\n{_preview.Error}");
            }
            catch (Exception ex)
            {
                ShowPreviewMessage($"Preview failed:\n{ex.Message}");
            }
        }

        private void ShowPreviewMessage(string message)
        {
            SkinViewerHost.Visibility = Visibility.Collapsed;
            SkinPreviewPlaceholder.Visibility = Visibility.Visible;
            SkinPreviewPlaceholder.Text = message;
        }

        // ── Setup tab ──
        private async Task LoadManifestAsync(bool forceRefresh)
        {
            SetupRefreshButton.IsEnabled = false;
            try
            {
                SetupLogLine("Fetching version manifest…");
                _manifest = await MojangManifest.FetchAsync(forceRefresh, CancellationToken.None);
                SetupLogLine($"{_manifest.Count} versions available.");
                ApplySetupFilter();
            }
            catch (Exception ex)
            {
                SetupLogLine("Could not load the manifest: " + ex.Message);
            }
            finally
            {
                SetupRefreshButton.IsEnabled = true;
            }
        }

        private async void SetupRefresh_Click(object sender, RoutedEventArgs e) =>
            await LoadManifestAsync(true);

        private void SetupFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplySetupFilter();
        private void SetupSearch_Changed(object sender, TextChangedEventArgs e) => ApplySetupFilter();

        private void ApplySetupFilter()
        {
            if (SetupList is null) return;

            string filter = SetupTypeCombo.SelectedItem as string ?? "Releases Only";
            string search = SetupSearchBox.Text.Trim();

            IEnumerable<MojangVersion> query = _manifest;

            if (filter == "Releases Only")
                query = query.Where(v => v.IsRelease);
            else if (filter == "Snapshots Only")
                query = query.Where(v => !v.IsRelease);

            if (search.Length > 0)
                query = query.Where(v => v.Id.Contains(search, StringComparison.OrdinalIgnoreCase));

            SetupList.ItemsSource = query.ToList();
        }

        private async void SetupList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            await LoadLoaderBuildsAsync();

        private async void SetupLoader_Changed(object sender, SelectionChangedEventArgs e) =>
            await LoadLoaderBuildsAsync();

        private string SelectedSetupLoader => SetupLoaderCombo?.SelectedItem as string ?? "None";

        /// <summary>Lists the builds of whichever loader is selected for the chosen version.</summary>
        private async Task LoadLoaderBuildsAsync()
        {
            if (SetupLoaderBuildCombo is null) return;

            string loader = SelectedSetupLoader;
            bool wanted = loader != "None";

            SetupLoaderBuildCombo.IsEnabled = wanted;
            SetupLoaderBuildCombo.ItemsSource = null;

            if (!wanted || SetupList.SelectedItem is not MojangVersion version) return;

            try
            {
                IReadOnlyList<string> builds = loader switch
                {
                    "Fabric" => await FabricMeta.LoaderVersionsAsync(version.Id, CancellationToken.None),
                    "Forge" => await ForgeMeta.VersionsAsync(ForgeFlavor.Forge, version.Id, CancellationToken.None),
                    "NeoForge" => await ForgeMeta.VersionsAsync(ForgeFlavor.NeoForge, version.Id, CancellationToken.None),
                    _ => Array.Empty<string>()
                };

                SetupLoaderBuildCombo.ItemsSource = builds;
                if (builds.Count > 0) SetupLoaderBuildCombo.SelectedIndex = 0;
                else SetupLogLine($"{loader} has no builds for {version.Id}.");
            }
            catch (Exception ex)
            {
                SetupLogLine($"Could not list {loader} builds: {ex.Message}");
            }
        }

        private async void SetupInstall_Click(object sender, RoutedEventArgs e)
        {
            if (SetupList.SelectedItem is not MojangVersion version)
            {
                SetupLogLine("Select a version to install.");
                return;
            }

            string loader = SelectedSetupLoader;
            string? loaderBuild = SetupLoaderBuildCombo.SelectedItem as string;

            if (loader != "None" && loaderBuild is null)
            {
                SetupLogLine($"Pick a {loader} build, or set the mod loader to None.");
                return;
            }

            _setupCts = new CancellationTokenSource();
            SetupInstallButton.IsEnabled = false;
            SetupCancelButton.IsEnabled = true;
            SetupProgress.Value = 0;

            var log = new Progress<string>(SetupLogLine);
            var percent = new Progress<double>(p => SetupProgress.Value = Math.Clamp(p, 0, 100));

            try
            {
                await VersionInstaller.InstallVanillaAsync(
                    version.Id, version.Url, log, percent, _setupCts.Token);

                // Forge and NeoForge run their own installer against the vanilla
                // install, so this has to come after it.
                switch (loader)
                {
                    case "Fabric":
                        await FabricMeta.InstallAsync(version.Id, loaderBuild!, log, _setupCts.Token);
                        break;
                    case "Forge":
                        await ForgeInstaller.InstallClientAsync(ForgeFlavor.Forge, version.Id,
                            loaderBuild!, Paths.VersionDir(version.Id), log, _setupCts.Token);
                        break;
                    case "NeoForge":
                        await ForgeInstaller.InstallClientAsync(ForgeFlavor.NeoForge, version.Id,
                            loaderBuild!, Paths.VersionDir(version.Id), log, _setupCts.Token);
                        break;
                }

                SetupProgress.Value = 100;
                SetupLogLine("Done.");
                RefreshInstalledVersions();
            }
            catch (OperationCanceledException)
            {
                SetupLogLine("Cancelled.");
            }
            catch (Exception ex)
            {
                SetupLogLine("Failed: " + ex.Message);
                MessageBox.Show(ex.Message, "Install failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetupInstallButton.IsEnabled = true;
                SetupCancelButton.IsEnabled = false;
                _setupCts.Dispose();
                _setupCts = null;
            }
        }

        private void SetupCancel_Click(object sender, RoutedEventArgs e)
        {
            _setupCts?.Cancel();
            SetupCancelButton.IsEnabled = false;
        }

        private void SetupLogLine(string message)
        {
            SetupLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            SetupLog.ScrollToEnd();
        }
    }
}
