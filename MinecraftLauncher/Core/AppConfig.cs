using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Client settings persisted to config.txt. Format matches the original
    /// launcher exactly (NICK=, max_MEM=, LAST_VERSION=, LAST_LOADER=, SKIN_SERVER=)
    /// so existing config files remain compatible.
    /// </summary>
    public class AppConfig
    {
        public string Username    { get; set; } = "";
        /// <summary>Client heap in GB. 0 means never set — pick one to suit the machine
        /// rather than shipping everyone the 2 GB this used to default to.</summary>
        public int    Memory      { get; set; } = 0;
        public string LastVersion { get; set; } = "";
        public string LastLoader  { get; set; } = "VANILLA";
        public string? SkinServer { get; set; } = null;   // optional manual override

        // ── Server tab ──
        // These used to be thrown away on exit, which mattered far more than it sounds:
        // the memory slider reset to 2 GB every launch, so a heavily modded server with
        // a dozen players was silently being started on 2 GB no matter what anyone set.
        public string SvVersion    { get; set; } = "";
        public string SvLoader     { get; set; } = "";
        public int    SvMemory     { get; set; } = 0;      // 0 = never set, pick a sensible default
        public int    SvPort       { get; set; } = 25565;
        public int    SvMaxPlayers { get; set; } = 10;
        public string SvGamemode   { get; set; } = "survival";
        public string SvDifficulty { get; set; } = "normal";
        public bool   SvPvp        { get; set; } = true;
        public int    SvView       { get; set; } = 10;
        public int    SvSimulation { get; set; } = 6;

        public static AppConfig Load()
        {
            var cfg = new AppConfig();
            if (!File.Exists(Paths.ConfigFile)) return cfg;

            foreach (var raw in File.ReadAllLines(Paths.ConfigFile))
            {
                var line = raw.Trim();
                Match m;
                if ((m = Regex.Match(line, @"^NICK=(.+)$")).Success)          cfg.Username = m.Groups[1].Value;
                else if ((m = Regex.Match(line, @"^max_MEM=(\d+)$")).Success)  cfg.Memory = int.Parse(m.Groups[1].Value);
                else if ((m = Regex.Match(line, @"^LAST_VERSION=(.+)$")).Success) cfg.LastVersion = m.Groups[1].Value;
                else if ((m = Regex.Match(line, @"^LAST_LOADER=(.+)$")).Success)  cfg.LastLoader = m.Groups[1].Value;
                else if ((m = Regex.Match(line, @"^SKIN_SERVER=(.+)$")).Success)  cfg.SkinServer = m.Groups[1].Value.Trim();
                else if ((m = Regex.Match(line, @"^SV_VERSION=(.+)$")).Success)   cfg.SvVersion = m.Groups[1].Value.Trim();
                else if ((m = Regex.Match(line, @"^SV_LOADER=(.+)$")).Success)    cfg.SvLoader = m.Groups[1].Value.Trim();
                else if ((m = Regex.Match(line, @"^SV_MEM=(\d+)$")).Success)      cfg.SvMemory = int.Parse(m.Groups[1].Value);
                else if ((m = Regex.Match(line, @"^SV_PORT=(\d+)$")).Success)     cfg.SvPort = int.Parse(m.Groups[1].Value);
                else if ((m = Regex.Match(line, @"^SV_MAX=(\d+)$")).Success)      cfg.SvMaxPlayers = int.Parse(m.Groups[1].Value);
                else if ((m = Regex.Match(line, @"^SV_GAMEMODE=(.+)$")).Success)  cfg.SvGamemode = m.Groups[1].Value.Trim();
                else if ((m = Regex.Match(line, @"^SV_DIFFICULTY=(.+)$")).Success) cfg.SvDifficulty = m.Groups[1].Value.Trim();
                else if ((m = Regex.Match(line, @"^SV_PVP=(true|false)$", RegexOptions.IgnoreCase)).Success)
                    cfg.SvPvp = bool.Parse(m.Groups[1].Value);
                else if ((m = Regex.Match(line, @"^SV_VIEW=(\d+)$")).Success)     cfg.SvView = int.Parse(m.Groups[1].Value);
                else if ((m = Regex.Match(line, @"^SV_SIM=(\d+)$")).Success)      cfg.SvSimulation = int.Parse(m.Groups[1].Value);
            }
            return cfg;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"NICK={Username}");
            sb.AppendLine($"max_MEM={Memory}");
            sb.AppendLine($"LAST_VERSION={LastVersion}");
            sb.AppendLine($"LAST_LOADER={LastLoader}");
            if (!string.IsNullOrWhiteSpace(SkinServer))
                sb.AppendLine($"SKIN_SERVER={SkinServer}");

            if (!string.IsNullOrWhiteSpace(SvVersion)) sb.AppendLine($"SV_VERSION={SvVersion}");
            if (!string.IsNullOrWhiteSpace(SvLoader))  sb.AppendLine($"SV_LOADER={SvLoader}");
            if (SvMemory > 0)                          sb.AppendLine($"SV_MEM={SvMemory}");
            sb.AppendLine($"SV_PORT={SvPort}");
            sb.AppendLine($"SV_MAX={SvMaxPlayers}");
            sb.AppendLine($"SV_GAMEMODE={SvGamemode}");
            sb.AppendLine($"SV_DIFFICULTY={SvDifficulty}");
            sb.AppendLine($"SV_PVP={SvPvp.ToString().ToLowerInvariant()}");
            sb.AppendLine($"SV_VIEW={SvView}");
            sb.AppendLine($"SV_SIM={SvSimulation}");

            File.WriteAllText(Paths.ConfigFile, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
