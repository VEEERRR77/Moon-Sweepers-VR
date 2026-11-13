// Laser pointer from the right hand, shown when you're not sweeping. It follows the same aim
// the game uses for highlighting items and machines, and stops at whatever it hits.
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MoonXrStarter
{
    internal static class Laser
    {
        private const float Width = 0.003f;      // beam thickness (m)
        private const float DotSize = 0.025f;    // dot at the hit point (m)
        internal static readonly string[] ColourNames = { "Yellow", "Blue", "Red", "Green", "White" };
        private static readonly Color[] Colours =
        {
            new Color(1f, 0.92f, 0.1f), new Color(0.2f, 0.35f, 1f), new Color(1f, 0.1f, 0.1f),
            new Color(0.15f, 1f, 0.2f), Color.white
        };
        private static Material _beamMat, _dotMat;
        private static float _appliedBrightness = -1f;
        private static string _appliedColour;

        private static int ColourIndex()
        {
            string want = (Plugin.LaserColour.Value ?? "").Trim();
            for (int i = 0; i < ColourNames.Length; i++)
                if (string.Equals(ColourNames[i], want, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        /// <summary>Step through the laser colours (dir = +1 / -1). Saved to the config.</summary>
        internal static void CycleColour(int dir)
        {
            int i = (ColourIndex() + dir + ColourNames.Length) % ColourNames.Length;
            Plugin.LaserColour.Value = ColourNames[i];
            if (!Plugin.LaserPointer.Value) Plugin.LaserPointer.Value = true;
            Plugin.L.LogInfo("Laser: colour " + ColourNames[i]);
        }

        /// <summary>Step the brightness up (+1) or down (-1). Saved to the config.</summary>
        internal static void ChangeBrightness(int dir)
        {
            float[] steps = { 0.01f, 0.02f, 0.03f, 0.05f, 0.08f, 0.12f, 0.2f, 0.35f, 0.5f, 0.75f, 1f };
            float cur = Plugin.LaserBrightness.Value;
            int best = 0;
            for (int i = 0; i < steps.Length; i++) if (Mathf.Abs(steps[i] - cur) < Mathf.Abs(steps[best] - cur)) best = i;
            int next = Mathf.Clamp(best + dir, 0, steps.Length - 1);
            Plugin.LaserBrightness.Value = steps[next];
            if (!Plugin.LaserPointer.Value) Plugin.LaserPointer.Value = true;
            Plugin.L.LogInfo($"Laser: brightness {steps[next] * 100f:F0}%");
        }

        internal static void Toggle()
        {
            Plugin.LaserPointer.Value = !Plugin.LaserPointer.Value;
            if (Plugin.LaserPointer.Value)
            {
                _failed = false; _errCount = 0;   // give it a fresh start if it had hit an error
                if (Plugin.LaserBrightness.Value < 0.03f) Plugin.LaserBrightness.Value = 0.03f;   // don't come back invisible
            }
            Plugin.L.LogInfo("Laser: " + (Plugin.LaserPointer.Value ? "on" : "off"));
        }

        private static void ApplyLook()
        {
            float b = Mathf.Clamp01(Plugin.LaserBrightness.Value);
            string colour = Plugin.LaserColour.Value;
            bool map = MapFix.Open;
            if (map) { b = 1f; colour = "Yellow"; }   // on the map: solid bright yellow
            if (b == _appliedBrightness && colour == _appliedColour) return;
            _appliedBrightness = b; _appliedColour = colour;
            if (_dot != null) _dot.transform.localScale = Vector3.one * (map ? DotSize * 1.8f : DotSize);
            // Dim the laser by making it see-through rather than darker: a dark colour just looks
            // black in the headset, whereas a faint transparent line keeps its colour.
            var c = map ? Colours[0] : Colours[ColourIndex()];
            float alpha = Mathf.Clamp(Mathf.Sqrt(b) * 1.2f, 0.08f, 1f);
            float k = Mathf.Lerp(0.6f, 1f, b);
            var tint = new Color(c.r * k, c.g * k, c.b * k, alpha);
            if (_beamMat != null) Dim(_beamMat, tint);
            if (_dotMat != null) Dim(_dotMat, tint);
        }

        private static GameObject _beam, _dot;
        private static bool _failed, _loggedReady, _loggedErr;
        private static int _errCount;

        public static void Tick()
        {
            if (_failed) return;
            try
            {
                Vector3 p = Vector3.zero; Quaternion r = Quaternion.identity;
                bool want = Plugin.LaserPointer.Value
                            && MotionControls.TryGetHandAim(out p, out r)
                            && !MotionControls.SweepHeld();
                if (!want || !Ensure()) { Show(false, false); return; }
                ApplyLook();

                Vector3 dir = r * Vector3.forward;
                float len = Mathf.Max(0.1f, Plugin.LaserLength.Value);
                Vector3 end = p + dir * len;
                bool hit = false;
                RaycastHit info;
                if (Physics.Raycast(p, dir, out info, len))
                {
                    len = Mathf.Max(0.01f, info.distance);
                    end = info.point;
                    hit = true;
                }
                if (MapFix.TryClip(p, dir, out float md) && md < len)
                {
                    len = Mathf.Max(0.01f, md - 0.01f); end = p + dir * len; hit = true;   // stop just in front of the map panel
                }

                var bt = _beam.transform;
                bt.SetPositionAndRotation(p + dir * (len * 0.5f), Quaternion.LookRotation(dir));
                bt.localScale = new Vector3(Width, Width, len);
                _dot.transform.position = end;
                Show(true, hit);
            }
            catch (Exception e)
            {
                // Don't give up for good (e.g. a scene change destroyed the beam): rebuild it
                // next frame, and only log the error once.
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Laser: error, rebuilding: " + e); }
                try { Show(false, false); } catch { }
                _beam = null; _dot = null;
                if (++_errCount > 300) _failed = true;   // persistent fault: stop until toggled back on
            }
        }

        private static bool Alive(GameObject g)
        {
            try { return g != null && g.transform != null; } catch { return false; }
        }

        private static void Show(bool beam, bool dot)
        {
            if (Alive(_beam) && _beam.activeSelf != beam) _beam.SetActive(beam);
            if (Alive(_dot) && _dot.activeSelf != (beam && dot)) _dot.SetActive(beam && dot);
        }

        // The game has no working default material (plain shapes render with Unity's error shader),
        // so borrow a simple unlit material that the game already loaded.
        private static readonly string[] Preferred =
        {
            "Universal Render Pipeline/Unlit", "Unlit/Color", "UI/Default", "Sprites/Default"
        };

        private static Material FindUnlitMaterial(out string shaderName)
        {
            shaderName = null;
            Material best = null; int bestRank = int.MaxValue;
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<Material>()))
                {
                    var m = o.TryCast<Material>();
                    if (m == null || m.shader == null) continue;
                    string n = m.shader.name;
                    int rank = Array.IndexOf(Preferred, n);
                    if (rank >= 0 && rank < bestRank) { best = m; bestRank = rank; shaderName = n; if (rank == 0) break; }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Laser: material search failed: " + e.Message); }
            if (best == null)
            {
                try { best = UnityEngine.UI.Graphic.defaultGraphicMaterial; shaderName = "UI default"; } catch { }
            }
            return best;
        }

        private static bool Ensure()
        {
            if (Alive(_beam) && Alive(_dot)) return true;
            var src = FindUnlitMaterial(out var shaderName);
            _beam = Make(PrimitiveType.Cube, src);
            _dot = Make(PrimitiveType.Sphere, src);
            _dot.transform.localScale = Vector3.one * DotSize;
            _appliedBrightness = -1f;   // re-apply colour and dot size to the new objects
            if (!_loggedReady)
            {
                _loggedReady = true;
                Plugin.L.LogMessage("Laser: pointer ready, using " + (src != null ? $"'{shaderName}' material" : "NO material (laser may look wrong)"));
            }
            return true;
        }

        private static System.Reflection.MethodInfo _propertyToId;
        private static bool _loggedDimErr, _loggedTint;

        // Shader.PropertyToID(string) is broken under this loader, so call its raw version.
        private static int PropertyId(string name)
        {
            if (_propertyToId == null)
                _propertyToId = typeof(Shader).GetMethod("PropertyToID_Injected",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var wt = _propertyToId.GetParameters()[0].ParameterType;
            if (wt.IsByRef) wt = wt.GetElementType();
            return (int)_propertyToId.Invoke(null, new[] { MotionControls.MakeSpan(wt, name) });
        }

        private static void Dim(Material m, Color c)
        {
            try
            {
                foreach (var prop in new[] { "_BaseColor", "_Color", "_TintColor" }) m.SetColor(PropertyId(prop), c);
                m.SetColor(PropertyId("_EmissionColor"), Color.black);
                if (!_loggedTint)
                {
                    _loggedTint = true;
                    string shader = "?"; try { shader = m.shader != null ? m.shader.name : "none"; } catch { }
                    var back = m.GetColor(PropertyId("_BaseColor"));
                    Plugin.L.LogInfo($"Laser: tint {c} applied, shader '{shader}', reads back {back}");
                }
            }
            catch (Exception e)
            {
                if (!_loggedDimErr) { _loggedDimErr = true; Plugin.L.LogWarning("Laser: couldn't change brightness: " + (e.InnerException ?? e).Message); }
            }
        }

        private static GameObject Make(PrimitiveType shape, Material src)
        {
            var go = GameObject.CreatePrimitive(shape);
            go.layer = 2;   // Ignore Raycast, so the laser never blocks the game's own aiming
            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);
            // The UI material multiplies by vertex colour, which plain shapes don't have: add white.
            try
            {
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null)
                {
                    var mesh = mf.mesh;   // per-object copy, the shared built-in mesh is left alone
                    var cols = new Color[mesh.vertexCount];
                    for (int i = 0; i < cols.Length; i++) cols[i] = Color.white;
                    mesh.colors = cols;
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Laser: couldn't set mesh colours: " + e.Message); }
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                if (src != null) mr.sharedMaterial = new Material(src);
                else mr.sharedMaterial = new Material(mr.sharedMaterial);
                mr.sharedMaterial.renderQueue = 4000;   // draw after the floating menus, so the beam stays visible on them
                mr.sortingOrder = 32767;                 // menus sort by their canvas order first, so outrank them too
                if (shape == PrimitiveType.Cube) _beamMat = mr.sharedMaterial; else _dotMat = mr.sharedMaterial;
                _appliedBrightness = -1f;   // re-tint the new materials
            }
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.SetActive(false);
            return go;
        }
    }
}
