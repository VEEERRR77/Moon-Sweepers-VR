// Internet radio on your left wrist. A small panel floats above the left hand (show / hide with
// both grips + Y); point the laser at its buttons and pull the right trigger. Stations come from
// the config ("Name|stream URL" pairs separated by ";"), and Search finds more in the free
// radio-browser.info directory (Save adds the one playing to your list). The audio is played by
// NAudio through Windows' default sound output, separately from the game's own sound.
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
        private class Station { public string Name, Url, Info; }
        private static Station _searchStation;   // a station picked from search results (not in the list)
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
            var st = _searchStation;
            if (st == null)
            {
                if (_stations.Count == 0) { _status = "No stations in the config"; return; }
                st = _stations[_index];
            }
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
            if (_searchStation != null) _searchStation = null;   // back to your own list
            else _index = (_index + d + _stations.Count) % _stations.Count;
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

        // ---------------------------------------------------------------- search (Radio Browser)
        // radio-browser.info: a free, community-run directory of internet radio stations.
        private static readonly string[] Servers = { "de1.api.radio-browser.info", "nl1.api.radio-browser.info", "at1.api.radio-browser.info" };
        private static readonly List<Station> _results = new List<Station>();
        private static volatile string _searchStatus = "";
        private static volatile bool _searching;
        private static int _resultsVersion, _shownResultsVersion = -1;

        private static async Task<List<Station>> Query(System.Net.Http.HttpClient http, string field, string q)
        {
            var list = new List<Station>();
            foreach (var host in Servers)
            {
                try
                {
                    string url = $"https://{host}/json/stations/search?{field}={Uri.EscapeDataString(q)}&limit=40&hidebroken=true&order=clickcount&reverse=true";
                    string json = await http.GetStringAsync(url);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        string Get(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : "";
                        string codec = Get("codec").ToUpperInvariant();
                        if (codec.Length > 0 && codec != "MP3" && codec != "AAC" && codec != "AAC+") continue;   // what Windows can play
                        string stream = Get("url_resolved"); if (stream.Length == 0) stream = Get("url");
                        if (stream.Length == 0) continue;
                        int br = e.TryGetProperty("bitrate", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Number ? b.GetInt32() : 0;
                        string country = Get("countrycode");
                        string tags = Get("tags"); if (tags.Length > 40) tags = tags.Substring(0, 40);
                        list.Add(new Station
                        {
                            Name = Get("name").Trim(), Url = stream,
                            Info = (country.Length > 0 ? country + "  " : "") + (br > 0 ? br + " kbps  " : "") + tags.Replace(",", ", ")
                        });
                    }
                    return list;
                }
                catch (Exception e) { Plugin.L.LogInfo($"Radio: search on {host} failed ({(e.InnerException ?? e).Message})"); }
            }
            return list;
        }

        private static void Search(string q)
        {
            q = (q ?? "").Trim();
            if (q.Length == 0 || _searching) return;
            _searching = true;
            _searchStatus = "Searching for \"" + q + "\"...";
            Task.Run(async () =>
            {
                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("MoonSweepersVR/" + Plugin.Version);
                    var byName = await Query(http, "name", q);
                    var byTag = await Query(http, "tag", q.ToLowerInvariant());
                    var seen = new HashSet<string>();
                    var all = new List<Station>();
                    foreach (var st in byName) if (seen.Add(st.Url)) all.Add(st);
                    foreach (var st in byTag) if (seen.Add(st.Url)) all.Add(st);
                    lock (_results) { _results.Clear(); _results.AddRange(all); _resultsVersion++; }
                    _searchStatus = all.Count == 0 ? "No stations found for \"" + q + "\"" : all.Count + " stations for \"" + q + "\"";
                    Plugin.L.LogInfo($"Radio: search \"{q}\" found {all.Count} stations");
                }
                catch (Exception e)
                {
                    _searchStatus = "Search failed - check your internet";
                    Plugin.L.LogWarning("Radio: search failed: " + (e.InnerException ?? e).Message);
                }
                finally { _searching = false; }
            });
        }

        private static string Clean(string t) => (t ?? "").Replace("|", "/").Replace(";", ",").Trim();

        // Add the station you're listening to (or a search result) to your own list in the config.
        private static void SaveCurrent()
        {
            RefreshStations();
            var st = _searchStation;
            if (st == null) { _flash = "Already in your list"; _flashUntil = Time.unscaledTime + 2f; return; }
            foreach (var x in _stations) if (x.Url == st.Url) { _flash = "Already in your list"; _flashUntil = Time.unscaledTime + 2f; return; }
            string raw = (Plugin.RadioStations.Value ?? "").Trim().TrimEnd(';');
            Plugin.RadioStations.Value = (raw.Length > 0 ? raw + "; " : "") + Clean(st.Name) + "|" + st.Url;
            RefreshStations();
            _index = _stations.Count - 1;
            Plugin.RadioStation.Value = _index;
            _searchStation = null;
            _flash = "Saved to your stations"; _flashUntil = Time.unscaledTime + 2f;
            Plugin.L.LogInfo($"Radio: saved '{st.Name}' to the station list");
        }

        // ---------------------------------------------------------------- panel
        private enum View { Player, Keyboard, Results }
        private class Btn { public RectTransform Rect; public UnityEngine.UI.Image Bg; public TMPro.TextMeshProUGUI Label; public Action Act; public View On; public bool Live = true; }
        private static GameObject _panel;
        private static RectTransform _rect;
        private static readonly Dictionary<View, RectTransform> _views = new Dictionary<View, RectTransform>();
        private static View _view = View.Player;
        private static TMPro.TextMeshProUGUI _title, _info, _query, _kbStatus, _resStatus;
        private static Btn _playBtn;
        private static readonly List<Btn> _btns = new List<Btn>();
        private static readonly List<Btn> _resultBtns = new List<Btn>();
        private static int _page;
        private const int PerPage = 6;
        private static string _typed = "";
        private static string _flash; private static float _flashUntil;
        private static Btn _hover;
        private static bool _wasDown, _failed, _loggedErr;
        private static int _shownFrame = -100;
        internal static bool PanelShown => Time.frameCount - _shownFrame <= 2;
        /// <summary>True while the laser is on the radio panel (the right trigger then belongs to it).</summary>
        internal static bool Hovering => PanelShown && _laserOnPanel;
        private static bool _laserOnPanel;
        private static TMPro.TMP_FontAsset _font;

        private static readonly Color PanelColour = new Color(0.08f, 0.09f, 0.11f, 0.88f);
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

        private static RectTransform Group(string name)
        {
            var go = new GameObject(name);
            go.layer = 5;
            go.transform.SetParent(_rect, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero; rt.sizeDelta = _rect.sizeDelta;
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

        private static Btn Button(View view, string label, Vector2 pos, Vector2 size, Action act, float fontSize = 34f)
        {
            var rt = Box(_views[view], "Button " + label, pos, size, ButtonColour, out var img);
            var t = Text(rt, "Label", Vector2.zero, size - new Vector2(12f, 0f), fontSize, label);
            var b = new Btn { Rect = rt, Bg = img, Label = t, Act = act, On = view };
            _btns.Add(b);
            return b;
        }

        private static void Show(View v)
        {
            _view = v;
            foreach (var kv in _views) kv.Value.gameObject.SetActive(kv.Key == v);
            _rect.sizeDelta = v == View.Player ? new Vector2(640f, 380f) : new Vector2(640f, 520f);
            if (_bg != null) _bg.rectTransform.sizeDelta = _rect.sizeDelta;
        }
        private static UnityEngine.UI.Image _bg;

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
            _rect.sizeDelta = new Vector2(640f, 520f);
            _bg = _panel.AddComponent<UnityEngine.UI.Image>();
            _bg.color = PanelColour;
            _bg.raycastTarget = false;
            foreach (View v in Enum.GetValues(typeof(View))) _views[v] = Group(v + " view");

            // Player
            var pl = _views[View.Player];
            _title = Text(pl, "Station", new Vector2(0f, 145f), new Vector2(600f, 60f), 40f, "Radio");
            _info = Text(pl, "Status", new Vector2(0f, 95f), new Vector2(600f, 40f), 26f, "");
            _info.color = new Color(0.75f, 0.75f, 0.78f, 1f);
            Button(View.Player, "<", new Vector2(-220f, 20f), new Vector2(150f, 90f), () => Step(-1));
            _playBtn = Button(View.Player, "Play", new Vector2(0f, 20f), new Vector2(240f, 90f), TogglePlay);
            Button(View.Player, ">", new Vector2(220f, 20f), new Vector2(150f, 90f), () => Step(1));
            Button(View.Player, "Vol -", new Vector2(-150f, -70f), new Vector2(240f, 60f), () => ChangeVolume(-0.1f));
            Button(View.Player, "Vol +", new Vector2(150f, -70f), new Vector2(240f, 60f), () => ChangeVolume(0.1f));
            Button(View.Player, "Search", new Vector2(-150f, -145f), new Vector2(240f, 60f), () => Show(View.Keyboard));
            Button(View.Player, "Save", new Vector2(150f, -145f), new Vector2(240f, 60f), SaveCurrent);

            // Keyboard
            var kb = _views[View.Keyboard];
            Box(kb, "Query box", new Vector2(0f, 205f), new Vector2(600f, 60f), new Color(0f, 0f, 0f, 0.6f), out _);
            _query = Text(kb, "Query", new Vector2(0f, 205f), new Vector2(580f, 60f), 36f, "");
            _kbStatus = Text(kb, "Hint", new Vector2(0f, 155f), new Vector2(600f, 36f), 24f, "Type a station name or genre (jazz, rock, bbc...)");
            _kbStatus.color = new Color(0.75f, 0.75f, 0.78f, 1f);
            string[] rows = { "1234567890", "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM" };
            for (int r = 0; r < rows.Length; r++)
            {
                string row = rows[r];
                float w = 58f, gap = 4f;
                float x0 = -(row.Length * (w + gap) - gap) / 2f + w / 2f;
                for (int c = 0; c < row.Length; c++)
                {
                    char ch = row[c];
                    Button(View.Keyboard, ch.ToString(), new Vector2(x0 + c * (w + gap), 95f - r * 64f), new Vector2(w, 58f),
                           () => { if (_typed.Length < 30) _typed += char.ToLowerInvariant(ch); }, 30f);
                }
            }
            Button(View.Keyboard, "Space", new Vector2(-160f, -165f), new Vector2(180f, 60f), () => { if (_typed.Length < 30 && _typed.Length > 0 && !_typed.EndsWith(" ")) _typed += " "; }, 28f);
            Button(View.Keyboard, "Delete", new Vector2(40f, -165f), new Vector2(180f, 60f), () => { if (_typed.Length > 0) _typed = _typed.Substring(0, _typed.Length - 1); }, 28f);
            Button(View.Keyboard, "Clear", new Vector2(220f, -165f), new Vector2(140f, 60f), () => _typed = "", 28f);
            Button(View.Keyboard, "Back", new Vector2(-160f, -230f), new Vector2(180f, 56f), () => Show(View.Player), 28f);
            Button(View.Keyboard, "Search", new Vector2(130f, -230f), new Vector2(340f, 56f), () => { if (_typed.Trim().Length > 0) { _page = 0; _shownResultsVersion = -1; Search(_typed); Show(View.Results); } }, 30f);

            // Results
            var rs = _views[View.Results];
            _resStatus = Text(rs, "Results status", new Vector2(0f, 220f), new Vector2(600f, 40f), 26f, "");
            for (int i = 0; i < PerPage; i++)
            {
                int slot = i;
                var b = Button(View.Results, "", new Vector2(0f, 160f - i * 62f), new Vector2(600f, 56f), () => PickResult(slot), 24f);
                b.Label.alignment = TMPro.TextAlignmentOptions.Left;
                _resultBtns.Add(b);
            }
            Button(View.Results, "< Prev", new Vector2(-205f, -230f), new Vector2(180f, 56f), () => { if (_page > 0) _page--; _shownResultsVersion = -1; }, 26f);
            Button(View.Results, "New search", new Vector2(0f, -230f), new Vector2(210f, 56f), () => Show(View.Keyboard), 26f);
            Button(View.Results, "Next >", new Vector2(205f, -230f), new Vector2(180f, 56f), () => { lock (_results) { if ((_page + 1) * PerPage < _results.Count) _page++; } _shownResultsVersion = -1; }, 26f);

            Show(View.Player);
            Plugin.L.LogInfo("Radio: panel built");
        }

        private static void PickResult(int slot)
        {
            Station st = null;
            lock (_results) { int i = _page * PerPage + slot; if (i < _results.Count) st = _results[i]; }
            if (st == null) return;
            _searchStation = st;
            Play();
            Show(View.Player);
        }

        private static void RefreshResults()
        {
            if (_shownResultsVersion == _resultsVersion) return;
            _shownResultsVersion = _resultsVersion;
            lock (_results)
            {
                for (int i = 0; i < PerPage; i++)
                {
                    int k = _page * PerPage + i;
                    var b = _resultBtns[i];
                    bool has = k < _results.Count;
                    b.Live = has;
                    b.Rect.gameObject.SetActive(has);
                    if (has) b.Label.text = $"<b>{_results[k].Name}</b>  <size=18><color=#BBBBBB>{_results[k].Info}</color></size>";
                }
            }
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

                // Above the left hand (higher than the wrist HUD), turned to face you. The search
                // pages (keyboard, results) are shown bigger so the keys are easy to hit.
                var up = HeadTracking.WorldUp(hp);
                float width = Plugin.RadioWidth.Value * (_view == View.Player ? 1f : 1.6f);
                float s = width / 640f;
                Vector3 pos = hp + up * (Plugin.RadioHeight.Value + _rect.sizeDelta.y * s * 0.5f - 0.06f);
                var toPanel = pos - eye;
                if (toPanel.sqrMagnitude < 0.0001f) return;
                _rect.SetPositionAndRotation(pos, Quaternion.LookRotation(toPanel.normalized, up));
                _rect.localScale = new Vector3(s, s, s);

                RefreshStations();
                if (_view == View.Player)
                {
                    _title.text = _searchStation != null ? _searchStation.Name : _stations.Count > 0 ? _stations[_index].Name : "No stations";
                    string msg = _flash != null && Time.unscaledTime < _flashUntil ? _flash : _status;
                    _info.text = $"{msg}   -   volume {Mathf.RoundToInt(Volume * 100f)}%";
                    _playBtn.Label.text = _wantPlaying ? "Stop" : "Play";
                }
                else if (_view == View.Keyboard) _query.text = _typed.Length > 0 ? _typed + "_" : "<color=#888888>search...</color>";
                else { _resStatus.text = _searchStatus + (_page > 0 ? $"  (page {_page + 1})" : ""); RefreshResults(); }

                // Laser: highlight the button it's on; the right trigger presses it.
                _hover = null; _laserOnPanel = false;
                if (MotionControls.TryGetHandAim(out var ap, out var ar) && HitPanel(ap, ar * Vector3.forward, out var pt, out _))
                {
                    _laserOnPanel = true;
                    foreach (var b in _btns)
                    {
                        if (b.On != _view || !b.Live) continue;
                        Vector3 l = b.Rect.InverseTransformPoint(pt);
                        if (b.Rect.rect.Contains(new Vector2(l.x, l.y))) { _hover = b; break; }
                    }
                }
                foreach (var b in _btns)
                {
                    if (b.On != _view) continue;
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
