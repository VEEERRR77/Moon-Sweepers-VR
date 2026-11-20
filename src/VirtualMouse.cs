// The game's chess table only listens to the mouse (it recomputes its square from a ray through
// the mouse position), so while the VR seated view at a chess table is active the laser is
// presented to the game as an ordinary mouse: a virtual Mouse device in Unity's Input System,
// fed raw state events each frame (same technique as VirtualPad).
// Unity Input System MouseState layout (sequential, little endian):
//   Vector2 position (8), Vector2 delta (8), Vector2 scroll (8), ushort buttons (2),
//   ushort displayIndex (2), ushort clickCount (2) -> 30 bytes.
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MoonXrStarter
{
    internal static unsafe class VirtualMouse
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static bool _tried, _ready, _loggedSend, _loggedErr, _loggedCurrent;
        private static int _deviceId;
        private static IntPtr _buffer;            // 20 byte InputEvent + 4 byte format + 30 byte MouseState
        private static MethodInfo _queue, _time;
        private static Type _ptrType;
        private static ConstructorInfo _ptrCtor;
        private static FieldInfo _ptrField;
        private static Vector2 _lastPos;
        private static bool _hasLast, _lastDown;

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
            if (_ptrCtor == null && _ptrField == null) throw new Exception("don't know how to build InputEventPtr");

            var state = FindType("UnityEngine.InputSystem.LowLevel.InputState");
            _time = state?.GetProperty("currentTime", Any)?.GetGetMethod(true);

            var device = add.Invoke(null, new object[] { "Mouse", "MoonVRMouse", null });
            if (device == null) throw new Exception("AddDevice returned null");
            _deviceId = Convert.ToInt32(device.GetType().GetProperty("deviceId", Any).GetValue(device));

            // If the game ignores the mouse position without focus, this makes our mouse the one.
            try
            {
                var make = device.GetType().GetMethod("MakeCurrent", Any);
                if (make != null && make.GetParameters().Length == 0)
                {
                    make.Invoke(device, null);
                    if (!_loggedCurrent) { _loggedCurrent = true; Plugin.L.LogInfo($"Mouse: made the virtual mouse current ({device.GetType().Name})"); }
                }
            }
            catch (Exception e) { Plugin.L.LogInfo("Mouse: MakeCurrent not available: " + (e.InnerException ?? e).Message); }

            _buffer = Marshal.AllocHGlobal(64);
            for (int i = 0; i < 64; i++) Marshal.WriteByte(_buffer, i, 0);
            _ready = true;
            Plugin.L.LogMessage($"Mouse: virtual mouse created (device {_deviceId}, ptr via {(_ptrCtor != null ? "ctor" : "field")})");
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

        /// <summary>Queues a mouse state event for the virtual mouse (lands next frame).</summary>
        public static void Send(Vector2 screenPos, bool leftDown)
        {
            try
            {
                if (!_tried) Setup();
                if (!_ready) return;
                if (_hasLast && screenPos == _lastPos && leftDown == _lastDown) return;
                var delta = _hasLast ? screenPos - _lastPos : Vector2.zero;
                _lastPos = screenPos; _lastDown = leftDown; _hasLast = true;

                double now = _time != null ? Convert.ToDouble(_time.Invoke(null, null)) : Time.realtimeSinceStartupAsDouble;
                byte* p = (byte*)_buffer;
                *(int*)(p + 0) = 0x53544154;          // 'STAT'
                *(ushort*)(p + 4) = 54;               // total size (20 + 4 + 30)
                *(ushort*)(p + 6) = (ushort)_deviceId;
                *(double*)(p + 8) = now;
                *(int*)(p + 16) = 0;                  // event id
                *(int*)(p + 20) = 0x4D4F5553;         // 'MOUS'
                *(float*)(p + 24) = screenPos.x; *(float*)(p + 28) = screenPos.y;   // position
                *(float*)(p + 32) = delta.x; *(float*)(p + 36) = delta.y;           // delta
                *(float*)(p + 40) = 0f; *(float*)(p + 44) = 0f;                     // scroll
                *(ushort*)(p + 48) = (ushort)(leftDown ? 1 : 0);                    // buttons (bit 0 = left)
                *(ushort*)(p + 50) = 0;                                             // displayIndex
                *(ushort*)(p + 52) = 0;                                             // clickCount
                _queue.Invoke(null, new[] { MakePtr() });
                if (!_loggedSend) { _loggedSend = true; Plugin.L.LogInfo($"Mouse: first virtual mouse event sent to the game at ({screenPos.x:F0},{screenPos.y:F0}) down {leftDown}"); }
            }
            catch (Exception e)
            {
                _ready = false;
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Mouse: virtual mouse error: " + (e.InnerException ?? e)); }
            }
        }
    }
}
