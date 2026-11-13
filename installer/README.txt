MOON SWEEPERS VR
================

What this is
------------
Play Moon Sweepers (Steam) in VR with head tracking,
using UUVR plus a small custom plugin ("Moon XR Starter") that makes UUVR
work on this game's Unity 6 engine. You can play with the VR motion
controllers (Quest Touch) - you sweep where your right hand points - or
with an Xbox/PlayStation controller. Both work at the same time.

Tested on: Moon Sweepers 1.0.5, Meta Quest 3S via SteamVR.

Before you start
----------------
- You need a free Nexus Mods account (nexusmods.com). The installer
  asks you to download one file from there: the "MoonSweepers BepInEx
  Patcher" by boverson777. It isn't included in this download because
  it's someone else's work.
- Play solo while using this. Mods + multiplayer can cause problems.

Install
-------
1. Extract the WHOLE zip somewhere (e.g. your Desktop). Don't run it
   from inside the zip.

2. Make sure the game is closed, then double-click Install.bat.
   If Windows shows a blue "protected your PC" box, click
   More info > Run anyway.

   The installer finds the game, backs up your save, and downloads
   BepInEx and UUVR by itself. When it gets to the patcher it opens
   the Nexus Mods page in your browser:
     - log in,
     - click MANUAL DOWNLOAD on "MoonSweepers BepInEx Patcher",
     - choose SLOW DOWNLOAD if asked, and let it save to Downloads.
   The installer notices the download and carries on by itself.
   (Press B in the installer window to pick the file yourself.)

3. Connect your headset to the PC (Link / Air Link / Virtual Desktop /
   Steam Link) and START STEAMVR FIRST.

4. Launch Moon Sweepers from Steam as normal (turn on your Xbox/PS
   controller first if you want to use one). The first launch takes a
   minute or two longer and needs an internet connection (BepInEx
   prepares the game once).

   Look straight ahead while it loads. The game appears in the headset
   a few seconds after the window opens.

If the install fails, install-log.txt (next to Install.bat) says why.

Controls (Quest / Touch motion controllers)
-------------------------------------------
- Point your RIGHT hand to aim; the tool is held in that hand
- Point your RIGHT hand at items / machines to highlight and use them
  (a faint laser shows where you're pointing when you're not sweeping)
- Left stick ............................. move
- Right thumb on the THUMB REST + left stick  D-pad
- Right stick ............................ turn and look up / down
- Triggers ............................... LT / RT (right trigger sweeps)
- Grips .................................. LB / RB
- A, B, X, Y ............................. A, B, X, Y (A = jump)
- Right grip + B ......................... Start (pause)
- Left grip + Y .......................... Select
- Hold BOTH grips and press A ............ recenter the view
  (or click BOTH thumbsticks together)
- Hold BOTH grips, right stick UP / DOWN . raise / lower your view
- Hold BOTH grips and press X ............ laser on / off
- Hold BOTH grips, right stick LEFT/RIGHT  laser colour
- Hold BOTH grips, left stick LEFT/RIGHT . laser brightness
- Hold BOTH grips, left stick UP / DOWN .. held-tool height (gamepad play)
- Hold BOTH grips, click LEFT stick ...... hide / show the HUD
- Tool upgrade / supply wall screen: left stick steps through the
  items, A selects, grips switch tabs.
- Moon map: point the laser at the map or a teleport station and
  press A to select. The laser turns bright yellow and switches on by
  itself while the map is open.

The HUD: tool belt at the bottom of your view, compass at the top,
dust / money / notices on your LEFT wrist.

Controls (Xbox / PlayStation controller)
----------------------------------------
- Hold LB + RB and press A ............... recenter the view
  (or click BOTH thumbsticks together)
- Hold LB + RB, D-pad UP / DOWN .......... raise / lower your view
- Hold LB + RB, D-pad RIGHT / LEFT ....... raise / lower the held tool
  (5 cm per press, saved automatically)
- Hold LB + RB and press X / Y ........... laser on/off / laser colour
- Hold LB + RB, click LEFT stick ......... hide / show the HUD
Keyboard: F9 recenter, F11 / F10 view up / down.

Motion blur, the screen vignette and the brush reticle are switched off
automatically in VR. The main and pause menus float a little further away;
the field manual and contract are shown face-on in front of you.

Tweaking
--------
Settings live in BepInEx\config\claude.moonxrstarter.cfg in the game
folder (edit with the game closed): menu panel distance /
size / height, wrist HUD (Hand = Left, Right or Off; Width; Height Above
Hand), compass / tool belt size and position (Compass Size, Compass Raise,
Tool Belt Size, Tool Belt Lower), Hide HUD, Main Menu Distance, Map Width,
Teleport Panel Size, Thumb Rest D-pad, field
manual size, laser colour / brightness, and the height offsets above.

If something goes wrong
-----------------------
- Game shows on the monitor but not the headset: SteamVR was not running
  before the game started. Close the game, start SteamVR, launch again.
- Motion controller buttons do nothing: in SteamVR open Settings >
  Controllers > Manage controller bindings, pick Moon Sweepers and
  choose the Default binding. To switch motion controls off entirely,
  set Enabled = false under [Motion Controls] in the config file.
- Things highlight oddly or prompts appear in the wrong place: set
  Hand Interaction = false under [Motion Controls] to go back to
  looking at things to select them.
- Wrist HUD in the way: set Hand = Off under [Hand HUD] to put the
  HUD back in front of you.
- No mods loading at all: BepInEx\LogOutput.log in the game folder
  should contain "Chainloader startup complete". If not, check the
  patcher is in BepInEx\patchers\MoonSweepers.ModEngine\ and run
  Install.bat again.
- A game update may break this until the mods are updated.

Uninstall
---------
Delete from the game folder: the BepInEx and dotnet folders,
winhttp.dll, doorstop_config.ini and .doorstop_version. Then in
Steam: Properties > Installed Files > Verify integrity of game files.

Credits
-------
- UUVR by Raicuparta (GPL-3.0) - github.com/Raicuparta/uuvr
- BepInEx 6.0.0-be.791 (LGPL-2.1) - github.com/BepInEx/BepInEx
- MoonSweepers BepInEx Patcher 1.0.2 by boverson777 - not included;
  get it from nexusmods.com/moonsweepers/mods/9 (please endorse it)
- Moon Sweepers VR plugin and installer (MIT) - source on GitHub

Support
-------
If you enjoy the mod, please consider buying me a coffee:
  buymeacoffee.com/VEEERRR
Thank you!
