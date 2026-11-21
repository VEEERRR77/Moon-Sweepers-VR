// Converts the game's screen-space (overlay) canvases into a world-space panel that floats
// in front of the game camera, so menus and HUD are visible in VR. UUVR's own UI system
// crashes on this Unity version, so this replaces it.
using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace MoonXrStarter
{
    public class UiToWorld : MonoBehaviour
    {
        public UiToWorld(IntPtr ptr) : base(ptr) { }

        private class Tracked
        {
            public Canvas Canvas;
            public float BaseScale;
            public int Rank;
            public bool OnHand;
            public string Name;
        }

        private static string _hudRaw;
        private static readonly HashSet<string> _hudNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool IsHudScreen(string name)
        {
            string raw = Plugin.HudScreens.Value ?? "";
            if (raw != _hudRaw)
            {
                _hudRaw = raw;
                _hudNames.Clear();
                foreach (var part in raw.Split(',')) { var x = part.Trim(); if (x.Length > 0) _hudNames.Add(x); }
            }
            return name != null && _hudNames.Contains(name);
        }

        // A shrunk screen pulls its contents in towards the middle of your view. Slide it back so
        // its contents sit exactly where they would at full size (compass stays at the top).
        private static void KeepAtFullSizeSpot(Transform canvas, float k)
        {
            if (k <= 0.01f || Mathf.Approximately(k, 1f)) return;
            try
            {
                Vector3 sum = Vector3.zero; int n = 0;
                for (int i = 0; i < canvas.childCount; i++)
                {
                    var c = canvas.GetChild(i);
                    if (c == null || !c.gameObject.activeSelf) continue;
                    var rt = c.TryCast<RectTransform>();
                    if (rt == null) continue;
                    sum += rt.TransformPoint(rt.rect.center); n++;
                }
                if (n == 0) return;
                Vector3 d = sum / n - canvas.position;          // contents offset at the shrunk size
                canvas.position += d * ((1f - k) / k);          // = offset it would have at full size
            }
            catch { }
        }

        // Full-view screens: the background fills your view, but the logo and text are shrunk back
        // towards the middle so they aren't huge.
        private static readonly Dictionary<IntPtr, (Vector3 pos, Vector3 scale)> _shrunk = new Dictionary<IntPtr, (Vector3, Vector3)>();

        // Children that fill the screen but must still be shrunk: the studio intro's logo video.
        private static readonly HashSet<string> _contentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Video" };

        private static void ShrinkContent(Canvas c, float k)
        {
            try
            {
                var root = c.transform.TryCast<RectTransform>();
                if (root == null) return;
                ShrinkChildren(root, root.rect.size, k, 0, _contentNames);
            }
            catch { }
        }

        // Walks the hierarchy: full-size (background) children are left alone but descended into (the
        // studio intro logo lives inside a full-screen 'Black' image); smaller ones are shrunk. The
        // full-size parents are centred on the canvas, so scaling a nested child's localPosition by k
        // scales it about the canvas centre too.
        private static void ShrinkChildren(RectTransform parent, Vector2 full, float k, int depth, HashSet<string> pieces)
        {
            if (depth > 4) return;
            for (int i = 0; i < parent.childCount; i++)
            {
                var ch = parent.GetChild(i).TryCast<RectTransform>();
                if (ch == null) continue;
                var sz = ch.rect.size;
                bool isPiece = false;
                try { isPiece = pieces != null && ch.gameObject != null && pieces.Contains(ch.gameObject.name); } catch { }
                if (!isPiece && sz.x >= full.x * 0.9f && sz.y >= full.y * 0.9f)   // background: keep full size, look inside
                {
                    ShrinkChildren(ch, full, k, depth + 1, pieces);
                    continue;
                }
                if (!_shrunk.TryGetValue(ch.Pointer, out var o)) { o = (ch.localPosition, ch.localScale); _shrunk[ch.Pointer] = o; }
                ch.localPosition = o.pos * k;
                ch.localScale = o.scale * k;
            }
        }

        // The studio intro is only shown briefly; slow its logo video down so it lasts longer.
        // Each VideoPlayer is only touched once, and only if it's the one feeding the 'Video' RawImage.
        private static readonly HashSet<IntPtr> _spedVideos = new HashSet<IntPtr>();

        private static void SlowIntroVideo(Canvas c)
        {
            try
            {
                float speed = Plugin.StudioIntroSpeed.Value;
                if (speed <= 0f) return;
                RenderTexture target = null;
                foreach (var raw in c.GetComponentsInChildren<UnityEngine.UI.RawImage>(true))
                {
                    if (raw == null || raw.gameObject == null) continue;
                    string n; try { n = raw.gameObject.name; } catch { continue; }
                    if (!string.Equals(n, "Video", StringComparison.OrdinalIgnoreCase)) continue;
                    try { target = raw.texture != null ? raw.texture.TryCast<RenderTexture>() : null; } catch { target = null; }
                    break;
                }
                foreach (var vp in UnityEngine.Object.FindObjectsOfType<UnityEngine.Video.VideoPlayer>())
                {
                    if (vp == null) continue;
                    bool match;
                    try { match = target != null && vp.targetTexture == target; }
                    catch { continue; }   // can't read targetTexture: leave this one alone
                    if (!match || _spedVideos.Contains(vp.Pointer)) continue;
                    _spedVideos.Add(vp.Pointer);
                    try { vp.playbackSpeed = speed; Plugin.L.LogInfo("UI: studio intro video speed set to " + speed); }
                    catch (Exception e) { Plugin.L.LogInfo("UI: could not slow studio intro video: " + e.Message); }
                }
            }
            catch { }
        }

        // HUD modes: Full -> Hand Only -> None -> Full. Hand Only is skipped when the hand HUD is off
        // (there would be nothing left to show).
        internal static void ToggleHud()
        {
            string m = Plugin.HudMode.Value;
            bool handOn = !string.Equals(Plugin.HandHud.Value, "Off", StringComparison.OrdinalIgnoreCase);
            if (m == "Full") m = handOn ? "Hand Only" : "None";
            else if (m == "Hand Only") m = "None";
            else m = "Full";
            Plugin.HudMode.Value = m;
            Plugin.L.LogInfo("HUD: " + m);
        }
        /// <summary>Nothing shown, wrist HUD included.</summary>
        internal static bool HudAllHidden => Plugin.HudMode.Value == "None";
        /// <summary>The HUD in front of you (tool belt, compass) is hidden; the wrist HUD may still show.</summary>
        internal static bool HudMainHidden => Plugin.HudMode.Value != "Full";

        private static string _handRaw;
        private static readonly HashSet<string> _handNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static void RefreshHandList()
        {
            string raw = Plugin.HandHudCanvases.Value ?? "";
            if (raw == _handRaw) return;
            _handRaw = raw;
            _handNames.Clear();
            foreach (var part in raw.Split(',')) { var t = part.Trim(); if (t.Length > 0) _handNames.Add(t); }
            foreach (var t in _tracked)
                if (t.Canvas != null) t.OnHand = _handNames.Contains(t.Canvas.gameObject.name);
        }

        private static readonly List<Tracked> _tracked = new List<Tracked>();
        internal static bool RenderHookActive;
        /// <summary>True while the game's main menu is on screen.</summary>
        internal static bool MainMenuShowing;
        private static int _hideTrackedCount = -1;
        private static float _nextHide;
        private float _nextScan;
        private bool _loggedNoCamera;

        private void LateUpdate()
        {
            try
            {
                var cam = Camera.main;
                if (cam == null)
                {
                    if (!_loggedNoCamera) { Plugin.L.LogInfo("UI: waiting for main camera"); _loggedNoCamera = true; }
                    return;
                }
                if (Time.unscaledTime >= _nextScan)
                {
                    _nextScan = Time.unscaledTime + 1f;
                    long st = Perf.Now(); Scan(cam); Perf.Add("Menu scan (incl. border hiding)", st);
                }
                long pt = Perf.Now(); Place(cam); Perf.Add("Menu placement", pt); // baseline; refined at render time by HeadTracking
                Canvas menu = null;
                foreach (var t in _tracked) if (t.Canvas != null && t.Name == "Main menu") { menu = t.Canvas; break; }
                try { MainMenuShowing = menu != null && menu.gameObject.activeInHierarchy; } catch { MainMenuShowing = false; }
                UpdateCheck.Tick(menu);
            }
            catch (Exception e)
            {
                Plugin.L.LogError("UI update error: " + e.Message);
                _nextScan = Time.unscaledTime + 5f;
            }
        }

        private bool IsTracked(Canvas c)
        {
            foreach (var t in _tracked)
                if (t.Canvas != null && t.Canvas.Pointer == c.Pointer) return true;
            return false;
        }

        private void Scan(Camera cam)
        {
            // Forget destroyed canvases.
            _tracked.RemoveAll(t => t.Canvas == null);

            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Canvas>());
            foreach (var o in all)
            {
                var c = o.TryCast<Canvas>();
                if (c == null) continue;
                if (!c.gameObject.scene.IsValid()) continue; // skip prefabs/assets
                if (!c.isRootCanvas) continue;
                if (IsTracked(c)) continue;
                if (c.renderMode == RenderMode.WorldSpace) continue; // already part of the 3D world

                var rt = c.transform.TryCast<RectTransform>();
                float pixelWidth = rt != null && rt.sizeDelta.x > 1f ? rt.sizeDelta.x : Screen.width;

                c.renderMode = RenderMode.WorldSpace;
                c.worldCamera = cam;
                if (rt != null && (rt.sizeDelta.x < 1f || rt.sizeDelta.y < 1f))
                    rt.sizeDelta = new Vector2(Screen.width, Screen.height);

                RefreshHandList();
                var t = new Tracked { Canvas = c, BaseScale = 1f / Mathf.Max(1f, pixelWidth), OnHand = _handNames.Contains(c.gameObject.name) };
                _tracked.Add(t);
                Plugin.L.LogInfo($"UI: moved canvas '{c.gameObject.name}' (sort {c.sortingOrder}) into VR");
                HeadTracking.NoteCanvas(c);
                if (Plugin.UiLogElements.Value) LogLargeElements(c);
            }
            // Border hiding walks every piece of every menu: only needed when a new menu turns up,
            // otherwise a check every 5 s is plenty (once a second cost a dropped frame or two).
            if (_tracked.Count != _hideTrackedCount || Time.unscaledTime >= _nextHide)
            {
                _hideTrackedCount = _tracked.Count;
                _nextHide = Time.unscaledTime + 5f;
                long ht = Perf.Now(); HideListed(); Perf.Add("Border hiding", ht);
            }

            // Higher sorting order = drawn on top = slightly closer to the viewer.
            _tracked.Sort((a, b) => a.Canvas.sortingOrder.CompareTo(b.Canvas.sortingOrder));
            for (int i = 0; i < _tracked.Count; i++) _tracked[i].Rank = i;

            // Make sure the camera actually draws the UI layer.
            cam.cullingMask |= 1 << 5;
        }

        // ---- finding and hiding screen borders
        private static string[] _hideNames;
        private static string _hideRaw;

        private static void LogLargeElements(Canvas c)
        {
            try
            {
                var root = c.transform.TryCast<RectTransform>();
                if (root == null) return;
                var size = root.rect.size;
                if (size.x < 1f || size.y < 1f) return;
                int n = 0;
                foreach (var g in c.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                {
                    if (g == null) continue;
                    var r = g.rectTransform.rect.size;
                    string lname = (g.gameObject.name ?? "").ToLowerInvariant();
                    bool full = r.x >= size.x * 0.85f && r.y >= size.y * 0.85f;
                    bool big = r.x >= size.x * 0.4f || r.y >= size.y * 0.4f;
                    bool named = lname.Contains("shade") || lname.Contains("shadow") || lname.Contains("gradient") || lname.Contains("vignet")
                                 || lname.Contains("edge") || lname.Contains("border") || lname.Contains("frame") || lname.Contains("fade")
                                 || lname.Contains("back") || lname.Contains("tint") || lname.Contains("dark") || lname.Contains("panel");
                    if (!full && !big && !named) continue;
                    string kind = full ? "full-screen" : big ? "large" : "named";
                    string parent = g.transform.parent != null ? g.transform.parent.gameObject.name : "-";
                    string sprite = "";
                    var img = g.TryCast<UnityEngine.UI.Image>();
                    if (img != null && img.sprite != null) sprite = $", sprite '{img.sprite.name}'";
                    var col = g.color;
                    Plugin.L.LogInfo($"UI:   {kind} piece '{g.gameObject.name}' (in '{parent}') on '{c.gameObject.name}' ({g.GetIl2CppType().Name}, {r.x:F0}x{r.y:F0} of {size.x:F0}x{size.y:F0}, colour {col.r:F2}/{col.g:F2}/{col.b:F2} alpha {col.a:F2}, active {g.gameObject.activeInHierarchy}{sprite})");
                    if (++n >= 30) break;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("UI: element listing failed: " + e.Message); }
        }

        private static void HideListed()
        {
            string raw = Plugin.UiHideElements.Value ?? "";
            if (raw != _hideRaw)
            {
                _hideRaw = raw;
                var list = new List<string>();
                foreach (var part in raw.Split(','))
                {
                    var t = part.Trim();
                    if (t.Length > 0) list.Add(t.ToLowerInvariant());
                }
                _hideNames = list.ToArray();
            }
            if (_hideNames == null || _hideNames.Length == 0) return;
            foreach (var t in _tracked)
            {
                if (t.Canvas == null) continue;
                try
                {
                    foreach (var g in t.Canvas.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                    {
                        if (g == null || !g.enabled) continue;
                        string name = g.gameObject.name.ToLowerInvariant();
                        foreach (var h in _hideNames)
                            if (name == h) { g.enabled = false; break; }
                    }
                }
                catch { }
            }
        }

        private static bool _hasPose;
        private static Vector3 _smoothPos;
        private static Quaternion _smoothRot;
        private static float _lastPlaceTime;

        internal static void Place(Camera cam)
        {
            var ct = cam.transform;
            float dist = Plugin.UiDistance.Value;
            float width = Plugin.UiWidth.Value;
            float h = Plugin.UiHeightOffset.Value;

            // Target pose: in front of the game camera (before head tracking is applied).
            // Anchor the panel on your eyes (the camera plus your view-height offset), not on the
            // game camera itself, so tilting the view with the right stick swings the HUD around you
            // instead of sliding it towards and away from you.
            // (Looking straight ahead the panel ends up exactly where it was before.)
            float eyeOff = Plugin.HeadTrackingEnabled.Value ? Plugin.HeadHeightOffset.Value : 0f;
            Vector3 eyeBase = ct.position + HeadTracking.WorldUp(ct.position) * eyeOff;
            Vector3 targetPos = eyeBase + ct.rotation * new Vector3(0f, h - eyeOff, dist);
            Quaternion targetRot = ct.rotation;

            // Light smoothing to iron out the game's stepped camera motion.
            float now = Time.unscaledTime;
            float dt = Mathf.Clamp(now - _lastPlaceTime, 0f, 0.1f);
            _lastPlaceTime = now;
            float k = Plugin.UiSmoothing.Value;
            if (!_hasPose || k <= 0f || (targetPos - _smoothPos).sqrMagnitude > 25f)
            {
                _smoothPos = targetPos;
                _smoothRot = targetRot;
                _hasPose = true;
            }
            else if (dt > 0f)
            {
                float a = 1f - Mathf.Exp(-k * dt);
                _smoothPos = Vector3.Lerp(_smoothPos, targetPos, a);
                _smoothRot = Quaternion.Slerp(_smoothRot, targetRot, a);
            }

            // Hand HUD: chosen HUD screens float just above a controller, turned to face you.
            RefreshHandList();
            string hand = (Plugin.HandHud.Value ?? "").Trim();
            bool useHand = hand.Equals("Left", StringComparison.OrdinalIgnoreCase) || hand.Equals("Right", StringComparison.OrdinalIgnoreCase);
            Vector3 handPanel = default; Quaternion handFacing = Quaternion.identity; bool handOk = false;
            if (useHand)
            {
                try
                {
                    if (MotionControls.TryGetHandPose(hand.Equals("Left", StringComparison.OrdinalIgnoreCase), out var hp, out var hr)
                        && HeadTracking.TryGetHeadWorld(out var eye))
                    {
                        // "Up" as you see it (the game camera can be tilted by the right stick).
                        var up = HeadTracking.WorldUp(hp);
                        handPanel = hp + up * Plugin.HandHudHeight.Value;
                        var toPanel = handPanel - eye;
                        if (toPanel.sqrMagnitude > 0.0001f)
                        {
                            handFacing = Quaternion.LookRotation(toPanel.normalized, up);
                            handOk = true;
                        }
                    }
                }
                catch { }
            }

            // Re-home HUD pieces onto the wrist screen (or put them back if the wrist HUD is off).
            Canvas wrist = null;
            if (handOk) foreach (var t in _tracked) if (t.OnHand && t.Canvas != null) { wrist = t.Canvas; break; }
            HudLayout.Apply(wrist);

            Vector3 fwd = _smoothRot * Vector3.forward;
            Vector3 handFwd = handFacing * Vector3.forward;
            foreach (var t in _tracked)
            {
                if (t.Canvas == null) continue;
                var tr = t.Canvas.transform;
                if (tr.parent != null) tr.SetParent(null, true);
                if (t.OnHand && handOk)
                {
                    float hs = (HudAllHidden ? 0f : Plugin.HandHudWidth.Value) * t.BaseScale;
                    tr.SetPositionAndRotation(handPanel - handFwd * (t.Rank * 0.0005f), handFacing);
                    tr.localScale = new Vector3(hs, hs, hs);
                    continue;
                }
                float w = width;
                Vector3 extra = Vector3.zero;
                if (t.Name == null) { try { t.Name = t.Canvas.gameObject.name; } catch { t.Name = ""; } }
                // Loading screens fill your whole view: locked to your head, big enough to cover it.
                if (Plugin.FullViewLoading.Value && (t.Name == "Loading screen canvas" || t.Name == "Teleport screen canvas" || t.Name == "Studio intro canvas"))
                {
                    // Before head tracking has started (the studio intro), the camera itself follows your head.
                    if (!HeadTracking.TryGetHeadPose(out var headPos, out var headRot))
                    {
                        // Head tracking not running yet (studio intro): the headset view is the camera
                        // plus the raw headset pose on top of it.
                        var lr = UnityEngine.XR.InputTracking.GetLocalRotation(UnityEngine.XR.XRNode.CenterEye);
                        var lp = UnityEngine.XR.InputTracking.GetLocalPosition(UnityEngine.XR.XRNode.CenterEye);
                        bool ok = !(lr.x == 0f && lr.y == 0f && lr.z == 0f && lr.w == 0f);
                        headRot = ok ? ct.rotation * lr : ct.rotation;
                        headPos = ok ? ct.position + ct.rotation * lp : ct.position;
                    }
                    // Put the canvas well inside 2 m: the world floor is closer than that when looking down
                    // and would be drawn in front of the canvas. The size scales with d, so the angular
                    // size (and the logo size) stays the same.
                    const float d = 0.5f;
                    float fs = d * 14f * t.BaseScale;   // ~7 m wide at 0.5 m: covers the whole view, top to bottom too
                    bool studio = t.Name == "Studio intro canvas";
                    float down = studio ? Plugin.IntroLower.Value * (d / 2f) : 0f;   // tuned for 2 m
                    tr.SetPositionAndRotation(headPos + headRot * new Vector3(0f, -down, d), headRot);
                    tr.localScale = new Vector3(fs, fs, fs);
                    float logo = studio ? Plugin.StudioIntroLogoSize.Value : Plugin.LoadingLogoSize.Value;
                    ShrinkContent(t.Canvas, logo * 7f / 14f);   // logo/text keep their old size
                    if (studio) SlowIntroVideo(t.Canvas);
                    continue;
                }
                Vector3 upv = _smoothRot * Vector3.up;
                if (t.Name == "How to play") w = Plugin.ManualWidth.Value;
                else if (t.Name == MapFix.CanvasName) w = Plugin.MapWidth.Value;
                else if (t.Name == "Supply wall screen") w = Plugin.ToolsScreenWidth.Value;
                else if (t.Name == "Garage menu")
                {
                    // Close to you, so it isn't behind the garage wall when you're at the back of it;
                    // scaled down to look the same size as the upgrade matrix.
                    // Pull the whole panel in towards your eyes (keeping its direction, so it stays as
                    // high or low in your view as the other menus).
                    float gd = Mathf.Max(0.5f, Plugin.GarageMenuDistance.Value);
                    float pull = gd / Mathf.Max(0.1f, dist);
                    w = Plugin.ToolsScreenWidth.Value * gd / (dist + 0.5f) * Mathf.Max(0.3f, Plugin.GarageMenuSize.Value);
                    extra = (_smoothPos - eyeBase) * (pull - 1f);
                }
                else if (t.Name == "Upgrades" || t.Name == "Construction HUD")
                {
                    w = t.Name == "Construction HUD" ? Plugin.BuildMenuWidth.Value : Plugin.ToolsScreenWidth.Value;
                    extra = fwd * 0.5f;   // half a metre further back than the other menus
                }
                else if (t.Name == "Region scanner HUD")
                {
                    w *= Plugin.CompassScale.Value;
                    extra = upv * Plugin.CompassRaise.Value;
                }
                else if (t.Name == "Equipment HUD - five slots")
                    extra = -upv * Plugin.ToolbarLower.Value;
                else if ((t.Name == "Main menu" || t.Name == "Pause menu") && dist > 0.1f)
                {
                    float md = Plugin.MainMenuDistance.Value;
                    extra = fwd * (md - dist);
                    // just moved back (same physical size, so it looks further away)
                }
                if ((HudAllHidden || (HudMainHidden && handOk)) && IsHudScreen(t.Name)) w = 0f;   // hidden: shrink to nothing
                float s = w * t.BaseScale;
                tr.SetPositionAndRotation(_smoothPos + extra - fwd * (t.Rank * 0.002f), _smoothRot);
                tr.localScale = new Vector3(s, s, s);
                if (t.Name == "Region scanner HUD" && w > 0f) KeepAtFullSizeSpot(tr, Plugin.CompassScale.Value);
            }
        }
    }
}
