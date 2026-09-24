using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Reading Minecraft's own crash reports and saying, in a sentence, what went wrong.
    /// </summary>
    /// <remarks>
    /// A crash report is a hundred-odd lines of stack trace with the answer buried in
    /// the middle, and the people using this launcher are not going to read it. Almost
    /// always the useful part is three things: what the game was doing, which mod broke,
    /// and why. All three are in there and can be lifted out.
    ///
    /// Written against a real report captured from a real crash — `-- MOD x --` sections,
    /// `Suspected Mods:`, the `Failure message:` continuation lines and all. The format
    /// is stable across versions because Minecraft has written it the same way for years,
    /// but the named-mod sections are a **Forge and NeoForge** feature: Fabric crash
    /// reports carry the description and the exception but do not name a culprit, so for
    /// those this says what it knows and does not invent the rest.
    ///
    /// Nothing here writes. A crash report is evidence, and deleting one is the user's
    /// decision made in their file manager, not a side effect of looking at it.
    /// </remarks>
    public static class CrashReports
    {
        /// <summary>One mod the report blames, with Forge's own explanation.</summary>
        public sealed record CrashMod(string Id, string? JarFile, string? FailureMessage, string? Version)
        {
            /// <summary>The jar's name alone — the full path in the report is another machine's.</summary>
            public string? JarName => JarFile is null ? null : Path.GetFileName(JarFile.Replace('/', '\\'));
        }

        /// <summary>A parsed crash report.</summary>
        public sealed record Report(
            string FileName,
            string FullPath,
            DateTime When,
            long Bytes,
            string? Description,
            string? Exception,
            string? MinecraftVersion,
            string? JavaVersion,
            string? SuspectedMods,
            IReadOnlyList<CrashMod> Mods)
        {
            public string WhenText => When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            public string SizeText => Bytes >= 1024 * 1024
                ? $"{Bytes / (1024.0 * 1024.0):0.#} MB"
                : $"{Bytes / 1024.0:0} KB";

            /// <summary>The one-line version for a list.</summary>
            public string Headline => Description ?? Exception ?? "Crash";

            /// <summary>
            /// What to tell somebody who is not going to read the stack trace.
            /// </summary>
            /// <remarks>
            /// Says only what the report actually states. When nothing names a mod this
            /// says so, rather than guessing from the stack trace — a wrong accusation
            /// costs someone an evening removing a mod that was never the problem.
            /// </remarks>
            public string Explain()
            {
                var lines = new List<string>();

                if (Description is not null) lines.Add($"What it was doing: {Description}");
                if (Exception is not null) lines.Add($"Error: {Exception}");

                if (Mods.Count > 0)
                {
                    lines.Add("");
                    lines.Add(Mods.Count == 1 ? "The report blames this mod:" : "The report blames these mods:");

                    foreach (var mod in Mods)
                    {
                        lines.Add($"  • {mod.Id}{(mod.Version is null ? "" : $" ({mod.Version})")}");
                        if (mod.JarName is not null) lines.Add($"      file: {mod.JarName}");
                        if (mod.FailureMessage is not null) lines.Add($"      why:  {mod.FailureMessage}");
                    }
                }
                else if (SuspectedMods is not null && !SuspectedMods.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add("");
                    lines.Add($"Suspected mods: {SuspectedMods}");
                }
                else
                {
                    lines.Add("");
                    lines.Add("No mod is named in this report. That usually means the crash was not " +
                              "caused by a single mod failing to load — check the error above.");
                }

                if (MinecraftVersion is not null || JavaVersion is not null)
                {
                    lines.Add("");
                    if (MinecraftVersion is not null) lines.Add($"Minecraft: {MinecraftVersion}");
                    if (JavaVersion is not null) lines.Add($"Java: {JavaVersion}");
                }

                return string.Join(Environment.NewLine, lines);
            }
        }

        /// <summary>Where a client's crash reports land: the game directory is the version folder.</summary>
        public static string FolderFor(string version) =>
            Path.Combine(Paths.VersionDir(version), "crash-reports");

        /// <summary>Every crash report for a version, newest first.</summary>
        public static List<Report> List(string version)
        {
            string folder = FolderFor(version);
            if (!Directory.Exists(folder)) return new List<Report>();

            var found = new List<Report>();

            foreach (string path in Directory.GetFiles(folder, "*.txt"))
            {
                var report = TryParse(path);
                if (report is not null) found.Add(report);
            }

            return found.OrderByDescending(r => r.When).ToList();
        }

        /// <summary>Parses one report, or null when the file cannot be read at all.</summary>
        public static Report? TryParse(string path)
        {
            try
            {
                return Parse(path, File.ReadAllLines(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// The parser, separated from the file so it can be tested against captured text.
        /// </summary>
        public static Report Parse(string path, IReadOnlyList<string> lines)
        {
            var info = new FileInfo(path);

            string? description = null;
            string? exception = null;
            string? mcVersion = null;
            string? javaVersion = null;
            string? suspected = null;
            DateTime? time = null;

            var mods = new List<CrashMod>();

            // Current -- MOD x -- section being filled in, if any.
            string? modId = null;
            string? modJar = null, modWhy = null, modVersion = null;

            void CloseMod()
            {
                if (modId is not null) mods.Add(new CrashMod(modId, modJar, modWhy, modVersion));
                modId = null; modJar = modWhy = modVersion = null;
            }

            bool inSystemDetails = false;

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                // Section headers look like "-- Head --", "-- MOD id --", "-- System Details --".
                if (trimmed.StartsWith("-- ", StringComparison.Ordinal) &&
                    trimmed.EndsWith(" --", StringComparison.Ordinal))
                {
                    CloseMod();

                    string name = trimmed[3..^3].Trim();
                    inSystemDetails = name.Equals("System Details", StringComparison.OrdinalIgnoreCase);

                    if (name.StartsWith("MOD ", StringComparison.OrdinalIgnoreCase))
                        modId = name[4..].Trim();

                    continue;
                }

                if (time is null && trimmed.StartsWith("Time:", StringComparison.Ordinal))
                {
                    // "2026-04-12 13:51:12". Invariant, because the report writes it that
                    // way whatever the machine's locale is.
                    if (DateTime.TryParse(trimmed[5..].Trim(), CultureInfo.InvariantCulture,
                                          DateTimeStyles.None, out var parsed))
                        time = parsed;
                    continue;
                }

                if (description is null && trimmed.StartsWith("Description:", StringComparison.Ordinal))
                {
                    description = Value(trimmed, "Description:");

                    // The exception is the next non-blank line, and it is the only thing
                    // between the description and the stack trace, which is indented.
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        if (lines[j].Trim().Length == 0) continue;
                        if (lines[j].StartsWith("\t", StringComparison.Ordinal) ||
                            lines[j].StartsWith("    ", StringComparison.Ordinal)) break;

                        exception = lines[j].Trim();
                        break;
                    }

                    continue;
                }

                if (suspected is null && trimmed.StartsWith("Suspected Mods:", StringComparison.Ordinal))
                {
                    suspected = Value(trimmed, "Suspected Mods:");
                    continue;
                }

                if (modId is not null)
                {
                    if (trimmed.StartsWith("Mod File:", StringComparison.Ordinal))
                        modJar = Value(trimmed, "Mod File:");
                    else if (trimmed.StartsWith("Mod Version:", StringComparison.Ordinal))
                        modVersion = Value(trimmed, "Mod Version:");
                    else if (trimmed.StartsWith("Failure message:", StringComparison.Ordinal))
                    {
                        modWhy = Value(trimmed, "Failure message:");

                        // It runs onto further, more deeply indented lines — the real
                        // reason often lives there ("Currently, x is not installed").
                        int indent = Indent(line);
                        for (int j = i + 1; j < lines.Count; j++)
                        {
                            if (lines[j].Trim().Length == 0) break;
                            if (Indent(lines[j]) <= indent) break;
                            modWhy += " " + lines[j].Trim();
                        }
                    }
                }

                if (inSystemDetails)
                {
                    if (mcVersion is null && trimmed.StartsWith("Minecraft Version:", StringComparison.Ordinal))
                        mcVersion = Value(trimmed, "Minecraft Version:");
                    else if (javaVersion is null && trimmed.StartsWith("Java Version:", StringComparison.Ordinal))
                        javaVersion = Value(trimmed, "Java Version:");
                }
            }

            CloseMod();

            return new Report(
                info.Name, path,
                // The name carries the time too, but the line inside is what the game
                // recorded; the file can be copied about and restamped.
                time ?? info.LastWriteTime,
                info.Length,
                description, exception, mcVersion, javaVersion, suspected, mods);
        }

        private static string? Value(string line, string prefix)
        {
            string value = line[prefix.Length..].Trim();
            return value.Length == 0 ? null : value;
        }

        private static int Indent(string line)
        {
            int n = 0;
            foreach (char c in line)
            {
                if (c == '\t') n += 4;
                else if (c == ' ') n++;
                else break;
            }
            return n;
        }
    }
}
