// Laser pointing for the garage menu (vehicles, fleet and upgrades pages): point the right-hand
// laser at a card, row, tab or button and pull the right trigger to press it.
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
        private static bool _loggedErr;
        private static int _clickLogs;
        private static bool _wasDown;
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
                if (!open) { _wasDown = false; return; }
                _openFrame = Time.frameCount;
                _panel = PanelOf(__instance);
                bool down = MotionControls.RawRightTrigger > 0.6f;
                bool press = down && !_wasDown;
                _wasDown = down;
                if (!press) return;
                if (!MotionControls.TryGetHandAim(out var hp, out var hr)) return;
                if (!Hit(hp, hr * Vector3.forward, out var pt, out _)) return;
                // Press the button under the laser ourselves: the game's menu is made of ordinary UI
                // buttons, but its mouse handling doesn't work with the menu floating in VR.
                UnityEngine.UI.Button best = null; float bestArea = float.MaxValue;
                foreach (var b in _panel.GetComponentsInChildren<UnityEngine.UI.Button>(false))
                {
                    if (b == null || !b.isActiveAndEnabled || !b.interactable) continue;
                    var rt = b.transform.TryCast<RectTransform>();
                    if (rt == null) continue;
                    Vector3 l = rt.InverseTransformPoint(pt);
                    var r = rt.rect;
                    if (!r.Contains(new Vector2(l.x, l.y))) continue;
                    float area = r.width * r.height;   // the smallest one is the one on top (a card's button over its panel)
                    if (area < bestArea) { bestArea = area; best = b; }
                }
                if (best == null)
                {
                    if (_clickLogs < 10) { _clickLogs++; Plugin.L.LogInfo("Garage: right trigger, but no button under the laser"); }
                    return;
                }
                if (_clickLogs < 20) { _clickLogs++; Plugin.L.LogInfo($"Garage: right trigger presses '{best.gameObject.name}'"); }
                best.onClick.Invoke();
            }
            catch (Exception e)
            {
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Garage: " + (e.InnerException ?? e)); }
            }
        }
    }
}
