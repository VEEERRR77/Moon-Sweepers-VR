// The supply wall / tool upgrade screen picks items by projecting the in-world shelf onto the
// flat screen, which doesn't line up in VR - so stick navigation finds nothing to move to.
// This steps the selection ourselves when the left stick (or D-pad) is flicked.
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class MenuFix
    {
        private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static Type _wall;
        private static MethodInfo _pick;
        private static Vector2 _lastDir; private static float _repeatAt; private static int _logs;
        private static bool _loggedErr;

        public static void Install(Harmony h)
        {
            try
            {
                _wall = AccessTools.TypeByName("MoonCleaners.MoonSupplyWallScreen");
                var upd = _wall != null ? AccessTools.Method(_wall, "Update") : null;
                if (upd == null) { Plugin.L.LogWarning("Menu fix: supply wall screen not found"); return; }
                foreach (var m in _wall.GetMethods(F))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "Pick" && ps.Length == 1 && ps[0].ParameterType.Name == "Spot") _pick = m;
                }
                h.Patch(upd, postfix: new HarmonyMethod(typeof(MenuFix).GetMethod(nameof(WallPost), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.L.LogMessage("Menu fix: supply wall stick navigation installed (pick method " + (_pick != null ? "found" : "MISSING") + ")");
            }
            catch (Exception e) { Plugin.L.LogError("Menu fix install failed: " + e); }
        }

        private static object Get(object o, string name)
        {
            var p = o.GetType().GetProperty(name, F);
            return p != null ? p.GetValue(o) : null;
        }

        private static IntPtr Ptr(object o) => o is Il2CppObjectBase b ? b.Pointer : IntPtr.Zero;

        private static Vector2 ReadDir()
        {
            var pad = UnityEngine.InputSystem.Gamepad.current;
            if (pad == null) return Vector2.zero;
            if (pad.leftStick.up.isPressed || pad.dpad.up.isPressed) return Vector2.up;
            if (pad.leftStick.down.isPressed || pad.dpad.down.isPressed) return Vector2.down;
            if (pad.leftStick.left.isPressed || pad.dpad.left.isPressed) return Vector2.left;
            if (pad.leftStick.right.isPressed || pad.dpad.right.isPressed) return Vector2.right;
            return Vector2.zero;
        }

        private static void WallPost(object __instance)
        {
            try
            {
                if (__instance == null || _pick == null || !Plugin.MotionControlsEnabled.Value) return;
                if (!(Get(__instance, "IsOpen") is bool open) || !open) { _lastDir = Vector2.zero; return; }
                var dir = ReadDir();
                float now = Time.unscaledTime;
                if (dir == Vector2.zero) { _lastDir = dir; return; }
                if (dir == _lastDir && now < _repeatAt) return;
                _repeatAt = now + (dir == _lastDir ? 0.2f : 0.45f);
                _lastDir = dir;
                Step(__instance, dir);
            }
            catch (Exception e)
            {
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Menu fix error: " + (e.InnerException ?? e)); }
            }
        }

        private static void Step(object screen, Vector2 dir)
        {
            object list = Get(screen, "spots") ?? Get(screen, "Spots");
            if (list == null) return;
            var lt = list.GetType();
            int count = Convert.ToInt32(lt.GetProperty("Count", F).GetValue(list));
            var item = lt.GetMethod("get_Item", F);
            if (count == 0 || item == null) return;

            var spots = new List<object>(); var centres = new List<Vector2>(); var valid = new List<bool>();
            int onScreen = 0, goodAreas = 0;
            for (int i = 0; i < count; i++)
            {
                var s = item.Invoke(list, new object[] { i });
                if (s == null) continue;
                spots.Add(s);
                bool ok = false; Vector2 c = Vector2.zero;
                if (Get(s, "area") is Rect r && r.width > 0.5f && r.height > 0.5f) { ok = true; c = r.center; goodAreas++; }
                if (Get(s, "onScreen") is bool os && os) onScreen++;
                centres.Add(c); valid.Add(ok);
            }
            if (spots.Count == 0) return;

            IntPtr sel = Ptr(Get(screen, "selected"));
            int cur = -1;
            for (int i = 0; i < spots.Count; i++) if (Ptr(spots[i]) == sel && sel != IntPtr.Zero) { cur = i; break; }

            int next = -1; string how;
            if (cur >= 0 && valid[cur] && goodAreas >= 2)
            {
                how = "by position";
                float best = float.MaxValue;
                for (int i = 0; i < spots.Count; i++)
                {
                    if (i == cur || !valid[i]) continue;
                    Vector2 d = centres[i] - centres[cur];
                    float along = Vector2.Dot(d, dir);
                    if (along < 1f) continue;
                    float perp = Mathf.Abs(d.x * dir.y - d.y * dir.x);
                    if (perp > along * 2f) continue;   // too far off to the side
                    float score = along + perp * 2f;
                    if (score < best) { best = score; next = i; }
                }
            }
            else
            {
                how = "in order";
                int stepBy = (dir == Vector2.right || dir == Vector2.down) ? 1 : -1;
                next = cur < 0 ? 0 : (cur + stepBy + spots.Count) % spots.Count;
            }
            if (_logs < 6)
            {
                _logs++;
                Plugin.L.LogInfo($"Menu fix: {spots.Count} items ({onScreen} flagged on-screen, {goodAreas} with positions), selected #{cur} -> #{next} {how}, dir {dir}" +
                    (cur >= 0 ? $", from {centres[cur]}" : "") + (next >= 0 ? $" to {centres[next]}" : ""));
            }
            if (next < 0 || next == cur) return;
            _pick.Invoke(screen, new[] { spots[next] });
        }
    }
}
