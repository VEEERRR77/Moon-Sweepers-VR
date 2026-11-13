// Reads the Quest controllers straight from SteamVR (OpenVR "actions" input). The Unity-side
// XR plugin that UUVR ships only reports controller positions, not buttons, so we ask SteamVR
// ourselves using the Xbox-style action set UUVR already installs in StreamingAssets/SteamVR.
using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using UnityEngine;

namespace MoonXrStarter
{
    internal static unsafe class XrPadSource
    {
        private static bool _tried, _ready, _loggedErr, _loggedInput, _loggedInactive;
        private static void** _fn;                  // IVRInput function table
        private static ulong _set;
        private static ulong _a, _b, _x, _y, _start, _select, _lb, _rb, _lt, _rt, _ls, _rs, _lsc, _rsc;
        private static byte* _active, _dig, _ana;   // scratch structs
        private static int _frames;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetGenericInterfaceFn(IntPtr version, int* error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte IsInterfaceVersionValidFn(IntPtr version);

        private static IntPtr Utf8(string s) => Marshal.StringToCoTaskMemUTF8(s);

        private static ulong Handle(string name, bool set = false)
        {
            ulong h = 0;
            IntPtr p = Utf8(name);
            int err = set
                ? ((delegate* unmanaged[Cdecl]<IntPtr, ulong*, int>)_fn[1])(p, &h)
                : ((delegate* unmanaged[Cdecl]<IntPtr, ulong*, int>)_fn[2])(p, &h);
            Marshal.FreeCoTaskMem(p);
            if (err != 0 || h == 0) Plugin.L.LogWarning($"Motion: SteamVR handle for {name} failed (error {err})");
            return h;
        }

        private static void Setup()
        {
            _tried = true;
            string dataDir = Path.Combine(Paths.GameRootPath, "MoonSweepers_Data");
            IntPtr lib;
            if (!NativeLibrary.TryLoad("openvr_api", out lib))
                lib = NativeLibrary.Load(Path.Combine(dataDir, "Plugins", "openvr_api.dll"));
            var getIf = Marshal.GetDelegateForFunctionPointer<GetGenericInterfaceFn>(NativeLibrary.GetExport(lib, "VR_GetGenericInterface"));
            var valid = Marshal.GetDelegateForFunctionPointer<IsInterfaceVersionValidFn>(NativeLibrary.GetExport(lib, "VR_IsInterfaceVersionValid"));

            string version = null;
            foreach (var v in new[] { "IVRInput_010", "IVRInput_007", "IVRInput_006", "IVRInput_005" })
            {
                IntPtr pv = Utf8(v);
                bool ok = valid(pv) != 0;
                Marshal.FreeCoTaskMem(pv);
                if (ok) { version = v; break; }
            }
            if (version == null) throw new Exception("SteamVR offers no supported IVRInput version");

            int error = 0;
            IntPtr pn = Utf8("FnTable:" + version);
            IntPtr table = getIf(pn, &error);
            Marshal.FreeCoTaskMem(pn);
            if (table == IntPtr.Zero) throw new Exception($"could not get {version} from SteamVR (error {error})");
            _fn = (void**)table;

            string manifest = Path.Combine(dataDir, "StreamingAssets", "SteamVR", "actions.json");
            IntPtr pm = Utf8(manifest);
            int merr = ((delegate* unmanaged[Cdecl]<IntPtr, int>)_fn[0])(pm);
            Marshal.FreeCoTaskMem(pm);
            Plugin.L.LogInfo($"Motion: SteamVR input {version}, action manifest result {merr}");

            _set = Handle("/actions/Xbox", true);
            _a = Handle("/actions/Xbox/in/A"); _b = Handle("/actions/Xbox/in/B");
            _x = Handle("/actions/Xbox/in/X"); _y = Handle("/actions/Xbox/in/Y");
            _start = Handle("/actions/Xbox/in/Start"); _select = Handle("/actions/Xbox/in/Select");
            _lb = Handle("/actions/Xbox/in/LB"); _rb = Handle("/actions/Xbox/in/RB");
            _lt = Handle("/actions/Xbox/in/LT"); _rt = Handle("/actions/Xbox/in/RT");
            _ls = Handle("/actions/Xbox/in/StickLeft"); _rs = Handle("/actions/Xbox/in/StickRight");
            _lsc = Handle("/actions/Xbox/in/StickLeftClick"); _rsc = Handle("/actions/Xbox/in/StickRightClick");
            _rest = Handle("/actions/Xbox/in/DUp");   // bound to "right thumb rest touched" in our binding file
            if (_set == 0) throw new Exception("SteamVR action set not available");

            _active = (byte*)Marshal.AllocHGlobal(32);
            _dig = (byte*)Marshal.AllocHGlobal(24);
            _ana = (byte*)Marshal.AllocHGlobal(48);
            for (int i = 0; i < 32; i++) _active[i] = 0;
            *(ulong*)_active = _set;
            _ready = true;
            Plugin.L.LogMessage("Motion: reading Quest controllers from SteamVR");
        }

        private static ulong _rest;
        /// <summary>True while the right thumb is resting on the right controller's thumb rest.</summary>
        internal static bool ThumbRest;
        private static bool _loggedRest, _loggedRestBound, _loggedRestUnbound;

        private static bool _anyActive;
        private static bool Digital(ulong h)
        {
            if (h == 0) return false;
            for (int i = 0; i < 24; i++) _dig[i] = 0;
            int err = ((delegate* unmanaged[Cdecl]<ulong, byte*, uint, ulong, int>)_fn[5])(h, _dig, 24u, 0UL);
            if (err != 0) return false;
            if (_dig[0] != 0) _anyActive = true;
            return _dig[0] != 0 && _dig[16] != 0;
        }

        private static Vector2 Analog(ulong h)
        {
            if (h == 0) return Vector2.zero;
            for (int i = 0; i < 48; i++) _ana[i] = 0;
            int err = ((delegate* unmanaged[Cdecl]<ulong, byte*, uint, ulong, int>)_fn[6])(h, _ana, 48u, 0UL);
            if (err != 0 || _ana[0] == 0) return Vector2.zero;
            _anyActive = true;
            return new Vector2(*(float*)(_ana + 16), *(float*)(_ana + 20));
        }

        /// <summary>Returns false if the controllers could not be read this frame.</summary>
        public static bool Read(out uint buttons, out Vector2 ls, out Vector2 rs, out float lt, out float rt)
        {
            buttons = 0; ls = rs = Vector2.zero; lt = rt = 0f;
            try
            {
                if (!_tried) Setup();
                if (!_ready) return false;
                int err = ((delegate* unmanaged[Cdecl]<byte*, uint, uint, int>)_fn[4])(_active, 32u, 1u);
                if (err != 0)
                {
                    if (!_loggedErr) { _loggedErr = true; Plugin.L.LogWarning($"Motion: SteamVR UpdateActionState error {err}"); }
                    return false;
                }
                _anyActive = false;
                if (Digital(_a)) buttons |= 1u << 6;
                if (Digital(_b)) buttons |= 1u << 5;
                if (Digital(_x)) buttons |= 1u << 7;
                if (Digital(_y)) buttons |= 1u << 4;
                if (Digital(_start)) buttons |= 1u << 12;
                if (Digital(_select)) buttons |= 1u << 13;
                if (Digital(_lb)) buttons |= 1u << 10;
                if (Digital(_rb)) buttons |= 1u << 11;
                if (Digital(_lsc)) buttons |= 1u << 8;
                if (Digital(_rsc)) buttons |= 1u << 9;
                bool before = _anyActive; _anyActive = false;
                ThumbRest = Digital(_rest);
                bool restBound = _anyActive; _anyActive |= before;
                if (restBound && !_loggedRestBound) { _loggedRestBound = true; Plugin.L.LogInfo("Motion: thumb rest is bound in SteamVR"); }
                if (ThumbRest && !_loggedRest) { _loggedRest = true; Plugin.L.LogInfo("Motion: right thumb rest touched - left stick is now the D-pad"); }
                if (!restBound && !_loggedRestUnbound && _frames == 600) { _loggedRestUnbound = true; Plugin.L.LogWarning("Motion: thumb rest is NOT bound in SteamVR (new binding file not loaded yet?)"); }
                ls = Analog(_ls); rs = Analog(_rs);
                lt = Analog(_lt).x; rt = Analog(_rt).x;

                _frames++;
                if (!_loggedInactive && _frames == 600 && !_anyActive)
                {
                    _loggedInactive = true;
                    Plugin.L.LogWarning("Motion: SteamVR reports no active bindings for the controllers (binding file not applied?)");
                }
                if (!_loggedInput && (buttons != 0 || lt > 0.5f || rt > 0.5f || ls.sqrMagnitude > 0.25f || rs.sqrMagnitude > 0.25f))
                {
                    _loggedInput = true;
                    Plugin.L.LogInfo($"Motion: SteamVR input received (buttons {buttons}, LT {lt:F1}, RT {rt:F1}, L {ls}, R {rs})");
                }
                return true;
            }
            catch (Exception e)
            {
                _ready = false;
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Motion: SteamVR input error: " + e); }
                return false;
            }
        }
    }
}
