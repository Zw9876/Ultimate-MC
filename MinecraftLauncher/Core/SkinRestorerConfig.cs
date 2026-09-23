using System.IO;
using System.Text;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Writes Skin Restorer's config so it fetches skins from our local skin
    /// server instead of Mojang.
    /// </summary>
    public static class SkinRestorerConfig
    {
        /// <summary>
        /// Must run before the Minecraft server starts, otherwise Skin Restorer
        /// generates its own default config and never reads ours. Any existing
        /// file is replaced outright rather than merged.
        /// </summary>
        public static string Write(string serverDir, int port) =>
            Write(serverDir, "127.0.0.1", port);

        /// <summary>
        /// As above, but pointing at <paramref name="host"/>. When another machine on
        /// the LAN is already running the skin server we reuse it rather than start a
        /// competing one, and Skin Restorer then has to reach across the network
        /// instead of talking to loopback.
        /// </summary>
        public static string Write(string serverDir, string host, int port)
        {
            string configDir = Path.Combine(serverDir, "config", "skinrestorer");
            Directory.CreateDirectory(configDir);

            string configFile = Path.Combine(configDir, "config.json");
            if (File.Exists(configFile)) File.Delete(configFile);

            // Usually the skin server is on this machine and the provider talks to
            // loopback; only the texture URLs it hands out need to be LAN-reachable.
            // When we adopt another machines server, host is their IP instead.
            string json = $$"""
            {
              "language": "en_us",
              "storage": { "location": "world" },
              "join": {
                "refreshSkin": true,
                "skipRefreshProviders": [],
                "applyDelay": 0,
                "autoFetch": {
                  "enabled": true,
                  "overrideExisting": true,
                  "providers": [ "launcher-skins" ]
                }
              },
              "request": { "proxy": "", "timeout": 10, "userAgent": "" },
              "providers": {
                "mojang":     { "enabled": false, "name": "mojang",     "cache": { "enabled": true, "duration": 60 } },
                "ely_by":     { "enabled": false, "name": "ely.by",     "cache": { "enabled": true, "duration": 60 } },
                "mineskin":   { "apiKey": "", "proxyUrlUpload": false, "enabled": false, "name": "web", "cache": { "enabled": true, "duration": 300 } },
                "collection": { "sources": [], "enabled": false, "name": "collection", "cache": { "enabled": true, "duration": 604800 } },
                "custom": [
                  {
                    "type": "yggdrasil",
                    "enabled": true,
                    "name": "launcher-skins",
                    "baseUrl": "http://{{host}}:{{port}}",
                    "servicesUrl": "http://{{host}}:{{port}}",
                    "sessionUrl": "http://{{host}}:{{port}}",
                    "useProviderSignature": true,
                    "cache": { "enabled": false, "duration": 1 }
                  }
                ]
              },
              "version": 3
            }
            """;

            File.WriteAllText(configFile, json, new UTF8Encoding(false));
            return configFile;
        }
    }
}
