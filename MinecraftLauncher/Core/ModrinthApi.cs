using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Finding and downloading mods from Modrinth.
    /// </summary>
    /// <remarks>
    /// Worth having because of something measured on the real machines: they cannot
    /// reach Mojang, but they *can* reach Modrinth. Mods are therefore obtainable on
    /// the machines themselves, even where the game is not, and the alternative is
    /// carrying jars around on a USB stick.
    ///
    /// The API needs no account and no key. Only reads happen here — search, list
    /// versions, fetch a file — and every download is checked against the SHA-1
    /// Modrinth publishes, using the same verified path as every other download in
    /// this launcher.
    /// </remarks>
    public static class ModrinthApi
    {
        private const string Root = "https://api.modrinth.com/v2";

        /// <summary>Modrinth asks callers to identify themselves; this is that.</summary>
        public const string UserAgent = "MinecraftPortableLauncher (offline LAN launcher)";

        /// <summary>How a search is ordered, matching Modrinth's own sort options.</summary>
        public static readonly (string Label, string Index)[] SortOptions =
        {
            ("Relevance",        "relevance"),
            ("Downloads",        "downloads"),
            ("Follows",          "follows"),
            ("Recently updated", "updated"),
            ("Newest",           "newest")
        };

        /// <summary>One search result.</summary>
        public sealed record Hit(
            string ProjectId,
            string Slug,
            string Title,
            string Description,
            string Author,
            long Downloads,
            long Follows,
            string? IconUrl,
            IReadOnlyList<string> Categories,
            DateTime? Updated,
            string? ClientSide,
            string? ServerSide,
            int? Color)
        {
            public string DownloadsText => Compact(Downloads);
            public string FollowsText   => Compact(Follows);

            /// <summary>
            /// The topic tags, tidied for display. Loader names arrive mixed in with
            /// them and are dropped: the whole list is already filtered to one loader,
            /// so repeating it on every card says nothing.
            /// </summary>
            public IEnumerable<string> CategoryList() =>
                Categories.Where(c => !LoaderNames.Contains(c, StringComparer.OrdinalIgnoreCase))
                          .Select(Title1);

            public string CategoriesText => string.Join(" · ", CategoryList().Take(4));

            public string UpdatedText => Updated is not DateTime d
                ? ""
                : (DateTime.UtcNow - d) switch
                {
                    { TotalDays: < 1 } => "updated today",
                    { TotalDays: < 2 } => "updated yesterday",
                    { TotalDays: < 30 } t => $"updated {(int)t.TotalDays} days ago",
                    { TotalDays: < 365 } t => $"updated {(int)(t.TotalDays / 30)} months ago",
                    var t => $"updated {(int)(t.TotalDays / 365)} years ago"
                };

            /// <summary>"Client and server", or whichever side it actually runs on.</summary>
            public string EnvironmentText
            {
                get
                {
                    bool client = Needed(ClientSide);
                    bool server = Needed(ServerSide);

                    if (client && server) return "Client and server";
                    if (client) return "Client";
                    if (server) return "Server";
                    return "";
                }
            }

            private static bool Needed(string? side) =>
                side is "required" or "optional";

            /// <summary>The first letter, for the tile shown when an icon will not load.</summary>
            public string Initial => string.IsNullOrWhiteSpace(Title) ? "?" : Title.Trim()[..1].ToUpperInvariant();
        }

        /// <summary>One page of results, with enough to know whether more exist.</summary>
        public sealed record SearchPage(List<Hit> Hits, int TotalHits, int Offset)
        {
            public bool HasMore => Offset + Hits.Count < TotalHits;
        }

        /// <summary>Loader names arrive mixed in with the topic tags and are noise here.</summary>
        private static readonly string[] LoaderNames =
            { "fabric", "forge", "neoforge", "quilt", "rift", "liteloader", "modloader",
              "bukkit", "spigot", "paper", "purpur", "sponge", "bungeecord", "velocity", "waterfall" };

        private static string Compact(long n) => n switch
        {
            >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
            >= 1_000     => $"{n / 1_000.0:0.#}k",
            _            => n.ToString(CultureInfo.InvariantCulture)
        };

        private static string Title1(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..].Replace('-', ' ');

        /// <summary>A downloadable file belonging to a version.</summary>
        public sealed record ModFile(string FileName, string Url, long Size, string? Sha1)
        {
            public string SizeText => Size >= 1024 * 1024
                ? $"{Size / (1024.0 * 1024.0):0.#} MB"
                : $"{Size / 1024.0:0} KB";
        }

        /// <summary>Another project this version needs, or merely suggests.</summary>
        public sealed record Dependency(string? ProjectId, string? VersionId, string Type)
        {
            public bool Required => Type.Equals("required", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>One published version of a project.</summary>
        public sealed record ModVersion(
            string Id,
            string ProjectId,
            string Name,
            string VersionNumber,
            string VersionType,
            DateTime? Published,
            ModFile? File,
            IReadOnlyList<Dependency> Dependencies)
        {
            public bool IsRelease => VersionType.Equals("release", StringComparison.OrdinalIgnoreCase);

            public string Describe() =>
                $"{VersionNumber} ({VersionType}{(Published is DateTime d ? $", {d:yyyy-MM-dd}" : "")})";
        }

        /// <summary>
        /// The Modrinth loader name for one of this launcher's loader types, or null
        /// when the selection cannot have mods (vanilla).
        /// </summary>
        public static string? LoaderFacet(string loaderType) =>
            (loaderType ?? "").ToUpperInvariant() switch
            {
                "FABRIC"   => "fabric",
                "FORGE"    => "forge",
                "NEOFORGE" => "neoforge",
                "QUILT"    => "quilt",
                "PAPER"    => "paper",
                "PURPUR"   => "purpur",
                _          => null
            };

        /// <summary>
        /// Searches for mods matching the game version and loader. An empty query is
        /// allowed and returns what is popular for that combination, which is a
        /// reasonable way in when you do not know what you are looking for.
        /// </summary>
        public static async Task<SearchPage> SearchAsync(
            string query, string gameVersion, string loaderType, int limit,
            CancellationToken ct, int offset = 0, string index = "relevance")
        {
            string? loader = LoaderFacet(loaderType);

            var facets = new List<string> { "[\"project_type:mod\"]" };
            if (!string.IsNullOrWhiteSpace(gameVersion))
                facets.Add($"[\"versions:{Escape(gameVersion)}\"]");
            if (loader is not null)
                facets.Add($"[\"categories:{loader}\"]");

            string url = $"{Root}/search" +
                         $"?query={Uri.EscapeDataString(query ?? "")}" +
                         $"&limit={Math.Clamp(limit, 1, 100)}" +
                         $"&offset={Math.Max(0, offset)}" +
                         $"&index={Uri.EscapeDataString(index)}" +
                         $"&facets=[{string.Join(",", facets)}]";

            using var doc = await GetJsonAsync(url, ct);

            var hits = new List<Hit>();
            int total = (int)Num(doc.RootElement, "total_hits");

            if (!doc.RootElement.TryGetProperty("hits", out var array))
                return new SearchPage(hits, total, offset);

            foreach (var h in array.EnumerateArray())
            {
                DateTime? updated = null;
                if (Str(h, "date_modified") is string ds &&
                    DateTime.TryParse(ds, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal, out var parsed))
                    updated = parsed;

                var categories = new List<string>();
                if (h.TryGetProperty("display_categories", out var dc) &&
                    dc.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in dc.EnumerateArray())
                        if (c.ValueKind == JsonValueKind.String) categories.Add(c.GetString()!);
                }
                else if (h.TryGetProperty("categories", out var cs) &&
                         cs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cs.EnumerateArray())
                        if (c.ValueKind == JsonValueKind.String) categories.Add(c.GetString()!);
                }

                int? color = null;
                if (h.TryGetProperty("color", out var col) &&
                    col.ValueKind == JsonValueKind.Number && col.TryGetInt32(out int c32))
                    color = c32;

                hits.Add(new Hit(
                    Str(h, "project_id") ?? "",
                    Str(h, "slug") ?? "",
                    Str(h, "title") ?? "(untitled)",
                    Str(h, "description") ?? "",
                    Str(h, "author") ?? "",
                    Num(h, "downloads"),
                    Num(h, "follows"),
                    Str(h, "icon_url"),
                    categories,
                    updated,
                    Str(h, "client_side"),
                    Str(h, "server_side"),
                    color));
            }

            return new SearchPage(hits, total, offset);
        }

        /// <summary>
        /// An icon's bytes, cached on disk so scrolling a list does not re-fetch and
        /// so a second visit works even when the network does not.
        /// </summary>
        /// <remarks>
        /// Returns null rather than throwing: a missing icon is a cosmetic problem and
        /// must never take the browser down with it.
        /// </remarks>
        public static async Task<byte[]?> IconAsync(string? url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            string folder = Path.Combine(Paths.Cache, "modrinth-icons");
            string file = Path.Combine(folder, CacheName(url!));

            try
            {
                if (File.Exists(file)) return await File.ReadAllBytesAsync(file, ct);

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(UserAgent);

                using var response = await Downloader.Client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return null;

                byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);

                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(file, bytes, ct);
                return bytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>A stable, safe file name for a URL, keeping the extension WIC needs.</summary>
        private static string CacheName(string url)
        {
            string extension = Path.GetExtension(url.Split('?')[0]);
            if (extension.Length is < 2 or > 6) extension = ".img";

            byte[] hash = System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(url));

            return Convert.ToHexString(hash).ToLowerInvariant() + extension;
        }

        /// <summary>
        /// Versions of one project that fit this game version and loader, newest first
        /// as Modrinth returns them.
        /// </summary>
        public static async Task<List<ModVersion>> VersionsAsync(
            string idOrSlug, string gameVersion, string loaderType, CancellationToken ct)
        {
            string? loader = LoaderFacet(loaderType);

            string url = $"{Root}/project/{Uri.EscapeDataString(idOrSlug)}/version";
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(gameVersion))
                query.Add($"game_versions=[\"{Escape(gameVersion)}\"]");
            if (loader is not null)
                query.Add($"loaders=[\"{loader}\"]");
            if (query.Count > 0) url += "?" + string.Join("&", query);

            using var doc = await GetJsonAsync(url, ct);

            var versions = new List<ModVersion>();
            foreach (var v in doc.RootElement.EnumerateArray())
                versions.Add(ReadVersion(v));

            return versions;
        }

        /// <summary>One specific version, used to follow a pinned dependency.</summary>
        public static async Task<ModVersion?> VersionAsync(string versionId, CancellationToken ct)
        {
            try
            {
                using var doc = await GetJsonAsync($"{Root}/version/{Uri.EscapeDataString(versionId)}", ct);
                return ReadVersion(doc.RootElement);
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        /// <summary>A project's display title, for naming a dependency in the UI.</summary>
        public static async Task<string?> ProjectTitleAsync(string idOrSlug, CancellationToken ct)
        {
            try
            {
                using var doc = await GetJsonAsync($"{Root}/project/{Uri.EscapeDataString(idOrSlug)}", ct);
                return Str(doc.RootElement, "title");
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        /// <summary>
        /// The newest version worth installing: a proper release if there is one,
        /// otherwise the newest of whatever exists. Betas are common on brand-new
        /// Minecraft versions, so refusing them outright would leave nothing at all.
        /// </summary>
        public static ModVersion? BestOf(IEnumerable<ModVersion> versions)
        {
            var list = versions.Where(v => v.File is not null).ToList();
            return list.FirstOrDefault(v => v.IsRelease) ?? list.FirstOrDefault();
        }

        /// <summary>
        /// Works out everything that has to be downloaded for one version: the mod
        /// itself, plus the required dependencies it names, and theirs in turn.
        /// </summary>
        /// <remarks>
        /// Handled because a missing required dependency is the usual way a hand-picked
        /// mod fails, and the error it produces names a package rather than a mod.
        /// Optional dependencies are deliberately ignored — "optional" on Modrinth
        /// usually means an integration, not something the mod needs.
        /// </remarks>
        public static async Task<List<ModVersion>> ResolveWithDependenciesAsync(
            ModVersion version, string gameVersion, string loaderType, CancellationToken ct)
        {
            var ordered = new List<ModVersion>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<ModVersion>();

            queue.Enqueue(version);
            seen.Add(version.ProjectId);

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var current = queue.Dequeue();
                ordered.Add(current);

                foreach (var dep in current.Dependencies.Where(d => d.Required))
                {
                    ModVersion? resolved = null;

                    // A pinned version wins; otherwise take the best fit for this setup.
                    if (!string.IsNullOrWhiteSpace(dep.VersionId))
                        resolved = await VersionAsync(dep.VersionId!, ct);

                    if (resolved is null && !string.IsNullOrWhiteSpace(dep.ProjectId))
                    {
                        var candidates = await VersionsAsync(dep.ProjectId!, gameVersion, loaderType, ct);
                        resolved = BestOf(candidates);
                    }

                    if (resolved?.File is null) continue;
                    if (!seen.Add(resolved.ProjectId)) continue;

                    queue.Enqueue(resolved);
                }
            }

            return ordered;
        }

        /// <summary>
        /// Downloads one file into the mods folder, verified against its published
        /// SHA-1. Returns the outcome rather than throwing, so one bad file in a set
        /// does not abandon the rest.
        /// </summary>
        public static async Task<string?> DownloadAsync(
            ModFile file, string modsFolder, CancellationToken ct)
        {
            var result = await InstallAsync(file, modsFolder, ct);

            return result.Status switch
            {
                InstallStatus.Installed => null,
                InstallStatus.Replaced  => null,
                _ => result.Detail
            };
        }

        /// <summary>What happened to one file.</summary>
        public enum InstallStatus { Installed, Replaced, AlreadyThere, Failed }

        /// <summary>The outcome of installing one file, and what it displaced.</summary>
        public sealed record InstallResult(
            InstallStatus Status, string? Detail, IReadOnlyList<string> Superseded)
        {
            public static InstallResult Simple(InstallStatus status, string? detail = null) =>
                new(status, detail, Array.Empty<string>());
        }

        /// <summary>
        /// Downloads one file into the mods folder, verified against its published
        /// SHA-1, and turns off any older build of the same mod it replaces.
        /// </summary>
        /// <remarks>
        /// Matching on file name alone is not enough, and getting that wrong is how a
        /// mods folder ends up holding two builds of one mod: the names differ, so
        /// nothing looks like a duplicate, and the loader then refuses to start. The
        /// mod's own id, read from inside the jar, is the only reliable identity.
        ///
        /// The old build is **turned off, not deleted**, which is the same promise the
        /// Mods tab makes everywhere else — a downgrade is one click away, and nothing
        /// a person put there is destroyed by an automatic action.
        /// </remarks>
        public static async Task<InstallResult> InstallAsync(
            ModFile file, string modsFolder, CancellationToken ct)
        {
            Directory.CreateDirectory(modsFolder);
            string target = Path.Combine(modsFolder, SafeName(file.FileName));

            // This exact build is already here, enabled or not.
            if (File.Exists(target))
                return InstallResult.Simple(InstallStatus.AlreadyThere, "already there");
            if (File.Exists(target + ".disabled"))
                return InstallResult.Simple(InstallStatus.AlreadyThere, "already there, turned off");

            bool ok = await Downloader.GetVerifiedAsync(file.Url, target, file.Sha1, null, ct);
            if (!ok)
            {
                try { File.Delete(target); } catch { }
                return InstallResult.Simple(InstallStatus.Failed, "failed verification");
            }

            var superseded = new List<string>();

            foreach (string other in ModInspector.OtherCopiesOf(target, modsFolder))
            {
                ct.ThrowIfCancellationRequested();

                // Already off; it cannot clash with anything.
                if (other.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    File.Move(other, other + ".disabled");
                    superseded.Add(Path.GetFileName(other));
                }
                catch
                {
                    // In use, or read-only. Say nothing was replaced rather than claim it.
                }
            }

            return new InstallResult(
                superseded.Count > 0 ? InstallStatus.Replaced : InstallStatus.Installed,
                null,
                superseded);
        }

        /// <summary>
        /// Keeps a server-supplied file name to a bare name in the mods folder.
        /// Nothing downloaded should be able to choose its own path.
        /// </summary>
        internal static string SafeName(string fileName)
        {
            string name = Path.GetFileName(fileName ?? "");
            if (string.IsNullOrWhiteSpace(name)) return "mod.jar";

            foreach (char bad in Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');

            return name;
        }

        private static ModVersion ReadVersion(JsonElement v)
        {
            ModFile? file = null;
            if (v.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                // Several files can hang off one version (sources, javadoc); the
                // primary one is the mod. Fall back to the first if none is marked.
                JsonElement? chosen = null;
                foreach (var f in files.EnumerateArray())
                {
                    if (f.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True)
                    {
                        chosen = f;
                        break;
                    }
                    chosen ??= f;
                }

                if (chosen is JsonElement picked)
                {
                    string? sha1 = null;
                    if (picked.TryGetProperty("hashes", out var hashes))
                        sha1 = Str(hashes, "sha1");

                    file = new ModFile(
                        Str(picked, "filename") ?? "mod.jar",
                        Str(picked, "url") ?? "",
                        Num(picked, "size"),
                        sha1);
                }
            }

            var deps = new List<Dependency>();
            if (v.TryGetProperty("dependencies", out var d) && d.ValueKind == JsonValueKind.Array)
            {
                foreach (var dep in d.EnumerateArray())
                    deps.Add(new Dependency(
                        Str(dep, "project_id"),
                        Str(dep, "version_id"),
                        Str(dep, "dependency_type") ?? "optional"));
            }

            DateTime? published = null;
            if (Str(v, "date_published") is string ds &&
                DateTime.TryParse(ds, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal, out var parsed))
                published = parsed;

            return new ModVersion(
                Str(v, "id") ?? "",
                Str(v, "project_id") ?? "",
                Str(v, "name") ?? "",
                Str(v, "version_number") ?? "",
                Str(v, "version_type") ?? "release",
                published,
                file,
                deps);
        }

        private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await Downloader.Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }

        private static string Escape(string s) => s.Replace("\\", "").Replace("\"", "");

        private static string? Str(JsonElement el, string name) =>
            el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;

        private static long Num(JsonElement el, string name) =>
            el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number &&
            p.TryGetInt64(out long v) ? v : 0;
    }
}
