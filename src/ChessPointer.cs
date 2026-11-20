// Chess with the laser: while the VR seated view at a chess table is active, point the right-hand
// laser at the board and the table's "hovered" square follows where the beam lands, so pressing A
// (the game's own handling) picks up and places the piece.
// The game, however, works out its own square inside Input() from its own mouse pointer (a ray
// from the camera through the mouse position), so the laser is also presented to it as a virtual
// mouse (VirtualMouse): the beam's board point is projected to screen space and the right trigger
// clicks. The "hovered" set below stays as a fallback for the highlight.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class ChessPointer
    {
        private static Type _t;
        private static MethodInfo _squareAt, _squareLocal, _input;
        private static PropertyInfo _hovered;
        private static bool _loggedError, _loggedHover, _loggedOverwrite, _loggedRun, _loggedMouse, _loggedClick;

        // Worked out once per table: does SquareAt() take a world point (true) or a table-local one?
        private static IntPtr _probedPtr;
        private static int _space;   // 0 = unknown/failed, 1 = world, 2 = local
        private static int _wanted = -1;

        public static void Install(Harmony h)
        {
            try
            {
                _t = AccessTools.TypeByName("MoonCleaners.MoonChessTable");
                _input = _t != null ? AccessTools.Method(_t, "Input") : null;
                if (_input == null) { Plugin.L.LogWarning("Chess: chess table input not found"); return; }
                _squareAt = AccessTools.Method(_t, "SquareAt", new[] { typeof(Vector3) });
                _squareLocal = AccessTools.Method(_t, "SquareLocal", new[] { typeof(int) });
                _hovered = AccessTools.Property(_t, "hovered");
                h.Patch(_input,
                    prefix: new HarmonyMethod(typeof(ChessPointer).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(ChessPointer).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic)));
                var show = AccessTools.Method(_t, "ShowMarks");
                _marks = AccessTools.Property(_t, "marks");
                _targetMat = AccessTools.Property(_t, "targetMat");
                if (show != null && _marks != null && _targetMat != null)
                    h.Patch(show, postfix: new HarmonyMethod(typeof(ChessPointer).GetMethod(nameof(ShowMarksPost), BindingFlags.Static | BindingFlags.NonPublic)));
                if (_squareAt != null) Plugin.L.LogInfo("Chess: SquareAt found (used for our own hit test only; the game's call is native so it can't be patched)");
                Plugin.L.LogMessage($"Chess: laser placing installed (squareAt {_squareAt != null}, squareLocal {_squareLocal != null}, hovered {_hovered != null})");
            }
            catch (Exception e) { Plugin.L.LogError("Chess: install failed: " + e); }
        }

        // Move dots: the game draws them white, which can't be seen on the white squares. Draw the
        // ones on light squares black instead.
        private static PropertyInfo _marks, _targetMat;
        private static Material _blackDot;
        private static IntPtr _blackFrom;
        private static bool _loggedDots, _loggedDotErr;

        private static bool IsLight(int sq) => (((sq & 7) + (sq >> 3)) & 1) == (Plugin.ChessLightOdd.Value ? 0 : 1);

        private static void ShowMarksPost(object __instance)
        {
            try
            {
                if (!(__instance is Component table) || _squareAt == null) return;
                var target = _targetMat.GetValue(__instance) as Material;
                var list = _marks.GetValue(__instance);
                if (target == null || list == null) return;
                if (_blackDot == null || _blackFrom != target.Pointer)
                {
                    _blackFrom = target.Pointer;
                    _blackDot = new Material(target);
                    foreach (var prop in new[] { "_BaseColor", "_Color", "_TintColor" }) _blackDot.SetColor(Laser.PropertyId(prop), Color.black);
                }
                var lt = list.GetType();
                int count = Convert.ToInt32(lt.GetProperty("Count").GetValue(list));
                var item = lt.GetMethod("get_Item");
                int changed = 0;
                for (int i = 0; i < count; i++)
                {
                    var go = item.Invoke(list, new object[] { i }) as GameObject;
                    if (go == null || !go.activeSelf) continue;
                    var rend = go.GetComponent<Renderer>();
                    if (rend == null) continue;
                    var m = rend.sharedMaterial;
                    if (m == null) continue;
                    bool isTarget = m.Pointer == target.Pointer, isBlack = m.Pointer == _blackDot.Pointer;
                    if (!isTarget && !isBlack) continue;
                    int sq = CallSquareAt(table, table.transform.InverseTransformPoint(go.transform.position));
                    if (sq < 0 || sq > 63) continue;
                    var want = IsLight(sq) ? _blackDot : target;
                    if (m.Pointer != want.Pointer) { rend.sharedMaterial = want; changed++; }
                }
                if (changed > 0 && !_loggedDots) { _loggedDots = true; Plugin.L.LogInfo($"[ChessPointer] move dots on light squares drawn black ({changed})"); }
            }
            catch (Exception e)
            {
                if (!_loggedDotErr) { _loggedDotErr = true; Plugin.L.LogWarning("[ChessPointer] move dot colour error: " + (e.InnerException ?? e).Message); }
            }
        }

        private static int CallSquareAt(object table, Vector3 p)
        {
            return _squareAt.Invoke(table, new object[] { p }) is int i ? i : -1;
        }

        private static Vector3 Local(object table, int sq) =>
            _squareLocal.Invoke(table, new object[] { sq }) is Vector3 v ? v : Vector3.zero;

        // Board plane in world space from squares 0, 7 and 56 (SquareLocal is table-local).
        private static bool Board(Component table, out Plane plane)
        {
            plane = default;
            var tr = table.transform;
            var a = tr.TransformPoint(Local(table, 0));
            var b = tr.TransformPoint(Local(table, 7));
            var c = tr.TransformPoint(Local(table, 56));
            var n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 1e-10f) return false;
            plane = new Plane(n.normalized, a);
            if (table.Pointer != _probedPtr)
            {
                _probedPtr = table.Pointer;
                int w0 = CallSquareAt(table, a), l0 = CallSquareAt(table, Local(table, 0));
                int w7 = CallSquareAt(table, b), l7 = CallSquareAt(table, Local(table, 7));
                _space = (w0 == 0 && w7 == 7) ? 1 : (l0 == 0 && l7 == 7) ? 2 : 0;
                Plugin.L.LogInfo($"[ChessPointer] board probe: world gives {w0},{w7}; local gives {l0},{l7} -> {(_space == 1 ? "world" : _space == 2 ? "local" : "unknown")}; square0 at {a}");
            }
            return _space != 0;
        }

        private static int SquareUnderLaser(Component table, out Vector3 hit)
        {
            hit = Vector3.zero;
            if (!MotionControls.TryGetHandAim(out var pos, out var rot)) return -1;
            if (!Board(table, out var plane)) return -1;
            var dir = rot * Vector3.forward;
            if (!plane.Raycast(new Ray(pos, dir), out float dist) || dist <= 0f) return -1;
            hit = pos + dir * dist;
            int sq = CallSquareAt(table, _space == 1 ? hit : table.transform.InverseTransformPoint(hit));
            return sq >= 0 && sq <= 63 ? sq : -1;
        }

        // Where the virtual mouse last pointed (screen space), and whether it has been used.
        private static Vector2 _mousePos;
        private static bool _mouseHasPos, _mouseWasActive, _mouseDown;

        // The game ignores the "hovered" we set and works its square out from its own mouse inside
        // Input(); so give it a mouse that follows the beam. The event is queued, so it lands next
        // frame - fine for hovering and clicking.
        private static void DriveMouse(Component table)
        {
            Vector3 hit;
            int sq = SquareUnderLaser(table, out hit);
            bool down = MotionControls.RawRightTrigger > 0.6f;
            bool sent = false;
            if (sq >= 0)
            {
                var cam = Camera.main;
                if (cam == null) return;
                var sp = cam.WorldToScreenPoint(hit);
                if (sp.z <= 0f) return;
                _mousePos = new Vector2(sp.x, sp.y);
                _mouseHasPos = true;
                VirtualMouse.Send(_mousePos, down);
                sent = true;
                if (!_loggedMouse) { _loggedMouse = true; Plugin.L.LogInfo($"[ChessPointer] virtual mouse at ({_mousePos.x:F0},{_mousePos.y:F0}) for square {sq}"); }
            }
            else if (_mouseHasPos)
            {
                // Laser off the board: keep the mouse where it was so a held click can still release.
                VirtualMouse.Send(_mousePos, down);
                sent = true;
            }
            if (down && !_mouseDown)
            {
                _mouseDown = true;
                if (!_loggedClick) { _loggedClick = true; Plugin.L.LogInfo($"[ChessPointer] right trigger clicks the virtual mouse at ({_mousePos.x:F0},{_mousePos.y:F0})"); }
            }
            else if (!down) _mouseDown = false;
            if (sent) _mouseWasActive = true;
        }

        private static void StopMouse()
        {
            if (!_mouseWasActive || !_mouseHasPos) { _mouseWasActive = false; return; }
            _mouseWasActive = false;
            _mouseDown = false;
            VirtualMouse.Send(_mousePos, false);
        }

        private static void Pre(object __instance)
        {
            _wanted = -1;
            try
            {
                bool active = HeadTracking.ChessActive && Plugin.MotionControlsEnabled.Value;
                if (!active)
                {
                    // Chess view ended: release the virtual mouse's button once.
                    if (_mouseWasActive) StopMouse();
                    return;
                }
                if (!_loggedRun) { _loggedRun = true; Plugin.L.LogInfo("[ChessPointer] chess input running with the seated view"); }
                if (!(__instance is Component table) || _squareAt == null || _squareLocal == null) return;
                DriveMouse(table);
                if (_hovered == null) return;
                int sq = SquareUnderLaser(table, out _);
                if (sq < 0) return;
                _wanted = sq;
                _hovered.SetValue(__instance, sq);
                if (!_loggedHover) { _loggedHover = true; Plugin.L.LogInfo($"[ChessPointer] hovering square {sq}"); }
            }
            catch (Exception e)
            {
                if (!_loggedError) { _loggedError = true; Plugin.L.LogWarning("[ChessPointer] error: " + (e.InnerException ?? e).Message); }
            }
        }

        // The game may work out its own hover inside Input(); keep the laser's square afterwards too,
        // so the highlight shows where you point.
        private static void Post(object __instance)
        {
            try
            {
                if (_wanted < 0 || _hovered == null) return;
                int now = _hovered.GetValue(__instance) is int h ? h : -1;
                if (now != _wanted)
                {
                    if (!_loggedOverwrite) { _loggedOverwrite = true; Plugin.L.LogInfo($"[ChessPointer] game changed hover {_wanted} -> {now} during its input; putting the laser's square back"); }
                    _hovered.SetValue(__instance, _wanted);
                }
            }
            catch { }
        }

        /// <summary>For the laser: distance to the chess board if the beam lands on a square.</summary>
        internal static bool TryClip(Vector3 p, Vector3 dir, out float dist)
        {
            dist = 0f;
            try
            {
                var table = HeadTracking.ChessTable as Component;
                if (table == null || !HeadTracking.ChessActive || _squareAt == null || _squareLocal == null) return false;
                if (!Board(table, out var plane)) return false;
                if (!plane.Raycast(new Ray(p, dir), out dist) || dist <= 0f) return false;
                var hit = p + dir * dist;
                int sq = CallSquareAt(table, _space == 1 ? hit : table.transform.InverseTransformPoint(hit));
                if (sq < 0 || sq > 63) { dist = 0f; return false; }
                return true;
            }
            catch { dist = 0f; return false; }
        }
    }
}
