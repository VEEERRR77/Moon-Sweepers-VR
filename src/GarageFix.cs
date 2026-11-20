// Laser pointing for the garage menu (vehicles, fleet and upgrades pages): the game's garage menu
// already works with a mouse, so the right-hand laser drives a virtual mouse over it and the right
// trigger is the mouse button - point at a card, row, tab or button and pull the trigger.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class GarageFix
    {
        private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static PropertyInfo _isOpen, _root;
        private static int _openFrame = -100;
        /// <summary>True while the garage menu is open (with motion controls on).</summary>
        internal static bool Open => Time.frameCount - _openFrame <= 2;
        private static RectTransform _panel;
        private static Vector2 _mousePos;
        private static bool _mouseUsed, _loggedErr, _loggedMouse;
        private static int _clickLogs;
        private static bool _wasDown;
        private static Canvas _canvas; private static Camera _oldCam;
        private static Camera _aimCam;

        // A camera that never draws anything: only used to aim the menu's clicks along the laser.
        private static Camera AimCamera()
        {
            if (_aimCam != null) return _aimCam;
            var go = new GameObject("Moon VR laser aim camera");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _aimCam = go.AddComponent<Camera>();
            _aimCam.enabled = false;
            _aimCam.cullingMask = 0;
            _aimCam.fieldOfView = 60f;
            _aimCam.nearClipPlane = 0.01f;
            return _aimCam;
        }

        public static void Install(Harmony h)
        {
            try
            {
                var t = AccessTools.TypeByName("MoonCleaners.MoonGarageMenu");
                var upd = t != null ? AccessTools.Method(t, "Update") : null;
                if (upd == null) { Plugin.L.LogWarning("Garage: menu not found"); return; }
                _isOpen = AccessTools.Property(t, "IsOpen");
                _root = AccessTools.Property(t, "root");
                h.Patch(upd, prefix: new HarmonyMethod(typeof(GarageFix).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.L.LogMessage($"Garage: laser pointing installed (open {_isOpen != null}, root {_root != null})");
            }
            catch (Exception e) { Plugin.L.LogError("Garage: install failed: " + e); }
        }

        private static RectTransform PanelOf(object inst)
        {
            var go = _root?.GetValue(inst) as GameObject;
            if (go == null) return null;
            RectTransform best = null;   // the whole screen: the top-most RectTransform (the canvas)
            for (var tr = go.transform; tr != null; tr = tr.parent)
            {
                var rt = tr.TryCast<RectTransform>();
                if (rt != null) best = rt;
            }
            return best;
        }

        private static bool Hit(Vector3 p, Vector3 dir, out Vector3 point, out float dist)
        {
            point = default; dist = 0f;
            if (_panel == null || !_panel.gameObject.activeInHierarchy) return false;
            var plane = new Plane(_panel.forward, _panel.position);
            if (!plane.Raycast(new Ray(p, dir), out dist) || dist <= 0f) return false;
            point = p + dir * dist;
            Vector3 l = _panel.InverseTransformPoint(point);
            return _panel.rect.Contains(new Vector2(l.x, l.y));
        }

        /// <summary>For the laser: distance to the garage menu if the beam lands on it.</summary>
        internal static bool TryClip(Vector3 p, Vector3 dir, out float dist)
        {
            dist = 0f;
            try { return Open && Hit(p, dir, out _, out dist); }
            catch { return false; }
        }

        private static void Pre(object __instance)
        {
            try
            {
                bool open = __instance != null && Plugin.MotionControlsEnabled.Value
                            && _isOpen?.GetValue(__instance) is bool o && o;
                if (!open)
                {
                    // Menu closed: let go of the virtual mouse button once.
                    if (_mouseUsed && Time.frameCount - _openFrame > 2) { _mouseUsed = false; _wasDown = false; VirtualMouse.Send(_mousePos, false); }
                    if (_canvas != null && Time.frameCount - _openFrame > 2)
                    {
                        try { _canvas.worldCamera = _oldCam != null ? _oldCam : Camera.main; } catch { }
                        _canvas = null; _oldCam = null;
                    }
                    return;
                }
                _openFrame = Time.frameCount;
                _panel = PanelOf(__instance);
                bool down = MotionControls.RawRightTrigger > 0.6f;
                var canvas = _panel != null ? _panel.GetComponent<Canvas>() : null;
                if (canvas == null) return;
                // The menu floats in front of your headset, which the game's own camera may not even
                // see, so screen positions from it miss. Instead the menu's event camera becomes a
                // (never-rendering) camera sitting on the laser: the mouse stays in the middle of the
                // screen, and the middle of that camera's view is exactly where the laser points.
                if (MotionControls.TryGetHandAim(out var hp, out var hr))
                {
                    var cam = AimCamera();
                    cam.transform.SetPositionAndRotation(hp, hr);
                    if (canvas.worldCamera == null || canvas.worldCamera.Pointer != cam.Pointer)
                    {
                        _canvas = canvas; _oldCam = canvas.worldCamera;
                        canvas.worldCamera = cam;
                    }
                    _mousePos = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                    if (!_loggedMouse) { _loggedMouse = true; Plugin.L.LogInfo("Garage: menu clicks follow the laser"); }
                }
                else if (!_mouseUsed) return;
                VirtualMouse.Send(_mousePos, down);
                _mouseUsed = true;
                if (down && !_wasDown && _clickLogs < 10)
                {
                    _clickLogs++;
                    Plugin.L.LogInfo($"Garage: right trigger click (laser on the menu {Hit(hp, hr * Vector3.forward, out _, out _)})");
                }
                _wasDown = down;
            }
            catch (Exception e)
            {
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Garage: " + (e.InnerException ?? e)); }
            }
        }
    }
}
