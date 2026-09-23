using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>The result of asking a host on the LAN what launcher build it runs.</summary>
    public sealed class UpdateCheck
    {
        public required string HostAddress    { get; init; }
        public required Version RemoteVersion { get; init; }
        public required Version LocalVersion  { get; init; }

        /// <summary>Files whose contents differ from ours, and so must be fetched.</summary>
        public required IReadOnlyList<PackageFile> Outdated { get; init; }

        /// <summary>Whether the host said it can send files compressed.</summary>
        public bool UseCompression { get; init; }

        public long TotalBytes => Outdated.Sum(f => f.Size);

        /// <summary>
        /// Only a strictly newer build is offered. Equal versions with differing files
        /// mean someone rebuilt without bumping the version — treating that as an update
        /// would leave two machines swapping binaries back and forth forever.
        /// </summary>
        public bool UpdateAvailable => RemoteVersion > LocalVersion && Outdated.Count > 0;
    }

    /// <summary>Why an update search ended the way it did.</summary>
    public enum UpdateOutcome
    {
        /// <summary>Nobody on the network answered the discovery broadcast. Usually
        /// means no host is running — their skin server starts with their server.</summary>
        NoHostFound,

        /// <summary>A host answered but has no update endpoints, so it is running a
        /// build from before this feature, or is not the distributable build.</summary>
        HostCannotServe,

        /// <summary>The host is on the same version. Nothing to do — and the most
        /// likely reason a rollout "does not work": everyone is already up to date.</summary>
        SameVersion,

        /// <summary>The host is running an older build than this machine.</summary>
        HostIsOlder,

        UpdateAvailable
    }

    /// <summary>The outcome of a search, in enough detail to explain to a person.</summary>
    public sealed class UpdateLookup
    {
        public required UpdateOutcome Outcome { get; init; }
        public required Version LocalVersion  { get; init; }
        public string? HostAddress    { get; init; }
        public Version? RemoteVersion { get; init; }

        /// <summary>Set whenever a host answered, whatever its version.</summary>
        public UpdateCheck? Check { get; init; }
    }

    /// <summary>
    /// Lets a launcher update itself from another machine on the LAN, over the skin
    /// server already running there. These machines have no internet, so the only
    /// alternative is carrying a 126 MB exe round on a USB stick by hand.
    /// </summary>
    /// <remarks>
    /// Trust model, stated plainly: the update comes from whichever machine answers the
    /// skin-server discovery broadcast. On an isolated LAN of machines you control that
    /// is fine, and it is the same trust already placed in the skin server. It would not
    /// be safe on a network with untrusted machines, since anyone answering the
    /// broadcast could offer an executable. Nothing is ever applied without the user
    /// agreeing, and the source address is always shown to them.
    /// </remarks>
    public static class LauncherUpdate
    {
        private const string StagingFolder = "update-staging";
        private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

        public static string StagingDir => Path.Combine(LauncherPackage.Dir, StagingFolder);

        /// <summary>
        /// Finds a host on the LAN and asks what it is running. Null when nobody answers
        /// or the host cannot serve updates. For anything that has to explain itself to
        /// a person, use <see cref="LookupAsync"/> instead.
        /// </summary>
        public static async Task<UpdateCheck?> CheckLanAsync(AppConfig config, CancellationToken ct = default)
        {
            var lookup = await LookupAsync(config, ct);
            return lookup.Outcome == UpdateOutcome.UpdateAvailable ? lookup.Check : null;
        }

        /// <summary>
        /// The same search, but reporting exactly what happened.
        /// </summary>
        /// <remarks>
        /// "Nothing happened" used to be indistinguishable from four very different
        /// situations: nobody hosting, a host too old to offer updates, a host on the
        /// same version, and a host on an older one. On machines nobody can look at
        /// remotely, that difference is the whole diagnosis.
        /// </remarks>
        public static async Task<UpdateLookup> LookupAsync(AppConfig config, CancellationToken ct = default)
        {
            Version local = LauncherPackage.CurrentVersion;

            string? host = config.SkinServer?.Trim();
            if (string.IsNullOrWhiteSpace(host))
                host = (await Task.Run(SkinDiscovery.FindRemote, ct))?.Address;

            if (string.IsNullOrWhiteSpace(host))
                return new UpdateLookup { Outcome = UpdateOutcome.NoHostFound, LocalVersion = local };

            var check = await CheckAsync(host!, ct);
            if (check is null)
                return new UpdateLookup
                {
                    Outcome = UpdateOutcome.HostCannotServe,
                    HostAddress = host,
                    LocalVersion = local
                };

            var outcome = check.UpdateAvailable            ? UpdateOutcome.UpdateAvailable
                        : check.RemoteVersion < local      ? UpdateOutcome.HostIsOlder
                                                           : UpdateOutcome.SameVersion;

            return new UpdateLookup
            {
                Outcome       = outcome,
                HostAddress   = host,
                RemoteVersion = check.RemoteVersion,
                LocalVersion  = local,
                Check         = check
            };
        }

        /// <summary>Asks one known host for its manifest and compares it with ours.</summary>
        public static async Task<UpdateCheck?> CheckAsync(string hostAddress, CancellationToken ct = default)
        {
            using var http = new HttpClient { Timeout = ManifestTimeout };

            string json;
            try
            {
                json = await http.GetStringAsync($"http://{hostAddress}/launcher/manifest", ct);
            }
            catch
            {
                // A host running an older build has no such endpoint. That is a normal
                // answer of "nothing to offer", not an error worth showing anyone.
                return null;
            }

            var manifest = LauncherPackage.FromJson(json);
            if (manifest is null || manifest.Files.Count == 0) return null;

            var local = LauncherPackage.LocalManifest()
                .Files.ToDictionary(f => f.Name, f => f.Sha256, StringComparer.OrdinalIgnoreCase);

            var outdated = manifest.Files
                .Where(f => LauncherPackage.IsPackageFileName(f.Name))
                .Where(f => !local.TryGetValue(f.Name, out string? mine) ||
                            !string.Equals(mine, f.Sha256, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return new UpdateCheck
            {
                HostAddress    = hostAddress,
                RemoteVersion  = manifest.ParsedVersion,
                LocalVersion   = LauncherPackage.CurrentVersion,
                Outdated       = outdated,
                UseCompression = manifest.SupportsGzip
            };
        }

        /// <summary>
        /// Downloads every outdated file into a staging folder, checking each against the
        /// hash the host advertised. Throws if anything fails to match, leaving the
        /// installed launcher untouched.
        /// </summary>
        public static async Task DownloadAsync(
            UpdateCheck check, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(StagingDir);

            using var http = new HttpClient { Timeout = DownloadTimeout };
            int index = 0;

            foreach (var file in check.Outdated)
            {
                index++;
                if (!LauncherPackage.IsPackageFileName(file.Name))
                    throw new InvalidOperationException($"Refusing to accept '{file.Name}'.");

                string target = Path.Combine(StagingDir, file.Name);
                progress?.Report(
                    $"Downloading {file.Name} ({file.Size / 1024.0 / 1024:N0} MB) — {index} of {check.Outdated.Count}");

                string url = $"http://{check.HostAddress}/launcher/file/{Uri.EscapeDataString(file.Name)}";
                if (check.UseCompression) url += "?gzip=1";

                string actual;
                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();

                    await using var network = await response.Content.ReadAsStreamAsync(ct);
                    bool packed = response.Content.Headers.ContentEncoding.Contains("gzip");
                    await using Stream source = packed
                        ? new GZipStream(network, CompressionMode.Decompress)
                        : network;

                    await using var destination = new FileStream(
                        target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

                    // Hashed as it is written. Checking afterwards meant reading all
                    // 126 MB back off the disk for no reason.
                    actual = await CopyAndHashAsync(source, destination, ct);
                }

                progress?.Report($"Checking {file.Name}...");
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(target);
                    throw new InvalidDataException(
                        $"{file.Name} did not match the host's checksum — the download was " +
                        "incomplete or altered. Nothing has been changed.");
                }

                if (file.Name.Equals(LauncherPackage.ExeName, StringComparison.OrdinalIgnoreCase) &&
                    !LooksLikeWindowsProgram(target))
                {
                    TryDelete(target);
                    throw new InvalidDataException(
                        $"{file.Name} is not a Windows program — refusing to install it.");
                }
            }

            progress?.Report("Download complete.");
        }

        /// <summary>
        /// Hands the swap to a small script and returns. Windows will not let a running
        /// program overwrite itself, so the script waits for this process to exit, copies
        /// the staged files in, and starts the new launcher.
        /// </summary>
        /// <remarks>The caller must shut the application down immediately afterwards.</remarks>
        public static void Apply()
        {
            if (!Directory.Exists(StagingDir) || !Directory.EnumerateFiles(StagingDir).Any())
                throw new InvalidOperationException("Nothing has been downloaded to install.");

            // A game watcher is a running copy of this exe, and Windows will not let the
            // file be replaced while it is open. Someone reopening the launcher mid-game
            // to take an update is exactly when one exists, so ask them to leave first.
            // The swap script already retries the copy for long enough to cover it.
            SessionShutdown.SignalWatchersToExit();

            // Beside the launcher under a fixed name, rather than a random one in
            // %TEMP%. The work is identical; the shape is not. A hidden cmd.exe running
            // a GUID-named .bat out of the temp folder, which then overwrites an
            // executable and relaunches it, is what a dropper looks like, and antivirus
            // heuristics score it as one — this launcher is unsigned, so it has no
            // reputation to argue with. In the install folder under its own name it is
            // also readable after a failed update, like update-watcher.log.
            string script = BuildSwapScript();
            string scriptPath = Path.Combine(LauncherPackage.Dir, SwapScriptName);

            try
            {
                File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An install folder that cannot be written to is not worth failing an
                // update over — the script only has to run from somewhere.
                scriptPath = Path.Combine(Path.GetTempPath(), SwapScriptName);
                File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
            }

            Process.Start(new ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/c \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow  = true
            });
        }

        /// <summary>
        /// The swap script's filename. Fixed rather than unique: one is only ever
        /// running at a time, it deletes itself when it finishes, and a leftover from a
        /// failed update is overwritten by the next one rather than accumulating.
        /// </summary>
        internal const string SwapScriptName = "update-swap.cmd";

        /// <summary>
        /// Copies one stream into another, returning the SHA-256 of what went through.
        /// </summary>
        private static async Task<string> CopyAndHashAsync(
            Stream source, Stream destination, CancellationToken ct)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1 << 16];

            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }

        /// <summary>Throws away a staged update without installing it.</summary>
        public static void DiscardStaged()
        {
            try { if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, true); }
            catch { /* only a cache — a leftover folder is harmless */ }
        }

        internal static string BuildSwapScript() =>
            BuildSwapScript(Environment.ProcessId, LauncherPackage.Dir, StagingDir);

        /// <summary>
        /// The swap itself, parameterised so it can be exercised against throwaway
        /// folders. This script is the one part of the update that runs after the
        /// launcher is gone and cannot report a failure to anyone, so it is worth
        /// being able to test for real.
        /// </summary>
        internal static string BuildSwapScript(int pid, string targetDir, string stagingDir)
        {
            string target  = targetDir.TrimEnd('\\');
            string staging = stagingDir.TrimEnd('\\');
            string exe     = Path.Combine(target, LauncherPackage.ExeName);

            // move, not copy: staging lives inside the install folder, so this is a
            // rename on the same volume rather than shifting 126 MB again.
            //
            // ping stands in for sleep: timeout.exe fails when stdin is redirected,
            // which it is here, because the script runs with no console of its own.
            //
            // Normalised to CRLF at the end: the literal below inherits whatever
            // endings this source file happens to have, and .bat is conventionally
            // CRLF. Measured as working either way, so this is belt-and-braces
            // rather than a fix for an observed failure.
            string script = $"""
                @echo off
                setlocal

                :waitloop
                tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul
                if not errorlevel 1 (
                  ping -n 2 127.0.0.1 >nul
                  goto waitloop
                )

                set /a TRIES=0
                :copyloop
                set /a TRIES+=1
                move /Y "{staging}\*" "{target}\" >nul
                if errorlevel 1 (
                  if %TRIES% LSS 15 (
                    ping -n 2 127.0.0.1 >nul
                    goto copyloop
                  )
                )

                start "" "{exe}"
                rmdir /S /Q "{staging}"
                del "%~f0"
                """;

            return script.ReplaceLineEndings("\r\n");
        }

        /// <summary>
        /// Cheap sanity check that we were handed a program rather than, say, an error
        /// page. The checksum already proves the bytes arrived intact; this catches a
        /// host that advertised something odd to begin with.
        /// </summary>
        private static bool LooksLikeWindowsProgram(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
            }
            catch { return false; }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }
    }
}
