using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Whether Windows is letting this machine's processor run at full speed.
    /// </summary>
    /// <remarks>
    /// Worth surfacing on a host, because a Minecraft server lives and dies on
    /// single-thread speed and this is the quietest way to lose it. A workstation set
    /// up for CAD may be on a vendor or managed power plan, and a maximum processor
    /// state of 99% is a well-known way of switching turbo off entirely — the machine
    /// looks healthy, the clock just never rises.
    /// </remarks>
    public static class MachinePower
    {
        private static readonly Regex RxScheme =
            new(@"GUID:\s*([0-9a-fA-F-]{36})\s*(?:\(([^)]*)\))?", RegexOptions.Compiled);

        private static readonly Regex RxAcIndex =
            new(@"Current AC Power Setting Index:\s*0x([0-9a-fA-F]+)", RegexOptions.Compiled);

        private static string? _planName;
        private static int? _maxPercent;
        private static bool _read;

        /// <summary>Friendly name of the active power plan, or null if unknown.</summary>
        public static string? PlanName { get { Read(); return _planName; } }

        /// <summary>Maximum processor state as a percentage, or null if unknown.</summary>
        public static int? MaxProcessorPercent { get { Read(); return _maxPercent; } }

        /// <summary>
        /// A warning worth showing, or null when nothing is holding the processor back.
        /// </summary>
        public static string? Advice()
        {
            Read();

            if (_maxPercent is int max && max < 100)
                return $"Windows is capping this processor at {max}%, which switches off turbo. " +
                       "Set the maximum processor state to 100% for hosting.";

            if (_planName is not null &&
                _planName.Contains("saver", StringComparison.OrdinalIgnoreCase))
                return $"Power plan is \"{_planName}\" — switch to High performance for hosting.";

            return null;
        }

        /// <summary>Parses the GUID and friendly name out of powercfg's scheme line.</summary>
        internal static (string? Guid, string? Name) ParseScheme(string output)
        {
            var m = RxScheme.Match(output ?? "");
            if (!m.Success) return (null, null);

            string? name = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
            return (m.Groups[1].Value, string.IsNullOrWhiteSpace(name) ? null : name);
        }

        /// <summary>Parses the AC setting index powercfg prints as hex.</summary>
        internal static int? ParseAcPercent(string output)
        {
            var m = RxAcIndex.Match(output ?? "");
            if (!m.Success) return null;

            return int.TryParse(m.Groups[1].Value, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out int value) && value is >= 0 and <= 100
                ? value
                : null;
        }

        private static void Read()
        {
            if (_read) return;
            _read = true;   // one attempt only; this is a nicety, not a feature

            try
            {
                var (guid, name) = ParseScheme(RunPowerCfg("/getactivescheme"));
                _planName = name;

                if (guid is not null)
                    _maxPercent = ParseAcPercent(
                        RunPowerCfg($"/query {guid} SUB_PROCESSOR PROCTHROTTLEMAX"));
            }
            catch
            {
                // Locked-down machines can refuse this outright. Saying nothing is fine.
            }
        }

        private static string RunPowerCfg(string arguments)
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (p is null) return "";

            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } }
            return output;
        }
    }
}
