using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinecraftLauncher.Core
{
    public static class VersionScanner
    {
        /// <summary>
        /// Returns the installed version folder names (each folder under versions/
        /// that contains a versions/&lt;name&gt;.json).
        /// </summary>
        public static List<string> InstalledVersions()
        {
            var result = new List<string>();
            if (!Directory.Exists(Paths.Versions)) return result;

            foreach (var dir in Directory.GetDirectories(Paths.Versions))
            {
                string name = Path.GetFileName(dir);
                string vjson = Path.Combine(dir, "versions", $"{name}.json");
                if (File.Exists(vjson))
                    result.Add(name);
            }
            result.Sort();
            return result;
        }

        /// <summary>Loader prefixes used when naming a server folder.</summary>
        private static readonly string[] LoaderPrefixes =
            { "vanilla", "fabric", "neoforge", "forge", "paper", "purpur" };

        /// <summary>
        /// Versions that already have a server folder, read back out of the
        /// <c>servers/&lt;loader&gt;-&lt;version&gt;</c> naming.
        /// </summary>
        public static List<string> ServerVersions()
        {
            var found = new List<string>();
            if (!Directory.Exists(Paths.Servers)) return found;

            foreach (string dir in Directory.GetDirectories(Paths.Servers))
            {
                string name = Path.GetFileName(dir);

                // "neoforge" has to be tested before "forge", or a NeoForge folder
                // would be read as Forge and its version come out as "e-1.21.1".
                foreach (string loader in LoaderPrefixes)
                {
                    if (!name.StartsWith(loader + "-", StringComparison.OrdinalIgnoreCase)) continue;

                    string version = name[(loader.Length + 1)..];
                    if (version.Length > 0 && !found.Contains(version, StringComparer.OrdinalIgnoreCase))
                        found.Add(version);
                    break;
                }
            }

            found.Sort(MavenVersion.Comparer);
            return found;
        }

        /// <summary>
        /// Versions offered for hosting and for server-side mods: everything with a
        /// client installed, plus everything that already has a server folder.
        /// </summary>
        /// <remarks>
        /// These lists differ now that game files are not bundled — a machine can
        /// have servers set up with no client installed at all, and only listing
        /// client installs left the Server tab empty.
        /// </remarks>
        public static List<string> HostableVersions()
        {
            var all = new List<string>(InstalledVersions());

            foreach (string v in ServerVersions())
                if (!all.Contains(v, StringComparer.OrdinalIgnoreCase))
                    all.Add(v);

            all.Sort(MavenVersion.Comparer);
            return all;
        }

        /// <summary>
        /// Recognises a NeoForge profile. Its installer names the file
        /// <c>neoforge-&lt;build&gt;.json</c>.
        /// </summary>
        public static bool IsNeoForgeProfile(string path) =>
            Path.GetFileName(path).Contains("neoforge", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Recognises a Forge profile.
        /// </summary>
        /// <remarks>
        /// Matching on a "forge*" prefix does not work: Forge names its profile
        /// <c>&lt;mcversion&gt;-forge-&lt;build&gt;.json</c> — game version first — so the
        /// name starts with a digit. Older files written by the PowerShell launcher
        /// used <c>forge-&lt;mcversion&gt;-&lt;build&gt;.json</c> instead, so both spellings
        /// have to be accepted, while excluding NeoForge which also contains "forge".
        /// </remarks>
        public static bool IsForgeProfile(string path) =>
            Path.GetFileName(path).Contains("forge", StringComparison.OrdinalIgnoreCase) &&
            !IsNeoForgeProfile(path);

        /// <summary>
        /// Which loaders appear installed for a version, based on the presence of
        /// loader JSON files in versions/&lt;version&gt;/versions/.
        /// Always includes VANILLA if the base version json exists.
        /// </summary>
        public static List<string> AvailableLoaders(string version)
        {
            var loaders = new List<string>();
            string versionsSub = Path.Combine(Paths.VersionDir(version), "versions");
            if (!Directory.Exists(versionsSub)) return loaders;

            if (File.Exists(Path.Combine(versionsSub, $"{version}.json")))
                loaders.Add("VANILLA");

            if (Directory.GetFiles(versionsSub, "fabric-loader*.json").Any())
                loaders.Add("FABRIC");

            var profiles = Directory.GetFiles(versionsSub, "*.json");

            if (profiles.Any(IsNeoForgeProfile)) loaders.Add("NEOFORGE");
            if (profiles.Any(IsForgeProfile))    loaders.Add("FORGE");

            return loaders;
        }
    }
}
