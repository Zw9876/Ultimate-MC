using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Updating a Fabric <b>server's</b> loader, the counterpart to
    /// <see cref="FabricLoaderUpdate"/> for clients.
    /// </summary>
    /// <remarks>
    /// A server is not a client with a different folder, and treating it as one is why
    /// MAKE A PACK only ever produced client packs. A client keeps its loader in a
    /// profile JSON — <c>versions/fabric-loader-&lt;loader&gt;-&lt;mc&gt;.json</c> —
    /// naming the libraries it needs. A server has no profile at all. Its
    /// <c>server.jar</c> is the Fabric server launcher, and the loader version is
    /// recorded in <c>install.properties</c> <i>inside that jar</i>:
    ///
    /// <code>
    /// fabric-loader-version=0.19.3
    /// game-version=26.1.2
    /// </code>
    ///
    /// So updating a server means replacing <c>server.jar</c>, not writing a profile.
    /// Dropping new loader libraries in beside the old ones changes nothing, because
    /// the jar still asks for the version baked into it.
    ///
    /// A server pack therefore carries <c>server.jar</c> plus the whole
    /// <c>libraries/</c> tree, which is small — about 53 files here. It deliberately
    /// leaves out <c>versions/&lt;mc&gt;/server-&lt;mc&gt;.jar</c>: that is Minecraft
    /// itself, it is not redistributable, and it does not change when the loader does.
    /// </remarks>
    public static class FabricServerLoader
    {
        /// <summary>The server folder naming this project uses: servers/fabric-&lt;mc&gt;.</summary>
        public const string LoaderType = "fabric";

        private const string InstallProperties = "install.properties";
        private const string LoaderKey = "fabric-loader-version";

        /// <summary>Where this Minecraft version's Fabric server lives.</summary>
        public static string Dir(string mcVersion) => Paths.ServerDir(LoaderType, mcVersion);

        /// <summary>True when there is a Fabric server here to update.</summary>
        public static bool Exists(string mcVersion) => File.Exists(ServerJar(mcVersion));

        private static string ServerJar(string mcVersion) =>
            Path.Combine(Dir(mcVersion), "server.jar");

        private static string LoaderLibraryDir(string mcVersion) =>
            Path.Combine(Dir(mcVersion), "libraries", "net", "fabricmc", "fabric-loader");

        /// <summary>
        /// The loader version this server actually runs, read from the jar that decides
        /// it. Null when there is no Fabric server here or the jar has no manifest.
        /// </summary>
        /// <remarks>
        /// This is the authoritative answer and the folder listing is not. A server
        /// keeps every loader it has ever had under
        /// <c>libraries/net/fabricmc/fabric-loader/</c>, so picking the newest folder
        /// reports whatever was installed last rather than what will start — the same
        /// mistake the client side made when it took the first profile file by name.
        /// </remarks>
        public static string? InstalledVersion(string mcVersion)
        {
            string jar = ServerJar(mcVersion);
            if (!File.Exists(jar)) return null;

            try
            {
                using var zip = ZipFile.OpenRead(jar);

                var entry = zip.GetEntry(InstallProperties);
                if (entry is null) return null;

                using var reader = new StreamReader(entry.Open());

                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    int split = line.IndexOf('=');
                    if (split <= 0) continue;

                    if (line[..split].Trim().Equals(LoaderKey, StringComparison.OrdinalIgnoreCase))
                    {
                        string value = line[(split + 1)..].Trim();
                        return value.Length > 0 ? value : null;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                // A half-written or corrupt jar is a "cannot tell", not a crash.
            }

            return null;
        }

        /// <summary>
        /// Every loader this server has libraries for, the one actually in use first.
        /// </summary>
        public static List<string> InstalledLoaders(string mcVersion)
        {
            var found = new List<string>();

            string? active = InstalledVersion(mcVersion);
            if (active is not null) found.Add(active);

            string dir = LoaderLibraryDir(mcVersion);
            if (Directory.Exists(dir))
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (!found.Contains(name, StringComparer.OrdinalIgnoreCase)) found.Add(name);
                }
            }

            return found;
        }

        /// <summary>
        /// Builds a pack from this server's loader, for carrying to a machine that
        /// cannot download one.
        /// </summary>
        public static async Task<FabricLoaderUpdate.PackInfo> ExportAsync(
            string mcVersion, string zipPath, IProgress<string>? log, CancellationToken ct)
        {
            string jar = ServerJar(mcVersion);
            if (!File.Exists(jar))
                throw new FileNotFoundException(
                    $"There is no Fabric server for Minecraft {mcVersion} here, so there is " +
                    "nothing to package.", jar);

            string loader = InstalledVersion(mcVersion)
                ?? throw new InvalidDataException(
                    "That server's jar does not say which Fabric loader it runs, so a pack " +
                    "made from it could not be trusted.");

            string serverDir = Dir(mcVersion);
            string libsDir = Path.Combine(serverDir, "libraries");

            string temp = zipPath + ".building";
            if (File.Exists(temp)) File.Delete(temp);

            long bytes = 0;
            int count = 0;

            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(temp, ZipArchiveMode.Create);

                // The jar first: it is the thing that actually selects the loader.
                zip.CreateEntryFromFile(jar, "server.jar");
                bytes += new FileInfo(jar).Length;
                count++;
                log?.Report($"server.jar (Fabric loader {loader}).");

                if (Directory.Exists(libsDir))
                {
                    foreach (string file in Directory.GetFiles(libsDir, "*", SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();

                        string relative = Path.GetRelativePath(libsDir, file).Replace('\\', '/');
                        zip.CreateEntryFromFile(file, $"libraries/{relative}");
                        bytes += new FileInfo(file).Length;
                        count++;
                    }
                }
                else
                {
                    // Survivable: the server launcher fetches them on first start, but
                    // only somewhere with internet, which is not where packs are used.
                    log?.Report("[WARNING] This server has no libraries folder yet. The " +
                                "receiving machine will need internet on first start.");
                }

                log?.Report($"Packed {count} files.");
                FabricLoaderUpdate.WriteManifest(zip, mcVersion, loader, count, forServer: true);
            }, ct);

            File.Move(temp, zipPath, overwrite: true);
            log?.Report($"Wrote {Path.GetFileName(zipPath)}.");

            return new FabricLoaderUpdate.PackInfo(mcVersion, loader, count, bytes, ForServer: true);
        }

        /// <summary>
        /// Unpacks a server pack over this server, replacing its loader.
        /// </summary>
        public static async Task<FabricLoaderUpdate.PackInfo> ImportAsync(
            string mcVersion, string zipPath, IProgress<string>? log, CancellationToken ct)
        {
            var info = FabricLoaderUpdate.Inspect(zipPath)
                ?? throw new InvalidDataException(
                    "That file is not a Fabric loader pack made by this launcher.");

            if (!info.ForServer)
                throw new InvalidDataException(
                    "That is a client pack. A server needs a pack made from a server — the two " +
                    "hold different files and are not interchangeable.");

            if (!info.MinecraftVersion.Equals(mcVersion, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"That pack is for Minecraft {info.MinecraftVersion}, but {mcVersion} is selected.");

            string serverDir = Dir(mcVersion);
            if (!Directory.Exists(serverDir))
                throw new DirectoryNotFoundException(
                    $"There is no Fabric server for Minecraft {mcVersion} here.");

            await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(zipPath);

                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    if (entry.Name.Length == 0) continue;                        // a directory
                    if (entry.FullName == FabricLoaderUpdate.ManifestName) continue;

                    // Nothing in a supplied file gets to choose where it lands.
                    string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string destination = Path.GetFullPath(Path.Combine(serverDir, relative));

                    if (!destination.StartsWith(Path.GetFullPath(serverDir) + Path.DirectorySeparatorChar,
                                                StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"That pack tries to write outside the server folder ({entry.FullName}).");

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }, ct);

            log?.Report($"Installed Fabric loader {info.LoaderVersion}.");
            RemoveOtherLoaders(mcVersion, info.LoaderVersion, log);

            return info;
        }

        /// <summary>
        /// Downloads the Fabric server launcher for a loader version and puts it in
        /// place. Needs internet.
        /// </summary>
        /// <remarks>
        /// Only the jar is fetched. The server launcher resolves its own libraries the
        /// next time it starts, which is fine on a machine that can reach the internet
        /// and is the reason the offline route is a pack rather than this.
        /// </remarks>
        public static async Task InstallOnlineAsync(
            string mcVersion, string loaderVersion, IProgress<string>? log, CancellationToken ct)
        {
            string serverDir = Dir(mcVersion);
            if (!Directory.Exists(serverDir))
                throw new DirectoryNotFoundException(
                    $"There is no Fabric server for Minecraft {mcVersion} here.");

            string installer = await FabricMeta.LatestInstallerAsync(ct);
            log?.Report($"Fabric loader {loaderVersion} (installer {installer}).");

            // Downloaded beside the real jar and moved into place only once it has
            // arrived whole, so a failed download cannot leave an unstartable server.
            string jar = ServerJar(mcVersion);
            string staged = jar + ".new";

            await Downloader.GetFileAsync(
                FabricMeta.ServerJarUrl(mcVersion, loaderVersion, installer), staged, ct);

            File.Move(staged, jar, overwrite: true);
            log?.Report("server.jar replaced.");

            RemoveOtherLoaders(mcVersion, loaderVersion, log);

            log?.Report("The server fetches the new loader's libraries next time it starts.");
        }

        /// <summary>
        /// Removes the library folders of every loader except the one now in use.
        /// </summary>
        /// <remarks>
        /// Safe to delete by folder name here, unlike the client: this path holds only
        /// <c>fabric-loader</c> builds, one folder per version, and nothing else shares
        /// it. The client's libraries folder mixes the vanilla game's jars with Forge's
        /// and NeoForge's, which is why that side has to check what every remaining
        /// profile still names before removing anything.
        /// </remarks>
        public static List<string> RemoveOtherLoaders(
            string mcVersion, string keepLoaderVersion, IProgress<string>? log)
        {
            var removed = new List<string>();

            string dir = LoaderLibraryDir(mcVersion);
            if (!Directory.Exists(dir)) return removed;

            // Nothing is removed until the replacement is really there — the same rule
            // the client side learned when a test asked it to keep a version that was
            // not installed and it deleted the only loader present.
            string keepDir = Path.Combine(dir, keepLoaderVersion);
            if (!Directory.Exists(keepDir))
            {
                log?.Report($"[SKIPPED] Leaving the old loaders: {keepLoaderVersion} has no " +
                            "libraries here yet.");
                return removed;
            }

            foreach (string sub in Directory.GetDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.Equals(keepLoaderVersion, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    Directory.Delete(sub, recursive: true);
                    removed.Add(name);
                    log?.Report($"Removed the old loader {name}.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A locked file means the server is running; leaving it is untidy
                    // but harmless, because the jar decides which one is used.
                    log?.Report($"[KEPT] {name} is in use and was left in place.");
                }
            }

            return removed;
        }
    }
}
