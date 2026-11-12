// Presents the Quest controllers to the game as an ordinary gamepad by creating a virtual
// Gamepad in Unity's Input System and feeding it raw state events each frame.
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MoonXrStarter
{
    internal static unsafe class VirtualPad
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static bool _tried, _ready, _loggedSend, _loggedErr;
        private static int _deviceId;
        private static IntPtr _buffer;            // 20 byte InputEvent + 4 byte format + 28 byte GamepadState
        private static MethodInfo _queue, _time;
        private static Type _ptrType;
        private static ConstructorInfo _ptrCtor;
        private static FieldInfo _ptrField;
        private static uint _lastButtons; private static Vector2 _lastL, _lastR; private static float _lastLT, _lastRT;

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        private static void Setup()
        {
            _tried = true;
            var sys = FindType("UnityEngine.InputSystem.InputSystem");
            if (sys == null) throw new Exception("InputSystem type not found");

            MethodInfo add = null;
            foreach (var m in sys.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var ps = m.GetParameters();
                if (m.Name == "AddDevice" && !m.IsGenericMethodDefinition && ps.Length == 3 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string)) add = m;
                if (m.Name == "QueueEvent" && !m.IsGenericMethodDefinition && ps.Length == 1 && ps[0].ParameterType.Name == "InputEventPtr") _queue = m;
            }
            if (add == null) throw new Exception("InputSystem.AddDevice(string,string,string) not found");
            if (_queue == null) throw new Exception("InputSystem.QueueEvent(InputEventPtr) not found");

            _ptrType = _queue.GetParameters()[0].ParameterType;
            foreach (var c in _ptrType.GetConstructors(Any))
            {
                var ps = c.GetParameters();
                if (ps.Length == 1 && (ps[0].ParameterType.IsPointer || ps[0].ParameterType == typeof(IntPtr))) _ptrCtor = c;
            }
            foreach (var f in _ptrType.GetFields(Any))
                if (!f.IsStatic && (f.FieldType.IsPointer || f.FieldType == typeof(IntPtr))) _ptrField = f;
            if (_ptrCtor == null && _ptrField == null)
            {
                string desc = "";
                foreach (var f in _ptrType.GetFields(Any)) desc += f.FieldType.Name + " " + f.Name + "; ";
                foreach (var c in _ptrType.GetConstructors(Any)) { desc += "ctor("; foreach (var q in c.GetParameters()) desc += q.ParameterType.Name + ","; desc += "); "; }
                throw new Exception("don't know how to build InputEventPtr: " + desc);
            }

            var state = FindType("UnityEngine.InputSystem.LowLevel.InputState");
            _time = state?.GetProperty("currentTime", Any)?.GetGetMethod(true);

            var device = add.Invoke(null, new object[] { "Gamepad", "MoonVRPad", null });
            if (device == null) throw new Exception("AddDevice returned null");
            _deviceId = Convert.ToInt32(device.GetType().GetProperty("deviceId", Any).GetValue(device));

            _buffer = Marshal.AllocHGlobal(64);
            for (int i = 0; i < 64; i++) Marshal.WriteByte(_buffer, i, 0);
            _ready = true;
            Plugin.L.LogMessage($"Motion: virtual gamepad created (device {_deviceId}, ptr via {(_ptrCtor != null ? "ctor" : "field")})");
        }

        private static object MakePtr()
        {
            if (_ptrCtor != null)
            {
                var pt = _ptrCtor.GetParameters()[0].ParameterType;
                return _ptrCtor.Invoke(new object[] { pt.IsPointer ? Pointer.Box((void*)_buffer, pt) : (object)_buffer });
            }
            object boxed = Activator.CreateInstance(_ptrType);
            _ptrField.SetValue(boxed, _ptrField.FieldType.IsPointer ? Pointer.Box((void*)_buffer, _ptrField.FieldType) : (object)_buffer);
            return boxed;
        }

        public static void Send(uint buttons, Vector2 left, Vector2 right, float lt, float rt)
        {
            try
            {
                if (!_tried) Setup();
                if (!_ready) return;
                if (buttons == _lastButtons && left == _lastL && right == _lastR && lt == _lastLT && rt == _lastRT) return;
                _lastButtons = buttons; _lastL = left; _lastR = right; _lastLT = lt; _lastRT = rt;

                double now = _time != null ? Convert.ToDouble(_time.Invoke(null, null)) : Time.realtimeSinceStartupAsDouble;
                byte* p = (byte*)_buffer;
                *(int*)(p + 0) = 0x53544154;          // 'STAT'
                *(ushort*)(p + 4) = 52;               // total size
                *(ushort*)(p + 6) = (ushort)_deviceId;
                *(double*)(p + 8) = now;
                *(int*)(p + 16) = 0;                  // event id
                *(int*)(p + 20) = 0x47504144;         // 'GPAD'
                *(uint*)(p + 24) = buttons;
                *(float*)(p + 28) = left.x; *(float*)(p + 32) = left.y;
                *(float*)(p + 36) = right.x; *(float*)(p + 40) = right.y;
                *(float*)(p + 44) = lt; *(float*)(p + 48) = rt;
                _queue.Invoke(null, new[] { MakePtr() });
                if (!_loggedSend) { _loggedSend = true; Plugin.L.LogInfo("Motion: first controller input sent to the game"); }
            }
            catch (Exception e)
            {
                _ready = false;
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Motion: virtual gamepad error: " + (e.InnerException ?? e)); }
            }
        }
    }
}
