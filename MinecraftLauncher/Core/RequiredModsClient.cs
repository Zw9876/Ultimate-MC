using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Getting the host's required-mods list, and installing what is missing.
    /// </summary>
    /// <remarks>
    /// Uses the same host discovery as the launcher update — the config override first,
    /// then UDP discovery — so there is one answer on a machine for "who is hosting"
    /// rather than two that can disagree.
    ///
    /// On the host itself discovery finds nothing, because it deliberately prefers a
    /// *remote* skin server and its own reads as local. That falls through to the local
    /// list, which on the host is the master copy — so the host checks itself against
    /// its own list without a special case.
    ///
    /// Nothing here throws at the caller. Every failure comes back as an outcome with a
    /// reason, because this runs between somebody pressing PLAY and their game starting,
    /// and the game has to start either way.
    /// </remarks>
    public static class RequiredModsClient
    {
        private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(8);

        /// <summary>Where the list came from, which is worth telling the user.</summary>
        public enum Origin { Host, Local, Nowhere }

        public sealed record Fetched(RequiredMods.Listing Listing, Origin From, string? HostAddress)
        {
            public string Describe() => From switch
            {
                Origin.Host    => $"from the host at {HostAddress}",
                Origin.Local   => "from this machine's own list",
                _              => "no list found"
            };
        }

        /// <summary>
        /// Finds the list: the host's if one is reachable, otherwise this machine's own.
        /// </summary>
        public static async Task<Fetched> FetchAsync(
            AppConfig config, IProgress<string>? log, CancellationToken ct)
        {
            string? host = config.SkinServer?.Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                try { host = (await Task.Run(SkinDiscovery.FindRemote, ct))?.Address; }
                catch (Exception) { host = null; }
            }

            if (!string.IsNullOrWhiteSpace(host))
            {
                try
                {
                    using var http = new HttpClient { Timeout = ListTimeout };
                    string json = await http.GetStringAsync(
                        $"http://{host}{RequiredMods.Endpoint}", ct);

                    var listing = RequiredMods.Parse(json);
                    log?.Report($"Required mods: {listing.Entries.Count} listed by the host at {host}.");
                    return new Fetched(listing, Origin.Host, host);
                }
                catch (Exception)
                {
                    // A host on an older build has no such endpoint, and a host that is
                    // simply not up is the normal case. Neither is worth a warning.
                    log?.Report("Required mods: the host had no list to offer.");
                }
            }

            var local = RequiredMods.LoadLocal();
            return local.Entries.Count > 0
                ? new Fetched(local, Origin.Local, null)
                : new Fetched(local, Origin.Nowhere, null);
        }

        /// <summary>What happened to one entry.</summary>
        public sealed record Outcome(RequiredMods.Entry Entry, bool Installed, string Detail);

        /// <summary>
        /// Installs the given entries into a mods folder.
        /// </summary>
        /// <remarks>
        /// One failure never stops the others: a folder is more useful with four of five
        /// required mods than with none, and the caller reports what did not make it.
        /// </remarks>
        public static async Task<List<Outcome>> InstallAsync(
            IEnumerable<RequiredMods.Entry> entries, string modsFolder, string? hostAddress,
            IProgress<string>? log, CancellationToken ct)
        {
            var done = new List<Outcome>();
            Directory.CreateDirectory(modsFolder);

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                log?.Report($"Getting {entry.Display}...");

                try
                {
                    done.Add(entry.From == RequiredMods.Source.Modrinth
                        ? await FromModrinthAsync(entry, modsFolder, log, ct)
                        : await FromHostAsync(entry, modsFolder, hostAddress, log, ct));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    done.Add(new Outcome(entry, false, ex.Message));
                }
            }

            return done;
        }

        private static async Task<Outcome> FromModrinthAsync(
            RequiredMods.Entry entry, string modsFolder, IProgress<string>? log, CancellationToken ct)
        {
            ModrinthApi.ModVersion? version = null;

            // A pinned version id is the normal case and the reason the format has one:
            // every machine ends up with the same build rather than whatever was newest
            // the day it happened to ask.
            if (!string.IsNullOrWhiteSpace(entry.VersionId))
            {
                version = await ModrinthApi.VersionAsync(entry.VersionId!, ct);
                if (version is null)
                    return new Outcome(entry, false, $"Modrinth has no version {entry.VersionId}");
            }
            else
            {
                var all = await ModrinthApi.VersionsAsync(entry.Project!, entry.Minecraft, entry.Loader, ct);
                version = ModrinthApi.BestOf(all);
                if (version is null)
                    return new Outcome(entry, false,
                        $"nothing on Modrinth fits {entry.Loader} on {entry.Minecraft}");
            }

            if (version.File is null)
                return new Outcome(entry, false, "that version has no file to download");

            // The same install path the Mods tab uses, so it is SHA-1 verified and an
            // older build of the same mod is turned off rather than left to clash.
            var result = await ModrinthApi.InstallAsync(version.File, modsFolder, ct);

            return result.Status == ModrinthApi.InstallStatus.Failed
                ? new Outcome(entry, false, result.Detail ?? "download failed")
                : new Outcome(entry, true, version.VersionNumber);
        }

        private static async Task<Outcome> FromHostAsync(
            RequiredMods.Entry entry, string modsFolder, string? hostAddress,
            IProgress<string>? log, CancellationToken ct)
        {
            // Validated the same way the server validates it, so a list entry cannot
            // name somewhere else on this machine to write to.
            string name = entry.File!;
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                name.Contains("..", StringComparison.Ordinal) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                return new Outcome(entry, false, $"that file name is not one we will write ({name})");

            string target = Path.Combine(modsFolder, name);

            if (File.Exists(target) || File.Exists(target + ".disabled"))
                return new Outcome(entry, true, "already there");

            // On the host itself there is no remote host to fetch from — discovery
            // prefers a *remote* server and its own reads as local — but the jar is
            // sitting in required-mods\ right here. Without this the one machine that
            // publishes the list is the one machine that cannot satisfy it, which is
            // absurd given the host plays too.
            string? beside = RequiredMods.JarPathOf(name);
            bool ok;

            if (beside is not null)
            {
                log?.Report($"Taking {name} from this machine's own required-mods folder.");
                File.Copy(beside, target, overwrite: true);

                // Hashed even here. A local copy rules out the network, not a list that
                // has drifted from the jar beside it.
                ok = Verify(target, entry.Sha1!);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(hostAddress))
                    return new Outcome(entry, false,
                        "only the host has this one, and no host is reachable");

                string url = $"http://{hostAddress}/launcher/required-mod/{Uri.EscapeDataString(name)}";

                // The hash is the point. This jar comes over plain HTTP from a machine
                // on the LAN, so what arrives is checked against what the list said
                // before it is left in a folder the game will load code from.
                ok = await Downloader.GetVerifiedAsync(url, target, entry.Sha1!, log, ct);
            }

            if (!ok)
            {
                try { File.Delete(target); } catch { }
                return new Outcome(entry, false, "what arrived did not match the expected hash");
            }

            // A downgraded mod is a repack of somebody else's, so it carries the
            // original's mod id. If the upstream build is also sitting here the loader
            // sees one mod twice and refuses to start, naming the mod rather than the
            // files. Turn the others off — renamed, not deleted, like everywhere else.
            int supersededCount = 0;
            foreach (string other in ModInspector.OtherCopiesOf(target, modsFolder))
            {
                string otherName = Path.GetFileName(other);
                if (otherName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) continue;

                if (ModManager.SetEnabled(modsFolder, otherName, false))
                {
                    supersededCount++;
                    log?.Report($"Turned off {otherName}, which is the same mod.");
                }
            }

            string where = beside is not null ? "from this machine" : "from the host";

            return new Outcome(entry, true,
                supersededCount == 0
                    ? where
                    : $"{where}, replacing {supersededCount} other copy/copies");
        }

        private static bool Verify(string path, string expectedSha1)
        {
            try
            {
                using var stream = File.OpenRead(path);
                string actual = Convert.ToHexString(
                    System.Security.Cryptography.SHA1.HashData(stream));
                return actual.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
