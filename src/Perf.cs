// Timing for the mod's own per-frame and periodic work, to find what causes frame drops.
// It only measures and writes to the log; it doesn't change what anything does.
//   - any single piece of mod work taking 4 ms or more is logged straight away (rate-limited)
//   - when a frame takes 50 ms or more (under 20 fps) the mod work done in that frame is logged
//   - every 30 s: a summary of the most expensive pieces of mod work
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class Perf
    {
        private class Stat { public double Total, Max, Frame; public int Count, Spikes; }
        private static readonly Dictionary<string, Stat> _stats = new Dictionary<string, Stat>();
        private static readonly double _msPerTick = 1000.0 / Stopwatch.Frequency;
        private static int _frame = -1;
        private static float _nextSummary = 60f;
        private static int _slowFrames;
        private static int _slowFrameLogs;

        internal static bool On => Plugin.PerfLog != null && Plugin.PerfLog.Value;
        internal static long Now() => On ? Stopwatch.GetTimestamp() : 0L;

        internal static void Add(string name, long start)
        {
            if (start == 0L) return;
            double ms = (Stopwatch.GetTimestamp() - start) * _msPerTick;
            try
            {
                NewFrame();
                if (!_stats.TryGetValue(name, out var s)) _stats[name] = s = new Stat();
                s.Total += ms; s.Count++; s.Frame += ms;
                if (ms > s.Max) s.Max = ms;
                if (ms >= 4.0 && s.Spikes++ < 20)
                    Plugin.L.LogWarning($"Perf: {name} took {ms:F1} ms");
            }
            catch { }
        }

        // Called once per frame (start of the mod's Update).
        internal static void Frame()
        {
            if (!On) return;
            try
            {
                float dt = Time.unscaledDeltaTime;
                if (dt >= 0.05f)
                {
                    _slowFrames++;
                    if (_slowFrameLogs++ < 40)
                    {
                        var parts = _stats.Where(kv => kv.Value.Frame >= 0.5).OrderByDescending(kv => kv.Value.Frame)
                                          .Take(5).Select(kv => $"{kv.Key} {kv.Value.Frame:F1}");
                        double mod = _stats.Values.Sum(v => v.Frame);
                        Plugin.L.LogWarning($"Perf: slow frame {dt * 1000f:F0} ms; mod work in the last frame {mod:F1} ms" +
                                            (mod >= 0.5 ? " (" + string.Join(", ", parts) + ")" : " - the slowdown wasn't the mod"));
                    }
                }
                foreach (var s in _stats.Values) s.Frame = 0;
                _frame = Time.frameCount;
                if (Time.unscaledTime >= _nextSummary)
                {
                    _nextSummary = Time.unscaledTime + 30f;
                    var top = _stats.OrderByDescending(kv => kv.Value.Total).Take(8)
                                    .Select(kv => $"{kv.Key}: avg {kv.Value.Total / Math.Max(1, kv.Value.Count):F2} ms, max {kv.Value.Max:F1} ms, x{kv.Value.Count}");
                    Plugin.L.LogInfo($"Perf summary (last 30 s, {_slowFrames} slow frames): " + string.Join(" | ", top));
                    foreach (var s in _stats.Values) { s.Total = 0; s.Max = 0; s.Count = 0; }
                    _slowFrames = 0;
                }
            }
            catch { }
        }

        private static void NewFrame()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
        }
    }
}
