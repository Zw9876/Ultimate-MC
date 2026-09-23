using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MinecraftLauncher.Core
{
    public sealed class ModEntry
    {
        public string FileName    { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public bool   Enabled     { get; init; }
        public double SizeKb      { get; init; }
        public string FullPath    { get; init; } = "";

        /// <summary>Which loaders the jar declares itself for. See <see cref="ModInspector"/>.</summary>
        public ModLoaders Loaders { get; init; }

        public string StateText  => Enabled ? "On" : "Off";
        public string SizeText   => $"{SizeKb:0.#} KB";
        public string LoaderText => ModInspector.Describe(Loaders);
    }

    public sealed class ModAddResult
    {
        public int Added { get; init; }
        public List<string> Skipped { get; init; } = new();
    }

    /// <summary>
    /// Mod and plugin management. Enabled state is carried entirely by the file
    /// extension — loaders ignore anything ending in .jar.disabled — so there is no
    /// separate state file to keep in sync.
    /// </summary>
    public static class ModManager
    {
        private const string DisabledSuffix = ".disabled";

        public static bool UsesPlugins(string loaderType) =>
            loaderType.Equals("Paper",  StringComparison.OrdinalIgnoreCase) ||
            loaderType.Equals("Purpur", StringComparison.OrdinalIgnoreCase);

        public static string Noun(bool server, string loaderType) =>
            server && UsesPlugins(loaderType) ? "Plugin" : "Mod";

        public static string FolderFor(string version, bool server, string loaderType)
        {
            string folder = server
                ? Path.Combine(Paths.ServerDir(loaderType, version), UsesPlugins(loaderType) ? "plugins" : "mods")
                : Path.Combine(Paths.VersionDir(version), "mods");

            Directory.CreateDirectory(folder);
            return folder;
        }

        public static List<ModEntry> List(string folder)
        {
            var mods = new List<ModEntry>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return mods;

            foreach (var path in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(path);
                bool disabled = IsDisabled(name);
                if (!disabled && !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;

                var info = new FileInfo(path);
                mods.Add(new ModEntry
                {
                    FileName    = name,
                    DisplayName = disabled ? name[..^DisabledSuffix.Length] : name,
                    Enabled     = !disabled,
                    SizeKb      = Math.Round(info.Length / 1024d, 1),
                    FullPath    = path,
                    Loaders     = ModInspector.Detect(path)
                });
            }

            return mods.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Copies jars in, never overwriting an existing file of the same name.</summary>
        public static ModAddResult Add(string folder, IEnumerable<string> sourcePaths)
        {
            Directory.CreateDirectory(folder);

            int added = 0;
            var skipped = new List<string>();

            foreach (var source in sourcePaths)
            {
                if (!File.Exists(source)) continue;

                string fileName = Path.GetFileName(source);
                string dest = Path.Combine(folder, fileName);

                if (File.Exists(dest) || File.Exists(dest + DisabledSuffix))
                {
                    skipped.Add(fileName);
                    continue;
                }

                try
                {
                    File.Copy(source, dest);
                    added++;
                }
                catch
                {
                    skipped.Add(fileName);
                }
            }

            return new ModAddResult { Added = added, Skipped = skipped };
        }

        public static bool Remove(string folder, string fileName)
        {
            string path = Path.Combine(folder, fileName);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        public static bool SetEnabled(string folder, string fileName, bool enabled)
        {
            string current = Path.Combine(folder, fileName);
            if (!File.Exists(current)) return false;

            bool disabled = IsDisabled(fileName);
            if (enabled == !disabled) return false;

            string target = Path.Combine(folder,
                enabled ? fileName[..^DisabledSuffix.Length] : fileName + DisabledSuffix);

            if (File.Exists(target)) return false;

            File.Move(current, target);
            return true;
        }

        /// <summary>
        /// Turns off every enabled mod this loader cannot run, and returns their names.
        /// </summary>
        /// <remarks>
        /// Nothing is deleted — disabling is a rename to <c>.jar.disabled</c>, so
        /// switching back to the other loader and running this again restores the set.
        /// That is what makes one shared folder workable: the jars for both loaders can
        /// live in it, with only the right ones enabled.
        /// </remarks>
        public static List<string> DisableIncompatible(string folder, string loaderType)
        {
            var turnedOff = new List<string>();

            foreach (var mod in List(folder))
            {
                if (!mod.Enabled || ModInspector.IsCompatible(mod.Loaders, loaderType)) continue;

                if (SetEnabled(folder, mod.FileName, false))
                    turnedOff.Add(mod.DisplayName);
            }

            return turnedOff;
        }

        /// <summary>
        /// Turns back on every disabled mod this loader *can* run — the other half of
        /// switching loaders, so the trip back does not have to be done by hand.
        /// </summary>
        public static List<string> EnableCompatible(string folder, string loaderType)
        {
            var turnedOn = new List<string>();

            foreach (var mod in List(folder))
            {
                // Only jars that positively name this loader, so unknown ones are not
                // switched on by a button the user pressed for a different reason.
                if (mod.Enabled || mod.Loaders == ModLoaders.None) continue;
                if (!ModInspector.IsCompatible(mod.Loaders, loaderType)) continue;

                if (SetEnabled(folder, mod.FileName, true))
                    turnedOn.Add(mod.DisplayName);
            }

            return turnedOn;
        }

        public static void OpenFolder(string folder)
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }

        private static bool IsDisabled(string fileName) =>
            fileName.EndsWith(".jar" + DisabledSuffix, StringComparison.OrdinalIgnoreCase);
    }
}
