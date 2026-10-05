using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace ModUpdater
{
    /// <summary>
    /// A small private mod manager. F7 opens a window listing installed mods, the versions on our private
    /// GitHub repo, and what versions the other players in the world have. Updates are pulled from GitHub
    /// into BepInEx\scripts and hot-reloaded through ScriptEngine.
    ///
    /// Split across files: Plugin.cs (setup/helpers), Plugin.Catalog.cs (what's installed / on GitHub / installing),
    /// Plugin.Peers.cs (sharing versions with other players), Plugin.UI.cs (the window).
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public partial class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.dhack.modupdater";
        public const string Name = "ModUpdater";
        public const string Version = "2.10.2";

        private const string ScriptEngineGuid = "com.bepis.bepinex.scriptengine";

        private ConfigEntry<string> _owner, _repo, _branch, _folder, _token, _extraFeeds;
        private ConfigEntry<KeyboardShortcut> _hotkey;
        private ConfigEntry<bool> _checkOnStart, _developerMode, _notifyOnJoin, _allowGitHubCli, _autoReload;
        private ConfigEntry<float> _uiScale;

        // A reload creates a brand-new copy of this mod in a fresh assembly, so statics are lost. AppDomain data
        // survives, which lets the new copy re-open the window and keep the last status message.
        private const string ReopenKey = "DHack.ModManager.Reopen";
        private const string StatusKey = "DHack.ModManager.Status";

        private string _scriptsDir, _pluginsDir;

        /// <summary>True while the manager window is open (used by the input patches).</summary>
        internal static bool WindowOpen;

        private void Awake()
        {
            Instance = this;
            _owner = Config.Bind("Repo", "Owner", "", "GitHub user or org that owns the repo.");
            _repo = Config.Bind("Repo", "Repo", "", "Repository name.");
            _branch = Config.Bind("Repo", "Branch", "main", "Branch to pull from.");
            _folder = Config.Bind("Repo", "Folder", "dist", "Folder in the repo that holds the built mod .dll files and manifest.json.");
            _mutedFeeds = Config.Bind("Repo", "MutedSources", "", "Community sources you chose to stop watching (managed in the Sources tab).");
            LoadCommunityCache();
            _extraFeeds = Config.Bind("Repo", "ExtraFeeds", "", "More places to get mods from, separated by semicolons. Each is owner/repo, owner/repo@branch or owner/repo@branch:folder (the folder holds manifest.json; default dist). You are trusting whoever runs them: their mods run inside your game. Easier to manage in the mod manager window.");
            _token = Config.Bind("Repo", "Token", "",
                "Only needed if the repo is private. A GitHub fine-grained token with READ-ONLY 'Contents' access to this one repo. Keep it private. " +
                "You can also paste it in the mod manager window (F7).");
            _allowGitHubCli = Config.Bind("Repo", "AllowGitHubCli", false,
                "OFF by default. If you turn this on and no Token is set, the manager runs `gh auth token` and uses your GitHub CLI login. " +
                "That login is your whole GitHub sign-in, with far more access than one read-only token, so only allow it if you're comfortable with that.");
            _hotkey = Config.Bind("General", "Hotkey", new KeyboardShortcut(KeyCode.F7), "Opens/closes the mod manager window.");
            _checkOnStart = Config.Bind("General", "CheckOnStart", false,
                "When the game starts, check GitHub and automatically install any updates.");
            _notifyOnJoin = Config.Bind("General", "NotifyOnJoin", true,
                "When you join a world, check GitHub once and say in chat if mod updates are waiting.");
            _uiScale = Config.Bind("General", "UiScale", 1f, "Size of the mod manager window (1 = normal, 1.25 = bigger).");
            _developerMode = Config.Bind("General", "DeveloperMode", false,
                "For the person who builds the mods: 'Update all' and auto-update never overwrite a build whose version is the same or newer than GitHub's.");
            _autoReload = Config.Bind("General", "AutoReloadRebuiltMods", true,
                "Developer Mode only: when you rebuild a mod, reload just that mod automatically (no F6, other mods untouched).");

            _scriptsDir = Path.Combine(Paths.BepInExRootPath, "scripts");
            _pluginsDir = Paths.PluginPath;
            Directory.CreateDirectory(_scriptsDir);

            _awakeFrame = Time.frameCount;
            string warmUp = ActiveToken; // if (and only if) the GitHub CLI was explicitly allowed, starts its lookup now so it's ready by F7
            _harmony = new Harmony(Guid);
            _harmony.PatchAll();
            ScanLocal();
            BuildRows();
            BaselineWatch(); // whatever is in scripts right now is what's loaded

            // Coming back from a reload (e.g. we just updated ourselves): re-open the window and keep the message.
            var domain = AppDomain.CurrentDomain;
            if (domain.GetData(ReopenKey) is bool reopen && reopen) WindowOpen = true;
            if (domain.GetData(StatusKey) is string lastStatus && lastStatus.Length > 0) _statusLine = lastStatus;
            domain.SetData(ReopenKey, false);
            domain.SetData(StatusKey, "");

            Logger.LogInfo($"{Name} {Version} ready. Press {_hotkey.Value} in-game to open the mod manager.");

            if (_checkOnStart.Value) StartCoroutine(RefreshRoutine(autoInstall: true));
        }

        private Harmony _harmony;
        private int _awakeFrame;
        private bool _firstCheckStarted, _joinCheckDone;

        // ScriptEngine destroys this copy when mods reload (including when we update ourselves).
        // Undo everything we hooked into the game so the fresh copy starts clean.
        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            _harmony?.UnpatchSelf();
            UnregisterRpc();

            AppDomain.CurrentDomain.SetData(ReopenKey, WindowOpen);
            AppDomain.CurrentDomain.SetData(StatusKey, _statusLine);
            WindowOpen = false;
            DestroyUi();
        }

        private void Update()
        {
            // Run whatever a button asked for (queued from OnGUI so the window layout never changes mid-draw).
            if (_deferred != null) { Action action = _deferred; _deferred = null; action(); }

            // (Not IsDown(): that ignores the key while another modifier, like the Shift you hold to run, is down.)
            if (_hotkey.Value.MainKey != KeyCode.None && Input.GetKeyDown(_hotkey.Value.MainKey) && _hotkey.Value.Modifiers.All(Input.GetKey))
                ToggleWindow();
            _needsSetup = !Configured || (_repoNeedsLogin && ActiveToken.Length == 0);
            WatchScripts();
            if (WindowOpen || (!_msLogged && Time.unscaledTime > 8f)) { RefreshModConfigs(); SaveSettingsSoon(); } // the Mod settings tab's lists, kept up to date outside drawing

            // Once, when we first get into a world: quietly check and tell the player if updates are waiting.
            // (Skipped if CheckOnStart is on, because that already installs updates and reports them.)
            if (!_joinCheckDone && Player.m_localPlayer != null && Configured && !_busy)
            {
                _joinCheckDone = true;
                if (_notifyOnJoin.Value && !_checkOnStart.Value) StartCoroutine(RefreshRoutine(autoInstall: false, notify: true));
            }

            // If the window was opened before we had credentials (just pasted a token, or the allowed CLI login is still
            // loading), do the first check as soon as they're there.
            if (WindowOpen && !_busy && !_firstCheckStarted && _lastRefresh == DateTime.MinValue && Configured)
            {
                _firstCheckStarted = true;
                StartCoroutine(RefreshRoutine(autoInstall: false));
            }
            UpdatePeers();
        }

        // ---- authentication ----------------------------------------------------------------------
        // Default: ONLY the read-only Token from the config is ever used.
        // The GitHub CLI login (far broader access) is used only if the user explicitly turned on AllowGitHubCli.

        private volatile string _ghToken;
        private bool _ghTried;
        /// <summary>Mods updated on disk that can't be hot-reloaded, so they only take effect after restarting the game (kept across hot reloads).</summary>
        private static HashSet<string> RestartPending
        {
            get
            {
                const string key = "DHack.ModUpdater.RestartPending";
                var set = AppDomain.CurrentDomain.GetData(key) as HashSet<string>;
                if (set == null) AppDomain.CurrentDomain.SetData(key, set = new HashSet<string>());
                return set;
            }
        }
        private bool _repoNeedsLogin;   // GitHub said it can't see the repo without a login (private repo)
        private string _loginFeed = "";
        private bool _needsSetup;   // cached once per frame so the window layout can't change mid-draw

        /// <summary>The token to use: the config's Token if set; otherwise the GitHub CLI's, but only with explicit permission.</summary>
        private string ActiveToken
        {
            get
            {
                if (!string.IsNullOrEmpty(_token.Value)) return _token.Value;

                if (!_allowGitHubCli.Value)
                {
                    // Permission not given (or taken back): never run gh, and forget anything we fetched earlier.
                    _ghToken = null;
                    _ghTried = false;
                    return "";
                }
                if (!_ghTried) { _ghTried = true; AskGitHubCli(); }
                return _ghToken ?? "";
            }
        }

        private string AuthSource =>
            !string.IsNullOrEmpty(_token.Value) ? "access token"
            : _allowGitHubCli.Value && !string.IsNullOrEmpty(_ghToken) ? "GitHub CLI login"
            : "no login (public repo)";

        /// <summary>
        /// Run `gh auth token` on a background thread (so the game never stalls). Only called with explicit permission.
        /// The token is held in memory only: never written to disk, only sent to api.github.com.
        /// </summary>
        private void AskGitHubCli()
        {
            new System.Threading.Thread(() =>
            {
                foreach (string exe in new[] { "gh", @"C:\Program Files\GitHub CLI\gh.exe" })
                {
                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(exe, "auth token")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        };
                        using (var p = System.Diagnostics.Process.Start(psi))
                        {
                            string output = p.StandardOutput.ReadToEnd().Trim();
                            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } continue; }
                            if (p.ExitCode == 0 && output.Length > 0) { _ghToken = output; return; }
                        }
                    }
                    catch { /* gh not installed at this path: try the next */ }
                }
            }) { IsBackground = true }.Start();
        }

        private bool Configured => true; // the main sources are built in


        /// <summary>One authenticated GET against the GitHub API.</summary>
        private IEnumerator Get(string url, string accept, Action<string, byte[], string> done, bool auth = true)
        {
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                string token = auth ? ActiveToken : "";
                if (token.Length > 0) req.SetRequestHeader("Authorization", "Bearer " + token); // public repos need no sign-in
                req.SetRequestHeader("Accept", accept);
                req.SetRequestHeader("User-Agent", Name);
                req.SetRequestHeader("X-GitHub-Api-Version", "2022-11-28");
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                    done(null, null, $"{req.responseCode} {req.error}".Trim());
                else
                    done(req.downloadHandler.text, req.downloadHandler.data, null);
            }
        }

        /// <summary>Ask ScriptEngine to reload scripts now (the same thing F6 does).</summary>
        private bool ReloadScripts()
        {
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue(ScriptEngineGuid, out var info) || info.Instance == null) return false;
                var reload = AccessTools.Method(info.Instance.GetType(), "ReloadPlugins");
                if (reload == null) return false;
                reload.Invoke(info.Instance, null);
                return true;
            }
            catch (Exception e)
            {
                Logger.LogWarning("Could not trigger ScriptEngine reload: " + e.Message);
                return false;
            }
        }

        /// <summary>Log it, and show it as a local chat line when in a world.</summary>
        private void Say(string message)
        {
            Logger.LogInfo(message);
            if (Chat.instance != null)
            {
                Chat.instance.AddString("[Updater]", message, Talker.Type.Normal);
                AccessTools.Field(typeof(Chat), "m_hideTimer").SetValue(Chat.instance, 0f);
                Chat.instance.m_chatWindow.gameObject.SetActive(true);
            }
        }
    }

    // While the window is open: don't let clicks/keys reach the game, and free the mouse cursor.
    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    internal static class PlayerController_TakeInput
    {
        private static void Postfix(ref bool __result) { if (Plugin.WindowOpen) __result = false; }
    }

    [HarmonyPatch(typeof(Player), "TakeInput")]
    internal static class Player_TakeInput
    {
        private static void Postfix(ref bool __result) { if (Plugin.WindowOpen) __result = false; }
    }

    [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture))]
    internal static class GameCamera_UpdateMouseCapture
    {
        // While our window is open, skip the game's own cursor handling entirely. If we let it run and then undo it, the game locks the
        // cursor every frame, and on Linux locking physically snaps the pointer to the middle of the screen, so it looks stuck there.
        private static bool Prefix()
        {
            if (!Plugin.WindowOpen) return true;
            ZCursor.LockState = CursorLockMode.None;
            ZCursor.Show();
            return false;
        }
    }
}

namespace ModUpdater
{
    // While you are typing in the manager's search box, tell the game (and the other mods that ask) that a text box has focus,
    // so letters like Q or B don't trigger their shortcuts.
    [HarmonyLib.HarmonyPatch(typeof(Minimap), nameof(Minimap.InTextInput))]
    internal static class Minimap_InTextInput
    {
        private static void Postfix(ref bool __result)
        {
            if (Plugin.WindowOpen && (UnityEngine.GUI.GetNameOfFocusedControl() == "modsearch" || Plugin.TypingInSettings)) __result = true;
        }
    }
}

namespace ModUpdater
{
    // While one of our windows is open, the mouse wheel should scroll the window, not zoom the camera (or cycle the hotbar).
    [HarmonyLib.HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel))]
    internal static class ZInput_GetMouseScrollWheel
    {
        private static void Postfix(ref float __result) { if (Plugin.WindowOpen) __result = 0f; }
    }
}

namespace ModUpdater
{
    // Escape closes our window, and only that: the game's own menu does not open (so the game is not paused by it).
    [HarmonyLib.HarmonyPatch(typeof(Menu), "Update")]
    internal static class Menu_Update_EscapeCloses
    {
        private static bool Prefix()
        {
            if (!(Plugin.WindowOpen)) return true;
            if (!(ZInput.GetKeyDown(UnityEngine.KeyCode.Escape) || ZInput.GetButtonDown("JoyMenu"))) return true;
            if (Plugin.CancelCapture()) return false; // Escape while choosing a key just cancels that
            Plugin.WindowOpen = false;
            return false; // skip the game's menu handling for this frame
        }
    }
}
