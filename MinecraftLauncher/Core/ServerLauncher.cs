using System.Diagnostics;
using System.IO;
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
    }

    public class ServerLaunchResult
    {
        public string ServerDir { get; set; } = "";
        public int Port { get; set; }
        public Process? Process { get; set; }
    }

    /// <summary>
    /// Prepares and launches a Minecraft server. Faithful port of
    /// Initialize-MinecraftServer + Start-MinecraftServer (minus the skin server
    /// wiring, which lives in the skin subsystem not yet ported here).
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

            WriteServerProperties(serverDir, s);

            string serverJar = Path.Combine(serverDir, "server.jar");
            if (!File.Exists(serverJar))
                throw new FileNotFoundException(
                    $"server.jar not found in {serverDir}. Place the server jar there (or use the Setup/Server tools to download it).");

            string? javaExe = Paths.FindJava();
            if (javaExe == null)
                throw new FileNotFoundException("No bundled Java runtime found under runtime/.");

            // A .bat wrapper gives the server a visible console window with a
            // pause at the end, matching the original launcher's behavior.
            string batch = Path.Combine(serverDir, "start_server.bat");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine($"title Minecraft Server - {loaderType} {version}");
            sb.AppendLine($"cd /d \"{serverDir}\"");
            sb.AppendLine("echo Starting Minecraft Server...");
            sb.AppendLine($"echo Version: {version}");
            sb.AppendLine($"echo Loader: {loaderType}");
            sb.AppendLine($"echo Port: {s.Port}");
            sb.AppendLine($"echo Memory: {s.Memory}GB");
            sb.AppendLine("echo.");
            sb.AppendLine($"\"{javaExe}\" -Xmx{s.Memory}G -Xms{s.Memory}G -jar \"{serverJar}\" nogui");
            sb.AppendLine("echo.");
            sb.AppendLine("echo Server stopped. Press any key to close...");
            sb.AppendLine("pause >nul");
            File.WriteAllText(batch, sb.ToString(), Encoding.ASCII);

            var psi = new ProcessStartInfo
            {
                FileName = batch,
                WorkingDirectory = serverDir,
                UseShellExecute = true   // visible console window
            };
            var proc = Process.Start(psi);

            return new ServerLaunchResult { ServerDir = serverDir, Port = s.Port, Process = proc };
        }

        private static void WriteServerProperties(string serverDir, ServerSettings s)
        {
            string path = Path.Combine(serverDir, "server.properties");
            var sb = new StringBuilder();
            sb.AppendLine("#Minecraft server properties");
            sb.AppendLine($"server-port={s.Port}");
            sb.AppendLine($"gamemode={s.Gamemode}");
            sb.AppendLine($"difficulty={s.Difficulty}");
            sb.AppendLine($"max-players={s.MaxPlayers}");
            sb.AppendLine($"pvp={s.Pvp.ToString().ToLowerInvariant()}");
            sb.AppendLine("online-mode=false");
            sb.AppendLine("motd=A Minecraft Server");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
