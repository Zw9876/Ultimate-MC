using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Drives the official Forge / NeoForge installer jars.
    /// </summary>
    /// <remarks>
    /// Both projects ship the same installer CLI, so one implementation covers
    /// them. Running the real installer is the only practical option: modern Forge
    /// builds the client by executing "processors" that binary-patch the vanilla
    /// jar, which a launcher cannot reasonably reimplement.
    /// </remarks>
    public static class ForgeInstaller
    {
        /// <summary>Installs a server into <paramref name="serverDir"/>.</summary>
        public static Task InstallServerAsync(
            ForgeFlavor flavor, string mcVersion, string loaderVersion, string serverDir,
            IProgress<string>? log, CancellationToken ct) =>
            RunAsync(flavor, mcVersion, loaderVersion, serverDir, "--installServer", log, ct);

        /// <summary>
        /// Installs a client into <paramref name="versionDir"/>, which already holds
        /// the vanilla install. The installer writes its profile JSON into
        /// <c>versions/</c> and its libraries into <c>libraries/</c> there, exactly
        /// where <see cref="ClientLauncher"/> looks for them.
        /// </summary>
        public static async Task InstallClientAsync(
            ForgeFlavor flavor, string mcVersion, string loaderVersion, string versionDir,
            IProgress<string>? log, CancellationToken ct)
        {
            // The installer refuses to run without this file and will not create it
            // itself; an empty profile set is enough.
            string profiles = Path.Combine(versionDir, "launcher_profiles.json");
            if (!File.Exists(profiles))
            {
                await File.WriteAllTextAsync(profiles,
                    """{"profiles":{},"settings":{},"version":3}""",
                    new UTF8Encoding(false), ct);
            }

            await RunAsync(flavor, mcVersion, loaderVersion, versionDir, "--installClient", log, ct);
            FlattenProfiles(versionDir, log);
        }

        /// <summary>
        /// Moves installer-written profiles from versions/&lt;id&gt;/&lt;id&gt;.json up to
        /// versions/&lt;id&gt;.json.
        /// </summary>
        /// <remarks>
        /// The installer follows the official launcher's layout, which nests each
        /// profile in its own folder. This project keeps profiles flat — that is
        /// where the Fabric install writes them and where VersionScanner and
        /// ClientLauncher look — so a nested profile would install fine and then be
        /// invisible, showing up as Vanilla in the loader list.
        /// </remarks>
        private static void FlattenProfiles(string versionDir, IProgress<string>? log)
        {
            string versionsDir = Path.Combine(versionDir, "versions");
            if (!Directory.Exists(versionsDir)) return;

            foreach (string sub in Directory.GetDirectories(versionsDir))
            {
                string id = Path.GetFileName(sub);
                string nested = Path.Combine(sub, id + ".json");

                // A folder with no profile in it is pure vendor leftovers — Forge
                // drops a second copy of the vanilla client jar this way. Nothing
                // reads nested folders in this layout, so it is dead weight.
                if (!File.Exists(nested))
                {
                    long bytes = Directory.GetFiles(sub, "*", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length);
                    try
                    {
                        Directory.Delete(sub, recursive: true);
                        log?.Report($"Removed leftover versions/{id}/ ({bytes / 1024 / 1024} MB).");
                    }
                    catch { }
                    continue;
                }

                // The installer also re-fetches the vanilla profile it inherits from.
                // We already have that, along with the client jar under our own name,
                // so a duplicate is discarded rather than moved up — moving it would
                // overwrite the existing profile and leave a second copy of a ~27 MB
                // jar behind.
                if (File.Exists(Path.Combine(versionsDir, id + ".json")))
                {
                    try { Directory.Delete(sub, recursive: true); } catch { }
                    continue;
                }

                File.Move(nested, Path.Combine(versionsDir, id + ".json"));

                // Carry across anything else the installer left beside it.
                foreach (string leftover in Directory.GetFiles(sub))
                {
                    string target = Path.Combine(versionsDir, Path.GetFileName(leftover));
                    if (!File.Exists(target)) File.Move(leftover, target);
                }

                try { Directory.Delete(sub, recursive: true); } catch { }
                log?.Report($"Profile {id}.json placed in versions/.");
            }
        }

        private static async Task RunAsync(
            ForgeFlavor flavor, string mcVersion, string loaderVersion, string targetDir,
            string mode, IProgress<string>? log, CancellationToken ct)
        {
            Directory.CreateDirectory(targetDir);

            string name = ForgeMeta.DisplayName(flavor);
            string installer = Path.Combine(targetDir, $"{name.ToLowerInvariant()}-installer.jar");

            log?.Report($"Downloading the {name} {loaderVersion} installer…");
            await Downloader.GetFileAsync(
                ForgeMeta.InstallerUrl(flavor, mcVersion, loaderVersion), installer, ct);

            // Use the same JVM the game version needs, so the installer never runs on
            // an older runtime than the files it is about to produce.
            string javaExe = Paths.FindJavaForMinecraft(mcVersion)
                ?? throw new FileNotFoundException(
                    "No bundled Java runtime found under runtime/ — cannot run the installer.");

            log?.Report($"Running the {name} installer — this can take a few minutes…");

            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                WorkingDirectory = targetDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-jar");
            psi.ArgumentList.Add(installer);
            psi.ArgumentList.Add(mode);

            // The target path is optional to the installer, and its default differs
            // per mode: --installServer uses the working directory, but
            // --installClient silently uses %APPDATA%\.minecraft. Always pass it, or
            // a client install reports success having written nothing here.
            psi.ArgumentList.Add(targetDir);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Could not start the {name} installer.");

            // The installer is chatty and slow; surfacing its output makes a failure
            // diagnosable instead of just an exit code.
            var tail = new System.Collections.Concurrent.ConcurrentQueue<string>();
            void Capture(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                tail.Enqueue(line);
                while (tail.Count > 40) tail.TryDequeue(out _);
            }

            process.OutputDataReceived += (_, e) => Capture(e.Data);
            process.ErrorDataReceived  += (_, e) => Capture(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                string output = string.Join(Environment.NewLine, tail);
                throw new InvalidOperationException(
                    $"The {name} installer exited with code {process.ExitCode}.\n\n{output}");
            }

            TryDelete(installer);
            TryDelete(installer + ".log");
            log?.Report($"{name} {loaderVersion} installed.");
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
