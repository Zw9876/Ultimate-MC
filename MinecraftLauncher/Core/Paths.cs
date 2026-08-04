using System;
using System.IO;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Central path resolver. Everything is relative to the directory the
    /// executable lives in, so the launcher stays fully portable.
    /// </summary>
    public static class Paths
    {
        private static string? _baseDir;

        /// <summary>The folder the launcher executable sits in.</summary>
        public static string BaseDir
        {
            get
            {
                if (_baseDir != null) return _baseDir;

                // AppContext.BaseDirectory is correct for a published single-file
                // exe as well as a normal build. Fall back defensively.
                string dir = AppContext.BaseDirectory;
                if (string.IsNullOrWhiteSpace(dir))
                    dir = Directory.GetCurrentDirectory();

                _baseDir = dir.TrimEnd(Path.DirectorySeparatorChar);
                return _baseDir;
            }
        }

        public static string Versions => Path.Combine(BaseDir, "versions");
        public static string Servers  => Path.Combine(BaseDir, "servers");
        public static string Runtime  => Path.Combine(BaseDir, "runtime");
        public static string Skins    => Path.Combine(BaseDir, "skins");
        public static string ConfigFile => Path.Combine(BaseDir, "config.txt");

        public static string VersionDir(string version) => Path.Combine(Versions, version);

        public static string ServerDir(string loaderType, string version) =>
            Path.Combine(Servers, $"{loaderType}-{version}".ToLowerInvariant());

        /// <summary>Finds the first available bundled Java, newest first.</summary>
        public static string? FindJava(int? preferred = null)
        {
            // If a specific major version is requested, try it first.
            if (preferred.HasValue)
            {
                string p = Path.Combine(Runtime, preferred.Value.ToString(), "bin", "java.exe");
                if (File.Exists(p)) return p;
            }

            foreach (var ver in new[] { "25", "23", "21", "17", "8" })
            {
                string p = Path.Combine(Runtime, ver, "bin", "java.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }
}
