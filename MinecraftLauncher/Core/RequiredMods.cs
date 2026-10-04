using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Mods the host asks everyone to have, offered when they press PLAY.
    /// </summary>
    /// <remarks>
    /// Provisioning, not enforcement. It offers; the person can say no and the game
    /// starts anyway. Nothing here can stop someone playing, which matters because the
    /// reasons a download fails on these machines — no internet for Mojang, a host that
    /// is not up yet, Defender eating something — have nothing to do with the person
    /// pressing the button.
    ///
    /// **An entry carries the mod id.** That is the point of the format: "do I already
    /// have this?" is then a filesystem question, answered offline in milliseconds by
    /// reading ids out of the jars already in the folder. The network is needed only to
    /// *install* something, never to work out whether it is needed. Matching on file
    /// name instead would be wrong for the reason it is always wrong here — two builds
    /// of one mod have different names, and authors rename freely.
    ///
    /// **Entries are keyed on Minecraft version *and* loader**, because
    /// `versions/&lt;v&gt;/mods` is one folder shared by every loader on that version.
    /// A Fabric mod required for 26.1.2 must not be handed to a NeoForge install
    /// reading the same folder.
    ///
    /// **Pinned, not "latest".** A list that says "newest Sodium" gives two machines
    /// that check a week apart different builds, which is the drift this project keeps
    /// paying for. A Modrinth version id names one build forever.
    /// </remarks>
    public static class RequiredMods
    {
        /// <summary>The list, as the host keeps it beside the launcher.</summary>
        public const string FileName = "required-mods.json";

        /// <summary>Where a client fetches it from the host's skin server.</summary>
        public const string Endpoint = "/launcher/required-mods";

        /// <summary>Remembers a "no" so PLAY stops asking. Beside the launcher.</summary>
        private const string DeclineFileName = "required-mods-declined.txt";

        /// <summary>Where a required jar comes from.</summary>
        public enum Source
        {
            /// <summary>Resolved and downloaded from Modrinth by project and version id.</summary>
            Modrinth,

            /// <summary>Served by the host itself, for anything not on Modrinth.</summary>
            Host
        }

        /// <summary>One mod the host wants present for one version and loader.</summary>
        /// <remarks>
        /// <paramref name="Source"/> and <paramref name="Sha1"/> are in the format from
        /// the start even while only Modrinth is implemented. Adding host-served jars
        /// later must not mean a format change rolled out across twenty machines.
        /// </remarks>
        public sealed record Entry
        {
            [JsonPropertyName("minecraft")] public string Minecraft { get; init; } = "";
            [JsonPropertyName("loader")]    public string Loader    { get; init; } = "";

            /// <summary>The id inside the jar. What "already installed" is decided on.</summary>
            [JsonPropertyName("modid")]     public string ModId     { get; init; } = "";

            /// <summary>For showing a person, not for matching.</summary>
            [JsonPropertyName("name")]      public string Name      { get; init; } = "";

            [JsonPropertyName("source")]    public string SourceName { get; init; } = "modrinth";

            /// <summary>Modrinth project slug or id.</summary>
            [JsonPropertyName("project")]   public string? Project   { get; init; }

            /// <summary>A Modrinth version id, pinning one exact build.</summary>
            [JsonPropertyName("version")]   public string? VersionId { get; init; }

            /// <summary>File name, for a jar the host serves itself.</summary>
            [JsonPropertyName("file")]      public string? File      { get; init; }

            /// <summary>Expected SHA-1. Required for a host-served jar.</summary>
            [JsonPropertyName("sha1")]      public string? Sha1      { get; init; }

            /// <summary>Why it is wanted, shown in the prompt when present.</summary>
            [JsonPropertyName("why")]       public string? Why       { get; init; }

            [JsonIgnore]
            public Source From => SourceName.Equals("host", StringComparison.OrdinalIgnoreCase)
                ? Source.Host : Source.Modrinth;

            [JsonIgnore]
            public string Display => Name.Length > 0 ? Name : ModId;

            /// <summary>For the admin list.</summary>
            [JsonIgnore]
            public string SourceDisplay => From == Source.Host ? "This host" : "Modrinth";

            /// <summary>What identifies the build, for the admin list.</summary>
            [JsonIgnore]
            public string Detail => From == Source.Host
                ? File ?? "(no file)"
                : VersionId is { Length: > 0 } v ? v : $"{Project} (newest)";

            /// <summary>
            /// False for an entry that could never be acted on, so a typo in the list
            /// is skipped rather than throwing in front of someone pressing PLAY.
            /// </summary>
            [JsonIgnore]
            public bool Usable =>
                Minecraft.Length > 0 && Loader.Length > 0 && ModId.Length > 0 &&
                (From == Source.Modrinth
                    ? !string.IsNullOrWhiteSpace(Project)
                    : !string.IsNullOrWhiteSpace(File) && !string.IsNullOrWhiteSpace(Sha1));
        }

        /// <summary>The whole list as published by the host.</summary>
        public sealed class Listing
        {
            [JsonPropertyName("entries")]
            public List<Entry> Entries { get; set; } = new();

            /// <summary>What is wanted for one version and loader, unusable rows dropped.</summary>
            public List<Entry> For(string mcVersion, string loaderType) =>
                Entries.Where(e => e.Usable
                             && e.Minecraft.Equals(mcVersion, StringComparison.OrdinalIgnoreCase)
                             && e.Loader.Equals(loaderType, StringComparison.OrdinalIgnoreCase))
                       .ToList();

            /// <summary>Every version named in the list, for the admin view.</summary>
            public List<string> Versions() =>
                Entries.Select(e => e.Minecraft).Where(v => v.Length > 0)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).ToList();

            /// <summary>
            /// Identifies what is being asked for, so a declined prompt can stay declined
            /// until the ask actually changes.
            /// </summary>
            /// <remarks>
            /// Over the entries for this version and loader only, sorted, and covering
            /// the pinned build as well as the mod. So pinning a different version of
            /// the same mod re-asks — it is a different ask — while editing an unrelated
            /// version's entries does not nag someone who already said no.
            /// </remarks>
            public string Fingerprint(string mcVersion, string loaderType)
            {
                var parts = For(mcVersion, loaderType)
                    .Select(e => $"{e.ModId}|{e.SourceName}|{e.Project}|{e.VersionId}|{e.File}|{e.Sha1}")
                    .OrderBy(s => s, StringComparer.Ordinal);

                string joined = string.Join("\n", parts);
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16];
            }
        }

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Reads a published list. Never throws: a malformed list must not be able to
        /// stand between somebody and the PLAY button.
        /// </summary>
        public static Listing Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Listing();

            try
            {
                return JsonSerializer.Deserialize<Listing>(json, Json) ?? new Listing();
            }
            catch (JsonException)
            {
                return new Listing();
            }
        }

        public static string ToJson(Listing listing) => JsonSerializer.Serialize(listing, Json);

        /// <summary>The list this machine keeps, which on the host is the master copy.</summary>
        public static string LocalPath => Path.Combine(Paths.BaseDir, FileName);

        public static Listing LoadLocal()
        {
            try
            {
                return File.Exists(LocalPath) ? Parse(File.ReadAllText(LocalPath)) : new Listing();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Listing();
            }
        }

        public static void SaveLocal(Listing listing) =>
            File.WriteAllText(LocalPath, ToJson(listing));

        /// <summary>Whether this machine publishes a list at all.</summary>
        public static bool HasLocalList => File.Exists(LocalPath);

        /// <summary>
        /// Whether the editing tab is offered on this machine.
        /// </summary>
        /// <remarks>
        /// An empty <c>admin.flag</c> beside the launcher, and nothing more. There is
        /// no password because there is no adversary — the point is that the tab is not
        /// on anyone else's launcher to be poked at, and the file only exists on the
        /// machine that maintains the list. It is not in the update package, so an
        /// update neither grants it nor takes it away.
        /// </remarks>
        public static bool AdminEnabled => File.Exists(Path.Combine(Paths.BaseDir, "admin.flag"));

        // ── jars the host serves itself ──

        /// <summary>Where the host keeps jars that are not on Modrinth.</summary>
        public static string JarFolder => Path.Combine(Paths.BaseDir, "required-mods");

        /// <summary>
        /// The jar a request names, or null when the name is not one we will serve.
        /// </summary>
        /// <remarks>
        /// The name is **validated, not resolved** — the same approach
        /// <see cref="LauncherPackage.PathOf"/> takes. A bare <c>.jar</c> file name with
        /// no separators and no dots leading anywhere: there is then nothing in the
        /// request that could point outside the folder, rather than a traversal check
        /// that has to be right.
        /// </remarks>
        public static string? JarPathOf(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return null;

            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return null;
            if (name.Contains("..", StringComparison.Ordinal)) return null;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            if (!name.Equals(Path.GetFileName(name), StringComparison.Ordinal)) return null;

            string path = Path.Combine(JarFolder, name);
            return File.Exists(path) ? path : null;
        }

        // ── what is actually missing ──

        /// <summary>
        /// The mod ids already in a folder, read from inside the jars.
        /// </summary>
        /// <remarks>
        /// Includes <c>.jar.disabled</c> files on purpose. A mod somebody switched off
        /// is still a mod they have, and re-enabling it behind their back would undo a
        /// deliberate decision — so a disabled required mod counts as present and is
        /// left exactly as it is.
        /// </remarks>
        public static HashSet<string> ModIdsIn(string modsFolder)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(modsFolder) || !Directory.Exists(modsFolder)) return ids;

            foreach (string path in Directory.EnumerateFiles(modsFolder))
            {
                string name = Path.GetFileName(path);
                if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ModInspector.ModIdOf(path) is string id && id.Length > 0) ids.Add(id);
            }

            return ids;
        }

        /// <summary>The SHA-1 of every jar in a folder, enabled or not.</summary>
        public static HashSet<string> Sha1sIn(string modsFolder)
        {
            var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(modsFolder) || !Directory.Exists(modsFolder)) return hashes;

            foreach (string path in Directory.EnumerateFiles(modsFolder))
            {
                string name = Path.GetFileName(path);
                if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    using var stream = File.OpenRead(path);
                    hashes.Add(Convert.ToHexString(SHA1.HashData(stream)));
                }
                catch (IOException) { /* a jar being written; it will be seen next time */ }
            }

            return hashes;
        }

        /// <summary>
        /// Whether a folder already satisfies one entry.
        /// </summary>
        /// <remarks>
        /// A host-served entry with a hash is satisfied only by **that exact jar**, not
        /// merely by the mod id. This exists because the first real use of it is a
        /// *downgraded* mod — a repack of somebody else's — which carries the original
        /// mod's id inside it. Matching on the id alone would see the upstream build
        /// already installed, call the requirement met, and never deliver the build that
        /// was actually asked for.
        ///
        /// Modrinth entries still match on mod id. Their pinned version is a real
        /// published build, and treating "some build of Sodium" as satisfying "Sodium"
        /// is the right answer there — forcing an exact hash would fight every person
        /// who updated a mod themselves.
        /// </remarks>
        private static bool Satisfied(Entry entry, HashSet<string> modIds, HashSet<string>? sha1s) =>
            entry.From == Source.Host && !string.IsNullOrWhiteSpace(entry.Sha1)
                ? sha1s is not null && sha1s.Contains(entry.Sha1!)
                : modIds.Contains(entry.ModId);

        /// <summary>Which of the wanted mods this folder does not have.</summary>
        public static List<Entry> MissingIn(IEnumerable<Entry> wanted, string modsFolder)
        {
            var list = wanted.Where(e => e.Usable).ToList();
            if (list.Count == 0) return list;

            var have = ModIdsIn(modsFolder);

            // Hashing every jar costs real time, so only do it when an entry actually
            // pins one.
            HashSet<string>? hashes = list.Any(e => e.From == Source.Host &&
                                                    !string.IsNullOrWhiteSpace(e.Sha1))
                ? Sha1sIn(modsFolder)
                : null;

            return list.Where(e => !Satisfied(e, have, hashes)).ToList();
        }

        // ── remembering a "no" ──

        private static string DeclinePath => Path.Combine(Paths.BaseDir, DeclineFileName);

        private static string DeclineKey(string mcVersion, string loaderType) =>
            $"{mcVersion.ToLowerInvariant()}|{loaderType.ToLowerInvariant()}";

        /// <summary>
        /// True when this exact ask was already turned down for this version and loader.
        /// </summary>
        /// <remarks>
        /// Stored beside the launcher rather than in the update package, so an update
        /// does not reset everyone's answer — and deliberately not in <c>config.txt</c>,
        /// which is the file people are told to delete when a machine misbehaves.
        /// </remarks>
        public static bool WasDeclined(string mcVersion, string loaderType, string fingerprint)
        {
            try
            {
                if (!File.Exists(DeclinePath)) return false;

                string wanted = $"{DeclineKey(mcVersion, loaderType)}|{fingerprint}";
                return File.ReadLines(DeclinePath)
                           .Any(l => l.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable means "not declined": asking once more is a smaller cost
                // than silently never asking again.
                return false;
            }
        }

        public static void RememberDecline(string mcVersion, string loaderType, string fingerprint)
        {
            try
            {
                var lines = File.Exists(DeclinePath)
                    ? File.ReadAllLines(DeclinePath).ToList()
                    : new List<string>();

                string key = DeclineKey(mcVersion, loaderType);

                // One line per version+loader: a new fingerprint replaces the old answer
                // rather than leaving a record of every ask ever declined.
                lines.RemoveAll(l => l.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase));
                lines.Add($"{key}|{fingerprint}");

                File.WriteAllLines(DeclinePath, lines);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Then they get asked again next time, which is the harmless direction.
            }
        }

        /// <summary>Clears the answer, so installing everything stops it being "declined".</summary>
        public static void ForgetDecline(string mcVersion, string loaderType)
        {
            try
            {
                if (!File.Exists(DeclinePath)) return;

                string key = DeclineKey(mcVersion, loaderType);
                var lines = File.ReadAllLines(DeclinePath)
                                .Where(l => !l.StartsWith(key + "|", StringComparison.OrdinalIgnoreCase))
                                .ToList();

                File.WriteAllLines(DeclinePath, lines);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
