using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Decides whether an installed version satisfies the requirement a mod declares.
    /// </summary>
    /// <remarks>
    /// Two syntaxes are in play, because the loaders disagree. Forge and NeoForge use
    /// Maven ranges in their TOML — <c>[46,)</c>, <c>[1.20,1.21)</c>. Fabric uses
    /// npm-style predicates in JSON — <c>&gt;=0.18.4</c>, sometimes several separated
    /// by spaces meaning "and".
    ///
    /// Everything here answers with a nullable bool, and **null means "cannot tell"**.
    /// That distinction matters more than it looks: a warning shown against a mod that
    /// is actually fine teaches people to ignore warnings, so anything unparsed is
    /// passed over in silence rather than guessed at.
    /// </remarks>
    public static class VersionRange
    {
        // [46,)   (,1.0]   [1.20,1.21)   [21.0-beta,)
        private static readonly Regex RxMaven = new(
            @"^\s*(?<lo>[\[\(])\s*(?<min>[^,\[\]\(\)]*)\s*,\s*(?<max>[^,\[\]\(\)]*)\s*(?<hi>[\]\)])\s*$",
            RegexOptions.Compiled);

        private static readonly Regex RxPredicate = new(
            @"^(?<op><=|>=|<|>|\^|~|=)?\s*(?<version>.+)$", RegexOptions.Compiled);

        /// <summary>
        /// Whether <paramref name="version"/> falls inside a Maven range, or null when
        /// the range is not a shape this understands.
        /// </summary>
        public static bool? SatisfiesMaven(string? version, string? range)
        {
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(range)) return null;

            range = range.Trim();

            // A bare version in Maven is a soft "this or newer" recommendation — but
            // only if it is a version at all. Without this check any stray word gets
            // compared as though it were one, and answers confidently.
            if (!range.StartsWith('[') && !range.StartsWith('('))
                return LooksLikeVersion(range) ? MavenVersion.Compare(version, range) >= 0 : null;

            // Unions like "[1,2),[3,)" are legal and rare; refuse rather than misread.
            var m = RxMaven.Match(range);
            if (!m.Success) return null;

            string min = m.Groups["min"].Value.Trim();
            string max = m.Groups["max"].Value.Trim();
            bool minInclusive = m.Groups["lo"].Value == "[";
            bool maxInclusive = m.Groups["hi"].Value == "]";

            if (min.Length > 0)
            {
                int c = MavenVersion.Compare(version, min);
                if (c < 0 || (c == 0 && !minInclusive)) return false;
            }

            if (max.Length > 0)
            {
                int c = MavenVersion.Compare(version, max);
                if (c > 0 || (c == 0 && !maxInclusive)) return false;
            }

            return true;
        }

        /// <summary>
        /// Whether <paramref name="version"/> satisfies a Fabric predicate. Several
        /// predicates separated by spaces all have to hold.
        /// </summary>
        public static bool? SatisfiesFabric(string? version, string? predicate)
        {
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(predicate)) return null;

            predicate = predicate.Trim();
            if (predicate == "*") return true;

            bool sawOne = false;

            foreach (string part in predicate.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                bool? one = SatisfiesOne(version, part);
                if (one is null) return null;        // one unreadable part spoils the answer

                sawOne = true;
                if (one == false) return false;
            }

            return sawOne ? true : null;
        }

        private static bool? SatisfiesOne(string version, string predicate)
        {
            var m = RxPredicate.Match(predicate.Trim());
            if (!m.Success) return null;

            string op = m.Groups["op"].Value;
            string want = m.Groups["version"].Value.Trim();
            if (want.Length == 0 || want == "*") return true;

            // An "x" placeholder means any, which this does not try to reason about.
            if (want.Contains('x', StringComparison.OrdinalIgnoreCase) && op is "" or "=") return null;

            // Anything that is not version-shaped is not something to draw a
            // conclusion from — comparing it would produce a confident wrong answer.
            if (!LooksLikeVersion(want)) return null;

            int c = MavenVersion.Compare(version, want);

            return op switch
            {
                ">=" => c >= 0,
                ">"  => c > 0,
                "<=" => c <= 0,
                "<"  => c < 0,
                "="  => c == 0,
                ""   => c == 0,
                // ~1.2.3 allows patch updates; ^1.2.3 allows minor updates too.
                "~"  => c >= 0 && SameThrough(version, want, 2),
                "^"  => c >= 0 && SameThrough(version, want, 1),
                _    => null
            };
        }

        /// <summary>
        /// Whether a string is plausibly a version at all. Versions start with a digit;
        /// a word does not, and must not be compared as though it were one.
        /// </summary>
        private static bool LooksLikeVersion(string value) =>
            value.Length > 0 && char.IsDigit(value[0]);

        /// <summary>True when two versions agree on their first <paramref name="parts"/> segments.</summary>
        private static bool SameThrough(string version, string want, int parts)
        {
            var a = version.Split('.', '-', '+');
            var b = want.Split('.', '-', '+');

            for (int i = 0; i < parts; i++)
            {
                string sa = i < a.Length ? a[i] : "0";
                string sb = i < b.Length ? b[i] : "0";
                if (!sa.Equals(sb, StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }
    }
}
