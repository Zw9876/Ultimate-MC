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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftLauncher.Core;

namespace MinecraftLauncher.UI
{
    /// <summary>
    /// Browsing Modrinth and installing mods straight into the folder the Mods tab is
    /// looking at, laid out like Modrinth's own explorer: a card per mod with its
    /// icon, description and tags, and a details panel for choosing a version.
    /// </summary>
    /// <remarks>
    /// These machines can reach Modrinth even though they cannot reach Mojang, so mods
    /// are obtainable on the machines themselves. The alternative was a USB stick.
    ///
    /// The target folder, game version and loader come from the Mods tab and are shown
    /// but not editable — a second place to choose them would be a way to install mods
    /// somewhere unexpected.
    /// </remarks>
    public partial class ModBrowserWindow : Window
    {
        private const int PageSize = 20;

        private readonly string _modsFolder;
        private readonly string _gameVersion;
        private readonly string _loaderType;

        /// <summary>Needed only to find the installed loader, which lives in a different place for servers.</summary>
        private readonly bool _server;

        private readonly List<ModrinthApi.Hit> _hits = new();
        private readonly Dictionary<string, Button> _cards = new();

        private CancellationTokenSource? _work;
        private ModrinthApi.Hit? _selected;
        private List<ModrinthApi.ModVersion> _versions = new();
        private int _total;

        /// <summary>True when anything was installed, so the caller knows to refresh.</summary>
        public bool DownloadedSomething { get; private set; }

        public ModBrowserWindow(string modsFolder, string gameVersion, string loaderType, bool server)
        {
            InitializeComponent();

            _modsFolder = modsFolder;
            _gameVersion = gameVersion;
            _loaderType = loaderType;
            _server = server;

            TargetLabel.Text =
                $"Installing into: {modsFolder}\n" +
                $"Filtered to {loaderType} · Minecraft {gameVersion} — " +
                "only mods that fit this combination are shown.";

            SortCombo.ItemsSource = ModrinthApi.SortOptions.Select(o => o.Label).ToList();
            SortCombo.SelectedIndex = 1;        // Downloads, which is how Modrinth opens

            VersionCombo.SelectionChanged += (_, _) => ShowVersionNote();

            Loaded += async (_, _) => await RunSearch(reset: true);
        }

        private string SortIndex =>
            ModrinthApi.SortOptions[Math.Max(0, SortCombo.SelectedIndex)].Index;

        // ── searching ──

        private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearch(reset: true);

        private async void Sort_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded) await RunSearch(reset: true);
        }

        private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await RunSearch(reset: true);
        }

        private async void More_Click(object sender, RoutedEventArgs e) => await RunSearch(reset: false);

        private async Task RunSearch(bool reset)
        {
            if (ModrinthApi.LoaderFacet(_loaderType) is null)
            {
                StatusLabel.Text =
                    $"{_loaderType} does not run mods, so there is nothing to search for.";
                return;
            }

            Busy(true, reset ? "Searching Modrinth..." : "Loading more...");

            try
            {
                _work?.Cancel();
                _work = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var ct = _work.Token;

                if (reset)
                {
                    _hits.Clear();
                    _cards.Clear();
                    ResultsPanel.Children.Clear();
                    ClearDetails();
                    ResultsScroller.ScrollToTop();
                }

                var page = await ModrinthApi.SearchAsync(
                    SearchBox.Text.Trim(), _gameVersion, _loaderType, PageSize, ct,
                    offset: _hits.Count, index: SortIndex);

                _total = page.TotalHits;

                foreach (var hit in page.Hits)
                {
                    _hits.Add(hit);
                    AddCard(hit, ct);
                }

                MoreButton.IsEnabled = page.HasMore;

                StatusLabel.Text = _hits.Count == 0
                    ? $"Nothing found for {_loaderType} on {_gameVersion}. Try a different search."
                    : $"Showing {_hits.Count} of {_total} that work with {_loaderType} on {_gameVersion}.";
            }
            catch (OperationCanceledException)
            {
                StatusLabel.Text = "Search cancelled.";
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "Could not reach Modrinth: " + ex.Message;
            }
            finally
            {
                Busy(false);
            }
        }

        // ── the cards ──

        /// <summary>
        /// Builds one result row in code rather than with a DataTemplate, because the
        /// icon has to be fetched and decoded per card and may fail, and a template
        /// binding cannot fall back to a letter tile on its own.
        /// </summary>
        private void AddCard(ModrinthApi.Hit hit, CancellationToken ct)
        {
            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Icon, with the project's own accent behind it until (or unless) it loads.
            var iconLetter = new TextBlock
            {
                Text = hit.Initial,
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var iconShell = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(8),
                Background = AccentOf(hit),
                VerticalAlignment = VerticalAlignment.Top,
                Child = iconLetter
            };

            Grid.SetColumn(iconShell, 0);
            layout.Children.Add(iconShell);

            var text = new StackPanel();
            Grid.SetColumn(text, 2);

            var heading = new StackPanel { Orientation = Orientation.Horizontal };
            heading.Children.Add(new TextBlock
            {
                Text = hit.Title,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("TextBrush")
            });
            heading.Children.Add(new TextBlock
            {
                Text = "  by " + hit.Author,
                FontSize = 12,
                Foreground = (Brush)FindResource("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 1)
            });
            text.Children.Add(heading);

            text.Children.Add(new TextBlock
            {
                Text = hit.Description,
                Foreground = (Brush)FindResource("TextBrush"),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 40,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4, 0, 6)
            });

            text.Children.Add(new TextBlock
            {
                Text = $"{hit.DownloadsText} downloads  ·  {hit.FollowsText} followers" +
                       (hit.UpdatedText.Length > 0 ? $"  ·  {hit.UpdatedText}" : ""),
                FontSize = 11,
                Foreground = (Brush)FindResource("MutedBrush"),
                Margin = new Thickness(0, 0, 0, 6)
            });

            // Category pills, the way Modrinth shows them.
            var pills = new WrapPanel();
            foreach (string tag in hit.CategoryList().Take(4))
                pills.Children.Add(Pill(tag, muted: false));
            if (hit.EnvironmentText.Length > 0)
                pills.Children.Add(Pill(hit.EnvironmentText, muted: true));

            if (pills.Children.Count > 0) text.Children.Add(pills);

            layout.Children.Add(text);

            var card = new Button
            {
                Style = (Style)FindResource("CardButton"),
                Content = layout
            };
            card.Click += (_, _) => Select(hit);

            _cards[hit.ProjectId] = card;
            ResultsPanel.Children.Add(card);

            // Fire and forget: an icon arriving late should not hold up the list, and
            // one that never arrives just leaves the letter tile showing.
            _ = LoadIconAsync(hit.IconUrl, iconShell, iconLetter, ct);
        }

        /// <summary>One category chip.</summary>
        private Border Pill(string text, bool muted) => new()
        {
            Style = (Style)FindResource("Tag"),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                Foreground = (Brush)FindResource(muted ? "MutedBrush" : "TextBrush")
            }
        };

        /// <summary>
        /// Fetches and decodes an icon, painting it as the tile's background so the
        /// rounded corners clip it.
        /// </summary>
        /// <remarks>
        /// Most Modrinth icons are WebP. Windows can usually decode that, but not on
        /// every build, and a mod with an unreadable icon must still be usable — so
        /// every failure simply leaves the letter tile showing.
        /// </remarks>
        private static async Task LoadIconAsync(
            string? url, Border shell, UIElement letter, CancellationToken ct)
        {
            try
            {
                byte[]? bytes = await ModrinthApi.IconAsync(url, ct);
                if (bytes is null || bytes.Length == 0) return;

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.DecodePixelWidth = 128;      // list icons are small; decode small
                image.EndInit();
                image.Freeze();

                shell.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                letter.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException)
            {
                // Window closed or a new search started. Nothing to do.
            }
            catch
            {
                // No decoder for this format, or a corrupt file. The letter stays.
            }
        }

        /// <summary>The project's own accent colour, dimmed to sit behind a dark card.</summary>
        private static Brush AccentOf(ModrinthApi.Hit hit)
        {
            if (hit.Color is not int rgb) return new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));

            byte r = (byte)((rgb >> 16) & 0xFF);
            byte g = (byte)((rgb >> 8) & 0xFF);
            byte b = (byte)(rgb & 0xFF);

            // Mix a third of the project colour into the card background so it reads as
            // a tint rather than a block of colour.
            return new SolidColorBrush(Color.FromRgb(
                (byte)(0x26 + (r - 0x26) / 3),
                (byte)(0x26 + (g - 0x26) / 3),
                (byte)(0x26 + (b - 0x26) / 3)));
        }

        // ── details ──

        private async void Select(ModrinthApi.Hit hit)
        {
            _selected = hit;

            foreach (var (id, card) in _cards)
                card.Tag = id == hit.ProjectId ? "selected" : null;

            DetailsPlaceholder.Visibility = Visibility.Collapsed;
            DetailsScroller.Visibility = Visibility.Visible;

            DetailTitle.Text = hit.Title;
            DetailAuthor.Text = "by " + hit.Author;
            DetailDescription.Text = hit.Description;

            var stats = new List<string> { $"{hit.DownloadsText} downloads", $"{hit.FollowsText} followers" };
            if (hit.UpdatedText.Length > 0) stats.Add(hit.UpdatedText);
            if (hit.EnvironmentText.Length > 0) stats.Add(hit.EnvironmentText);
            DetailStats.Text = string.Join("  ·  ", stats);

            DetailTags.Text = hit.CategoriesText;
            DetailIconLetter.Text = hit.Initial;
            DetailIconLetter.Visibility = Visibility.Visible;
            DetailIconShell.Background = AccentOf(hit);

            _work ??= new CancellationTokenSource();
            _ = LoadIconAsync(hit.IconUrl, DetailIconShell, DetailIconLetter, _work.Token);

            await LoadVersions(hit);
        }

        private async Task LoadVersions(ModrinthApi.Hit hit)
        {
            VersionCombo.ItemsSource = null;
            VersionNote.Text = "Looking up versions...";
            DownloadButton.IsEnabled = false;

            try
            {
                var ct = new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token;
                _versions = (await ModrinthApi.VersionsAsync(hit.Slug, _gameVersion, _loaderType, ct))
                            .Where(v => v.File is not null)
                            .ToList();

                if (_versions.Count == 0)
                {
                    VersionNote.Text =
                        $"No download for {_loaderType} on Minecraft {_gameVersion}.";
                    return;
                }

                VersionCombo.ItemsSource = _versions.Select(v => v.Describe()).ToList();

                // Default to what the Download button would have picked anyway.
                var best = ModrinthApi.BestOf(_versions);
                VersionCombo.SelectedIndex = best is null ? 0 : _versions.IndexOf(best);

                DownloadButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                VersionNote.Text = "Could not load versions: " + ex.Message;
            }
        }

        private void ShowVersionNote()
        {
            if (VersionCombo.SelectedIndex < 0 || VersionCombo.SelectedIndex >= _versions.Count)
            {
                VersionNote.Text = "";
                return;
            }

            var v = _versions[VersionCombo.SelectedIndex];
            int required = v.Dependencies.Count(d => d.Required);

            VersionNote.Text = $"{v.File!.FileName}  ({v.File.SizeText})" +
                               (required > 0
                                   ? $"\nNeeds {required} other mod{(required == 1 ? "" : "s")}, " +
                                     "which will be downloaded too."
                                   : "");
        }

        private void ClearDetails()
        {
            _selected = null;
            _versions = new List<ModrinthApi.ModVersion>();
            DetailsScroller.Visibility = Visibility.Collapsed;
            DetailsPlaceholder.Visibility = Visibility.Visible;
            DownloadButton.IsEnabled = false;
        }

        private void OpenPage_Click(object sender, RoutedEventArgs e)
        {
            if (_selected is null) return;

            // These machines can reach Modrinth, so the project page is genuinely useful
            // for the things a list cannot show: screenshots, changelogs, the full text.
            try
            {
                Process.Start(new ProcessStartInfo(
                    $"https://modrinth.com/mod/{_selected.Slug}") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "Could not open a browser: " + ex.Message;
            }
        }

        // ── downloading ──

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (_selected is null || VersionCombo.SelectedIndex < 0) return;
            if (VersionCombo.SelectedIndex >= _versions.Count) return;

            var chosen = _versions[VersionCombo.SelectedIndex];
            Busy(true, $"Working out what {_selected.Title} needs...");

            try
            {
                _work = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var ct = _work.Token;

                var set = await ModrinthApi.ResolveWithDependenciesAsync(
                    chosen, _gameVersion, _loaderType, ct);

                long total = set.Sum(v => v.File!.Size);
                string list = string.Join("\n", set.Select(v => $"  {v.File!.FileName}  ({v.File.SizeText})"));
                int extras = set.Count - 1;

                var answer = MessageBox.Show(
                    $"Install {_selected.Title} {chosen.VersionNumber}?\n\n{list}\n\n" +
                    (extras > 0
                        ? $"{extras} of those are required by it and will not work without each other.\n\n"
                        : "") +
                    $"{ModrinthApi.LoaderFacet(_loaderType)} · Minecraft {_gameVersion} · " +
                    $"{total / 1024.0 / 1024.0:0.#} MB in total.",
                    "Download from Modrinth", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (answer != MessageBoxResult.Yes)
                {
                    StatusLabel.Text = "Nothing downloaded.";
                    return;
                }

                int done = 0, skipped = 0, failed = 0;
                var replaced = new List<string>();

                foreach (var v in set)
                {
                    ct.ThrowIfCancellationRequested();
                    StatusLabel.Text = $"Downloading {v.File!.FileName} ({done + skipped + 1} of {set.Count})...";

                    var result = await ModrinthApi.InstallAsync(v.File, _modsFolder, ct);

                    switch (result.Status)
                    {
                        case ModrinthApi.InstallStatus.Installed:
                        case ModrinthApi.InstallStatus.Replaced:
                            done++;
                            DownloadedSomething = true;
                            replaced.AddRange(result.Superseded);
                            break;

                        case ModrinthApi.InstallStatus.AlreadyThere: skipped++; break;
                        default: failed++; break;
                    }
                }

                string report = $"Installed {done}.";
                if (skipped > 0) report += $" {skipped} already there.";
                if (failed > 0) report += $" {failed} failed to verify and were not kept.";

                // Replacing an older build is the normal case when updating a mod, and
                // saying nothing would leave the user wondering where it went.
                if (replaced.Count > 0)
                    report += $" Turned off the older {string.Join(", ", replaced)} " +
                              "(renamed, not deleted).";

                // Modrinth cannot tell us which loader *version* a mod needs — only the
                // jar knows — so the check happens here, once the file exists. Better
                // now than as a crash on the next launch.
                if (WhatNeedsANewerLoader(set) is string tooNew) report += "  " + tooNew;

                StatusLabel.Text = report;
            }
            catch (OperationCanceledException)
            {
                StatusLabel.Text = "Download cancelled.";
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "Download failed: " + ex.Message;
            }
            finally
            {
                Busy(false);
            }
        }

        /// <summary>
        /// Checks what was just installed against the loader version actually present,
        /// and says so plainly. Returns null when everything fits, or nothing could be
        /// determined.
        /// </summary>
        private string? WhatNeedsANewerLoader(IEnumerable<ModrinthApi.ModVersion> installed)
        {
            var loader = LoaderVersions.Detect(_gameVersion, _server, _loaderType);
            if (!loader.Known) return null;

            var problems = new List<string>();

            foreach (var v in installed)
            {
                string path = Path.Combine(_modsFolder, ModrinthApi.SafeName(v.File!.FileName));
                if (!File.Exists(path)) continue;

                var need = ModInspector.RequiredLoaderVersion(path);
                if (ModInspector.IsLoaderVersionOk(need, loader.Version) == false)
                    problems.Add($"{v.File.FileName} needs {_loaderType} {need!.Requirement}");
            }

            if (problems.Count == 0) return null;

            return $"WARNING: {loader.Describe()} is installed, but " +
                   string.Join("; ", problems) + ". Update the loader before playing.";
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _work?.Cancel();
            Close();
        }

        private void Busy(bool busy, string? message = null)
        {
            SearchButton.IsEnabled = !busy;
            SearchBox.IsEnabled = !busy;
            SortCombo.IsEnabled = !busy;
            MoreButton.IsEnabled = !busy && _hits.Count > 0 && _hits.Count < _total;
            DownloadButton.IsEnabled = !busy && _versions.Count > 0;

            Cursor = busy ? Cursors.Wait : null;
            if (message is not null) StatusLabel.Text = message;
        }
    }
}
