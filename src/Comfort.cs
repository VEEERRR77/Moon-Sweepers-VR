// VR comfort tweaks: keeps the game's motion blur switched off while playing in VR.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class Comfort
    {
        private const BindingFlags S = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static float _next;
        private static bool _resolved, _broken, _logged;
        private static PropertyInfo _current, _blur;

        private static float _nextVignette;
        private static bool _vignetteBroken;
        private static int _vignetteLogged = -1;
        private static bool _listedEffects;

        // The game's dark screen-edge shading (a URP Vignette effect) looks like a frame in VR.
        private static void KillVignette()
        {
            if (_vignetteBroken || !Plugin.DisableVignette.Value || Time.unscaledTime < _nextVignette) return;
            _nextVignette = Time.unscaledTime + 3f;
            try
            {
                int n = 0;
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.Rendering.Universal.Vignette>()))
                {
                    var v = o.TryCast<UnityEngine.Rendering.Universal.Vignette>();
                    if (v == null) continue;
                    if (v.active) { v.active = false; n++; }
                }
                if (n > 0 && n != _vignetteLogged) { _vignetteLogged = n; Plugin.L.LogMessage($"Comfort: switched off {n} screen-edge vignette effect(s)"); }
                if (!_listedEffects)
                {
                    _listedEffects = true;
                    var names = new System.Collections.Generic.SortedSet<string>();
                    foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.Rendering.VolumeComponent>()))
                    {
                        var vc = o.TryCast<UnityEngine.Rendering.VolumeComponent>();
                        if (vc != null && vc.active) names.Add(vc.GetIl2CppType().Name);
                    }
                    Plugin.L.LogInfo("Comfort: active screen effects: " + (names.Count > 0 ? string.Join(", ", names) : "none"));
                }
            }
            catch (Exception e)
            {
                _vignetteBroken = true;
                Plugin.L.LogWarning("Comfort: could not switch off the vignette: " + (e.InnerException ?? e).Message);
            }
        }

        private static float _nextStageFix;
        private static readonly System.Collections.Generic.HashSet<IntPtr> _fixedCams = new System.Collections.Generic.HashSet<IntPtr>();

        // The field manual and contract are filmed by their own cameras into an image. In VR those
        // cameras would otherwise be turned by your head, so the picture comes out at the wrong angle.
        private static void FixStageCameras()
        {
            if (Time.unscaledTime < _nextStageFix) return;
            _nextStageFix = Time.unscaledTime + 1f;
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Camera>()))
                {
                    var c = o.TryCast<Camera>();
                    if (c == null || !c.gameObject.scene.IsValid()) continue;
                    bool toImage = c.targetTexture != null;
                    var parentCam = c.transform.parent != null && c.transform.parent.parent != null
                        ? c.transform.parent.parent.GetComponent<Camera>() : null;
                    bool uuvrChildOfImageCam = c.name == "VrChildCamera" && parentCam != null && parentCam.targetTexture != null;
                    // Only UUVR's extra copy of an image camera is touched (switched off); the game's own
                    // image cameras are left alone (changing them crashed the game).
                    if (!uuvrChildOfImageCam) continue;
                    // The moon map's camera worked fine with UUVR's copy left on; switching it off
                    // showed the globe from the inside, so leave that one alone.
                    if (c.transform.parent.parent.name.IndexOf("Map", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (_fixedCams.Add(c.Pointer))
                        Plugin.L.LogInfo($"Comfort: switching off extra VR camera under '{c.transform.parent.parent.name}'");
                    if (c.enabled) c.enabled = false;
                }
            }
            catch (Exception e)
            {
                _nextStageFix = float.MaxValue;
                Plugin.L.LogWarning("Comfort: image camera fix failed: " + (e.InnerException ?? e).Message);
            }
        }

        public static void Tick()
        {
            if (XrStarter.Started) FixStageCameras();
            if (XrStarter.Started) KillVignette();
            if (_broken || !Plugin.DisableMotionBlur.Value || !XrStarter.Started) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            try
            {
                if (!_resolved)
                {
                    _resolved = true;
                    var t = AccessTools.TypeByName("MoonCleaners.MoonUserSettings");
                    _current = t?.GetProperty("Current", S);
                    _blur = t?.GetProperty("motionBlur", I);
                    if (_current == null || _blur == null) { _broken = true; Plugin.L.LogWarning("Comfort: motion blur setting not found"); return; }
                }
                var settings = _current.GetValue(null);
                if (settings == null) return;
                object v = _blur.GetValue(settings);
                object off;
                if (v is bool) off = false;
                else if (v is float) off = 0f;
                else if (v is int) off = 0;
                else { _broken = true; Plugin.L.LogWarning("Comfort: unexpected motion blur setting type " + (v?.GetType().Name ?? "null")); return; }
                if (!Equals(v, off))
                {
                    _blur.SetValue(settings, off);
                    if (!_logged) { _logged = true; Plugin.L.LogMessage($"Comfort: motion blur switched off (was {v})"); }
                }
            }
            catch (Exception e)
            {
                _broken = true;
                Plugin.L.LogWarning("Comfort: could not change motion blur: " + (e.InnerException ?? e).Message);
            }
        }
    }
}
