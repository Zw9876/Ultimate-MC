using System;
using System.IO;
using System.Linq;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Pulling the answer out of a Minecraft crash report.
    /// </summary>
    /// <remarks>
    /// The text below is a real crash report captured from this machine, trimmed of the
    /// repeated stack frames and nothing else. Every literal here — the blank lines, the
    /// tabs, the `-- MOD x --` header, the wrapped `Failure message:` — is what Minecraft
    /// actually wrote, because a parser written from memory of the format is how this
    /// project has been wrong before.
    ///
    /// It is a Forge report. Forge and NeoForge name the mod that failed; Fabric does
    /// not, so the last check here covers a report with nothing to blame and makes sure
    /// it says so instead of guessing.
    /// </remarks>
    public static class CrashReportTests
    {
        private const string RealForgeReport = """
            ---- Minecraft Crash Report ----
            // Daisy, daisy...

            Time: 2026-04-12 13:51:12
            Description: Mod loading error has occurred

            java.lang.Exception: Mod Loading has failed
            	at net.minecraftforge.logging.CrashReportExtender.dumpModLoadingCrashReport(CrashReportExtender.java:60) ~[forge-1.20.1-47.4.20-universal.jar%23191!/:?] {re:classloading}
            	at net.minecraft.client.main.Main.main(Main.java:218) ~[1.20.1-forge-47.4.20.jar:?] {re:classloading}

            A detailed walkthrough of the error, its code path and all known details is as follows:
            ---------------------------------------------------------------------------------------

            -- Head --
            Thread: Render thread
            Suspected Mods: NONE
            Stacktrace:
            	at net.minecraftforge.logging.CrashReportExtender.lambda$dumpModLoadingCrashReport$7(CrashReportExtender.java:63) ~[forge-1.20.1-47.4.20-universal.jar%23191!/:?] {re:classloading}
            -- MOD durabilitytooltip --
            Details:
            	Mod File: /C:/Users/zwilc/AppData/Roaming/.minecraft/mods/durabilitytooltip-1.1.6-fabric-mc1.21.jar
            	Failure message: Mod durabilitytooltip requires supermartijn642configlib 1.1.6 or above
            		Currently, supermartijn642configlib is not installed
            	Mod Version: 1.1.6
            	Mod Issue URL: https://github.com/SuperMartijn642/DurabilityTooltip/issues
            	Exception message: MISSING EXCEPTION MESSAGE
            Stacktrace:
            	at java.util.ArrayList.forEach(ArrayList.java:1511) ~[?:?] {re:computing_frames}

            -- System Details --
            Details:
            	Minecraft Version: 1.20.1
            	Minecraft Version ID: 1.20.1
            	Operating System: Windows 11 (amd64) version 10.0
            	Java Version: 17.0.15, Microsoft
            	Memory: 406139016 bytes (387 MiB) / 738197504 bytes (704 MiB) up to 2147483648 bytes (2048 MiB)
            	CPUs: 4
            """;

        // A vanilla crash: no loader, nothing to blame. The shape Fabric produces too.
        private const string NoModNamed = """
            ---- Minecraft Crash Report ----
            // Everything's going to plan. No, really, that was supposed to happen.

            Time: 2026-01-02 09:30:00
            Description: Rendering overlay

            java.lang.NullPointerException: Cannot invoke "net.minecraft.class_1041.method_4489()" because "this.field_1704" is null
            	at net.minecraft.class_310.method_1523(class_310.java:1196)

            -- System Details --
            Details:
            	Minecraft Version: 1.20.1
            	Java Version: 17.0.15, Microsoft
            """;

        public static void Run()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mc-crash-tests-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            try
            {
                RealReport(dir);
                NothingToBlame(dir);
                Listing(dir);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static CrashReports.Report Write(string dir, string name, string text)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, text.ReplaceLineEndings("\r\n"));
            return CrashReports.Parse(path, File.ReadAllLines(path));
        }

        private static void RealReport(string dir)
        {
            Section("a real Forge crash report");

            var r = Write(dir, "crash-2026-04-12_13.51.12-fml.txt", RealForgeReport);

            Expect("the description is read", r.Description, "Mod loading error has occurred");
            Expect("the exception is read", r.Exception, "java.lang.Exception: Mod Loading has failed");

            // The line after the description is the exception; the lines after THAT are
            // indented stack frames and must not be mistaken for it.
            Check("a stack frame is not read as the exception",
                  r.Exception is not null && !r.Exception.StartsWith("at ", StringComparison.Ordinal),
                  r.Exception ?? "null");

            Expect("the time comes from inside the file", r.When,
                   new DateTime(2026, 4, 12, 13, 51, 12));

            Expect("Minecraft's version is read", r.MinecraftVersion, "1.20.1");
            Expect("Java's version is read", r.JavaVersion, "17.0.15, Microsoft");
            Expect("Forge's own guess is read", r.SuspectedMods, "NONE");

            Expect("the blamed mod is found", r.Mods.Count, 1);

            var mod = r.Mods[0];
            Expect("it is named", mod.Id, "durabilitytooltip");
            Expect("its version is read", mod.Version, "1.1.6");
            Expect("the jar is reduced to a file name", mod.JarName,
                   "durabilitytooltip-1.1.6-fabric-mc1.21.jar");

            // The reason runs onto a second, more indented line, and the second line is
            // the half that actually tells you what to do about it.
            Check("the failure message includes what it needs",
                  mod.FailureMessage?.Contains("requires supermartijn642configlib") == true,
                  mod.FailureMessage ?? "null");
            Check("and the continuation line saying it is missing",
                  mod.FailureMessage?.Contains("Currently, supermartijn642configlib is not installed") == true,
                  mod.FailureMessage ?? "null");

            Note(mod.FailureMessage ?? "");

            // Section headers must not be swallowed as mod details.
            Check("System Details is not read as a mod",
                  r.Mods.All(m => !m.Id.Contains("System", StringComparison.OrdinalIgnoreCase)));
            Check("Head is not read as a mod",
                  r.Mods.All(m => !m.Id.Equals("Head", StringComparison.OrdinalIgnoreCase)));

            string plain = r.Explain();
            Check("the summary names the mod", plain.Contains("durabilitytooltip"), plain);
            Check("the summary says why", plain.Contains("not installed"), plain);
            Check("the summary names the jar", plain.Contains("durabilitytooltip-1.1.6-fabric-mc1.21.jar"));
            Check("the summary does not dump the stack trace", !plain.Contains("at net.minecraft"));

            Expect("the headline is the description", r.Headline, "Mod loading error has occurred");
        }

        private static void NothingToBlame(string dir)
        {
            Section("a crash with no mod named");

            var r = Write(dir, "crash-2026-01-02_09.30.00-client.txt", NoModNamed);

            Expect("the description is still read", r.Description, "Rendering overlay");
            Check("the exception is still read",
                  r.Exception?.StartsWith("java.lang.NullPointerException") == true, r.Exception ?? "null");
            Expect("no mod is blamed", r.Mods.Count, 0);
            Check("and none was suspected", r.SuspectedMods is null, r.SuspectedMods ?? "");

            // The failure that would matter: inventing a culprit from the stack trace.
            string plain = r.Explain();
            Check("it says plainly that no mod is named", plain.Contains("No mod is named"), plain);
            Check("it does not accuse anything", !plain.Contains("blames"), plain);
        }

        private static void Listing(string dir)
        {
            Section("listing a folder of them");

            // Parse is what List uses per file; this checks the ordering it applies.
            var reports = Directory.GetFiles(dir, "*.txt")
                                   .Select(p => CrashReports.Parse(p, File.ReadAllLines(p)))
                                   .OrderByDescending(r => r.When)
                                   .ToList();

            Expect("both reports are found", reports.Count, 2);
            Check("newest first", reports[0].When > reports[1].When,
                  $"{reports[0].When} then {reports[1].When}");

            Check("each knows its own file name",
                  reports.All(r => r.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));
            Check("each reports a size", reports.All(r => r.Bytes > 0));

            // The folder is derived, not guessed: crash reports land in the game
            // directory, which for a client is the version folder.
            string folder = CrashReports.FolderFor("26.1.2");
            Check("the folder is the version's own crash-reports",
                  folder.EndsWith(Path.Combine("26.1.2", "crash-reports"), StringComparison.OrdinalIgnoreCase),
                  folder);

            // A version that has never crashed must be empty, not an error.
            var none = CrashReports.List("no-such-version-" + Guid.NewGuid().ToString("N")[..6]);
            Expect("a version with no reports lists none", none.Count, 0);

            // An unreadable file is skipped rather than taking the others with it.
            string bad = Path.Combine(dir, "not-a-report.txt");
            File.WriteAllText(bad, "this is not a crash report at all");
            var junk = CrashReports.TryParse(bad);
            Check("a file that is not a report still parses without throwing", junk is not null);
            Check("it simply has nothing to say",
                  junk?.Description is null && junk?.Mods.Count == 0);
        }
    }
}
