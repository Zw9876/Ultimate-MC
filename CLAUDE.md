# Minecraft Portable Launcher

**Read [HANDOFF.md](HANDOFF.md) before doing anything else.** It is the single
source of truth: what this is, how it's structured, what's done, what's left and
the gotchas. This file is the working rules plus a one-screen status — keep detail
in HANDOFF.md so the two don't drift, and resist re-narrating features here.

## Status

The C# launcher (`MinecraftLauncher/`, .NET 10 WPF) is **complete and shipping**.
The whole PowerShell feature set is ported plus a good deal more;
`MinecraftLauncher-Standalone.ps1` is now only a behavioural reference.

**Current build: 1.2.266.222** (2026-09-23), at the repo root and in the rollout
zip kept outside the repo (56.2 MB, verified byte-for-byte). Updates are mandatory
from here on, so **this is the last build anyone copies by hand**: put it on the
host and every other machine installs it itself.

Shipping and verified on the real machines: Client / Server / Mods / Skins / Setup;
Vanilla, Fabric, Forge and NeoForge; the LAN skin server and 3D skin preview; the
server console with pre-generation, the live player list and crowd tuning; mod
loader checks by family *and* version; Modrinth mod downloading; Fabric loader
updating that replaces rather than accumulates; session shutdown; and self-update
over the LAN, now unattended. What each of those does and why is HANDOFF.md
section 5 — read it there rather than expecting a summary here.

Two things are worth carrying in your head because they shape decisions:

- **The worlds are still not pre-generated.** Hardware is not the limit — the hosts
  are high-clock 8-core workstation parts. This is the real remaining cause of host
  lag, and the plan is HANDOFF.md section 14.
- **Discovery preferring a *remote* skin server over a local one is not yet proven.**
  It cannot be exercised on one machine, and skins working for everyone is
  consistent with it but is not the same observation.

Outstanding work is HANDOFF.md section 10.

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
| Antivirus flagging the launcher | 15 |

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
- **Run `tools\run-tests.ps1` before and after changing `Core/`.** 452 checks, about 11
  seconds, exit code 0 when clean (371 of them need no internet). `-Offline` skips the network suite. Piped
  anywhere it prints only failures and the count — `-ShowAll` for a line per check.
  Add to `MinecraftLauncher.Tests/Suites/` as work lands — and write the checks against
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
