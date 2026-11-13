// Splits the game's main HUD up for VR:
//   - the dark "HUD shade" band behind it is hidden;
//   - the tool belt stays at the bottom of the panel in front of you;
//   - the dust/inventory panel, money and notices move onto the wrist screen, in a row.
// The game keeps driving those pieces as normal; we just re-home and line them up each frame.
using System;
using System.Collections.Generic;
using MoonCleaners;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class HudLayout
    {
        private class Moved
        {
            public RectTransform T;
            public Transform OldParent;
            public Vector2 AnchorMin, AnchorMax, Pivot, Pos;
            public int SiblingIndex;
        }

        private static MoonPlayerHud _hud;
        private static float _nextFind;
        private static bool _loggedFound, _loggedErr;
        private static readonly List<Moved> _moved = new List<Moved>();
        private const float Gap = 40f;

        private static bool Alive(UnityEngine.Object o)
        {
            try { return o != null && !o.WasCollected; } catch { return false; }
        }

        private static MoonPlayerHud FindHud()
        {
            if (Alive(_hud)) return _hud;
            if (Time.unscaledTime < _nextFind) return null;
            _nextFind = Time.unscaledTime + 2f;
            _moved.Clear();   // a new HUD was built (e.g. after loading)
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MoonPlayerHud>()))
            {
                var h = o.TryCast<MoonPlayerHud>();
                if (h == null || !h.gameObject.scene.IsValid()) continue;
                _hud = h;
                if (!_loggedFound) { _loggedFound = true; Plugin.L.LogMessage("HUD: found the player HUD (shade hidden, wrist layout available)"); }
                return h;
            }
            return null;
        }

        private static Moved Remember(RectTransform t)
        {
            foreach (var m in _moved) if (m.T.Pointer == t.Pointer) return m;
            var n = new Moved
            {
                T = t, OldParent = t.parent, AnchorMin = t.anchorMin, AnchorMax = t.anchorMax,
                Pivot = t.pivot, Pos = t.anchoredPosition, SiblingIndex = t.GetSiblingIndex()
            };
            _moved.Add(n);
            return n;
        }

        private static void RestoreAll()
        {
            foreach (var m in _moved)
            {
                try
                {
                    if (!Alive(m.T)) continue;
                    if (Alive(m.OldParent) && m.T.parent != null && m.T.parent.Pointer != m.OldParent.Pointer)
                    {
                        m.T.SetParent(m.OldParent, false);
                        m.T.SetSiblingIndex(m.SiblingIndex);
                    }
                    m.T.anchorMin = m.AnchorMin; m.T.anchorMax = m.AnchorMax; m.T.pivot = m.Pivot; m.T.anchoredPosition = m.Pos;
                }
                catch { }
            }
            _moved.Clear();
        }

        /// <param name="wrist">The canvas that is shown on the wrist, or null if the wrist HUD is off.</param>
        public static void Apply(Canvas wrist)
        {
            try
            {
                var hud = FindHud();
                if (hud == null) return;

                // Always hide the dark band behind the HUD.
                var shade = hud.shade;
                if (Alive(shade)) shade.rectTransform.localScale = Vector3.zero;

                // Tool belt size (it shrinks towards its own anchor at the bottom of the view).
                var bar = hud.toolBar;
                if (Alive(bar)) bar.localScale = Vector3.one * Mathf.Clamp(Plugin.ToolbarScale.Value, 0.3f, 2f);

                if (wrist == null) { if (_moved.Count > 0) RestoreAll(); return; }

                var items = new List<RectTransform>();
                foreach (var t in new[] { hud.dustPanel, hud.walletPanel, hud.fullTag })
                    if (Alive(t)) items.Add(t);
                try { var n = hud.notice; if (Alive(n)) { var nt = n.transform.TryCast<RectTransform>(); if (nt != null) items.Add(nt); } } catch { }

                var parent = wrist.transform;
                float total = 0f; int shown = 0;
                foreach (var t in items)
                {
                    Remember(t);
                    if (t.parent == null || t.parent.Pointer != parent.Pointer) t.SetParent(parent, false);
                    if (!t.gameObject.activeSelf) continue;
                    total += t.rect.width * Mathf.Abs(t.localScale.x);
                    shown++;
                }
                if (shown == 0) return;
                total += Gap * (shown - 1);

                // Lay the visible pieces out left to right, centred on the wrist screen.
                float x = -total * 0.5f;
                var mid = new Vector2(0.5f, 0.5f);
                foreach (var t in items)
                {
                    if (!t.gameObject.activeSelf) continue;
                    float w = t.rect.width * Mathf.Abs(t.localScale.x);
                    float h = t.rect.height * Mathf.Abs(t.localScale.y);
                    t.anchorMin = mid; t.anchorMax = mid;
                    var p = t.pivot;
                    t.anchoredPosition = new Vector2(x + w * p.x, (p.y - 0.5f) * h);
                    x += w + Gap;
                }
            }
            catch (Exception e)
            {
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("HUD layout error: " + e); }
            }
        }
    }
}
