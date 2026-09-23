using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MinecraftLauncher.Core
{
    /// <summary>How a server directory expects to be started.</summary>
    public enum ServerStartKind
    {
        /// <summary>A runnable jar: vanilla, Fabric, Paper, Purpur, pre-1.17 Forge.</summary>
        ExecutableJar,

        /// <summary>
        /// An argument file: Forge 1.17+ and every NeoForge build. The installer
        /// leaves no runnable jar, only a libraries tree plus a generated argument
        /// file naming the module path and main class.
        /// </summary>
        ArgumentFile
    }

    public sealed record ServerStartPlan(
        ServerStartKind Kind,
        IReadOnlyList<string> JavaArgs,
        string Describe);

    /// <summary>
    /// Works out how to launch whatever the installer left behind.
    /// </summary>
    /// <remarks>
    /// The PowerShell launcher only ever ran <c>java -jar server.jar</c>, so Forge
    /// 1.17 and newer — which install a run script instead — could be downloaded but
    /// never started. Detecting the layout here is what makes those versions work.
    /// </remarks>
    public static class ServerStartPlanner
    {
        /// <summary>Relative locations an installer may write its argument file to.</summary>
        private static readonly string[] ArgumentFileRoots =
        {
            @"libraries\net\minecraftforge\forge",
            @"libraries\net\neoforged\neoforge"
        };

        public const string UserJvmArgsFile = "user_jvm_args.txt";

        /// <summary>True when the directory already holds a startable server.</summary>
        public static bool IsInstalled(string serverDir) => TryResolve(serverDir, 2) is not null;

        public static ServerStartPlan Resolve(string serverDir, int memoryGb) =>
            TryResolve(serverDir, memoryGb)
            ?? throw new FileNotFoundException(
                $"No startable server found in {serverDir}. Expected either server.jar or a " +
                "Forge/NeoForge argument file under libraries\\.");

        private static ServerStartPlan? TryResolve(string serverDir, int memoryGb)
        {
            if (!Directory.Exists(serverDir)) return null;

            // The argument file is checked first on purpose. Modern Forge installs
            // BOTH an argument file and a "-shim.jar", and the argument file is the
            // path its own run.bat uses; the shim exists only for hosting panels
            // that insist on a jar. Non-Forge servers have no argument file, so they
            // fall through to the jar below.
            string? argsFile = FindArgumentFile(serverDir);
            if (argsFile is null)
            {
                string jar = Path.Combine(serverDir, "server.jar");
                if (!File.Exists(jar)) return null;

                var args = new List<string> { $"-Xmx{memoryGb}G", $"-Xms{memoryGb}G" };
                args.AddRange(JvmTuning.GcFlags(memoryGb));
                args.AddRange(new[] { "-jar", jar, "nogui" });

                return new ServerStartPlan(
                    ServerStartKind.ExecutableJar,
                    args,
                    "server.jar");
            }

            // Memory belongs in user_jvm_args.txt: the generated argument file is
            // overwritten on reinstall, and Forge intends the JVM file to be edited.
            WriteJvmArgs(serverDir, memoryGb);

            return new ServerStartPlan(
                ServerStartKind.ArgumentFile,
                new[]
                {
                    $"@{Path.Combine(serverDir, UserJvmArgsFile)}",
                    $"@{argsFile}",
                    "nogui"
                },
                Path.GetFileName(Path.GetDirectoryName(argsFile)) is string owner
                    ? $"argument file ({owner})"
                    : "argument file");
        }

        /// <summary>
        /// Finds the installer-generated argument file. Windows builds use
        /// win_args.txt; unix_args.txt is accepted as a fallback because some builds
        /// have shipped only that.
        /// </summary>
        public static string? FindArgumentFile(string serverDir)
        {
            foreach (string relative in ArgumentFileRoots)
            {
                string root = Path.Combine(serverDir, relative);
                if (!Directory.Exists(root)) continue;

                foreach (string name in new[] { "win_args.txt", "unix_args.txt" })
                {
                    string? match = Directory
                        .EnumerateFiles(root, name, SearchOption.AllDirectories)
                        .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();

                    if (match is not null) return match;
                }
            }

            return null;
        }

        private static void WriteJvmArgs(string serverDir, int memoryGb)
        {
            string path = Path.Combine(serverDir, UserJvmArgsFile);
            var text = new StringBuilder();
            text.AppendLine("# Written by Minecraft Portable Launcher — edits here are overwritten.");
            text.AppendLine("# Memory comes from the launcher's slider. The rest are garbage-collector");
            text.AppendLine("# settings that trade one long pause for many short ones, which is what");
            text.AppendLine("# stops a busy server freezing for a moment every so often.");
            text.AppendLine($"-Xmx{memoryGb}G");
            text.AppendLine($"-Xms{memoryGb}G");

            foreach (string flag in JvmTuning.GcFlags(memoryGb))
                text.AppendLine(flag);

            try { File.WriteAllText(path, text.ToString(), new UTF8Encoding(false)); }
            catch { /* a read-only folder still launches, just with default memory */ }
        }
    }
}
