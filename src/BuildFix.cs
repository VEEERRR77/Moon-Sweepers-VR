// Build mode (MoonDepotBuilder):
//  - the build menu works with the laser: point at a card and press A to choose it;
//  - while placing a structure, laying a conveyor or moving something, the game's aim comes from
//    your right hand (like the brushes), so the ghost slides along the ground where you point and
//    a conveyor's far end follows your hand.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class BuildFix
    {
        private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        internal static bool Hovering;
        private static int _hoverKind = int.MinValue;
        private static PropertyInfo _scroll, _cardEntry, _entryKind;
        private static MethodInfo _selectStructure, _beginPlacement, _cancelPlacement;
        private static PropertyInfo _buildId, _ghostKind;
        private static int _chosenKind = int.MinValue, _chosenFrame = -1000, _fixLogs, _lastSel = int.MinValue;
        private static bool _wasMenu;
        private static PropertyInfo _selected, _menu, _window, _cards, _placing, _conveyor, _move, _cardRect, _cardButton;
        private static int _openFrame = -100;
        internal static bool Open => Time.frameCount - _openFrame <= 2;
        private static RectTransform _panel;
        private static bool _aHeld, _swapUpdate, _swapLate, _loggedErr, _loggedClick, _loggedAim, _loggedMenu, _loggedMiss;
        private static int _aFrame = -1; private static bool _aDown;
        private static int _lastState = -1, _stateLogs, _clickLogs, _missLogs;

        public static void Install(Harmony h)
        {
            try
            {
                var t = AccessTools.TypeByName("MoonCleaners.MoonDepotBuilder");
                var upd = t != null ? AccessTools.Method(t, "Update") : null;
                var late = t != null ? AccessTools.Method(t, "LateUpdate") : null;
                if (upd == null) { Plugin.L.LogWarning("Build: builder not found"); return; }
                _menu = AccessTools.Property(t, "menu");
                _window = AccessTools.Property(t, "window");
                _cards = AccessTools.Property(t, "cards");
                _placing = AccessTools.Property(t, "placing");
                _conveyor = AccessTools.Property(t, "conveyorMode");
                _move = AccessTools.Property(t, "moveMode");
                _selected = AccessTools.Property(t, "selectedStructure");
                _scroll = AccessTools.Property(t, "catalogScroll");
                _selectStructure = AccessTools.Method(t, "SelectStructure", new[] { typeof(int) });
                _beginPlacement = AccessTools.Method(t, "BeginPlacement", Type.EmptyTypes);
                _cancelPlacement = AccessTools.Method(t, "CancelPlacement", Type.EmptyTypes);
                _buildId = AccessTools.Property(t, "selectedBuildId");
                _ghostKind = AccessTools.Property(t, "ghostKind");
                var cardT = AccessTools.TypeByName("MoonCleaners.MoonDepotBuilder+Card");
                if (cardT == null) foreach (var nt in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) if (nt.Name == "Card") cardT = nt;
                if (cardT != null)
                {
                    _cardRect = AccessTools.Property(cardT, "rect"); _cardButton = AccessTools.Property(cardT, "button");
                    _cardEntry = AccessTools.Property(cardT, "entry");
                    if (_cardEntry != null) _entryKind = AccessTools.Property(_cardEntry.PropertyType, "kind");
                }
                h.Patch(upd,
                    prefix: new HarmonyMethod(typeof(BuildFix).GetMethod(nameof(UpdPre), BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(BuildFix).GetMethod(nameof(UpdPost), BindingFlags.Static | BindingFlags.NonPublic)));
                if (late != null)
                    h.Patch(late,
                        prefix: new HarmonyMethod(typeof(BuildFix).GetMethod(nameof(LatePre), BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(BuildFix).GetMethod(nameof(LatePost), BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.L.LogMessage($"Build: laser menu and hand placement installed (cards {_cards != null && _cardRect != null}, select {_selectStructure != null && _entryKind != null}, placing {_placing != null})");
            }
            catch (Exception e) { Plugin.L.LogError("Build: install failed: " + e); }
        }

        private static bool Flag(PropertyInfo p, object inst) { try { return p != null && p.GetValue(inst) is bool b && b; } catch { return false; } }

        private static bool Placing(object inst) => Flag(_placing, inst) || Flag(_conveyor, inst) || Flag(_move, inst);

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

        internal static bool TryClip(Vector3 p, Vector3 dir, out float dist)
        {
            dist = 0f;
            try { return Open && _panel != null && _panel.gameObject.activeInHierarchy && PlaneHit(_panel, p, dir, out var pt, out dist) && InRect(_panel, pt); }
            catch { return false; }
        }

        private static bool UpdPre(object __instance)
        {
            _swapUpdate = false;
            if (!Open) { Hovering = false; _hoverKind = int.MinValue; }
            if (__instance == null || !Plugin.MotionControlsEnabled.Value) return true;
            try
            {
                if (_aFrame != Time.frameCount)
                {
                    _aFrame = Time.frameCount;
                    // In the build menu the RIGHT TRIGGER selects (A is left to the game).
                    bool aNow = MotionControls.RawRightTrigger > 0.6f;
                    _aDown = aNow && !_aHeld; _aHeld = aNow;
                }

                // Menu first: the game can count as "placing" while its menu is up.
                var menu = _menu?.GetValue(__instance) as GameObject;
                var window = _window?.GetValue(__instance) as RectTransform;
                bool menuActive = menu != null && menu.activeInHierarchy;
                bool windowActive = window != null && window.gameObject.activeInHierarchy;
                bool menuOpen = menu != null ? menuActive && (window == null || windowActive) : windowActive;
                int state = (menuActive ? 1 : 0) | (windowActive ? 2 : 0) | (Placing(__instance) ? 4 : 0);
                if (state != _lastState && _stateLogs < 12) { _stateLogs++; _lastState = state; Plugin.L.LogInfo($"Build: menu {menuActive}, window {windowActive}, placing {Placing(__instance)}"); }
                if (!menuOpen)
                {
                    // Menu just closed into placing: check the piece is the one that was selected.
                    if (_wasMenu && Placing(__instance) && _lastSel != int.MinValue) { _chosenKind = _lastSel; FixGhost(__instance); }
                    _wasMenu = false;
                    if (Placing(__instance))
                    {
                        MotionControls.HandAimBegin(); _swapUpdate = true;
                        if (!_loggedAim) { _loggedAim = true; Plugin.L.LogInfo("Build: placing from the right hand"); }
                    }
                    return true;
                }
                _openFrame = Time.frameCount;
                _panel = window;
                _wasMenu = true;
                try { _lastSel = Convert.ToInt32(_selected.GetValue(__instance)); } catch { _lastSel = int.MinValue; }
                if (!_loggedMenu) { _loggedMenu = true; Plugin.L.LogInfo($"Build: menu open (window {window != null})"); }
                if (!Plugin.BuildMenuLaser.Value) return true;   // menu driven by the left stick (game's own), trigger = A
                // Get the hand first: that also reads the controllers, which looks at Hovering, so it
                // must still hold last frame's answer at that point.
                if (!MotionControls.TryGetHandAim(out var hp, out var hr)) return true;
                Hovering = false;
                if (_cards == null || _cardRect == null) return true;
                Vector3 dir = hr * Vector3.forward;

                // Only cards showing in the scroll window count (scrolled-away ones still exist).
                RectTransform viewport = null;
                try { var sr = _scroll?.GetValue(__instance) as UnityEngine.UI.ScrollRect; if (sr != null) viewport = sr.viewport; } catch { }
                if (viewport != null && (!PlaneHit(viewport, hp, dir, out var vp, out _) || !InRect(viewport, vp))) { _hoverKind = int.MinValue; return true; }

                var list = _cards.GetValue(__instance);
                if (list == null) return true;
                var lt = list.GetType();
                int count = Convert.ToInt32(lt.GetProperty("Count", F).GetValue(list));
                var item = lt.GetMethod("get_Item", F);
                for (int i = 0; i < count; i++)
                {
                    var c = item.Invoke(list, new object[] { i });
                    var rect = c != null ? _cardRect.GetValue(c) as RectTransform : null;
                    if (rect == null || !rect.gameObject.activeInHierarchy) continue;
                    if (!PlaneHit(rect, hp, dir, out var pt, out _) || !InRect(rect, pt)) continue;
                    Hovering = true;
                    // Pointing at a card selects it, the same as moving onto it with the stick.
                    var entry = _cardEntry?.GetValue(c);
                    if (entry != null && _entryKind != null && _selectStructure != null)
                    {
                        int kind = Convert.ToInt32(_entryKind.GetValue(entry));
                        if (kind != _hoverKind)
                        {
                            _hoverKind = kind;
                            object before = _selected?.GetValue(__instance);
                            _selectStructure.Invoke(__instance, new object[] { kind });
                            if (_clickLogs++ < 40) Plugin.L.LogInfo($"Build: pointing at card {i} (kind {kind}), selected {before} -> {_selected?.GetValue(__instance)}");
                        }
                    }
                    if (_aDown) { _chosenKind = _hoverKind; _chosenFrame = Time.frameCount; }
                    return true;
                }
                _hoverKind = int.MinValue;
            }
            catch (Exception e)
            {
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Build: " + (e.InnerException ?? e)); }
            }
            return true;
        }

        // The game sometimes keeps placing the piece you placed last time, even though you chose a
        // different one. If the piece being placed isn't the one just chosen, start again with it.
        private static void FixGhost(object inst)
        {
            int kind = _chosenKind; _chosenKind = int.MinValue;
            try
            {
                int id = _buildId != null ? Convert.ToInt32(_buildId.GetValue(inst)) : kind;
                int ghost = _ghostKind != null ? Convert.ToInt32(_ghostKind.GetValue(inst)) : kind;
                bool wrong = id != kind;
                if (_fixLogs++ < 10) Plugin.L.LogInfo($"Build: chose {kind}, placing id {id}, ghost {ghost}{(wrong ? " - wrong piece, restarting placement" : "")}");
                if (!wrong || _beginPlacement == null || _selectStructure == null) return;
                _cancelPlacement?.Invoke(inst, null);
                _selectStructure.Invoke(inst, new object[] { kind });
                _beginPlacement.Invoke(inst, null);
                if (_fixLogs < 12) Plugin.L.LogInfo($"Build: now placing id {_buildId?.GetValue(inst)}, ghost {_ghostKind?.GetValue(inst)}, placing {Placing(inst)}");
            }
            catch (Exception e) { Plugin.L.LogWarning("Build: couldn't fix the chosen piece: " + (e.InnerException ?? e).Message); }
        }

        private static void UpdPost() { if (_swapUpdate) { _swapUpdate = false; MotionControls.HandAimEnd(); } }

        private static void LatePre(object __instance)
        {
            _swapLate = false;
            if (__instance == null || !Plugin.MotionControlsEnabled.Value) return;
            if (Open) return;   // menu up: leave the camera alone
            if (Placing(__instance)) { MotionControls.HandAimBegin(); _swapLate = true; }
        }

        private static void LatePost() { if (_swapLate) { _swapLate = false; MotionControls.HandAimEnd(); } }
    }
}
