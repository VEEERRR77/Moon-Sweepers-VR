// Internet radio on your left wrist. A small panel floats above the left hand (show / hide with
// both grips + Y); point the laser at its buttons and pull the right trigger. Stations come from
// the config ("Name|stream URL" pairs separated by ";"). The audio is played by NAudio through
// Windows' default sound output, separately from the game's own sound.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class Radio
    {
        // ---------------------------------------------------------------- stations
        private class Station { public string Name, Url; }
        private static readonly List<Station> _stations = new List<Station>();
        private static string _stationsRaw;
        private static int _index;

        private static void RefreshStations()
        {
            string raw = Plugin.RadioStations.Value ?? "";
            if (raw == _stationsRaw) return;
            _stationsRaw = raw;
            _stations.Clear();
            foreach (var part in raw.Split(';'))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                int bar = p.IndexOf('|');
                string name = bar > 0 ? p.Substring(0, bar).Trim() : p;
                string url = bar > 0 ? p.Substring(bar + 1).Trim() : p;
                if (url.Length > 0) _stations.Add(new Station { Name = name, Url = url });
            }
            _index = Mathf.Clamp(Plugin.RadioStation.Value, 0, Math.Max(0, _stations.Count - 1));
        }

        // ---------------------------------------------------------------- audio (background thread)
        private static readonly object _lock = new object();
        private static IWavePlayer _out;
        private static MediaFoundationReader _reader;
        private static SampleChannel _channel;
        private static volatile string _status = "Off";
        private static volatile bool _wantPlaying;
        private static int _generation;   // bumps on every play/stop so old connections give up

        internal static bool Playing => _wantPlaying;

        private static void StopAudio()
        {
            lock (_lock)
            {
                try { _out?.Stop(); } catch { }
                try { _out?.Dispose(); } catch { }
                try { _reader?.Dispose(); } catch { }
                _out = null; _reader = null; _channel = null;
            }
        }

        private static void Play()
        {
            RefreshStations();
            if (_stations.Count == 0) { _status = "No stations in the config"; return; }
            var st = _stations[_index];
            _wantPlaying = true;
            int gen = Interlocked.Increment(ref _generation);
            _status = "Connecting...";
            Task.Run(() => Connect(st, gen));
        }

        private static void Connect(Station st, int gen)
        {
            StopAudio();
            for (int attempt = 0; attempt < 3 && gen == _generation; attempt++)
            {
                try
                {
                    var reader = new MediaFoundationReader(st.Url);
                    var channel = new SampleChannel(reader, true) { Volume = Volume };
                    var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 200);
                    output.Init(channel);
                    lock (_lock)
                    {
                        if (gen != _generation) { output.Dispose(); reader.Dispose(); return; }   // changed station meanwhile
                        _reader = reader; _channel = channel; _out = output;
                        output.PlaybackStopped += (s, e) =>
                        {
                            // The stream dropped: reconnect if we still want this station.
                            if (gen == _generation && _wantPlaying)
                            {
                                _status = "Reconnecting...";
                                Plugin.L.LogInfo("Radio: stream stopped (" + (e.Exception?.Message ?? "end of stream") + "), reconnecting");
                                Task.Delay(2000).ContinueWith(_ => { if (gen == _generation && _wantPlaying) Connect(st, gen); });
                            }
                        };
                        output.Play();
                    }
                    _status = "Playing";
                    Plugin.L.LogInfo($"Radio: playing '{st.Name}' ({st.Url})");
                    return;
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning($"Radio: couldn't play '{st.Name}': {(e.InnerException ?? e).Message}");
                    _status = "Can't connect - retrying";
                    Thread.Sleep(2000);
                }
            }
            if (gen == _generation) { _status = "Station unavailable"; }
        }

        private static float Volume => Mathf.Clamp01(Plugin.RadioVolume.Value);

        private static void Stop()
        {
            _wantPlaying = false;
            Interlocked.Increment(ref _generation);
            _status = "Off";
            Task.Run(StopAudio);
        }

        private static void TogglePlay() { if (_wantPlaying) Stop(); else Play(); }

        private static void Step(int d)
        {
            RefreshStations();
            if (_stations.Count == 0) return;
            _index = (_index + d + _stations.Count) % _stations.Count;
            Plugin.RadioStation.Value = _index;
            if (_wantPlaying) Play();
        }

        private static void ChangeVolume(float d)
        {
            Plugin.RadioVolume.Value = Mathf.Round(Mathf.Clamp01(Plugin.RadioVolume.Value + d) * 20f) / 20f;
            lock (_lock) { if (_channel != null) _channel.Volume = Volume; }
        }

        internal static void ToggleShown()
        {
            Plugin.RadioShown.Value = !Plugin.RadioShown.Value;
            Plugin.L.LogInfo("Radio: panel " + (Plugin.RadioShown.Value ? "shown" : "hidden"));
        }

        internal static void Shutdown() { _wantPlaying = false; Interlocked.Increment(ref _generation); StopAudio(); }

        // ---------------------------------------------------------------- panel
        private class Btn { public RectTransform Rect; public UnityEngine.UI.Image Bg; public TMPro.TextMeshProUGUI Label; public Action Act; }
        private static GameObject _panel;
        private static RectTransform _rect;
        private static TMPro.TextMeshProUGUI _title, _info;
        private static Btn _playBtn;
        private static readonly List<Btn> _btns = new List<Btn>();
        private static Btn _hover;
        private static bool _wasDown, _failed, _loggedErr;
        private static int _shownFrame = -100;
        internal static bool PanelShown => Time.frameCount - _shownFrame <= 2;
        /// <summary>True while the laser is on the radio panel (the right trigger then belongs to it).</summary>
        internal static bool Hovering => PanelShown && _laserOnPanel;
        private static bool _laserOnPanel;
        private static TMPro.TMP_FontAsset _font;

        private static readonly Color PanelColour = new Color(0.08f, 0.09f, 0.11f, 0.85f);
        private static readonly Color ButtonColour = new Color(0.20f, 0.22f, 0.26f, 1f);
        private static readonly Color HoverColour = new Color(1f, 0.85f, 0.2f, 1f);

        private static TMPro.TMP_FontAsset GetFont()
        {
            if (_font != null) return _font;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<TMPro.TMP_FontAsset>()))
            {
                var f = o.TryCast<TMPro.TMP_FontAsset>();
                if (f != null) { _font = f; break; }
            }
            return _font;
        }

        private static RectTransform Box(Transform parent, string name, Vector2 pos, Vector2 size, Color colour, out UnityEngine.UI.Image img)
        {
            var go = new GameObject(name);
            go.layer = 5;
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            img = go.AddComponent<UnityEngine.UI.Image>();
            img.color = colour;
            img.raycastTarget = false;
            return rt;
        }

        private static TMPro.TextMeshProUGUI Text(Transform parent, string name, Vector2 pos, Vector2 size, float fontSize, string text)
        {
            var go = new GameObject(name);
            go.layer = 5;
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var t = go.AddComponent<TMPro.TextMeshProUGUI>();
            var f = GetFont(); if (f != null) t.font = f;
            t.fontSize = fontSize;
            t.alignment = TMPro.TextAlignmentOptions.Center;
            t.color = Color.white;
            t.raycastTarget = false;
            t.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            t.text = text;
            return t;
        }

        private static Btn Button(string label, Vector2 pos, Vector2 size, Action act)
        {
            var rt = Box(_rect, "Button " + label, pos, size, ButtonColour, out var img);
            var t = Text(rt, "Label", Vector2.zero, size, 34f, label);
            var b = new Btn { Rect = rt, Bg = img, Label = t, Act = act };
            _btns.Add(b);
            return b;
        }

        private static void Build()
        {
            // Named "MoonXr..." so the pink-object fix never touches it.
            _panel = new GameObject("MoonXr radio panel");
            UnityEngine.Object.DontDestroyOnLoad(_panel);
            _panel.layer = 5;
            var canvas = _panel.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 3200;
            _rect = _panel.GetComponent<RectTransform>();
            _rect.sizeDelta = new Vector2(640f, 300f);
            var bg = _panel.AddComponent<UnityEngine.UI.Image>();
            bg.color = PanelColour;
            bg.raycastTarget = false;
            _title = Text(_rect, "Station", new Vector2(0f, 105f), new Vector2(600f, 60f), 40f, "Radio");
            _info = Text(_rect, "Status", new Vector2(0f, 55f), new Vector2(600f, 40f), 28f, "");
            _info.color = new Color(0.75f, 0.75f, 0.78f, 1f);
            Button("<", new Vector2(-220f, -25f), new Vector2(150f, 90f), () => Step(-1));
            _playBtn = Button("Play", new Vector2(0f, -25f), new Vector2(240f, 90f), TogglePlay);
            Button(">", new Vector2(220f, -25f), new Vector2(150f, 90f), () => Step(1));
            Button("Vol -", new Vector2(-150f, -115f), new Vector2(240f, 60f), () => ChangeVolume(-0.1f));
            Button("Vol +", new Vector2(150f, -115f), new Vector2(240f, 60f), () => ChangeVolume(0.1f));
            Plugin.L.LogInfo("Radio: panel built");
        }

        private static bool HitPanel(Vector3 p, Vector3 dir, out Vector3 point, out float dist)
        {
            point = default; dist = 0f;
            if (_rect == null || !PanelShown) return false;
            var plane = new Plane(_rect.forward, _rect.position);
            if (!plane.Raycast(new Ray(p, dir), out dist) || dist <= 0f) return false;
            point = p + dir * dist;
            Vector3 l = _rect.InverseTransformPoint(point);
            return _rect.rect.Contains(new Vector2(l.x, l.y));
        }

        /// <summary>For the laser: stop on the radio panel.</summary>
        internal static bool TryClip(Vector3 p, Vector3 dir, out float dist)
        {
            dist = 0f;
            try { return HitPanel(p, dir, out _, out dist); } catch { return false; }
        }

        /// <summary>Called every frame just before the UI is drawn (with the game camera in place).</summary>
        internal static void Tick()
        {
            if (_failed) return;
            try
            {
                bool show = Plugin.RadioEnabled.Value && Plugin.RadioShown.Value && Plugin.MotionControlsEnabled.Value
                            && !HeadTracking.TableSessionActive;
                Vector3 hp = default, eye = default; Quaternion hr = Quaternion.identity;
                if (show) show = MotionControls.TryGetHandPose(true, out hp, out hr) && HeadTracking.TryGetHeadWorld(out eye);
                if (!show)
                {
                    if (_panel != null && _panel.activeSelf) _panel.SetActive(false);
                    _laserOnPanel = false;
                    return;
                }
                if (_panel == null) Build();
                if (!_panel.activeSelf) _panel.SetActive(true);
                _shownFrame = Time.frameCount;

                // Above the left hand (higher than the wrist HUD), turned to face you.
                var up = HeadTracking.WorldUp(hp);
                Vector3 pos = hp + up * Plugin.RadioHeight.Value;
                var toPanel = pos - eye;
                if (toPanel.sqrMagnitude < 0.0001f) return;
                float s = Plugin.RadioWidth.Value / 640f;
                _rect.SetPositionAndRotation(pos, Quaternion.LookRotation(toPanel.normalized, up));
                _rect.localScale = new Vector3(s, s, s);

                RefreshStations();
                _title.text = _stations.Count > 0 ? _stations[_index].Name : "No stations";
                _info.text = $"{_status}   -   volume {Mathf.RoundToInt(Volume * 100f)}%";
                _playBtn.Label.text = _wantPlaying ? "Stop" : "Play";

                // Laser: highlight the button it's on; the right trigger presses it.
                _hover = null; _laserOnPanel = false;
                if (MotionControls.TryGetHandAim(out var ap, out var ar) && HitPanel(ap, ar * Vector3.forward, out var pt, out _))
                {
                    _laserOnPanel = true;
                    foreach (var b in _btns)
                    {
                        Vector3 l = b.Rect.InverseTransformPoint(pt);
                        if (b.Rect.rect.Contains(new Vector2(l.x, l.y))) { _hover = b; break; }
                    }
                }
                foreach (var b in _btns)
                {
                    bool h = b == _hover;
                    b.Bg.color = h ? HoverColour : ButtonColour;
                    b.Label.color = h ? Color.black : Color.white;
                }
                bool down = MotionControls.RawRightTrigger > 0.6f;
                if (down && !_wasDown && _hover != null) _hover.Act();
                _wasDown = down;
            }
            catch (Exception e)
            {
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogError("Radio: " + e); }
                _failed = true;
                try { if (_panel != null) _panel.SetActive(false); } catch { }
            }
        }
    }
}

// The build references the game's own core library, which lacks these two attributes; the compiler
// needs them for lambdas over NAudio's events, so they're declared here.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;
        public NullableAttribute(byte flag) { NullableFlags = new[] { flag }; }
        public NullableAttribute(byte[] flags) { NullableFlags = flags; }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Delegate | AttributeTargets.Interface | AttributeTargets.Method | AttributeTargets.Struct, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;
        public NullableContextAttribute(byte flag) { Flag = flag; }
    }
}
