using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MinecraftLauncher.Core
{
    /// <summary>One file belonging to the launcher, as advertised or compared.</summary>
    public sealed class PackageFile
    {
        [JsonPropertyName("name")]   public string Name   { get; set; } = "";
        [JsonPropertyName("size")]   public long   Size   { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    }

    /// <summary>What a host advertises about the launcher build it is running.</summary>
    public sealed class LauncherManifest
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "0.0.0.0";
        [JsonPropertyName("files")]   public List<PackageFile> Files { get; set; } = new();

        /// <summary>
        /// Set to "gzip" by a host that can send files compressed. Absent from older
        /// hosts, which is exactly why the client must be told rather than just asking:
        /// a host that does not understand the request would send raw bytes to a client
        /// expecting compressed ones, and the checksum would fail for no good reason.
        /// </summary>
        [JsonPropertyName("compression")] public string? Compression { get; set; }

        public bool SupportsGzip =>
            string.Equals(Compression, "gzip", StringComparison.OrdinalIgnoreCase);

        public Version ParsedVersion =>
            System.Version.TryParse(Version, out var v) ? v : new Version(0, 0, 0, 0);
    }

    /// <summary>
    /// Describes the launcher as a set of files on disk, so a host can advertise its
    /// build and a client can work out what it is missing.
    /// </summary>
    /// <remarks>
    /// Only meaningful for the self-contained single-file publish, which is what the
    /// offline machines actually run. A framework-dependent build is a small exe that
    /// needs its sibling managed DLLs and an installed .NET, so handing one to another
    /// machine would break it — <see cref="CanServeUpdates"/> refuses to advertise it.
    /// </remarks>
    public static class LauncherPackage
    {
        public const string ExeName = "MinecraftLauncher.exe";

        /// <summary>
        /// Native WPF libraries that cannot be embedded in the single-file bundle and
        /// therefore sit beside the exe. Matched by shape rather than a hard-coded list
        /// so a future .NET runtime adding one does not silently ship a broken set.
        /// </summary>
        private static readonly Regex RxNative =
            new(@"^[A-Za-z0-9_]+_cor3\.dll$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The folder the running launcher lives in.</summary>
        public static string Dir => AppContext.BaseDirectory;

        /// <summary>This build's version, from the assembly.</summary>
        public static Version CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

        /// <summary>
        /// True when this is the self-contained build. The framework-dependent build
        /// leaves MinecraftLauncher.dll beside the exe; the single-file one does not.
        /// </summary>
        public static bool IsSelfContained =>
            !File.Exists(Path.Combine(Dir, "MinecraftLauncher.dll"));

        /// <summary>Whether this launcher may offer its own files to other machines.</summary>
        public static bool CanServeUpdates =>
            IsSelfContained && File.Exists(Path.Combine(Dir, ExeName));

        /// <summary>
        /// True if <paramref name="name"/> is a file we are willing to serve or write.
        /// Rejects anything with a path in it, which is what keeps the download
        /// endpoint from reaching outside the launcher folder.
        /// </summary>
        public static bool IsPackageFileName(string name) =>
            !string.IsNullOrEmpty(name) &&
            name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 &&
            name != "." && name != ".." &&
            (name.Equals(ExeName, StringComparison.OrdinalIgnoreCase) || RxNative.IsMatch(name));

        /// <summary>Full path of a package file, or null if the name is not allowed.</summary>
        public static string? PathOf(string name) =>
            IsPackageFileName(name) ? Path.Combine(Dir, name) : null;

        private static LauncherManifest? _cached;
        private static readonly object CacheLock = new();

        /// <summary>
        /// Our own manifest, hashed once and kept. These files cannot change while the
        /// launcher is running — Windows holds the exe open — and hashing 126 MB on a
        /// three-minute timer would be pure waste.
        /// </summary>
        public static LauncherManifest LocalManifest()
        {
            lock (CacheLock) return _cached ??= BuildLocalManifest();
        }

        /// <summary>Hashes every file that makes up this launcher build.</summary>
        private static LauncherManifest BuildLocalManifest()
        {
            var files = new List<PackageFile>();

            foreach (string path in PackageFilePaths())
            {
                var info = new FileInfo(path);
                files.Add(new PackageFile
                {
                    Name   = info.Name,
                    Size   = info.Length,
                    Sha256 = HashFile(path)
                });
            }

            return new LauncherManifest
            {
                Version     = CurrentVersion.ToString(),
                Compression = "gzip",
                Files       = files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }

        /// <summary>
        /// A gzip copy of <paramref name="path"/>, made once and reused.
        /// </summary>
        /// <remarks>
        /// The exe is ~126 MB raw and a little over 56 MB compressed, so on anything
        /// slower than wired gigabit this more than halves the wait. Compressing costs
        /// several seconds at Optimal, which would be silly to repeat per client, so it is
        /// cached in temp and keyed by the source's size and timestamp — a rebuilt
        /// launcher invalidates it without anyone having to remember to clear it.
        /// </remarks>
        public static string GzipCopy(string path)
        {
            var info = new FileInfo(path);
            string key = $"{info.Name}-{info.Length}-{info.LastWriteTimeUtc.Ticks}.gz";
            string cached = Path.Combine(Path.GetTempPath(), "mc-launcher-cache", key);

            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            if (File.Exists(cached)) return cached;

            // Compress to a temporary name first, so an interrupted run never leaves a
            // truncated file looking like a valid cache entry.
            string partial = cached + ".partial";
            using (var source = File.OpenRead(path))
            using (var destination = File.Create(partial))
            using (var gzip = new GZipStream(destination, CompressionLevel.Optimal))
                source.CopyTo(gzip, 1 << 20);

            File.Move(partial, cached, overwrite: true);
            return cached;
        }

        private static IEnumerable<string> PackageFilePaths()
        {
            string exe = Path.Combine(Dir, ExeName);
            if (File.Exists(exe)) yield return exe;

            foreach (string path in Directory.EnumerateFiles(Dir, "*_cor3.dll"))
                if (RxNative.IsMatch(Path.GetFileName(path)))
                    yield return path;
        }

        public static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        public static string ToJson(LauncherManifest manifest) =>
            JsonSerializer.Serialize(manifest);

        public static LauncherManifest? FromJson(string json)
        {
            try { return JsonSerializer.Deserialize<LauncherManifest>(json); }
            catch { return null; }
        }
    }
}
