// Diagnostic: when the contract (new hire) or the field manual opens, record how the game
// shows it (its private camera, the image it draws into, and the screen it appears on),
// so it can be placed properly in VR.
using System;
using System.Reflection;
using System.Text;
using MoonCleaners;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class ScreenProbe
    {
        private const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static object Prop(object owner, string name)
        {
            var p = owner.GetType().GetProperty(name, I);
            return p?.GetValue(p.GetGetMethod(true).IsStatic ? null : owner);
        }
        private static float _next;
        private static bool _manualWas, _contractWas;
        private static int _logs;

        public static void Tick()
        {
            if (_logs > 12 || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MoonHowToPlay>()))
                {
                    var m = o.TryCast<MoonHowToPlay>();
                    if (m == null || !m.gameObject.scene.IsValid()) continue;
                    bool open = Prop(m, "IsOpen") is bool b1 && b1;
                    if (open && !_manualWas) { _manualWas = true; Dump("Field manual", m, new[] { "eye", "view", "overlay", "stage", "rig", "group", "texture", "book" }); }
                    if (!open) _manualWas = false;
                }
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<MoonNewHire>()))
                {
                    var n = o.TryCast<MoonNewHire>();
                    if (n == null || !n.gameObject.scene.IsValid()) continue;
                    bool open = Prop(n, "Active") is bool b2 && b2;
                    if (open && !_contractWas)
                    {
                        _contractWas = true;
                        Dump("Contract", n, new[] { "view", "group", "contractGroup", "beside" });
                        var clip = Prop(n, "clipboard");
                        if (clip != null) Dump("Contract clipboard", clip, new[] { "eye", "picture", "stage", "rig", "board" });
                    }
                    if (!open) _contractWas = false;
                }
            }
            catch (Exception e) { _logs++; Plugin.L.LogWarning("Probe error: " + (e.InnerException ?? e).Message); }
        }

        private static void Dump(string label, object owner, string[] names)
        {
            _logs++;
            var sb = new StringBuilder();
            sb.Append($"PROBE {label} opened:");
            foreach (var name in names)
            {
                object v = null;
                try { v = Prop(owner, name); } catch (Exception e) { v = "error " + e.Message; }
                sb.Append($"\n   {name} = {Describe(v)}");
            }
            sb.Append("\n   cameras:");
            foreach (var c in Camera.allCameras) sb.Append("\n     " + Describe(c));
            Plugin.L.LogInfo(sb.ToString());
        }

        private static string Path(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }

        private static string Describe(object v)
        {
            try
            {
                if (v == null) return "null";
                if (v is Camera c)
                {
                    var tt = c.targetTexture;
                    return $"Camera '{Path(c.transform)}' enabled {c.enabled}, target {(tt == null ? "SCREEN" : tt.width + "x" + tt.height)}, rect {c.rect}, depth {c.depth}, stereo {c.stereoTargetEye}, fov {c.fieldOfView:F0}, pos {c.transform.position}, rot {c.transform.eulerAngles}";
                }
                if (v is RenderTexture rt) return $"RenderTexture {rt.width}x{rt.height}";
                if (v is Texture tx) return $"Texture {tx.width}x{tx.height}";
                if (v is Transform t) return $"Transform '{Path(t)}' pos {t.position} rot {t.eulerAngles} scale {t.lossyScale}";
                if (v is Component comp)
                {
                    var canvas = comp.GetComponentInParent<Canvas>();
                    string cv = canvas == null ? "no canvas" : $"canvas '{canvas.rootCanvas.name}' mode {canvas.rootCanvas.renderMode}";
                    return $"{comp.GetIl2CppType().Name} on '{Path(comp.transform)}' active {comp.gameObject.activeInHierarchy}, {cv}";
                }
                if (v is GameObject go) return $"GameObject '{Path(go.transform)}' active {go.activeInHierarchy} pos {go.transform.position}";
                return v.GetType().Name + ": " + v;
            }
            catch (Exception e) { return "(" + e.Message + ")"; }
        }
    }
}
