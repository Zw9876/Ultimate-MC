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
    public static class ClientLauncher
    {
        public static void Launch(string version, string loaderType, string username, int memory)
        {
            string versionDir = Paths.VersionDir(version);
            string libsDir    = Path.Combine(versionDir, "libraries");
            string natives    = Path.Combine(versionDir, "natives");
            string gameDir    = versionDir;

            string uuid = ComputerId.Get();

            // ── Resolve the version JSON (loader-specific, else vanilla) ──
            string? versionJson = null;
            string versionsSub = Path.Combine(versionDir, "versions");

            if (loaderType == "FABRIC")
                versionJson = Directory.Exists(versionsSub)
                    ? Directory.GetFiles(versionsSub, "fabric-loader*.json").FirstOrDefault()
                    : null;
            else if (loaderType == "FORGE")
                versionJson = Directory.Exists(versionsSub)
                    ? Directory.GetFiles(versionsSub, "forge*.json").FirstOrDefault()
                    : null;

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
            int javaVersion = 25;
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

            if (loaderType is "FABRIC" or "FORGE")
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
                    string.CompareOrdinal(ver, existing) > 0)
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
            if (loaderType != "FORGE")
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

            string classpath = string.Join(";", cp);

            // ── JVM args ──
            var javaArgs = new List<string>
            {
                $"-Djava.library.path=\"{natives}\"",
                $"-Xmx{memory}G",
                "-Xmn128M",
                $"-Dorg.lwjgl.librarypath=\"{natives}\""
            };

            // authlib-injector (skins) — discovery + optional upload
            string aliJar = Path.Combine(Paths.Runtime, "authlib-injector", "authlib-injector.jar");
            if (File.Exists(aliJar))
            {
                var cfg = AppConfig.Load();
                string? addr = SkinDiscovery.Resolve(cfg);
                if (addr != null)
                {
                    javaArgs.Insert(0, $"-javaagent:\"{aliJar}\"=http://{addr}");
                    // Best-effort push of this player's skin to the host.
                    string model = SkinModel.Get(username);
                    SkinDiscovery.UploadSkin(addr, username, model);
                }
            }

            // Forge-only JVM args from JSON
            if (loaderType == "FORGE" &&
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

            if (loaderType == "FORGE" &&
                root.TryGetProperty("arguments", out var gargs) &&
                gargs.TryGetProperty("game", out var ggame) &&
                ggame.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in ggame.EnumerateArray())
                    if (a.ValueKind == JsonValueKind.String)
                        gameArgs.Add(a.GetString()!);
            }

            string argsString = string.Join(" ", javaArgs.Concat(gameArgs));

            // ── Launch java.exe hidden, working dir = BaseDir (relative cp resolves) ──
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = argsString,
                WorkingDirectory = Paths.BaseDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
        }
    }
}
