using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// A running Minecraft server, with its console piped back to the launcher.
    /// </summary>
    /// <remarks>
    /// The server used to be launched through a .bat in its own window, which meant
    /// the launcher had no idea whether it was still running and no way to stop it
    /// cleanly. Redirecting the streams gives us both, and lets the Server tab show
    /// the console instead of leaving a stray window on the taskbar.
    ///
    /// Output arrives on background threads — marshal before touching UI.
    /// </remarks>
    public sealed class ServerSession : IDisposable
    {
        private readonly Process _process;
        private bool _disposed;

        public string ServerDir  { get; }
        public string Version    { get; }
        public string LoaderType { get; }
        public int    Port       { get; }

        /// <summary>Identifies which server this is, for "already running" checks.</summary>
        public string Key => $"{LoaderType}-{Version}";

        public bool IsRunning
        {
            get
            {
                try { return !_process.HasExited; }
                catch { return false; }
            }
        }

        /// <summary>One line of server console output.</summary>
        public event Action<string>? Output;

        /// <summary>Raised once the server process ends, however it ended.</summary>
        public event Action? Exited;

        internal ServerSession(Process process, string serverDir, string version, string loaderType, int port)
        {
            _process   = process;
            ServerDir  = serverDir;
            Version    = version;
            LoaderType = loaderType;
            Port       = port;

            _process.OutputDataReceived += OnData;
            _process.ErrorDataReceived  += OnData;
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) => Exited?.Invoke();

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        private void OnData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null) Output?.Invoke(e.Data);
        }

        /// <summary>
        /// Sends a line to the server console, exactly as typing it would. Returns
        /// false if the server has already gone.
        /// </summary>
        public bool SendCommand(string command)
        {
            if (!IsRunning) return false;

            try
            {
                _process.StandardInput.WriteLine(command);
                _process.StandardInput.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Asks the server to shut down and waits for it. Falls back to killing the
        /// process only if it ignores the request — a Minecraft server that is killed
        /// mid-save can leave a corrupted region file, so this is a last resort.
        /// </summary>
        public async Task<bool> StopAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            if (!IsRunning) return true;

            SendCommand("stop");

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                await _process.WaitForExitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                Kill();
                return false;
            }
        }

        /// <summary>Ends the process immediately, along with anything it started.</summary>
        public void Kill()
        {
            try { if (IsRunning) _process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _process.CancelOutputRead(); } catch { }
            try { _process.CancelErrorRead(); }  catch { }
            _process.Dispose();
        }
    }
}
