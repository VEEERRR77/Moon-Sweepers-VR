// EXPERIMENT (Level 3): motion controls.
//  - The game's sweeping is driven by MoonBrush.Sweep(Ray ...). We replace that ray with one
//    coming out of the right-hand controller, so you clean where you point your hand.
//  - Quest thumbsticks/triggers are fed into the game's own input getters (MoonInput).
// Everything is wrapped so a failure only disables this feature and logs why.
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace MoonXrStarter
{
    internal static class MotionControls
    {
        private static bool _installed;
        private static int _frame = -1;
        private static bool _rightOk, _leftOk;
        private static Vector3 _rPos; private static Quaternion _rRot;
        private static float _rTrigger, _lTrigger;
        private static Vector2 _rStick, _lStick;
        private static float _nextViewAdj, _nextToolAdj;
        private static bool _recenterHeld, _laserToggleHeld, _hudToggleHeld;
        private static float _nextColourAdj, _nextBrightAdj;
        internal static Vector3 HandPos; internal static Quaternion HandRot = Quaternion.identity; internal static int HandFrame = -1;
        private static bool _loggedRay, _loggedInput, _loggedErr, _loggedValues;
        private static Vector2 Dead(Vector2 v) => v.sqrMagnitude < 0.02f ? Vector2.zero : v;
        internal static void Tick()
        {
            // Safety net: if a patched game method ever bailed out mid-way, put the camera back.
            if (_depth != 0 || _swapped != null) { _depth = 1; BrushPost(); }
            if (_installed && Plugin.MotionControlsEnabled.Value) Poll();
        }

        public static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                var h = new Harmony("claude.moonxrstarter.motion");
                var brush = AccessTools.TypeByName("MoonCleaners.MoonBrush");
                var late = brush != null ? AccessTools.Method(brush, "LateUpdate") : null;
                if (!Plugin.MotionHandAim.Value) Plugin.L.LogInfo("Motion: hand aim disabled in config");
                else if (late != null)
                {
                    h.Patch(late,
                        prefix: new HarmonyMethod(typeof(MotionControls).GetMethod(nameof(BrushPre), BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(MotionControls).GetMethod(nameof(BrushPost), BindingFlags.Static | BindingFlags.NonPublic)));
                    Plugin.L.LogMessage("Motion: patched MoonBrush.LateUpdate (hand aim)");
                }
                else Plugin.L.LogWarning("Motion: MoonBrush.LateUpdate not found");

                // Interactions (pick up, use, open...) are decided by where the game camera looks.
                // Run those checks from the right hand too, so you point at things to highlight them.
                if (Plugin.MotionHandAim.Value && Plugin.HandInteraction.Value && brush != null)
                {
                    var pre = new HarmonyMethod(typeof(MotionControls).GetMethod(nameof(BrushPre), BindingFlags.Static | BindingFlags.NonPublic));
                    var post = new HarmonyMethod(typeof(MotionControls).GetMethod(nameof(BrushPost), BindingFlags.Static | BindingFlags.NonPublic));
                    Type[] types;
                    try { types = brush.Assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                    var extra = new System.Collections.Generic.HashSet<string> { "MoonDustHand", "MoonCoffee", "MoonCatcherLook", "MoonLoungePlayer" };
                    const BindingFlags D = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                    int n = 0; string names = "";
                    foreach (var t in types)
                    {
                        if (t == null || t.Namespace != "MoonCleaners" || t.IsNested) continue;
                        if (!t.Name.EndsWith("Interaction") && !extra.Contains(t.Name)) continue;
                        bool any = false;
                        foreach (var mn in new[] { "Update", "LateUpdate" })
                        {
                            try
                            {
                                var m = t.GetMethod(mn, D, null, Type.EmptyTypes, null);
                                if (m == null) continue;
                                h.Patch(m, prefix: pre, postfix: post);
                                n++; any = true;
                            }
                            catch (Exception e) { Plugin.L.LogWarning($"Motion: could not hook {t.Name}.{mn}: {e.Message}"); }
                        }
                        if (any) names += t.Name.Replace("Moon", "").Replace("Interaction", "") + " ";
                    }
                    Plugin.L.LogMessage($"Motion: hand-aimed interaction on {n} methods: {names}");
                }

                MenuFix.Install(h);

                // Hide the brush reticle while sweeping (only its drawing is skipped).
                var draw = brush != null ? AccessTools.Method(brush, "DrawBrushReticle") : null;
                _hideReticle = brush != null ? AccessTools.Method(brush, "HideBrushReticle") : null;
                if (draw != null)
                {
                    h.Patch(draw, prefix: new HarmonyMethod(typeof(MotionControls).GetMethod(nameof(ReticlePre), BindingFlags.Static | BindingFlags.NonPublic)));
                    Plugin.L.LogMessage("Motion: reticle hiding while sweeping installed (hide method " + (_hideReticle != null ? "found" : "MISSING") + ")");
                }
                else Plugin.L.LogWarning("Motion: MoonBrush.DrawBrushReticle not found");

                // The game's menus read sticks/triggers through its own MoonInput helper, which
                // ignores our virtual gamepad's analog values - so top those up directly.
                _moonInput = AccessTools.TypeByName("MoonCleaners.MoonInput");
                if (Plugin.MotionInput.Value && _moonInput != null)
                {
                    PatchGetter(h, _moonInput, "LeftStick", nameof(LeftStickPost));
                    PatchGetter(h, _moonInput, "RightStick", nameof(RightStickPost));
                    PatchGetter(h, _moonInput, "LeftTrigger", nameof(LeftTriggerPost));
                    PatchGetter(h, _moonInput, "RightTrigger", nameof(RightTriggerPost));
                }

            }
            catch (Exception e)
            {
                Plugin.L.LogError("Motion: install failed: " + e);
            }
        }

        private static Type _moonInput;
        private static Vector2 _sentL, _sentR; private static float _sentLT, _sentRT;
        private static int _diag;

        private static void LeftStickPost(ref Vector2 __result)
        {
            if (_sentL.sqrMagnitude > 0.25f && _diag < 3)
            {
                _diag++;
                try
                {
                    string cur = "none"; var gp = UnityEngine.InputSystem.Gamepad.current; if (gp != null) cur = gp.name;
                    object P(string n) { try { return _moonInput.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null); } catch { return "?"; } }
                    Plugin.L.LogInfo($"Motion: menu check - game saw left stick {__result}, ours {_sentL}, current pad '{cur}', usingGamepad {P("UsingGamepad")}, resting {P("ControllerResting")}");
                }
                catch { }
            }
            if (_sentL.sqrMagnitude > __result.sqrMagnitude) __result = _sentL;
        }
        private static void RightStickPost(ref Vector2 __result) { if (_sentR.sqrMagnitude > __result.sqrMagnitude) __result = _sentR; }
        private static void LeftTriggerPost(ref float __result) { if (_sentLT > __result) __result = _sentLT; }
        private static void RightTriggerPost(ref float __result) { if (_sentRT > __result) __result = _sentRT; }

        private static void PatchGetter(Harmony h, Type t, string prop, string post)
        {
            try
            {
                var g = AccessTools.PropertyGetter(t, prop);
                if (g == null) { Plugin.L.LogWarning("Motion: no getter " + prop); return; }
                h.Patch(g, postfix: new HarmonyMethod(typeof(MotionControls).GetMethod(post, BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.L.LogInfo("Motion: patched MoonInput." + prop);
            }
            catch (Exception e) { Plugin.L.LogWarning("Motion: could not patch " + prop + ": " + e.Message); }
        }

        // Read both controllers once per frame.
        private static void Poll()
        {
            int f = Time.frameCount;
            if (f == _frame) return;
            _frame = f;
            _rightOk = _leftOk = _leftPoseOk = false;
            _rTrigger = _lTrigger = 0f; _rStick = _lStick = Vector2.zero;
            uint buttons = 0;
            try
            {
                EnsureReflection();
                ulong rid = DeviceId(XRNode.RightHand), lid = DeviceId(XRNode.LeftHand);
                if (rid != 0UL && rid != ulong.MaxValue)
                {
                    _rRot = InputTracking.GetLocalRotation(XRNode.RightHand);
                    _rPos = InputTracking.GetLocalPosition(XRNode.RightHand);
                    _rightOk = !(_rRot.x == 0f && _rRot.y == 0f && _rRot.z == 0f && _rRot.w == 0f);
                    if (!_inputBroken) try
                    {
                        _rTrigger = ReadFloat(rid, _uTrigger); _rStick = ReadVec2(rid, _uAxis);
                    } catch { _inputBroken = true; }
                }
                if (lid != 0UL && lid != ulong.MaxValue)
                {
                    _leftOk = true;
                    _lRot = InputTracking.GetLocalRotation(XRNode.LeftHand);
                    _lPos = InputTracking.GetLocalPosition(XRNode.LeftHand);
                    _leftPoseOk = !(_lRot.x == 0f && _lRot.y == 0f && _lRot.z == 0f && _lRot.w == 0f);
                    if (!_inputBroken) try
                    {
                        _lTrigger = ReadFloat(lid, _uTrigger); _lStick = ReadVec2(lid, _uAxis);
                    } catch { _inputBroken = true; }
                }
                // Preferred source: the Input System's own XR controller devices.
                if (Plugin.MotionInput.Value && XrPadSource.Read(out uint b2, out Vector2 l2, out Vector2 r2, out float lt2, out float rt2))
                {
                    buttons |= b2;
                    if (l2.sqrMagnitude > _lStick.sqrMagnitude) _lStick = l2;
                    if (r2.sqrMagnitude > _rStick.sqrMagnitude) _rStick = r2;
                    _lTrigger = Mathf.Max(_lTrigger, lt2); _rTrigger = Mathf.Max(_rTrigger, rt2);
                }
                // Both grips held: sticks adjust heights instead of moving you.
                //   right stick up/down = your view height, left stick up/down = tool height.
                if ((buttons & (1u << 10)) != 0 && (buttons & (1u << 11)) != 0)
                {
                    float now = Time.unscaledTime;
                    if (Mathf.Abs(_rStick.y) > 0.6f && now >= _nextViewAdj) { _nextViewAdj = now + 0.3f; HeadTracking.ChangeHeight(_rStick.y > 0f ? 0.05f : -0.05f); }
                    if (Mathf.Abs(_lStick.y) > 0.6f && now >= _nextToolAdj) { _nextToolAdj = now + 0.3f; HeadTracking.ChangeToolHeight(_lStick.y > 0f ? 0.05f : -0.05f); }
                    // ...right stick left/right changes the laser colour...
                    if (Mathf.Abs(_rStick.x) > 0.7f && now >= _nextColourAdj) { _nextColourAdj = now + 0.4f; Laser.CycleColour(_rStick.x > 0f ? 1 : -1); }
                    // ...left stick left/right changes the laser brightness...
                    if (Mathf.Abs(_lStick.x) > 0.7f && now >= _nextBrightAdj) { _nextBrightAdj = now + 0.25f; Laser.ChangeBrightness(_lStick.x > 0f ? 1 : -1); }
                    _lStick = _rStick = Vector2.zero;
                    // ...X turns the laser on/off...
                    bool x = (buttons & (1u << 7)) != 0;
                    if (x && !_laserToggleHeld) Laser.Toggle();
                    _laserToggleHeld = x;
                    buttons &= ~(1u << 7);
                    // ...left stick click hides / shows the HUD...
                    bool lsc = (buttons & (1u << 8)) != 0;
                    if (lsc && !_hudToggleHeld) UiToWorld.ToggleHud();
                    _hudToggleHeld = lsc;
                    buttons &= ~(1u << 8);
                    // ...and A recenters the view (kept away from the game so you don't jump).
                    bool a = (buttons & (1u << 6)) != 0;
                    if (a && !_recenterHeld) { HeadTracking.Recenter(); Plugin.L.LogInfo("Head tracking: recentered (grips + A)"); }
                    _recenterHeld = a;
                    buttons &= ~(1u << 6);
                }
                else { _recenterHeld = false; _laserToggleHeld = false; _hudToggleHeld = false; }
                // Right stick up/down is passed to the game, so it looks up/down the same way
                // left/right turns you.
                _sentL = Dead(_lStick); _sentR = Dead(_rStick); _sentLT = _lTrigger < 0.05f ? 0f : _lTrigger; _sentRT = _rTrigger < 0.05f ? 0f : _rTrigger;
                if (Plugin.MotionInput.Value && (_rightOk || _leftOk))
                    VirtualPad.Send(buttons, Dead(_lStick), Dead(_rStick), _lTrigger < 0.05f ? 0f : _lTrigger, _rTrigger < 0.05f ? 0f : _rTrigger);
                if (!_loggedValues && (_rTrigger > 0.5f || _lTrigger > 0.5f || _lStick.sqrMagnitude > 0.25f || _rStick.sqrMagnitude > 0.25f || buttons != 0))
                {
                    _loggedValues = true;
                    Plugin.L.LogInfo($"Motion: controller readings OK (LT {_lTrigger:F1} RT {_rTrigger:F1} L {_lStick} R {_rStick} buttons {buttons})");
                }
                if (!_loggedInput && (_rightOk || _leftOk))
                {
                    _loggedInput = true;
                    Plugin.L.LogInfo($"Motion: controllers detected (right pose {_rightOk}, left {_leftOk})");
                }
            }
            catch (Exception e)
            {
                _inputBroken = true; // stop retrying stick/trigger reads; hand pose still works
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Motion: poll error: " + e); }
            }
        }

        private static MethodInfo _mDeviceId, _mFloat, _mVec2, _mBool;
        private static object _uGrip, _uPrimary, _uSecondary, _uStickClick, _uMenu;
        // ---- reticle + laser support
        private static MethodInfo _hideReticle;
        private static PropertyInfo _sweepingProp, _reticleMatProp;
        private static bool _reticleHidden, _loggedReticleErr;
        internal static object Brush;
        internal static bool BrushSweeping;

        /// <summary>True while the player is sweeping (sweep trigger held, or the brush reports it is cleaning).</summary>
        internal static bool SweepHeld()
        {
            Poll();
            if (_rTrigger > 0.3f || BrushSweeping) return true;
            try { var gp = UnityEngine.InputSystem.Gamepad.current; if (gp != null && gp.rightTrigger.isPressed) return true; } catch { }
            return false;
        }

        private static Vector3 _lPos; private static Quaternion _lRot;
        private static bool _leftPoseOk;

        /// <summary>A controller's pose in world space (no aiming tilt).</summary>
        internal static bool TryGetHandPose(bool left, out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = Quaternion.identity;
            if (!_installed || !Plugin.MotionControlsEnabled.Value) return false;
            Poll();
            if (left ? !_leftPoseOk : !_rightOk) return false;
            return HeadTracking.TrackingToWorld(left ? _lPos : _rPos, left ? _lRot : _rRot, out pos, out rot);
        }

        /// <summary>The right-hand aiming pose in world space (the same one the game's aiming uses).</summary>
        internal static bool TryGetHandAim(out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = Quaternion.identity;
            if (!_installed || !Plugin.MotionControlsEnabled.Value || !Plugin.MotionHandAim.Value) return false;
            Poll();
            if (!_rightOk) return false;
            if (!HeadTracking.TrackingToWorld(_rPos, _rRot, out var wp, out var wr)) return false;
            pos = wp; rot = wr * Quaternion.Euler(Plugin.HandAimPitch.Value, 0f, 0f);
            return true;
        }

        /// <summary>The brush's reticle material (used to colour the laser), or null.</summary>
        internal static Material ReticleMaterial()
        {
            try
            {
                if (Brush == null) return null;
                if (_reticleMatProp == null) _reticleMatProp = Brush.GetType().GetProperty("reticleMaterial", AnyInst);
                return _reticleMatProp?.GetValue(Brush) as Material;
            }
            catch { return null; }
        }

        private static bool ReticlePre(object __instance)
        {
            try
            {
                Brush = __instance;
                if (_sweepingProp == null) _sweepingProp = __instance.GetType().GetProperty("Sweeping", AnyInst);
                BrushSweeping = _sweepingProp != null && _sweepingProp.GetValue(__instance) is bool b && b;
                bool hide = Plugin.HideReticle.Value
                            || (Plugin.MotionControlsEnabled.Value && Plugin.HideReticleWhileSweeping.Value && SweepHeld());
                if (!hide)
                {
                    _reticleHidden = false;
                    return true;            // draw the reticle as normal
                }
                if (!_reticleHidden)
                {
                    _reticleHidden = true;
                    _hideReticle?.Invoke(__instance, null);
                }
                return false;               // skip drawing it while sweeping
            }
            catch (Exception e)
            {
                if (!_loggedReticleErr) { _loggedReticleErr = true; Plugin.L.LogError("Motion: reticle hide error: " + (e.InnerException ?? e)); }
                return true;
            }
        }

        private static bool _reflected, _inputBroken;
        private static object _uTrigger, _uAxis;
        private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static MethodInfo Find(Type t, string name, int paramCount)
        {
            foreach (var m in t.GetMethods(AnyStatic))
                if (m.Name == name && m.GetParameters().Length == paramCount) return m;
            return null;
        }

        // Unity's string-taking functions are broken under this loader, so we call the raw
        // "_Injected" versions and hand them a pinned UTF-16 string wrapper ourselves.
        internal static unsafe object MakeSpan(Type wrapperType, string text)
        {
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(text, System.Runtime.InteropServices.GCHandleType.Pinned);
            IntPtr ptr = handle.AddrOfPinnedObject();
            object boxed = Activator.CreateInstance(wrapperType);
            bool setPtr = false, setLen = false;
            foreach (var f in wrapperType.GetFields(AnyInst))
            {
                if (f.FieldType == typeof(int)) { f.SetValue(boxed, text.Length); setLen = true; }
                else if (f.FieldType == typeof(IntPtr)) { f.SetValue(boxed, ptr); setPtr = true; }
                else if (f.FieldType.IsPointer) { f.SetValue(boxed, Pointer.Box((void*)ptr, f.FieldType)); setPtr = true; }
            }
            if (!setPtr || !setLen)
            {
                var names = "";
                foreach (var f in wrapperType.GetFields(AnyInst)) names += f.FieldType.Name + " " + f.Name + "; ";
                foreach (var pr in wrapperType.GetProperties(AnyInst)) names += "[prop] " + pr.PropertyType.Name + " " + pr.Name + "; ";
                throw new Exception("span wrapper layout not understood: " + wrapperType.FullName + " { " + names + "}");
            }
            return boxed;
        }

        private static void EnsureReflection()
        {
            if (_reflected) return;
            _reflected = true;
            _mDeviceId = Find(typeof(InputTracking), "GetDeviceIdAtXRNode", 1);
            _mFloat = Find(typeof(InputDevices), "TryGetFeatureValue_float_Injected", 3);
            _mVec2 = Find(typeof(InputDevices), "TryGetFeatureValue_Vector2f_Injected", 3);
            _mBool = Find(typeof(InputDevices), "TryGetFeatureValue_bool_Injected", 3);
            Plugin.L.LogInfo($"Motion: input functions found: id={_mDeviceId != null} float={_mFloat != null} vec2={_mVec2 != null}");
            if (_mFloat != null)
            {
                var wt = _mFloat.GetParameters()[1].ParameterType;
                if (wt.IsByRef) wt = wt.GetElementType();
                _uTrigger = MakeSpan(wt, "Trigger");
                _uAxis = MakeSpan(wt, "Primary2DAxis");
                _uGrip = MakeSpan(wt, "Grip");
                _uPrimary = MakeSpan(wt, "PrimaryButton");
                _uSecondary = MakeSpan(wt, "SecondaryButton");
                _uStickClick = MakeSpan(wt, "Primary2DAxisClick");
                _uMenu = MakeSpan(wt, "MenuButton");
            }
        }

        private static ulong DeviceId(XRNode node)
        {
            if (_mDeviceId == null) return 0UL;
            return Convert.ToUInt64(_mDeviceId.Invoke(null, new object[] { node }));
        }

        private static float ReadFloat(ulong id, object usage)
        {
            if (_mFloat == null || usage == null) return 0f;
            var args = new object[] { id, usage, 0f };
            return (bool)_mFloat.Invoke(null, args) ? (float)args[2] : 0f;
        }

        private static bool ReadBool(ulong id, object usage)
        {
            if (_mBool == null || usage == null) return false;
            var args = new object[] { id, usage, false };
            return (bool)_mBool.Invoke(null, args) && (bool)args[2];
        }

        private static Vector2 ReadVec2(ulong id, object usage)
        {
            if (_mVec2 == null || usage == null) return Vector2.zero;
            var args = new object[] { id, usage, Vector2.zero };
            return (bool)_mVec2.Invoke(null, args) ? (Vector2)args[2] : Vector2.zero;
        }

        // While the brush runs its per-frame logic, temporarily put the game camera where the
        // right hand is (aimed along the controller), so aiming/cleaning follows the hand.
        private static Transform _swapped;
        private static Vector3 _savedPos; private static Quaternion _savedRot;

        private static int _depth;

        private static void BrushPre()
        {
            if (_depth++ > 0) return;   // already aiming from the hand (nested call)
            try
            {
                _swapped = null;
                if (!Plugin.MotionControlsEnabled.Value) return;
                Poll();
                if (!_rightOk) return;
                Vector3 wp; Quaternion wr;
                if (!HeadTracking.TrackingToWorld(_rPos, _rRot, out wp, out wr)) return;
                var main = Camera.main;
                if (main == null) return;
                var aim = wr * Quaternion.Euler(Plugin.HandAimPitch.Value, 0f, 0f);
                var t = main.transform;
                _savedPos = t.position; _savedRot = t.rotation;
                t.SetPositionAndRotation(wp, aim);
                HandPos = wp; HandRot = aim; HandFrame = Time.frameCount;
                _swapped = t;
                if (!_loggedRay) { _loggedRay = true; Plugin.L.LogInfo($"Motion: aiming from hand at {wp}"); }
            }
            catch (Exception e)
            {
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Motion: brush prefix error: " + e); }
            }
        }

        private static void BrushPost()
        {
            if (--_depth > 0) return;
            _depth = 0;
            try { if (_swapped != null) _swapped.SetPositionAndRotation(_savedPos, _savedRot); } catch { }
            _swapped = null;
        }

    }
}
