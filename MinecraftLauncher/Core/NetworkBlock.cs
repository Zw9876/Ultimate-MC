using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// A download that never reached its host: something on the network answered
    /// in its place with a page of its own.
    /// </summary>
    /// <remarks>
    /// Its own type so callers can say what happened in plain words rather than
    /// passing on whatever the parser made of a web page - which on the workstations
    /// was "'&lt;' is an invalid start of a value", a message about JSON syntax for
    /// what was actually a content filter.
    /// </remarks>
    public sealed class NetworkBlockedException : IOException
    {
        public string Url { get; }
        public string Host { get; }

        /// <summary>The start of what arrived instead, for the error log.</summary>
        public string Snippet { get; }

        public NetworkBlockedException(string url, string snippet)
            : base(NetworkBlock.Explain(url, snippet))
        {
            Url = url;
            Host = NetworkBlock.HostOf(url);
            Snippet = snippet;
        }
    }

    /// <summary>
    /// Recognising a content filter's substitute page, so the launcher can say "this
    /// host is blocked here" instead of failing somewhere downstream.
    /// </summary>
    /// <remarks>
    /// Written against the real thing. On 2026-10-23 a workstation asked
    /// launchermeta.mojang.com for the version manifest and got back, with HTTP 200,
    /// sixty-five characters of ContentKeeper's silent block page:
    ///
    /// <code>
    /// &lt;!--
    /// function Blank()
    /// {
    /// var nothing=0;
    /// }
    /// Blank();
    /// //--&gt;
    /// </code>
    ///
    /// The status code said success, so nothing on the HTTP layer can catch this.
    /// What can be caught is the body: every file the launcher downloads is JSON
    /// (starts with <c>{</c>), a jar or zip (starts with <c>PK</c>), or an asset blob
    /// - and none of those begin like an HTML page. So the test is deliberately
    /// narrow: the opening of a web page or a known filter's marker. It does NOT
    /// match a bare <c>&lt;</c>, so <c>&lt;?xml</c> metadata from a Maven
    /// repository is never mistaken for a block.
    /// </remarks>
    public static class NetworkBlock
    {
        private static readonly string[] PageOpenings =
        {
            "<!--", "<html", "<!doctype", "<head", "<body", "<script", "<meta"
        };

        private static readonly string[] FilterMarkers =
        {
            "function Blank()",     // ContentKeeper's silent block page, captured
            "contentkeeper"
        };

        /// <summary>Whether this text is a filter's page rather than the file asked for.</summary>
        public static bool LooksLikeFilterPage(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // A BOM and leading whitespace are not evidence either way.
            string t = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
            if (t.Length == 0) return false;

            foreach (string opening in PageOpenings)
                if (t.StartsWith(opening, StringComparison.OrdinalIgnoreCase)) return true;

            foreach (string marker in FilterMarkers)
                if (t.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>The same test against the first bytes of a downloaded file.</summary>
        public static bool LooksLikeFilterPage(byte[] head) =>
            LooksLikeFilterPage(Encoding.UTF8.GetString(head));

        /// <summary>Reads the start of a file and tests it. False if it cannot be read.</summary>
        public static bool FileIsFilterPage(string path, out string snippet)
        {
            snippet = "";
            try
            {
                using var fs = File.OpenRead(path);
                var buf = new byte[512];
                int n = fs.Read(buf, 0, buf.Length);
                snippet = Encoding.UTF8.GetString(buf, 0, n);
                return LooksLikeFilterPage(snippet);
            }
            catch { return false; }
        }

        /// <summary>
        /// Parses JSON, turning a filter's page into a <see cref="NetworkBlockedException"/>
        /// instead of a parser error about the character <c>&lt;</c>.
        /// </summary>
        public static JsonDocument ParseOrBlocked(string url, string text)
        {
            try
            {
                return JsonDocument.Parse(text);
            }
            catch (JsonException) when (LooksLikeFilterPage(text))
            {
                throw new NetworkBlockedException(url, Shorten(text));
            }
        }

        public static string HostOf(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

        /// <summary>The message people actually read, so it is written for them.</summary>
        public static string Explain(string url, string snippet)
        {
            string host = HostOf(url);
            return
                $"{host} is blocked on this network. Something answered in its place with a " +
                $"{snippet.Length}-character web page instead of the file - that is what a " +
                "content filter does, and the launcher cannot get past it. " +
                $"Ask for {host} to be allowed, or install this version on a computer that " +
                "can reach it and copy it across.";
        }

        private static string Shorten(string text)
        {
            string t = text.Trim();
            return t.Length <= 200 ? t : t.Substring(0, 200);
        }
    }
}
