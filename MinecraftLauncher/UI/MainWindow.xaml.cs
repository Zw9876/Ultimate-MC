using System;
using System.Windows;
using System.Windows.Controls;
using MinecraftLauncher.Core;

namespace MinecraftLauncher.UI
{
    public partial class MainWindow : Window
    {
        private AppConfig _config = new();

        public MainWindow()
        {
            InitializeComponent();
            LoadEverything();
        }

        private void LoadEverything()
        {
            _config = AppConfig.Load();

            // Populate versions (both tabs share the installed list)
            var versions = VersionScanner.InstalledVersions();
            VersionCombo.ItemsSource = versions;
            SvVersionCombo.ItemsSource = versions;

            // Server type + option combos
            SvTypeCombo.ItemsSource = new[] { "Vanilla", "Fabric", "Forge", "Paper", "Purpur" };
            SvTypeCombo.SelectedIndex = 0;
            SvGamemodeCombo.ItemsSource = new[] { "survival", "creative", "adventure", "spectator" };
            SvGamemodeCombo.SelectedIndex = 0;
            SvDifficultyCombo.ItemsSource = new[] { "peaceful", "easy", "normal", "hard" };
            SvDifficultyCombo.SelectedIndex = 2;

            // Restore client settings
            UsernameBox.Text = _config.Username;
            MemSlider.Value = Math.Clamp(_config.Memory, 1, 16);
            MemLabel.Text = $"{(int)MemSlider.Value} GB";

            if (!string.IsNullOrEmpty(_config.LastVersion) && versions.Contains(_config.LastVersion))
                VersionCombo.SelectedItem = _config.LastVersion;
            else if (versions.Count > 0)
                VersionCombo.SelectedIndex = 0;

            if (versions.Count > 0 && SvVersionCombo.SelectedIndex < 0)
                SvVersionCombo.SelectedIndex = 0;
        }

        // ── Navigation ──
        private void NavClient_Checked(object sender, RoutedEventArgs e)
        {
            if (ClientPanel == null) return;
            ClientPanel.Visibility = Visibility.Visible;
            ServerPanel.Visibility = Visibility.Collapsed;
        }

        private void NavServer_Checked(object sender, RoutedEventArgs e)
        {
            if (ServerPanel == null) return;
            ClientPanel.Visibility = Visibility.Collapsed;
            ServerPanel.Visibility = Visibility.Visible;
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
        }

        private void MemSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MemLabel != null) MemLabel.Text = $"{(int)e.NewValue} GB";
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

            // Persist settings
            _config.Username = username;
            _config.Memory = memory;
            _config.LastVersion = version;
            _config.LastLoader = loader;
            _config.Save();

            try
            {
                ClientStatus.Text = "Launching…";
                ClientLauncher.Launch(version, loader, username, memory);
                ClientStatus.Text = "Game launched.";
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

        private void StartServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (SvVersionCombo.SelectedItem is not string version)
            {
                ServerStatus.Text = "Select a version first.";
                return;
            }
            string loaderType = (SvTypeCombo.SelectedItem as string ?? "Vanilla").ToUpperInvariant();

            if (!int.TryParse(SvPortBox.Text.Trim(), out int port)) port = 25565;
            if (!int.TryParse(SvMaxBox.Text.Trim(), out int maxPlayers)) maxPlayers = 10;

            var settings = new ServerSettings
            {
                Port = port,
                Gamemode = SvGamemodeCombo.SelectedItem as string ?? "survival",
                Difficulty = SvDifficultyCombo.SelectedItem as string ?? "normal",
                MaxPlayers = maxPlayers,
                Pvp = SvPvpCheck.IsChecked == true,
                Memory = (int)SvMemSlider.Value
            };

            try
            {
                ServerStatus.Text = "Starting server…";
                var result = ServerLauncher.Launch(version, loaderType, settings);
                ServerStatus.Text = $"Server started on port {result.Port}.\nFolder: {result.ServerDir}";
            }
            catch (Exception ex)
            {
                ServerStatus.Text = "Error: " + ex.Message;
                MessageBox.Show(ex.Message, "Server failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
