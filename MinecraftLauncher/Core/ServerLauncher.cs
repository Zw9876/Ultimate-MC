using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;

namespace MinecraftLauncher.Core
{
    public class ServerSettings
    {
        public int    Port       { get; set; } = 25565;
        public string Gamemode   { get; set; } = "survival";
        public string Difficulty { get; set; } = "normal";
        public int    MaxPlayers { get; set; } = 10;
        public bool   Pvp        { get; set; } = true;
        public int    Memory     { get; set; } = 2;

        /// <summary>How far players can see, in chunks.</summary>
        public int    ViewDistance       { get; set; } = 10;

        /// <summary>How far the world actually ticks. The main lever on tick cost.</summary>
        public int    SimulationDistance { get; set; } = 6;

        /// <summary>
        /// How far entities are sent to players, as a percentage of the normal range.
        /// Every player multiplies this work, so it is the setting that scales worst
        /// with a crowd.
        /// </summary>
        public int    EntityBroadcastPercent { get; set; } = 75;
    }

    public class ServerLaunchResult
    {
        public string ServerDir { get; set; } = "";
        public int Port { get; set; }

        /// <summary>The live server, owning its process and console.</summary>
        public ServerSession? Session { get; set; }
    }

    /// <summary>
    /// Prepares and launches a Minecraft server. Port of Initialize-MinecraftServer
    /// + Start-MinecraftServer. The skin server is started by the caller, not here —
    /// see the Server tab in MainWindow.
    /// </summary>
    public static class ServerLauncher
    {
        public static ServerLaunchResult Launch(string version, string loaderType, ServerSettings s)
        {
            string serverDir = Paths.ServerDir(loaderType, version);
            Directory.CreateDirectory(serverDir);

            // eula.txt — required for the server to start
            File.WriteAllText(Path.Combine(serverDir, "eula.txt"),
                "eula=true\n", new UTF8Encoding(false));

            // Only the launcher-owned keys are touched; hand edits survive.
            ServerProperties.Write(serverDir, version, s);

            // Forge 1.17+ and NeoForge leave an argument file rather than a runnable
            // jar, so the launch command depends on what the installer produced.
            var plan = ServerStartPlanner.Resolve(serverDir, s.Memory);

            // Must match what the game version was built against — too old a JVM
            // fails with UnsupportedClassVersionError once the server starts.
            string? javaExe = Paths.FindJavaForMinecraft(version);
            if (javaExe == null)
                throw new FileNotFoundException("No bundled Java runtime found under runtime/.");

            // Still written, but only as a record of the exact command and a way to
            // start the server without the launcher. It is no longer what we run:
            // a .bat in its own window cannot be read back, so the launcher could
            // neither show the console nor stop the server cleanly.
            WriteStartScript(serverDir, version, loaderType, javaExe, plan, s);

            var psi = new ProcessStartInfo
            {
                FileName               = javaExe,
                WorkingDirectory       = serverDir,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8
            };

            // ArgumentList quotes each argument only when it needs it, which matters
            // for the @argfile references: those must reach the JVM intact even when
            // the launcher lives under a path with spaces in it.
            foreach (string arg in plan.JavaArgs) psi.ArgumentList.Add(arg);

            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("The server process could not be started.");

            var session = new ServerSession(process, serverDir, version, loaderType, s.Port);
            return new ServerLaunchResult { ServerDir = serverDir, Port = s.Port, Session = session };
        }

        /// <summary>
        /// Quotes a batch argument, except an @argfile reference which both cmd and
        /// the JVM require unquoted.
        /// </summary>
        private static string Quote(string arg)
        {
            if (arg.StartsWith('@')) return arg;
            return arg.Contains(' ') ? $"\"{arg}\"" : arg;
        }

        private static void WriteStartScript(
            string serverDir, string version, string loaderType,
            string javaExe, ServerStartPlan plan, ServerSettings s)
        {
            string command = $"\"{javaExe}\" " + string.Join(" ", plan.JavaArgs.Select(Quote));

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine(":: Written by Minecraft Portable Launcher as a record of the exact");
            sb.AppendLine(":: launch command. The launcher runs java directly so it can show the");
            sb.AppendLine(":: console; this file is here for starting the server without it.");
            sb.AppendLine($"title Minecraft Server - {loaderType} {version}");
            sb.AppendLine($"cd /d \"{serverDir}\"");
            sb.AppendLine($"echo Version: {version}   Loader: {loaderType}");
            sb.AppendLine($"echo Port: {s.Port}   Memory: {s.Memory}GB");
            sb.AppendLine($"echo Launch: {plan.Describe}");
            sb.AppendLine("echo.");
            sb.AppendLine(command);
            sb.AppendLine("echo.");
            sb.AppendLine("echo Server stopped. Press any key to close...");
            sb.AppendLine("pause >nul");

            try
            {
                File.WriteAllText(Path.Combine(serverDir, "start_server.bat"),
                    sb.ToString(), Encoding.ASCII);
            }
            catch
            {
                // Only a convenience file — never block the launch over it.
            }
        }

        /// <summary>
        /// True if something is already listening on this port. Catches a server left
        /// running by a previous launcher session, which the in-process session list
        /// knows nothing about.
        /// </summary>
        public static bool IsPortInUse(int port)
        {
            try
            {
                return IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners()
                    .Any(endpoint => endpoint.Port == port);
            }
            catch
            {
                // If we cannot tell, do not stand in the user's way.
                return false;
            }
        }
    }
}
