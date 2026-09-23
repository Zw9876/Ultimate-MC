using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MinecraftLauncher.Core
{
    /// <summary>
    /// The host-side skin server: a minimal Yggdrasil / Minecraft Services API that
    /// Skin Restorer and authlib-injector talk to, plus a UDP responder so client
    /// launchers can find it without being configured.
    /// </summary>
    /// <remarks>
    /// Deliberately built on <see cref="TcpListener"/> rather than
    /// <c>HttpListener</c>: HttpListener needs an administrator-registered URL ACL
    /// to accept connections from anything but localhost, which a portable
    /// no-install launcher cannot rely on. TcpListener binds unelevated.
    ///
    /// The wire protocol below is consumed by authlib-injector and Skin Restorer,
    /// neither of which we can change — response shapes must stay exactly as they
    /// are.
    /// </remarks>
    public sealed class SkinServer : IDisposable
    {
        public const int DefaultPort = 25567;
        private const string DiscoverProbe = "MCSKINSERVER_DISCOVER";

        private const int MaxHeaderBytes = 64 * 1024;
        private const int MaxBodyBytes   = 2 * 1024 * 1024;
        private const int RequestTimeoutMs = 15_000;

        /// <summary>Deadline for a launcher download — generous, since a slow wireless
        /// link can legitimately spend minutes on 126 MB.</summary>
        private const int BulkTimeoutMs = 10 * 60 * 1000;

        private static readonly Regex RxUpload =
            new(@"^/upload/([^/?#]+)$", RegexOptions.Compiled);
        private static readonly Regex RxLookupName =
            new(@"^(?:/minecraftservices)?/minecraft/profile/lookup/name/([^/?#]+)$", RegexOptions.Compiled);
        private static readonly Regex RxLegacyName =
            new(@"^(?:/api)?/users/profiles/minecraft/([^/?#]+)$", RegexOptions.Compiled);
        private static readonly Regex RxBulk =
            new(@"(?:^|/)profiles/minecraft$", RegexOptions.Compiled);
        private static readonly Regex RxSession =
            new(@"^(?:/sessionserver)?/session/minecraft/profile/([^/?#]+)$", RegexOptions.Compiled);
        private static readonly Regex RxSkinPng =
            new(@"^/skins/([^/?#]+)\.png$", RegexOptions.Compiled);
        private static readonly Regex RxLauncherFile =
            new(@"^/launcher/file/([^/?#]+)$", RegexOptions.Compiled);

        private readonly RSA _rsa;
        private readonly string _metadataJson;

        private TcpListener? _tcp;
        private Socket? _udp;
        private CancellationTokenSource? _cts;

        public int Port { get; }
        public int DiscoveryPort => Port + 1;
        public string HostIp { get; }
        public bool IsRunning { get; private set; }

        /// <summary>
        /// "Everyone close Minecraft", as polled by the game watchers on each client.
        /// Lives here because every client already talks to this server for skins.
        /// </summary>
        public ShutdownAnnouncer Shutdown { get; } = new();

        /// <summary>Raised from background threads — marshal before touching UI.</summary>
        public event Action<string>? Log;

        public SkinServer(int port = DefaultPort)
        {
            Port = port;
            HostIp = LanAddress();
            _rsa = SkinKey.LoadOrCreate();

            _metadataJson = JsonSerializer.Serialize(new
            {
                meta = new
                {
                    serverName = "Launcher Skins",
                    implementationName = "launcher-skin-server",
                    implementationVersion = "1.0"
                },
                skinDomains = new[] { HostIp, "127.0.0.1", "localhost" },
                signaturePublickey = SkinKey.PublicKeyPem(_rsa)
            });
        }

        /// <summary>First non-loopback IPv4 address, or loopback if the machine has none.</summary>
        public static string LanAddress()
        {
            try
            {
                var addresses = Dns.GetHostAddresses(Dns.GetHostName())
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToList();

                return addresses.FirstOrDefault(a => a != "127.0.0.1") ?? "127.0.0.1";
            }
            catch { return "127.0.0.1"; }
        }

        public void Start()
        {
            if (IsRunning) return;

            // A fresh session must never inherit the last one's shutdown, or the
            // first game to connect would be closed on arrival.
            Shutdown.Clear();

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _tcp = new TcpListener(IPAddress.Any, Port);
            _tcp.Start();
            Log?.Invoke($"Skin server listening on {HostIp}:{Port}");

            try
            {
                _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _udp.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                Log?.Invoke($"Discovery listening on UDP {DiscoveryPort}");
                _ = Task.Run(() => DiscoveryLoopAsync(ct), ct);
            }
            catch (Exception ex)
            {
                // Losing discovery is survivable — clients can still be pointed at
                // the server with SKIN_SERVER= in config.txt.
                _udp = null;
                Log?.Invoke($"Discovery unavailable ({ex.Message}); clients must set SKIN_SERVER= manually.");
            }

            _ = Task.Run(() => AcceptLoopAsync(ct), ct);
            IsRunning = true;
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;

            try { _cts?.Cancel(); } catch { }
            try { _tcp?.Stop(); } catch { }
            try { _udp?.Close(); } catch { }

            _tcp = null;
            _udp = null;
            _cts?.Dispose();
            _cts = null;

            Log?.Invoke("Skin server stopped.");
        }

        public void Dispose()
        {
            Stop();
            _rsa.Dispose();
        }

        // ── Accept loop ──────────────────────────────────────────────
        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _tcp!.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Log?.Invoke($"Accept failed: {ex.Message}");
                    continue;
                }

                // Each connection is served on its own task, so one slow client
                // cannot stall the others or the discovery responder.
                _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                // A client that connects and then goes quiet must not hold a task
                // forever; the PowerShell server had no timeout here.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeoutMs);

                try
                {
                    var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream, timeout.Token);
                    if (request is null) return;

                    // The short deadline suits the small API calls this server was
                    // built for, but a launcher download is ~126 MB and would be cut
                    // off part-way on anything slower than a fast wired LAN.
                    if (IsBulkTransfer(request)) timeout.CancelAfter(BulkTimeoutMs);

                    await RouteAsync(stream, request, timeout.Token);
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (Exception ex)
                {
                    Log?.Invoke($"Request failed: {ex.Message}");
                }
            }
        }

        /// <summary>Requests that legitimately take far longer than an API call.</summary>
        private static bool IsBulkTransfer(Request request) =>
            request.Method == "GET" &&
            request.Path.StartsWith("/launcher/file/", StringComparison.Ordinal);

        // ── Request parsing ──────────────────────────────────────────
        private sealed record Request(string Method, string Path, string Query, byte[] Body);

        private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            using var received = new MemoryStream();
            int headerEnd = -1;

            while (headerEnd < 0 && received.Length < MaxHeaderBytes)
            {
                int read = await stream.ReadAsync(buffer, ct);
                if (read <= 0) return null;

                received.Write(buffer, 0, read);
                headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
            }
            if (headerEnd < 0) return null;

            byte[] data = received.GetBuffer();
            string headerText = Encoding.ASCII.GetString(data, 0, headerEnd);

            string firstLine = headerText.Split("\r\n")[0];
            var parts = firstLine.Split(' ');
            if (parts.Length < 2) return null;

            string method = parts[0];
            string target = parts[1];

            int q = target.IndexOf('?');
            string path  = q >= 0 ? target[..q] : target;
            string query = q >= 0 ? target[(q + 1)..] : "";

            // Body, if the request declared one.
            int contentLength = 0;
            var match = Regex.Match(headerText, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
            if (match.Success) int.TryParse(match.Groups[1].Value, out contentLength);
            if (contentLength is < 0 or > MaxBodyBytes) contentLength = 0;

            while (received.Length - headerEnd < contentLength)
            {
                int read = await stream.ReadAsync(buffer, ct);
                if (read <= 0) break;
                received.Write(buffer, 0, read);
            }

            byte[] body = Array.Empty<byte>();
            int available = (int)received.Length - headerEnd;
            if (contentLength > 0 && available >= contentLength)
            {
                body = new byte[contentLength];
                Array.Copy(received.GetBuffer(), headerEnd, body, 0, contentLength);
            }

            return new Request(method, path, query, body);
        }

        private static int FindHeaderEnd(byte[] data, int length)
        {
            for (int i = 3; i < length; i++)
                if (data[i - 3] == 13 && data[i - 2] == 10 && data[i - 1] == 13 && data[i] == 10)
                    return i + 1;
            return -1;
        }

        // ── Routing ──────────────────────────────────────────────────
        private async Task RouteAsync(NetworkStream stream, Request request, CancellationToken ct)
        {
            string path = request.Path;
            bool isGet  = request.Method == "GET";
            bool isPost = request.Method == "POST";

            Match m;

            if (isGet && path == "/")
            {
                await SendJsonAsync(stream, 200, _metadataJson, ct);
            }
            else if (isPost && (m = RxUpload.Match(path)).Success)
            {
                await HandleUploadAsync(stream, m.Groups[1].Value, request, ct);
            }
            else if (isGet && (m = RxLookupName.Match(path)).Success)
            {
                var profile = FindByName(m.Groups[1].Value);
                if (profile is null)
                    await SendJsonAsync(stream, 404,
                        """{"path":"/minecraft/profile/lookup/name","error":"NOT_FOUND"}""", ct);
                else
                    await SendJsonAsync(stream, 200, NameIdJson(profile), ct);
            }
            else if (isGet && (m = RxLegacyName.Match(path)).Success)
            {
                var profile = FindByName(m.Groups[1].Value);
                if (profile is null)
                    await SendJsonAsync(stream, 404, """{"error":"Not Found"}""", ct);
                else
                    await SendJsonAsync(stream, 200, NameIdJson(profile), ct);
            }
            else if (isPost && RxBulk.IsMatch(path))
            {
                await HandleBulkLookupAsync(stream, request, ct);
            }
            else if (isGet && (m = RxSession.Match(path)).Success)
            {
                await HandleSessionProfileAsync(stream, m.Groups[1].Value, ct);
            }
            else if (isGet && (m = RxSkinPng.Match(path)).Success)
            {
                await HandleSkinPngAsync(stream, m.Groups[1].Value, ct);
            }
            else if (isGet && path == SessionShutdown.Endpoint)
            {
                // Polled every second by every client, so deliberately not logged —
                // it would bury everything else in the skin server's status line.
                await SendJsonAsync(stream, 200, Shutdown.ToJson(), ct);
            }
            else if (isGet && path == "/launcher/manifest")
            {
                await HandleLauncherManifestAsync(stream, ct);
            }
            else if (isGet && (m = RxLauncherFile.Match(path)).Success)
            {
                await HandleLauncherFileAsync(stream, m.Groups[1].Value, request, ct);
            }
            else
            {
                Log?.Invoke($"Unmatched: {request.Method} {path}");
                await SendAsync(stream, 400, "text/plain", Array.Empty<byte>(), ct);
            }
        }

        private async Task HandleUploadAsync(
            NetworkStream stream, string rawUsername, Request request, CancellationToken ct)
        {
            string username = Uri.UnescapeDataString(rawUsername);

            if (!SkinStore.IsValidUsername(username))
            {
                await SendJsonAsync(stream, 400, """{"error":"invalid username"}""", ct);
                return;
            }
            if (request.Body.Length == 0)
            {
                await SendJsonAsync(stream, 400, """{"error":"empty body"}""", ct);
                return;
            }
            // The PowerShell server wrote whatever bytes arrived; a truncated or
            // wrong-type upload then rendered as a broken skin for everyone.
            if (!SkinStore.LooksLikePng(request.Body))
            {
                await SendJsonAsync(stream, 400, """{"error":"not a png"}""", ct);
                return;
            }
            if (request.Body.Length > SkinStore.MaxSkinBytes)
            {
                await SendJsonAsync(stream, 413, """{"error":"too large"}""", ct);
                return;
            }

            string model = QueryValue(request.Query, "model") == "alex" ? "alex" : "steve";
            SkinStore.Save(username, request.Body, model);

            Log?.Invoke($"Skin uploaded: {username} ({model}, {request.Body.Length} bytes)");
            await SendJsonAsync(stream, 200, """{"ok":true}""", ct);
        }

        private async Task HandleBulkLookupAsync(NetworkStream stream, Request request, CancellationToken ct)
        {
            var results = new List<object>();
            try
            {
                var names = JsonSerializer.Deserialize<List<string>>(
                    Encoding.UTF8.GetString(request.Body)) ?? new List<string>();

                foreach (var name in names)
                {
                    var profile = FindByName(name);
                    if (profile is not null)
                        results.Add(new { id = Uuid.Strip(profile.Uuid), name = profile.Username });
                }
            }
            catch
            {
                // Malformed body → empty array, matching the reference behavior of
                // silently omitting anything that did not resolve.
            }

            await SendJsonAsync(stream, 200, JsonSerializer.Serialize(results), ct);
        }

        private async Task HandleSessionProfileAsync(NetworkStream stream, string rawUuid, CancellationToken ct)
        {
            var profile = FindByUuid(rawUuid);
            if (profile is null)
            {
                await SendJsonAsync(stream, 404, """{"error":"Not Found"}""", ct);
                return;
            }

            string textureJson = TextureJson(profile);
            string value = Convert.ToBase64String(Encoding.UTF8.GetBytes(textureJson));

            // Signed over the base64 text, with SHA-1 + PKCS#1 — what
            // authlib-injector expects from a self-hosted Yggdrasil provider.
            string signature = Convert.ToBase64String(_rsa.SignData(
                Encoding.UTF8.GetBytes(value),
                HashAlgorithmName.SHA1,
                RSASignaturePadding.Pkcs1));

            string json = JsonSerializer.Serialize(new
            {
                id = Uuid.Strip(profile.Uuid),
                name = profile.Username,
                properties = new[]
                {
                    new { name = "textures", value, signature }
                }
            });

            await SendJsonAsync(stream, 200, json, ct);
        }

        private async Task HandleSkinPngAsync(NetworkStream stream, string rawUsername, CancellationToken ct)
        {
            string username = Uri.UnescapeDataString(rawUsername);

            if (!SkinStore.IsValidUsername(username))
            {
                await SendAsync(stream, 404, "text/plain", Array.Empty<byte>(), ct);
                return;
            }

            string path = SkinStore.SkinPath(username);
            if (!File.Exists(path))
            {
                await SendAsync(stream, 404, "text/plain", Array.Empty<byte>(), ct);
                return;
            }

            await SendAsync(stream, 200, "image/png", await File.ReadAllBytesAsync(path, ct), ct);
        }

        // ── Profile lookup ───────────────────────────────────────────
        private sealed record Profile(string Username, string Model, string Uuid);

        /// <summary>
        /// Read live from the skin store on every request rather than from a map
        /// built at startup, so skins added while the server runs resolve
        /// immediately. At LAN scale the directory read is free.
        /// </summary>
        private static List<Profile> Snapshot() =>
            SkinStore.List()
                .Select(s => new Profile(s.Username, s.Model, Uuid.OfflinePlayer(s.Username)))
                .ToList();

        private static Profile? FindByName(string username)
        {
            string name = Uri.UnescapeDataString(username);
            return Snapshot().FirstOrDefault(p =>
                string.Equals(p.Username, name, StringComparison.OrdinalIgnoreCase));
        }

        private static Profile? FindByUuid(string rawUuid)
        {
            string stripped = Uuid.Strip(rawUuid).ToLowerInvariant();
            return Snapshot().FirstOrDefault(p =>
                Uuid.Strip(p.Uuid).Equals(stripped, StringComparison.OrdinalIgnoreCase));
        }

        private static string NameIdJson(Profile profile) =>
            JsonSerializer.Serialize(new { id = Uuid.Strip(profile.Uuid), name = profile.Username });

        private string TextureJson(Profile profile)
        {
            string url = $"http://{HostIp}:{Port}/skins/{profile.Username}.png";

            // Alex carries metadata.model = slim; Steve omits the key entirely
            // rather than sending "default".
            object skin = profile.Model == "alex"
                ? new { url, metadata = new { model = "slim" } }
                : new { url };

            return JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                profileId = Uuid.Strip(profile.Uuid),
                profileName = profile.Username,
                signatureRequired = true,
                textures = new { SKIN = skin }
            });
        }

        // ── Launcher self-update ─────────────────────────────────────
        /// <summary>
        /// Advertises the launcher build this host is running, so clients on the LAN
        /// can update themselves from it instead of the files being carried round by
        /// hand. Only the self-contained build is offered: the framework-dependent one
        /// is a stub that needs .NET installed and its sibling DLLs, so serving it
        /// would break whoever took it.
        /// </summary>
        private async Task HandleLauncherManifestAsync(NetworkStream stream, CancellationToken ct)
        {
            if (!LauncherPackage.CanServeUpdates)
            {
                await SendJsonAsync(stream, 404,
                    """{"error":"this host is not running a distributable build"}""", ct);
                return;
            }

            try
            {
                string json = LauncherPackage.ToJson(LauncherPackage.LocalManifest());
                Log?.Invoke($"Served launcher manifest ({LauncherPackage.CurrentVersion})");
                await SendJsonAsync(stream, 200, json, ct);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Launcher manifest failed: {ex.Message}");
                await SendJsonAsync(stream, 500, """{"error":"manifest unavailable"}""", ct);
            }
        }

        /// <summary>
        /// Serves one file of the launcher package. The name is validated against the
        /// package's own naming rules rather than being treated as a path, so there is
        /// nothing here to point outside the launcher folder.
        /// </summary>
        private async Task HandleLauncherFileAsync(
            NetworkStream stream, string rawName, Request request, CancellationToken ct)
        {
            if (!LauncherPackage.CanServeUpdates)
            {
                await SendJsonAsync(stream, 404,
                    """{"error":"this host is not running a distributable build"}""", ct);
                return;
            }

            string name = Uri.UnescapeDataString(rawName);
            string? path = LauncherPackage.PathOf(name);

            if (path is null || !File.Exists(path))
            {
                Log?.Invoke($"Refused launcher file request: {name}");
                await SendJsonAsync(stream, 404, """{"error":"not part of the launcher"}""", ct);
                return;
            }

            // Compressed only when asked for, and the client only asks when the manifest
            // said we could — an older host would have ignored the query and sent raw
            // bytes to a client unpacking them as gzip.
            if (QueryValue(request.Query, "gzip") == "1")
            {
                try
                {
                    string packed = LauncherPackage.GzipCopy(path);
                    long raw = new FileInfo(path).Length;
                    long sent = new FileInfo(packed).Length;
                    Log?.Invoke($"Sending {name} compressed ({sent / 1048576} of {raw / 1048576} MB)");
                    await SendFileAsync(stream, packed, ct, "gzip");
                    return;
                }
                catch (Exception ex)
                {
                    // Falling back to the raw file is always correct, just slower.
                    Log?.Invoke($"Could not compress {name} ({ex.Message}); sending it as is");
                }
            }

            Log?.Invoke($"Sending {name} to an updating client");
            await SendFileAsync(stream, path, ct);
        }

        // ── Responses ────────────────────────────────────────────────
        private static Task SendJsonAsync(NetworkStream stream, int status, string json, CancellationToken ct) =>
            SendAsync(stream, status, "application/json", Encoding.UTF8.GetBytes(json), ct);

        private static async Task SendAsync(
            NetworkStream stream, int status, string contentType, byte[] body, CancellationToken ct)
        {
            string reason = status switch
            {
                200 => "OK",
                400 => "Bad Request",
                404 => "Not Found",
                413 => "Payload Too Large",
                500 => "Internal Server Error",
                _   => "Error"
            };

            string header =
                $"HTTP/1.1 {status} {reason}\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            if (body.Length > 0) await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
        }

        /// <summary>
        /// Streams a file rather than buffering it. The launcher exe is ~126 MB, so
        /// reading it into a byte[] per request would be a needless spike on a machine
        /// that is also running a Minecraft server.
        /// </summary>
        private static async Task SendFileAsync(
            NetworkStream stream, string path, CancellationToken ct, string? contentEncoding = null)
        {
            await using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);

            string header =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: application/octet-stream\r\n" +
                (contentEncoding is null ? "" : $"Content-Encoding: {contentEncoding}\r\n") +
                $"Content-Length: {file.Length}\r\n" +
                "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await file.CopyToAsync(stream, 1 << 16, ct);
            await stream.FlushAsync(ct);
        }

        private static string? QueryValue(string query, string key)
        {
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                if (pair[..eq].Equals(key, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
            return null;
        }

        // ── UDP discovery ────────────────────────────────────────────
        private async Task DiscoveryLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[256];
            byte[] reply = Encoding.ASCII.GetBytes($"MCSKINSERVER:{Port}");

            while (!ct.IsCancellationRequested && _udp is not null)
            {
                try
                {
                    var sender = new IPEndPoint(IPAddress.Any, 0);
                    var result = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, sender, ct);

                    string message = Encoding.ASCII.GetString(buffer, 0, result.ReceivedBytes);
                    if (message.Trim() == DiscoverProbe)
                    {
                        await _udp.SendToAsync(reply, SocketFlags.None, result.RemoteEndPoint, ct);
                        Log?.Invoke($"Discovery reply sent to {result.RemoteEndPoint}");
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Log?.Invoke($"Discovery error: {ex.Message}");
                }
            }
        }
    }
}
