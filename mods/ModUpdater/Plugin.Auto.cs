using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace ModUpdater
{
    /// <summary>
    /// Automatic updating, for the mods the player switched it on for (off for every mod until then, and off as a whole until the switch in the
    /// Manager tab is on). Polite to GitHub: GitHub lets a game without a login make 60 API requests an hour, and a full check is about seven
    /// of them (the sources list, then a folder listing and the manifest per source), so a check every few minutes would soon be refused (the
    /// 403 players have seen). So the timed check reads only each source's manifest.json from raw.githubusercontent.com, which is not counted
    /// against that limit, and sends the ETag of the last answer so an unchanged manifest costs almost nothing. Only when a version actually
    /// went up does it do one full check, then updates the mods one at a time, a gap apart (AutoPlan), the manager itself last. The files come
    /// from raw.githubusercontent.com too and are checked against GitHub's listing before they are used.
    ///
    /// A mod that can't be reloaded while the game runs (it asks for a restart, or it lives in BepInEx\plugins) is downloaded and left waiting
    /// for a restart, as a manual update does, and the player is told once. Nothing runs on a loading screen, while dead, teleporting or in a
    /// cutscene, nothing that isn't installed is installed, nothing is downgraded, and switched-off (.disabled) mods are left alone.
    /// </summary>
    public partial class Plugin
    {
        private ConfigEntry<bool> _autoUpdate;
        private ConfigEntry<string> _autoUpdateMods;
        private ConfigEntry<int> _autoMinutes, _autoGapSeconds;

        private readonly AutoPlan _auto = new AutoPlan();
        private readonly Dictionary<string, string> _manifestEtag = new Dictionary<string, string>();   // raw manifest URL -> its last ETag
        private readonly Dictionary<string, string> _manifestSeen = new Dictionary<string, string>();   // raw manifest URL -> its last body
        private bool _autoRunning, _limitSaid;
        private bool _rateLimited;              // set by Describe when GitHub refused a request for its limit (read by the automatic check)
        private string _autoNote = "";          // shown under the switch: when the next check is, or why it is paused
        // mod@version already tried, and when: a version that failed (or is still in GitHub's file cache) is not tried again for half an hour, so
        // it can't cost a full check (about seven API requests) every few minutes
        private readonly Dictionary<string, double> _autoTried = new Dictionary<string, double>();
        private const double RetryAfterSeconds = 1800.0;

        private bool TriedLately(string guid, string version) =>
            _autoTried.TryGetValue(guid + "@" + version, out double at) && Time.unscaledTime - at < RetryAfterSeconds;

        private void BindAuto()
        {
            _autoUpdate = Config.Bind("AutoUpdate", "Enabled", false,
                "Check for updates by itself and update the mods you switched 'Auto update' on for, one at a time. Off by default.");
            _autoUpdateMods = Config.Bind("AutoUpdate", "Mods", "",
                "The mods (by id) that update by themselves. Easier to change with the 'Auto update' switch on each mod in the mod manager (F7).");
            _autoMinutes = Config.Bind("AutoUpdate", "CheckEveryMinutes", 5,
                new ConfigDescription("How often to look for new versions. Each look only reads each source's manifest.json, which GitHub does not count against its hourly limit.", new AcceptableValueRange<int>(2, 120)));
            _autoGapSeconds = Config.Bind("AutoUpdate", "SecondsBetweenMods", 20,
                new ConfigDescription("When several mods have updates, they are updated one at a time, this many seconds apart.", new AcceptableValueRange<int>(5, 300)));
        }

        private HashSet<string> AutoMods => new HashSet<string>(SplitSpecs(_autoUpdateMods.Value));

        private bool AutoOn(string guid) => !string.IsNullOrEmpty(guid) && AutoMods.Contains(guid);

        private void SetAuto(string guid, bool on)
        {
            HashSet<string> mods = AutoMods;
            if (on) mods.Add(guid); else mods.Remove(guid);
            _autoUpdateMods.Value = string.Join(";", mods.OrderBy(g => g).ToArray());
        }

        /// <summary>Every frame (from Update): the timer, then at most one step of the queue.</summary>
        private void TickAuto()
        {
            if (!_autoUpdate.Value) { _auto.Clear(); _autoNote = ""; return; }
            double now = Time.unscaledTime;
            _auto.Interval = _autoMinutes.Value * 60.0;
            _auto.Gap = _autoGapSeconds.Value;
            _auto.Start(now);

            if (_auto.Queue.Count > 0) { StepAuto(now); return; }
            if (_autoRunning || _busy || !_auto.CheckDue(now)) { NoteAuto(now); return; }
            if (!SafeForAuto()) { _auto.Skipped(now - _auto.Interval + 15.0); return; }   // (try again in 15 s)
            if (AutoMods.Count == 0) { _auto.Checked(now); NoteAuto(now); return; }
            StartCoroutine(AutoCheck());
        }

        /// <summary>Not on a loading screen, not dead, not teleporting, not in a cutscene. At the main menu it is fine.</summary>
        private static bool SafeForAuto()
        {
            if (ZNet.instance == null) return true;
            Player p = Player.m_localPlayer;
            return p != null && !p.IsDead() && !p.IsTeleporting() && !p.InCutscene();
        }

        private void NoteAuto(double now)
        {
            if (_auto.Backoff > 0) _autoNote = $"Paused: GitHub's limit for requests without a login was reached. Next try in {Mathf.CeilToInt((float)((_auto.NextCheck - now) / 60.0))} min.";
            else if (_auto.NextCheck > now) _autoNote = $"Next check in {Mathf.CeilToInt((float)((_auto.NextCheck - now) / 60.0))} min.";
        }

        /// <summary>One timed check: the manifests only; a full check and the queue only when a version went up.</summary>
        private IEnumerator AutoCheck()
        {
            _autoRunning = true;
            double started = Time.unscaledTime;
            try
            {
                HashSet<string> mine = AutoMods;
                if (_remote.Length == 0)
                {
                    // nothing known about GitHub yet this session: one full check first (it also fills in where each mod comes from)
                    _rateLimited = false;
                    yield return RefreshRoutine(autoInstall: false);
                    if (_rateLimited) { LimitHit(started, 0); yield break; }
                    QueueFromRows(mine, Time.unscaledTime);
                    _auto.Checked(Time.unscaledTime);
                    yield break;
                }

                // the sources the switched-on mods come from: one small request each, to raw.githubusercontent.com
                var feeds = _rows.Where(r => r.Remote != null && r.Local != null && (mine.Contains(r.Remote.guid) || mine.Contains(r.Local.Guid)))
                                 .Select(r => r.Feed).Where(f => f != null).GroupBy(f => f.Spec).Select(g => g.First()).ToList();
                var newer = new List<string>();
                foreach (Feed feed in feeds)
                {
                    string url = $"https://raw.githubusercontent.com/{feed.Owner}/{feed.Repo}/{feed.Branch}/{feed.Folder}/manifest.json";
                    string body = null; long code = 0; string retry = null, reset = null;
                    yield return GetRaw(url, (b, c, ra, rs) => { body = b; code = c; retry = ra; reset = rs; });
                    if (code == 403 || code == 429) { LimitHit(started, AutoPlan.WaitAsked(retry, reset, DateTime.UtcNow)); yield break; }
                    if (code == 304) body = _manifestSeen.TryGetValue(url, out string seen) ? seen : null;
                    if (body == null) continue;   // offline, or this source has nothing yet
                    _manifestSeen[url] = body;
                    try { newer.AddRange(NewerIn(body, mine)); }
                    catch (Exception e) { Logger.LogWarning($"Auto update: couldn't read {feed.Label}'s manifest.json: {e.Message}"); }
                }

                if (newer.Count > 0)
                {
                    Logger.LogInfo($"Auto update: new versions on GitHub for {string.Join(", ", newer.ToArray())}: checking in full");
                    _rateLimited = false;
                    yield return RefreshRoutine(autoInstall: false);
                    if (_rateLimited) { LimitHit(started, 0); yield break; }
                    QueueFromRows(mine, Time.unscaledTime);
                }
                _limitSaid = false;
                _auto.Checked(Time.unscaledTime);
            }
            finally { _autoRunning = false; NoteAuto(Time.unscaledTime); }
        }

        /// <summary>The switched-on, installed mods whose version in this manifest is newer than the copy installed.</summary>
        private IEnumerable<string> NewerIn(string manifestJson, HashSet<string> mine)
        {
            var candidates = new List<AutoPlan.Candidate>();
            foreach (JToken m in JObject.Parse(manifestJson)["mods"])
            {
                string guid = (string)m["guid"], version = (string)m["version"];
                string[] was = m["was"] is JArray w ? w.Select(g => (string)g).ToArray() : new string[0];
                LocalMod local = LiveCopy(guid) ?? was.Select(LiveCopy).FirstOrDefault(l => l != null);
                candidates.Add(new AutoPlan.Candidate
                {
                    Guid = guid, Remote = version, Local = local?.Version,
                    OptedIn = mine.Contains(guid) || (local != null && mine.Contains(local.Guid)),
                    Installed = local != null, Disabled = local != null && local.Disabled,
                });
            }
            return AutoPlan.Pick(candidates, CompareVersions).Where(g => !TriedLately(g, candidates.First(c => c.Guid == g).Remote));
        }

        /// <summary>After a full check: queue the switched-on mods that really have an update (installed, enabled, older).</summary>
        private void QueueFromRows(HashSet<string> mine, double now)
        {
            var picks = AutoPlan.Pick(_rows.Where(r => r.Remote != null).Select(r => new AutoPlan.Candidate
            {
                Guid = r.Remote.guid, Remote = r.RemoteVersion, Local = r.LocalVersion,
                OptedIn = mine.Contains(r.Remote.guid) || (r.Local != null && mine.Contains(r.Local.Guid)),
                Installed = r.Local != null, Disabled = r.Status == Status.Disabled,
            }), CompareVersions).Where(g => !TriedLately(g, _rows.First(r => r.Remote != null && r.Remote.guid == g).RemoteVersion)).ToList();
            if (picks.Count == 0) return;
            Logger.LogInfo($"Auto update: {picks.Count} to update, one every {_auto.Gap:0} s: {string.Join(", ", picks.ToArray())}");
            _auto.Enqueue(picks, Guid, now);
        }

        /// <summary>One mod from the queue, if its turn has come and the game is in a state to do it.</summary>
        private void StepAuto(double now)
        {
            if (_busy || _autoRunning) return;
            string guid = _auto.NextStep(now);
            if (guid == null) return;
            if (!SafeForAuto()) { _auto.Postpone(guid, now); return; }
            Row row = _rows.FirstOrDefault(r => r.Remote != null && r.Remote.guid == guid);
            if (row == null || row.Local == null || row.Status != Status.UpdateAvailable || row.Status == Status.Disabled) return;   // changed meanwhile
            Logger.LogInfo($"Auto update: {row.Name} {row.LocalVersion} -> {row.RemoteVersion}");
            _autoTried[guid + "@" + row.RemoteVersion] = now;
            StartCoroutine(InstallRoutine(new List<RemoteMod> { row.Remote }, viaRaw: true));
        }

        private void LimitHit(double started, double asked)
        {
            double wait = _auto.RateLimited(Time.unscaledTime, asked);
            _auto.Clear();
            Logger.LogWarning($"Auto update: GitHub's request limit was reached; checks pause for {wait / 60.0:0} min");
            if (!_limitSaid) { _limitSaid = true; Say($"Automatic updates are paused for {Mathf.CeilToInt((float)(wait / 60.0))} min: GitHub's limit for requests without a login was reached."); }
        }

        /// <summary>A GET on raw.githubusercontent.com with the last ETag. done(body or null, HTTP code, Retry-After, X-RateLimit-Reset).</summary>
        private IEnumerator GetRaw(string url, Action<string, long, string, string> done)
        {
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("User-Agent", Name);
                if (_manifestEtag.TryGetValue(url, out string etag)) req.SetRequestHeader("If-None-Match", etag);
                req.timeout = 20;
                yield return req.SendWebRequest();
                long code = req.responseCode;
                string newTag = req.GetResponseHeader("ETag");
                if (code == 200 && !string.IsNullOrEmpty(newTag)) _manifestEtag[url] = newTag;
                done(code == 200 ? req.downloadHandler.text : null, code, req.GetResponseHeader("Retry-After"), req.GetResponseHeader("X-RateLimit-Reset"));
            }
        }

        /// <summary>Where a feed's file is on raw.githubusercontent.com (public feeds; a feed that needs the token keeps using the API).</summary>
        private string RawUrl(Feed feed, string file) => $"https://raw.githubusercontent.com/{feed.Owner}/{feed.Repo}/{feed.Branch}/{feed.Folder}/{file}";

        private bool UsesToken(Feed feed) => feed.Primary && ActiveToken.Length > 0;
    }
}
