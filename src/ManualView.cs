// The field manual is a binder lying on a desk, filmed from an angle by its own camera. On a
// floating VR panel that reads as a book lying on its side, so we film it straight from above
// instead: the binder then faces you like a page. Done every frame just before rendering.
using System;
using System.Reflection;
using MoonCleaners;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class ManualView
    {
        private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static MoonHowToPlay _manual;
        private static float _nextFind;
        private static PropertyInfo _isOpen, _eye, _rig, _book;
        private static Vector3 _lastSet;
        private static float _distance = -1f;
        private static bool _logged, _broken;

        private static object Get(PropertyInfo p, object o) => p?.GetValue(p.GetGetMethod(true).IsStatic ? null : o);

        public static void Tick()
        {
            if (_broken || !Plugin.ManualFaceOn.Value) return;
            try
            {
                if (_manual == null || _manual.WasCollected)
                {
                    if (Time.unscaledTime < _nextFind) return;
                    _nextFind = Time.unscaledTime + 2f;
                    _manual = null;
                    foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MoonHowToPlay>()))
                    {
                        var m = o.TryCast<MoonHowToPlay>();
                        if (m != null && m.gameObject.scene.IsValid()) { _manual = m; break; }
                    }
                    if (_manual == null) return;
                    var t = _manual.GetType();
                    _isOpen = t.GetProperty("IsOpen", F); _eye = t.GetProperty("eye", F); _rig = t.GetProperty("rig", F);
                }
                if (!(Get(_isOpen, _manual) is bool open) || !open) { _distance = -1f; return; }
                var eye = Get(_eye, _manual) as Camera;
                var rig = Get(_rig, _manual) as Transform;
                if (eye == null || rig == null) return;

                var et = eye.transform;
                // Frame the open binder so it fills the picture (and so the panel it's shown on).
                float tanHalf = Mathf.Tan(eye.fieldOfView * 0.5f * Mathf.Deg2Rad);
                float aspect = eye.aspect > 0.01f ? eye.aspect : 16f / 9f;
                float margin = Mathf.Max(0.8f, Plugin.ManualMargin.Value);
                Vector3 centre = rig.position; float w = 0f, h = 0f; string how;
                try
                {
                    var book = Get(_book ??= _manual.GetType().GetProperty("book", F), _manual);
                    if (book != null)
                    {
                        var bt = book.GetType();
                        if (Get(bt.GetProperty("OpenWidth", F), book) is float ow) w = ow;
                        if (Get(bt.GetProperty("PageH", F), book) is float ph) h = ph;
                    }
                }
                catch { }
                if (w > 0.01f && h > 0.01f) how = "binder size";
                else
                {
                    // Fallback: measure what's drawn.
                    bool any = false; Bounds bb = default;
                    foreach (var r in rig.GetComponentsInChildren<Renderer>(false))
                    {
                        if (r == null || !r.enabled) continue;
                        if (!any) { bb = r.bounds; any = true; } else bb.Encapsulate(r.bounds);
                    }
                    if (!any) return;
                    centre = bb.center; w = bb.size.x; h = bb.size.z; how = "measured size";
                }
                float need = Mathf.Max((h * 0.5f) / tanHalf, (w * 0.5f) / (tanHalf * aspect)) * margin;
                var want = new Vector3(centre.x, centre.y + need, centre.z);
                // Ease towards it so page turns don't make the view jump.
                var pos = _distance < 0f ? want : Vector3.Lerp(_lastSet, want, Mathf.Clamp01(Time.unscaledDeltaTime * 6f));
                _distance = need;
                et.SetPositionAndRotation(pos, Quaternion.LookRotation(Vector3.down, Vector3.forward));
                _lastSet = pos;
                if (!_logged) { _logged = true; Plugin.L.LogInfo($"Field manual: filming face-on, {how} {w:F2} x {h:F2} m, camera {need:F2} m above"); }
            }
            catch (Exception e)
            {
                _broken = true;
                Plugin.L.LogWarning("Field manual face-on view switched off after an error: " + (e.InnerException ?? e).Message);
            }
        }
    }
}
