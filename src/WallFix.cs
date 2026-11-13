// Laser pointing for the tools screen (supply wall) and the upgrade matrix, the same way as the
// moon map: point the right-hand laser at an item to select it, press A to use it as normal.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class WallFix
    {
        internal static bool Open => Supply.Open || Upgrades.Open;
        internal static bool TryClip(Vector3 p, Vector3 dir, out float dist) =>
            Supply.TryClip(p, dir, out dist) || Upgrades.TryClip(p, dir, out dist);
        internal static readonly LaserScreen Supply = new LaserScreen("Tools screen", "MoonCleaners.MoonSupplyWallScreen", "spots", "selected", "Spot", "card");
        internal static readonly LaserScreen Upgrades = new LaserScreen("Upgrade matrix", "MoonCleaners.MoonUpgradeTreeUI", "views", "picked", "NodeView", "row");
        public static void Install(Harmony h) { Supply.Install(h); Upgrades.Install(h); }
    }

    internal class LaserScreen
    {
        private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly System.Collections.Generic.Dictionary<Type, LaserScreen> _byType = new System.Collections.Generic.Dictionary<Type, LaserScreen>();
        private readonly string _label, _typeName, _listName, _selName, _itemType, _itemRect;
        private Type _t;
        private PropertyInfo _isOpen, _spots, _selected, _root, _spotCard;
        private MethodInfo _pick;
        private int _openFrame = -100;
        internal bool Open => Time.frameCount - _openFrame <= 2;
        private RectTransform _panel;
        private bool _loggedErr, _loggedPick;

        internal LaserScreen(string label, string typeName, string listName, string selName, string itemType, string itemRect)
        { _label = label; _typeName = typeName; _listName = listName; _selName = selName; _itemType = itemType; _itemRect = itemRect; }

        public void Install(Harmony h)
        {
            try
            {
                _t = AccessTools.TypeByName(_typeName);
                var upd = _t != null ? AccessTools.Method(_t, "Update") : null;
                if (upd == null) { Plugin.L.LogWarning(_label + ": not found"); return; }
                _isOpen = AccessTools.Property(_t, "IsOpen");
                _spots = AccessTools.Property(_t, _listName);
                _selected = AccessTools.Property(_t, _selName);
                _root = AccessTools.Property(_t, "root");
                foreach (var m in _t.GetMethods(F))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "Pick" && ps.Length == 1 && ps[0].ParameterType.Name == _itemType) _pick = m;
                }
                if (_pick != null) _spotCard = AccessTools.Property(_pick.GetParameters()[0].ParameterType, _itemRect);
                _byType[_t] = this;
                h.Patch(upd, prefix: new HarmonyMethod(typeof(LaserScreen).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.L.LogMessage($"{_label}: laser pointing installed (items {_spots != null}, rects {_spotCard != null}, pick {_pick != null})");
            }
            catch (Exception e) { Plugin.L.LogError(_label + ": install failed: " + e); }
        }

        private static void Pre(object __instance)
        {
            if (__instance != null && _byType.TryGetValue(__instance.GetType(), out var me)) me.Run(__instance);
        }

        private static RectTransform AsRect(object o)
        {
            if (o is RectTransform r) return r;
            if (o is Component c) return c.transform.TryCast<RectTransform>();
            return null;
        }

        private static bool InRect(RectTransform r, Vector3 world)
        {
            Vector3 l = r.InverseTransformPoint(world);
            return r.rect.Contains(new Vector2(l.x, l.y));
        }

        private static bool PlaneHit(RectTransform r, Vector3 p, Vector3 dir, out Vector3 point, out float dist)
        {
            point = default;
            var plane = new Plane(r.forward, r.position);
            if (!plane.Raycast(new Ray(p, dir), out dist) || dist <= 0f) return false;
            point = p + dir * dist;
            return true;
        }

        /// <summary>For the laser: distance to the tools screen if the beam lands on it.</summary>
        internal bool TryClip(Vector3 p, Vector3 dir, out float dist)
        {
            dist = 0f;
            try
            {
                if (!Open || _panel == null || !_panel.gameObject.activeInHierarchy) return false;
                return PlaneHit(_panel, p, dir, out var pt, out dist) && InRect(_panel, pt);
            }
            catch { return false; }
        }

        private RectTransform PanelOf(object inst)
        {
            var go = _root?.GetValue(inst) as GameObject;
            if (go == null) return null;
            // The whole screen: the top-most RectTransform above the root (the canvas).
            RectTransform best = null;
            for (var tr = go.transform; tr != null; tr = tr.parent)
            {
                var rt = tr.TryCast<RectTransform>();
                if (rt != null) best = rt;
            }
            return best;
        }

        private void Run(object __instance)
        {
            try
            {
                if (__instance == null || !Plugin.MotionControlsEnabled.Value) return;
                if (!(_isOpen.GetValue(__instance) is bool open) || !open) return;
                _openFrame = Time.frameCount;
                _panel = PanelOf(__instance);
                if (_pick == null || _spotCard == null) return;
                if (!MotionControls.TryGetHandAim(out var hp, out var hr)) return;
                Vector3 dir = hr * Vector3.forward;

                var list = _spots.GetValue(__instance);
                if (list == null) return;
                var lt = list.GetType();
                int count = Convert.ToInt32(lt.GetProperty("Count", F).GetValue(list));
                var item = lt.GetMethod("get_Item", F);
                object sel = _selected?.GetValue(__instance);
                IntPtr selPtr = sel is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase sb ? sb.Pointer : IntPtr.Zero;
                for (int i = 0; i < count; i++)
                {
                    var s = item.Invoke(list, new object[] { i });
                    var card = s != null ? AsRect(_spotCard.GetValue(s)) : null;
                    if (card == null || !card.gameObject.activeInHierarchy) continue;
                    if (!PlaneHit(card, hp, dir, out var pt, out _) || !InRect(card, pt)) continue;
                    if (s is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase b && b.Pointer == selPtr) return;   // already selected
                    _pick.Invoke(__instance, new[] { s });
                    if (!_loggedPick) { _loggedPick = true; Plugin.L.LogInfo($"{_label}: laser picked item {i}"); }
                    return;
                }
            }
            catch (Exception e)
            {
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError(_label + ": " + (e.InnerException ?? e)); }
            }
        }
    }
}
