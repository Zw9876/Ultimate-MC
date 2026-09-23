using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Telling whether the mods in a folder have newer builds on Modrinth.
    /// </summary>
    /// <remarks>
    /// Identity comes from the **SHA-1 of the jar**, not its file name. Mod authors name
    /// files however they like and the same build is often renamed between downloads, so
    /// a name is not an identity — the hash is what Modrinth itself indexes by.
    ///
    /// Modrinth has an endpoint built for exactly this question:
    /// <c>POST /version_files/update</c> takes a list of hashes plus the loader and game
    /// version, and hands back the newest build of each project that fits. So the whole
    /// folder is two requests regardless of how many mods are in it, rather than one per
    /// mod — which matters on a connection that is already the constrained part.
    ///
    /// A jar whose hash Modrinth does not know is reported as such rather than as
    /// up to date. Hand-built jars, private builds and files from anywhere else are all
    /// legitimately unknown, and saying "no update" about them would be a lie.
    /// </remarks>
    public static partial class ModrinthApi
    {
        /// <summary>What was found out about one jar in the folder.</summary>
        public sealed record ModUpdateStatus(
            string FileName,
            string FullPath,
            string Sha1,
            ModVersion? Installed,
            ModVersion? Latest,
            string? ProjectTitle)
        {
            /// <summary>False when Modrinth has never seen this file.</summary>
            public bool OnModrinth => Installed is not null;

            /// <summary>
            /// True only when Modrinth knows this file *and* offers a different build
            /// for the loader and game version asked about.
            /// </summary>
            public bool HasUpdate =>
                Installed is not null && Latest is not null &&
                !Latest.Id.Equals(Installed.Id, StringComparison.Ordinal);

            /// <summary>The name to show a person: the project's, falling back to the file's.</summary>
            public string Title => ProjectTitle ?? Installed?.Name ?? FileName;

            /// <summary>One short phrase for the Update column.</summary>
            public string Text =>
                !OnModrinth      ? "not on Modrinth"
                : HasUpdate      ? $"{Installed!.VersionNumber} → {Latest!.VersionNumber}"
                : Latest is null ? "none for this version"
                                 : "up to date";
        }

        /// <summary>
        /// Checks a set of jars against Modrinth. Never throws for one bad file: a jar
        /// that cannot be read is returned unknown, because one unreadable mod should
        /// not cost the answer for all the others.
        /// </summary>
        public static async Task<List<ModUpdateStatus>> CheckForUpdatesAsync(
            IEnumerable<(string FileName, string FullPath)> jars,
            string gameVersion, string loaderType,
            IProgress<string>? log, CancellationToken ct)
        {
            string? loader = LoaderFacet(loaderType);
            if (loader is null)
                throw new InvalidOperationException($"{loaderType} does not run mods from Modrinth.");

            var files = jars.ToList();
            var hashes = new Dictionary<string, (string FileName, string FullPath)>(StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                foreach (var (name, path) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var stream = File.OpenRead(path);
                        string hash = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
                        hashes[hash] = (name, path);
                    }
                    catch (IOException)
                    {
                        log?.Report($"[SKIPPED] {name} could not be read.");
                    }
                }
            }, ct);

            log?.Report($"Asking Modrinth about {hashes.Count} files.");

            var installed = await LookupAsync($"{Root}/version_files", hashes.Keys, loader, gameVersion,
                                              includeTarget: false, ct);
            var latest = await LookupAsync($"{Root}/version_files/update", hashes.Keys, loader, gameVersion,
                                           includeTarget: true, ct);

            // One request for every project name, rather than one per mod.
            var titles = await ProjectTitlesAsync(
                installed.Values.Select(v => v.ProjectId).Where(id => id.Length > 0).Distinct(), ct);

            var results = new List<ModUpdateStatus>();

            foreach (var (hash, file) in hashes)
            {
                installed.TryGetValue(hash, out var have);
                latest.TryGetValue(hash, out var newest);

                string? title = have is not null && titles.TryGetValue(have.ProjectId, out var t) ? t : null;

                results.Add(new ModUpdateStatus(file.FileName, file.FullPath, hash, have, newest, title));
            }

            return results.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Posts a batch of hashes to one of the two lookup endpoints and reads the
        /// hash-keyed reply.
        /// </summary>
        /// <remarks>
        /// Chunked because the request body is a URL-length-free but not unlimited thing,
        /// and a folder here can hold well over a hundred jars. The two endpoints differ
        /// only in whether the body names the loader and game version being moved to,
        /// which is why they share this.
        /// </remarks>
        private static async Task<Dictionary<string, ModVersion>> LookupAsync(
            string url, IEnumerable<string> hashes, string loader, string gameVersion,
            bool includeTarget, CancellationToken ct)
        {
            var found = new Dictionary<string, ModVersion>(StringComparer.OrdinalIgnoreCase);
            var all = hashes.ToList();

            for (int start = 0; start < all.Count; start += 100)
            {
                ct.ThrowIfCancellationRequested();

                var batch = all.Skip(start).Take(100);

                var body = new StringBuilder();
                body.Append("{\"hashes\":[");
                body.Append(string.Join(",", batch.Select(h => $"\"{Escape(h)}\"")));
                body.Append("],\"algorithm\":\"sha1\"");

                if (includeTarget)
                {
                    body.Append($",\"loaders\":[\"{Escape(loader)}\"]");
                    if (!string.IsNullOrWhiteSpace(gameVersion))
                        body.Append($",\"game_versions\":[\"{Escape(gameVersion)}\"]");
                }

                body.Append('}');

                using var doc = await PostJsonAsync(url, body.ToString(), ct);

                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    found[entry.Name] = ReadVersion(entry.Value);
                }
            }

            return found;
        }

        /// <summary>Project id to title, for every id in one request.</summary>
        private static async Task<Dictionary<string, string>> ProjectTitlesAsync(
            IEnumerable<string> projectIds, CancellationToken ct)
        {
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ids = projectIds.ToList();
            if (ids.Count == 0) return titles;

            try
            {
                string list = string.Join(",", ids.Select(i => $"\"{Escape(i)}\""));
                using var doc = await GetJsonAsync($"{Root}/projects?ids=[{Uri.EscapeDataString(list)}]", ct);

                foreach (var p in doc.RootElement.EnumerateArray())
                {
                    string? id = Str(p, "id");
                    string? title = Str(p, "title");
                    if (id is not null && title is not null) titles[id] = title;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                // Names are a courtesy. Losing them must not lose the answer.
            }

            return titles;
        }

        private static async Task<JsonDocument> PostJsonAsync(string url, string body, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await Downloader.Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
    }
}
