// Applies the headset's rotation/position to the VR camera just before it renders, then
// restores the game's own camera pose afterwards, so the game keeps controlling where you
// "are" and your head controls where you look. UUVR's own tracking fails on Unity 6.
using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace MoonXrStarter
{
    public class HeadTracking : MonoBehaviour
    {
        public HeadTracking(IntPtr ptr) : base(ptr) { }

        private struct Saved
        {
            public IntPtr TransformPtr;
            public Transform T;
            public Vector3 Pos;
            public Quaternion Rot;
        }

        private static readonly List<Saved> SavedPoses = new List<Saved>();

        private struct SavedChild { public Transform T; public Vector3 LocalPos; public Quaternion LocalRot; public bool Rot; public Vector3 LocalScale; public bool Scale; }
        private static readonly List<SavedChild> SavedChildren = new List<SavedChild>();
        private static bool _loggedHierarchy;
        private static int _lastUiFrame = -1;
        private static bool _loggedUiCam;

        private static void LogHierarchy(Transform t, int depth, int maxDepth)
        {
            try
            {
                string comps = "";
                if (t.GetComponent<Camera>() != null) comps += " [Camera]";
                if (t.GetComponent<Renderer>() != null) comps += " [Renderer]";
                Plugin.L.LogInfo(new string(' ', depth * 2) + "- " + t.name + comps + (t.gameObject.activeInHierarchy ? "" : " (inactive)"));
                if (depth >= maxDepth) return;
                for (int i = 0; i < t.childCount; i++) LogHierarchy(t.GetChild(i), depth + 1, maxDepth);
            }
            catch { }
        }

        // The game camera is the first ancestor of the VR camera that UUVR didn't create.
        private static Transform GameCameraRoot(Transform vrCam)
        {
            var t = vrCam;
            while (t.parent != null && t.name.StartsWith("Vr", StringComparison.Ordinal)) t = t.parent;
            return t;
        }

        private static bool IsAncestorOf(Transform a, Transform b)
        {
            for (var t = b; t != null; t = t.parent) if (t.Pointer == a.Pointer) return true;
            return false;
        }

        // Lower the game camera's attached items (held tool etc.) without moving the view itself.
        private static void OffsetTools(Transform vrCam)
        {
            var root = GameCameraRoot(vrCam);
            // Motion controls: draw the held tool as if it hung off the right hand instead of the face.
            if (MotionControls.HandFrame == Time.frameCount && !TableSessionActive)
            {
                var hp = MotionControls.HandPos; var hr = MotionControls.HandRot;
                var sc = root.lossyScale;
                // Brushes only, and only while not sweeping: pull them in toward the hand and lower
                // them. The weight fades to 0 as soon as the trigger is pressed, so the brush on the
                // floor while sweeping is exactly where it always was.
                bool sweeping = MotionControls.SweepHeld();
                float fade = Mathf.Max(0.01f, Plugin.BrushOffsetFade.Value);
                if (_brushWFrame != Time.frameCount)
                {
                    _brushWFrame = Time.frameCount;
                    _brushW = Mathf.MoveTowards(_brushW, sweeping ? 0f : 1f, Time.unscaledDeltaTime / fade);
                }
                for (int i = 0; i < root.childCount; i++)
                {
                    var c = root.GetChild(i);
                    if (IsAncestorOf(c, vrCam)) continue;
                    try { if (c.GetComponent<Camera>() != null) continue; } catch { }
                    var lp = c.localPosition; var lr = c.localRotation;
                    SavedChildren.Add(new SavedChild { T = c, LocalPos = lp, LocalRot = lr, Rot = true });
                    var wpos = hp + hr * Vector3.Scale(lp, sc);
                    Transform hammer = IsBrushHolder(c) ? ActiveHammer(c) : null;
                    if (hammer != null)
                    {
                        // Hammer: put its own origin right in the hand (where the laser starts), keeping
                        // the rotation exactly as the game gives it. An optional hand-space nudge on top.
                        var rot = hr * lr;
                        var hs = c.lossyScale; // holder scale applies to the hammer's local offset
                        var hl = hammer.localPosition;
                        var nudge = new Vector3(Plugin.HammerOffsetX.Value, Plugin.HammerOffsetY.Value, Plugin.HammerOffsetZ.Value);
                        wpos = hp + hr * nudge - rot * Vector3.Scale(hl, hs);
                    }
                    else if (_brushW > 0.0001f && IsBrushHolder(c))
                    {
                        wpos = Vector3.Lerp(wpos, hp, Mathf.Clamp01(Plugin.BrushPullIn.Value) * _brushW);
                        var up = WorldUp(hp);
                        wpos -= up * (Plugin.BrushLower.Value * _brushW);
                        // Back toward your body, along where your head faces (flattened to the ground).
                        var fwd = Vector3.ProjectOnPlane(vrCam.forward, up);
                        if (fwd.sqrMagnitude > 1e-6f) wpos -= fwd.normalized * (Plugin.BrushCloser.Value * _brushW);
                    }
                    c.SetPositionAndRotation(wpos, hr * lr);
                }
                return;
            }
            float off = Plugin.ToolHeightOffset.Value;
            if (Mathf.Abs(off) < 0.0001f) return;
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (IsAncestorOf(c, vrCam)) continue; // UUVR's camera rig
                try { if (c.GetComponent<Camera>() != null) continue; } catch { }
                SavedChildren.Add(new SavedChild { T = c, LocalPos = c.localPosition });
                var ls = root.lossyScale;
                var dirLocal = root.InverseTransformDirection(WorldUp(root.position) * off);
                c.localPosition += new Vector3(dirLocal.x / Mathf.Max(ls.x, 1e-4f), dirLocal.y / Mathf.Max(ls.y, 1e-4f), dirLocal.z / Mathf.Max(ls.z, 1e-4f));
            }
        }

        private static float _brushW = 1f; private static int _brushWFrame = -1;
        private static Transform ActiveHammer(Transform holder)
        {
            try
            {
                for (int i = 0; i < holder.childCount; i++)
                {
                    var t = holder.GetChild(i);
                    if (t == null || !t.gameObject.activeSelf) continue;
                    var n = t.name;
                    if (n != null && n.StartsWith("building_tool")) return t;
                }
            }
            catch { }
            return null;
        }
        private static bool IsBrushHolder(Transform c)
        {
            try { var n = c.name; return n != null && n.StartsWith("Equipped cleaning tool"); } catch { return false; }
        }

        private static void RestoreTools()
        {
            for (int i = SavedChildren.Count - 1; i >= 0; i--)
            {
                try { var sv = SavedChildren[i]; if (sv.T != null) { sv.T.localPosition = sv.LocalPos; if (sv.Rot) sv.T.localRotation = sv.LocalRot; if (sv.Scale) sv.T.localScale = sv.LocalScale; } } catch { }
            }
            SavedChildren.Clear();
        }

        // Hide the game camera's held items (brush etc.) for this frame by zeroing their scale;
        // RestoreTools puts the scale back after rendering. Called after OffsetTools so the two
        // don't fight over the same items.
        private static void HideHeldTools(Transform vrCam)
        {
            try
            {
                var root = GameCameraRoot(vrCam);
                for (int i = 0; i < root.childCount; i++)
                {
                    var c = root.GetChild(i);
                    if (c == null) continue;
                    if (IsAncestorOf(c, vrCam)) continue; // UUVR's camera rig
                    try { if (c.GetComponent<Camera>() != null) continue; } catch { }
                    var ls = c.localScale;
                    SavedChildren.Add(new SavedChild { T = c, LocalScale = ls, Scale = true });
                    c.localScale = Vector3.zero;
                }
            }
            catch { }
        }
        private static bool _calibrated;
        private static float _calibYaw;
        private static Vector3 _calibPos;
        private static bool _loggedError;
        private static bool _loggedFirst;

        // Keep delegates alive so the GC doesn't collect them.
        private static Delegate _beginManaged, _endManaged;
        private static object _beginIl2Cpp, _endIl2Cpp;

        private bool _registered;
        private bool _fallback;
        private float _nextRegisterTry;

        private bool _recenterHeld, _heightUpHeld, _heightDownHeld;

        private void HandleControls()
        {
            try
            {
                bool recenter = false, up = false, down = false, toolUp = false, toolDown = false, laserToggle = false, laserColour = false, hudToggle = false;
                var pad = UnityEngine.InputSystem.Gamepad.current;
                if (pad != null)
                {
                    // Click both thumbsticks together = recenter.
                    recenter = pad.leftStickButton.isPressed && pad.rightStickButton.isPressed;
                    // Hold LB + RB and press D-pad up/down = raise/lower your view.
                    bool bumpers = pad.leftShoulder.isPressed && pad.rightShoulder.isPressed;
                    // Hold LB + RB and press A = recenter (same on the Quest: both grips + A).
                    recenter |= bumpers && pad.buttonSouth.isPressed;
                    up = bumpers && pad.dpad.up.isPressed;
                    down = bumpers && pad.dpad.down.isPressed;
                    toolUp = bumpers && pad.dpad.right.isPressed;
                    toolDown = bumpers && pad.dpad.left.isPressed;
                    // Hold LB + RB: X = laser on/off, Y = next laser colour.
                    laserToggle = bumpers && pad.buttonWest.isPressed;
                    laserColour = bumpers && pad.buttonNorth.isPressed;
                    hudToggle = bumpers && pad.leftStickButton.isPressed && !pad.rightStickButton.isPressed;
                }
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb != null)
                {
                    recenter |= kb.f9Key.isPressed;
                    up |= kb.f11Key.isPressed;
                    down |= kb.f10Key.isPressed;
                }
                if (recenter && !_recenterHeld) { Recenter(); Plugin.L.LogInfo("Head tracking: recentered"); }
                if (up && !_heightUpHeld) ChangeHeight(+0.05f);
                if (down && !_heightDownHeld) ChangeHeight(-0.05f);
                if (toolUp && !_toolUpHeld) ChangeToolHeight(+0.05f);
                if (toolDown && !_toolDownHeld) ChangeToolHeight(-0.05f);
                _recenterHeld = recenter; _heightUpHeld = up; _heightDownHeld = down;
                _toolUpHeld = toolUp; _toolDownHeld = toolDown;
                if (laserToggle && !_laserToggleHeld) Laser.Toggle();
                if (laserColour && !_laserColourHeld) Laser.CycleColour(1);
                _laserToggleHeld = laserToggle; _laserColourHeld = laserColour;
                if (hudToggle && !_hudToggleHeld) UiToWorld.ToggleHud();
                _hudToggleHeld = hudToggle;
            }
            catch (Exception e)
            {
                if (!_loggedControlsError) { _loggedControlsError = true; Plugin.L.LogWarning("Head tracking controls error: " + e.Message); }
            }
        }

        private bool _loggedControlsError;
        private bool _toolUpHeld, _toolDownHeld, _laserToggleHeld, _laserColourHeld, _hudToggleHeld;

        internal static void ChangeToolHeight(float delta)
        {
            Plugin.ToolHeightOffset.Value = Mathf.Round((Plugin.ToolHeightOffset.Value + delta) * 100f) / 100f;
            Plugin.L.LogInfo($"Tool height offset now {Plugin.ToolHeightOffset.Value:F2} m");
        }

        internal static void ChangeHeight(float delta)
        {
            Plugin.HeadHeightOffset.Value = Mathf.Round((Plugin.HeadHeightOffset.Value + delta) * 100f) / 100f;
            Plugin.L.LogInfo($"Head tracking: height offset now {Plugin.HeadHeightOffset.Value:F2} m");
        }

        private void Update()
        {
            Perf.Frame();
            if (!Plugin.HeadTrackingEnabled.Value) return;
            long t = Perf.Now(); HandleControls(); Perf.Add("Controls", t);
            t = Perf.Now(); MotionControls.Tick(); Perf.Add("Motion controls", t);
            Comfort.Tick();
            t = Perf.Now(); ScreenProbe.Tick(); Perf.Add("Screen probe", t);
            // Only hook rendering once the game has fully booted (hooking during startup can crash it).
            if (!_registered && !_fallback && Time.unscaledTime >= _nextRegisterTry
                && Time.frameCount > 300 && XrStarter.Started && Camera.main != null)
            {
                _nextRegisterTry = Time.unscaledTime + 2f;
                _registered = TryRegister();
                if (!_registered) { _fallback = true; Plugin.L.LogWarning("Head tracking: using LateUpdate fallback"); }
            }
            if (_fallback) RestoreAll();
        }

        private void LateUpdate()
        {
            if (!_fallback || !Plugin.HeadTrackingEnabled.Value) return;
            var cam = Camera.main;
            if (cam != null) Apply(cam);
        }

        private static Delegate _canvasManaged;
        private static object _canvasIl2Cpp;
        private static bool _loggedCanvasHook;

        // Runs just before Unity locks in UI positions for the frame (after all LateUpdates).
        private static void OnPreWillRenderCanvases()
        {
            long pt = Perf.Now(); Radio.Tick(); Perf.Add("Radio", pt);
            pt = Perf.Now(); VrMenu.Tick(); Perf.Add("VR menu", pt);
            pt = Perf.Now(); NameTags.Tick(); Perf.Add("Name tags", pt);
            pt = Perf.Now(); Laser.Tick(); Perf.Add("Laser", pt);   // after all game updates, so the beam lines up with this frame's aim
            pt = Perf.Now(); ManualView.Tick(); Perf.Add("Field manual view", pt);
            try
            {
                if (!Plugin.UiRenderTimePlacement.Value) return;
                var main = Camera.main;
                if (main == null) return;
                UiToWorld.Place(main);
                MapFix.Layout();
                if (!_loggedCanvasHook) { _loggedCanvasHook = true; Plugin.L.LogInfo("UI: pre-canvas placement active"); }
            }
            catch { }
        }

        private static void TryRegisterCanvasHook()
        {
            try
            {
                Action a = OnPreWillRenderCanvases;
                _canvasManaged = a;
                var delType = typeof(Canvas).GetNestedType("WillRenderCanvases");
                MethodInfo conv = null;
                foreach (var m in typeof(DelegateSupport).GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == "ConvertDelegate" && m.IsGenericMethodDefinition) { conv = m; break; }
                _canvasIl2Cpp = conv.MakeGenericMethod(delType).Invoke(null, new object[] { a });
                typeof(Canvas).GetMethod("add_preWillRenderCanvases", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { _canvasIl2Cpp });
                Plugin.L.LogMessage("UI: hooked canvas update");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("UI: could not hook canvas update: " + (e.InnerException?.Message ?? e.Message));
            }
        }

        private bool TryRegister()
        {
            try
            {
                Action<ScriptableRenderContext, Camera> begin = OnBegin;
                Action<ScriptableRenderContext, Camera> end = OnEnd;
                _beginManaged = begin;
                _endManaged = end;
                _beginIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(begin);
                _endIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(end);
                var t = typeof(RenderPipelineManager);
                t.GetMethod("add_beginCameraRendering", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { _beginIl2Cpp });
                t.GetMethod("add_endCameraRendering", BindingFlags.Public | BindingFlags.Static).Invoke(null, new[] { _endIl2Cpp });
                Plugin.L.LogMessage("Head tracking: hooked camera rendering");
                TryRegisterCanvasHook();
                MotionControls.Install();
                return true;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("Head tracking: could not hook rendering: " + (e.InnerException?.Message ?? e.Message));
                return false;
            }
        }

        // Convert a pose from headset tracking space into the game world (same mapping as the head).
        private static int _viewOverrideFrame = -100;
        internal static bool ViewOverrideActive => Time.frameCount - _viewOverrideFrame <= 2;
        private static Vector3 _viewOverridePos; private static Quaternion _viewOverrideRot;

        internal static bool TrackingToWorld(Vector3 p, Quaternion r, out Vector3 wp, out Quaternion wr)
        {
            wp = default; wr = Quaternion.identity;
            if (!_calibrated) return false;
            var main = Camera.main;
            if (main == null) return false;
            var bt = main.transform;
            var yawFix = Quaternion.Euler(0f, -_calibYaw, 0f);
            // While a table view (chess / air hockey) has moved your view, hands and the laser
            // follow that view, not the game's own camera.
            Vector3 bp = bt.position; Quaternion br = bt.rotation;
            if (Time.frameCount - _viewOverrideFrame <= 2) { bp = _viewOverridePos; br = _viewOverrideRot; }
            wp = bp + br * (yawFix * (p - _calibPos)) + WorldUp(bp) * Plugin.HeadHeightOffset.Value;
            wr = br * (yawFix * r);
            return true;
        }

        /// <summary>
        /// "Up" where you're standing. The Moon is a real sphere centred on the world origin, so on
        /// the far side "up" points the other way; near the origin (menus, interiors) it's plain up.
        /// </summary>
        internal static Vector3 WorldUp(Vector3 pos)
        {
            return pos.sqrMagnitude > 500f * 500f ? pos.normalized : Vector3.up;
        }

        /// <summary>Where your eyes are in the game world right now.</summary>
        internal static bool TryGetHeadWorld(out Vector3 wp)
        {
            wp = default;
            var rot = InputTracking.GetLocalRotation(XRNode.CenterEye);
            if (rot.x == 0f && rot.y == 0f && rot.z == 0f && rot.w == 0f) return false;
            var p = Plugin.PositionTracking.Value ? InputTracking.GetLocalPosition(XRNode.CenterEye) : _calibPos;
            return TrackingToWorld(p, rot, out wp, out _);
        }

        /// <summary>Your head's pose in the game world (position and where you're looking).</summary>
        internal static bool TryGetHeadPose(out Vector3 wp, out Quaternion wr)
        {
            wp = default; wr = Quaternion.identity;
            var rot = InputTracking.GetLocalRotation(XRNode.CenterEye);
            if (rot.x == 0f && rot.y == 0f && rot.z == 0f && rot.w == 0f) return false;
            var p = Plugin.PositionTracking.Value ? InputTracking.GetLocalPosition(XRNode.CenterEye) : _calibPos;
            return TrackingToWorld(p, rot, out wp, out wr);
        }

        private static bool GetHead(out Quaternion rot, out Vector3 pos)
        {
            rot = InputTracking.GetLocalRotation(XRNode.CenterEye);
            pos = InputTracking.GetLocalPosition(XRNode.CenterEye);
            if (rot.x == 0f && rot.y == 0f && rot.z == 0f && rot.w == 0f) return false;
            if (!_calibrated)
            {
                _calibYaw = rot.eulerAngles.y;
                _calibPos = pos;
                _calibrated = true;
                Plugin.L.LogInfo($"Head tracking: calibrated (yaw {_calibYaw:F0})");
            }
            var yawFix = Quaternion.Euler(0f, -_calibYaw, 0f);
            rot = yawFix * rot;
            pos = yawFix * (pos - _calibPos);
            if (!Plugin.PositionTracking.Value) pos = Vector3.zero;
            return true;
        }

        internal static void Recenter() => _calibrated = false;

        // Diagnostic-only logging. Never changes game transforms or player inputs.
        // Sparse snapshots avoid filling LogOutput with frame-by-frame spam.
        private static float _nextMiniGameProbe;
        private static float _nextObjectsProbe;
        private static void ProbeMinigame(Camera cam, Vector3 gamePos, Quaternion gameRot,
                                          Vector3 headLocalPos, Quaternion headLocalRot)
        {
            if (!Plugin.MinigameDiagnostics.Value) return;
            float now = Time.unscaledTime;
            if (now < _nextMiniGameProbe) return;
            _nextMiniGameProbe = now + 1f;
            try
            {
                Transform root = GameCameraRoot(cam.transform);
                Vector3 gameAngles = gameRot.eulerAngles;
                Vector3 headsetAngles = headLocalRot.eulerAngles;
                Plugin.L.LogInfo($"[TableProbe] camera={cam.name} root={root.name} " +
                    $"gamePos={gamePos.ToString("F3")} gameEuler={gameAngles.ToString("F1")} " +
                    $"headLocalPos={headLocalPos.ToString("F3")} headLocalEuler={headsetAngles.ToString("F1")} " +
                    $"renderEuler={(gameRot * headLocalRot).eulerAngles.ToString("F1")} " +
                    $"handValid={MotionControls.HandFrame == Time.frameCount} handPos={MotionControls.HandPos.ToString("F3")} " +
                    $"time={now:F1}");
                if (now >= _nextObjectsProbe)
                {
                    _nextObjectsProbe = now + 8f;
                    int matches = 0;
                    foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
                    {
                        if (t == null || t.gameObject == null || !t.gameObject.scene.IsValid()) continue;
                        string n = t.name ?? "";
                        if (n.IndexOf("hockey", StringComparison.OrdinalIgnoreCase) < 0 &&
                            n.IndexOf("chess", StringComparison.OrdinalIgnoreCase) < 0 &&
                            n.IndexOf("mallet", StringComparison.OrdinalIgnoreCase) < 0 &&
                            n.IndexOf("paddle", StringComparison.OrdinalIgnoreCase) < 0 &&
                            n.IndexOf("lounge", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        Plugin.L.LogInfo($"[TableProbe] object={n} parent={(t.parent != null ? t.parent.name : "none")} " +
                            $"pos={t.position.ToString("F3")} euler={t.rotation.eulerAngles.ToString("F1")} " +
                            $"active={t.gameObject.activeInHierarchy}");
                        if (++matches >= 35) break;
                    }
                    Plugin.L.LogInfo($"[TableProbe] object scan completed; matchesLogged={matches}");
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("[TableProbe] scan error: " + e.Message); }
        }

        // TEST 2: do not freeze the last camera pose during the interaction transition.
        // The original Test 1 captured an in-transition Y lower than the playing surface.
        // Instead use the real tabletop geometry as a stable render-only viewpoint.
        // Never modify the game's camera transform permanently, gameplay input, or mallet.
        private static Transform _hockeyAim;
        private static float _nextHockeySearch;


        private static bool _hockeyWasActive;
        private static Vector3 _hockeyAnchorPos;
        private static Quaternion _hockeyAnchorRot;
        private static bool _hockeyWarned;
        private static float _hockeyLostSince = -1f;
        private const float HockeyEndDelay = 1.0f;
        private static string _hockeyEndName = "Air hockey | right end";

        // Every air hockey table's "aim" marker. Each station has its own table and stations load
        // as you teleport, so the list is refreshed regularly (a typed lookup is cheap) and the
        // active one is used.
        private static readonly System.Collections.Generic.List<Transform> _hockeyAims = new System.Collections.Generic.List<Transform>();
        private static int _hockeyTablesLogged = -1;

        private static void RefreshHockeyAims()
        {
            long pt = Perf.Now();
            try { RefreshHockeyAimsInner(); } finally { Perf.Add("Air hockey table search", pt); }
        }
        private static void RefreshHockeyAimsInner()
        {
            _hockeyAims.Clear();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MoonCleaners.MoonAirHockey>()))
            {
                var h = o.TryCast<MoonCleaners.MoonAirHockey>();
                if (h == null || h.gameObject == null || !h.gameObject.scene.IsValid()) continue;
                Transform table = h.transform;
                while (table != null && table.name != "Air hockey") table = table.parent;
                if (table == null) table = h.transform.Find("Air hockey");
                var t = table != null ? table.Find("Air hockey | aim") : null;
                if (t != null && !_hockeyAims.Contains(t)) _hockeyAims.Add(t);
            }
            if (_hockeyAims.Count != _hockeyTablesLogged)
            {
                _hockeyTablesLogged = _hockeyAims.Count;
                Plugin.L.LogInfo($"[HockeyCamera] Found {_hockeyAims.Count} air hockey table(s)");
            }
        }

        private static bool HockeyActive()
        {
            try
            {
                // While playing, stick with the table we're at.
                if (_hockeyWasActive && _hockeyAim != null && _hockeyAim.gameObject.activeInHierarchy) return true;
                float now = Time.unscaledTime;
                if (now >= _nextHockeySearch)
                {
                    _nextHockeySearch = now + (_hockeyAims.Count == 0 ? 1f : 2f);
                    RefreshHockeyAims();
                }
                for (int i = 0; i < _hockeyAims.Count; i++)
                {
                    var t = _hockeyAims[i];
                    if (t == null) continue;
                    try
                    {
                        if (t.gameObject != null && t.gameObject.activeInHierarchy) { _hockeyAim = t; return true; }
                    }
                    catch { }
                }
                return false;
            }
            catch (Exception e)
            {
                if (!_hockeyWarned) { _hockeyWarned = true; Plugin.L.LogWarning("[HockeyCamera] Marker lookup failed: " + e.Message); }
                _hockeyAims.Clear();
                return false;
            }
        }

        // Resolve the LOCAL player's goal before overriding the render camera.
        // Auto compares the game-controlled camera to both goal markers at entry.
        // Each client can independently identify its own goal; manual override
        // is available for games where both players share the same camera anchor.
        private static bool SelectHockeyEnd(Vector3 gameCamera)
        {
            try
            {
                Transform table = _hockeyAim != null ? _hockeyAim.parent : null;
                if (table == null) return false;
                Transform right = table.Find("Air hockey | right end");
                Transform left = table.Find("Air hockey | left end");
                if (right == null || left == null) return false;
                string selection = Plugin.HockeyPlayerEnd != null ? Plugin.HockeyPlayerEnd.Value : "Auto";
                if (string.Equals(selection, "Right", StringComparison.OrdinalIgnoreCase))
                    _hockeyEndName = "Air hockey | right end";
                else if (string.Equals(selection, "Left", StringComparison.OrdinalIgnoreCase))
                    _hockeyEndName = "Air hockey | left end";
                else
                {
                    // The vanilla minigame camera is normally located outside the
                    // player's goal. Compare horizontal positions only; camera elevation
                    // does not help us decide which goal belongs to the player.
                    Vector3 hup = WorldUp(gameCamera);
                    Vector3 a = Vector3.ProjectOnPlane(gameCamera - right.position, hup);
                    Vector3 b = Vector3.ProjectOnPlane(gameCamera - left.position, hup);
                    _hockeyEndName = a.sqrMagnitude <= b.sqrMagnitude
                        ? "Air hockey | right end" : "Air hockey | left end";
                    if (Mathf.Abs(a.sqrMagnitude - b.sqrMagnitude) < 0.25f)
                        Plugin.L.LogWarning("[HockeyCamera] Local camera is nearly equidistant from both ends; set Player End manually if the view faces the wrong way.");
                }
                Plugin.L.LogInfo($"[HockeyCamera] playerEnd={_hockeyEndName} selection={selection} gameCamera={gameCamera.ToString("F3")} right={right.position.ToString("F3")} left={left.position.ToString("F3")}");
                return true;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[HockeyCamera] Could not select end: " + e.Message);
                return false;
            }
        }

        private static bool TryHockeyPose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            try
            {
                Transform table = _hockeyAim != null ? _hockeyAim.parent : null;
                if (table == null) return false;
                Transform nearEnd = table.Find(_hockeyEndName);
                if (nearEnd == null) return false;
                Vector3 tableCenter = table.position;
                // "Up" is the Moon's surface normal here (the far-side stations are upside down in
                // world terms), not world Y.
                Vector3 up = WorldUp(tableCenter);
                Vector3 outward = Vector3.ProjectOnPlane(nearEnd.position - tableCenter, up);
                if (outward.sqrMagnitude < 0.01f) return false;
                outward.Normalize();
                Vector3 forward = -outward; // inward toward the opponent
                Vector3 sideways = Vector3.Cross(up, forward).normalized;
                position = nearEnd.position + outward * Plugin.HockeyEndGap.Value
                           + sideways * Plugin.HockeySideOffset.Value;
                position += up * (Vector3.Dot(tableCenter - position, up) + Plugin.HockeyEyeHeight.Value);
                rotation = Quaternion.LookRotation(forward, up);
                return true;
            }
            catch (Exception e)
            {
                if (!_hockeyWarned) { _hockeyWarned = true; Plugin.L.LogWarning("[HockeyCamera] Pose lookup failed: " + e.Message); }
                return false;
            }
        }

        private static void UpdateHockeyView(Camera cam, Vector3 gamePos, Quaternion gameRot,
                                             ref Vector3 renderPos, ref Quaternion renderRot)
        {
            if (Plugin.HockeyKeepView == null || !Plugin.HockeyKeepView.Value)
            {
                _hockeyWasActive = false;
                return;
            }
            bool active = HockeyActive();
            float now = Time.unscaledTime;
            if (!active)
            {
                // The game's join transition briefly deactivates the aim marker; only call it
                // ended once the marker has stayed gone for a while.
                if (_hockeyLostSince < 0f) _hockeyLostSince = now;
                if (now - _hockeyLostSince < HockeyEndDelay) return;
                if (_hockeyWasActive) Plugin.L.LogInfo("[HockeyCamera] Air hockey ended; using regular camera again");
                _hockeyWasActive = false;
                return;
            }
            _hockeyLostSince = -1f;
            _tableSeenTime = now;
            if (!_hockeyWasActive)
            {
                if (!SelectHockeyEnd(gamePos) || !TryHockeyPose(out _hockeyAnchorPos, out _hockeyAnchorRot))
                {
                    if (!_hockeyWarned) { _hockeyWarned = true; Plugin.L.LogWarning("[HockeyCamera] Could not find table end; leaving camera unchanged"); }
                    return;
                }
                _hockeyWasActive = true;
                Plugin.L.LogInfo($"[HockeyCamera] START gameCamera={gamePos.ToString("F3")} " +
                    $"tableAnchor={_hockeyAnchorPos.ToString("F3")} " +
                    $"levelEuler={_hockeyAnchorRot.eulerAngles.ToString("F1")}");
            }
            // Recompute on each frame to allow live config changes and moving tables.
            if (TryHockeyPose(out _hockeyAnchorPos, out _hockeyAnchorRot))
            {
                renderPos = _hockeyAnchorPos;
                renderRot = _hockeyAnchorRot;
            }
        }

        // CHESS: when the local player sits down to play, the game swings its camera up to a high
        // top-down view. Keep the render camera in the chair instead. Render-only, like hockey.
        // Activation uses the game's own "Chess HUD" canvas (the only reliable "actually playing"
        // signal; MoonLoungeSpot.Seated is true even when nobody is sitting there), plus a chess
        // table near where the player was standing just before the HUD appeared.
        private const float ChessSpotRadius = 2.5f;
        private const float ChessTableRadius = 4f;
        private const float ChessEndDelay = 0.5f;

        private static readonly List<MoonCleaners.MoonChessTable> _chessTables = new List<MoonCleaners.MoonChessTable>();
        private static readonly List<MoonCleaners.MoonLoungeSpot> _chessSpots = new List<MoonCleaners.MoonLoungeSpot>();
        private static Canvas _chessHud;
        private static float _nextChessHudSearch;
        private static float _nextChessTableSearch;
        private static float _nextChessSpotSearch;
        private static bool _chessSeated;
        private static float _chessLostSince = -1f;
        private static Vector3 _lastFreeGamePos;
        private static bool _haveFreeGamePos;
        private static MoonCleaners.MoonLoungeSpot _chessSpot;
        private static MoonCleaners.MoonChessTable _chessTable;
        private static Vector3 _chessAnchorPos;
        private static Quaternion _chessAnchorRot;
        private static bool _chessWarned;

        // Latest chess override state, read by Apply() after UpdateChessView/UpdateHockeyView.
        private static bool _chessOverride;
        /// <summary>True while the chess seated view is being applied (used by ChessPointer).</summary>
        internal static bool ChessActive => _chessOverride;
        internal static bool HockeyViewActive => _hockeyWasActive;
        // True from the moment a table game is detected until well after it ends, including the
        // join transition where the game's markers flicker. While true, nothing may move the game
        // camera to the hand (that feeds into the game's sit-down camera move and flips the view).
        private static float _tableSeenTime = -100f;
        internal static bool TableSessionActive =>
            ViewOverrideActive || _hockeyWasActive || _chessOverride || _chessSeated ||
            Time.unscaledTime - _tableSeenTime < 0.75f;
        internal static MoonCleaners.MoonChessTable ChessTable => _chessTable;

        // Is the game's chess board UI on screen? Looks only at Canvas objects, every 0.5 s at most
        // while "Chess HUD" hasn't been found yet.
        // Every station has its own "Chess HUD" canvas, and stations load as you teleport: keep all
        // of them (refreshed regularly with a typed lookup) and treat chess as on when any is shown.
        private static readonly System.Collections.Generic.List<Canvas> _chessHuds = new System.Collections.Generic.List<Canvas>();
        private static int _chessHudsLogged = -1;
        // Called by UiToWorld when it first picks up a canvas (it already walks all canvases to move
        // them into VR), so chess needs no scanning of its own.
        internal static void NoteCanvas(Canvas c)
        {
            try
            {
                if (c == null || !string.Equals(c.gameObject.name, "Chess HUD", StringComparison.Ordinal)) return;
                _chessHuds.RemoveAll(h => h == null);
                _chessHuds.Add(c);
                if (_chessHuds.Count != _chessHudsLogged) { _chessHudsLogged = _chessHuds.Count; Plugin.L.LogInfo($"[ChessCamera] Found {_chessHuds.Count} chess HUD canvas(es)"); }
            }
            catch { }
        }

        private static bool ChessHudActive()
        {
            for (int k = 0; k < _chessHuds.Count; k++)
            {
                try
                {
                    var c = _chessHuds[k];
                    if (c != null && c.gameObject != null && c.gameObject.activeInHierarchy && c.enabled) return true;
                }
                catch { }
            }
            return false;
        }

        // Cheap component lookup: only the one game type, at most every 5 s when nothing is found.
        private static float _nextChessRetry; private static int _chessTablesLogged = -1;
        private static void RefreshChessTables()
        {
            _chessTables.Clear();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MoonCleaners.MoonChessTable>()))
            {
                var t = o.TryCast<MoonCleaners.MoonChessTable>();
                if (t == null || t.gameObject == null || !t.gameObject.scene.IsValid()) continue;
                _chessTables.Add(t);
            }
            if (_chessTables.Count != _chessTablesLogged) { _chessTablesLogged = _chessTables.Count; Plugin.L.LogInfo($"[ChessCamera] Found {_chessTables.Count} chess table(s)"); }
        }

        // Cheap component lookup: only the one game type, so a short retry while it's missing is fine.
        private static void RefreshChessSpots()
        {
            _chessSpots.Clear();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MoonCleaners.MoonLoungeSpot>()))
            {
                var s = o.TryCast<MoonCleaners.MoonLoungeSpot>();
                if (s == null || s.gameObject == null || !s.gameObject.scene.IsValid()) continue;
                _chessSpots.Add(s);
            }
        }

        // Squared distance between two points along the Moon's surface (ignoring height), using the
        // surface "up" at a. Points more than 5 m apart in height count as far away, so a table at
        // the opposite station can never look close (east/west stations sit on world X, and
        // flattening world Y there would merge them).
        private static float FlatDistSq(Vector3 a, Vector3 b)
        {
            var up = WorldUp(a);
            var d = b - a;
            float h = Vector3.Dot(d, up);
            if (Mathf.Abs(h) > 5f) return float.MaxValue;
            return (d - up * h).sqrMagnitude;
        }

        // Nearest chess table to "at", or null when none is within ChessTableRadius.
        private static MoonCleaners.MoonChessTable NearestChessTable(Vector3 at)
        {
            MoonCleaners.MoonChessTable near = null;
            float best = ChessTableRadius * ChessTableRadius;
            for (int i = 0; i < _chessTables.Count; i++)
            {
                var t = _chessTables[i];
                if (t == null || t.gameObject == null) continue;
                float d = FlatDistSq(at, t.transform.position);
                if (d > best) continue;
                best = d; near = t;
            }
            return near;
        }

        // Pick the seat to use for a table, based on where the unbidden game camera was.
        private static MoonCleaners.MoonLoungeSpot PickChessSeat(MoonCleaners.MoonChessTable table, Vector3 wasAt)
        {
            if (table == null) return null;
            Vector3 tablePos = table.transform.position;
            MoonCleaners.MoonLoungeSpot bestSeat = null;
            float best = float.MaxValue;
            for (int i = 0; i < _chessSpots.Count; i++)
            {
                var s = _chessSpots[i];
                if (s == null || s.gameObject == null) continue;
                try
                {
                    if (FlatDistSq(tablePos, s.transform.position) > ChessSpotRadius * ChessSpotRadius) continue;
                    float d = FlatDistSq(wasAt, s.BodyPosition);
                    if (d >= best) continue;
                    best = d; bestSeat = s;
                }
                catch { }
            }
            return bestSeat;
        }

        private static void EndChess(bool log)
        {
            if (log && _chessSeated) Plugin.L.LogInfo("[ChessCamera] END; using regular camera again");
            _chessSeated = false;
            _chessLostSince = -1f;
            _chessSpot = null;
            _chessTable = null;
        }

        private static void UpdateChessView(Vector3 gamePos, ref Vector3 renderPos, ref Quaternion renderRot)
        {
            _chessOverride = false;
            try
            {
                if (Plugin.ChessSeatedView == null || !Plugin.ChessSeatedView.Value) { EndChess(false); return; }

                float now = Time.unscaledTime;
                bool hud = ChessHudActive();
                if (!hud)
                {
                    // While not playing, keep remembering where the vanilla camera is, so the
                    // seat can be chosen at the moment chess starts.
                    _lastFreeGamePos = gamePos;
                    _haveFreeGamePos = true;
                    if (_chessLostSince < 0f) _chessLostSince = now;
                    if (_chessSeated && now - _chessLostSince < ChessEndDelay)
                    {
                        // HUD flicker: keep the current override until it has been gone a moment.
                    }
                    else
                    {
                        EndChess(true);
                        return;
                    }
                }
                else
                {
                    _chessLostSince = -1f;
                    _tableSeenTime = now;
                }

                if ((_chessTables.Count == 0 || _nextChessTableSearch == 0f) && now >= _nextChessTableSearch)
                {
                    // Retry quickly while nothing is found, so the first chess game isn't late.
                    _nextChessTableSearch = now + (_chessTables.Count == 0 ? 0.5f : 5f);
                    RefreshChessTables();
                }
                if (now >= _nextChessSpotSearch)
                {
                    _nextChessSpotSearch = now + (_chessSpots.Count == 0 ? 0.5f : 5f);
                    RefreshChessSpots();
                }

                if (!_chessSeated)
                {
                    // Only start on a fresh "the player is playing chess" edge with a table nearby.
                    if (!hud) return;
                    Vector3 at = _haveFreeGamePos ? _lastFreeGamePos : gamePos;
                    MoonCleaners.MoonChessTable table = NearestChessTable(at);
                    var seat = table != null ? PickChessSeat(table, at) : null;
                    if (seat == null && now >= _nextChessRetry)
                    {
                        // After teleporting, this station's tables and seats may not be in our
                        // lists yet: look them up again and retry.
                        _nextChessRetry = now + 0.5f;
                        RefreshChessTables();
                        RefreshChessSpots();
                        table = NearestChessTable(at);
                        seat = table != null ? PickChessSeat(table, at) : null;
                    }
                    if (table == null || seat == null) return;
                    _chessSeated = true;
                    _chessSpot = seat;
                    _chessTable = table;
                    try
                    {
                        Plugin.L.LogInfo($"[ChessCamera] START seat={seat.transform.position.ToString("F3")} " +
                            $"body={seat.BodyPosition.ToString("F3")} table={table.transform.position.ToString("F3")} " +
                            $"wasAt={at.ToString("F3")}");
                    }
                    catch { }
                }

                // Seat and table stay locked until chess ends; recompute the pose each frame so
                // live config changes apply.
                if (!TryChessPose(out _chessAnchorPos, out _chessAnchorRot)) return;
                renderPos = _chessAnchorPos;
                renderRot = _chessAnchorRot;
                _chessOverride = true;
            }
            catch (Exception e)
            {
                if (!_chessWarned) { _chessWarned = true; Plugin.L.LogWarning("[ChessCamera] error: " + e.Message); }
                EndChess(false);
            }
        }

        private static bool TryChessPose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            var seat = _chessSpot;
            var table = _chessTable;
            if (seat == null || seat.gameObject == null || table == null || table.gameObject == null) return false;
            Vector3 body = seat.BodyPosition;
            Vector3 up = WorldUp(body); // Moon surface normal (far-side stations are upside down in world terms)
            position = body + up * Plugin.ChessEyeHeight.Value;
            Vector3 toward = Vector3.ProjectOnPlane(table.transform.position - body, up);
            float len = toward.magnitude;
            if (len < 0.01f) return false;
            toward /= len;
            position += toward * Plugin.ChessLean.Value;
            rotation = Quaternion.LookRotation(toward, up);
            return true;
        }

        private static void Apply(Camera cam)
        {
            try
            {
                if (!GetHead(out var hr, out var hp)) return;
                var tr = cam.transform;
                var baseRot = tr.rotation;
                var basePos = tr.position;
                ProbeMinigame(cam, basePos, baseRot, hp, hr);
                Vector3 renderPos = basePos;
                Quaternion renderRot = baseRot;
                UpdateHockeyView(cam, basePos, baseRot, ref renderPos, ref renderRot);
                UpdateChessView(basePos, ref renderPos, ref renderRot);
                if (renderPos != basePos || renderRot != baseRot)
                { _viewOverrideFrame = Time.frameCount; _viewOverridePos = renderPos; _viewOverrideRot = renderRot; }
                SavedPoses.Add(new Saved { TransformPtr = tr.Pointer, T = tr, Pos = basePos, Rot = baseRot });
                tr.SetPositionAndRotation(renderPos + renderRot * hp + WorldUp(renderPos) * Plugin.HeadHeightOffset.Value, renderRot * hr);
                OffsetTools(tr);
                // A held tool (brush) dangling in front of a seated/table view looks wrong: hide it.
                // Also for the moment after you stand up, so the brush doesn't flash up in front of
                // your face before it's back in your hand.
                if (_hockeyWasActive || _chessOverride || TableSessionActive) HideHeldTools(tr);
                if (!_loggedFirst) { _loggedFirst = true; Plugin.L.LogInfo($"Head tracking: applying to camera '{cam.name}'"); }
                if (!_loggedHierarchy && Time.unscaledTime > 30f)
                {
                    _loggedHierarchy = true;
                    var root = GameCameraRoot(tr);
                    Plugin.L.LogInfo("Game camera hierarchy (root parent: " + (root.parent != null ? root.parent.name : "none") + "):");
                    LogHierarchy(root, 0, 3);
                }
            }
            catch (Exception e)
            {
                if (!_loggedError) { _loggedError = true; Plugin.L.LogError("Head tracking apply error: " + e); }
            }
        }

        private static void RestoreAll()
        {
            RestoreTools();
            for (int i = SavedPoses.Count - 1; i >= 0; i--)
            {
                var s = SavedPoses[i];
                try { if (s.T != null) s.T.SetPositionAndRotation(s.Pos, s.Rot); } catch { }
            }
            SavedPoses.Clear();
        }

        private static void OnBegin(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null || !Plugin.HeadTrackingEnabled.Value) return;
            if (!cam.stereoEnabled) return;
            // Only the main view follows your head. Cameras that film into an image (the field
            // manual, the contract clipboard) must keep their own fixed angle.
            if (cam.targetTexture != null) return;
            // Place the UI from the camera's final (pre-head) pose for this frame, so it can't lag.
            try
            {
                int f = Time.frameCount;
                if (Plugin.UiRenderTimePlacement.Value && f != _lastUiFrame)
                {
                    var main = Camera.main;
                    if (main != null && (cam.Pointer == main.Pointer || GameCameraRoot(cam.transform).Pointer == main.transform.Pointer))
                    {
                        _lastUiFrame = f;
                        long ut = Perf.Now();
                        UiToWorld.Place(main); // final pose of the game's own camera for this frame
                        Perf.Add("Menu placement (render)", ut);
                        ut = Perf.Now(); MapFix.Layout(); Perf.Add("Map layout", ut);
                        if (!_loggedUiCam) { _loggedUiCam = true; Plugin.L.LogInfo("UI: render-time placement active"); }
                    }
                }
            }
            catch { }
            long at = Perf.Now(); Apply(cam); Perf.Add("Head tracking + table views", at);
        }

        private static void OnEnd(ScriptableRenderContext ctx, Camera cam)
        {
            if (cam == null) return;
            IntPtr p;
            try { p = cam.transform.Pointer; } catch { return; }
            RestoreTools();
            for (int i = SavedPoses.Count - 1; i >= 0; i--)
            {
                if (SavedPoses[i].TransformPtr != p) continue;
                var s = SavedPoses[i];
                try { s.T.SetPositionAndRotation(s.Pos, s.Rot); } catch { }
                SavedPoses.RemoveAt(i);
            }
        }
    }
}
