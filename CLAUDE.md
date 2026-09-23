# Minecraft Portable Launcher

**Read [HANDOFF.md](HANDOFF.md) before doing anything else.** It is the single
source of truth: what this is, how it's structured, what's done, what's left and
the gotchas. This file is a status snapshot plus working rules — keep detail in
HANDOFF.md so the two don't drift.

## Status

The C# launcher (`MinecraftLauncher/`, .NET 10 WPF) is **complete and shipping**.
The whole PowerShell feature set is ported, plus things it never had. The
PowerShell version (`MinecraftLauncher-Standalone.ps1`) is now only a behavioural
reference.

Working and verified: Client / Server / Mods / Skins / Setup tabs; Vanilla,
Fabric, Forge and NeoForge for both clients and servers; the LAN skin server
(all seven endpoints, UDP discovery, RSA signing); the WebView2 3D skin preview;
version download links; and a self-contained distributable that runs with no
.NET, Java or internet on the target.

**Skins are confirmed working across machines** (2026-08-18): a host served skins
to other clients on the offline network. The one reported oddity — the skin server
seeming to start by itself — was traced to two real causes and fixed: clients could
bind to their own stray local skin server, and the Server tab starts one silently
by design. Discovery now prefers a remote host, both start paths adopt an existing
server instead of creating a second, and the Skins tab says *why* it is running.

**New:** **server performance work** — the launcher was passing no garbage-collector
flags at all, and the Server tab reset its memory slider to 2 GB on every launch, so
servers were quietly running on 2 GB. Settings now persist, the default heap comes
from the machine's RAM, the standard G1 flag set is applied on both launch paths, and
`sync-chunk-writes` is forced off every start, and the
distances are launcher-owned so existing servers pick the tuning up too. The
**client** is tuned separately — its own G1 flag set aimed at frame smoothness, and
the harmful `-Xmn128M` inherited from the PowerShell version is gone.

**Crowd tuning (a couple of dozen players):** entity broadcast 75%, packet compression off,
simulation distance 6 — all launcher-owned, so existing servers pick them up too.
The Server tab warns if Windows is capping the processor (99% max state turns turbo
off), and the console reports the server's own "Can't keep up!" warnings, which is
the honest test of host versus client. Hardware is **not** the limit — the hosts are
high-clock 8-core workstation parts, which is the right shape for Minecraft. **The worlds
are not pre-generated, and that is the real remaining cause.**

**Pre-generation is now a panel in the server console** (`Core/ChunkyPregen.cs`):
radius and shape, an optional Nether pass at an eighth of the radius, a cost
estimate before committing, a live progress bar from Chunky's own reports, pause /
resume / stop, and a border button that handles radius-versus-diameter. Chunk counts
are exact — radius 700 predicted 7,921 and generated 7,921. Driven end to end
through the real UI against a real server. **It still has to be run on the real
worlds**; the plan is HANDOFF.md section 14 and has not changed.

**Mods are now checked against the loader.** `versions/<v>/mods` is per-version, not
per-loader, so Fabric and NeoForge on one Minecraft version shared a folder and
crashed on each other's jars. The Mods tab reads inside each jar, shows what it is
**Built for**, warns when the selected loader cannot run what is enabled, and offers
one button that turns those off and the matching ones back on — a `.disabled` rename,
so switching loaders is reversible and nothing is deleted. `Core/ModInspector.cs`.
Verified against all five real mod folders here with no false alarms.

**Mods can be downloaded from Modrinth** — "Get mods online…" on the Mods tab opens a
**Modrinth-style explorer**: a card per mod with its icon, description, downloads,
followers, last-updated and category pills; sorting and paging; and a details pane with
a version picker. Filtered to the version and loader already selected, installing into
that folder with SHA-1 verification and required dependencies followed.
Installing a newer build **turns the old one off** rather than leaving both — identity
comes from the mod id inside the jar, because two builds of one mod have different file
names and a loader handed one mod twice refuses to start. The old build is renamed, not
deleted. `Core/ModrinthApi.cs`, `UI/ModBrowserWindow.xaml`. This works on
the offline machines because **they can reach Modrinth even though they cannot reach
Mojang** — the user established that, and it is also the best evidence so far that the
block is on Mojang domains rather than on `java.exe`.

**Loader versions are checked, not just loader families.** A mod declares which loader
version it needs (`">=0.18.4"`, `"[46,)"`) and **Modrinth's API does not expose that**,
so the jar is read instead. The installed version comes off disk — profile name or
`libraries/` folder — so it works offline. Warned about on the Mods tab and right after
a Modrinth install. Unparsable requirements say nothing rather than guessing.
`Core/LoaderVersions.cs`, `Core/VersionRange.cs`.

**The Fabric loader can be updated without reinstalling Minecraft** — "Update loader…"
on the Mods tab. Online it lists what Fabric offers and installs it; offline it takes a
**pack** (profile + libraries zip) made on a machine that can download. **Installing
replaces**: the old profile and the libraries nothing else names are deleted, so one
loader remains. That matters because the launch path used to take the *first* profile
file, which sorts `0.19.2` above `0.19.3` — the game could run the old loader while the
launcher reported the new one. Nothing is removed until the replacement is on disk. Packs declare
what they are for, are refused for the wrong Minecraft version, and cannot write outside
the version folder. Forge and NeoForge are not offered — their loader is baked into the
profile their installer generates. `Core/FabricLoaderUpdate.cs`.

**Live player list:** a PLAYERS panel in the server console — who is on, how long
they have been on — built from the server's own join/leave lines and corrected by the
reply to `list`. `Core/PlayerRoster.cs`. Parsers written against real archived logs.

**Session shutdown:** a red **Shut down for everyone** button on the host closes
Minecraft on every machine after a 5-second countdown (with **Keep playing**), then
saves and stops the servers. Each game has a hidden watcher — the same exe with
`--watch-game` — because the launcher closes after PLAY. Watchers poll the host
rather than being pushed to, since Windows Firewall blocks inbound by default.
Proven on real Minecraft here and **confirmed across machines** (2026-09-22).
Only games started from a 1.2.261+ launcher have a watcher, so anyone already
playing when a machine updates must restart their game once.

Nobody can hijack it: the button only appears when *this* launcher is hosting, and
a second launcher cannot start a skin server while one is already running (it
adopts instead). Verified.

**Earlier in 1.1.0:** the **server console** — the Server tab becomes a live console once
a server starts, with a command box and a Stop that saves the world first; **one
server at a time**, enforced; and `server.properties` is now merged rather than
overwritten, which used to silently destroy hand edits and mod-written keys.

**Self-update is proven working** — end to end with real binaries here (2026-08-20)
and then **on the real machines over the LAN** (2026-09-22): an older install
spotted a newer host, prompted, downloaded, verified, swapped itself and came back
up on the new version. The field report that it "did not work" was a
missed version bump — the server-console build shipped still numbered 1.1.0, so
nothing was numbered higher and nothing was offered. Versions are generated now.
**Only the host needs a new build by hand; everyone else pulls it over the LAN.**
Transfers are gzipped between two current builds: 57 MB on the wire, not 132.

**Updates are now mandatory and install themselves.** A hidden watcher (`--watch-updates`)
outlives the launcher window — which closes at PLAY, so for most of a session nothing
was watching — checks every two minutes, downloads, shows a 10-second countdown that
cannot be refused, and swaps. Windows will not replace a running exe, so a named event
asks every other copy of the launcher to exit first and the installer waits for them;
without that the swap fails silently and starts a second launcher. Both that event and
the single-instance mutex are scoped per install folder. It writes `update-watcher.log`
beside the exe, which is the only way to tell apart the several causes of "it did not
update" on a machine nobody can reach. `Core/UpdateEnforcement.cs`, `UI/UpdateWatcher.cs`.
Proven with two real installs: the client updated itself, untouched, in 16 seconds.

**Self-update over the LAN** was added in 1.1.0: a launcher finds a host running a
newer build and updates itself from it, over the skin server already running there.
Downloads are SHA-256 verified and the user always confirms. 1.1.1 adds a version
label in the sidebar, a "Check for updates" link that always says what it found, and
a repeating check — 1.1.0 looked only once at startup, so a client that opened
before the host started their server never saw anything.

**Confirmed on the real machines** (2026-09-22, reported by the user): "shut down
for everyone" reaches watchers on other computers, and self-update finds the host
over the LAN by itself — the UDP hop that the local test had to pin with
`SKIN_SERVER=`. Both were the main open questions; both work.

**Not yet proven:** discovery preferring a *remote* skin server over a local one.
It cannot be exercised here — a host on the same box reads as local — and the
session that proved the other two did not isolate it. Skins working for everyone
is consistent with it, but is not the same observation: it also holds if nobody
had a stray local server to be misled by.

**Current build: 1.2.265.376** — 2026-09-23. Updates are mandatory from here on, so
this is the last build anyone copies by hand: put it on the host and every other
machine installs it itself. Also carries pre-generation, the mod loader checks (family
*and* version), the player list, Modrinth mod downloading and Fabric loader updating. At the repo root and in
the rollout zip kept outside the repo (56.2 MB, verified byte-for-byte). Host
machine only — everyone else pulls it over the LAN. Confirmed
able to serve it: the root build advertises all 6 files with gzip and hands back its
own exe byte-for-byte.

Full list of outstanding work is HANDOFF.md section 10.

## Where to look in HANDOFF.md

| Question | Section |
|----------|---------|
| What is this project? | 1 |
| C# project layout | 3 |
| How to build / publish | 4 |
| What's done | 5 |
| Skin server contract (do not break) | 6 |
| How the skin system works | 7 |
| Deployment layout + building the zip | 8 |
| Next tasks | 10 |
| Host operations: hardware, Chunky, borders | 14 |
| Gotchas + fixed reference bugs | 11 |
| Running with no internet | 12 |
| Testing: the test project + what's covered | 13 |

## Build

```powershell
cd MinecraftLauncher
dotnet build
```

That compiles, but it does **not** refresh the launcher at the repo root. That copy
is the published self-contained build on purpose — only that one can serve updates
to the other machines — so changing it means a `dotnet publish` and copying the exe
plus its `*_cor3.dll` siblings across. The publish command (it needs `--source` on
this machine) is in HANDOFF.md section 4.

Do not run the launcher from `bin\Debug`: it looks for `runtime\` and `servers\`
beside the exe and finds nothing.

## Working rules

- **`MinecraftLauncher-Standalone.ps1` is the behavioural reference.** When
  something is unclear, read how the PowerShell version did it — but see the next
  rule before copying it.
- **Do not "restore fidelity" to the PowerShell version for anything listed under
  HANDOFF.md section 11.** Those behaviours were wrong and were deliberately
  changed. Several are subtle (lexicographic version sort, unverified assets).
- **Keep `Core/` free of UI dependencies.** It is pure logic, which is what lets
  every area be tested from a console harness. Put logic there, keep the WPF
  code-behind thin.
- **Do not add `RuntimeIdentifier` or `PublishSingleFile` to the csproj.** They
  belong only on the `dotnet publish` command; in the csproj they force runtime
  pack downloads on every plain `dotnet build`.
- **Do not set `Private=true` on the WebView2 references.** They load from
  `runtime\webview2\` at run time; copying them next to the exe breaks the
  deployment layout. See HANDOFF.md section 3.
- **Do not hand-edit `<Version>`.** It is generated in the csproj from the build
  date and time, precisely because forgetting to bump it once already made the
  update feature look broken — the server-console build went out still calling
  itself 1.1.0, and equal versions are ignored on purpose. Change `VersionPrefix`
  only for a release people should notice.
- **Verify against reality, not the build log.** Most bugs this project hit
  compiled fine and failed at run time — a wrong Java version, a silently
  misdirected installer, a profile the scanner could not see. Launch it, drive the
  UI, read the server output.
- **Run `tools\run-tests.ps1` before and after changing `Core/`.** 340 checks, about
  4.5 seconds, exit code 0 when clean. `-Offline` skips the network suite. Add to
  `MinecraftLauncher.Tests/Suites/` as work lands — and write the checks against
  **captured real output**, not an invented format; every parser in this project that
  was written from memory turned out to be wrong about something.
- **Do not add a test-framework package.** The runner is one file and the machine has
  no NuGet feed configured; a dependency is one more way the build stops working
  offline. Do not add `UseWPF` to the test project either — its absence is what
  enforces the rule above about `Core/`.
- **Run `tools\Verify-Publish.ps1` after every publish.** A framework-dependent build
  at the repo root looks perfectly normal and silently refuses to serve updates; this
  is the only thing that catches it before rollout day. `tools\Verify-AutoUpdate.ps1`
  covers the unattended update path when that changes. See HANDOFF.md section 13.
- **Keep HANDOFF.md updated** as work lands — sections 5, 10, 11 and 13. Update it
  there, not here.
