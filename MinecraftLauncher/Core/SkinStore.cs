using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinecraftLauncher.Core
{
    public sealed class SkinEntry
    {
        public string Username { get; init; } = "";
        public string Model    { get; init; } = "steve";
        public string FileName { get; init; } = "";
        public string FullPath { get; init; } = "";

        public string ModelLabel => Model == "alex" ? "Alex (slim)" : "Steve (classic)";
    }

    /// <summary>
    /// The central skins folder: one PNG per username plus metadata.json mapping
    /// username → "steve"/"alex". Shared by the Skins tab and the skin server, so
    /// every operation takes a lock — the server handles requests concurrently.
    /// </summary>
    public static class SkinStore
    {
        private static readonly object Gate = new();

        /// <summary>Minecraft's own username rules. Also keeps request paths from escaping the skins folder.</summary>
        private static readonly Regex ValidUsername = new(@"^[A-Za-z0-9_]{1,16}$", RegexOptions.Compiled);

        private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>A 64x64 skin is a few KB; this only exists to stop absurd uploads.</summary>
        public const int MaxSkinBytes = 1024 * 1024;

        public static string Dir
        {
            get
            {
                Directory.CreateDirectory(Paths.Skins);
                return Paths.Skins;
            }
        }

        public static string MetadataFile => Path.Combine(Dir, "metadata.json");

        public static bool IsValidUsername(string? username) =>
            !string.IsNullOrEmpty(username) && ValidUsername.IsMatch(username);

        public static bool LooksLikePng(byte[] data) =>
            data.Length > PngMagic.Length && data.Take(PngMagic.Length).SequenceEqual(PngMagic);

        public static string SkinPath(string username) => Path.Combine(Dir, $"{username}.png");

        public static List<SkinEntry> List()
        {
            lock (Gate)
            {
                var meta = ReadMetadata();
                var skins = new List<SkinEntry>();

                foreach (var file in Directory.EnumerateFiles(Dir, "*.png"))
                {
                    string username = Path.GetFileNameWithoutExtension(file);
                    skins.Add(new SkinEntry
                    {
                        Username = username,
                        Model    = meta.TryGetValue(username, out var m) ? Normalize(m) : "steve",
                        FileName = Path.GetFileName(file),
                        FullPath = file
                    });
                }

                return skins.OrderBy(s => s.Username, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public static string GetModel(string username)
        {
            lock (Gate)
                return ReadMetadata().TryGetValue(username, out var m) ? Normalize(m) : "steve";
        }

        public static void SetModel(string username, string model)
        {
            lock (Gate)
            {
                var meta = ReadMetadata();
                meta[username] = Normalize(model);
                WriteMetadata(meta);
            }
        }

        public static void Add(string sourcePath, string username, string model = "steve")
        {
            if (!IsValidUsername(username))
                throw new ArgumentException(
                    $"'{username}' is not a valid Minecraft username (letters, digits and underscore, up to 16).");

            byte[] data = File.ReadAllBytes(sourcePath);
            if (!LooksLikePng(data))
                throw new InvalidDataException("That file is not a PNG image.");
            if (data.Length > MaxSkinBytes)
                throw new InvalidDataException($"Skin is larger than {MaxSkinBytes / 1024} KB.");

            Save(username, data, model);
        }

        /// <summary>Writes a skin and its model. Used by both the UI and the server's upload endpoint.</summary>
        public static void Save(string username, byte[] png, string model)
        {
            lock (Gate)
            {
                File.WriteAllBytes(SkinPath(username), png);

                var meta = ReadMetadata();
                meta[username] = Normalize(model);
                WriteMetadata(meta);
            }
        }

        public static bool Remove(string username)
        {
            lock (Gate)
            {
                string path = SkinPath(username);
                bool existed = File.Exists(path);
                if (existed) File.Delete(path);

                var meta = ReadMetadata();
                if (meta.Remove(username)) WriteMetadata(meta);

                return existed;
            }
        }

        public static string Normalize(string? model) =>
            string.Equals(model, "alex", StringComparison.OrdinalIgnoreCase) ? "alex" : "steve";

        // Callers must hold Gate.
        private static Dictionary<string, string> ReadMetadata()
        {
            try
            {
                if (!File.Exists(MetadataFile)) return new Dictionary<string, string>();

                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(MetadataFile));

                return parsed ?? new Dictionary<string, string>();
            }
            catch
            {
                // A corrupt metadata file must not take the skin list down with it;
                // models fall back to steve and get rewritten on the next change.
                return new Dictionary<string, string>();
            }
        }

        // Callers must hold Gate.
        private static void WriteMetadata(Dictionary<string, string> meta)
        {
            string json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(MetadataFile, json, new UTF8Encoding(false));
        }
    }
}
