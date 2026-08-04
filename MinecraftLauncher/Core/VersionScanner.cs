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

            if (Directory.GetFiles(versionsSub, "forge*.json").Any())
                loaders.Add("FORGE");

            return loaders;
        }
    }
}
