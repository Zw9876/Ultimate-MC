using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// Version comparison and the two range syntaxes the loaders use.
    /// </summary>
    /// <remarks>
    /// The original PowerShell launcher compared versions as plain strings, which ranks
    /// "9" above "10" and can put an older library on the classpath. That is listed in
    /// HANDOFF section 11 as a fixed reference bug, so it is pinned here.
    /// </remarks>
    public static class VersionAndRangeTests
    {
        public static void Run()
        {
            Ordering();
            MavenRanges();
            FabricPredicates();
        }

        private static void Ordering()
        {
            Section("version ordering");

            Check("10 outranks 9, which string sorting gets wrong",
                  MavenVersion.Compare("10", "9") > 0);
            Check("1.20.10 outranks 1.20.9", MavenVersion.Compare("1.20.10", "1.20.9") > 0);
            Check("equal versions compare equal", MavenVersion.Compare("1.2.3", "1.2.3") == 0);
            Check("more segments outrank fewer", MavenVersion.Compare("1.0.1", "1.0") > 0);
            Check("a release outranks its own pre-release",
                  MavenVersion.Compare("1.0", "1.0-beta") > 0);
            Check("21.1.248 outranks 21.0-beta",
                  MavenVersion.Compare("21.1.248", "21.0-beta") > 0);
            Check("0.19.3 outranks 0.9.0 — 19 is not nine",
                  MavenVersion.Compare("0.19.3", "0.9.0") > 0);
            Check("null sorts below anything", MavenVersion.Compare(null, "0.1") < 0);
        }

        private static void MavenRanges()
        {
            Section("Maven ranges (Forge and NeoForge)");

            Check("[46,) accepts 47", VersionRange.SatisfiesMaven("47", "[46,)") == true);
            Check("[46,) accepts 46 itself", VersionRange.SatisfiesMaven("46", "[46,)") == true);
            Check("[46,) rejects 45", VersionRange.SatisfiesMaven("45", "[46,)") == false);
            Check("(46,) excludes 46", VersionRange.SatisfiesMaven("46", "(46,)") == false);
            Check("[1.20,1.21) accepts 1.20.4",
                  VersionRange.SatisfiesMaven("1.20.4", "[1.20,1.21)") == true);
            Check("[1.20,1.21) rejects 1.21",
                  VersionRange.SatisfiesMaven("1.21", "[1.20,1.21)") == false);
            Check("(,2.0] accepts 1.9", VersionRange.SatisfiesMaven("1.9", "(,2.0]") == true);
            Check("(,2.0] accepts 2.0 itself", VersionRange.SatisfiesMaven("2.0", "(,2.0]") == true);
            Check("[21.0-beta,) accepts 21.1.248 — a real NeoForge range",
                  VersionRange.SatisfiesMaven("21.1.248", "[21.0-beta,)") == true);
            Check("a bare version reads as 'this or newer'",
                  VersionRange.SatisfiesMaven("2.0", "1.0") == true);

            // Refusing to answer matters as much as answering: a warning raised against
            // a mod that is actually fine teaches people to ignore warnings.
            Check("a union range is refused rather than misread",
                  VersionRange.SatisfiesMaven("1.5", "[1,2),[3,)") is null);
            Check("a word is refused, not compared as a version",
                  VersionRange.SatisfiesMaven("1.0", "wat") is null);
            Check("an empty range says nothing", VersionRange.SatisfiesMaven("1.0", "") is null);
            Check("an unknown installed version says nothing",
                  VersionRange.SatisfiesMaven(null, "[1,)") is null);
        }

        private static void FabricPredicates()
        {
            Section("Fabric predicates");

            Check(">=0.18.4 accepts 0.19.3", VersionRange.SatisfiesFabric("0.19.3", ">=0.18.4") == true);
            Check(">=0.18.4 accepts 0.18.4 exactly",
                  VersionRange.SatisfiesFabric("0.18.4", ">=0.18.4") == true);
            Check(">=0.18.4 rejects 0.16.0", VersionRange.SatisfiesFabric("0.16.0", ">=0.18.4") == false);
            Check(">=0.18.4 rejects 0.9.0", VersionRange.SatisfiesFabric("0.9.0", ">=0.18.4") == false);
            Check("* accepts anything", VersionRange.SatisfiesFabric("0.1", "*") == true);
            Check("an exact version matches itself",
                  VersionRange.SatisfiesFabric("1.2.3", "1.2.3") == true);
            Check("an exact version rejects another",
                  VersionRange.SatisfiesFabric("1.2.4", "1.2.3") == false);

            Check("a conjunction holds when both parts do",
                  VersionRange.SatisfiesFabric("0.2.5", ">=0.2.3 <0.3") == true);
            Check("a conjunction fails when one does not",
                  VersionRange.SatisfiesFabric("0.3.1", ">=0.2.3 <0.3") == false);

            Check("^1.2.0 accepts 1.5.0", VersionRange.SatisfiesFabric("1.5.0", "^1.2.0") == true);
            Check("^1.2.0 rejects 2.0.0", VersionRange.SatisfiesFabric("2.0.0", "^1.2.0") == false);
            Check("~1.2.0 accepts 1.2.9", VersionRange.SatisfiesFabric("1.2.9", "~1.2.0") == true);
            Check("~1.2.0 rejects 1.3.0", VersionRange.SatisfiesFabric("1.3.0", "~1.2.0") == false);

            Check("one unreadable part refuses the whole predicate",
                  VersionRange.SatisfiesFabric("1.0", ">=1.0 @@@") is null);
            Check("an x placeholder is not reasoned about",
                  VersionRange.SatisfiesFabric("1.2.3", "1.2.x") is null);
            Check("a null version says nothing", VersionRange.SatisfiesFabric(null, ">=1.0") is null);
        }
    }
}
