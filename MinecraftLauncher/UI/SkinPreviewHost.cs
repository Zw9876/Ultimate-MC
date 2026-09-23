using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Controls;
using MinecraftLauncher.Core;

namespace MinecraftLauncher.UI
{
    /// <summary>
    /// Owns the WebView2 control used for the 3D skin preview.
    /// </summary>
    /// <remarks>
    /// Every reference to a WebView2 type in this project lives in this class, and
    /// each entry point is <see cref="MethodImplOptions.NoInlining"/>. That is
    /// load-bearing, not stylistic: the WebView2 assemblies live in
    /// <c>runtime\webview2\</c> and may be absent, and the JIT resolves the types a
    /// method mentions *before* running any of its statements. A availability check
    /// sitting in the same method as a WebView2 reference never executes — it throws
    /// <c>FileNotFoundException</c> first. Callers must check
    /// <see cref="WebView2Resolver.Available"/> before constructing this class.
    /// </remarks>
    internal sealed class SkinPreviewHost
    {
        private Microsoft.Web.WebView2.Wpf.WebView2? _view;

        public bool Ready { get; private set; }
        public string? Error { get; private set; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public async Task InitializeAsync(ContentControl container)
        {
            try
            {
                string fixedRuntime = FindFixedRuntime();

                // The Fixed Version runtime needs AppContainer read access on
                // Windows 10; harmless on Windows 11.
                await Task.Run(() => GrantAppContainerAccess(fixedRuntime));

                // Without an explicit folder WebView2 creates
                // "<exe name>.WebView2" beside the executable, which litters the
                // deployment root. Keep its cache with the rest of the WebView2
                // files instead.
                string userData = Path.Combine(WebView2Resolver.Directory, "userdata");
                Directory.CreateDirectory(userData);

                var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
                    .CreateAsync(fixedRuntime, userData, null);

                var view = new Microsoft.Web.WebView2.Wpf.WebView2
                {
                    // Matches the preview panel so there is no white flash before
                    // the first page paints.
                    DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 16, 16, 16)
                };
                container.Content = view;

                // The container must already be visible: EnsureCoreWebView2Async
                // only completes once the control is realized in the visual tree,
                // so awaiting it while collapsed hangs forever.
                await view.EnsureCoreWebView2Async(environment);

                _view = view;
                Ready = true;
            }
            catch (Exception ex)
            {
                // WebView2 takes an exclusive lock on its user data folder, so a
                // second launcher instance lands here. Say that plainly rather than
                // showing a raw COM error.
                Error = ex.Message.Contains("user data folder", StringComparison.OrdinalIgnoreCase)
                    ? "Another copy of the launcher is already using the 3D preview.\n" +
                      "Close it, or use \"Open in Browser\"."
                    : ex.Message;
                Ready = false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Navigate(string html)
        {
            if (!Ready || _view is null) return false;

            try
            {
                _view.NavigateToString(html);
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                return false;
            }
        }

        private static string FindFixedRuntime()
        {
            string baseDir = Path.Combine(Paths.Runtime, "webview2runtime");
            if (!Directory.Exists(baseDir))
                throw new DirectoryNotFoundException($"WebView2 runtime folder not found: {baseDir}");

            string? exe = Directory
                .EnumerateFiles(baseDir, "msedgewebview2.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            return exe is not null ? Path.GetDirectoryName(exe)! : baseDir;
        }

        private static void GrantAppContainerAccess(string path)
        {
            foreach (string sid in new[] { "*S-1-15-2-2", "*S-1-15-2-1" })
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = "icacls",
                        Arguments = $"\"{path}\" /grant {sid}:(OI)(CI)(RX)",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    process?.WaitForExit(10_000);
                }
                catch { }
            }
        }
    }
}
