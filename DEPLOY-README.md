# Minecraft Portable Launcher — offline install

Extract this zip anywhere and run `MinecraftLauncher.exe`. There is no installer
and nothing to configure.

## Requirements

None. The launcher, Java, and the browser component used by the skin preview are
all bundled — the target machine needs no .NET, no Java, and no internet.

## What is in here

```
MinecraftLauncher.exe        the launcher
*.dll                        must stay next to the exe
background.png               wallpaper shown in the launcher; swap it for your own
version-links.txt            where game versions can be downloaded from
runtime/                     Java 17/21/25, WebView2, skinview3d, authlib-injector
versions/                    installed clients — empty until you add one
servers/<loader>-<version>/  server instances, including worlds
skins/                       skin PNGs shared over the LAN
```

## Adding a game version

Game versions are **not** included — they are large and not redistributable.

On the Client tab, press **Download Versions** for the list of places to get them.
Clicking an entry opens it in your browser. Extract the download into `versions\`
so you end up with:

```
versions\<version>\versions\<version>.json
```

Reopen the launcher and the version appears in the dropdown.

The list comes from `version-links.txt` next to the exe — edit it in any text
editor, or use the **Edit list** button in that window. One entry per line:

```
1.21.1 (Fabric) | https://example.com/mc/1.21.1.zip
```

If a machine has internet, the **Setup** tab can download versions from Mojang
directly instead, including Fabric, Forge and NeoForge.

Keep the whole folder together — the launcher finds everything by looking beside
its own executable, which is what makes it portable.

## First run

`config.txt` is written the first time you press PLAY or START SERVER, and
`computer_uuid.dat` the first time the game is launched. Neither is in the zip on
purpose: the UUID identifies this machine on LAN servers, so shipping one copy
would give every machine the same player identity.

Set your username on the Client tab before playing.

Windows may warn about an unrecognised app the first time — the exe is unsigned.
If the zip came from a browser or network share, right-click it → Properties →
**Unblock** before extracting, or Windows may block the bundled DLLs.

## Playing together

1. One machine hosts: **Server** tab → pick version and type → **START SERVER**.
2. Everyone else joins that machine's LAN address on port `25565`.

The skin server starts automatically with the server; nobody needs to configure
anything. Players add their own skin on their own **Skins** tab and it is pushed
to the host at launch.

Ports used: `25565` game, `25567` skin server, `25568` skin discovery.
Custom skins need Fabric, with `fabric-api` and `skinrestorer` in the server's
`mods/` folder.

## Offline limits

Everything works offline **except the Setup tab**, which downloads new versions
from Mojang, and server installs for a version that has no `server.jar` yet.
Both are already populated here, so this only matters if you want to add a
version that is not in the zip.
