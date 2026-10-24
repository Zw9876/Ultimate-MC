using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MinecraftLauncher.Core;
using static MinecraftLauncher.Tests.Harness;

namespace MinecraftLauncher.Tests
{
    /// <summary>
    /// A content filter answering in a host's place, and the launcher noticing.
    /// </summary>
    /// <remarks>
    /// On the workstations, launchermeta.mojang.com answers HTTP 200 with
    /// ContentKeeper's silent block page instead of the version manifest. The Setup
    /// tab showed a JSON parser's complaint once, on screen, and wrote nothing to
    /// disk; and the installers ignored a failed hash check at seven places, so a
    /// filter's page saved under a jar's name would have been reported as "Done."
    ///
    /// The fixture below is the real body, captured on 2026-10-23 by
    /// Test-DownloadHosts.ps1 on MAHD2300906006 - not a page written to look like
    /// one. Every parser in this project written from memory was wrong about
    /// something.
    /// </remarks>
    public static class NetworkBlockTests
    {
        /// <summary>ContentKeeper's page, exactly as it arrived: 65 characters, CRLF.</summary>
        private const string Captured =
            "<!--\r\nfunction Blank()\r\n{\r\nvar nothing=0;\r\n}\r\nBlank();\r\n//-->\r\n\r\n";

        private const string ManifestUrl =
            "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

        public static async Task RunAsync()
        {
            Recognising();
            Parsing();
            await DownloadingAsync();
        }

        private static void Recognising()
        {
            Section("recognising a filter's page");

            Expect("the fixture is the size that was captured", Captured.Length, 65);
            Check("ContentKeeper's real page is recognised", NetworkBlock.LooksLikeFilterPage(Captured));
            Check("with a byte-order mark in front of it",
                  NetworkBlock.LooksLikeFilterPage("﻿" + Captured));
            Check("and with whitespace in front of it",
                  NetworkBlock.LooksLikeFilterPage("\r\n  " + Captured));
            Check("as raw bytes too",
                  NetworkBlock.LooksLikeFilterPage(Encoding.UTF8.GetBytes(Captured)));
            Check("an ordinary block page is recognised",
                  NetworkBlock.LooksLikeFilterPage("<html><body>Access denied</body></html>"));

            // The ones that must NOT be mistaken for a block. A false positive here
            // would refuse a perfectly good download and blame the network for it.
            Check("the real manifest is not a block",
                  !NetworkBlock.LooksLikeFilterPage("{\"latest\":{\"release\":\"26.1.2\"},\"versions\":[]}"));
            Check("Maven metadata XML is not a block",
                  !NetworkBlock.LooksLikeFilterPage("<?xml version=\"1.0\" encoding=\"UTF-8\"?><metadata/>"));
            Check("a jar is not a block",
                  !NetworkBlock.LooksLikeFilterPage(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }));
            Check("nothing at all is not a block", !NetworkBlock.LooksLikeFilterPage(""));
            Check("nor is null", !NetworkBlock.LooksLikeFilterPage((string?)null));
        }

        private static void Parsing()
        {
            Section("parsing JSON that turns out to be a filter's page");

            try
            {
                using var _ = NetworkBlock.ParseOrBlocked(ManifestUrl, Captured);
                Check("the captured page is reported as a block", false, "it parsed");
            }
            catch (NetworkBlockedException ex)
            {
                Check("the captured page is reported as a block", true);
                Expect("naming the host that was blocked", ex.Host, "launchermeta.mojang.com");
                Check("in words, not as a parser error",
                      ex.Message.Contains("blocked") && !ex.Message.Contains("invalid start"), ex.Message);
                Check("and saying what to do about it", ex.Message.Contains("Ask for"), ex.Message);
            }

            using (var good = NetworkBlock.ParseOrBlocked(ManifestUrl, "{\"versions\":[]}"))
                Check("real JSON still parses", good.RootElement.TryGetProperty("versions", out _));

            // Genuinely broken JSON is a different fault and must stay one: blaming
            // the network for a truncated file sends somebody to the wrong person.
            bool jsonError = false, blocked = false;
            try { using var _ = NetworkBlock.ParseOrBlocked(ManifestUrl, "{\"versions\":[ {\"id\""); }
            catch (NetworkBlockedException) { blocked = true; }
            catch (JsonException) { jsonError = true; }
            Check("broken JSON is still a JSON error, not a block", jsonError && !blocked);
        }

        // ---- the downloader, over a real socket -------------------------------
        private static async Task DownloadingAsync()
        {
            Section("the installers' download, when a filter answers instead");

            string dir = Path.Combine(Path.GetTempPath(), "mc-netblock-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            byte[] realJar = { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            string realSha = Convert.ToHexString(SHA1.HashData(realJar)).ToLowerInvariant();

            try
            {
                // 1. The filter's page where a hashed jar should be.
                string dest = Path.Combine(dir, "client.jar");
                bool threwBlocked = false;
                using (var srv = new OneShotServer(Encoding.UTF8.GetBytes(Captured)))
                {
                    try { await Downloader.GetVerifiedOrThrowAsync(srv.Url, dest, realSha, null, CancellationToken.None); }
                    catch (NetworkBlockedException) { threwBlocked = true; }
                }
                Check("a filter's page where a jar should be is a block", threwBlocked);
                Check("and is NOT left on disk under the jar's name", !File.Exists(dest));

                // 2. Same, with no hash to check against - FabricMeta downloads some
                // libraries that way, so the page has to be caught by its content.
                string dest2 = Path.Combine(dir, "unhashed.jar");
                bool threw2 = false;
                using (var srv = new OneShotServer(Encoding.UTF8.GetBytes(Captured)))
                {
                    try { await Downloader.GetVerifiedOrThrowAsync(srv.Url, dest2, null, null, CancellationToken.None); }
                    catch (NetworkBlockedException) { threw2 = true; }
                }
                Check("caught even with no hash to compare", threw2);
                Check("and not left on disk either", !File.Exists(dest2));

                // 3. The real file, which must still work.
                string dest3 = Path.Combine(dir, "good.jar");
                using (var srv = new OneShotServer(realJar))
                    await Downloader.GetVerifiedOrThrowAsync(srv.Url, dest3, realSha, null, CancellationToken.None);
                Check("the real file still downloads", File.Exists(dest3) &&
                      Downloader.HashMatches(dest3, realSha));

                // 4. Wrong bytes that are NOT a page - corruption, not a filter.
                string dest4 = Path.Combine(dir, "corrupt.jar");
                bool ioErr = false, blocked4 = false;
                using (var srv = new OneShotServer(new byte[] { 0x50, 0x4B, 9, 9, 9 }))
                {
                    try { await Downloader.GetVerifiedOrThrowAsync(srv.Url, dest4, realSha, null, CancellationToken.None); }
                    catch (NetworkBlockedException) { blocked4 = true; }
                    catch (IOException) { ioErr = true; }
                }
                Check("a corrupt file fails as a hash failure, not a block", ioErr && !blocked4);
                Check("and is deleted rather than kept", !File.Exists(dest4));

                // 5. The bool version other callers rely on keeps its contract, but no
                // longer leaves the failed file behind.
                string dest5 = Path.Combine(dir, "boolcall.jar");
                bool ok;
                using (var srv = new OneShotServer(Encoding.UTF8.GetBytes(Captured)))
                    ok = await Downloader.GetVerifiedAsync(srv.Url, dest5, realSha, null, CancellationToken.None);
                Check("the bool version still answers false", !ok);
                Check("and now deletes what failed", !File.Exists(dest5));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// Answers exactly one HTTP request with the given body and HTTP 200 - which
        /// is the whole trick a content filter plays.
        /// </summary>
        private sealed class OneShotServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Task _serving;
            public string Url { get; }

            public OneShotServer(byte[] body)
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                Url = $"http://127.0.0.1:{port}/file";

                _serving = Task.Run(async () =>
                {
                    try
                    {
                        using var client = await _listener.AcceptTcpClientAsync();
                        using var stream = client.GetStream();

                        // Read the request headers; the content does not matter.
                        var buf = new byte[4096];
                        var seen = new StringBuilder();
                        while (!seen.ToString().Contains("\r\n\r\n"))
                        {
                            int n = await stream.ReadAsync(buf);
                            if (n <= 0) break;
                            seen.Append(Encoding.ASCII.GetString(buf, 0, n));
                        }

                        string head = "HTTP/1.1 200 OK\r\n" +
                                      "Content-Type: text/html\r\n" +
                                      $"Content-Length: {body.Length}\r\n" +
                                      "Connection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                        await stream.WriteAsync(body);
                        await stream.FlushAsync();
                    }
                    catch { }
                });
            }

            public void Dispose()
            {
                try { _serving.Wait(2000); } catch { }
                _listener.Stop();
            }
        }
    }
}
