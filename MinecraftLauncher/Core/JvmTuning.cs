using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// JVM flags for running a Minecraft server, beyond just the heap size.
    /// </summary>
    /// <remarks>
    /// The launcher used to pass nothing but <c>-Xmx</c> and <c>-Xms</c>, which leaves
    /// the JVM on its default collector settings. On a modded server with a dozen or
    /// more players that shows up as periodic freezes: the heap fills, a stop-the-world
    /// collection runs for hundreds of milliseconds, and every player sees rubber-banding
    /// and delayed block breaking. It is not "the server is underpowered" — it is the
    /// garbage collector doing one big pause instead of many small ones.
    ///
    /// These are the widely used Aikar flags: G1 with a small, aggressively collected
    /// young generation and a 200 ms pause target, so collection happens constantly in
    /// short bursts rather than rarely in long ones. Verified to be accepted without
    /// warnings on the bundled Java 17, 21 and 25.
    /// </remarks>
    public static class JvmTuning
    {
        /// <summary>Above this heap size the flags need different proportions.</summary>
        private const int LargeHeapGb = 12;

        /// <summary>
        /// Garbage-collector flags for a server with <paramref name="heapGb"/> of heap.
        /// </summary>
        /// <remarks>
        /// Order matters: <c>-XX:+UnlockExperimentalVMOptions</c> has to come before the
        /// experimental G1 options or the JVM refuses to start.
        /// </remarks>
        public static IReadOnlyList<string> GcFlags(int heapGb)
        {
            bool large = heapGb >= LargeHeapGb;

            return new[]
            {
                "-XX:+UseG1GC",
                "-XX:+ParallelRefProcEnabled",
                "-XX:MaxGCPauseMillis=200",
                "-XX:+UnlockExperimentalVMOptions",
                "-XX:+DisableExplicitGC",

                // Commits the whole heap up front. Costs a few seconds of startup and
                // holds the RAM for good, which is what you want on a machine that is
                // hosting: no page-fault stalls mid-session.
                "-XX:+AlwaysPreTouch",

                $"-XX:G1NewSizePercent={(large ? 40 : 30)}",
                $"-XX:G1MaxNewSizePercent={(large ? 50 : 40)}",
                $"-XX:G1HeapRegionSize={(large ? 16 : 8)}M",
                $"-XX:G1ReservePercent={(large ? 15 : 20)}",
                "-XX:G1HeapWastePercent=5",
                "-XX:G1MixedGCCountTarget=4",
                $"-XX:InitiatingHeapOccupancyPercent={(large ? 20 : 15)}",
                "-XX:G1MixedGCLiveThresholdPercent=90",
                "-XX:G1RSetUpdatingPauseTimePercent=5",
                "-XX:SurvivorRatio=32",
                "-XX:+PerfDisableSharedMem",
                "-XX:MaxTenuringThreshold=1",
            };
        }

        /// <summary>
        /// Garbage-collector flags for the game client.
        /// </summary>
        /// <remarks>
        /// Not the same job as the server. A server wants throughput and can absorb a
        /// 200 ms pause; a client is drawing frames, and a 200 ms pause is a visible
        /// hitch. So the pause target is lower, the young generation is collected more
        /// eagerly, and concurrent marking is spread thinner to stay out of the way of
        /// the render thread.
        ///
        /// Two flags that appear in most published "optimised Minecraft flags" lists are
        /// deliberately absent: <c>G1ConcRSHotCardLimit</c> and
        /// <c>G1ConcRefinementServiceIntervalMillis</c>. Both were removed in Java 21 —
        /// a warning there, but *unrecognised* in Java 25, where the JVM simply refuses
        /// to start. Since the client runs on whichever Java the version needs, copying
        /// them in would have stopped the game launching outright.
        /// </remarks>
        public static IReadOnlyList<string> ClientGcFlags(int heapGb) => new[]
        {
            "-XX:+UseG1GC",
            "-XX:+ParallelRefProcEnabled",

            // Lower than the server's 200 ms: this is a frame budget, not a tick budget.
            "-XX:MaxGCPauseMillis=130",

            "-XX:+UnlockExperimentalVMOptions",
            "-XX:+DisableExplicitGC",
            "-XX:+AlwaysPreTouch",
            "-XX:G1NewSizePercent=28",
            "-XX:G1HeapRegionSize=16M",
            "-XX:G1ReservePercent=20",
            "-XX:G1MixedGCCountTarget=3",
            "-XX:InitiatingHeapOccupancyPercent=10",
            "-XX:G1MixedGCLiveThresholdPercent=90",

            // Zero, not the server's 5: remembered-set work is pushed off the pause and
            // onto the concurrent threads, which a client has spare and a server does not.
            "-XX:G1RSetUpdatingPauseTimePercent=0",

            "-XX:SurvivorRatio=32",
            "-XX:MaxTenuringThreshold=1",
            "-XX:+PerfDisableSharedMem",
            "-XX:G1SATBBufferEnqueueingThresholdPercent=30",
            "-XX:G1ConcMarkStepDurationMillis=5",
        };

        /// <summary>
        /// A sensible client heap: a quarter of the machine's RAM, between 3 and 6 GB.
        /// </summary>
        /// <remarks>
        /// Much smaller than the server's share, and intentionally so. Over-allocating a
        /// Minecraft client is one of the most common mistakes there is: the extra heap
        /// is never touched, but every collection has more ground to cover, so the
        /// stutter people were trying to fix gets worse. Even heavily modded packs are
        /// comfortable in 6 GB.
        /// </remarks>
        public static int RecommendedClientHeapGb(int maximum = 16)
        {
            int ram = TotalRamGb;
            if (ram <= 0) return 4;
            return Math.Clamp(ram / 4, 3, Math.Min(6, maximum));
        }

        /// <summary>Physical RAM in this machine, in whole GB. 0 if it cannot be read.</summary>
        public static int TotalRamGb
        {
            get
            {
                try
                {
                    long bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                    return bytes <= 0 ? 0 : (int)(bytes / (1024L * 1024 * 1024));
                }
                catch { return 0; }
            }
        }

        /// <summary>
        /// A sensible default heap for this machine: about half the RAM, capped at
        /// 8 GB, or 12 GB on a machine with 32 GB or more.
        /// </summary>
        /// <remarks>
        /// Half leaves room for Windows and for someone playing on the same machine.
        /// The ceiling is deliberate — a bigger heap does not make a Minecraft server
        /// faster, it just gives the collector more to walk, and the pauses grow with
        /// it. More memory only helps once the server is genuinely running out.
        /// </remarks>
        public static int RecommendedHeapGb(int maximum = 16)
        {
            int ram = TotalRamGb;
            if (ram <= 0) return 4;

            // A workstation with plenty of RAM can afford more, but the ceiling stays
            // low on purpose. Past a point the collector simply has more to walk.
            int ceiling = ram >= 32 ? 12 : 8;
            return Math.Clamp(ram / 2, 2, Math.Min(ceiling, maximum));
        }

        /// <summary>
        /// The largest heap worth offering on this machine: everything except a 4 GB
        /// reserve for Windows, and never beyond 32 GB.
        /// </summary>
        /// <remarks>
        /// A fixed 16 GB limit was wrong in both directions — unreachable on a 16 GB
        /// machine, where `-XX:+AlwaysPreTouch` would try to commit the entire system
        /// memory, and needlessly low on a workstation with 64 GB or more. The 32 GB
        /// ceiling is there because a Minecraft server that cannot manage on 32 GB has
        /// a mod leaking memory, and handing it more only makes the collector's job
        /// bigger.
        /// </remarks>
        public static int MaxHeapGb
        {
            get
            {
                int ram = TotalRamGb;
                return ram <= 0 ? 16 : Math.Clamp(ram - 4, 4, 32);
            }
        }

        /// <summary>Logical processors visible to this machine.</summary>
        public static int LogicalCores => Environment.ProcessorCount;

        /// <summary>The CPU model string, or null if it cannot be read.</summary>
        public static string? CpuName
        {
            get
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    return (key?.GetValue("ProcessorNameString") as string)?.Trim();
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// One line describing what this machine has, for the Server tab.
        /// </summary>
        /// <remarks>
        /// Worth showing because the answer differs on every machine and nobody can
        /// check them all. It also makes the memory default explainable rather than
        /// looking like an arbitrary number.
        /// </remarks>
        public static string DescribeMachine()
        {
            string cpu = CpuName ?? "unknown CPU";
            int ram = TotalRamGb;
            string memory = ram > 0 ? $"{ram} GB RAM" : "unknown RAM";
            return $"{cpu} — {LogicalCores} logical cores, {memory}";
        }
    }
}
