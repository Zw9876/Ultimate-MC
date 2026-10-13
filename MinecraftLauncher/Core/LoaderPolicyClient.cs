using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Bringing this machine's Fabric loaders up to what the host has pinned.
    /// </summary>
    /// <remarks>
    /// Runs quietly in the background shortly after the launcher opens, alongside the
    /// update check. Nothing here is allowed to be in anybody's way:
    ///
    /// <list type="bullet">
    /// <item>It never throws at the caller. Every failure is an outcome with a reason.</item>
    /// <item>It does nothing at all unless the host has turned the policy on <i>and</i>
    /// this machine is actually behind.</item>
    /// <item>It installs only for Minecraft versions this machine <b>already has a
    /// Fabric loader for</b>. Keeping Fabric current is the job; putting Fabric on a
    /// version somebody plays on Vanilla or Forge is not.</item>
    /// <item>It never downgrades. A machine ahead of the pin is left alone, because
    /// pushing somebody backwards over a stale pin is worse than a mismatch.</item>
    /// </list>
    ///
    /// <para><b>Why the loader comes from Fabric and not from the host.</b> The host
    /// pins the <i>version</i>, which is what keeps the fleet together; each machine
    /// then fetches that exact version itself. Serving the loader from the host as a
    /// pack would remove the need for client internet, and the machinery exists
    /// (<c>MAKE A PACK</c>), but it is only worth building if Fabric turns out to be
    /// unreachable from the machines — <c>tools\Test-DownloadHosts.ps1</c> answers
    /// that, and until it does this reports the failure plainly rather than pretending
    /// to have another route.</para>
    /// </remarks>
    public static class LoaderPolicyClient
    {
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(8);

        public enum Origin { Host, Cache, Nowhere }

        public sealed record Fetched(LoaderPolicy.Listing Policy, Origin From);

        /// <summary>What one install attempt did.</summary>
        public sealed record Outcome(LoaderPolicy.Behind Item, bool Installed, string Detail);

        /// <summary>
        /// Gets the host's policy, falling back to the last one this machine was told.
        /// </summary>
        public static async Task<Fetched> FetchAsync(
            string? hostAddress, CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(hostAddress))
            {
                try
                {
                    using var http = new HttpClient { Timeout = FetchTimeout };
                    string json = await http.GetStringAsync(
                        $"http://{hostAddress}{LoaderPolicy.Endpoint}", ct);

                    var policy = LoaderPolicy.Parse(json);
                    LoaderPolicy.SaveCache(policy);
                    return new Fetched(policy, Origin.Host);
                }
                catch (Exception)
                {
                    // An older host has no such endpoint, and a host that is not up is
                    // the normal case. Neither is worth telling anybody about.
                }
            }

            var cached = LoaderPolicy.LoadCache();
            return new Fetched(cached,
                cached.Targets.Count > 0 ? Origin.Cache : Origin.Nowhere);
        }

        /// <summary>
        /// Installs whatever this machine is behind on. Returns an empty list when
        /// there is nothing to do, which is the normal case.
        /// </summary>
        /// <remarks>
        /// One failure never stops the others: a machine with two versions behind is
        /// better off with one of them fixed, and the caller reports what did not make
        /// it. A server install is a separate code path from a client one because a
        /// server keeps its loader inside <c>server.jar</c>.
        /// </remarks>
        public static async Task<List<Outcome>> CatchUpAsync(
            LoaderPolicy.Listing policy, IProgress<string>? log, CancellationToken ct = default)
        {
            var done = new List<Outcome>();

            foreach (var item in LoaderPolicy.WhatIsBehind(policy))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    log?.Report($"Fabric loader {item.Want} for {item.Minecraft}…");

                    if (item.Server)
                        await FabricServerLoader.InstallOnlineAsync(item.Minecraft, item.Want, log, ct);
                    else
                        await FabricLoaderUpdate.InstallOnlineAsync(item.Minecraft, item.Want, log, ct);

                    done.Add(new Outcome(item, true, $"installed {item.Want}"));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    done.Add(new Outcome(item, false, ex.Message));
                }
            }

            return done;
        }

        /// <summary>
        /// The whole thing: ask the host, install what is behind, say what happened.
        /// Null when there was nothing to do.
        /// </summary>
        public static async Task<string?> SyncAsync(
            string? hostAddress, IProgress<string>? log, CancellationToken ct = default)
        {
            try
            {
                var fetched = await FetchAsync(hostAddress, ct);
                if (!fetched.Policy.Enabled) return null;

                var behind = LoaderPolicy.WhatIsBehind(fetched.Policy);
                if (behind.Count == 0) return null;

                log?.Report($"Updating {behind.Count} Fabric loader(s)…");

                var done = await CatchUpAsync(fetched.Policy, log, ct);

                var installed = done.FindAll(o => o.Installed);
                var failed = done.FindAll(o => !o.Installed);

                if (failed.Count == 0)
                    return $"Fabric loader updated: " +
                           string.Join(", ", installed.ConvertAll(o => o.Item.Describe()));

                string trouble = string.Join("; ",
                    failed.ConvertAll(o => $"{o.Item.Describe()} ({o.Detail})"));

                return installed.Count > 0
                    ? $"Fabric loader updated for {installed.Count}, but not all: {trouble}"
                    : $"Could not update the Fabric loader: {trouble}";
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                // A background convenience must never surface as a fault.
                return $"Could not check the Fabric loader: {ex.Message}";
            }
        }
    }
}
