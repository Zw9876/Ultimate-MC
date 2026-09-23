using System;
using System.Collections.Generic;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// Compares Maven-style version strings segment by segment, numerically where
    /// both segments are numeric. The original launcher compared these as plain
    /// strings, which ranks "9" above "10" and can put an older library on the
    /// classpath.
    /// </summary>
    public static class MavenVersion
    {
        private static readonly char[] Separators = { '.', '-', '_', '+' };

        public static int Compare(string? a, string? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a is null) return -1;
            if (b is null) return 1;

            var ta = a.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            var tb = b.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

            int n = Math.Max(ta.Length, tb.Length);
            for (int i = 0; i < n; i++)
            {
                string sa = i < ta.Length ? ta[i] : "";
                string sb = i < tb.Length ? tb[i] : "";

                bool na = long.TryParse(sa, out long va);
                bool nb = long.TryParse(sb, out long vb);

                // A version that ran out of segments outranks one continuing with a
                // qualifier (1.0 > 1.0-beta) but loses to one continuing numerically
                // (1.0 < 1.0.1).
                if (sa.Length == 0) return nb ? -1 : 1;
                if (sb.Length == 0) return na ? 1 : -1;

                int c;
                if (na && nb) c = va.CompareTo(vb);
                else if (na) c = 1;
                else if (nb) c = -1;
                else c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);

                if (c != 0) return c;
            }
            return 0;
        }

        public static IComparer<string> Comparer { get; } =
            Comparer<string>.Create((x, y) => Compare(x, y));
    }
}
