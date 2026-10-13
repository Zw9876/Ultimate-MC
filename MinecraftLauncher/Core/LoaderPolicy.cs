using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// The Fabric loader version the host wants every machine on, per Minecraft version.
    /// </summary>
    /// <remarks>
    /// <para><b>The host pins it; clients install that exact version.</b> The obvious
    /// alternative — each machine asking Fabric for "the latest" when it updates — is
    /// how twenty machines end up on several different loaders, because a new loader
    /// released between two machines starting is enough to split them. That is the same
    /// reasoning already applied to required mods: resolved once on the machine with
    /// internet, not per client. Keeping every machine on the same thing is the whole
    /// point of the mandatory update, and a loader is part of what they run.</para>
    ///
    /// <para><b>Only versions that already have Fabric.</b> A target is published for a
    /// Minecraft version this host actually has a Fabric loader for, and a client only
    /// acts on a target for a version <i>it</i> already has one for. So this keeps
    /// existing installs current and never adds a Fabric profile to a version somebody
    /// plays on Vanilla or Forge — where it would sit next to the wrong mods folder and
    /// crash whoever picked it.</para>
    ///
    /// <para>Servers are covered too, and separately: a server keeps its loader inside
    /// <c>server.jar</c> rather than as a profile, so the two are read and installed by
    /// different code (<see cref="FabricServerLoader"/>).</para>
    /// </remarks>
    public static class LoaderPolicy
    {
        /// <summary>Where a client asks what loader it should be on.</summary>
        public const string Endpoint = "/launcher/loaders";

        /// <summary>The host's copy, beside the launcher.</summary>
        public static string FilePath => Path.Combine(Paths.BaseDir, "loader-policy.json");

        /// <summary>
        /// The last answer this machine was given.
        /// </summary>
        /// <remarks>
        /// Unlike a play block, this cache is a convenience rather than an enforcement:
        /// it lets a machine that has already been told the target finish the job after
        /// the host goes away mid-evening. Nothing bad happens if it is deleted.
        /// </remarks>
        public static string CachePath => Path.Combine(Paths.BaseDir, "loader-policy-cache.json");

        public sealed class Target
        {
            [JsonPropertyName("minecraft")] public string Minecraft { get; set; } = "";
            [JsonPropertyName("loader")]    public string Loader { get; set; } = "";

            /// <summary>Whether this host had it as a client profile, a server, or both.</summary>
            [JsonPropertyName("client")] public bool Client { get; set; } = true;
            [JsonPropertyName("server")] public bool Server { get; set; }

            [JsonPropertyName("pinned")] public DateTime Pinned { get; set; }

            public string Describe() =>
                $"Minecraft {Minecraft}: Fabric loader {Loader}" +
                (Client && Server ? " (client and server)" : Server ? " (server)" : "");
        }

        public sealed class Listing
        {
            /// <summary>
            /// Off by default, so publishing a build can never start installing loaders
            /// on twenty machines because somebody forgot a switch existed.
            /// </summary>
            [JsonPropertyName("enabled")] public bool Enabled { get; set; }

            [JsonPropertyName("targets")] public List<Target> Targets { get; set; } = new();

            [JsonPropertyName("by")] public string? By { get; set; }

            public Target? For(string mcVersion) =>
                Targets.FirstOrDefault(t =>
                    string.Equals(t.Minecraft, mcVersion, StringComparison.OrdinalIgnoreCase));
        }

        // ── What this machine has, and what it needs ─────────────────

        /// <summary>One thing that is behind, and what it should be on.</summary>
        /// <param name="Server">
        /// True for a server install, false for a client version. The two are installed
        /// by different code and are reported separately so a status line can say which.
        /// </param>
        public sealed record Behind(string Minecraft, string? Have, string Want, bool Server)
        {
            public string Describe() =>
                $"{(Server ? "server" : "client")} {Minecraft}: " +
                $"{(Have is null ? "none" : Have)} -> {Want}";
        }

        /// <summary>
        /// The client Minecraft versions on this machine that already have a Fabric
        /// loader, newest loader first.
        /// </summary>
        public static Dictionary<string, string> FabricClients()
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(Paths.Versions)) return found;

            foreach (string dir in Directory.GetDirectories(Paths.Versions))
            {
                string mc = Path.GetFileName(dir);
                var loaders = FabricLoaderUpdate.InstalledLoaders(mc);
                if (loaders.Count > 0) found[mc] = loaders[0];
            }

            return found;
        }

        /// <summary>
        /// The Fabric servers on this machine and the loader each one actually starts on.
        /// </summary>
        /// <remarks>
        /// Read from inside <c>server.jar</c>, which is authoritative — the libraries
        /// folder keeps every loader a server has ever had, so a folder listing reports
        /// what was installed last rather than what will run.
        /// </remarks>
        public static Dictionary<string, string> FabricServers()
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(Paths.Servers)) return found;

            foreach (string dir in Directory.GetDirectories(Paths.Servers))
            {
                string name = Path.GetFileName(dir);

                // servers/<loader>-<version>; only the Fabric ones are ours to touch.
                if (!name.StartsWith(FabricServerLoader.LoaderType + "-", StringComparison.OrdinalIgnoreCase))
                    continue;

                string mc = name[(FabricServerLoader.LoaderType.Length + 1)..];
                if (!FabricServerLoader.Exists(mc)) continue;

                string? installed = FabricServerLoader.InstalledVersion(mc);
                if (!string.IsNullOrWhiteSpace(installed)) found[mc] = installed!;
            }

            return found;
        }

        /// <summary>
        /// What this machine would have to install to match the policy.
        /// </summary>
        /// <remarks>
        /// A version with no Fabric loader is skipped rather than installed into: the
        /// policy is to keep Fabric current, not to put Fabric everywhere.
        ///
        /// A machine already on something <i>newer</i> than the target is left alone.
        /// Downgrading somebody because the host has not re-checked lately would be a
        /// worse outcome than a version mismatch, and compared with
        /// <see cref="MavenVersion"/> rather than by string, since "0.19.10" is newer
        /// than "0.19.5" and sorts before it.
        /// </remarks>
        public static List<Behind> WhatIsBehind(Listing policy)
        {
            var behind = new List<Behind>();
            if (!policy.Enabled) return behind;

            foreach (var (mc, have) in FabricClients())
            {
                var target = policy.For(mc);
                if (target is null || !target.Client) continue;

                if (MavenVersion.Compare(have, target.Loader) < 0)
                    behind.Add(new Behind(mc, have, target.Loader, Server: false));
            }

            foreach (var (mc, have) in FabricServers())
            {
                var target = policy.For(mc);
                if (target is null || !target.Server) continue;

                if (MavenVersion.Compare(have, target.Loader) < 0)
                    behind.Add(new Behind(mc, have, target.Loader, Server: true));
            }

            return behind;
        }

        // ── Deciding the targets (host side, needs internet) ─────────

        /// <summary>
        /// Asks Fabric for the newest loader for everything this host has Fabric on, and
        /// returns the policy that would pin it. Does not save.
        /// </summary>
        /// <remarks>
        /// One failure does not lose the rest: a Minecraft version Fabric has no answer
        /// for keeps whatever target it already had, so re-checking on a flaky
        /// connection cannot quietly empty the list.
        /// </remarks>
        public static async Task<(Listing Policy, List<string> Notes)> ResolveLatestAsync(
            Listing existing, IProgress<string>? log, CancellationToken ct)
        {
            var notes = new List<string>();

            var clients = FabricClients();
            var servers = FabricServers();

            var versions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string mc in clients.Keys) versions.Add(mc);
            foreach (string mc in servers.Keys) versions.Add(mc);

            if (versions.Count == 0)
                notes.Add("Nothing on this machine has a Fabric loader, so there is nothing to pin.");

            var policy = new Listing
            {
                Enabled = existing.Enabled,
                By = FleetRoster.ThisMachine,
                Targets = new List<Target>()
            };

            foreach (string mc in versions)
            {
                ct.ThrowIfCancellationRequested();

                string? latest = null;
                try
                {
                    log?.Report($"Asking Fabric about Minecraft {mc}…");
                    latest = await FabricMeta.LatestLoaderAsync(mc, ct);
                }
                catch (Exception ex)
                {
                    notes.Add($"Minecraft {mc}: could not ask Fabric ({ex.Message}).");
                }

                // Keep the old pin rather than dropping the version entirely.
                var previous = existing.For(mc);
                if (string.IsNullOrWhiteSpace(latest))
                {
                    if (previous is not null)
                    {
                        policy.Targets.Add(previous);
                        notes.Add($"Minecraft {mc}: kept the existing pin of {previous.Loader}.");
                    }
                    continue;
                }

                policy.Targets.Add(new Target
                {
                    Minecraft = mc,
                    Loader    = latest!,
                    Client    = clients.ContainsKey(mc),
                    Server    = servers.ContainsKey(mc),
                    Pinned    = DateTime.UtcNow
                });

                string hadClient = clients.TryGetValue(mc, out var hc) ? hc : "none";
                string hadServer = servers.TryGetValue(mc, out var hs) ? hs : "none";

                notes.Add(
                    $"Minecraft {mc}: latest is {latest}" +
                    $" (this host has client {hadClient}, server {hadServer}).");
            }

            return (policy, notes);
        }

        // ── Storage ──────────────────────────────────────────────────

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public static string ToJson(Listing listing) => JsonSerializer.Serialize(listing, Options);

        public static Listing Parse(string json)
        {
            try { return JsonSerializer.Deserialize<Listing>(json) ?? new Listing(); }
            catch (JsonException) { return new Listing(); }
        }

        /// <summary>
        /// The host's policy. Unreadable reads as empty and therefore disabled — a
        /// corrupt file must not be able to start installing loaders.
        /// </summary>
        public static Listing LoadLocal()
        {
            try
            {
                return File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath)) : new Listing();
            }
            catch (Exception) { return new Listing(); }
        }

        public static bool SaveLocal(Listing listing) => Save(FilePath, listing);

        public static Listing LoadCache()
        {
            try
            {
                return File.Exists(CachePath) ? Parse(File.ReadAllText(CachePath)) : new Listing();
            }
            catch (Exception) { return new Listing(); }
        }

        public static bool SaveCache(Listing listing) => Save(CachePath, listing);

        private static bool Save(string path, Listing listing)
        {
            try
            {
                string temp = path + ".tmp";
                File.WriteAllText(temp, ToJson(listing));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>A line for the Admin tab, and for the sidebar on a client.</summary>
        public static string Summarise(Listing policy)
        {
            if (policy.Targets.Count == 0)
                return "No loader pinned yet. Press CHECK FABRIC to find the latest.";

            string what = string.Join(", ", policy.Targets.Select(t => $"{t.Minecraft} -> {t.Loader}"));
            string when = policy.Targets.Max(t => t.Pinned).ToLocalTime()
                                .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            return policy.Enabled
                ? $"Keeping everyone on {what}. Pinned {when}."
                : $"Turned off. Would keep everyone on {what}. Pinned {when}.";
        }
    }
}
