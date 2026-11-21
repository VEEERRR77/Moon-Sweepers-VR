// Moon XR Starter: starts Unity 6's XR display/input subsystems (OpenVR) so UUVR's
// VR camera actually renders to the headset. UUVR's legacy XRSettings.enabled path
// does nothing on Unity 6, so this does the modern equivalent directly.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace MoonXrStarter
{
    [BepInPlugin("claude.moonxrstarter", "Moon XR Starter", Version)]
    public class Plugin : BasePlugin
    {
        /// <summary>The mod's version; keep in step with the GitHub release tag (v1.5.3 etc.).</summary>
        internal const string Version = "1.5.3";
        internal static BepInEx.Configuration.ConfigEntry<bool> CheckForUpdates;
        internal static BepInEx.Configuration.ConfigEntry<float> BuildMenuWidth, GarageMenuDistance;
        internal static BepInEx.Configuration.ConfigEntry<bool> PerfLog;
        internal static BepInEx.Configuration.ConfigEntry<bool> RadioEnabled, RadioShown;
        internal static BepInEx.Configuration.ConfigEntry<string> RadioStations;
        internal static BepInEx.Configuration.ConfigEntry<int> RadioStation;
        internal static BepInEx.Configuration.ConfigEntry<float> RadioVolume, RadioHeight, RadioWidth;
        internal static ManualLogSource L;
        internal static BepInEx.Configuration.ConfigEntry<float> UiDistance;
        internal static BepInEx.Configuration.ConfigEntry<bool> HeadTrackingEnabled;
        internal static BepInEx.Configuration.ConfigEntry<bool> MinigameDiagnostics;
        internal static BepInEx.Configuration.ConfigEntry<bool> HockeyKeepView;
        internal static BepInEx.Configuration.ConfigEntry<float> HockeyEyeHeight;
        internal static BepInEx.Configuration.ConfigEntry<float> HockeyEndGap;
        internal static BepInEx.Configuration.ConfigEntry<float> HockeySideOffset;
        internal static BepInEx.Configuration.ConfigEntry<string> HockeyPlayerEnd;
        internal static BepInEx.Configuration.ConfigEntry<bool> ChessSeatedView;
        internal static BepInEx.Configuration.ConfigEntry<float> ChessEyeHeight;
        internal static BepInEx.Configuration.ConfigEntry<float> ChessLean;
        internal static BepInEx.Configuration.ConfigEntry<bool> ChessLightOdd;
        internal static BepInEx.Configuration.ConfigEntry<bool> PositionTracking;
        internal static BepInEx.Configuration.ConfigEntry<float> HeadHeightOffset;
        internal static BepInEx.Configuration.ConfigEntry<float> ToolHeightOffset;
        internal static BepInEx.Configuration.ConfigEntry<float> BrushPullIn, BrushLower, BrushOffsetFade, BrushCloser, HammerOffsetX, HammerOffsetY, HammerOffsetZ;
        internal static BepInEx.Configuration.ConfigEntry<float> UiWidth;
        internal static BepInEx.Configuration.ConfigEntry<float> UiHeightOffset;
        internal static BepInEx.Configuration.ConfigEntry<float> ManualWidth;
        internal static BepInEx.Configuration.ConfigEntry<bool> ManualFaceOn;
        internal static BepInEx.Configuration.ConfigEntry<float> MainMenuDistance;
        internal static BepInEx.Configuration.ConfigEntry<float> ManualMargin;
        internal static BepInEx.Configuration.ConfigEntry<float> CompassScale, CompassRaise, ToolbarScale, ToolbarLower;
        internal static BepInEx.Configuration.ConfigEntry<bool> HideHud, ThumbRestDpad, FullViewLoading, BuildMenuLaser;
        internal static BepInEx.Configuration.ConfigEntry<float> ThumbRestHold;
        internal static BepInEx.Configuration.ConfigEntry<float> MapWidth, TeleportScale, ToolsScreenWidth, LoadingLogoSize, IntroLower, StudioIntroLogoSize, StudioIntroSpeed;
        internal static BepInEx.Configuration.ConfigEntry<string> HudScreens;
        internal static BepInEx.Configuration.ConfigEntry<string> HandHud, HudMode;
        internal static BepInEx.Configuration.ConfigEntry<string> HandHudCanvases;
        internal static BepInEx.Configuration.ConfigEntry<float> HandHudWidth;
        internal static BepInEx.Configuration.ConfigEntry<float> HandHudHeight;
        internal static BepInEx.Configuration.ConfigEntry<bool> CoffeeCupInHand;
        internal static BepInEx.Configuration.ConfigEntry<float> CoffeeCupOffsetX, CoffeeCupOffsetY, CoffeeCupOffsetZ;
        internal static BepInEx.Configuration.ConfigEntry<float> UiSmoothing;
        internal static BepInEx.Configuration.ConfigEntry<bool> UiRenderTimePlacement;
        internal static BepInEx.Configuration.ConfigEntry<bool> MotionControlsEnabled;
        internal static BepInEx.Configuration.ConfigEntry<float> HandAimPitch;
        internal static BepInEx.Configuration.ConfigEntry<bool> HideReticleWhileSweeping;
        internal static BepInEx.Configuration.ConfigEntry<bool> HideReticle;
        internal static BepInEx.Configuration.ConfigEntry<float> LaserBrightness;
        internal static BepInEx.Configuration.ConfigEntry<string> LaserColour;
        internal static BepInEx.Configuration.ConfigEntry<bool> DisableVignette;
        internal static BepInEx.Configuration.ConfigEntry<string> UiHideElements;
        internal static BepInEx.Configuration.ConfigEntry<bool> UiLogElements;
        internal static BepInEx.Configuration.ConfigEntry<bool> LaserPointer;
        internal static BepInEx.Configuration.ConfigEntry<float> LaserLength;
        internal static BepInEx.Configuration.ConfigEntry<bool> MotionHandAim;
        internal static BepInEx.Configuration.ConfigEntry<bool> MotionInput;
        internal static BepInEx.Configuration.ConfigEntry<bool> HandInteraction;
        internal static BepInEx.Configuration.ConfigEntry<bool> DisableMotionBlur;
        internal static BepInEx.Configuration.ConfigEntry<bool> HideBrokenPink;

        public override void Load()
        {
            L = Log;
            HockeyKeepView = Config.Bind("Air Hockey VR", "Keep Normal View", true, "Use an upright VR viewpoint at the local player's air hockey goal. Keeps vanilla thumbstick paddle and puck collision logic.");
            HockeyEyeHeight = Config.Bind("Air Hockey VR", "Eye Height Above Table Base", 1.85f, "Eye anchor metres above the Air hockey parent origin (before the existing global headset height offset). Try 1.65 to 2.05 if needed.");
            HockeyEndGap = Config.Bind("Air Hockey VR", "Distance Behind End", 0.35f, "Distance in metres behind the player's end of the table; higher moves farther away.");
            HockeySideOffset = Config.Bind("Air Hockey VR", "Side Offset", 0f, "Offset sideways across the table in metres, relative to the goal you play from.");
            HockeyPlayerEnd = Config.Bind("Air Hockey VR", "Player End", "Auto", "Auto picks the goal closest to the local game's camera when hockey starts. For opposite-side multiplayer or incorrect automatic selection, set Left or Right (the names are game object labels, not colors). Each player's PC can use a different setting.");
            ChessSeatedView = Config.Bind("Chess VR", "Seated View", true, "Sit in the chair at a chess table instead of the game's high top-down camera. Off: leave the game's chess camera alone.");
            ChessEyeHeight = Config.Bind("Chess VR", "Eye Height Above Seat", 0.75f, "Eye height above the seat, in metres (before the existing global headset height offset). Try 0.6 to 0.9.");
            ChessLightOdd = Config.Bind("Chess VR", "Flip Move Dot Colours", false, "Move dots are drawn black on the light squares so you can see them. If they come out black on the dark squares instead, set this to true.");
            ChessLean = Config.Bind("Chess VR", "Lean Toward Table", 0.1f, "Lean this many metres toward the table from the seat. 0 = exactly above the seat.");
            RadioEnabled = Config.Bind("Radio", "Enabled", true, "Internet radio on your left wrist. Show / hide the panel with both grips + Y; point the laser at its buttons and pull the right trigger.");
            RadioShown = Config.Bind("Radio", "Panel Shown", false, "Whether the radio panel is showing (both grips + Y toggles it).");
            RadioStations = Config.Bind("Radio", "Stations", "SomaFM Space Station|https://ice1.somafm.com/spacestation-128-mp3; SomaFM Deep Space One|https://ice1.somafm.com/deepspaceone-128-mp3; SomaFM Groove Salad|https://ice1.somafm.com/groovesalad-128-mp3; SomaFM Secret Agent|https://ice1.somafm.com/secretagent-128-mp3; SomaFM DEF CON Radio|https://ice1.somafm.com/defcon-128-mp3; Radio Paradise|https://stream.radioparadise.com/mp3-128", "Radio stations as Name|stream URL, separated by semicolons. Most MP3 / AAC internet radio streams work (the direct stream address, not a web page).");
            RadioStation = Config.Bind("Radio", "Station", 0, "Which station in the list was last chosen (0 = first).");
            RadioVolume = Config.Bind("Radio", "Volume", 0.5f, "Radio volume, 0 to 1 (separate from the game's own volume).");
            RadioHeight = Config.Bind("Radio", "Height Above Hand", 0.2f, "How far above your left hand the radio panel floats, in metres.");
            RadioWidth = Config.Bind("Radio", "Width", 0.3f, "Width of the radio panel, in metres.");
            CheckForUpdates = Config.Bind("Updates", "Check For Updates", true, "At start-up, check GitHub for a newer Moon Sweepers VR release and show a notice above the main menu if there is one. Nothing is downloaded or installed.");
            PerfLog = Config.Bind("Diagnostics", "Performance Log", false, "Log how long the mod's own work takes, any slow pieces, and frames under 20 fps (to find stutters). Only writes to the log.");
            MinigameDiagnostics = Config.Bind("Diagnostics", "Table Camera Probe", false, "Log camera poses and nearby named chess/air-hockey objects about once per second. Disable after testing.");
            HeadTrackingEnabled = Config.Bind("Head Tracking", "Enabled", true, "Apply headset rotation/position to the game camera.");
            PositionTracking = Config.Bind("Head Tracking", "Position Tracking", true, "Also apply head position (lean). Turn off for rotation only.");
            HeadHeightOffset = Config.Bind("Head Tracking", "Height Offset", -0.2f, "Raises (+) or lowers (-) your view, in metres. Adjust in-game with LB+RB + D-pad up/down, or F11/F10.");
            ToolHeightOffset = Config.Bind("Head Tracking", "Tool Height Offset", -0.25f, "Moves your held tool up (+) or down (-) without moving your view, in metres. Adjust in-game with LB+RB + D-pad right/left.");
            BrushPullIn = Config.Bind("Motion Controls", "Brush Pull In", 0.5f, "Motion controls: how far a held brush is pulled in toward your hand when you're not sweeping (0 = as before, 1 = right in your hand). The brush on the floor while sweeping is not affected.");
            BrushLower = Config.Bind("Motion Controls", "Brush Lower", 0.15f, "Motion controls: how far a held brush is lowered when you're not sweeping, in metres. The brush on the floor while sweeping is not affected.");
            BrushCloser = Config.Bind("Motion Controls", "Brush Closer To Body", 0.5f, "Motion controls: how far a held brush is moved back toward your body when you're not sweeping, in metres. The brush on the floor while sweeping is not affected.");
            HammerOffsetX = Config.Bind("Motion Controls", "Hammer Offset X", 0f, "Motion controls: the hammer sits in your right hand; nudge it right (+) / left (-), in metres, relative to the controller.");
            HammerOffsetY = Config.Bind("Motion Controls", "Hammer Offset Y", 0f, "Motion controls: nudge the hammer up (+) / down (-) relative to the controller, in metres.");
            HammerOffsetZ = Config.Bind("Motion Controls", "Hammer Offset Z", 0f, "Motion controls: nudge the hammer forward (+) / back (-) along the controller, in metres.");
            BrushOffsetFade = Config.Bind("Motion Controls", "Brush Offset Fade", 0.15f, "Seconds the brush takes to move between its held position and the sweeping position when you press or release the trigger.");
            UiDistance = Config.Bind("VR UI", "Distance", 1.5f, "How far in front of you the menu/HUD panel floats, in metres.");
            UiWidth = Config.Bind("VR UI", "Width", 2.4f, "Width of the menu/HUD panel, in metres.");
            UiHeightOffset = Config.Bind("VR UI", "Height Offset", 0f, "Moves the panel up (+) or down (-), in metres.");
            ManualWidth = Config.Bind("VR UI", "Field Manual Width", 1.6f, "Width of the field manual panel, in metres.");
            if (Mathf.Approximately(ManualWidth.Value, 1.3f) || Mathf.Approximately(ManualWidth.Value, 2.2f)) ManualWidth.Value = 1.6f;   // old defaults
            ManualFaceOn = Config.Bind("VR UI", "Field Manual Face On", true, "Show the field manual binder face-on (straight at you) instead of lying on a desk.");
            MainMenuDistance = Config.Bind("VR UI", "Main Menu Distance", 3.5f, "How far away the main menu floats, in metres.");
            if (MainMenuDistance.Value < 3f) MainMenuDistance.Value = 3.5f;   // older, too-close defaults
            ManualMargin = Config.Bind("VR UI", "Field Manual Zoom Out", 0.85f, "How much space to leave around the field manual binder (1 = edge to edge, higher = smaller binder).");
            if (Mathf.Approximately(ManualMargin.Value, 1.15f) || Mathf.Approximately(ManualMargin.Value, 1.03f)) ManualMargin.Value = 0.85f;   // old defaults
            CompassScale = Config.Bind("VR UI", "Compass Size", 0.8f, "Size of the compass / scanner at the top (1 = normal).");
            CompassRaise = Config.Bind("VR UI", "Compass Raise", -0.2f, "Moves the compass up (+) or down (-), in metres. 0 = the game's normal spot at the top of your view.");
            ToolbarScale = Config.Bind("VR UI", "Tool Belt Size", 0.7f, "Size of the tool belt at the bottom (1 = normal).");
            ToolbarLower = Config.Bind("VR UI", "Tool Belt Lower", 0.2f, "Moves the tool belt down (+) or up (-), in metres. 0 = the game's normal spot at the bottom of your view.");
            var hudLayoutVer = Config.Bind("VR UI", "HUD Layout Version", 0, "Internal: used to update old HUD position defaults. Leave alone.");
            if (hudLayoutVer.Value < 2)
            {
                CompassRaise.Value = -0.2f; ToolbarLower.Value = 0.2f;   // both 20 cm lower than the game's spot
                hudLayoutVer.Value = 2;
            }
            MapWidth = Config.Bind("VR UI", "Map Width", 2.0f, "Width of the moon map panel, in metres.");
            if (Mathf.Approximately(MapWidth.Value, 1.4f) || Mathf.Approximately(MapWidth.Value, 1.7f)) MapWidth.Value = 2.0f;   // old defaults
            FullViewLoading = Config.Bind("VR UI", "Full View Loading Screens", true, "Loading, teleport and studio intro screens fill your whole view instead of floating in a window.");
            LoadingLogoSize = Config.Bind("VR UI", "Loading Logo Size", 0.25f, "Size of the logo and text on full-view loading screens (1 = stretched with the background).");
            IntroLower = Config.Bind("VR UI", "Studio Intro Lower", 0.35f, "Moves the studio intro (developer's name) down (+) or up (-) in your view, in metres.");
            StudioIntroLogoSize = Config.Bind("VR UI", "Studio Intro Logo Size", 0.25f, "Size of the logo video on the studio intro screen (1 = stretched with the background).");
            StudioIntroSpeed = Config.Bind("VR UI", "Studio Intro Speed", 0.6f, "Playback speed of the studio intro video (1 = normal). Lower makes the intro last longer.");
            BuildMenuLaser = Config.Bind("Motion Controls", "Build Menu Laser", true, "Choose build menu cards with the laser + right trigger. Off: use the left stick to move and the right trigger (or A) to choose.");
            var buildVer = Config.Bind("Motion Controls", "Build Menu Version", 0, "Internal: used to update old build menu defaults. Leave alone.");
            if (buildVer.Value < 1) { BuildMenuLaser.Value = true; buildVer.Value = 1; }
            ToolsScreenWidth = Config.Bind("VR UI", "Tools Screen Width", 1.9f, "Width of the supply wall / tools screen and the upgrade matrix, in metres.");
            GarageMenuDistance = Config.Bind("VR UI", "Garage Menu Distance", 0.9f, "How far in front of you the garage menu appears, in metres (close, so the garage walls don't hide it).");
            BuildMenuWidth = Config.Bind("VR UI", "Build Menu Width", 2.6f, "Width of the build menu, in metres (it sits as far back as the upgrade matrix).");
            TeleportScale = Config.Bind("VR UI", "Teleport Panel Size", 0f, "0 = the map's teleport list stays inside the map (normal). Above 0: shown as its own panel to the right of the map, this many times its normal size.");
            if (Mathf.Approximately(TeleportScale.Value, 1.6f)) TeleportScale.Value = 0f;   // old default
            ThumbRestDpad = Config.Bind("Motion Controls", "Thumb Rest D-pad", true, "While your right thumb rests on the right controller's thumb rest, the left stick works as the D-pad.");
            ThumbRestHold = Config.Bind("Motion Controls", "Thumb Rest Hold Seconds", 2f, "How long your thumb has to rest on the thumb rest before the left stick becomes the D-pad, in seconds.");
            var restVer = Config.Bind("Motion Controls", "Thumb Rest Version", 0, "Internal: used to update old thumb rest defaults. Leave alone.");
            if (restVer.Value < 3) { ThumbRestHold.Value = 2f; restVer.Value = 3; }
            HideHud = Config.Bind("VR UI", "Hide HUD", false, "Old setting, replaced by HUD Mode (kept so older configs carry over).");
            HudMode = Config.Bind("VR UI", "HUD Mode", HideHud.Value ? "None" : "Full", new BepInEx.Configuration.ConfigDescription("Which HUD you see: Full (wrist HUD, tool belt and compass), Hand Only (just the wrist HUD) or None. Step through them in game with both grips + click the left stick (gamepad: LB + RB + click the left stick).", new BepInEx.Configuration.AcceptableValueList<string>("Full", "Hand Only", "None")));
            HudScreens = Config.Bind("VR UI", "HUD Screens", "Region scanner HUD, Equipment HUD - five slots, Save indicator HUD, Money popups", "Which of the game's screens count as the HUD for Hide HUD.");
            HandHud = Config.Bind("Hand HUD", "Hand", "Left", "Show the in-game HUD (tool slots, money etc.) on a hand instead of in front of you: Left, Right or Off.");
            HandHudCanvases = Config.Bind("Hand HUD", "Screens", "Save indicator HUD, Money popups", "Comma-separated names of the game's HUD screens to put on the hand. The first one also receives the dust panel, money and notices from the main HUD (the tool belt and compass stay in front of you).");
            if (HandHudCanvases.Value == "Equipment HUD - five slots, Region scanner HUD, Save indicator HUD, Money popups") HandHudCanvases.Value = "Save indicator HUD, Money popups";   // old default
            HandHudWidth = Config.Bind("Hand HUD", "Width", 0.4f, "Width of the hand HUD panel, in metres.");
            HandHudHeight = Config.Bind("Hand HUD", "Height Above Hand", 0.05f, "How far above the controller the hand HUD floats, in metres.");
            if (Mathf.Approximately(HandHudHeight.Value, 0.2f)) HandHudHeight.Value = 0.12f;   // old default
            var handHudVer = Config.Bind("Hand HUD", "Hand HUD Version", 0, "Internal: used to update old hand HUD defaults. Leave alone.");
            if (handHudVer.Value < 1)
            {
                HandHudHeight.Value = 0.05f; HandHudWidth.Value = 0.4f;   // lower and closer to the hand
                handHudVer.Value = 1;
            }
            UiSmoothing = Config.Bind("VR UI", "Smoothing", 15f, "How tightly the panel follows you. Higher = snappier, lower = smoother. 0 = locked to the camera.");
            UiRenderTimePlacement = Config.Bind("VR UI", "Render Time Placement", true, "Re-position the panel just before drawing to remove stutter. Turn off if menus disappear.");
            MotionControlsEnabled = Config.Bind("Motion Controls", "Enabled", true, "EXPERIMENTAL: sweep where the right controller points, and use Quest sticks/triggers.");
            MotionHandAim = Config.Bind("Motion Controls", "Hand Aim", true, "Aim/clean from the right controller.");
            MotionInput = Config.Bind("Motion Controls", "Controller Input", true, "Feed Quest sticks/triggers into the game.");
            HandInteraction = Config.Bind("Motion Controls", "Hand Interaction", true, "Point your right hand at things to highlight / pick up / use them (instead of looking at them).");
            CoffeeCupInHand = Config.Bind("Motion Controls", "Coffee Cup In Hand", true, "When you pick up the coffee cup, hold the mug in your right hand instead of having it float away.");
            CoffeeCupOffsetX = Config.Bind("Motion Controls", "Coffee Cup Offset X", 0f, "Sideways offset of the held coffee cup from the controller, in metres. 0 = in the middle of your hand.");
            CoffeeCupOffsetY = Config.Bind("Motion Controls", "Coffee Cup Offset Y", -0.03f, "Up (+) or down (-) offset of the held coffee cup from the controller, in metres.");
            CoffeeCupOffsetZ = Config.Bind("Motion Controls", "Coffee Cup Offset Z", 0.08f, "Forward offset of the held coffee cup from the controller, in metres.");
            DisableMotionBlur = Config.Bind("Comfort", "Disable Motion Blur", true, "Turn the game's motion blur off while in VR (it is uncomfortable in a headset).");
            HandAimPitch = Config.Bind("Motion Controls", "Hand Aim Pitch", 35f, "How far below the controller's pointing direction the sweep ray aims, in degrees.");
            HideReticleWhileSweeping = Config.Bind("Motion Controls", "Hide Reticle While Sweeping", true, "Hide the brush aiming reticle while the sweep trigger is held (cleaning still works).");
            LaserPointer = Config.Bind("Motion Controls", "Laser Pointer", true, "Show a laser from the right hand when you're not sweeping, to make pointing at buttons and items easier.");
            LaserLength = Config.Bind("Motion Controls", "Laser Length", 5f, "Maximum laser length in metres (it stops at whatever it hits).");
            HideReticle = Config.Bind("Motion Controls", "Hide Reticle", true, "Never show the brush aiming reticle (cleaning still works).");
            LaserBrightness = Config.Bind("Motion Controls", "Laser Brightness", 0.03f, "Laser brightness, 0 (black) to 1 (full).");
            if (Mathf.Approximately(LaserBrightness.Value, 0.08f)) LaserBrightness.Value = 0.03f;   // old default
            LaserColour = Config.Bind("Motion Controls", "Laser Colour", "White", "Laser colour: " + string.Join(", ", Laser.ColourNames) + ". Change in game with both grips + right stick left/right.");
            DisableVignette = Config.Bind("Comfort", "Disable Vignette", true, "Remove the dark shaded edges the game draws around the screen.");
            HideBrokenPink = Config.Bind("Comfort", "Hide Broken Pink Objects", true, "Hide objects whose material/shader failed in VR (they show up as bright pink boxes, e.g. at the character's feet on the main menu).");
            UiHideElements = Config.Bind("VR UI", "Hide Elements", "HUD shade", "Comma-separated names of UI pieces to hide in VR (e.g. screen borders). The log lists candidates.");
            if (string.IsNullOrWhiteSpace(UiHideElements.Value)) UiHideElements.Value = "HUD shade";   // the dark band around the HUD
            UiLogElements = Config.Bind("VR UI", "Log Large Elements", true, "Write full-screen UI pieces and edge strips to the log, to find borders to hide.");
            AddComponent<XrStarterBehaviour>();
            AddComponent<UiToWorld>();
            AddComponent<HeadTracking>();
            L.LogInfo($"Moon XR Starter {Version} loaded from {typeof(Plugin).Assembly.Location}; will start XR shortly after boot");
            UpdateCheck.Start();
        }
    }

    public class XrStarterBehaviour : MonoBehaviour
    {
        public XrStarterBehaviour(IntPtr ptr) : base(ptr) { }

        private int _frames;
        private int _attempts;
        private bool _done;

        private void Update()
        {
            if (_done) return;
            _frames++;
            // Try once after the game has booted, and once more later if the first try failed.
            if (_frames == 90 || _frames == 900)
            {
                _attempts++;
                bool ok = XrStarter.TryStart();
                if (ok || _attempts >= 2) _done = true;
            }
        }
    }

    internal static class XrStarter
    {
        private static ManualLogSource L => Plugin.L;
        internal static bool Started;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct UserDefinedSettings
        {
            public ushort stereoRenderingMode;   // 0 = MultiPass, 1 = SinglePassInstanced
            public ushort initializationType;    // 1 = Scene, 2 = Overlay
            public ushort mirrorViewMode;        // 0 = None, 1 = Left, 2 = Right, 3 = OpenVR
            [MarshalAs(UnmanagedType.LPStr)] public string editorAppKey;
            [MarshalAs(UnmanagedType.LPStr)] public string actionManifestPath;
            [MarshalAs(UnmanagedType.LPStr)] public string applicationName;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate void SetUserDefinedSettingsFn(UserDefinedSettings settings);

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        public static bool TryStart()
        {
            try
            {
                ApplyOpenVrSettings();
                bool display = StartSubsystem("UnityEngine.XR.XRDisplaySubsystemDescriptor");
                bool input = StartSubsystem("UnityEngine.XR.XRInputSubsystemDescriptor");
                L.LogMessage($"XR display started: {display}, XR input started: {input}");
                Started = display;
                return display;
            }
            catch (Exception e)
            {
                L.LogError("XR start failed: " + e);
                return false;
            }
        }

        private static void ApplyOpenVrSettings()
        {
            try
            {
                string dataDir = Path.Combine(Paths.GameRootPath, "MoonSweepers_Data");
                IntPtr handle;
                if (!NativeLibrary.TryLoad("XRSDKOpenVR", out handle))
                    handle = NativeLibrary.Load(Path.Combine(dataDir, "Plugins", "XRSDKOpenVR.dll"));
                var fn = Marshal.GetDelegateForFunctionPointer<SetUserDefinedSettingsFn>(
                    NativeLibrary.GetExport(handle, "SetUserDefinedSettings"));
                var s = new UserDefinedSettings
                {
                    stereoRenderingMode = 0,
                    initializationType = 1,
                    mirrorViewMode = 1,
                    editorAppKey = "application.generated.unity.moonsweepers.exe",
                    actionManifestPath = Path.Combine(dataDir, "StreamingAssets", "SteamVR", "actions.json"),
                    applicationName = "Moon Sweepers",
                };
                fn(s);
                L.LogInfo("OpenVR settings applied (multi-pass, scene app)");
            }
            catch (Exception e)
            {
                L.LogWarning("Could not apply OpenVR settings (continuing anyway): " + e.Message);
            }
        }

        private static bool StartSubsystem(string descriptorTypeName)
        {
            var descType = FindType(descriptorTypeName);
            var mgrType = FindType("UnityEngine.SubsystemManager");
            var listOpen = FindType("Il2CppSystem.Collections.Generic.List`1");
            if (descType == null || mgrType == null || listOpen == null)
            {
                L.LogError($"Missing types: desc={descType != null} mgr={mgrType != null} list={listOpen != null}");
                return false;
            }

            var list = Activator.CreateInstance(listOpen.MakeGenericType(descType));
            var getDescs = mgrType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "GetSubsystemDescriptors" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
            if (getDescs == null)
            {
                L.LogError("SubsystemManager.GetSubsystemDescriptors<T> not found");
                return false;
            }
            getDescs.MakeGenericMethod(descType).Invoke(null, new[] { list });

            var listType = list.GetType();
            int count = (int)listType.GetProperty("Count").GetValue(list);
            L.LogInfo($"{descType.Name}: {count} descriptor(s)");
            var itemProp = listType.GetProperty("Item");

            object chosen = null;
            string chosenId = null;
            for (int i = 0; i < count; i++)
            {
                var d = itemProp.GetValue(list, new object[] { i });
                string id = null;
                try { id = descType.GetProperty("id")?.GetValue(d) as string; } catch { }
                id ??= d?.ToString();
                L.LogInfo("  - " + id);
                bool isOpenVr = id != null && id.IndexOf("OpenVR", StringComparison.OrdinalIgnoreCase) >= 0;
                if (chosen == null || isOpenVr)
                {
                    chosen = d;
                    chosenId = id;
                    if (isOpenVr) break;
                }
            }
            if (chosen == null) return false;

            var create = descType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 0);
            if (create == null)
            {
                L.LogError(descType.Name + ".Create() not found");
                return false;
            }
            var subsystem = create.Invoke(chosen, null);
            if (subsystem == null)
            {
                L.LogError("Create() returned null for " + chosenId);
                return false;
            }
            subsystem.GetType().GetMethod("Start", Type.EmptyTypes)?.Invoke(subsystem, null);

            bool running = false;
            try { running = (bool)subsystem.GetType().GetProperty("running").GetValue(subsystem); } catch { }
            L.LogMessage($"Started {chosenId}: running={running}");
            return running;
        }
    }
}
