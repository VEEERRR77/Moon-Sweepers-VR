// Crew name tags in 3D: the game draws other players' names flat on the screen, placed for the
// normal camera, which can't line up in VR. Instead each player gets a small label floating above
// their head, turned to face you and kept a readable size at any distance. The game's own flat
// tags are hidden (see UiToWorld).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class NameTags
    {
        private class Tag { public MoonCleaners.MoonRemotePlayer Player; public GameObject Go; public RectTransform Rect; public TMPro.TextMeshProUGUI Text; public string Name; }
        private static readonly Dictionary<IntPtr, Tag> _tags = new Dictionary<IntPtr, Tag>();
        private static readonly List<MoonCleaners.MoonRemotePlayer> _players = new List<MoonCleaners.MoonRemotePlayer>();
        private static float _nextSearch;
        private static readonly HashSet<IntPtr> _seen = new HashSet<IntPtr>();
        private static bool _failed, _loggedErr;
        private static int _loggedCount = -1;
        private static TMPro.TMP_FontAsset _font;

        internal static bool On => Plugin.NameTagsEnabled != null && Plugin.NameTagsEnabled.Value;

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

        private static Tag Make(MoonCleaners.MoonRemotePlayer p)
        {
            var go = new GameObject("MoonXr name tag");   // "MoonXr" so the pink-object fix leaves it alone
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.layer = 5;
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 50;
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(600f, 90f);
            var bgGo = new GameObject("Backing"); bgGo.layer = 5;
            bgGo.transform.SetParent(rt, false);
            var bg = bgGo.AddComponent<UnityEngine.UI.Image>();
            bg.color = new Color(0f, 0f, 0f, 0.45f); bg.raycastTarget = false;
            var brt = bgGo.GetComponent<RectTransform>();
            brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0.5f, 0.5f);
            var txGo = new GameObject("Name"); txGo.layer = 5;
            txGo.transform.SetParent(rt, false);
            var t = txGo.AddComponent<TMPro.TextMeshProUGUI>();
            var f = GetFont(); if (f != null) t.font = f;
            t.fontSize = 56f; t.alignment = TMPro.TextAlignmentOptions.Center; t.raycastTarget = false;
            t.color = new Color(1f, 0.92f, 0.6f, 1f);
            var trt = txGo.GetComponent<RectTransform>();
            trt.anchorMin = trt.anchorMax = trt.pivot = new Vector2(0.5f, 0.5f);
            trt.sizeDelta = rt.sizeDelta;
            return new Tag { Player = p, Go = go, Rect = rt, Text = t, Name = null };
        }

        private static void Search()
        {
            _players.Clear();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MoonCleaners.MoonRemotePlayer>()))
            {
                var p = o.TryCast<MoonCleaners.MoonRemotePlayer>();
                if (p == null || p.gameObject == null || !p.gameObject.scene.IsValid()) continue;
                _players.Add(p);
            }
            if (_players.Count != _loggedCount) { _loggedCount = _players.Count; Plugin.L.LogInfo($"Name tags: {_players.Count} other player(s)"); }
        }

        private static void HideAll()
        {
            foreach (var t in _tags.Values) try { if (t.Go != null && t.Go.activeSelf) t.Go.SetActive(false); } catch { }
        }

        /// <summary>Called every frame just before the UI is drawn.</summary>
        internal static void Tick()
        {
            if (_failed) return;
            try
            {
                if (!On || !HeadTracking.TryGetHeadWorld(out var eye)) { HideAll(); return; }
                float now = Time.unscaledTime;
                if (now >= _nextSearch) { _nextSearch = now + 2f; Search(); }
                if (_players.Count == 0 && _tags.Count == 0) return;

                var seen = _seen; seen.Clear();
                float maxDist = Mathf.Max(5f, Plugin.NameTagsDistance.Value);
                foreach (var p in _players)
                {
                    IntPtr key;
                    try { if (p == null || p.gameObject == null) continue; key = p.Pointer; } catch { continue; }
                    seen.Add(key);
                    if (!_tags.TryGetValue(key, out var tag)) _tags[key] = tag = Make(p);

                    bool show = false;
                    try { show = p.gameObject.activeInHierarchy; } catch { }
                    string name = null;
                    try { name = p.DisplayName; } catch { }
                    if (!show || string.IsNullOrEmpty(name)) { if (tag.Go.activeSelf) tag.Go.SetActive(false); continue; }

                    var root = p.transform.position;
                    var up = HeadTracking.WorldUp(root);
                    float h = 2.1f;
                    try { float nh = p.NameTagHeight; if (nh > 0.5f && nh < 5f) h = nh; } catch { }
                    Vector3 pos = root + up * (h + 0.15f);
                    var to = pos - eye;
                    float d = to.magnitude;
                    if (d > maxDist || d < 0.3f) { if (tag.Go.activeSelf) tag.Go.SetActive(false); continue; }
                    if (!tag.Go.activeSelf) tag.Go.SetActive(true);

                    if (tag.Name != name)
                    {
                        tag.Name = name;
                        tag.Text.text = name;
                        // Size the dark backing to the name.
                        float w = Mathf.Clamp(tag.Text.GetPreferredValues(name).x + 50f, 160f, 900f);
                        tag.Rect.sizeDelta = new Vector2(w, 90f);
                        var bg = tag.Rect.GetChild(0).TryCast<RectTransform>(); if (bg != null) bg.sizeDelta = tag.Rect.sizeDelta;
                        tag.Text.rectTransform.sizeDelta = tag.Rect.sizeDelta;
                    }
                    // About the same size in your view at any distance (a little bigger when close),
                    // so names stay readable across the station.
                    float size = Plugin.NameTagsSize.Value;
                    float worldH = Mathf.Clamp(d * 0.045f, 0.12f, 2.5f) * size;   // height of the label in metres
                    float s = worldH / 90f;
                    tag.Rect.SetPositionAndRotation(pos, Quaternion.LookRotation(to / d, up));
                    tag.Rect.localScale = new Vector3(s, s, s);
                }

                // Players who left: drop their tags.
                if (_tags.Count > seen.Count)
                {
                    var gone = new List<IntPtr>();
                    foreach (var kv in _tags) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
                    foreach (var k in gone) { try { UnityEngine.Object.Destroy(_tags[k].Go); } catch { } _tags.Remove(k); }
                }
            }
            catch (Exception e)
            {
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Name tags: " + e); }
                _failed = true;
                HideAll();
            }
        }
    }
}
