// Checks GitHub once at start-up for a newer Moon Sweepers VR release, and if there is one shows a
// notice above the main menu (and in the log). It never downloads or installs anything.
using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace MoonXrStarter
{
    internal static class UpdateCheck
    {
        internal const string Repo = "VEEERRR77/Moon-Sweepers-VR";
        private static volatile string _newer;      // tag of a newer release, once found
        private static TMPro.TextMeshProUGUI _text;
        private static bool _started, _dismissed, _menuSeen, _loggedErr;

        internal static void Start()
        {
            if (_started || !Plugin.CheckForUpdates.Value) return;
            _started = true;
            Task.Run(async () =>
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("MoonSweepersVR/" + Plugin.Version);
                    http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    string json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                    var m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                    if (!m.Success) { Plugin.L.LogInfo("Update check: no release found"); return; }
                    string tag = m.Groups[1].Value;
                    if (IsNewer(tag, Plugin.Version))
                    {
                        _newer = tag;
                        Plugin.L.LogMessage($"Update check: Moon Sweepers VR {tag} is available (you have v{Plugin.Version}) - https://github.com/{Repo}/releases/latest");
                    }
                    else Plugin.L.LogInfo($"Update check: up to date (v{Plugin.Version}, latest {tag})");
                }
                catch (Exception e) { Plugin.L.LogInfo("Update check: couldn't reach GitHub (" + (e.InnerException?.Message ?? e.Message) + ")"); }
            });
        }

        private static Version Parse(string v)
        {
            var m = Regex.Match(v ?? "", @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
            if (!m.Success) return null;
            int P(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0;
            return new Version(P(1), P(2), P(3));
        }

        internal static bool IsNewer(string tag, string current)
        {
            var a = Parse(tag); var b = Parse(current);
            return a != null && b != null && a > b;
        }

        /// <summary>Called each frame by UiToWorld with the main menu canvas (null when it isn't showing).</summary>
        internal static void Tick(Canvas mainMenu)
        {
            try
            {
                if (_dismissed || _newer == null) return;
                bool showing = mainMenu != null && mainMenu.gameObject.activeInHierarchy;
                if (!showing)
                {
                    // Shown once per game start: gone after you leave the main menu.
                    if (_menuSeen) { _dismissed = true; if (_text != null) UnityEngine.Object.Destroy(_text.gameObject); _text = null; }
                    return;
                }
                _menuSeen = true;
                if (_text != null) return;
                var src = mainMenu.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                var go = new GameObject("Moon Sweepers VR update notice");
                go.transform.SetParent(mainMenu.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.anchoredPosition = new Vector2(0f, 20f);   // just above the top of the menu
                rt.sizeDelta = new Vector2(1600f, 140f);
                _text = go.AddComponent<TMPro.TextMeshProUGUI>();
                if (src != null && src.font != null) _text.font = src.font;
                _text.fontSize = 40f;
                _text.alignment = TMPro.TextAlignmentOptions.Center;
                _text.color = new Color(1f, 0.85f, 0.2f, 1f);
                _text.raycastTarget = false;
                _text.text = $"Moon Sweepers VR {_newer} is available (you have v{Plugin.Version})\n" +
                             $"<size=30>Download it from github.com/{Repo}/releases</size>";
            }
            catch (Exception e)
            {
                if (!_loggedErr) { _loggedErr = true; Plugin.L.LogWarning("Update notice: " + e.Message); }
                _dismissed = true;
            }
        }
    }
}
