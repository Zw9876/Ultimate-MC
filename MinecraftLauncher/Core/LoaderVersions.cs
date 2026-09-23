using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Which loader version is actually installed, and whether a mod can run on it.
    /// </summary>
    /// <remarks>
    /// Filtering mods by Minecraft version and loader family is not enough. A mod also
    /// declares the loader version it needs — <c>"fabricloader": "&gt;=0.18.4"</c>, or
    /// <c>versionRange="[46,)"</c> for Forge and NeoForge — and Modrinth's API does not
    /// expose that at all. Installing a mod that wants a newer loader than is present
    /// produces a crash at startup whose message is about a missing dependency rather
    /// than about the loader, so it is worth catching here.
    ///
    /// Everything is read off disk, so it works with no internet.
    /// </remarks>
    public static class LoaderVersions
    {
        // versions/<mc>/versions/fabric-loader-0.19.3-26.1.2.json
        private static readonly Regex RxFabricProfile =
            new(@"^fabric-loader-(?<loader>.+?)-(?<mc>.+)$", RegexOptions.Compiled);

        // versions/<mc>/versions/1.20.1-forge-47.4.10.json
        private static readonly Regex RxForgeProfile =
            new(@"^(?<mc>.+?)-forge-(?<loader>.+)$", RegexOptions.Compiled);

        // versions/<mc>/versions/neoforge-21.1.72.json, or 1.21.1-neoforge-21.1.72
        private static readonly Regex RxNeoForgeProfile =
            new(@"neoforge-(?<loader>[0-9][^-]*(?:-[A-Za-z][^-]*)?)$", RegexOptions.Compiled);

        /// <summary>The installed loader version, and where that was read from.</summary>
        public sealed record Installed(string LoaderType, string? Version, string Source)
        {
            public bool Known => !string.IsNullOrWhiteSpace(Version);

            public string Describe() => Known
                ? $"{LoaderType} loader {Version}"
                : $"{LoaderType} loader version unknown";
        }

        /// <summary>
        /// Reads the installed loader version for a client version or a server folder.
        /// Returns a result with a null version when it cannot be determined, which is
        /// treated everywhere as "do not warn".
        /// </summary>
        public static Installed Detect(string version, bool server, string? loaderType)
        {
            string type = loaderType ?? "";

            try
            {
                return type.ToUpperInvariant() switch
                {
                    "FABRIC"   => server ? FabricServer(version, type) : FabricClient(version, type),
                    "FORGE"    => server ? ForgeServer(version, type)  : ForgeClient(version, type),
                    "NEOFORGE" => server ? NeoForgeServer(version, type) : NeoForgeClient(version, type),
                    _ => new Installed(type, null, "not a modded loader")
                };
            }
            catch
            {
                return new Installed(type, null, "could not be read");
            }
        }

        private static Installed FabricClient(string version, string loaderType)
        {
            string dir = Path.Combine(Paths.VersionDir(version), "versions");
            if (!Directory.Exists(dir)) return new Installed(loaderType, null, "no version folder");

            // The newest, matching what the launcher will actually run.
            if (FabricLoaderUpdate.NewestProfile(version) is string file)
            {
                var m = RxFabricProfile.Match(Path.GetFileNameWithoutExtension(file));
                if (m.Success) return new Installed(loaderType, m.Groups["loader"].Value, Path.GetFileName(file));
            }

            return new Installed(loaderType, null, "no fabric profile");
        }

        /// <summary>
        /// Fabric servers keep each loader they have ever had under
        /// <c>libraries/net/fabricmc/fabric-loader/</c>, so an upgraded server has
        /// several. The newest is the one in use.
        /// </summary>
        private static Installed FabricServer(string version, string loaderType)
        {
            string dir = Path.Combine(Paths.ServerDir(loaderType, version),
                                      "libraries", "net", "fabricmc", "fabric-loader");

            string? newest = NewestChildName(dir);
            return new Installed(loaderType, newest,
                newest is null ? "no fabric-loader library" : "libraries/net/fabricmc/fabric-loader");
        }

        private static Installed ForgeClient(string version, string loaderType)
        {
            string dir = Path.Combine(Paths.VersionDir(version), "versions");
            if (!Directory.Exists(dir)) return new Installed(loaderType, null, "no version folder");

            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                if (!VersionScanner.IsForgeProfile(file)) continue;

                var m = RxForgeProfile.Match(Path.GetFileNameWithoutExtension(file));
                if (m.Success) return new Installed(loaderType, m.Groups["loader"].Value, Path.GetFileName(file));
            }

            return new Installed(loaderType, null, "no forge profile");
        }

        private static Installed ForgeServer(string version, string loaderType)
        {
            // libraries/net/minecraftforge/forge/1.20.1-47.4.10
            string dir = Path.Combine(Paths.ServerDir(loaderType, version),
                                      "libraries", "net", "minecraftforge", "forge");

            string? newest = NewestChildName(dir);
            if (newest is null) return new Installed(loaderType, null, "no forge library");

            // The folder is "<mc>-<forge>"; only the second half is the loader version.
            int dash = newest.IndexOf('-');
            string loaderVersion = dash >= 0 ? newest[(dash + 1)..] : newest;

            return new Installed(loaderType, loaderVersion, "libraries/net/minecraftforge/forge");
        }

        private static Installed NeoForgeClient(string version, string loaderType)
        {
            string dir = Path.Combine(Paths.VersionDir(version), "versions");
            if (!Directory.Exists(dir)) return new Installed(loaderType, null, "no version folder");

            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                if (!VersionScanner.IsNeoForgeProfile(file)) continue;

                var m = RxNeoForgeProfile.Match(Path.GetFileNameWithoutExtension(file));
                if (m.Success) return new Installed(loaderType, m.Groups["loader"].Value, Path.GetFileName(file));
            }

            return new Installed(loaderType, null, "no neoforge profile");
        }

        private static Installed NeoForgeServer(string version, string loaderType)
        {
            string dir = Path.Combine(Paths.ServerDir(loaderType, version),
                                      "libraries", "net", "neoforged", "neoforge");

            string? newest = NewestChildName(dir);
            return new Installed(loaderType, newest,
                newest is null ? "no neoforge library" : "libraries/net/neoforged/neoforge");
        }

        /// <summary>The highest-versioned subfolder name, or null when there is none.</summary>
        private static string? NewestChildName(string dir)
        {
            if (!Directory.Exists(dir)) return null;

            return Directory.GetDirectories(dir)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderByDescending(n => n!, MavenVersion.Comparer)
                .FirstOrDefault();
        }
    }
}
