using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Copying a world into a dated zip, and keeping the last few.
    /// </summary>
    /// <remarks>
    /// The worlds are the only part of this install that cannot be rebuilt. Launcher,
    /// mods, loaders and Minecraft itself all come back from a download or a pack; a
    /// world that corrupts is simply gone, and twenty people's evenings go with it.
    /// Nothing in this project backed anything up before this — <c>ServerProperties</c>
    /// still said so in a comment.
    ///
    /// Two things here are not obvious and both come from the world being *live*:
    ///
    /// <list type="bullet">
    /// <item><b><c>session.lock</c> is skipped.</b> Minecraft holds it open for the
    /// lifetime of the world and it carries nothing worth keeping. Restoring one is
    /// actively unhelpful: the game rewrites it on load, and a stale one is how
    /// "someone else is playing in this world" appears out of nowhere.</item>
    /// <item><b>Every file is opened shared.</b> A world being written to has region
    /// files open, and the default <see cref="FileStream"/> share mode would throw on
    /// the first one. Sharing means a backup of a running world can still catch a
    /// region file mid-write, which is why the server is asked to flush and hold its
    /// saves first — see <c>MainWindow.BackupServerWorld</c>. On a stopped world none
    /// of that applies and the copy is exact.</item>
    /// </list>
    ///
    /// Disk space is checked before starting and is deliberately checked against the
    /// *uncompressed* size. Region files do compress well, usually by about half, but
    /// "usually" is not something to bet a 111 MB world on when the machine has a few
    /// gigabytes free.
    /// </remarks>
    public static class WorldBackups
    {
        /// <summary>Held open by the running game, and meaningless once restored.</summary>
        public const string LockFile = "session.lock";

        /// <summary>Where backups go. Beside the launcher, so a pack never carries them.</summary>
        public static string Folder => Path.Combine(Paths.BaseDir, "backups");

        /// <summary>How many to keep per world unless told otherwise.</summary>
        public const int DefaultKeep = 5;

        /// <summary>Whether a world belongs to a single-player save or a server.</summary>
        public enum Kind { ClientSave, ServerWorld }

        /// <summary>A world that can be backed up.</summary>
        /// <param name="Owner">
        /// What the world belongs to — a client version, or a server's loader-and-version
        /// folder. Part of the backup's file name so two worlds both called "world" on
        /// the same machine cannot overwrite each other.
        /// </param>
        public sealed record Target(Kind Of, string Owner, string Name, string Path)
        {
            public string Label => Of == Kind.ClientSave
                ? $"{Name}  (single-player, {Owner})"
                : $"{Name}  (server, {Owner})";

            /// <summary>The prefix every backup of this world shares.</summary>
            public string Slug => Sanitise($"{Owner}-{Name}");

            public bool Exists => Directory.Exists(Path);
        }

        /// <summary>One backup zip that already exists.</summary>
        public sealed record Existing(string Path, DateTime When, long Bytes)
        {
            public string Name => System.IO.Path.GetFileName(Path);

            public string WhenText =>
                When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            public string SizeText => Describe(Bytes);
        }

        /// <summary>How a backup turned out.</summary>
        public sealed record Result(
            bool Ok, string? Path, long Bytes, int Files, int Skipped, string Message);

        // ── Finding worlds ───────────────────────────────────────────

        /// <summary>
        /// The single-player worlds for a client version.
        /// </summary>
        /// <remarks>
        /// The client's game directory *is* the version folder — see
        /// <c>ClientLauncher</c> — so saves live at <c>versions/&lt;v&gt;/saves</c>.
        /// </remarks>
        public static List<Target> ClientTargets(string version)
        {
            string saves = Path.Combine(Paths.VersionDir(version), "saves");
            return WorldsIn(saves, Kind.ClientSave, version);
        }

        /// <summary>
        /// The worlds belonging to one server install.
        /// </summary>
        /// <remarks>
        /// Driven by <c>level-name</c> from the server's own <c>server.properties</c>
        /// rather than by looking for a folder called "world": the launcher leaves
        /// <c>level-name</c> unset on purpose (section 11 of the handoff — writing it
        /// once pointed a server at a fresh world and the real one looked lost), so an
        /// absent key means the server's default of "world".
        ///
        /// The <c>_nether</c> and <c>_the_end</c> siblings are included when they exist.
        /// Vanilla and Fabric keep both dimensions inside the main world folder, so
        /// normally they do not; some server flavours split them out, and a backup that
        /// quietly dropped the Nether would be worse than no backup at all.
        /// </remarks>
        public static List<Target> ServerTargets(string loaderType, string version)
        {
            string dir = Paths.ServerDir(loaderType, version);
            if (!Directory.Exists(dir)) return new List<Target>();

            string owner = Path.GetFileName(dir);
            string level = "world";

            try
            {
                string? named = ServerProperties.Read(dir, "level-name");
                if (!string.IsNullOrWhiteSpace(named)) level = named.Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the default. An unreadable server.properties is not a reason to
                // refuse to back up a world that is sitting right there.
            }

            var found = new List<Target>();

            foreach (string suffix in new[] { "", "_nether", "_the_end" })
            {
                string path = Path.Combine(dir, level + suffix);
                if (Directory.Exists(path))
                    found.Add(new Target(Kind.ServerWorld, owner, level + suffix, path));
            }

            return found;
        }

        /// <summary>Every world on this machine, client and server alike.</summary>
        public static List<Target> All()
        {
            var found = new List<Target>();

            if (Directory.Exists(Paths.Versions))
                foreach (string dir in Directory.GetDirectories(Paths.Versions))
                    found.AddRange(ClientTargets(Path.GetFileName(dir)));

            if (Directory.Exists(Paths.Servers))
                foreach (string dir in Directory.GetDirectories(Paths.Servers))
                {
                    // servers/<loader>-<version>, which ServerDir composes and which
                    // ServerTargets needs back in its two halves.
                    string name = Path.GetFileName(dir);
                    int dash = name.IndexOf('-');
                    if (dash <= 0 || dash == name.Length - 1) continue;

                    found.AddRange(ServerTargets(name[..dash], name[(dash + 1)..]));
                }

            return found;
        }

        /// <summary>A folder is a world when it has a <c>level.dat</c>.</summary>
        private static List<Target> WorldsIn(string parent, Kind kind, string owner)
        {
            var found = new List<Target>();
            if (!Directory.Exists(parent)) return found;

            foreach (string dir in Directory.GetDirectories(parent))
            {
                if (!File.Exists(Path.Combine(dir, "level.dat"))) continue;
                found.Add(new Target(kind, owner, Path.GetFileName(dir), dir));
            }

            return found.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ── Sizing ───────────────────────────────────────────────────

        /// <summary>What the world takes up on disk right now.</summary>
        public static long SizeOf(string folder)
        {
            if (!Directory.Exists(folder)) return 0;

            long total = 0;

            foreach (string file in Files(folder))
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            return total;
        }

        /// <summary>Free space on whatever drive a path lives on, or null when unknown.</summary>
        public static long? FreeBytesFor(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrWhiteSpace(root)) return null;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Whether there is room, judged against the uncompressed size.
        /// </summary>
        /// <remarks>
        /// Null means the free space could not be read, which is not the same as "no":
        /// the caller should say so rather than refuse.
        /// </remarks>
        public static bool? HasRoomFor(long bytes) =>
            FreeBytesFor(Folder) is long free ? free > bytes : null;

        public static string Describe(long bytes) => bytes switch
        {
            >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.0} GB",
            >= 1024L * 1024        => $"{bytes / 1024.0 / 1024:0.0} MB",
            >= 1024                => $"{bytes / 1024.0:0} KB",
            _                      => $"{bytes} bytes"
        };

        // ── Naming and listing ───────────────────────────────────────

        private static readonly Regex RxUnsafe = new(@"[^A-Za-z0-9._-]+", RegexOptions.Compiled);

        /// <summary>Makes a world or owner name safe to put in a file name.</summary>
        public static string Sanitise(string name)
        {
            string cleaned = RxUnsafe.Replace(name, "_").Trim('_', '.');
            return cleaned.Length == 0 ? "world" : cleaned;
        }

        /// <summary>
        /// The file name for a backup taken at a given moment.
        /// </summary>
        /// <remarks>
        /// Local time, and to the minute. Local because the person reading the list is
        /// standing in the room and wants to recognise "the one from before dinner";
        /// to the minute because a second's precision in a file name reads as noise.
        /// Two backups of the same world inside one minute get a counter — see
        /// <see cref="FreePath"/>.
        /// </remarks>
        public static string NameFor(Target target, DateTime when) =>
            $"{target.Slug}-{when:yyyy-MM-dd-HHmm}.zip";

        /// <summary>A path nothing is using yet, suffixing only if it has to.</summary>
        public static string FreePath(Target target, DateTime when)
        {
            string path = Path.Combine(Folder, NameFor(target, when));
            if (!File.Exists(path)) return path;

            string stem = path[..^4];
            for (int n = 2; n < 100; n++)
            {
                string candidate = $"{stem}-{n}.zip";
                if (!File.Exists(candidate)) return candidate;
            }

            return $"{stem}-{Guid.NewGuid().ToString("N")[..6]}.zip";
        }

        /// <summary>Backups of one world, newest first.</summary>
        public static List<Existing> List(Target target) => Listing(target.Slug + "-");

        /// <summary>Every backup on the machine, newest first.</summary>
        public static List<Existing> ListAll() => Listing(null);

        private static List<Existing> Listing(string? prefix)
        {
            if (!Directory.Exists(Folder)) return new List<Existing>();

            var found = new List<Existing>();

            foreach (string path in Directory.GetFiles(Folder, "*.zip"))
            {
                string name = Path.GetFileName(path);
                if (prefix is not null && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var info = new FileInfo(path);
                    found.Add(new Existing(path, info.LastWriteTime, info.Length));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            return found.OrderByDescending(b => b.When).ToList();
        }

        /// <summary>
        /// Deletes the oldest backups of one world beyond <paramref name="keep"/>.
        /// </summary>
        /// <remarks>
        /// Deliberately permanent rather than sent to the Recycle Bin, unlike
        /// <c>ModManager.RemoveDisabled</c>. A world zip is hundreds of megabytes and
        /// the whole point of pruning is to get the space back; filling the bin with
        /// them would defeat it on a machine that is already short.
        /// </remarks>
        public static int Prune(Target target, int keep = DefaultKeep)
        {
            if (keep < 1) keep = 1;

            var all = List(target);
            int removed = 0;

            foreach (var old in all.Skip(keep))
            {
                try { DeleteFile(old.Path); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            return removed;
        }

        /// <summary>Replaced by the tests so pruning can be checked without deleting.</summary>
        internal static Action<string> DeleteFile { get; set; } = File.Delete;

        // ── Making one ───────────────────────────────────────────────

        /// <summary>
        /// Zips a world. Never throws: the caller is usually a button and sometimes a
        /// server about to be stopped.
        /// </summary>
        public static async Task<Result> CreateAsync(
            Target target, IProgress<string>? log = null, CancellationToken ct = default)
        {
            if (!target.Exists)
                return new Result(false, null, 0, 0, 0, $"There is no world at {target.Path}.");

            long size = SizeOf(target.Path);

            if (HasRoomFor(size) == false)
                return new Result(false, null, 0, 0, 0,
                    $"Not enough room: the world is {Describe(size)} and the drive has " +
                    $"{Describe(FreeBytesFor(Folder) ?? 0)} free. Delete some backups first.");

            string destination;

            try
            {
                Directory.CreateDirectory(Folder);
                destination = FreePath(target, DateTime.Now);
            }
            catch (Exception ex)
            {
                return new Result(false, null, 0, 0, 0, $"Could not write to {Folder}: {ex.Message}");
            }

            log?.Report($"Backing up {target.Name} ({Describe(size)})…");

            int files = 0, skipped = 0;

            try
            {
                await Task.Run(() =>
                {
                    using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);

                    foreach (string file in Files(target.Path))
                    {
                        ct.ThrowIfCancellationRequested();

                        string relative = Path.GetRelativePath(target.Path, file);

                        if (string.Equals(Path.GetFileName(file), LockFile,
                                          StringComparison.OrdinalIgnoreCase))
                        {
                            skipped++;
                            continue;
                        }

                        try
                        {
                            AddShared(zip, file, relative);
                            files++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // One unreadable file must not lose the other nine hundred.
                            skipped++;
                            log?.Report($"  skipped {relative}: {ex.Message}");
                        }
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
                TryDelete(destination);
                return new Result(false, null, 0, 0, 0, "Backup cancelled.");
            }
            catch (Exception ex)
            {
                TryDelete(destination);
                return new Result(false, null, 0, 0, 0, $"Backup failed: {ex.Message}");
            }

            long wrote = 0;
            try { wrote = new FileInfo(destination).Length; } catch (Exception) { }

            string note = skipped > 0
                ? $"Backed up {target.Name}: {files} files, {Describe(wrote)} ({skipped} skipped)."
                : $"Backed up {target.Name}: {files} files, {Describe(wrote)}.";

            log?.Report(note);
            return new Result(true, destination, wrote, files, skipped, note);
        }

        /// <summary>
        /// Adds one file, reading it with sharing so a world in use can still be copied.
        /// </summary>
        /// <remarks>
        /// <see cref="ZipFileExtensions.CreateEntryFromFile"/> cannot do this — it opens
        /// the source exclusively — so the entry is created and filled by hand. The
        /// timestamp is carried across so a restored world keeps its own history.
        /// </remarks>
        private static void AddShared(ZipArchive zip, string file, string relative)
        {
            // Zip entries are '/'-separated regardless of platform.
            var entry = zip.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.Optimal);

            try { entry.LastWriteTime = new FileInfo(file).LastWriteTime; }
            catch (Exception) { }

            using var source = new FileStream(
                file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var target = entry.Open();

            source.CopyTo(target);
        }

        private static IEnumerable<string> Files(string folder)
        {
            try { return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }
    }
}
