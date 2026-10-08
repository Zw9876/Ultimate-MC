Minecraft Portable Launcher - version @VERSION@
Built @DATE@

READ THIS FIRST: ONLY ONE COMPUTER NEEDS THIS FILE
  Put this on the computer that HOSTS the server. Everyone else gets it
  over the network.

  On the host:
    1. Close the launcher.
    2. Copy the 6 files here into the launcher folder, replacing the old
       ones. Choose Replace when Windows asks.
    3. Open the launcher and start your server as normal.

================================================================
0. IF WINDOWS DEFENDER EATS THIS FILE - DO THIS FIRST
================================================================

  Defender may quarantine the zip or the launcher. Nothing is wrong with
  it. An unsigned 126 MB program that downloads a new copy of itself and
  replaces itself with it is a truthful description of this launcher and
  also of a virus, so Defender's guesswork scores it as one.

  DO THIS BEFORE COPYING THE FILES ACROSS, not after - otherwise it can
  be taken while it is arriving.

  On each computer:
    1. Open Windows Security.
    2. Virus & threat protection.
    3. Manage settings (under "Virus & threat protection settings").
    4. Scroll down to Exclusions, then "Add or remove exclusions".
    5. Add an exclusion -> Folder -> pick the folder that holds
       MinecraftLauncher.exe.

  IF IT WAS ALREADY TAKEN
    The exclusion does not bring anything back. Go to Windows Security ->
    Protection history, find the item, then Actions -> Allow. Do the
    exclusion first or the next scan may take it again.

  DO NOT use a script to do this. A script that adds an exclusion is
  itself treated as a virus - correctly, because that is what real
  malware does first. It has to be the clicks above.

  AVOID DOWNLOADING THIS ZIP IF YOU CAN
    A file arriving from a browser is marked as coming from the internet,
    and that mark is most of what Defender reacts to. The same file on a
    USB stick or copied over the network carries no such mark and is not
    scored the same way. Copying it across is the quiet path.

  WHAT IT COSTS
    Defender stops scanning that folder, including your mods. That is a
    real trade. It is worth making on your own machines for a program
    you know the origin of, and not worth making anywhere else.

================================================================
1. UPDATES ARE AUTOMATIC AND COMPULSORY
================================================================

  YOU SHOULD NOT HAVE TO COPY FILES ANYWHERE BY HAND AFTER THIS.

  Every launcher updates itself. Nobody is asked, and nobody can
  decline. All the machines end up on the same version without anyone
  thinking about it.

  HOW IT WORKS
    When a launcher spots that the host is running a newer version, it
    downloads it quietly, then shows a 10 second countdown and installs
    it. Minecraft is not affected and keeps running. The launcher
    reopens by itself once it is done.

  IT KEEPS WATCHING AFTER YOU PRESS PLAY
    The launcher closes when you press PLAY, so previously nothing was
    left running to notice an update - which is exactly why the machines
    drifted onto different versions. A small hidden helper now stays
    running in the background and keeps checking every couple of
    minutes. It uses almost nothing and stops when the computer shuts
    down.

  IF SOMETHING LOOKS WRONG
    Each machine keeps a small file next to the launcher called
    update-watcher.log. It records one line every time it checks, saying
    what it found - no host, a host on the same version, or an update.
    If a machine is ever out of date, that file says why.

================================================================
2. NEW IN THIS BUILD: BACK UP YOUR WORLDS
================================================================

  Client tab -> "Back up worlds..."     (your single-player worlds)
  Server tab -> "Back up worlds..."     (a server's world)

  Until now nothing here backed anything up. Everything else on these
  machines can be rebuilt from a download - the launcher, Java, the
  mods, Minecraft itself. A world cannot. If one corrupts it is simply
  gone, and so is everything built in it.

  It writes a dated zip into a "backups" folder beside the launcher and
  keeps the last 5 of each world, deleting older ones so the folder
  cannot grow forever. It tells you how big the world is and how much
  room is left on the drive BEFORE you start, and refuses rather than
  filling the disk.

  IT IS SAFE TO USE WHILE THE SERVER IS RUNNING
    The server is asked to write everything to disk and hold still for
    the moment the copy takes, then carry on. Nobody is kicked and the
    world is not unloaded. This matters more than it sounds: without it
    a copy taken from a live world still SUCCEEDS, having caught part of
    the world halfway through being written, and you only find out when
    somebody loads it.

  THE BEST MOMENT TO TAKE ONE
    Before pre-generating. Pre-generation rewrites a great deal of the
    world at once, and that is exactly when you want yesterday's copy
    sitting safely in a zip.

  For reference: a 110 MB world took 3 seconds and came out at 70 MB.

================================================================
3. ALSO NEW: TWO THINGS YOUR COMPUTER NOW TELLS THE HOST
================================================================

  Both of these happen on their own, over your own network, between
  machines in the same room. They are written here because you should
  know what leaves your computer rather than find out later.

  WHICH VERSION YOU ARE ON, AND YOUR NAME
    When you open the launcher, and again when you press PLAY, your
    computer tells the host three things: the computer's name, the
    launcher version it is running, and the Minecraft name you play
    under. Nothing else.

    WHY: the whole point of automatic updates is that every machine ends
    up on the same version, and until now nobody could actually check
    that. Several updates went out without anyone being able to say
    which machines had them. Now the host can look instead of guess, and
    the name is there so a machine can be recognised as yours rather
    than as DESKTOP-7F3K2A1.

  YOUR CRASH REPORTS
    If Minecraft closes itself, it writes a report saying why. When your
    game next exits, that report is handed to the host.

    WHY: the person who fixes a crash is whoever is hosting, and the
    report was sitting on your machine where they could not read it -
    so fixing anything meant walking over to each computer. There is now
    nothing for you to do.

    A crash report describes what the game was doing and which mods were
    loaded. It is not a copy of your world and contains nothing you
    typed in chat.

  IF NO ONE IS HOSTING, none of this happens and nothing is kept
  waiting except crash reports, which go the next time a host is there.

================================================================
4. UPDATING THE FABRIC LOADER WITHOUT REINSTALLING
================================================================

  Mods tab -> "Update loader..."

  CHOOSE CLIENT OR SERVER AT THE TOP OF THAT WINDOW.
    A client and a server keep their loader in completely different
    places, so the choice changes everything below it, including what
    MAKE A PACK produces.

    A client pack and a server pack are NOT interchangeable. The window
    refuses the wrong one rather than breaking your install, and the
    kind is written into the file name.

  Installing a loader REPLACES the one you had, so you always have
  exactly one. Your worlds and mods are untouched.

  ON A COMPUTER WITH INTERNET
    Press FIND VERSIONS, choose one, press INSTALL.
    For a SERVER, it needs internet once more on the next start to fetch
    the new loader's supporting files.

  ON THE COMPUTERS WITHOUT INTERNET
    1. On the computer that HAS internet, choose CLIENT or SERVER, then
       press MAKE A PACK. It writes a single .zip file of a few MB.
    2. Copy that file to the other computers.
    3. On each, choose the same CLIENT or SERVER, press OPEN A PACK and
       pick the file. It tells you what the pack contains before
       changing anything.

  Nothing is removed until the new loader is safely in place, so a
  failed download cannot leave you with no loader at all.

  Forge and NeoForge are not offered - their loader is built into the
  version when it is first installed and cannot be swapped afterwards.

================================================================
5. GETTING MODS WITHOUT A USB STICK
================================================================

  These computers cannot reach Minecraft's own servers, but they CAN
  reach Modrinth.

  Mods tab -> "Get mods online..."

  A browser laid out like the Modrinth website: a card per mod with its
  icon, what it does, downloads and when it was last updated. Search, or
  sort. Click a mod, pick a version, press DOWNLOAD. Anything the mod
  REQUIRES comes with it. Results appear shortly after you stop typing;
  Enter still works if you do not want to wait.

  UPDATING A MOD YOU ALREADY HAVE
    Install the newer version and the old one is switched off for you.
    Two versions of one mod stops the game starting, and the error names
    the mod rather than the files, so it is hard to diagnose. The old
    version is renamed, never deleted.

  ARE MY MODS OUT OF DATE?  (Mods tab -> "Check for updates")
    Asks Modrinth whether anything you have installed has a newer build
    for your version and loader, and fills in an Update column. It can
    install them all in one go. The build being replaced is turned off,
    not deleted, so going back is one click.

    A mod Modrinth has never seen says "not on Modrinth" rather than
    "up to date" - those are different answers and only one is honest.

================================================================
6. MODS ARE CHECKED AGAINST YOUR LOADER
================================================================

  WRONG LOADER. All the mods for one Minecraft version share a folder,
  whichever loader you use. Play Fabric, switch to NeoForge, and the
  game crashes on startup with an error that explains nothing. The Mods
  tab shows a "Built for" column and warns you, with a button that turns
  the wrong ones off. Nothing is deleted - turning a mod off renames it,
  so you can flip between both sets.

  LOADER TOO OLD. A mod can be built for the right loader but need a
  NEWER VERSION of it than you have. Modrinth does not tell anyone this;
  it is written inside the mod file. The launcher reads it and warns
  you, so you find out now rather than when the game refuses to start.

  TIDYING UP TURNED-OFF MODS  (Mods tab -> "Delete disabled")
    Switching a version between loaders leaves the folder full of
    .jar.disabled files until the list is hard to read. This clears them
    out in one go. It shows you exactly which files it means first, and
    they go to the RECYCLE BIN, not for good, so a mistake is undoable.
    Anything switched on is never touched.

================================================================
7. MODS THE HOST ASKS EVERYONE TO HAVE
================================================================

  When you press PLAY, your launcher checks whether the host wants any
  particular mods for the version you are starting. If you are missing
  any, it offers to download them and tells you what they are.

  SAYING NO IS FINE. The game starts either way, and you will not be
  asked again about the same thing. If you change your mind there is a
  CHECK FOR REQUIRED MODS button on the Client tab - it uses whichever
  version is selected there.

  If a required mod needs other mods, those are offered too, and a mod
  you have that is TOO OLD for it counts as missing - so it gets updated
  rather than quietly left to stop the game starting.

  Mods you already have are left alone, including ones you have switched
  off. Turning a mod off is treated as a decision, not a mistake, so
  nothing is re-enabled behind your back.

================================================================
8. WHY DID THE GAME CLOSE?
================================================================

  Client tab -> "Crash reports..."

  When Minecraft shuts itself, it writes a report explaining why. Nobody
  reads them - they are a hundred lines of stack trace. This reads it
  for you and says, in plain words, what the game was doing and WHICH
  MOD broke and why. For example:

    The report blames this mod:
      durabilitytooltip (1.1.6)
        why: requires supermartijn642configlib 1.1.6 or above
             Currently, supermartijn642configlib is not installed

  The whole report is underneath, with a button to copy it. Nothing is
  deleted - the reports stay where the game put them.

  Forge and NeoForge name the mod that failed. Fabric does not, so for a
  Fabric crash it shows the error but will not guess at a culprit.

================================================================
9. RUNNING A SERVER
================================================================

  PRE-GENERATE THE WORLD (Server console -> PRE-GENERATE)
    Builds terrain ahead of time so the server is not inventing it while
    everyone plays, which is what causes the lag spikes. It tells you
    how long and how much disk before starting. People can keep playing
    while it runs.

    TAKE A BACKUP FIRST - section 2.

    Set the border FIRST to land that already exists, keep generating
    past it, and only widen into finished ground. Making a border
    SMALLER takes effect instantly and pushes anyone outside it in.

  WHO IS ON (Server console -> PLAYERS)
    Everyone connected and how long they have been on. The panel is
    already open; it no longer starts hidden behind a button.

WHAT THIS DOES NOT TOUCH
  Your versions, servers, worlds, mods, skins, runtime folder and
  settings are all left exactly as they are.

  Windows may show a blue "Windows protected your PC" box the first
  time. Click More info, then Run anyway.

FILES
  MinecraftLauncher.exe          the launcher itself
  D3DCompiler_47_cor3.dll        \
  PenImc_cor3.dll                 |  Windows graphics files the launcher
  PresentationNative_cor3.dll     |  needs to start. Unchanged, included
  vcruntime140_cor3.dll           |  so the set is always complete.
  wpfgfx_cor3.dll                /

  Why-was-this-blocked.cmd       double-click if Defender blocks this;
  Get-DefenderDetection.ps1        reads why, changes nothing, no admin
