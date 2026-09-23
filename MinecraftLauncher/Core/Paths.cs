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

        /// <summary>
        /// Makes every path resolve against this folder instead of the executable's own.
        /// </summary>
        /// <remarks>
        /// Exists for the test project, which runs from its own bin directory and needs
        /// Core to read the real install — and, for a few checks, a throwaway copy of it.
        /// The launcher never sets this; it is null in every shipped code path.
        ///
        /// The alternative was junctioning folders next to the test binary, which put a
        /// link to the real <c>versions/</c> inside <c>bin/</c>, where a routine
        /// <c>Remove-Item -Recurse</c> or <c>dotnet clean</c> could follow it and delete
        /// gigabytes of somebody's Minecraft install. A settable property is worth more
        /// than that risk.
        /// </remarks>
        public static string? BaseDirOverride { get; set; }

        /// <summary>The folder the launcher executable sits in.</summary>
        public static string BaseDir
        {
            get
            {
                if (BaseDirOverride is { Length: > 0 } overridden)
                    return overridden.TrimEnd(Path.DirectorySeparatorChar);

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

        /// <summary>Scratch space that can be deleted without losing anything.</summary>
        public static string Cache => Path.Combine(BaseDir, "cache");

        public static string VersionDir(string version) => Path.Combine(Versions, version);

        public static string ServerDir(string loaderType, string version) =>
            Path.Combine(Servers, $"{loaderType}-{version}".ToLowerInvariant());

        /// <summary>
        /// The Java major version a given Minecraft release needs.
        /// </summary>
        /// <remarks>
        /// Server installs have no version manifest to read (unlike the client), so
        /// this maps from the game version directly. Getting it wrong surfaces as
        /// <c>UnsupportedClassVersionError</c> at startup rather than anything
        /// obviously Java-related, so the ladder matters.
        /// </remarks>
        public static int JavaMajorForMinecraft(string mcVersion)
        {
            // Versions from the new scheme (26.x and later) are all current-JDK.
            if (!mcVersion.StartsWith("1.", StringComparison.Ordinal)) return 25;

            var parts = mcVersion[2..].Split('.');
            if (!int.TryParse(parts[0], out int minor)) return 21;

            if (minor >= 21) return 21;   // 1.21+
            if (minor >= 18) return 17;   // 1.18 – 1.20
            if (minor == 17) return 17;
            return 8;                     // 1.16 and older
        }

        /// <summary>Bundled Java suitable for running the given Minecraft version.</summary>
        public static string? FindJavaForMinecraft(string mcVersion) =>
            FindJava(JavaMajorForMinecraft(mcVersion));

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
