using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Asking the host whether this computer may start a game.
    /// </summary>
    /// <remarks>
    /// Runs between somebody pressing PLAY and their game starting, so it is on a short
    /// timeout and never throws at the caller.
    ///
    /// <para><b>Unreachable host means the cached answer, not "allowed".</b> Those are
    /// different, and the difference is the whole feature: if an unreachable host meant
    /// allowed, unplugging the network would defeat it. Failing closed is not an option
    /// either, because that would stop everybody playing single-player whenever the host
    /// is off. So the last answer is remembered and honoured until a host says
    /// otherwise.</para>
    ///
    /// <para>This takes a host address rather than finding one, because PLAY already pays
    /// a 1500 ms UDP discovery for the required-mods check and another for the skin
    /// server (handoff section 10, item 7). Adding a third would be the most noticeable
    /// thing about this feature.</para>
    /// </remarks>
    public static class PlayBlocksClient
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        /// <summary>Where the answer came from, which decides what to tell the person.</summary>
        public enum Origin { Host, Cache, Nowhere }

        public sealed record Verdict(PlayBlocks.Block? Block, Origin From)
        {
            public bool Blocked => Block is not null;

            public string Describe() => From switch
            {
                Origin.Host  => "checked with the host",
                Origin.Cache => "from the last answer this machine was given",
                _            => "no host and nothing remembered"
            };
        }

        /// <summary>
        /// Whether this machine may play.
        /// </summary>
        /// <param name="hostAddress">
        /// A host already discovered by the caller, or null to use only what is cached.
        /// </param>
        public static async Task<Verdict> CheckAsync(
            string? hostAddress, CancellationToken ct = default)
        {
            // The host's own launcher is never blocked, and it also holds the master
            // list rather than a cache, so there is nothing to ask anybody.
            if (RequiredMods.AdminEnabled)
                return new Verdict(null, Origin.Host);

            var now = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(hostAddress))
            {
                try
                {
                    using var http = new HttpClient { Timeout = Timeout };
                    string json = await http.GetStringAsync(
                        $"http://{hostAddress}{PlayBlocks.Endpoint}", ct);

                    var listing = PlayBlocks.Parse(json);

                    // Written down before the verdict is returned, so a machine that is
                    // switched off straight afterwards still knows next time.
                    PlayBlocks.SaveCache(listing);

                    return new Verdict(PlayBlocks.BlockOnThisMachine(listing, now), Origin.Host);
                }
                catch (Exception)
                {
                    // A host on an older build has no such endpoint and a host that is
                    // not up is the normal case. Fall through to what we remember.
                }
            }

            var cached = PlayBlocks.LoadCache();
            var block = PlayBlocks.BlockOnThisMachine(cached, now);

            return block is not null
                ? new Verdict(block, Origin.Cache)
                : new Verdict(null, cached.Blocks.Count > 0 ? Origin.Cache : Origin.Nowhere);
        }
    }
}
