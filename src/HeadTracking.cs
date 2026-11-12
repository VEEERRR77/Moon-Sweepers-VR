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

        private struct SavedChild { public Transform T; public Vector3 LocalPos; public Quaternion LocalRot; public bool Rot; }
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
            if (MotionControls.HandFrame == Time.frameCount)
            {
                var hp = MotionControls.HandPos; var hr = MotionControls.HandRot;
                var sc = root.lossyScale;
                for (int i = 0; i < root.childCount; i++)
                {
                    var c = root.GetChild(i);
                    if (IsAncestorOf(c, vrCam)) continue;
                    try { if (c.GetComponent<Camera>() != null) continue; } catch { }
                    var lp = c.localPosition; var lr = c.localRotation;
                    SavedChildren.Add(new SavedChild { T = c, LocalPos = lp, LocalRot = lr, Rot = true });
                    c.SetPositionAndRotation(hp + hr * Vector3.Scale(lp, sc), hr * lr);
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
                var dirLocal = root.InverseTransformDirection(Vector3.up * off);
                c.localPosition += new Vector3(dirLocal.x / Mathf.Max(ls.x, 1e-4f), dirLocal.y / Mathf.Max(ls.y, 1e-4f), dirLocal.z / Mathf.Max(ls.z, 1e-4f));
            }
        }

        private static void RestoreTools()
        {
            for (int i = SavedChildren.Count - 1; i >= 0; i--)
            {
                try { var sv = SavedChildren[i]; if (sv.T != null) { sv.T.localPosition = sv.LocalPos; if (sv.Rot) sv.T.localRotation = sv.LocalRot; } } catch { }
            }
            SavedChildren.Clear();
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
                bool recenter = false, up = false, down = false, toolUp = false, toolDown = false, laserToggle = false, laserColour = false;
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
            }
            catch (Exception e)
            {
                if (!_loggedControlsError) { _loggedControlsError = true; Plugin.L.LogWarning("Head tracking controls error: " + e.Message); }
            }
        }

        private bool _loggedControlsError;
        private bool _toolUpHeld, _toolDownHeld, _laserToggleHeld, _laserColourHeld;

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
            if (!Plugin.HeadTrackingEnabled.Value) return;
            HandleControls();
            MotionControls.Tick();
            Comfort.Tick();
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
            Laser.Tick();   // after all game updates, so the beam lines up with this frame's aim
            try
            {
                if (!Plugin.UiRenderTimePlacement.Value) return;
                var main = Camera.main;
                if (main == null) return;
                UiToWorld.Place(main);
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
        internal static bool TrackingToWorld(Vector3 p, Quaternion r, out Vector3 wp, out Quaternion wr)
        {
            wp = default; wr = Quaternion.identity;
            if (!_calibrated) return false;
            var main = Camera.main;
            if (main == null) return false;
            var bt = main.transform;
            var yawFix = Quaternion.Euler(0f, -_calibYaw, 0f);
            wp = bt.position + bt.rotation * (yawFix * (p - _calibPos)) + Vector3.up * Plugin.HeadHeightOffset.Value;
            wr = bt.rotation * (yawFix * r);
            return true;
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

        private static void Apply(Camera cam)
        {
            try
            {
                if (!GetHead(out var hr, out var hp)) return;
                var tr = cam.transform;
                var baseRot = tr.rotation;
                var basePos = tr.position;
                SavedPoses.Add(new Saved { TransformPtr = tr.Pointer, T = tr, Pos = basePos, Rot = baseRot });
                tr.SetPositionAndRotation(basePos + baseRot * hp + Vector3.up * Plugin.HeadHeightOffset.Value, baseRot * hr);
                OffsetTools(tr);
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
                        UiToWorld.Place(main); // final pose of the game's own camera for this frame
                        if (!_loggedUiCam) { _loggedUiCam = true; Plugin.L.LogInfo("UI: render-time placement active"); }
                    }
                }
            }
            catch { }
            Apply(cam);
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
