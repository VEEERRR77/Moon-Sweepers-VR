// "VR Controls" and "VR Settings" next to the game's pause menu. While the pause menu is up, two
// buttons float on its right-hand side; point the laser and pull the right trigger. VR Controls
// lists every VR button combo; VR Settings changes the mod's main settings live (saved to the
// config straight away).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class VrMenu
    {
        private enum View { Buttons, Controls, Settings }
        private class Btn { public RectTransform Rect; public UnityEngine.UI.Image Bg; public TMPro.TextMeshProUGUI Label; public Action Act; public View On; }
        private class Row { public string Name; public Func<string> Value; public Action Down, Up; }

        private static GameObject _panel;
        private static RectTransform _rect;
        private static UnityEngine.UI.Image _bg;
        private static readonly Dictionary<View, RectTransform> _views = new Dictionary<View, RectTransform>();
        private static View _view = View.Buttons;
        private static readonly List<Btn> _btns = new List<Btn>();
        private static readonly List<Row> _rows = new List<Row>();
        private static readonly List<(TMPro.TextMeshProUGUI name, TMPro.TextMeshProUGUI value, Btn down, Btn up)> _rowUi = new List<(TMPro.TextMeshProUGUI, TMPro.TextMeshProUGUI, Btn, Btn)>();
        private static TMPro.TextMeshProUGUI _pageLabel;
        private static int _page;
        private const int PerPage = 8;
        private static Btn _hover;
        private static bool _wasDown, _failed, _loggedErr, _laserOn;
        private static int _shownFrame = -100;
        internal static bool Shown => Time.frameCount - _shownFrame <= 2;
        internal static bool Hovering => Shown && _laserOn;

        private static readonly Color PanelColour = new Color(0.08f, 0.09f, 0.11f, 0.92f);
        private static readonly Color ButtonColour = new Color(0.20f, 0.22f, 0.26f, 1f);
        private static readonly Color HoverColour = new Color(1f, 0.85f, 0.2f, 1f);

        // ------------------------------------------------------------------ settings list
        private static string OnOff(bool b) => b ? "On" : "Off";
        private static void Step(BepInEx.Configuration.ConfigEntry<float> e, float d, float min, float max)
            => e.Value = Mathf.Round(Mathf.Clamp(e.Value + d, min, max) * 100f) / 100f;
        private static string Cycle(string cur, string[] all, int dir)
        {
            int i = Array.FindIndex(all, a => string.Equals(a, (cur ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            return all[((i < 0 ? 0 : i) + dir + all.Length) % all.Length];
        }

        private static void Add(string name, Func<string> value, Action down, Action up) => _rows.Add(new Row { Name = name, Value = value, Down = down, Up = up });
        private static void AddToggle(string name, BepInEx.Configuration.ConfigEntry<bool> e) => Add(name, () => OnOff(e.Value), () => e.Value = !e.Value, () => e.Value = !e.Value);
        private static void AddNumber(string name, BepInEx.Configuration.ConfigEntry<float> e, float step, float min, float max, string unit)
            => Add(name, () => e.Value.ToString(step < 0.1f ? "0.00" : "0.0") + unit, () => Step(e, -step, min, max), () => Step(e, step, min, max));

        private static void BuildRows()
        {
            _rows.Clear();
            AddToggle("Laser pointer", Plugin.LaserPointer);
            Add("Laser colour", () => Plugin.LaserColour.Value, () => Laser.CycleColour(-1), () => Laser.CycleColour(1));
            Add("Laser brightness", () => Mathf.RoundToInt(Plugin.LaserBrightness.Value * 100f) + "%", () => Laser.ChangeBrightness(-1), () => Laser.ChangeBrightness(1));
            Add("HUD", () => Plugin.HudMode.Value, () => UiToWorld.ToggleHud(), () => UiToWorld.ToggleHud());
            string[] hands = { "Left", "Right", "Off" };
            Add("Wrist HUD hand", () => Plugin.HandHud.Value, () => Plugin.HandHud.Value = Cycle(Plugin.HandHud.Value, hands, -1), () => Plugin.HandHud.Value = Cycle(Plugin.HandHud.Value, hands, 1));
            Add("View height", () => Plugin.HeadHeightOffset.Value.ToString("0.00") + " m", () => HeadTracking.ChangeHeight(-0.05f), () => HeadTracking.ChangeHeight(0.05f));
            Add("Recenter view", () => "", () => HeadTracking.Recenter(), () => HeadTracking.Recenter());
            AddNumber("Menu distance", Plugin.UiDistance, 0.1f, 0.8f, 4f, " m");

            AddToggle("Thumb-rest D-pad", Plugin.ThumbRestDpad);
            AddNumber("Thumb-rest hold", Plugin.ThumbRestHold, 0.5f, 0f, 5f, " s");
            AddToggle("Wrist radio", Plugin.RadioEnabled);
            AddNumber("Radio volume", Plugin.RadioVolume, 0.05f, 0f, 1f, "");
            AddToggle("Hide brush reticle", Plugin.HideReticle);
            AddToggle("Coffee cup in hand", Plugin.CoffeeCupInHand);
            AddNumber("Garage menu size", Plugin.GarageMenuSize, 0.1f, 0.5f, 3f, "x");
            AddNumber("Build menu width", Plugin.BuildMenuWidth, 0.1f, 1f, 5f, " m");

            AddToggle("Air hockey VR view", Plugin.HockeyKeepView);
            AddToggle("Chess seated view", Plugin.ChessSeatedView);
            AddToggle("Player name tags", Plugin.NameTagsEnabled);
            AddToggle("Full-view loading screens", Plugin.FullViewLoading);
            AddToggle("Check for updates", Plugin.CheckForUpdates);
            AddToggle("Performance log", Plugin.PerfLog);
        }

        private const string ControlsText =
            "<b>Quest / Touch controllers</b>\n" +
            "Right hand: aim the tool, highlight items and machines (laser shows where)\n" +
            "Left stick: move      Right stick: turn and look up / down\n" +
            "Right trigger: sweep / click VR menus      Grips: LB / RB\n" +
            "A, B, X, Y: as on a gamepad (A = jump)\n" +
            "Right grip + B: Start (pause)      Left grip + Y: Select\n" +
            "Right thumb on the thumb rest (hold): left stick becomes the D-pad\n" +
            "\n<b>Hold BOTH grips and...</b>\n" +
            "A (or click both sticks): recenter the view\n" +
            "Right stick up / down: raise / lower your view\n" +
            "Right stick left / right: laser colour\n" +
            "Left stick left / right: laser brightness\n" +
            "Left stick up / down: held-tool height\n" +
            "X: laser on / off      Y: wrist radio\n" +
            "Click left stick: HUD (full / hand only / none)\n" +
            "\n<b>Menus</b>: point the laser and press A (map, supply wall, upgrades)\n" +
            "or pull the right trigger (build menu, garage, chess, radio, this menu)";

        // ------------------------------------------------------------------ building the panel
        private static TMPro.TMP_FontAsset _font;
        private static TMPro.TMP_FontAsset GetFont()
        {
            if (_font != null) return _font;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<TMPro.TMP_FontAsset>()))
            {
                var f = o.TryCast<TMPro.TMP_FontAsset>();
                if (f != null) { _font = f; break; }
            }
            return _font;
        }

        private static RectTransform Rect(Transform parent, string name, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            go.layer = 5;
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            return rt;
        }

        private static TMPro.TextMeshProUGUI Text(Transform parent, Vector2 pos, Vector2 size, float fontSize, string text, TMPro.TextAlignmentOptions align = TMPro.TextAlignmentOptions.Center)
        {
            var rt = Rect(parent, "Text", pos, size);
            var t = rt.gameObject.AddComponent<TMPro.TextMeshProUGUI>();
            var f = GetFont(); if (f != null) t.font = f;
            t.fontSize = fontSize; t.alignment = align; t.color = Color.white; t.raycastTarget = false;
            t.text = text;
            return t;
        }

        private static Btn Button(View view, string label, Vector2 pos, Vector2 size, Action act, float fontSize = 30f)
        {
            var rt = Rect(_views[view], "Button " + label, pos, size);
            var img = rt.gameObject.AddComponent<UnityEngine.UI.Image>();
            img.color = ButtonColour; img.raycastTarget = false;
            var t = Text(rt, Vector2.zero, size, fontSize, label);
            var b = new Btn { Rect = rt, Bg = img, Label = t, Act = act, On = view };
            _btns.Add(b);
            return b;
        }

        private static void Show(View v)
        {
            _view = v;
            foreach (var kv in _views) kv.Value.gameObject.SetActive(kv.Key == v);
            _rect.sizeDelta = v == View.Buttons ? new Vector2(700f, 230f) : new Vector2(900f, 760f);
            _page = 0;
        }

        private static void Build()
        {
            BuildRows();
            _panel = new GameObject("MoonXr VR menu");   // "MoonXr" so the pink-object fix leaves it alone
            UnityEngine.Object.DontDestroyOnLoad(_panel);
            _panel.layer = 5;
            var canvas = _panel.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 3300;
            _rect = _panel.GetComponent<RectTransform>();
            _bg = _panel.AddComponent<UnityEngine.UI.Image>();
            _bg.color = PanelColour; _bg.raycastTarget = false;
            foreach (View v in Enum.GetValues(typeof(View)))
            {
                var g = Rect(_rect, v + " view", Vector2.zero, new Vector2(900f, 760f));
                _views[v] = g;
            }

            Button(View.Buttons, "VR Controls", new Vector2(0f, 55f), new Vector2(620f, 90f), () => Show(View.Controls), 40f);
            Button(View.Buttons, "VR Settings", new Vector2(0f, -55f), new Vector2(620f, 90f), () => Show(View.Settings), 40f);

            var c = _views[View.Controls];
            Text(c, new Vector2(0f, 340f), new Vector2(860f, 50f), 40f, "<b>VR Controls</b>");
            Text(c, new Vector2(0f, 0f), new Vector2(860f, 600f), 24f, ControlsText, TMPro.TextAlignmentOptions.TopLeft);
            Button(View.Controls, "Back", new Vector2(0f, -330f), new Vector2(260f, 70f), () => Show(View.Buttons));

            var s = _views[View.Settings];
            Text(s, new Vector2(0f, 340f), new Vector2(860f, 50f), 40f, "<b>VR Settings</b>");
            for (int i = 0; i < PerPage; i++)
            {
                int slot = i;
                float y = 260f - i * 68f;
                var name = Text(s, new Vector2(-170f, y), new Vector2(480f, 60f), 28f, "", TMPro.TextAlignmentOptions.Left);
                var value = Text(s, new Vector2(245f, y), new Vector2(200f, 60f), 28f, "");
                var dn = Button(View.Settings, "<", new Vector2(115f, y), new Vector2(64f, 58f), () => RowAct(slot, false));
                var up = Button(View.Settings, ">", new Vector2(375f, y), new Vector2(64f, 58f), () => RowAct(slot, true));
                _rowUi.Add((name, value, dn, up));
            }
            Button(View.Settings, "< Prev", new Vector2(-300f, -330f), new Vector2(200f, 70f), () => { if (_page > 0) _page--; });
            Button(View.Settings, "Back", new Vector2(0f, -330f), new Vector2(240f, 70f), () => Show(View.Buttons));
            Button(View.Settings, "Next >", new Vector2(300f, -330f), new Vector2(200f, 70f), () => { if ((_page + 1) * PerPage < _rows.Count) _page++; });
            _pageLabel = Text(s, new Vector2(0f, -275f), new Vector2(400f, 36f), 22f, "");
            _pageLabel.color = new Color(0.75f, 0.75f, 0.78f, 1f);

            Show(View.Buttons);
            Plugin.L.LogInfo("VR menu: built");
        }

        private static void RowAct(int slot, bool up)
        {
            int i = _page * PerPage + slot;
            if (i >= _rows.Count) return;
            try { (up ? _rows[i].Up : _rows[i].Down)?.Invoke(); }
            catch (Exception e) { Plugin.L.LogWarning($"VR menu: '{_rows[i].Name}' failed: {e.Message}"); }
        }

        private static void Refresh()
        {
            _bg.rectTransform.sizeDelta = _rect.sizeDelta;
            if (_view != View.Settings) return;
            for (int k = 0; k < PerPage; k++)
            {
                int i = _page * PerPage + k;
                var ui = _rowUi[k];
                bool has = i < _rows.Count;
                ui.name.text = has ? _rows[i].Name : "";
                string v = "";
                if (has) { try { v = _rows[i].Value() ?? ""; } catch { v = "?"; } }
                ui.value.text = v;
                ui.down.Rect.gameObject.SetActive(has);
                ui.up.Rect.gameObject.SetActive(has);
            }
            int pages = (_rows.Count + PerPage - 1) / PerPage;
            _pageLabel.text = $"Page {_page + 1} of {pages}  -  changes are saved straight away";
        }

        // ------------------------------------------------------------------ per frame
        private static bool Hit(Vector3 p, Vector3 dir, out Vector3 point, out float dist)
        {
            point = default; dist = 0f;
            if (_rect == null || !Shown) return false;
            var plane = new Plane(_rect.forward, _rect.position);
            if (!plane.Raycast(new Ray(p, dir), out dist) || dist <= 0f) return false;
            point = p + dir * dist;
            Vector3 l = _rect.InverseTransformPoint(point);
            return _rect.rect.Contains(new Vector2(l.x, l.y));
        }

        internal static bool TryClip(Vector3 p, Vector3 dir, out float dist)
        {
            dist = 0f;
            try { return Hit(p, dir, out _, out dist); } catch { return false; }
        }

        /// <summary>Called every frame just before the UI is drawn.</summary>
        internal static void Tick()
        {
            if (_failed) return;
            try
            {
                var pause = UiToWorld.PauseCanvas;
                bool show = pause != null && Plugin.MotionControlsEnabled.Value;
                if (!show)
                {
                    if (_panel != null && _panel.activeSelf) { _panel.SetActive(false); Show(View.Buttons); }
                    _laserOn = false;
                    return;
                }
                if (_panel == null) Build();
                if (!_panel.activeSelf) _panel.SetActive(true);
                _shownFrame = Time.frameCount;

                // On the right-hand side of the pause screen, a touch in front of it.
                var pt = pause.transform;
                var prt = pt.TryCast<RectTransform>();
                float pw = (prt != null ? prt.rect.width : 1920f) * pt.lossyScale.x;
                float width = pw * (_view == View.Buttons ? 0.3f : 0.45f);
                float s = width / _rect.sizeDelta.x;
                Vector3 pos = pt.position + pt.right * (pw * 0.25f) - pt.forward * 0.02f;
                _rect.SetPositionAndRotation(pos, pt.rotation);
                _rect.localScale = new Vector3(s, s, s);
                Refresh();

                _hover = null; _laserOn = false;
                if (MotionControls.TryGetHandAim(out var ap, out var ar) && Hit(ap, ar * Vector3.forward, out var hitPt, out _))
                {
                    _laserOn = true;
                    foreach (var b in _btns)
                    {
                        if (b.On != _view || !b.Rect.gameObject.activeInHierarchy) continue;
                        Vector3 l = b.Rect.InverseTransformPoint(hitPt);
                        if (b.Rect.rect.Contains(new Vector2(l.x, l.y))) { _hover = b; break; }
                    }
                }
                foreach (var b in _btns)
                {
                    if (b.On != _view) continue;
                    bool h = b == _hover;
                    b.Bg.color = h ? HoverColour : ButtonColour;
                    b.Label.color = h ? Color.black : Color.white;
                }
                bool down = MotionControls.RawRightTrigger > 0.6f;
                if (down && !_wasDown && _hover != null) _hover.Act();
                _wasDown = down;
            }
            catch (Exception e)
            {
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("VR menu: " + e); }
                _failed = true;
                try { if (_panel != null) _panel.SetActive(false); } catch { }
            }
        }
    }
}
