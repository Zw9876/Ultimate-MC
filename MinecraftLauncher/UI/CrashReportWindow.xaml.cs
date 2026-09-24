using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using MinecraftLauncher.Core;

namespace MinecraftLauncher.UI
{
    /// <summary>
    /// Minecraft's crash reports for one version, with the answer pulled out of them.
    /// </summary>
    /// <remarks>
    /// The reports have always been on disk; nobody was ever going to open one. They
    /// are a hundred lines of stack trace, and the sentence that matters — usually
    /// "this mod needs that one, and that one is not installed" — is somewhere in the
    /// middle of it. This lists them newest first, says what each one means, and keeps
    /// the full text underneath for when the summary is not enough.
    ///
    /// Read-only. Deleting a crash report is a decision someone can make in the folder,
    /// which is one button away, rather than something a viewer offers to do for them.
    /// </remarks>
    public partial class CrashReportWindow : Window
    {
        private readonly string _version;
        private readonly List<CrashReports.Report> _reports;

        public CrashReportWindow(string version)
        {
            InitializeComponent();

            _version = version;
            _reports = CrashReports.List(version);

            ReportsList.ItemsSource = _reports;

            if (_reports.Count == 0)
            {
                IntroLabel.Text =
                    $"Minecraft {version} has not crashed on this computer — there are no reports in " +
                    $"{CrashReports.FolderFor(version)}.";
                SummaryBox.Text = "Nothing to show.";
                CopyButton.IsEnabled = false;
                return;
            }

            IntroLabel.Text =
                $"{_reports.Count} crash report(s) for Minecraft {version}, newest first. " +
                "Each one is the game explaining why it stopped.";

            ReportsList.SelectedIndex = 0;
        }

        private void Report_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ReportsList.SelectedItem is not CrashReports.Report report)
            {
                SummaryBox.Text = "";
                FullBox.Text = "";
                return;
            }

            SummaryBox.Text = report.Explain();
            StatusLabel.Text = $"{report.FileName} · {report.SizeText}";

            try
            {
                FullBox.Text = File.ReadAllText(report.FullPath);
            }
            catch (Exception ex)
            {
                // The summary was parsed when the window opened, so it survives a file
                // that has since been moved or locked; only the full text is lost.
                FullBox.Text = "Could not read the file: " + ex.Message;
            }

            CopyButton.IsEnabled = true;
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder = CrashReports.FolderFor(_version);

            if (!Directory.Exists(folder))
            {
                MessageBox.Show($"There is no crash-reports folder yet:\n\n{folder}",
                    "Crash reports", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (FullBox.Text.Length == 0) return;

            try
            {
                Clipboard.SetText(FullBox.Text);
                StatusLabel.Text = "Copied the whole report to the clipboard.";
            }
            catch (Exception)
            {
                // Another process can hold the clipboard open; it is not worth a dialog.
                StatusLabel.Text = "Windows would not let go of the clipboard. Try again.";
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
