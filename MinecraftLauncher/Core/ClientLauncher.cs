using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Launches the Minecraft client. Faithful port of Start-MinecraftGame:
    /// resolves the version JSON, merges + de-duplicates libraries keeping the
    /// newest of each, builds a relative-path classpath, attaches
    /// authlib-injector when present, and starts java.exe hidden.
    /// </summary>
    /// <summary>Everything needed to start the game, before anything is started.</summary>
    public sealed record ClientLaunchPlan(
        string JavaExe,
        string Arguments,
        string WorkingDirectory,
        string LoaderType,
        string MainClass,
        int ClasspathEntries,
        string? SkinServerAddress = null);

    /// <summary>A started game, and the skin server it was pointed at (if any).</summary>
    public sealed record ClientLaunchResult(Process Game, string? SkinServerAddress);

    public static class ClientLauncher
    {
        public static ClientLaunchResult Launch(string version, string loaderType, string username, int memory)
        {
            var plan = BuildPlan(version, loaderType, username, memory);

            var game = Process.Start(new ProcessStartInfo
            {
                FileName = plan.JavaExe,
                Arguments = plan.Arguments,
                WorkingDirectory = plan.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("The game process could not be started.");

            return new ClientLaunchResult(game, plan.SkinServerAddress);
        }

        /// <summary>
        /// Builds the launch command without running it, so it can be inspected or
        /// run with output captured when the game fails to start.
        /// </summary>
        public static ClientLaunchPlan BuildPlan(
            string version, string loaderType, string username, int memory)
        {
            string versionDir = Paths.VersionDir(version);
            string libsDir    = Path.Combine(versionDir, "libraries");
            string natives    = Path.Combine(versionDir, "natives");
            string gameDir    = versionDir;

            string uuid = ComputerId.Get();

            // ── Resolve the version JSON (loader-specific, else vanilla) ──
            string? versionJson = null;
            string versionsSub = Path.Combine(versionDir, "versions");

            if (Directory.Exists(versionsSub))
            {
                versionJson = loaderType switch
                {
                    "FABRIC" => Directory.GetFiles(versionsSub, "fabric-loader*.json").FirstOrDefault(),
                    // Profile names vary by loader and installer version, so match on
                    // content rather than a prefix — see VersionScanner.
                    "NEOFORGE" => Directory.GetFiles(versionsSub, "*.json")
                        .FirstOrDefault(VersionScanner.IsNeoForgeProfile),
                    "FORGE" => Directory.GetFiles(versionsSub, "*.json")
                        .FirstOrDefault(VersionScanner.IsForgeProfile),
                    _ => null
                };
            }

            if (versionJson == null || !File.Exists(versionJson))
            {
                versionJson = Path.Combine(versionsSub, $"{version}.json");
                loaderType = "VANILLA";
            }
            if (!File.Exists(versionJson))
                throw new FileNotFoundException($"Version JSON not found: {versionJson}");

            using var doc = JsonDocument.Parse(File.ReadAllText(versionJson));
            var root = doc.RootElement;

            // ── Java version ──
            // Loader profiles usually omit javaVersion, inheriting it from the
            // vanilla profile. Falling back to a fixed "newest" here picked Java 25
            // for Minecraft 1.20.1, which wants 17; derive it from the game version
            // instead and let the profile override when it does declare one.
            int javaVersion = Paths.JavaMajorForMinecraft(version);
            if (root.TryGetProperty("javaVersion", out var jv) &&
                jv.TryGetProperty("majorVersion", out var mj))
                javaVersion = mj.GetInt32();

            string? javaExe = Paths.FindJava(javaVersion);
            if (javaExe == null)
                throw new FileNotFoundException($"Java {javaVersion} not found under runtime/.");

            string mainClass = root.GetProperty("mainClass").GetString() ?? "";

            // ── Asset index ──
            string assetIndex = version;
            string idxDir = Path.Combine(versionDir, "assets", "indexes");
            if (Directory.Exists(idxDir))
            {
                var idx = Directory.GetFiles(idxDir, "*.json").FirstOrDefault();
                if (idx != null) assetIndex = Path.GetFileNameWithoutExtension(idx);
            }

            // ── Gather library coordinates ──
            var allLibs = new List<string>();

            void AddLibsFrom(JsonElement el)
            {
                if (el.TryGetProperty("libraries", out var libs) &&
                    libs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lib in libs.EnumerateArray())
                        if (lib.TryGetProperty("name", out var nm) && nm.GetString() is string s)
                            allLibs.Add(s);
                }
            }

            if (loaderType is "FABRIC" or "FORGE" or "NEOFORGE")
            {
                AddLibsFrom(root); // loader libs first

                string vanillaJson = Path.Combine(versionsSub, $"{version}.json");
                if (File.Exists(vanillaJson))
                {
                    using var vdoc = JsonDocument.Parse(File.ReadAllText(vanillaJson));
                    AddLibsFrom(vdoc.RootElement);
                }
            }
            else
            {
                AddLibsFrom(root);
            }

            // Keep insertion order, remove exact-duplicate coordinates
            allLibs = allLibs.Distinct().ToList();

            // ── Keep only the newest version of each group:artifact[:classifier] ──
            var latest = new Dictionary<string, string>();
            foreach (var lib in allLibs)
            {
                var parts = lib.Split(':');
                if (parts.Length < 3) continue;

                string key = $"{parts[0]}:{parts[1]}";
                if (parts.Length == 4) key += $":{parts[3]}";
                string ver = parts[2];

                if (!latest.TryGetValue(key, out var existing) ||
                    MavenVersion.Compare(ver, existing) > 0)
                    latest[key] = ver;
            }

            var libraries = new List<string>();
            foreach (var kv in latest)
            {
                var kp = kv.Key.Split(':');
                string group = kp[0], artifact = kp[1];
                string? classifier = kp.Length == 3 ? kp[2] : null;
                string ver = kv.Value;
                libraries.Add(classifier != null
                    ? $"{group}:{artifact}:{ver}:{classifier}"
                    : $"{group}:{artifact}:{ver}");
            }

            // ── Build classpath with RELATIVE paths (verify each jar exists) ──
            var cp = new List<string>();
            foreach (var lib in libraries)
            {
                var parts = lib.Split(':');
                if (parts.Length < 3) continue;

                string group = parts[0], artifact = parts[1], libVer = parts[2];
                string? classifier = parts.Length == 4 ? parts[3] : null;
                string groupPath = group.Replace('.', '\\');
                string jarName = classifier != null
                    ? $"{artifact}-{libVer}-{classifier}.jar"
                    : $"{artifact}-{libVer}.jar";

                string fullPath = Path.Combine(libsDir, groupPath, artifact, libVer, jarName);
                string relPath  = $"versions\\{version}\\libraries\\{groupPath}\\{artifact}\\{libVer}\\{jarName}";

                if (File.Exists(fullPath))
                    cp.Add(relPath);
            }

            // ── Client jar (not for Forge) ──
            if (loaderType is not ("FORGE" or "NEOFORGE"))
            {
                string clientJar = Path.Combine(versionDir, "versions", $"{version}-client.jar");
                if (File.Exists(clientJar))
                    cp.Add($"versions\\{version}\\versions\\{version}-client.jar");
                else
                {
                    string plainJar = Path.Combine(versionDir, "versions", $"{version}.jar");
                    if (!File.Exists(plainJar))
                        throw new FileNotFoundException(
                            $"Client JAR not found: {version}-client.jar or {version}.jar in {Path.Combine(versionDir, "versions")}");
                    cp.Add($"versions\\{version}\\versions\\{version}.jar");
                }
            }

            // ── JVM args ──
            //
            // -Xmn128M used to be here, carried over from the PowerShell launcher. It
            // was actively harmful: a fixed young generation overrides G1's adaptive
            // sizing, so the collector can no longer meet its pause target, and 128 MB
            // is far too small for a modded client — it forces constant young
            // collections. Removing it is half the reason this feels smoother.
            //
            // -Xms matches -Xmx so the heap never grows mid-game, which is a stall of
            // its own.
            var javaArgs = new List<string>
            {
                $"-Djava.library.path=\"{natives}\"",
                $"-Xmx{memory}G",
                $"-Xms{memory}G",
                $"-Dorg.lwjgl.librarypath=\"{natives}\""
            };

            javaArgs.AddRange(JvmTuning.ClientGcFlags(memory));

            // authlib-injector (skins) — discovery + optional upload
            string? skinServer = null;
            string aliJar = Path.Combine(Paths.Runtime, "authlib-injector", "authlib-injector.jar");
            if (File.Exists(aliJar))
            {
                var cfg = AppConfig.Load();
                string? addr = SkinDiscovery.Resolve(cfg);
                skinServer = addr;
                if (addr != null)
                {
                    javaArgs.Insert(0, $"-javaagent:\"{aliJar}\"=http://{addr}");
                    // Best-effort push of this player's skin to the host.
                    string model = SkinStore.GetModel(username);
                    SkinDiscovery.UploadSkin(addr, username, model);
                }
            }

            // Forge-only JVM args from JSON
            if (loaderType is "FORGE" or "NEOFORGE" &&
                root.TryGetProperty("arguments", out var fargs) &&
                fargs.TryGetProperty("jvm", out var fjvm) &&
                fjvm.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in fjvm.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.String) continue;
                    string processed = a.GetString()!
                        .Replace("${library_directory}", Path.Combine(versionDir, "libraries"))
                        .Replace("${classpath_separator}", ";")
                        .Replace("${version_name}", version);
                    javaArgs.Add(processed);
                }
            }

            // Forge and NeoForge load part of their stack from the module path (-p).
            // A jar listed there must not also appear on the classpath, or
            // BootstrapLauncher aborts with "Module named ... was already on the
            // JVMs module path but class-path contains it" and the game never opens.
            var modulePathJars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < javaArgs.Count - 1; i++)
            {
                if (javaArgs[i] is not ("-p" or "--module-path")) continue;

                foreach (string entry in javaArgs[i + 1]
                             .Split(';', StringSplitOptions.RemoveEmptyEntries))
                    modulePathJars.Add(Path.GetFileName(entry.Trim().Trim('"')));
            }

            if (modulePathJars.Count > 0)
                cp.RemoveAll(entry => modulePathJars.Contains(Path.GetFileName(entry)));

            string classpath = string.Join(";", cp);

            javaArgs.Add("-cp");
            javaArgs.Add($"\"{classpath}\"");
            javaArgs.Add(mainClass);

            // ── Game args ──
            var gameArgs = new List<string>
            {
                "--username", username,
                "--version", version,
                "--gameDir", $"\"{gameDir}\"",
                "--assetsDir", $"\"{Path.Combine(versionDir, "assets")}\"",
                "--assetIndex", assetIndex,
                "--uuid", uuid,
                "--accessToken", "0",
                "--userType", "legacy"
            };

            if (loaderType is "FORGE" or "NEOFORGE" &&
                root.TryGetProperty("arguments", out var gargs) &&
                gargs.TryGetProperty("game", out var ggame) &&
                ggame.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in ggame.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.String)
                        gameArgs.Add(a.GetString()!);
            }

            string argsString = string.Join(" ", javaArgs.Concat(gameArgs));

            // Working dir is BaseDir so the relative classpath entries resolve.
            return new ClientLaunchPlan(
                javaExe, argsString, Paths.BaseDir, loaderType, mainClass, cp.Count, skinServer);
        }
    }
}
