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
        public int    Memory      { get; set; } = 2;
        public string LastVersion { get; set; } = "";
        public string LastLoader  { get; set; } = "VANILLA";
        public string? SkinServer { get; set; } = null;   // optional manual override

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

            File.WriteAllText(Paths.ConfigFile, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
