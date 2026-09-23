using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// The script that replaces the launcher's own executable once it has exited.
    /// </summary>
    /// <remarks>
    /// This is the one part of an update that runs when there is no launcher left to
    /// report a failure to — get it wrong and a machine is simply left on the old
    /// build with nothing on screen to say why. It was written parameterised so it
    /// could be exercised against throwaway folders, and then never was.
    ///
    /// It is also the most malware-shaped thing the product does: wait for a process
    /// to die, overwrite an executable, relaunch it. That is why it now lives beside
    /// the launcher under its own name instead of being a randomly named .bat in
    /// %TEMP%, and why these checks cover where it is written as well as what it does.
    /// </remarks>
    public static class SwapScriptTests
    {
        public static void Run()
        {
            Shape();
            Location();
            ForReal();
        }

        private static void Shape()
        {
            Section("what the script says");

            string script = LauncherUpdate.BuildSwapScript(1234, @"C:\install", @"C:\install\staging");

            Check("it waits for the launcher's own process id", script.Contains("PID eq 1234"));
            Check("it moves the staged files into the install folder",
                  script.Contains(@"move /Y ""C:\install\staging\*"" ""C:\install\"""));
            Check("it retries rather than giving up on the first locked file",
                  script.Contains("TRIES") && script.Contains("LSS 15"));
            Check("it starts the new launcher afterwards",
                  script.Contains(@"start """" ""C:\install\MinecraftLauncher.exe"""));
            Check("it clears the staging folder", script.Contains(@"rmdir /S /Q ""C:\install\staging"""));
            Check("it deletes itself", script.Contains(@"del ""%~f0"""));

            // A .cmd with bare LF endings runs, but not dependably across shells; the
            // source file's own endings must not decide this.
            Check("every line ends CRLF, whatever this source file uses",
                  script.Contains("\r\n") && !script.Replace("\r\n", "").Contains('\n'));
        }

        private static void Location()
        {
            Section("where the script is written");

            string name = LauncherUpdate.SwapScriptName;

            Check("it has a fixed name rather than a generated one",
                  name.Length < 32 && !name.Any(char.IsDigit), name);
            Check("it is a .cmd", Path.GetExtension(name) == ".cmd", name);

            // The point of the move: a hidden cmd.exe running a random .bat out of the
            // temp folder, which then overwrites an executable, is the shape antivirus
            // heuristics score as a dropper. The fallback to %TEMP% for a read-only
            // install folder is deliberate and is not covered here.
            string primary = Path.Combine(LauncherPackage.Dir, name);
            Check("by default it sits beside the launcher, not in %TEMP%",
                  !primary.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
                  primary);
        }

        private static void ForReal()
        {
            Section("running it against throwaway folders");

            string root    = Path.Combine(Path.GetTempPath(), "mc-swap-test-" + Guid.NewGuid().ToString("N")[..8]);
            string target  = Path.Combine(root, "install");
            string staging = Path.Combine(target, "update-staging");

            try
            {
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(staging);

                // What the install already has, including a file the update does not
                // touch — a swap that tidies away unrelated files would be a disaster.
                File.WriteAllText(Path.Combine(target, LauncherPackage.ExeName), "old build");
                File.WriteAllText(Path.Combine(target, "config.txt"), "SKIN_SERVER=10.0.0.5");

                File.WriteAllText(Path.Combine(staging, LauncherPackage.ExeName), "new build");
                File.WriteAllText(Path.Combine(staging, "wpfgfx_cor3.dll"), "new sibling");

                // A process that has already exited, so the wait loop falls straight
                // through instead of this test having to sit through a real shutdown.
                using var dead = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
                    { CreateNoWindow = true, UseShellExecute = false })!;
                dead.WaitForExit();

                // Everything except the relaunch: `start` would fire a text file at
                // ShellExecute and put a dialog on someone's screen mid-test.
                string script = LauncherUpdate.BuildSwapScript(dead.Id, target, staging);
                string runnable = string.Join("\r\n",
                    script.Split("\r\n").Where(l => !l.TrimStart().StartsWith("start ")));

                string scriptFile = Path.Combine(root, LauncherUpdate.SwapScriptName);
                File.WriteAllText(scriptFile, runnable, new UTF8Encoding(false));

                using var run = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptFile}\"")
                    { CreateNoWindow = true, UseShellExecute = false })!;

                bool finished = run.WaitForExit(60_000);
                Check("the script runs to completion", finished, "still going after 60s");
                if (!finished) { try { run.Kill(true); } catch { } return; }

                string exe = Path.Combine(target, LauncherPackage.ExeName);

                Expect("the executable is replaced, not left as it was",
                       File.ReadAllText(exe), "new build");
                Expect("the files shipped alongside it arrive too",
                       File.ReadAllText(Path.Combine(target, "wpfgfx_cor3.dll")), "new sibling");
                Expect("files the update does not carry are left alone",
                       File.ReadAllText(Path.Combine(target, "config.txt")), "SKIN_SERVER=10.0.0.5");

                Check("the staging folder is cleared up", !Directory.Exists(staging));
                Check("the script removes itself when it is done", !File.Exists(scriptFile));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }
    }
}
