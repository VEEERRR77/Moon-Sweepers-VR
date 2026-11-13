// The moon map (MoonMapScreen) normally follows the mouse / gamepad cursor. In VR this points its
// cursor wherever the right-hand laser touches the map panel, so you aim with the laser and press
// A to select. It also tells UiToWorld which canvas is the map so it can be sized on its own.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class MapFix
    {
        private static Type _t;
        private static PropertyInfo _isOpen, _pointer, _reticle, _canvasRect, _travelP, _travelListP, _worldP;
        private static PropertyInfo _rowRect, _rowStation;
        private static MethodInfo _teleport;
        private static RectTransform _travel;
        private static bool _moved, _aHeld, _aDown;
        private static int _aFrame = -1;
        private static Vector3 _origPos, _origScale; private static Quaternion _origRot;
        internal static string CanvasName;
        // There can be more than one map screen (e.g. the console hologram); the map counts as open
        // if any of them was open this frame.
        private static int _openFrame = -100;
        internal static bool Open => Time.frameCount - _openFrame <= 2;
        private static bool _laserForced, _laserWas;

        // Laser on while the map is open, then back to how it was.
        private static void LaserForMap(bool open)
        {
            if (open && !_laserForced)
            {
                _laserForced = true; _laserWas = Plugin.LaserPointer.Value;
                if (!_laserWas) { Plugin.LaserPointer.Value = true; Plugin.L.LogInfo("Map: laser on for the map"); }
            }
            else if (!open && _laserForced)
            {
                _laserForced = false;
                if (Plugin.LaserPointer.Value != _laserWas) Plugin.LaserPointer.Value = _laserWas;
            }
        }
        private static RectTransform _rt;
        private static bool _driving;
        // How the game's pointer maps onto the map panel: 0 = screen pixels, 1 = canvas units from the
        // bottom-left corner, 2 = canvas units from the centre. Worked out by watching the game's own cursor.
        private static int _mode = -1, _samples;
        private static readonly float[] _err = new float[3];
        private static bool _loggedErr, _loggedName;

        public static void Install(Harmony h)
        {
            try
            {
                _t = AccessTools.TypeByName("MoonCleaners.MoonMapScreen");
                var upd = _t != null ? AccessTools.Method(_t, "Update") : null;
                if (upd == null) { Plugin.L.LogWarning("Map: map screen not found"); return; }
                _isOpen = AccessTools.Property(_t, "IsOpen");
                _pointer = AccessTools.Property(_t, "pointer");
                _reticle = AccessTools.Property(_t, "reticle");
                _canvasRect = AccessTools.Property(_t, "canvasRect");
                _travelP = AccessTools.Property(_t, "travel");
                _travelListP = AccessTools.Property(_t, "travelList");
                _worldP = AccessTools.Property(_t, "World");
                _teleport = AccessTools.Method(_t, "Teleport");
                var row = AccessTools.TypeByName("MoonCleaners.MoonMapScreen+TravelRow") ?? AccessTools.TypeByName("TravelRow");
                if (row == null) foreach (var nt in _t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) if (nt.Name == "TravelRow") row = nt;
                if (row != null) { _rowRect = AccessTools.Property(row, "rect"); _rowStation = AccessTools.Property(row, "station"); }
                h.Patch(upd,
                    prefix: new HarmonyMethod(typeof(MapFix).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(MapFix).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.L.LogMessage($"Map: laser pointing installed (pointer {_pointer != null}, reticle {_reticle != null}, canvas {_canvasRect != null}, teleport {_teleport != null && _rowRect != null && _travelListP != null})");
            }
            catch (Exception e) { Plugin.L.LogError("Map: install failed: " + e); }
        }

        private static Vector2 Size => _rt.rect.size;

        /// <summary>Where the laser ray meets the map panel (local, from the panel centre).</summary>
        private static bool Hit(Vector3 p, Vector3 dir, out Vector2 local, out float dist)
        {
            local = default; dist = 0f;
            if (!Open || _rt == null) return false;
            var plane = new Plane(_rt.forward, _rt.position);
            if (!plane.Raycast(new Ray(p, dir), out dist) || dist <= 0f) return false;
            Vector3 l = _rt.InverseTransformPoint(p + dir * dist);
            local = new Vector2(l.x, l.y) - _rt.rect.center;
            var half = Size * 0.5f;
            return Mathf.Abs(local.x) <= half.x && Mathf.Abs(local.y) <= half.y;
        }

        /// <summary>For the laser: distance to the map panel if the beam lands on it.</summary>
        internal static bool TryClip(Vector3 p, Vector3 dir, out float dist)
        {
            try { return Hit(p, dir, out _, out dist) || TravelHit(p, dir, out _, out dist); } catch { dist = 0f; return false; }
        }

        // ---- Teleport panel: shown as its own, bigger screen to the right of the map ----

        private static bool TravelShown => _travel != null && _travel.gameObject.activeInHierarchy;

        private static bool InRect(RectTransform r, Vector3 world)
        {
            Vector3 l = r.InverseTransformPoint(world);
            return r.rect.Contains(new Vector2(l.x, l.y));
        }

        private static bool TravelHit(Vector3 p, Vector3 dir, out Vector3 point, out float dist)
        {
            point = default; dist = 0f;
            if (!Open || !TravelShown) return false;
            var plane = new Plane(_travel.forward, _travel.position);
            if (!plane.Raycast(new Ray(p, dir), out dist) || dist <= 0f) return false;
            point = p + dir * dist;
            return InRect(_travel, point);
        }

        /// <summary>Called at render time, after the map panel is placed: moves the teleport panel.</summary>
        internal static void Layout()
        {
            LaserForMap(Open);
            if (!Open) _rt = null;
            try
            {
                if (!Open || _rt == null || !TravelShown || Plugin.TeleportScale.Value <= 0f) { Restore(); return; }
                if (!_moved)
                {
                    _origPos = _travel.localPosition; _origRot = _travel.localRotation; _origScale = _travel.localScale;
                    _moved = true;
                }
                _travel.rotation = _rt.rotation;
                _travel.localScale = _origScale * Plugin.TeleportScale.Value;
                Vector3 centre = _travel.TransformPoint(_travel.rect.center);
                float halfW = _travel.rect.width * 0.5f * _travel.lossyScale.x;
                Vector3 mapRight = _rt.TransformPoint(_rt.rect.center + new Vector2(_rt.rect.width * 0.5f, 0f));
                Vector3 target = mapRight + _rt.right * (0.06f + halfW);
                _travel.position += target - centre;
            }
            catch (Exception e)
            {
                if (!_loggedLayout) { _loggedLayout = true; Plugin.L.LogError("Map: teleport panel layout: " + e); }
            }
        }
        private static bool _loggedLayout; private static int _missLogs;

        private static void Restore()
        {
            if (!_moved) return;
            _moved = false;
            try { if (_travel != null) { _travel.localPosition = _origPos; _travel.localRotation = _origRot; _travel.localScale = _origScale; } } catch { }
        }

        /// <summary>Laser on a teleport row + A = teleport. Returns true if A was used here.</summary>
        private static bool TeleportClick(object inst, Vector3 hp, Vector3 dir, bool aDown)
        {
            if (!aDown || !TravelHit(hp, dir, out var point, out _)) return false;
            var list = _travelListP?.GetValue(inst);
            if (list == null || _teleport == null) return true;
            var cnt = (int)list.GetType().GetProperty("Count").GetValue(list);
            var item = list.GetType().GetProperty("Item");
            for (int i = 0; i < cnt; i++)
            {
                var row = item.GetValue(list, new object[] { i });
                var rect = row != null ? _rowRect.GetValue(row) as RectTransform : null;
                if (rect == null || !rect.gameObject.activeInHierarchy || !InRect(rect, point)) continue;
                var station = _rowStation.GetValue(row);
                if (station == null) continue;
                _teleport.Invoke(inst, new[] { _worldP.GetValue(inst), station });
                Plugin.L.LogInfo($"Map: teleport row {i} pressed");
                return true;
            }
            if (_missLogs++ < 5) Plugin.L.LogInfo($"Map: A pressed on the teleport panel but not on a row ({cnt} rows)");
            return true;
        }

        private static Vector2 ToPointer(Vector2 local)
        {
            var s = Size;
            switch (_mode)
            {
                case 1: return local + s * 0.5f;
                case 2: return local;
                default: return new Vector2((local.x / s.x + 0.5f) * Screen.width, (local.y / s.y + 0.5f) * Screen.height);
            }
        }

        private static Vector2 FromPointer(int mode, Vector2 p)
        {
            var s = Size;
            switch (mode)
            {
                case 1: return p - s * 0.5f;
                case 2: return p;
                default: return new Vector2((p.x / Mathf.Max(1, Screen.width) - 0.5f) * s.x, (p.y / Mathf.Max(1, Screen.height) - 0.5f) * s.y);
            }
        }

        private static bool Pre(object __instance)
        {
            _driving = false;
            if (_aFrame != Time.frameCount)   // once per frame, however many map screens there are
            {
                _aFrame = Time.frameCount;
                var pad = UnityEngine.InputSystem.Gamepad.current;
                bool aNow = pad != null && pad.buttonSouth.isPressed;
                _aDown = aNow && !_aHeld; _aHeld = aNow;
            }
            bool aDown = _aDown;
            try
            {
                if (!(bool)_isOpen.GetValue(__instance)) return true;   // a closed map screen: leave everything alone
                _openFrame = Time.frameCount;
                _rt = (RectTransform)_canvasRect.GetValue(__instance);
                if (_rt == null) return true;
                if (_travelP != null) _travel = _travelP.GetValue(__instance) as RectTransform;
                if (CanvasName == null)
                {
                    for (var tr = _rt.transform; tr != null; tr = tr.parent)
                        if (tr.GetComponent<Canvas>() != null) CanvasName = tr.gameObject.name;
                    if (CanvasName != null && !_loggedName) { _loggedName = true; Plugin.L.LogInfo($"Map: map screen is canvas '{CanvasName}' ({Size.x:F0}x{Size.y:F0}, screen {Screen.width}x{Screen.height})"); }
                }
                if (!MotionControls.TryGetHandAim(out var hp, out var hr)) return true;
                Vector3 dir = hr * Vector3.forward;
                if (TeleportClick(__instance, hp, dir, aDown)) return false;   // skip the game's own click this frame
                if (_mode < 0) return true;   // still learning how the pointer works
                if (!Hit(hp, dir, out var local, out _)) return true;
                _pointer.SetValue(__instance, ToPointer(local));
                _driving = true;
            }
            catch (Exception e)
            {
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Map: " + e); }
            }
            return true;
        }

        private static void Post(object __instance)
        {
            if (!Open || _rt == null || _mode >= 0 || _driving) return;
            try
            {
                if (!(bool)_isOpen.GetValue(__instance)) return;
                var ret = (RectTransform)_reticle.GetValue(__instance);
                if (ret == null) return;
                Vector3 l = _rt.InverseTransformPoint(ret.position);
                var seen = new Vector2(l.x, l.y) - _rt.rect.center;
                var p = (Vector2)_pointer.GetValue(__instance);
                for (int m = 0; m < 3; m++) _err[m] += (FromPointer(m, p) - seen).magnitude;
                if (++_samples < 20) return;
                int best = 0;
                for (int m = 1; m < 3; m++) if (_err[m] < _err[best]) best = m;
                _mode = best;
                Plugin.L.LogInfo($"Map: pointer mode {best} (errors {_err[0] / _samples:F0} / {_err[1] / _samples:F0} / {_err[2] / _samples:F0}; last pointer {p}, cursor at {seen})");
            }
            catch (Exception e)
            {
                _mode = 0;
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Map: calibration failed, assuming screen pixels: " + e); }
            }
        }
    }
}
