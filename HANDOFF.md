# Minecraft Portable Launcher — Project Handoff

This document is a complete briefing for continuing the project in **Claude Code**.
Give this file to Claude Code first; it explains what exists, how it works, what's
done, and what's left.

---

## 1. What this project is

A **portable, offline-friendly Minecraft launcher** for Windows. It runs from any
folder on any drive with no installation. It can launch the Minecraft client,
host a dedicated server, download versions, manage mods, and — its standout
feature — share custom skins across a LAN with zero per-player configuration.

There are currently **two implementations**:

1. **PowerShell** (`launcher.ps1`) — the original, fully working, feature-complete
   version. Compiled to `.exe` with ps2exe. This is the production/reference version.
2. **C#/.NET 10 WPF** (`MinecraftLauncher/` project) — an in-progress rewrite.
   Client and Server tabs are ported and working. Setup, Mods, and Skins tabs are
   not yet ported.

The goal going forward: **finish porting the PowerShell version to C#**, because
the C# version is far less fragile (the PowerShell + ps2exe combo caused many
hard runtime bugs around processes, threads, and job objects that simply don't
exist in C#).

---

## 2. Files to give Claude Code

Put these in the project working directory:

| File | Purpose |
|------|---------|
| `launcher.ps1` | The complete PowerShell version — **the reference implementation**. Everything not yet ported to C# must be ported *from here*. |
| `MinecraftLauncher/` (whole folder) | The in-progress C# project (see layout below). |
| `README.md` | User-facing documentation (already on GitHub). |
| This handoff doc | Context/briefing. |

The PowerShell file is the single source of truth for behavior. When porting a
feature, read how `launcher.ps1` does it and replicate the logic in C#.

---

## 3. C# project layout

```
MinecraftLauncher/
├── MinecraftLauncher.csproj      Project file. Targets net10.0-windows, WPF enabled.
├── app.manifest                  DPI awareness manifest.
├── Core/                         ── Engine (no UI dependency; pure logic) ──
│   ├── Paths.cs                  Portable path resolution (everything relative to the exe).
│   ├── AppConfig.cs              Reads/writes config.txt (NICK=, max_MEM=, etc.).
│   ├── ComputerId.cs             Stable per-machine offline UUID (computer name → MD5).
│   ├── VersionScanner.cs         Lists installed versions; detects loaders (Vanilla/Fabric/Forge).
│   ├── ClientLauncher.cs         Builds classpath + args and launches the client. (Largest/most complex.)
│   ├── ServerLauncher.cs         Writes server.properties/eula, launches the server.
│   ├── SkinDiscovery.cs          Client-side: UDP discovery of a skin server + skin upload.
│   └── SkinModel.cs              Reads steve/alex model from skins/metadata.json.
└── UI/                           ── WPF presentation layer ──
    ├── App.xaml / App.xaml.cs    App entry point + dark theme resources/styles.
    ├── MainWindow.xaml           Sidebar + CLIENT and SERVER tab layouts.
    └── MainWindow.xaml.cs        Wires the UI controls to the Core engine.
```

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

The built output lands in `bin\Debug\net10.0-windows\`. A framework-dependent
build's exe needs its sibling files (`.dll`, `.runtimeconfig.json`, `.deps.json`),
so to run it next to game files, copy the **whole** output folder's contents — not
just the exe.

**For a standalone distributable exe** (no .NET needed on target — ideal for the
offline machines):
```powershell
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true
```
Output: `bin\Release\net10.0-windows\win-x64\publish\` — one ~150MB portable exe.

> Note: `RuntimeIdentifier`/`PublishSingleFile` are intentionally **kept out of the
> csproj** so a plain `dotnet build` doesn't try to download platform runtime packs.
> They belong only on the publish command.

---

## 5. What's DONE in C#

- **Client tab** — version + loader dropdowns, username, memory slider, PLAY.
  Launches the game. Verified working.
- **Server tab** — version, type, port, gamemode, difficulty, max players, memory,
  PVP. Writes config, starts the server in a console window. Verified working.
- **Config persistence** — `config.txt`, same format as PowerShell version.
- **Portable paths** — all relative to the exe, works from any drive.
- **Offline UUID** — stable per machine.
- **Skin client-side hooks** — discovery + upload + authlib-injector attachment are
  already wired into `ClientLauncher` (so a client will still push its skin and get
  the javaagent). What's missing is the *host* side (the skin server itself).

---

## 6. What's LEFT to port (from launcher.ps1)

In rough priority order:

### a) Skin server (host side) — the biggest and most important piece
The PowerShell version runs a local HTTP skin server that Skin Restorer queries.
It must be ported to C#. **In C# this is dramatically simpler** than the PowerShell
version, which needed WMI, job-object escapes, `Socket.Select` polling, and
persistent-RSA gymnastics. A C# `HttpListener` (or a background socket loop) in a
real thread handles all of it natively.

Endpoints the server must implement (see `launcher.ps1`, the `$serverScript`
here-string inside `Start-SkinHttpServer`):
- `GET /` → authlib-injector metadata (skinDomains + signaturePublickey, PEM).
- `GET /minecraft/profile/lookup/name/<name>` → `{id,name}` (the modern Services API — the one Skin Restorer actually calls).
- `GET /users/profiles/minecraft/<name>` → legacy lookup.
- `POST /profiles/minecraft` → bulk lookup.
- `GET /session/minecraft/profile/<uuid>?unsigned=false` → signed texture profile.
- `GET /skins/<name>.png` → serve the PNG.
- `POST /upload/<name>?model=<steve|alex>` → receive a skin upload (binary body).
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

### b) Setup tab — version downloading
Downloads versions from Mojang's manifest with SHA-1 verification, plus optional
Fabric. See the Setup region in `launcher.ps1` (background runspace + queue + timer).
In C#, use `HttpClient` + `async/await` + `IProgress<T>` for progress reporting.

### c) Mods tab
Add/remove/enable/disable mods for client or server versions. Mostly file
operations. See the Mod Management region in `launcher.ps1`.

### d) Skins tab (UI)
ListView of skins (username/model/file), add/remove/toggle model/open folder, and
the embedded 3D preview (WebView2 + skinview3d). The 3D viewer is the fiddly part;
WebView2 needs the runtime files and `UnsafeLoadFrom` to avoid the zone block.
See the Skins region in `launcher.ps1`.

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

Required on the **server** (in its `mods/` folder, versions matching the MC version):
- `fabric-api-<version>.jar`
- `skinrestorer-<version>-fabric.jar`

Required **runtime deps** bundled with the launcher:
- `runtime/authlib-injector/authlib-injector.jar`
- `runtime/webview2/` (DLLs) + `runtime/webview2runtime/` (Fixed Version runtime)
- `runtime/skinview3d/skinview3d.bundle.js`
- Java runtimes under `runtime/<major>/`

Ports: `25565/TCP` Minecraft, `25567/TCP` skin server, `25568/UDP` discovery.

Skin system is **Fabric-only** (Skin Restorer is Fabric-only).

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

---

## 9. GitHub repo

- URL: https://github.com/Zw9876/Minecraft-Portable-Launcher
- Tracked files: `MinecraftLauncher-Standalone.ps1`, `README.md`, `.gitignore`, `LICENSE`
- The `.gitignore` excludes runtime/versions/servers/skins/config and all user state.
- Releases: distribute the runnable bundle (exe + runtime) as a zip asset; the
  repo itself stays code-only.

---

## 10. Suggested first tasks for Claude Code

1. **Confirm the C# project builds cleanly**: `dotnet build` — fix any warnings.
2. **Add a console test harness** (a small second project referencing `Core/`) so
   the launch/scan logic can be exercised without the GUI.
3. **Port the skin server** (`Core/SkinServer.cs`) from the PowerShell
   `Start-SkinHttpServer` / `$serverScript`, using a real background thread and
   `HttpListener` or a `TcpListener` loop. Wire `Start Server` in the UI to launch
   it, and write the Skin Restorer config first (port `Write-SkinRestorer-Config`).
4. Then the **Setup**, **Mods**, and **Skins** tabs.

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
- (PowerShell-era, for context) skin URLs must be a domain the client trusts, hence
  authlib-injector; Skin Restorer calls the modern
  `/minecraft/profile/lookup/name/<name>` endpoint; and offline UUID is MD5v3 of
  `OfflinePlayer:<name>`.
