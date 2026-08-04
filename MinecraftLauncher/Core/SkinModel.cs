using System.IO;
using System.Text.Json;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Reads a username's skin model ("steve" or "alex") from skins/metadata.json.
    /// Defaults to "steve" when unknown.
    /// </summary>
    public static class SkinModel
    {
        public static string Get(string username)
        {
            try
            {
                string metaFile = Path.Combine(Paths.Skins, "metadata.json");
                if (!File.Exists(metaFile)) return "steve";

                using var doc = JsonDocument.Parse(File.ReadAllText(metaFile));
                if (doc.RootElement.TryGetProperty(username, out var val) &&
                    val.ValueKind == JsonValueKind.String)
                {
                    string m = val.GetString() ?? "steve";
                    return m.ToLowerInvariant() == "alex" ? "alex" : "steve";
                }
            }
            catch { }
            return "steve";
        }
    }
}
