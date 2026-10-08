Minecraft Portable Launcher - HOST ONLY - version @VERSION@
Built @DATE@

                 DO NOT GIVE THIS ZIP TO ANYONE
                 DO NOT PUT ANY OF IT IN THE UPDATE ZIP

  Everything in here is yours as the person running the sessions. The
  update zip that goes to the other machines deliberately contains none
  of it, and that is not tidiness - see admin.flag below.

================================================================
WHAT IS IN HERE
================================================================

  admin.flag
    An empty file, and the entire lock on the ADMIN tab. The launcher
    shows that tab if this file sits beside MinecraftLauncher.exe, and
    hides it if it does not. There is no password and nothing else to
    set.

    PUT IT: next to MinecraftLauncher.exe on your machine only.
    REMOVE IT: to hide the tab again. Nothing else changes.

    If this ever reaches the other machines, all of them get the
    required-mods editor, the list of everyone's computers and
    usernames, and everyone's crash reports. That is the whole reason
    the update zip is checked against a list before it is built.

  tools\
    The scripts that are not part of the launcher. None of them are
    needed to play; they exist for when something is wrong, or to check
    the launcher before a rollout.

    WHEN SOMETHING IS WRONG
      Why-was-this-blocked.cmd     double-click after Defender takes
      Get-DefenderDetection.ps1      something; reads why, changes
                                     nothing, needs no admin
      Diagnose-MinecraftNet.ps1    why Minecraft cannot reach the
                                     internet on a machine. Runs the
                                     same connection twice - once as
                                     Windows, once as the bundled
                                     java.exe - and the comparison is
                                     the diagnosis
      Test-DownloadHosts.ps1       which of Mojang's download hosts a
                                     machine can actually reach
      Relay.java / NetTest.java    a plain TCP relay and a connection
                                     test, for when the diagnosis says
                                     rerouting would help

    BEFORE A ROLLOUT
      Build-RolloutZip.ps1         builds both zips. Run this rather
                                     than zipping a folder by hand -
                                     it refuses to write an update zip
                                     containing anything host-only
      Verify-Publish.ps1           the launcher at the repo root really
                                     can serve updates. A build that
                                     cannot looks completely normal and
                                     fails silently on rollout day
      Verify-AutoUpdate.ps1        the whole unattended update path,
                                     host to client, byte for byte
      Verify-Admin.ps1             the ADMIN tab and the two things
                                     clients send the host
      Verify-Backups.ps1           the backup buttons on both tabs.
                                     -IncludeServerWorld also backs up
                                     the real server world and keeps it
      run-tests.ps1                the offline checks over the launcher
                                     logic. Needs the source

  Docs\
    HANDOFF.md        everything about this project: how it is built,
                        what is done, what is left, and every gotcha
                        that cost somebody an evening. Start here
    CLAUDE.md         the working rules and a one-screen status
    DEPLOY-README.md  what goes in a full from-scratch install

  required-mods.json, required-mods\
    Included only if you have set any up. This is the list the other
    machines are offered when they press PLAY, plus any jars this host
    serves itself for mods that are not on Modrinth. Edited in the
    ADMIN tab, not by hand.

    These are the HOST's copy on purpose. A client carrying its own
    would check itself against a stale list instead of asking you.

================================================================
WHAT IS DELIBERATELY NOT IN HERE
================================================================

  These are created by using the launcher. They belong to one machine
  and one machine only, so copying them anywhere - including onto
  another machine of your own - does damage rather than good.

  fleet.json
    Your record of which machines exist, what they run and who last
    played on each. Rebuilt as machines check in. Copying it to a
    second host would show you that host's own made-up history.

  crash-inbox\
    Crash reports other people's machines have sent you. Other people's
    data, and already readable in the ADMIN tab.

  crash-sent.txt
    One machine's note of which of its OWN crash reports it has already
    handed over. Copy it somewhere and that machine will think it has
    already sent crashes it has not, and will never send them.

  required-mods-declined.txt
    One machine's note of which required mods the person there said no
    to. Copy it and you make that decision on their behalf.

  backups\
    World zips. Yours, large, and already on the machine that made
    them. Nothing to distribute.

  launcher_errors.txt, update-watcher.log
    One machine's logs. Read them where they are - that is the point of
    them. update-watcher.log is the first thing to read when a machine
    is not updating; it says what it found every time it looked.

  config.txt, computer_uuid.dat
    Each machine's own settings and its identity on LAN servers. Ship
    one copy of computer_uuid.dat and every machine becomes the same
    player. Both come back on their own.

================================================================
THE ONE THING TO REMEMBER
================================================================

  The update zip is built by tools\Build-RolloutZip.ps1, which checks
  its contents against the list above before writing anything. If you
  ever build one by hand instead, check for admin.flag first. Everything
  else on that list is embarrassing; admin.flag is the one that hands
  over the keys.
