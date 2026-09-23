# Minecraft Portable Launcher — Project Handoff

This document is a complete briefing for continuing the project. It explains what
exists, how it works, what's done, and what's left, and it is the **single source
of truth** for that context — keep it updated as work lands.

`CLAUDE.md` in the repo root is a short pointer to this file (plus a few
Claude-specific working rules), so Claude Code auto-loads it and is directed
here. Do not copy this document's content into `CLAUDE.md`.

---

## 1. What this project is

A **portable, offline-friendly Minecraft launcher** for Windows. It runs from any
folder on any drive with no installation. It can launch the Minecraft client,
host a dedicated server, download versions, manage mods, and — its standout
feature — share custom skins across a LAN with zero per-player configuration.

There are currently **two implementations**:

1. **PowerShell** (`launcher.ps1`) — the original, fully working, feature-complete
   version. Compiled to `.exe` with ps2exe. Reference implementation only.
2. **C#/.NET 10 WPF** (`MinecraftLauncher/` project) — the current version.
   All tabs are ported and working; it has since gained features the PowerShell
   version never had (NeoForge, version download links).

The port is complete. The PowerShell version is kept only as a behavioural
reference, because the PowerShell + ps2exe combo caused many hard runtime bugs
around processes, threads and job objects that simply do not exist in C#.

---

## 2. Files to give Claude Code

Put these in the project working directory:

| File | Purpose |
|------|---------|
| `MinecraftLauncher-Standalone.ps1` | The PowerShell version — **behavioural reference only**. Consult it when C# behaviour is unclear; do not port from it wholesale. |
| `MinecraftLauncher/` (whole folder) | The C# project — the actual launcher (see layout below). |
| `README.md` | User-facing documentation (already on GitHub). |
| This handoff doc | Context/briefing. |

The PowerShell file records how the original behaved. When something is unclear,
read how it did it — then check section 11 before copying, as several of its
behaviours were deliberately not reproduced.

---

## 3. C# project layout

```
MinecraftLauncher/
├── MinecraftLauncher.csproj      Project file. Targets net10.0-windows, WPF enabled.
├── app.manifest                  DPI awareness manifest.
├── Core/                         ── Engine (no UI dependency; pure logic) ──
│   ├── Paths.cs                  Portable path resolution (everything relative to the exe).
│   ├── AppConfig.cs              Reads/writes config.txt (NICK=, max_MEM=, etc.).
│   ├── ComputerId.cs             Stable per-machine UUID (MachineGuid → RFC 4122 v3).
│   ├── Uuid.cs                   Name-based UUIDs, incl. the offline-player scheme.
│   ├── MavenVersion.cs           Semver-aware comparison of library coordinates.
│   ├── VersionScanner.cs         Lists installed versions; detects loaders (Vanilla/Fabric/Forge).
│   ├── ClientLauncher.cs         Builds classpath + args and launches the client. (Largest/most complex.)
│   ├── ServerLauncher.cs         Writes server.properties/eula, launches the server.
│   ├── ModManager.cs             Mod/plugin folders, listing, add/remove, enable via .disabled.
│   ├── Downloader.cs             Shared HttpClient + SHA-1 verified downloads.
│   ├── MojangManifest.cs         The one Mojang version manifest source.
│   ├── VersionInstaller.cs       Installs a full vanilla client (jar, libs, natives, assets).
│   ├── FabricMeta.cs             Fabric loader/installer metadata + client install.
│   ├── ServerJarInstaller.cs     Vanilla/Fabric/Forge/Paper/Purpur server jars.
│   ├── SkinStore.cs              skins/ folder + metadata.json; thread-safe.
│   ├── SkinKey.cs                Persistent RSA key (skins/skinserver_rsa.xml) + SPKI PEM.
│   ├── SkinServer.cs             The host-side Yggdrasil/Services API + UDP discovery.
│   ├── SkinRestorerConfig.cs     Writes the server's config/skinrestorer/config.json.
│   ├── SkinPreviewHtml.cs        Self-contained skinview3d page for the 3D preview.
│   ├── VersionLinks.cs           Reads version-links.txt for the Download Versions list.
│   ├── LauncherPackage.cs        The launcher as a file set: version, hashes, name rules.
│   ├── LauncherUpdate.cs         Client side of the LAN self-update: check, verify, swap.
│   ├── ServerProperties.cs       Merges server.properties instead of overwriting it.
│   ├── ServerSession.cs          A running server: piped console, commands, clean stop.
│   ├── JvmTuning.cs              GC flags and a sane default heap for the machine.
│   ├── MachinePower.cs           Whether Windows is letting the processor run at full speed.
│   ├── ServerHealth.cs           Reads "Can't keep up!" out of the server's own output.
│   ├── SessionShutdown.cs        "Close Minecraft for everyone": host announcement + client poll.
│   └── SkinDiscovery.cs          Client-side: UDP discovery of a skin server + skin upload.
└── UI/                           ── WPF presentation layer ──
    ├── App.xaml / App.xaml.cs    App entry point + dark theme resources/styles.
    ├── MainWindow.xaml           Sidebar + CLIENT/SERVER/MODS/SKINS/SETUP tab layouts.
    ├── MainWindow.xaml.cs        Wires the UI controls to the Core engine.
    └── GameWatcher.cs            Hidden per-game watcher + the countdown window (watcher mode).
```

The only assembly references outside the framework are the two WebView2 DLLs,
referenced straight out of `runtime\webview2\` rather than from NuGet. That keeps
the project building on a machine with no package feed (this one has none) and
guarantees the SDK matches the Fixed Version runtime bundled beside it.

**Those references are `Private=false` on purpose**: compile-time only, never
copied to the output or publish folder. All three WebView2 files
(`Microsoft.Web.WebView2.Core.dll`, `.Wpf.dll`, `WebView2Loader.dll`) exist as a
single copy in `runtime\webview2\`, matching the PowerShell launcher's layout,
and `Core\WebView2Resolver.cs` loads them from there at run time via a module
initializer — the direct equivalent of the PowerShell version's
`UnsafeLoadFrom` + `PATH` prepend at `launcher.ps1:53-71`.

Setting `Private=true` "to make it build" silently reintroduces duplicate DLLs at
the exe's root and breaks that layout. If the preview reports WebView2 missing,
the fix is to check `runtime\webview2\`, not to copy DLLs next to the exe.

**Key architectural decision:** `Core/` has **zero UI dependencies**. It's pure
logic. This means the UI framework (WPF) can be swapped (e.g. for Avalonia)
without touching the engine, and the engine can be unit-tested or driven from a
console harness. Preserve this separation when adding features.

---

## 4. How to build and run (Windows, .NET 10 SDK installed)

```powershell
cd MinecraftLauncher
dotnet build            # compile
dotnet run              # build + run
```

The built output lands in `bin\Debug\net10.0-windows\`, but do not run it from
there: `Paths.BaseDir` is the exe's own folder, so it looks for `runtime\` and
`servers\` beside the binary, finds nothing, and fails with "No bundled Java
runtime found under runtime/".

**The copy at the repo root is the published self-contained build**, not the debug
one, and deliberately so: only a self-contained build passes
`LauncherPackage.CanServeUpdates`, so hosting from this machine is what lets the
other machines pull updates from it. A framework-dependent build sitting there
answers `/launcher/manifest` with a 404 and every client reports "found a skin
server, but it cannot offer updates".

The consequence: **`dotnet build` alone does not refresh the root launcher.**
Publish, then copy the exe and its `*_cor3.dll` siblings over — and delete any
`MinecraftLauncher.dll` left beside them, because its presence alone is what marks
the build as framework-dependent.

**For a standalone distributable** (no .NET needed on target — this is what the
offline machines require):
```powershell
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true --source https://api.nuget.org/v3/index.json
```

The `--source` flag is required **on this machine**: it has no NuGet feed
configured, and a self-contained publish must pull the win-x64 runtime packs.
Without it the restore fails with `NU1100: Unable to resolve
'Microsoft.NETCore.App.Runtime.win-x64'`. A plain `dotnet build` is unaffected.
Producing this build needs internet, even though running it does not.

Output lands in `bin\Release\net10.0-windows\win-x64\publish\` and is **not a
single file** despite the flag — it is a 132 MB exe plus five native DLLs that WPF
cannot embed:

```
MinecraftLauncher.exe          D3DCompiler_47_cor3.dll   PenImc_cor3.dll
PresentationNative_cor3.dll    vcruntime140_cor3.dll     wpfgfx_cor3.dll
```

**Ship all of them** — the exe will not start without its DLL siblings. The
WebView2 DLLs are deliberately *not* here; they stay in `runtime\webview2\`
(see section 3).

`AppContext.BaseDirectory` resolves to the exe's own folder in this publish mode,
not to a temp extraction directory, so `Paths.BaseDir` and the portable layout
behave exactly as they do in a normal build. Verified, because everything —
including where WebView2 is loaded from — depends on it.

> Note: `RuntimeIdentifier`/`PublishSingleFile` are intentionally **kept out of the
> csproj** so a plain `dotnet build` doesn't try to download platform runtime packs.
> They belong only on the publish command.

---

## 5. What's DONE in C#

- **Client tab** — version + loader dropdowns, username, memory slider, PLAY.
  Launches the game. Verified working.
- **Server tab** — version, type, port, gamemode, difficulty, max players, memory,
  PVP. Writes config, downloads the server jar if missing, starts the server in a
  console window. Verified working.
- **Mods tab** — client/server target, version + server-type selectors, list with
  enabled state, add (skip-not-overwrite), enable/disable, remove, open folder.
  `Core/ModManager.cs`.
- **Setup tab** — Mojang manifest browsing with release/snapshot filter and search,
  full vanilla install (client jar, Windows libraries + natives, asset index, all
  assets — every file SHA-1 verified), optional Fabric loader install, live log,
  progress bar, cancel. `Core/VersionInstaller.cs`, `Core/FabricMeta.cs`,
  `Core/MojangManifest.cs`, `Core/Downloader.cs`.
- **Server install** — vanilla / Fabric / Forge / NeoForge / Paper / Purpur.
  `Core/ServerJarInstaller.cs`.
- **Forge and NeoForge** — build resolution, official-installer runs for both
  client and server, and the argument-file launch that modern builds require.
  `Core/ForgeMeta.cs`, `Core/ForgeInstaller.cs`, `Core/ServerStartPlan.cs`.
  Verified by booting real 26.1.2 servers of both to "Done … For help, type help".
- **Config persistence** — `config.txt`, same format as PowerShell version.
- **Portable paths** — all relative to the exe, works from any drive.
- **Machine UUID** — stable per machine, RFC 4122 valid.
- **Skins tab** — skin list (username/model/file), add with username prompt,
  toggle Steve/Alex, remove, open folder, skin-server status with manual
  start/stop, and the embedded 3D preview (WebView2 + skinview3d) with an
  open-in-browser fallback.
- **Skin server** — the full host side: all seven HTTP endpoints, UDP discovery,
  persistent RSA signing, live skin updates without a restart. Runs in-process on
  a background thread; starts automatically when you host a server (after writing
  Skin Restorer's config) and can also be toggled by hand from the Skins tab.
- **Skin client-side hooks** — discovery + upload + authlib-injector attachment
  wired into `ClientLauncher`.
- **Download Versions** — game versions are no longer bundled. The Client tab
  button lists entries from `version-links.txt` next to the exe and opens the
  chosen one in a browser; the user extracts it into `versions\`. `Core/VersionLinks.cs`.
  Only `http(s)` links are accepted — the file is user-edited and its contents are
  handed to the shell.
- **Launcher closes after PLAY**, matching the PowerShell version — except while
  hosting, because the skin server runs in-process and closing would drop skins
  for everyone. Closing the window while hosting asks first and defaults to No.

- **Self-update over the LAN** — a launcher discovers a host running a newer build
  and updates itself from it, over the skin server already running there. These
  machines have no internet, so the alternative was carrying a 126 MB exe round on
  a USB stick. Downloads are verified by SHA-256 before anything is replaced, only
  a strictly newer version is offered, and nothing is applied without the user
  agreeing to a prompt naming the source machine. `Core/LauncherPackage.cs`,
  `Core/LauncherUpdate.cs`.

- **Server console in the launcher** — the Server tab turns into a live console once
  a server starts: piped output, a command box and a Stop that saves the world
  first. The server used to run in a detached .bat window the launcher could not
  read, so it could neither show what happened nor shut it down. `Core/ServerSession.cs`.
- **Shut down for everyone** — a red sidebar button, shown only while this launcher
  is hosting, for when everyone leaves at once. It confirms first (default No), then
  announces the shutdown on the skin server. Every game started from a launcher has a
  hidden **watcher** (the same exe with `--watch-game <pid> --host <addr>`, started
  at PLAY because the launcher itself closes) that polls the host once a second, shows
  a 5-second countdown with a **Keep playing** button, then closes the game as if its
  X was clicked. The host counts down in chat too, for fullscreen players, waits for
  games to leave, stops the Minecraft server with `stop` so the world saves, and
  stops the skin server last. `Core/SessionShutdown.cs`, `UI/GameWatcher.cs`.
- **One server at a time**, enforced. Starting a second while one runs is refused —
  two servers on one machine just starve each other. A port check also catches a
  server left running by an earlier launcher session.

- **Server JVM tuning** — the launcher passed nothing but `-Xmx`/`-Xms`, leaving the
  JVM on default collector settings. On a modded server with a dozen players that is
  felt as periodic freezing: the heap fills and one long stop-the-world collection
  runs. `Core/JvmTuning.cs` adds the standard G1 flag set, scaled by heap size, on
  both the jar and argument-file launch paths.
- **Server-tab settings persist**, which was not cosmetic: the memory slider reset to
  2 GB on every launch, so servers were being started on 2 GB no matter what anyone
  set, silently. The default heap is now derived from the machine's RAM.
- **Pre-generation** — a PRE-GENERATE panel in the server console drives Chunky:
  radius, shape, an optional Nether pass at an eighth of the radius, a cost estimate
  before committing, a live progress bar fed by Chunky's own reports, pause / resume /
  stop, and a button that sets the world border to match. `Core/ChunkyPregen.cs`.
  This is the operational fix for the crowd problem in section 14, turned into
  something that can be done in a ninety-minute window without remembering that
  radius is a half-width while `worldborder set` takes a diameter.
  - Chunk counts are exact, not approximate: `(2 * ceil(radius/16) + 1)²`, which
    predicted 7,921 for radius 700 and got 7,921 from a real run.
  - It follows a run started by hand in the command box too, because it reads the
    console rather than tracking only what its own button sent.
  - `chunky trim` is deliberately absent — it deletes chunks outside the selection,
    and nothing here should be one mis-click from that.

- **Mod loader mismatch warning** — `versions/<version>/mods` is per-version, not
  per-loader, so Fabric and NeoForge on the same Minecraft version share it and each
  crashes on the other's jars. The Mods tab now reads inside every jar, shows what
  each is **Built for**, and warns when the chosen loader cannot run what is enabled.
  One button turns those off and turns the matching ones back on — nothing is
  deleted, it is the existing `.disabled` rename, so switching loaders is reversible.
  `Core/ModInspector.cs`.
  - The loader selector is now enabled for clients too. It previously read as
    "Vanilla" for a client, which runs no mods at all, so the check had nothing
    sensible to compare against.
  - Multi-loader jars are common and must not be flagged: detection is a flags set,
    and a jar carrying only `META-INF/mods.toml` is reported as Forge **or**
    NeoForge rather than guessed at, because NeoForge used that file up to 1.20.1.
  - A jar declaring nothing is left alone — plenty of legitimate library jars carry
    no descriptor, and disabling those would break working setups.
- **Mod downloading from Modrinth** — a "Get mods online…" button on the Mods tab
  opens a browser filtered to the version and loader already selected, and installs
  straight into that folder. `Core/ModrinthApi.cs`, `UI/ModBrowserWindow.xaml`.
  - **Why this is worth having on these machines:** they cannot reach Mojang but
    they *can* reach Modrinth — established by the user, who found the Modrinth
    launcher able to download mods but not Minecraft. Mods are therefore obtainable
    on the machines themselves rather than carried in on a USB stick. It is also the
    strongest clue so far that the block is on Mojang domains rather than on
    `java.exe`, since that launcher is a Rust app, not a Java one.
  - No account or API key; reads only. Every download is checked against the SHA-1
    Modrinth publishes, through the same verified path as every other download here.
  - **Required dependencies are followed** (breadth-first, de-duplicated), because a
    missing one is the usual way a hand-picked mod fails and the error names a
    package rather than a mod. Optional ones are deliberately ignored.
  - The target folder, version and loader are shown but not editable — a second
    place to choose them would be a way to install mods somewhere unexpected.
  - File names are flattened to a bare name: nothing downloaded picks its own path.
  - Already-present mods are skipped, **including ones that are turned off**, or the
    folder fills with disabled twins.
  - **Installing a newer build turns the old one off.** Matching on file name alone is
    not enough and was a real bug: `sodium-fabric-0.5.12…` and `sodium-fabric-0.5.13…`
    look like different files, so both ended up installed, and a loader handed one mod
    twice refuses to start. Identity comes from the **mod id inside the jar**
    (`ModInspector.ModIdOf`). The old build is renamed to `.disabled`, never deleted,
    so a downgrade stays one click away — and a deliberate downgrade replaces in the
    same way. The Mods tab also warns about duplicates arriving any other way.
  - **Laid out like Modrinth's own explorer**: a card per mod with its icon, author,
    description, download and follower counts, when it was last updated, and category
    pills; sort by relevance / downloads / follows / recently updated / newest;
    **Show more** paging; and a details pane with a **version picker**, the exact jar
    and size, and a link to the project page.
  - **Icons are WebP about 80% of the time.** Windows decodes that through WIC on the
    builds tested here, but it is not guaranteed on every Windows install, so a failed
    decode falls back to a letter tile on the project's own accent colour rather than
    an empty box. Icons are cached under `cache/modrinth-icons/`.
  - The icon is painted as the tile's **background**, not as a child `Image`: a child
    sits square on top of the rounded corners instead of being clipped by them.
- **Loader version checking** — `Core/LoaderVersions.cs`, `Core/VersionRange.cs`,
  and the requirement reader in `ModInspector`. Filtering mods by Minecraft version
  and loader family is **not enough**: a mod also declares the loader version it needs
  and Modrinth's API does not expose that at all. The jar does, so it is read there.
  - Fabric: `"fabricloader": ">=0.18.4"` in `fabric.mod.json` (arriving `>=`
    escaped). Forge/NeoForge: `versionRange="[46,)"` against `modId="forge"` /
    `"neoforge"` in the TOML.
  - Installed versions come off disk, so it works offline: the client profile name
    (`fabric-loader-0.19.3-26.1.2.json`, `1.20.1-forge-47.4.10.json`) or the server's
    `libraries/net/{fabricmc,minecraftforge,neoforged}/…` folder.
  - **Everything answers with a nullable bool and null means "cannot tell".** A
    warning against a mod that is actually fine teaches people to ignore warnings, so
    anything unparsed passes in silence. A string that is not version-shaped is never
    compared as though it were one.
  - Checked in two places: the Mods tab warning, and right after a Modrinth install —
    the only moment the jar exists and the mistake is still cheap.
- **Fabric loader updating** — `Core/FabricLoaderUpdate.cs`, `UI/LoaderUpdateWindow`.
  Updates the loader of an installed version **without reinstalling Minecraft**, which
  is the download these machines cannot make and which would take the mods and worlds
  with it. Two routes, because the machines differ:
  - **Online**: lists what Fabric offers and installs it (wrapping the existing
    `FabricMeta.InstallAsync`).
  - **Offline**: a **pack** — a zip of the profile JSON and the libraries it names,
    made by a machine that can download and carried to ones that cannot. It declares
    its Minecraft and loader version in a manifest, is refused if built for a
    different Minecraft version, and **cannot write outside the version folder**.
  - Forge and NeoForge are deliberately not offered: their loader is baked into the
    profile their installer generates and cannot be swapped underneath an install.
- **Live player list** — a PLAYERS panel in the server console showing who is on and
  how long they have been on, assembled from the server's own join and leave lines
  and corrected by the reply to `list`. `Core/PlayerRoster.cs`. The count also sits
  in the console header. The `list` the launcher sends is not echoed, since it is the
  launcher asking rather than the user typing.

The whole PowerShell feature set is ported, plus NeoForge, version links,
self-update, the server console, JVM tuning, pre-generation, the mod loader check
and the player list.

The legacy **OfflineSkins** skin path from the PowerShell version (per-version
`config/offlineskins/<user>.png`, `Show-SkinPicker`, the Client tab's "Change Skin"
button) is **deliberately not ported**. It depended on an unbundled third-party mod
and had no connection to the LAN skin-sharing system.

---

## 6. The skin server, as built

Everything below is implemented in `Core/SkinServer.cs`; this section is the
contract it has to keep, because authlib-injector and Skin Restorer are external
consumers that cannot be changed. **Any change here needs the protocol tests
re-run** (see section 12).

Endpoints (the PowerShell original is the `$serverScript` here-string inside
`Start-SkinHttpServer` in `MinecraftLauncher-Standalone.ps1`):
- `GET /` → authlib-injector metadata (skinDomains + signaturePublickey, PEM).
- `GET /minecraft/profile/lookup/name/<name>` → `{id,name}` (the modern Services API — the one Skin Restorer actually calls).
- `GET /users/profiles/minecraft/<name>` → legacy lookup.
- `POST /profiles/minecraft` → bulk lookup.
- `GET /session/minecraft/profile/<uuid>?unsigned=false` → signed texture profile.
- `GET /skins/<name>.png` → serve the PNG.
- `POST /upload/<name>?model=<steve|alex>` → receive a skin upload (binary body).

Two more endpoints serve the launcher self-update. They are ours alone — no external
mod reads them — so unlike the seven above they can be changed freely:

- `GET /launcher/manifest` → this host's launcher version plus every package file
  with size and SHA-256. 404s unless the host is running the self-contained build.
- `GET /launcher/shutdown` → `{"shutdown":false}`, or `{"shutdown":true,"id","countdown"}`
  while an "everyone close Minecraft" announcement is live (60 s). Polled every
  second by each game watcher, so it is deliberately not logged.
- `GET /launcher/file/<name>` → one package file. `<name>` is validated against
  `LauncherPackage.IsPackageFileName`, never treated as a path.
- UDP `25568` → reply `MCSKINSERVER:25567` to `MCSKINSERVER_DISCOVER` broadcasts.

Key details to preserve:
- Persistent RSA 2048 key saved to `skins/skinserver_rsa.xml` (stable public key).
- Texture JSON is base64'd and RSA-signed; offline clients don't verify against
  Mojang so any valid signature is accepted, but authlib-injector must trust our key.
- Offline UUID = MD5v3 of `OfflinePlayer:<username>`.
- The launcher writes Skin Restorer's `config.json` before server start (yggdrasil
  provider, `useProviderSignature: true`, `autoFetch.providers: ["launcher-skins"]`),
  force-deleting any existing one first. See `Write-SkinRestorer-Config`.
- authlib-injector jar goes in `runtime/authlib-injector/authlib-injector.jar`.

Note the skin server must reuse `Uuid.OfflinePlayer()` (already in `Core/Uuid.cs`)
for its lookup table. Offline servers derive a joining player's UUID from the
username, so the skin server has to agree with that or skins resolve to the wrong
player. This is *not* the same UUID as `ComputerId.Get()`, which is what the client
launch passes and which is deliberately machine-based.

### How the C# version differs from the PowerShell one, and why

- **`TcpListener`, not `HttpListener`.** This contradicts the earlier plan and the
  reason matters: `HttpListener` can only bind `http://localhost:<port>/` without
  an administrator-registered URL ACL. A `+`/`*` prefix — the one that accepts LAN
  connections, which is the entire point — throws `HttpListenerException: Access
  is denied` for a normal user. A portable no-install launcher cannot require
  elevation, so the transport stays raw TCP with a small hand-written HTTP layer.
  `TcpListener` and the UDP socket both bind fine unelevated.
- **Concurrent, with timeouts.** Each connection is served on its own task instead
  of the reference's one-at-a-time `Socket.Select` loop, and every request has a
  15s budget. The old loop had no read timeout at all, so a client that connected
  and said nothing blocked every other client, including discovery.
- **Shared state is locked.** Serving concurrently means `metadata.json` writes
  and skin reads can now overlap; `SkinStore` takes a lock around all of it. The
  PowerShell version was accidentally race-free because it was single-threaded.
- **Profiles are read live per request** rather than from a map built at startup,
  so a skin added in the Skins tab (or uploaded by a player) resolves immediately.
  The reference required a restart for UI-added skins.
- **Uploads are validated** — PNG magic bytes, a 1 MB cap, and usernames
  restricted to `[A-Za-z0-9_]{1,16}`. The last one also stops a request path from
  escaping the skins folder. `/upload` remains unauthenticated by design: that is
  what makes it zero-config on a LAN, and it means anyone on the network can
  overwrite any name's skin. Deliberate tradeoff, not an oversight.
- **`RSA.ExportSubjectPublicKeyInfoPem()`** replaces the hand-rolled ASN.1/DER
  encoder. The key file itself stays in .NET's RSA XML format so keys written by
  the PowerShell launcher keep working — verified by test.
- **No WMI process spawn.** The `Win32_Process.Create` call, the `ShowWindow=0`
  startup info and the orphaned-process sweep existed only to escape ps2exe's job
  object. The server is now a background thread in-process, which does mean it
  stops when the launcher closes.

---

## 7. How the skin system works (conceptual — important context)

Offline Minecraft servers can't show custom skins normally because clients only
trust skins signed by Mojang and served from Mojang domains. This project works
around that with two mods/tools plus a local server:

1. **Skin Restorer** (Fabric server mod) — on player join, fetches skin data from
   a configured provider. We point it at our local skin server via a custom
   Yggdrasil provider.
2. **Our skin server** (local HTTP, port 25567) — answers the Yggdrasil/Services
   API endpoints, serves the PNG, signs texture data with a local RSA key.
3. **authlib-injector** (client Java agent) — patches the client to trust our
   skin server's domain and signature key, so the client will actually download
   and render the texture.
4. **Auto-discovery (UDP 25568)** — clients find the host automatically; no IP
   config. **Auto-upload** — a player's skin is pushed to the host at launch.

**Exactly one machine on the network should run the skin server.** Discovery is a
broadcast, so two servers means clients pick between them arbitrarily and players
end up seeing different skins. The launcher enforces this: both places that can
start one — the Skins-tab Start button and the automatic start behind START SERVER
— probe the LAN first and adopt an existing remote server rather than starting a
second. Discovery also prefers a *remote* responder over a local one, so a client
with a leftover local server no longer talks to itself. `SKIN_SERVER=<ip>:25567`
in `config.txt` overrides discovery entirely and skips the probe.

Required on the **server** (in its `mods/` folder, versions matching the MC version):
- `fabric-api-<version>.jar`
- `skinrestorer-<version>-fabric.jar`

Required **runtime deps** bundled with the launcher:
- `runtime/authlib-injector/authlib-injector.jar`
- `runtime/webview2/` (DLLs) + `runtime/webview2runtime/` (Fixed Version runtime)
- `runtime/skinview3d/skinview3d.bundle.js`
- Java runtimes under `runtime/<major>/`

Ports: `25565/TCP` Minecraft, `25567/TCP` skin server, `25568/UDP` discovery.

Skin Restorer ships a **separate jar per loader** — Fabric, NeoForge and Forge all
exist. Match the jar to the server: a `-fabric` jar will not load on NeoForge. Only
Bukkit-family servers (Paper/Purpur) are unsupported. `fabric-api` is needed only
on Fabric.

---

## 8. Deployment folder layout (what the end-user runs)

```
<AnyFolder>/
├── MinecraftLauncher.exe        (or the publish output folder's contents)
├── config.txt                   auto-generated
├── computer_uuid.dat            auto-generated
├── runtime/
│   ├── <java>/                  Java runtimes (8/17/21/25)
│   ├── webview2/                WebView2 DLLs
│   ├── webview2runtime/         Edge WebView2 Fixed Version runtime
│   ├── skinview3d/skinview3d.bundle.js
│   └── authlib-injector/authlib-injector.jar
├── versions/<version>/          client game files (versions/, libraries/, assets/, natives/)
├── servers/<loader>-<ver>/      server instances (server.jar, mods/, config/, world/)
└── skins/                       skin PNGs + metadata.json + skinserver_rsa.xml
```

Minecraft's own files (under `versions/` and `server.jar`) are **not**
redistributable and are not in the GitHub repo — they come from the Setup tab /
server download, or are copied in for offline machines.

### Building the distributable zip

The offline machines get a single zip that is extracted and run — no installer,
no .NET, no Java, no internet. Build it in two steps:

1. Publish self-contained (section 4).
2. Zip the publish output **plus** `runtime/`, `servers/`, `skins/`,
   `background.png` and `version-links.txt`, under one top-level folder.

**`versions/` is deliberately excluded.** Game files are large and not
redistributable, so the launcher instead shows a list of download links —
the *Download Versions* button on the Client tab, read from `version-links.txt`.
Users extract a download into `versions\` themselves.

Ship `DEPLOY-README.md` as `README.txt` inside it.

**Leave out** `computer_uuid.dat` — it is the machine's identity on LAN servers,
so shipping one copy makes every machine the same player. `config.txt` is also
omitted so each machine prompts for its own username. Both regenerate on first
run. Source, `.pdb` files, the PowerShell launcher and dev docs do not belong in
it either.

Without `versions/`, expect roughly 2.2 GB raw — almost entirely the bundled
Java and WebView2 runtimes. Compress at *fastest*; the payload is mostly jars and
DLLs where higher settings cost minutes and save little.

---

## 9. GitHub repo

- URL: https://github.com/Zw9876/Minecraft-Portable-Launcher
- Tracked files: `MinecraftLauncher-Standalone.ps1`, `README.md`, `.gitignore`, `LICENSE`
- The `.gitignore` excludes runtime/versions/servers/skins/config and all user state.
- Releases: distribute the runnable bundle (exe + runtime) as a zip asset; the
  repo itself stays code-only.

---

## 10. Next tasks

The port is complete and shipping. Rollout is no longer a problem: the machines
run 1.2.x, they detect updates over the LAN, and only the host needs a new build
by hand. Current build at the repo root and in the rollout zip: **1.2.265.81**
(`C:\Users\Zach\Minecraft-Launcher-Update.zip`).

**Cleared 2026-09-22.** The user ran both on the real machines and reported them
working as intended: **"shut down for everyone" reaches watchers on other
computers**, and **self-update finds the host over the LAN by itself** — the UDP
hop the local test had to pin with `SKIN_SERVER=`. Those were the two biggest open
questions and they are now field-proven, not just bench-proven. The standing
limitation is unchanged: **only games started from a 1.2.261+ launcher have a
watcher**, so anyone already playing when a machine updates must restart their game
once before the host can close it for them.

What is genuinely outstanding:

1. **One two-machine test still pending:** discovery preferring a *remote* skin
   server over a local one. It cannot be exercised on this box — a host here reads
   as local — and the session that proved the other two did not isolate it. Skins
   working for everyone is consistent with it but does not establish it: that also
   holds if nobody had a stray local server to be misled by. To test it properly,
   start a skin server on a client machine, leave it running, then have that
   machine join a session the host is serving and check it resolves to the *host*.
2. **Pre-generate the worlds.** Still the biggest remaining cause of the host
   struggling, and still operational rather than code — but no longer a matter of
   typing commands correctly under time pressure: the server console now has a
   PRE-GENERATE panel that drives Chunky, estimates the cost first and shows live
   progress (section 5). **It needs running on the real worlds.** The plan is in
   section 14 and has not changed: border first, generate past it afterwards, widen
   only into finished terrain.
3. **~1.5 s freeze on PLAY** when no skin server is on the LAN — the UDP discovery
   timeout runs on the UI thread. Harmless but noticeable offline; make the launch
   path async.
4. **Consider `spark`** if the server still falls behind once the worlds are
   pre-generated. Nothing general is left to tune at that point; the next step is
   profiling to find the specific mod or contraption. Not installed on any server.

Because `Core/` is UI-independent, prefer putting logic there and keeping the
WPF code-behind thin.

---
## 11. Notable gotchas learned the hard way (so they aren't rediscovered)

- **XML comments cannot contain `--`** — broke the csproj twice.
- **The app.manifest `<assembly>` element** must not have stray attributes — a typo
  caused a "side-by-side configuration is incorrect" launch failure.
- **Framework-dependent exe needs its sibling files** — copy the whole output
  folder, or publish single-file.
- **Keep `RuntimeIdentifier` out of the csproj** — it forces runtime-pack downloads
  on every build. Use it only on `dotnet publish`.
- **`HttpListener` needs admin for LAN prefixes.** `http://+:<port>/` and
  `http://*:<port>/` both throw `Access is denied` unelevated; only
  `http://localhost:<port>/` works. This is why the skin server uses `TcpListener`.
  Measured on this machine, not assumed.
- **This machine has no NuGet feed configured** (`dotnet nuget list source` is
  empty), so `dotnet add package` fails. WebView2 is referenced from
  `runtime\webview2\` instead. Anything new that needs a package will have to
  either be vendored the same way or have a feed added deliberately.
- **Assembly references default to copying themselves next to the exe.** A
  `<Reference>` with a `HintPath` copies to the output unless `Private=false`,
  which is how the WebView2 DLLs ended up duplicated at the root instead of
  living only in `runtime\webview2\`. Vendored DLLs that belong in `runtime\`
  need `Private=false` *and* a resolver — see `Core\WebView2Resolver.cs`.
- **skinview3d's model must be a constructor option.** Setting
  `viewer.playerObject.skin.modelType` after construction is silently overwritten
  when the skin texture finishes loading asynchronously, so Alex skins previewed
  as Steve. Pass `model: 'slim'|'default'` to the `SkinViewer` constructor.
- **Forge 1.17+ and every NeoForge build install no runnable server jar.** They
  produce a libraries tree plus a generated `win_args.txt`, started with
  `java @user_jvm_args.txt @…/win_args.txt nogui`. `Core/ServerStartPlan.cs`
  detects which layout is present. Modern Forge *also* drops a `-shim.jar`; it is
  a compatibility stub for hosting panels, so the argument file is checked first
  and the shim is never renamed to `server.jar`.
- **Java version must match the game version, not just "something modern".**
  NeoForge 26.1.2 is compiled for Java 25 and dies on Java 21 with
  `UnsupportedClassVersionError: class file version 69.0`, which says nothing
  obvious about Java versions. `Paths.JavaMajorForMinecraft` maps game version →
  JDK; server installs have no manifest to read, unlike the client.
- **Forge and NeoForge name their client profiles differently.** NeoForge writes
  `neoforge-<build>.json`; Forge writes `<mcversion>-forge-<build>.json` — game
  version *first*, so it starts with a digit. Matching on a `forge*` prefix finds
  NeoForge but misses Forge, which installs fine and then shows as Vanilla.
  `VersionScanner.IsForgeProfile` matches on *contains* "forge" and excludes
  "neoforge"; test both orderings when touching it.
- **`--installClient` defaults to `%APPDATA%\.minecraft`.** The installer's target
  path is optional and the default differs per mode: `--installServer` uses the
  working directory, `--installClient` silently uses the real Minecraft folder.
  Always pass the path explicitly, or a client install reports success having
  written nothing into the portable tree.
- **The installer nests profiles; this launcher keeps them flat.** It writes
  `versions/<id>/<id>.json`, while `VersionScanner` and `ClientLauncher` expect
  `versions/<id>.json`. `ForgeInstaller.FlattenProfiles` moves them up, discards a
  duplicate of the vanilla profile, and deletes leftover folders (Forge also drops
  a second ~27 MB copy of the client jar there).
- **Forge/NeoForge split startup across a module path and the classpath.** A jar
  named in the profile's `-p` argument must not also be on `-cp`, or
  BootstrapLauncher aborts with "Module … was already on the JVMs module path but
  class-path contains it" and the game never opens. `ClientLauncher` parses `-p`
  out of the loader's own JVM args and removes those jars from the classpath.
- **Loader profiles usually omit `javaVersion`.** Falling back to "newest
  installed" picked Java 25 for Minecraft 1.20.1, which wants 17. Derive it from
  the game version (`Paths.JavaMajorForMinecraft`) and let the profile override.
- **Server-tab dropdowns must not come from `versions/`.** Now that game files are
  not bundled, a machine can have servers and no clients. `HostableVersions()` is
  the union of installed clients and versions parsed back out of
  `servers/<loader>-<version>`. Test `neoforge` before `forge` in that parsing, or
  a NeoForge folder yields a version of `e-1.21.1`.
- **`runtime\webview2\userdata\` must never be packaged.** It is WebView2's browser
  profile, created when the 3D preview first runs — 71 MB of cache, local storage
  and logs from whoever built the zip. It regenerates on first use.
- **`readPixels` cannot verify a WebGL render.** The drawing buffer is cleared
  after compositing unless `preserveDrawingBuffer` is set, so it always reads
  back zeros. Use `CoreWebView2.CapturePreviewAsync` to check what actually drew.
- **A type check cannot guard a type reference in the same method.** The JIT
  resolves every type a method mentions *before* running its first statement, so
  an `if (!WebView2Resolver.Available) return;` sitting beside a WebView2
  reference throws `FileNotFoundException` instead of returning. All WebView2
  references are quarantined in `UI\SkinPreviewHost.cs` with `NoInlining`, and
  `MainWindow` must never mention a WebView2 type. This crashed the launcher on
  opening the Skins tab whenever `runtime\webview2\` was absent.
- **`EnsureCoreWebView2Async` never completes while the control is collapsed.**
  It only finishes once the control is realized in the visual tree, so awaiting
  it behind `Visibility.Collapsed` hangs forever and the preview sits on
  "Starting 3D preview…". Reveal the container first, and do not collapse it
  again while initialization is in flight — a second `UpdatePreview` call doing
  exactly that was a real bug.
- **WebView2 creates `<exe name>.WebView2` beside the executable** if you pass a
  null user data folder. It is now pointed at `runtime\webview2\userdata`, both
  to keep the deployment root clean and because the folder is exclusively
  locked — a second launcher instance otherwise fails to start the preview.
- (PowerShell-era, for context) skin URLs must be a domain the client trusts, hence
  authlib-injector; Skin Restorer calls the modern
  `/minecraft/profile/lookup/name/<name>` endpoint; and offline UUID is MD5v3 of
  `OfflinePlayer:<name>`.

### Reference-implementation bugs that were fixed rather than ported

Do not "restore fidelity" on these — the PowerShell behavior was wrong.

- **Library version comparison** was a plain string compare, ranking `1.2.9` above
  `1.2.10`. `Core/MavenVersion.cs` compares numeric segments numerically.
- **The machine UUID** was a raw MD5 of the computer name: not a valid RFC 4122
  UUID, and identical on two machines sharing a default name. `ComputerId` now
  seeds from Windows' per-install `MachineGuid` and sets the version/variant bits.
  An existing `computer_uuid.dat` is still honored, so upgrades keep their identity.
- **Asset objects were never re-verified**, so a corrupt asset stayed corrupt
  forever. They are SHA-1 checked now (the filename *is* the hash, so it's free).
- **The Fabric server loader was pinned** to `0.19.2` and the installer to `1.0.1`;
  both are stale. Loader and installer versions are queried live.
- **Two different Mojang manifest endpoints** were in use. `MojangManifest` is now
  the single source.
- **Forge**: modern Forge installs a `run.bat` instead of a runnable jar, which the
  old code reported as an opaque "server JAR not found". It now says so explicitly
  and lists what the installer actually produced.

- **The 3D preview showed Alex skins with classic arms** — see the skinview3d note
  above. Only affected the preview; the server always sent the right model.

- **A client used to bind to its own stray skin server.** `SkinDiscovery` sent a
  UDP broadcast and took the *first* reply. A skin server on the same machine
  answers over loopback long before a real host answers across the LAN, so any
  client that had one running — most easily by hosting a Minecraft server earlier
  in the same launcher session — silently talked to itself. It only knew its own
  skin, so every other player rendered with a default skin and **nothing reported
  an error**, because discovery had "succeeded". Discovery now collects replies
  and prefers a non-local responder, falling back to a local one only so a real
  host still resolves to itself. Fixed 2026-08-18.

- **Two skin servers on one LAN is worse than one.** Clients pick whichever
  answers first, so players split across servers and see different skins. Both
  entry points — the Skins-tab Start button and the automatic start behind
  START SERVER — now probe first and adopt an existing remote server instead of
  starting a second. When they adopt one, `SkinRestorerConfig.Write` has to be
  given that machine's IP; the loopback default is only right when we host.

- **"The skin server starts by itself."** It does, by design: START SERVER on the
  Server tab brings it up because Skin Restorer needs a live address in its config
  before the Minecraft server boots. The Skins tab used to show a bare "running"
  with no explanation, which reads as the launcher acting on its own. It now says
  *why* it is running.

- **`<Version>` in the csproj is load-bearing now.** Clients compare it against the
  host they discover, so a build shipped without bumping it will never be offered
  as an update. Equal versions are deliberately treated as "no update" even when
  the files differ, otherwise two machines would swap binaries back and forth.

- **Never serve the framework-dependent build.** It is a small exe that needs .NET
  installed and its sibling managed DLLs, so a machine that took it would end up
  with a launcher that cannot start. `LauncherPackage.CanServeUpdates` detects the
  distributable build by the *absence* of `MinecraftLauncher.dll` beside the exe.

- **The skin server had a 15-second request deadline.** Fine for the small API
  calls it was built for, but a launcher download is ~126 MB and was being cut off
  part-way on anything slower than a fast wired LAN. `IsBulkTransfer` now extends
  it for `/launcher/file/`.

- **A running exe cannot overwrite itself on Windows**, so the update is finished by
  a small .bat that waits for the launcher to exit, copies the staged files in and
  restarts it. It uses `ping` rather than `timeout` as its sleep, because
  `timeout.exe` fails when stdin is redirected — which it is, since the script runs
  with no console.

- **Do not test the swap script from Git Bash.** External commands inherit a
  POSIX-style PATH and `tasklist`, `find` and `ping` all fail silently, so the wait
  loop falls straight through while cmd builtins like `copy` still work. It looks
  exactly like a broken wait. Run that harness from PowerShell.

- **The settings that scale worst with a crowd are per-player, not per-world.**
  `entity-broadcast-range-percentage` is paid once per player, so at twenty players
  it is twenty times the work; it is now owned and set to 75.
  `network-compression-threshold` is off (-1): compression exists to save internet
  bandwidth, and this launcher only ever serves a LAN, where CPU is the scarce
  thing and bandwidth is not.

- **A capped processor state is a silent throttle.** A maximum processor state of
  99% switches turbo off entirely, and managed or vendor power plans on work
  machines do set it. The host loses exactly the single-thread speed a Minecraft
  server depends on, and nothing looks wrong. `MachinePower` reads it with
  `powercfg` and the Server tab says so — but only when something really is
  holding the processor back.

- **Live world generation is usually the real cause of a struggling host.** Worlds
  here were never pre-generated (26 MB, 29 region files for the main one), so twenty
  players spreading out means terrain being generated during ticks. Chunky is already
  installed on every server; `chunky radius`/`chunky start` from the console moves
  that work off the session entirely. No launcher change can substitute for it.

- **Clients are asked, not told.** The shutdown is polled by each client rather than
  pushed by the host, because Windows Firewall blocks unsolicited inbound traffic by
  default and opening it needs an administrator on every client. Clients asking out
  to the host is the same traffic skins already rely on. Polls back off to every
  30 s against an older host that lacks the endpoint, so its log is not flooded.

- **Setting `StartupUri = null` at run time throws on .NET 10 WPF.** That was how
  watcher mode first skipped the main window, and every watcher died silently with
  `0xE0434352` before the error handler existed — no log, no dialog. The crash only
  showed up in the Windows Application event log (.NET Runtime, event 1026).
  `StartupUri` is gone from App.xaml; `App.OnStartup` opens `MainWindow` itself.

- **A watcher is a running copy of the exe, so it locks it against updates.**
  `LauncherUpdate.Apply` sets the `LocalMinecraftPortableLauncher.WatcherExit`
  event first, and every watcher exits on it; the swap script already retries long
  enough to cover the gap.

- **Game logs land in the repo-root `logs` folder**, not `versions<v>ogs`,
  because the game runs with the launcher folder as its working directory. Look
  there for `Stopping!` when checking a game closed normally.

- **Never force-kill a server to shut it down.** With `sync-chunk-writes=false`,
  more chunk data sits in memory waiting to be written, so a hard kill loses more
  than it used to and can leave a corrupt region file. Everything that stops a
  server goes through `ServerSession.StopAsync`, which sends `stop` and only kills
  after 45 seconds of being ignored.

- **`-Xmn128M` on the client was actively harmful.** Carried over from the
  PowerShell launcher, it pinned the young generation at a fixed size, which
  overrides G1's adaptive sizing so the collector can no longer meet its pause
  target — and 128 MB is far too small for a modded client anyway, forcing constant
  young collections. Removing it is half of what made the client smoother.

- **Client and server want different collector settings.** The server can absorb a
  200 ms pause; on a client that is a visible hitch, so the client aims at 130 ms
  and pushes remembered-set work off the pause entirely
  (`G1RSetUpdatingPauseTimePercent=0`). `JvmTuning` keeps two separate sets.

- **Two flags from the published "optimised Minecraft flags" lists will stop the
  game starting.** `G1ConcRSHotCardLimit` and `G1ConcRefinementServiceIntervalMillis`
  were removed in Java 21 — a warning there, but *unrecognised* in Java 25, where
  the JVM refuses to start. The client runs on whichever Java the version needs, so
  never add a flag without running it against 17, 21 and 25 first.

- **`view-distance` and `simulation-distance` are launcher-owned, not seeded.** As
  first-run defaults they only ever reached brand-new server folders, so every
  server people were actually playing on kept its original values and none of the
  tuning arrived. Owned keys apply on every start, which is the only way an
  existing server picks a change up.

- **`sync-chunk-writes` is forced off on every start**, not offered as a setting.
  With it on, the server thread waits for each chunk write to physically commit —
  an 8-12 ms seek mid-tick on any mechanical drive, however fast it is at
  throughput, because the cost is latency and not bandwidth. The trade is a
  possible corrupt chunk after a hard power cut, which is acceptable here: worlds
  are started fresh, nothing is backed up, and Stop saves before exiting.

- **`server.properties` used to be rewritten from scratch on every start**, throwing
  away everything the user or the mods had set. The damage was real: a Fabric server
  writes 60+ keys of its own, including `initial-enabled-packs` listing the mods'
  data packs. Worst of all it dropped `level-name`, which sent the server back to the
  default "world" and generated a fresh one — the existing world looked deleted even
  though the folder was still there. `ServerProperties` now updates only the keys the
  launcher owns and preserves comments, ordering and unknown keys. `online-mode` is
  the one thing forced back, because nothing can join without it.

- **The update check has to repeat.** The host's skin server starts with their
  Minecraft server, which is long after everyone else opened their launcher, so a
  single check at startup would miss it for everybody. A three-minute timer re-checks
  until it finds something, then stops. `LauncherPackage.LocalManifest()` caches its
  hashes so this does not re-read 126 MB every tick.

- **A stopped server left the console with no way out.** The Server tab shows the
  console while a server runs, and re-picking an already-selected nav item does not
  raise `Checked` — so the settings were unreachable. The Stop button becomes
  "BACK TO SETTINGS" once the server has gone.

- **Do not run the launcher from `bin\Debug` to test servers or skins.**
  `Paths.BaseDir` is the exe folder, so it looks for `runtime\` and `servers\`
  next to the binary and finds nothing — "No bundled Java runtime found under
  runtime/". Run the copy at the repo root, which sits beside the real folders.

- **"The update feature does not work" was every machine being on the same version.**
  Nothing newer existed, so nothing was offered — correct behaviour that looks
  identical to a broken one. Two things made it undiagnosable: the launcher showed
  its version nowhere, and the check was silent about *why* it found nothing. 1.1.1
  adds a version label and a "Check for updates" link that distinguishes no host /
  host too old / same version / host older / update available.

- **Only the host needs a hand-copied build.** Everyone else pulls it over the LAN.
  For a 1.1.0 client the order matters once — it checks only at startup, so the
  host must be running first. From 1.1.1 the repeating check removes that.

- **UI Automation cannot see a `Border` or a WPF `MessageBox`.** A Border has no
  automation peer, and the message box did not enumerate under RootElement by
  process id either. Three test "failures" in a row were this, not the product —
  target the `Button` inside a card, drive message boxes with `AppActivate` +
  `SendKeys`, and screenshot before believing a negative result.

- **The version is generated now, not typed.** The server-console build shipped
  still calling itself 1.1.0, so no machine had anything numbered higher and the
  update feature stayed silent while working exactly as designed. The csproj now
  derives build/revision from days-since-2026-01-01 and minutes-since-midnight UTC,
  which always increases and cannot be forgotten. Bump `VersionPrefix` by hand only
  for a release people should notice.

- **Update transfers are gzipped, but only when both ends are new.** The host
  advertises `"compression":"gzip"` in the manifest and the client appends `?gzip=1`
  only if it saw that — an older host would ignore the query and send raw bytes to a
  client unpacking them as gzip, failing the checksum for no reason. Measured: 56.9 MB
  on the wire instead of 131.7 MB, 43%. The compressed copy is cached in temp, keyed
  by size and timestamp, so it is made once per build and not once per client.

- **The swap moves rather than copies.** Staging lives inside the install folder, so
  it is a rename on one volume instead of writing 126 MB a second time.

- **The server memory slider reset to 2 GB on every launch.** Nothing on the Server
  tab was ever restored, and the slider is built with `Value="2"`, so a modded
  server with fifteen players was being started on 2 GB no matter what anyone set.
  It looked like a hardware problem. Server settings now persist in `config.txt`
  under `SV_*`, and the default heap comes from `JvmTuning.RecommendedHeapGb()`.

- **`-Xmx` alone is not enough.** With no collector flags the JVM does one long
  stop-the-world pause instead of many short ones, which players experience as the
  server freezing for a moment. `JvmTuning.GcFlags` adds the standard G1 set, scaled
  at a 12 GB threshold, and it has to go on **both** launch paths — the jar args and
  the Forge/NeoForge `user_jvm_args.txt`. `-XX:+UnlockExperimentalVMOptions` must
  come before the experimental G1 options or the JVM refuses to start.

- **The memory sliders were fixed at 16 GB, which was wrong in both directions.**
  Unreachable on a 16 GB machine — `AlwaysPreTouch` would have tried to commit all
  of system memory — and far too low on a workstation with 64 GB. Both sliders now
  size themselves to the machine: RAM minus a 4 GB reserve, capped at 32 GB. The
  suggested value is separate and stays low on purpose (12 GB ceiling).

- **`-XX:+AlwaysPreTouch` commits the whole heap at startup.** That is wanted on a
  host — no page-fault stalls mid-session — but it makes startup slower (measured:
  15 s to 32 s for 7 GB) and the RAM is held for good. On a machine where someone
  also plays, leave room: 16 GB total does not mean 16 GB for the server.

- **Chunky's reported rate counts chunks it skipped.** A run over already-generated
  ground reported **1950 chunks/sec**, because "processed" includes chunks that were
  simply already there. Remembering that as this machine's speed made the panel
  estimate a radius of 3000 at about a minute, against a real hour and a quarter.
  `ChunkyPregen.IsCredibleRate` ignores anything outside 2-150 cps for that reason.
  The same trap makes a small test run look instant — if a pre-generation test seems
  to finish impossibly fast, it generated nothing and proved nothing.
- **"Nobody is on" is a change, and forgetting that leaves a panel looking broken.**
  The roster's first `list` reply on an empty server compared the new empty set
  against the old empty set, reported "no change", and so refreshed nothing — the
  header stayed blank when it should have said the server was empty. Going from *not
  knowing* to *knowing it is empty* is a state change even though the contents match.
- **An owned WPF window is a child of its OWNER in the UI Automation tree, not of the
  desktop.** `new ModBrowserWindow { Owner = this }.ShowDialog()` produces a window
  that `RootElement.FindFirst(Children, …)` never finds — by process id or by name,
  open or not — so a test looking there concludes it failed to open. `AppActivate`
  finds it, which is the tell that the window is fine and the search is wrong. Look
  for it under the main window instead. (A plain `MessageBox` is different again: it
  is in neither place, and has to be driven with `AppActivate` + `SendKeys`.)
- **A `Border` still has no automation peer, and a collapsed card is invisible twice
  over.** Testing `Visibility` by looking for the Border itself passes whether the
  card is shown or not, because it is never in the tree — three assertions passed
  vacuously before one of them finally failed and exposed the rest. Probe a **child**
  with a peer instead: absent when collapsed, present when shown.
- **A WPF `CheckBox` driven by UI Automation's `TogglePattern` did not fire `Click`.**
  The box visibly cleared and the panel kept describing the old state. `Checked` and
  `Unchecked` fire however the value changed, and are the right handlers for
  "recompute when this setting changes".

Deliberately kept: `--uuid` is machine-based, not username-based, so renaming
yourself preserves your local player data. This is *not* impersonation protection —
offline servers compute a joining player's UUID from the username and ignore what
the client sends. Preventing impersonation would require server-side enforcement.

---

## 12. Running with no internet

The target machines are offline, so this is a hard requirement, and it was
measured rather than assumed.

**Startup touches nothing networked** (~115 ms total, all filesystem):
config load, machine UUID, version scan, skin list, RSA key load.

| Feature | Offline? |
|---------|----------|
| Launching the client | Yes — needs the version pre-copied into `versions/` |
| Hosting a server | Yes — **only if `server.jar` is already in `servers/<loader>-<ver>/`**, otherwise it tries to download and errors |
| Mods tab | Yes, pure filesystem |
| Skins tab + 3D preview | Yes — the WebView2 Fixed Version runtime is bundled |
| Skin sharing across the LAN | Yes — LAN traffic only, no internet involved |
| Setup tab | **No**, by nature: it downloads from Mojang and Fabric |

Internet-dependent calls fail fast and are caught, so nothing hangs: an
unresolvable host errors in ~120 ms, and a blackholed route (the realistic
"on a LAN with no gateway" case) in ~2 s, bounded by the OS TCP connect timeout
rather than `HttpClient.Timeout`. The Setup tab logs the failure and re-enables
its button.

Known offline rough edge: with no skin server on the network, every **PLAY**
blocks for ~1.5 s on the UDP discovery timeout, on the UI thread. Harmless but
noticeable on a standalone machine.

---

## 13. Testing

### The test project

`MinecraftLauncher.Tests/` — **308 checks, about 4.5 seconds.**

```powershell
tools\run-tests.ps1                 # everything
tools\run-tests.ps1 -Offline        # skip the suites needing the internet
tools\run-tests.ps1 loader mods     # only matching suites
tools\run-tests.ps1 -List           # names only
```

Suites: `versions`, `mods`, `chunky`, `players`, `loader`, `packs`, `modrinth`
(the last needs internet). Exit code is 0 only when everything passed.

Things worth knowing before changing it:

- **No test-framework package.** The runner is one file, `Harness.cs`. This machine
  has no NuGet feed configured, so a dependency is one more thing that can stop the
  build working offline. Cost on disk is about 1.3 MB.
- **It compiles `Core/` directly and leaves `UseWPF` off**, which is what enforces
  the rule that `Core/` has no UI dependency — put a WPF type there and this project
  stops compiling. `net10.0-windows` because `Core` reads the registry.
- **The real installs are read, never written.** Anything that changes files works on
  a temp copy. `Paths.BaseDirOverride` points Core at the real folder, and
  `LocalInstall.RedirectBase` scopes a sandbox for the few checks that need to write.
- **Suites skip rather than fail** when the real installs or the network are absent,
  so this still runs somewhere else.
- Tests are written against **captured real output** — Chunky's console lines, the
  archived server logs, the actual mod jars — not against invented formats.

### What has been covered

Earlier work was verified with throwaway console harnesses, rebuilt from scratch in
every session until the project above existed. The table below is the full record;
the suites now cover the `Core/` half of it permanently, and the UI-driven rows
remain one-off runs.

| Area | Checks |
|------|--------|
| `MavenVersion`, `Uuid` | ordering edge cases; offline UUID matches the known value for "Notch" |
| Setup / downloads | full 1.7.10 install, layout, re-run skips, a corrupted asset repairs itself |
| Fabric + mods | live metadata, loader install, every mod operation |
| Skin server | all seven endpoints' exact shapes, signature verification, discovery, 60 concurrent mixed requests, restart key stability, PowerShell key-file compatibility |
| Skin preview | WebView2 boots the bundled Fixed Version runtime, skinview3d loads, both models apply, composited capture proves it rendered |
| DLL layout | with no WebView2 files beside the exe, the resolver loads them from `runtime\webview2\` and the preview still renders — confirmed in both a normal build and a single-file self-contained publish |
| End-to-end | host serves → client discovers by broadcast → uploads → host serves the signed profile back |
| Forge / NeoForge | NeoForge base-version mapping for both Minecraft version schemes, live build resolution, installer URLs reachable, start-planner picks the right launch style, and **real 26.1.2 servers of both installed and booted to "Done"** |
| Forge / NeoForge clients | profile detection for both naming schemes, module-path/classpath split, Java-by-version; **both launched to the Minecraft main menu** |
| Version links | separator forms, bare URLs, comments, whitespace, garbage files, and rejection of `file:`/`javascript:`/UNC/local-exe entries |
| Server dropdowns | versions parsed back out of `servers/<loader>-<version>`, incl. neoforge-vs-forge prefix and versions containing a dash |
| Close on launch | game spawns and launcher exits; stays open and warns while hosting |
| Distributable zip | extracted and run from the extraction — all tabs, server list, 3D preview |
| Skin discovery | against a **real running skin server**: own server is flagged local, `FindRemote()` ignores it so no second server starts, the host still resolves to itself, config override skips the probe, and a local-only probe settles in ~435 ms instead of the full 1.5 s window |
| Skin Restorer config | loopback by default, remote host when adopting another machine's server, with `baseUrl`/`servicesUrl`/`sessionUrl` all repointed and no loopback left behind |
| Power plan | powercfg parsing for the plan name and the AC index, including 0x63 (99%, turbo off) and out-of-range values this machine cannot produce; read for real here (Ultimate Performance, 100%, no advice shown) |
| Server health | the real "Can't keep up! ... Running 2051ms or 41 ticks behind" line parsed, ordinary lines ignored, worst kept rather than latest, plain-language summary, reset per session; the crowd settings written for a new server and applied to an existing one on its next start |
| Close for everyone | core against a **real skin server**: announce, unique ids, clamping, clear-on-restart, expiry, hostile replies, Unsupported back-off, the update exit signal. Stand-in games: watchers windowless, countdown shown to both, one closed the normal way after 5 s while **Keep playing** left the other running, watchers exit with their game, update signal removes a watcher and leaves the game. **Real Minecraft 1.20.1**: countdown appeared over it and the game logged `Stopping!` — its own normal shutdown |
| Shut down servers | against a **real Fabric server with the skin server up**: button hidden when idle and shown when hosting; answering No left both running; answering Yes saved the world ("All dimensions are saved"), stopped Java and freed port 25567 with no force-kill; the button hid again and the launcher stayed open |
| Client JVM tuning | the client set accepted by Java 17, 21 and 25 together; no `-Xmn`; unlock flag ordered first; client pause target below the server's; client heap recommendation lower than the server's. Proven by **launching the real game**: all 20 flags on the live process, window title "Minecraft 1.20.1", responding, no `-Xmn` |
| Existing-server upgrade | a real server deliberately reset to the old values (simulation 10, sync true, max 10) was corrected to 6 / false / 25 by the launcher on a live start — the case that matters, since the servers people play on were created long before the tuning existed |
| Forced overrides | `sync-chunk-writes` set back to true by hand is overridden again on the next start, while a hand-tuned `simulation-distance` survives — the difference between an override and a default. Confirmed on the **real Fabric server**, whose file flipped from true to false on a live start |
| server.properties | first-run creation, then hand edits surviving a restart — level-name, level-seed, a tuned view-distance, custom motd, unknown keys and comments all preserved while the launcher's own settings still apply; keys updated in place not duplicated; hardcore as a flag; online-mode forced back on sabotage. Confirmed on the **real Fabric server**, whose 60+ own keys survived two launches |
| Server console | driven through UI Automation against a **real Fabric 26.1.2 server**: console replaced the settings panel, live output captured, a typed command answered ("There are 0 of a max of 10"), STOP saved and shut down gracefully with no java left, and the way back to the settings works |
| Port guard | a free port reads free, a bound one reads in use, and free again once released |
| Machine-aware sizing | slider maximum and suggested heap computed across 8-256 GB machines; CPU name, core count and RAM read on the real machine and shown on the Server tab; both sliders re-sized at load and existing values re-clamped |
| JVM tuning | machine RAM detected and the recommended heap clamped sanely; flag proportions switch at the 12 GB threshold; the unlock flag precedes the experimental ones; **all 18 flags accepted without warnings by the bundled Java 17, 21 and 25** at two heap sizes; both launch paths carry them — verified in `user_jvm_args.txt` for the real NeoForge and Forge servers, and on a **live Fabric server** whose running command line showed all 20 arguments with a 7 GB heap |
| Server settings persistence | version, loader, memory, port, max players, gamemode, difficulty and PvP all written to `config.txt` and restored on the next launch |
| Self-update, compressed | gzip advertised in the manifest and carried over the wire; both files arrive fully decompressed and hash-clean; the compressed copy is smaller and cached rather than remade. On the real host: 56.9 MB sent instead of 131.7 MB (43%), 2.65 s to compress once then 0.05 s from cache |
| Self-update, full flow | **two real installs on one machine**: a 1.1.0 install noticed a 1.1.1 host by itself, showed the card, prompted for confirmation, downloaded 126 MB, verified it, closed itself, swapped the exe byte-for-byte, cleaned up staging and relaunched reporting 1.1.1.0 |
| Launcher self-update | package name rules incl. traversal and non-package files; manifest and download served over a **real skin server**; every refusal path on the download endpoint; checksum mismatch and illegal-name rejection leaving nothing staged; the four version-comparison rules; and the swap script **executed for real** against throwaway folders — waits for the process, replaces the exe and dll, leaves unrelated files alone, cleans up |
| Self-update, real build | the **published 126 MB launcher** advertised all 6 package files with correct sizes and hashes, and served its own exe back in full with an intact MZ header |
| Skins tab status | driven through UI Automation on the real launcher: Start reports `running on <ip>:25567 — started from this tab.` and the button flips to Stop |
| Loader versions | detection run against **all five real installs here** — Fabric client 0.19.3 and server 0.19.3, Forge client and server 47.4.10 (with the Minecraft version correctly stripped off the server's folder name), NeoForge server 21.1.248 — plus vanilla and a version that is not installed, both of which report nothing rather than guessing. Requirements read out of **real jars**: fabric-api's `>=0.18.4` with its JSON escapes decoded, Chunky's `[46,)` on Forge and `[21.0-beta,)` on NeoForge. A sweep of **every installed mod against its own installed loader**: 100 of 121 answerable, none needing a newer loader, none falsely flagged. Range syntax covers Maven bounds and exclusivity, Fabric `>= < ~ ^` and conjunctions, and refuses unions, nonsense and non-version words rather than answering confidently — the last two were found by this suite and fixed |
| Fabric loader packs | a pack **exported from the real 26.1.2 install** (3.7 MB, 8 files) then imported into a throwaway copy that had the game but no loader: profile and all 7 libraries arrived **byte-for-byte identical** to the originals, the detector then found 0.19.3, and nothing but `versions/`, `libraries/` and the manifest was swept in. Refusals: a pack for the wrong Minecraft version, a stranger's zip, a file that is not a zip, exporting a loader that is not installed, and a zip containing `../../../escaped.txt` — which wrote nothing. Through the real UI: the window states the installed loader, INSTALL stays disabled until versions are fetched, and a live query to Fabric returned **253 loader versions for 26.1.2, newest 0.19.5** |
| Upgrading an installed mod | against the **live API with two real Sodium builds**: installing the newer over the older reports `Replaced` and names what it turned off, one copy is left enabled, the old build survives as `.disabled`, and both builds are confirmed to share the mod id `sodium` — which is why file names cannot be trusted for this. Re-installing the same build is still `AlreadyThere`; a deliberate **downgrade** replaces the same way round; an unrelated mod (fabric-api) displaces nothing and lives alongside; and a jar copied in by hand is caught by the duplicate warning |
| Modrinth explorer view | the fields the cards show, read live: icon url, categories with loader names stripped, "updated N days ago", environment, follower count, accent colour. All five sort options accepted by the API, downloads genuinely descending, paging returning a **non-overlapping** second page, and `updated` ordering differing from `downloads`. Icons: a real fetch, a second fetch served from cache byte-identical, and null (not a crash) for a missing or broken url. Then **looked at**: screenshots of the card list and the details pane, which is how the two visual faults were found — icons not clipped to their rounded corners, and categories rendering as plain text instead of pills. Driven through the UI end to end: 24 cards drawn, sort and Show more present, details pane replacing the prompt on click, version picker naming the exact jar and size, decline leaving the folder untouched, then a real install and the folder restored afterwards |
| Modrinth | against the **live API**: loader mapping, filtered search (Fabric/NeoForge, real and impossible game versions), version listing, best-version choice, and dependency resolution — `sodium-extra` correctly pulled in Fabric API and Sodium and left the optional ones out. A **real download**: size and SHA-1 both matching what Modrinth published, the jar then passing the loader check as Fabric and failing it for NeoForge, appearing in the mods list, a repeat download being a no-op, and a **turned-off** copy still counting as present. Path traversal in a file name is flattened. Then through the real UI: the browser opened filtered to the right folder/version/loader, searched, offered a confirmation, **declined with nothing downloaded**, then installed Ksyxis for real — Mods tab went 35 → 36 with no false loader warning, and the folder was restored to exactly 35 afterwards |
| Mod loader detection | run against **every real mod folder on this machine** — 35 Fabric client jars, 28 Fabric server, 20 Forge client, 17 Forge server, 21 NeoForge — with **no false alarms** in any of them, including the multi-loader jars that a name-based check would have flagged. Junk survives (a non-zip `.jar`, a missing file, an empty path). The turn-off/turn-on round trip was proven on a **temp copy of real jars**, not on the live folders: wrong ones off, right ones back on, switched to the other loader and back, file count unchanged throughout. Then through the real UI: no warning on a correct folder, the warning and its wording on a mismatched one, the confirmation offered and **declined**, and all five real mod folders verified untouched afterwards |
| Player list | parsed from **real archived server logs** (`servers/fabric-26.1.2/logs`) — actual join and leave lines from real sessions, the real `list` reply including its trailing space when nobody is on, and `lost connection` / `logged in with entity id` lines that must not move the roster. Chat is the trap and is covered: `<Zach> haha I joined the game` does not add a player. Roster behaviour over a session: duplicate joins ignored, unknown leaves ignored, a `list` reply correcting people who joined before the console opened, and playtimes surviving that correction. Then through the real UI against a real server: the panel, the roster syncing from the server's own reply, the header line, and `list` not echoing into the console |
| Pre-generation | parser written against **captured Chunky 1.5.3 output**, not from memory: running/finished/started lines, "No tasks to…", and lines that must not be mistaken for progress ("Preparing spawn area: 100%", "Can't keep up!"). Chunk counts checked against a real run (radius 300 → 1521) and section 14's table. Then driven through **UI Automation on the real launcher against a real Fabric server**: panel hidden until asked for, estimate tracking radius/shape/Nether, controls locked while running, live progress and bar, correct finish, controls released, and the border confirmation declining cleanly without sending a `worldborder` command. Radius 700 predicted 7,921 chunks and generated exactly 7,921 |
| **On the real machines** (2026-09-22, user-reported, not instrumented here) | "shut down for everyone" reached watchers on other computers and closed their games; self-update found the host over the LAN unaided, which is the UDP hop every bench test had to pin with `SKIN_SERVER=`. Remote-preferred discovery was *not* isolated in that session and is still open — see section 10 |

**If you change the skin server's wire format, re-run the protocol tests.** Skin
Restorer and authlib-injector are external and unforgiving; a wrong field name
fails silently as "skins just don't show up".

---

## 14. Running the sessions (host operations)

Context that is not in the code but decides how the thing actually performs.

### The real setup

| | |
|---|---|
| Machines | Dell Precision 7820 towers — servers *and* clients |
| CPU | **Xeon Gold 6244** — 8 cores, ~3.6 GHz base / 4.4 turbo. A high-frequency part, which is the right shape for Minecraft: the tick loop is single-threaded, so clock beats core count. **The host is not underpowered.** |
| GPU | NVIDIA T1000 — roughly GTX 1650 class. Fine for Minecraft; not a bottleneck. |
| Storage | 2 × 2 TB mechanical per machine. Fast at throughput, but chunk saving is latency-bound (~8-12 ms seeks), which is why `sync-chunk-writes` is forced off. |
| Players | 15-20 at once, occasionally more. `max-players` is 25. |
| Also runs | AutoCAD, Autodesk, Adobe CC — these are work machines. |

Dev box for comparison (where all testing happens): i7-7700, 4 cores, 16 GB.
Slower than the hosts, so measurements taken here are a pessimistic floor.

### What the launcher already does

Memory from the machine's RAM, G1 flags on both launch paths, `simulation-distance`
6, `entity-broadcast-range-percentage` 75, `network-compression-threshold` -1,
`sync-chunk-writes` false. All launcher-owned, so existing servers pick them up on
their next start. Nothing meaningful is left to tune in `server.properties`.

### The remaining cause: worlds are not pre-generated

The worlds are tiny (the main one was 26 MB / 29 region files). With twenty people
spreading out, the server generates terrain **during ticks**, which is the most
expensive thing it does and exactly matches "struggles sometimes". No launcher
setting fixes this. Chunky is installed on all three servers.

**Measured on the dev box** (i7-7700, so the hosts will be faster):

- **37 chunks/sec**, **9 KB per chunk**.
- radius 3000 → 141 k chunks, ~1.2 GB, ~1 h here (expect 40-55 min on a 6244)
- radius 5000 → 391 k chunks, ~3.4 GB
- radius 10000 → 1.56 M chunks, ~13 GB, 6-12 h

Disk is never the constraint; time is.

### Chunky and world borders — all verified on the real server

- **Default shape is `square`**, default radius 500. **Radius is the half-width**,
  so `chunky radius 10000` is a 20000 × 20000 area. `chunky shape circle` generates
  ~21% fewer chunks, but vanilla's border is square, so square matches better.
- `worldborder set` takes a **diameter**; Chunky takes a **radius**. Easy to mix up.
- `chunky worldborder` copies the current border into Chunky's selection — set the
  border, then this, and there is only one number to get right.
- **Chunky generates outside the border.** Proven: with the border at 200 wide, a
  patch at 5000,5000 generated fully and wrote `r.9.9`/`r.10.10` to disk. The border
  restricts *players*, not generation. This is what makes the plan below work.
- The Nether is 1:8, so `chunky radius 1250` covers a 10000 overworld. Generating
  the Nether at the overworld radius would waste hours.
- Per dimension: `chunky world minecraft:the_nether`, and for the border
  `execute in minecraft:the_nether run worldborder set <diameter>`.
- Resumable across restarts: `chunky pause` / `chunky continue`. `chunky progress`
  reports ETA and rate. `chunky quiet <seconds>` throttles the chat spam.
- **Never suggest `chunky trim` casually** — it deletes chunks outside the selection.

### The launcher now drives this

Since 2026-09-22 the server console has a **PRE-GENERATE** panel that issues the
commands below, so the plan can be followed without typing any of them. It shows
what a radius will cost before starting, tracks Chunky's own progress, and its
border button does the radius-to-diameter conversion. The commands are still worth
knowing — the command box is right there, and the panel follows a run started by
hand — but nothing here has to be done from memory any more.

### The agreed plan

The constraint is time, not capability: the user gets **~90 minutes** with the
machine at most, and people always want to play. So the two jobs are separated.

1. **Border first**, sized to what is already generated — this is what protects a
   session, because nobody can reach ungenerated land:
   `worldborder set 6000` and
   `execute in minecraft:the_nether run worldborder set 750`.
2. **Pre-generate to match** in the window: `chunky worldborder`, `chunky start`
   (~40-55 min), then the Nether at `chunky radius 375` (~2 min).
3. **Keep generating past the border afterwards, even while people play** — they
   cannot reach it, and it is far cheaper than twenty people generating randomly.
   `chunky pause` if anyone complains.
4. **Widen the border only into finished terrain**, checked with `chunky progress`.
   `worldborder add 4000 300` creeps it outward over 300 s rather than jumping.

Order is always **generate first, widen second**. A border that outruns the
generated area puts you straight back to generating during play.

Two cautions worth repeating to the user: `worldborder set` shrinks **instantly**,
so anyone outside the new border is pushed in and takes damage — check where people
have built first. And since they start fresh worlds fairly often, the cheapest habit
is to pre-generate a new world while it is empty, rather than retrofitting one
people already live in.
