using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MinecraftLauncher.Core
{
    /// <summary>One downloadable version, as listed in version-links.txt.</summary>
    public sealed record VersionLink(string Label, string Url);

    /// <summary>
    /// Reads the user-maintained list of places to download game versions from.
    /// </summary>
    /// <remarks>
    /// Versions are no longer shipped inside the distributable, so the launcher
    /// points at them instead. The list is a plain text file so it can be edited
    /// on a deployed machine without rebuilding anything.
    /// </remarks>
    public static class VersionLinks
    {
        public const string FileName = "version-links.txt";

        public static string FilePath => Path.Combine(Paths.BaseDir, FileName);

        private const string Template = """
            # Minecraft Portable Launcher — version download links
            #
            # One entry per line, in either form:
            #     Name | https://example.com/downloads/1.21.1.zip
            #     Name = https://example.com/downloads/1.21.1.zip
            #
            # A bare URL on its own line also works; the name is taken from it.
            # Lines starting with # are ignored.
            #
            # Clicking an entry opens the link in your browser. Download the file,
            # then extract it into the launcher's versions\ folder so that you get:
            #     versions\<version>\versions\<version>.json
            #
            # Examples — replace these with your own:
            # 1.21.1 (Fabric) | https://example.com/mc/1.21.1-fabric.zip
            # 1.20.1 (Forge)  | https://example.com/mc/1.20.1-forge.zip
            """;

        /// <summary>Writes a commented template if the file does not exist yet.</summary>
        public static void EnsureFileExists()
        {
            try
            {
                if (!File.Exists(FilePath))
                    File.WriteAllText(FilePath, Template, new UTF8Encoding(false));
            }
            catch
            {
                // A read-only folder just means no template; the list reads empty.
            }
        }

        public static List<VersionLink> Load()
        {
            var links = new List<VersionLink>();
            if (!File.Exists(FilePath)) return links;

            foreach (string raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;

                string label, url;
                int sep = line.IndexOfAny(new[] { '|', '=' });

                // A bare URL is allowed: "https://host/a/b.zip" also contains no
                // separator, so only split when the left side is not itself a URL.
                if (sep > 0 && !LooksLikeUrl(line[..sep].Trim()))
                {
                    label = line[..sep].Trim();
                    url = line[(sep + 1)..].Trim();
                }
                else
                {
                    url = line;
                    label = NameFromUrl(line);
                }

                // Only web links are accepted. Anything else in this file would be
                // handed to the shell, which would happily run a local executable.
                if (!IsSafeWebUrl(url)) continue;
                if (label.Length == 0) label = NameFromUrl(url);

                links.Add(new VersionLink(label, url));
            }

            return links;
        }

        public static bool IsSafeWebUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

        private static bool LooksLikeUrl(string text) =>
            text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        private static string NameFromUrl(string url)
        {
            try
            {
                string path = new Uri(url).AbsolutePath.TrimEnd('/');
                string name = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(name) ? url : Uri.UnescapeDataString(name);
            }
            catch { return url; }
        }
    }
}
