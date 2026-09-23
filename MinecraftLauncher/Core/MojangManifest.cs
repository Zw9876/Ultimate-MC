using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    public sealed class MojangVersion
    {
        public string Id { get; init; } = "";
        public string Type { get; init; } = "";
        public string ReleaseDate { get; init; } = "";
        public string Url { get; init; } = "";
        public string? Sha1 { get; init; }

        public bool IsRelease => Type.Equals("release", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mojang's version manifest. One endpoint for the whole launcher — the client
    /// and server download paths used to read different manifest versions.
    /// </summary>
    public static class MojangManifest
    {
        public const string ManifestUrl =
            "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

        private static List<MojangVersion>? _cache;

        public static async Task<IReadOnlyList<MojangVersion>> FetchAsync(
            bool forceRefresh, CancellationToken ct)
        {
            if (!forceRefresh && _cache is not null) return _cache;

            string json = await Downloader.GetStringAsync(ManifestUrl, ct);
            using var doc = JsonDocument.Parse(json);

            var list = new List<MojangVersion>();
            foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
            {
                string releaseTime = v.TryGetProperty("releaseTime", out var rt)
                    ? rt.GetString() ?? "" : "";

                list.Add(new MojangVersion
                {
                    Id   = v.GetProperty("id").GetString() ?? "",
                    Type = v.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                    ReleaseDate = releaseTime.Length >= 10 ? releaseTime[..10] : releaseTime,
                    Url  = v.GetProperty("url").GetString() ?? "",
                    Sha1 = v.TryGetProperty("sha1", out var s) ? s.GetString() : null
                });
            }

            _cache = list;
            return list;
        }

        public static async Task<MojangVersion?> FindAsync(string id, CancellationToken ct)
        {
            foreach (var v in await FetchAsync(false, ct))
                if (v.Id == id) return v;
            return null;
        }
    }
}
