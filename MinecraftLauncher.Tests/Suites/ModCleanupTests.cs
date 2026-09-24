using System;
using System.IO;
using System.Linq;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Clearing the turned-off jars out of a mods folder.
    /// </summary>
    /// <remarks>
    /// Switching a version between loaders a few times fills the folder with
    /// <c>.jar.disabled</c> files until the list stops being readable, which is what
    /// this is for. It is also the only button on the Mods tab that removes anything,
    /// so the checks here are mostly about what it must **not** touch.
    ///
    /// <c>ModManager.DeleteFile</c> is pointed at a plain delete for the duration.
    /// Shipped, these go to the Recycle Bin; doing that here would leave fixtures in
    /// the user's bin on every run of the suite.
    /// </remarks>
    public static class ModCleanupTests
    {
        public static void Run()
        {
            var was = ModManager.DeleteFile;
            var recycled = new System.Collections.Generic.List<string>();
            ModManager.DeleteFile = p => { recycled.Add(Path.GetFileName(p)); File.Delete(p); };

            string dir = Path.Combine(Path.GetTempPath(), "mc-cleanup-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            try
            {
                Choosing(dir);
                Clearing(dir, recycled);
                Refusing(dir);
            }
            finally
            {
                ModManager.DeleteFile = was;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void Fill(string dir)
        {
            foreach (string f in Directory.GetFiles(dir)) File.Delete(f);

            File.WriteAllText(Path.Combine(dir, "sodium-0.6.0.jar"), "on");
            File.WriteAllText(Path.Combine(dir, "lithium-0.12.jar"), "on");
            File.WriteAllText(Path.Combine(dir, "sodium-0.5.12.jar.disabled"), "off");
            File.WriteAllText(Path.Combine(dir, "create-forge-1.20.jar.disabled"), "off");
            File.WriteAllText(Path.Combine(dir, "oldmod.jar.disabled"), "off");

            // Not a mod at all. A folder holds configs and stray files, and none of
            // them are this button's business.
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "keep me");
        }

        private static void Choosing(string dir)
        {
            Section("which files it picks");

            Fill(dir);

            var disabled = ModManager.DisabledIn(dir);

            Expect("it finds the turned-off jars", disabled.Count, 3);
            Check("and only those",
                  disabled.All(m => m.FileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)),
                  string.Join(", ", disabled.Select(m => m.FileName)));

            // The display name is what the confirmation shows, and it must read like
            // the mod rather than like the rename that turned it off.
            Check("the name shown drops the .disabled",
                  disabled.All(m => m.DisplayName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)),
                  string.Join(", ", disabled.Select(m => m.DisplayName)));

            Check("an enabled jar is never listed",
                  disabled.All(m => !m.FileName.Contains("sodium-0.6.0")),
                  string.Join(", ", disabled.Select(m => m.FileName)));
        }

        private static void Clearing(string dir, System.Collections.Generic.List<string> recycled)
        {
            Section("clearing them out");

            Fill(dir);
            recycled.Clear();

            var result = ModManager.RemoveDisabled(dir);

            Expect("it reports what it removed", result.Removed.Count, 3);
            Expect("and nothing failed", result.Failed.Count, 0);

            // The point of the whole thing.
            Check("no .disabled files are left",
                  Directory.GetFiles(dir, "*.disabled").Length == 0,
                  string.Join(", ", Directory.GetFiles(dir, "*.disabled").Select(Path.GetFileName)));

            // And the point of being careful about it.
            Check("the enabled mods are still there",
                  File.Exists(Path.Combine(dir, "sodium-0.6.0.jar")) &&
                  File.Exists(Path.Combine(dir, "lithium-0.12.jar")));
            Check("a file that is not a mod is left alone",
                  File.Exists(Path.Combine(dir, "notes.txt")));

            Check("every removal went through the recycling path",
                  recycled.Count == 3 && recycled.All(n => n.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)),
                  string.Join(", ", recycled));

            // Running it again is a no-op, not an error.
            var again = ModManager.RemoveDisabled(dir);
            Expect("running it again removes nothing", again.Removed.Count, 0);
            Expect("and still does not fail", again.Failed.Count, 0);
        }

        private static void Refusing(string dir)
        {
            Section("when there is nothing to do");

            string empty = Path.Combine(dir, "empty");
            Directory.CreateDirectory(empty);

            var none = ModManager.RemoveDisabled(empty);
            Expect("an empty folder removes nothing", none.Removed.Count, 0);

            var missing = ModManager.RemoveDisabled(Path.Combine(dir, "no-such-folder"));
            Expect("a folder that does not exist is not an error", missing.Removed.Count, 0);

            var blank = ModManager.RemoveDisabled("");
            Expect("nor is no folder at all", blank.Removed.Count, 0);

            // A folder of nothing but enabled mods must come back untouched — the
            // failure that would matter most.
            string safe = Path.Combine(dir, "all-on");
            Directory.CreateDirectory(safe);
            File.WriteAllText(Path.Combine(safe, "a.jar"), "on");
            File.WriteAllText(Path.Combine(safe, "b.jar"), "on");

            var untouched = ModManager.RemoveDisabled(safe);
            Expect("a folder with nothing turned off loses nothing", untouched.Removed.Count, 0);
            Expect("and still has both mods", Directory.GetFiles(safe, "*.jar").Length, 2);
        }
    }
}
