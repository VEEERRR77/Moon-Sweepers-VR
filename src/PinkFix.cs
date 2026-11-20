// Hides objects whose shader failed: Unity draws those with its error shader, which shows up as a
// bright pink box in the headset (e.g. at the character's feet on the main menu). Switching such
// renderers off is easier on the eyes than the magenta blob.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class PinkFix
    {
        private const string ErrorShader = "Hidden/InternalErrorShader";
        private static float _next;
        private static bool _broken;
        private static readonly HashSet<IntPtr> _handled = new HashSet<IntPtr>();

        private static string ShaderName(Material m)
        {
            try { return m != null && m.shader != null ? m.shader.name : "no shader"; }
            catch (Exception e) { return "unknown shader (" + (e.InnerException ?? e).Message + ")"; }
        }

        private static bool IsBroken(Material m)
        {
            try
            {
                if (m == null) return false;                  // a missing entry isn't a drawable object
                var sh = m.shader;
                if (sh == null) return true;
                if (string.Equals(sh.name, ErrorShader, StringComparison.Ordinal)) return true;
                return !sh.isSupported;
            }
            catch { return false; }
        }

        // Our own objects (the laser beam and dot) are named "MoonXr ...": never touch those.
        private static bool IsOurs(GameObject g)
        {
            try
            {
                for (var t = g.transform; t != null; t = t.parent)
                    if (t.name != null && t.name.StartsWith("MoonXr", StringComparison.Ordinal)) return true;
            }
            catch { }
            return false;
        }

        // Looking at every renderer in one go took 60-100 ms, once a second (a visible stutter).
        // Now the list is fetched every 10 s and checked a slice at a time, each renderer and each
        // material only once, so it costs a fraction of a millisecond per frame.
        private const int PerFrame = 150;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Object> _list;
        private static int _pos;
        private static readonly HashSet<IntPtr> _checked = new HashSet<IntPtr>();
        private static readonly Dictionary<IntPtr, bool> _matBroken = new Dictionary<IntPtr, bool>();

        private static bool MaterialBroken(Material m)
        {
            if (m == null) return false;
            IntPtr p;
            try { p = m.Pointer; } catch { return false; }
            if (_matBroken.TryGetValue(p, out bool b)) return b;
            b = IsBroken(m);
            _matBroken[p] = b;
            return b;
        }

        public static void Tick()
        {
            if (_broken || !Plugin.HideBrokenPink.Value) return;
            try
            {
                if (_list == null || _pos >= _list.Length)
                {
                    if (Time.unscaledTime < _next) return;
                    _next = Time.unscaledTime + 10f;
                    long st = Perf.Now();
                    _list = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Renderer>());
                    Perf.Add("Pink box fix (renderer list)", st);
                    _pos = 0;
                    if (_checked.Count > 50000) _checked.Clear();
                    if (_matBroken.Count > 20000) _matBroken.Clear();
                }
                int end = Math.Min(_list.Length, _pos + PerFrame);
                for (; _pos < end; _pos++)
                {
                    var o = _list[_pos];
                    if (o == null) continue;
                    IntPtr ptr;
                    try { ptr = o.Pointer; } catch { continue; }
                    if (_checked.Contains(ptr)) continue;
                    var r = o.TryCast<Renderer>();
                    if (r == null) continue;
                    var go = r.gameObject;
                    if (go == null || !go.scene.IsValid()) continue;   // prefabs and assets: look again next time
                    _checked.Add(ptr);
                    string shader = null;
                    var mats = r.sharedMaterials;
                    if (mats != null)
                    {
                        for (int i = 0; i < mats.Length; i++)
                        {
                            var m = mats[i];
                            if (!MaterialBroken(m)) continue;
                            shader = ShaderName(m);
                            break;
                        }
                    }
                    if (shader == null) continue;
                    if (IsOurs(go)) continue;
                    if (!_handled.Add(ptr)) continue;
                    string parent = "none";
                    try { if (go.transform.parent != null) parent = go.transform.parent.name; } catch { }
                    Plugin.L.LogInfo($"Pink fix: hiding '{go.name}' (parent '{parent}') with shader '{shader}'");
                    if (r.enabled) r.enabled = false;
                }
                if (_pos >= _list.Length) _list = null;   // done with this list: let it go
            }
            catch (Exception e)
            {
                _broken = true;   // stop scanning: the search itself isn't working in this game
                Plugin.L.LogWarning("Pink fix: could not scan for broken pink objects: " + (e.InnerException ?? e).Message);
            }
        }
    }
}
