using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ModUpdater
{
    /// <summary>
    /// Where mods come from. Three kinds of source, all the same format (a GitHub folder with manifest.json + DLLs):
    ///   main      built in: the mod manager's own repo (so it can update itself) and the main mods repo. Auto-updated if you ask.
    ///   community suggested by anyone through a pull request to the mod manager's sources.json. Watched by default (browse only;
    ///             nothing is installed until you click Install), and you can stop watching any of them.
    ///   yours     added by you in the Sources tab (or ExtraFeeds in the config).
    /// </summary>
    public partial class Plugin
    {
        internal const string ManagerRepo = "HardHeadHackerHead/valheim-mod-manager";
        internal const string MainModsRepo = "HardHeadHackerHead/valheim-mods";

        private ConfigEntry<string> _mutedFeeds;
        private List<Feed> _community = new List<Feed>();

        private string CommunityCache => Path.Combine(Paths.ConfigPath, Guid + ".sources.json");

        private static Feed Core(string spec)
        {
            Feed f = ParseFeed(spec);
            f.Primary = true;
            f.Kind = "main";
            return f;
        }

        /// <summary>Every feed to read, in priority order: main, anything from the old [Repo] settings, community, then yours.</summary>
        private List<Feed> Feeds()
        {
            var list = new List<Feed>();
            void Add(Feed f) { if (f != null && !list.Any(x => x.Spec == f.Spec)) list.Add(f); }

            Add(Core(ManagerRepo));
            Add(Core(MainModsRepo));
            if (!string.IsNullOrEmpty(_owner.Value) && !string.IsNullOrEmpty(_repo.Value)) // an older config that points somewhere else
                Add(new Feed { Owner = _owner.Value, Repo = _repo.Value, Branch = _branch.Value, Folder = _folder.Value, Primary = true, Kind = "main" });
            foreach (Feed f in _community) if (!IsMuted(f)) Add(f);
            foreach (string spec in SplitSpecs(_extraFeeds.Value))
            {
                Feed f = ParseFeed(spec);
                if (f != null) { f.Kind = "yours"; Add(f); }
            }
            return list;
        }

        private static IEnumerable<string> SplitSpecs(string text) =>
            (text ?? "").Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0);

        private bool IsMuted(Feed f) => SplitSpecs(_mutedFeeds.Value).Contains(f.Spec);

        private void SetMuted(Feed f, bool muted)
        {
            List<string> now = SplitSpecs(_mutedFeeds.Value).Where(s => s != f.Spec).ToList();
            if (muted) now.Add(f.Spec);
            _mutedFeeds.Value = string.Join(";", now.ToArray());
        }

        /// <summary>Your own sources (the ones from ExtraFeeds), without the built-in and community ones.</summary>
        private List<Feed> YourFeeds() => Feeds().Where(f => f.Kind == "yours").ToList();

        private void SetYourFeeds(IEnumerable<string> specs) => _extraFeeds.Value = string.Join(";", specs.ToArray());

        // ---- the community list (sources.json in the mod manager's repo) -----------------------

        private void LoadCommunityCache()
        {
            try { if (File.Exists(CommunityCache)) ParseCommunity(File.ReadAllText(CommunityCache)); }
            catch (Exception e) { Logger.LogWarning("Ignoring the saved community source list: " + e.Message); }
        }

        /// <summary>{ "sources": [ { "repo": "owner/repo", "branch": "main", "folder": "dist", "name": "...", "about": "..." } ] }</summary>
        private void ParseCommunity(string json)
        {
            var list = new List<Feed>();
            JToken sources = JObject.Parse(json)["sources"];
            if (sources != null)
                foreach (JToken s in sources)
                {
                    string spec = (string)s["repo"] ?? "";
                    string branch = (string)s["branch"], folder = (string)s["folder"];
                    if (!string.IsNullOrEmpty(branch) || !string.IsNullOrEmpty(folder))
                        spec += "@" + (string.IsNullOrEmpty(branch) ? "main" : branch) + ":" + (string.IsNullOrEmpty(folder) ? "dist" : folder);
                    Feed f = ParseFeed(spec);
                    if (f == null) continue;
                    f.Kind = "community";
                    f.Name = (string)s["name"];
                    f.About = (string)s["about"];
                    list.Add(f);
                }
            _community = list;
        }

        private IEnumerator FetchCommunity()
        {
            string json = null, error = null;
            yield return Get($"https://api.github.com/repos/{ManagerRepo}/contents/sources.json?ref=main", "application/vnd.github.raw+json",
                             (t, b, e) => { json = t; error = e; }, true);
            if (error != null || json == null) yield break; // offline or not published yet: keep the saved list
            try { ParseCommunity(json); File.WriteAllText(CommunityCache, json); }
            catch (Exception e) { Logger.LogWarning("Could not read the community source list: " + e.Message); }
        }

        // ---- Sources tab -----------------------------------------------------------------------

        private string _feedInput = "";
        private string _feedConfirm;       // the source waiting for a second click
        private float _feedConfirmAt;
        private string _feedProblem = "";

        private void DrawFeeds()
        {
            List<Feed> active = Feeds();

            GUILayout.Label("Main sources", _sH2);
            GUILayout.Space(2);
            GUILayout.BeginVertical(_sCard);
            foreach (Feed feed in active.Where(f => f.Primary))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(feed.Label + (feed.Label == ManagerRepo ? "   (this mod manager)" : feed.Label == MainModsRepo ? "   (the main mods)" : ""), _sBody, GUILayout.ExpandWidth(false));
                if (feed.Error != null) GUILayout.Label("  " + feed.Error, _sDim);
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("Built in. Updates from these can install automatically (Settings).", _sDim);
            GUILayout.EndVertical();

            GUILayout.Space(12);
            GUILayout.Label($"Community sources  ({_community.Count})", _sH2);
            GUILayout.Space(2);
            GUILayout.BeginVertical(_sCard);
            if (_community.Count == 0)
                GUILayout.Label("None yet. Anyone can suggest a mod repo for this list with a pull request to " + ManagerRepo + " (edit sources.json).", _sDim);
            foreach (Feed feed in _community)
            {
                bool muted = IsMuted(feed);
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
                GUILayout.Label((string.IsNullOrEmpty(feed.Name) ? feed.Label : feed.Name) + "   " + feed.Label, muted ? _sDim : _sBody);
                if (!string.IsNullOrEmpty(feed.About)) GUILayout.Label(feed.About, _sDim);
                Feed shown = active.FirstOrDefault(f => f.Spec == feed.Spec);
                if (shown != null && shown.Error != null) GUILayout.Label(shown.Error, TextStyle(12, Warn, FontStyle.Normal, true));
                GUILayout.EndVertical();
                Feed target = feed;
                if (Button(muted ? "Watch" : "Stop watching", 120, muted, !_busy))
                    Defer(() => { SetMuted(target, !muted); StartCoroutine(RefreshRoutine(autoInstall: false)); });
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("Suggested by other players. You only see their mods in Browse; nothing is installed until you click Install.", _sDim);
            GUILayout.EndVertical();

            GUILayout.Space(12);
            GUILayout.Label("Your sources", _sH2);
            GUILayout.Space(2);
            GUILayout.BeginVertical(_sCard);
            foreach (Feed feed in YourFeeds())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(feed.Spec, _sBody, GUILayout.ExpandWidth(false));
                if (feed.Error != null) GUILayout.Label("  " + feed.Error, _sDim);
                GUILayout.FlexibleSpace();
                if (Button("Remove", 80, false, !_busy, true))
                {
                    Feed gone = feed;
                    Defer(() =>
                    {
                        SetYourFeeds(YourFeeds().Where(f => f.Spec != gone.Spec).Select(f => f.Spec));
                        StartCoroutine(RefreshRoutine(autoInstall: false));
                    });
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.Label("Add another place to get mods from, for example a friend's repo. Format: owner/repo (or owner/repo@branch:folder). " +
                            "Mods from other sources run inside your game with full access to your PC, so only add people you trust. " +
                            "They are never installed automatically; you click Install yourself.", _sDim);
            GUILayout.BeginHorizontal();
            _feedInput = GUILayout.TextField(_feedInput, 120, GUILayout.ExpandWidth(true), GUILayout.Height(28));
            bool sure = _feedConfirm != null && _feedConfirm == _feedInput.Trim() && Time.realtimeSinceStartup - _feedConfirmAt < 8f;
            if (Button(sure ? "Yes, I trust them" : "Add source", 150, true, _feedInput.Trim().Length > 0 && !_busy))
            {
                string spec = _feedInput.Trim();
                Defer(() =>
                {
                    Feed f = ParseFeed(spec);
                    if (f == null) { _feedProblem = "That does not look right. Use owner/repo, or owner/repo@branch:folder."; _feedConfirm = null; return; }
                    if (Feeds().Any(x => x.Spec == f.Spec)) { _feedProblem = "That source is already in the list."; _feedConfirm = null; return; }
                    if (_feedConfirm != spec || Time.realtimeSinceStartup - _feedConfirmAt >= 8f) { _feedConfirm = spec; _feedConfirmAt = Time.realtimeSinceStartup; _feedProblem = ""; return; }
                    SetYourFeeds(YourFeeds().Select(x => x.Spec).Concat(new[] { f.Spec }));
                    _feedInput = ""; _feedConfirm = null; _feedProblem = "";
                    StartCoroutine(RefreshRoutine(autoInstall: false));
                });
            }
            GUILayout.EndHorizontal();
            if (_feedProblem.Length > 0) GUILayout.Label(_feedProblem, _sDim);
            GUILayout.EndVertical();
        }
    }
}
