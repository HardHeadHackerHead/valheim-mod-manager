using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ModUpdater
{
    /// <summary>The F7 window. Unity's immediate-mode GUI (OnGUI), with our own dark, opaque styling.</summary>
    public partial class Plugin
    {
        private const int WindowId = 7731;

        // ---- palette ---------------------------------------------------------------------------
        private static readonly Color Gold = new Color(0.95f, 0.78f, 0.35f);
        private static readonly Color TextMain = new Color(0.92f, 0.90f, 0.86f);
        private static readonly Color TextDim = new Color(0.62f, 0.60f, 0.56f);
        private static readonly Color Good = new Color(0.50f, 0.95f, 0.58f);
        private static readonly Color Warn = new Color(1f, 0.78f, 0.30f);
        private static readonly Color Bad = new Color(1f, 0.50f, 0.50f);

        private static readonly Color PillGreen = new Color(0.17f, 0.42f, 0.24f);
        private static readonly Color PillAmber = new Color(0.58f, 0.40f, 0.08f);
        private static readonly Color PillRed = new Color(0.52f, 0.18f, 0.18f);
        private static readonly Color PillBlue = new Color(0.17f, 0.33f, 0.58f);
        private static readonly Color PillGrey = new Color(0.27f, 0.27f, 0.27f);

        // ---- state -----------------------------------------------------------------------------
        private Rect _window;
        private bool _windowPlaced;
        private Vector2 _scroll;
        private string _confirmRevert;      // which mod's "Use GitHub copy" is waiting for a second click
        private float _confirmRevertAt;
        private Action _deferred;   // clicks are run from Update, not mid-draw, so the layout never changes under IMGUI

        private readonly List<Texture2D> _textures = new List<Texture2D>();
        private bool _stylesReady;
        private GUIStyle _sWindow, _sCard, _sTitle, _sH2, _sName, _sBody, _sDim, _sVer, _sPill, _sBtn, _sBtnSmall, _sBtnPrimary, _sToggle, _sRule, _sTab, _sTabOn, _sBanner, _sTile;
        private Texture2D _texWhite;

        private void ToggleWindow()
        {
            WindowOpen = !WindowOpen;
            if (!WindowOpen) return;

            ScanLocal();
            BuildRows();
            // Refresh when opened if we've never checked, or it's been a while.
            if (Configured && (DateTime.Now - _lastRefresh).TotalSeconds > 60)
                StartCoroutine(RefreshRoutine(autoInstall: false));
        }

        // ---- styles ----------------------------------------------------------------------------

        private Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            _textures.Add(t);
            return t;
        }

        /// <summary>A tiny texture with a 2px border, used 9-sliced so any size gets a crisp outline.</summary>
        private Texture2D Boxed(Color fill, Color border)
        {
            const int size = 6;
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
            };
            for (int x = 0; x < size; x++)
                for (int y = 0; y < size; y++)
                    t.SetPixel(x, y, (x < 2 || y < 2 || x >= size - 2 || y >= size - 2) ? border : fill);
            t.Apply();
            _textures.Add(t);
            return t;
        }

        private static Font FindGameFont()
        {
            try
            {
                return Resources.FindObjectsOfTypeAll<Font>().FirstOrDefault(f =>
                    f.name.IndexOf("Averia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    f.name.IndexOf("Norse", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return null; }
        }

        private static GUIStyle TextStyle(int size, Color color, FontStyle fontStyle = FontStyle.Normal, bool wrap = false, Font font = null)
        {
            var s = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = fontStyle, wordWrap = wrap, richText = false };
            if (font != null) s.font = font;
            s.normal.textColor = color;
            return s;
        }

        private GUIStyle ButtonStyle(Color fill, Color hover, Color pressed, Color border, Color text)
        {
            var s = new GUIStyle(GUI.skin.button)
            {
                fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(2, 2, 2, 2), padding = new RectOffset(10, 10, 4, 4), margin = new RectOffset(3, 3, 3, 3),
            };
            s.normal.background = Boxed(fill, border);
            s.hover.background = Boxed(hover, border);
            s.active.background = Boxed(pressed, border);
            s.focused.background = s.normal.background;
            s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = text;
            return s;
        }

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;
            Font font = FindGameFont();

            _sWindow = new GUIStyle(GUI.skin.window)
            {
                border = new RectOffset(2, 2, 2, 2), padding = new RectOffset(18, 18, 14, 12),
            };
            Texture2D win = Boxed(new Color(0.075f, 0.065f, 0.055f, 1f), new Color(0.62f, 0.47f, 0.22f, 1f));
            _sWindow.normal.background = _sWindow.onNormal.background = win;
            _sWindow.normal.textColor = _sWindow.onNormal.textColor = TextMain;

            _sCard = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(2, 2, 2, 2), padding = new RectOffset(12, 12, 9, 9), margin = new RectOffset(0, 0, 0, 0),
            };
            _sCard.normal.background = Boxed(new Color(0.13f, 0.115f, 0.10f, 1f), new Color(0.26f, 0.22f, 0.17f, 1f));

            _sTitle = TextStyle(22, Gold, FontStyle.Bold, false, font);
            _sH2 = TextStyle(15, Gold, FontStyle.Bold, false, font);
            _sName = TextStyle(15, TextMain, FontStyle.Bold, false, font);
            _sBody = TextStyle(12, TextMain, FontStyle.Normal, true);
            _sDim = TextStyle(12, TextDim, FontStyle.Normal, true);
            _sVer = TextStyle(13, TextDim);

            _sPill = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter, fontSize = 12, fontStyle = FontStyle.Bold,
                padding = new RectOffset(6, 6, 2, 2), margin = new RectOffset(2, 2, 2, 2),
            };
            _sPill.normal.background = Solid(Color.white); // tinted per use through GUI.backgroundColor
            _sPill.normal.textColor = Color.white;

            _sBtn = ButtonStyle(new Color(0.22f, 0.19f, 0.15f), new Color(0.33f, 0.27f, 0.18f), new Color(0.42f, 0.33f, 0.16f),
                                new Color(0.45f, 0.36f, 0.2f), TextMain);
            _sBtnSmall = new GUIStyle(_sBtn) { fontSize = 12, padding = new RectOffset(4, 4, 2, 2) };
            _sBtnPrimary = ButtonStyle(new Color(0.18f, 0.40f, 0.24f), new Color(0.24f, 0.52f, 0.31f), new Color(0.15f, 0.33f, 0.2f),
                                       new Color(0.4f, 0.8f, 0.5f), Color.white);

            _sToggle = new GUIStyle(GUI.skin.toggle) { fontSize = 12, alignment = TextAnchor.MiddleLeft };
            _sToggle.normal.textColor = _sToggle.onNormal.textColor = _sToggle.hover.textColor = _sToggle.onHover.textColor =
                _sToggle.active.textColor = _sToggle.onActive.textColor = TextMain;

            _sTab = ButtonStyle(new Color(0.15f, 0.13f, 0.11f), new Color(0.24f, 0.20f, 0.14f), new Color(0.30f, 0.24f, 0.14f), new Color(0.30f, 0.25f, 0.18f), TextDim);
            _sTabOn = ButtonStyle(new Color(0.28f, 0.22f, 0.12f), new Color(0.33f, 0.26f, 0.14f), new Color(0.33f, 0.26f, 0.14f), Gold, Gold);
            _sBanner = new GUIStyle(GUI.skin.box) { border = new RectOffset(2, 2, 2, 2), padding = new RectOffset(12, 12, 8, 8) };
            _sBanner.normal.background = Boxed(new Color(0.22f, 0.16f, 0.06f, 1f), new Color(0.75f, 0.55f, 0.15f, 1f));
            _texWhite = Solid(Color.white);
            _sTile = TextStyle(32, new Color(1f, 1f, 1f, 0.85f), FontStyle.Bold);
            _sTile.alignment = TextAnchor.MiddleCenter;
            _sRule = new GUIStyle { margin = new RectOffset(0, 0, 8, 8), fixedHeight = 1 };
            _sRule.normal.background = Solid(new Color(0.35f, 0.29f, 0.2f, 1f));
        }

        private void DestroyUi()
        {
            foreach (Texture2D t in _textures) if (t != null) Destroy(t);
            _textures.Clear();
            _stylesReady = false;
            DestroyCovers();
        }

        // ---- window ----------------------------------------------------------------------------

        private void OnGUI()
        {
            if (!WindowOpen) return;

            // Make sure the mouse can move. The game only frees the cursor when it thinks the mouse is the active input device, which on
            // some setups (Linux, Steam Deck/Steam Input, a controller plugged in) it doesn't, leaving it stuck in the middle of the screen.
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;

            EnsureStyles();

            // Scale with the screen so text stays readable at 1440p/4K (UiScale in the config adjusts it further).
            float s = Mathf.Max(0.75f, Screen.height / 1080f) * Mathf.Clamp(_uiScale.Value, 0.5f, 2f);
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            float sw = Screen.width / s, sh = Screen.height / s;
            float w = Mathf.Min(960f, sw - 30f), h = Mathf.Min(700f, sh - 30f);

            if (!_windowPlaced)
            {
                _window = new Rect((sw - w) / 2f, (sh - h) / 2f, w, h);
                _windowPlaced = true;
            }
            _window.width = w; // size follows the screen; the position is draggable
            _window.height = h;

            _window = GUI.Window(WindowId, _window, DrawWindow, GUIContent.none, _sWindow);
            _window.x = Mathf.Clamp(_window.x, 0f, Mathf.Max(0f, sw - w));
            _window.y = Mathf.Clamp(_window.y, 0f, Mathf.Max(0f, sh - h));
            GUI.matrix = previousMatrix; // leave the drawing scale as we found it, for whatever draws after us
        }

        private enum Tab { Mods, Browse, Players, Sources, Settings }

        private Tab _tab = Tab.Mods;
        private Filter _filter = Filter.All;
        private string _search = "";

        private void DrawWindow(int id)
        {
            DrawHeader();
            GUILayout.Space(6);
            DrawTabs();
            GUILayout.Space(8);
            DrawBanners();

            _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.ExpandHeight(true));
            switch (_tab)
            {
                case Tab.Mods: DrawMods(false); break;
                case Tab.Browse: DrawMods(true); break;
                case Tab.Players: DrawPlayers(); break;
                case Tab.Sources: DrawFeeds(); GUILayout.Space(14); DrawLogin(); break;
                case Tab.Settings: DrawSettings(); break;
            }
            GUILayout.EndScrollView();

            DrawStatusBar();
            GUI.DragWindow(new Rect(0, 0, 10000, 46)); // drag by the header
        }

        private void DrawHeader()
        {
            int pending = _rows.Count(NeedsUpdate);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Mod Manager", _sTitle, GUILayout.ExpandWidth(false));
            GUILayout.Space(8);
            GUILayout.BeginVertical();
            GUILayout.Space(9);
            GUILayout.Label($"v{Version}", _sVer, GUILayout.ExpandWidth(false));
            GUILayout.EndVertical();
            GUILayout.Space(14);

            // one glance: is anything waiting?
            string summary; Color summaryColor;
            if (_busy) { summary = "Checking..."; summaryColor = PillBlue; }
            else if (_lastRefresh == DateTime.MinValue) { summary = "Not checked yet"; summaryColor = PillGrey; }
            else if (pending > 0) { summary = pending == 1 ? "1 update ready" : $"{pending} updates ready"; summaryColor = PillAmber; }
            else { summary = "All up to date"; summaryColor = PillGreen; }
            GUILayout.BeginVertical();
            GUILayout.Space(6);
            Pill(summary, summaryColor, 130);
            GUILayout.EndVertical();

            GUILayout.FlexibleSpace();
            if (Button("Refresh", 90, false, !_busy && Configured)) Defer(() => StartCoroutine(RefreshRoutine(autoInstall: false)));
            if (Button(pending > 0 ? $"Update all ({pending})" : "Update all", 130, pending > 0, !_busy && pending > 0)) Defer(InstallAll);
            if (Button("Close", 70)) Defer(() => WindowOpen = false);
            GUILayout.EndHorizontal();
        }

        private void DrawTabs()
        {
            int pending = _rows.Count(NeedsUpdate);
            GUILayout.BeginHorizontal();
            TabButton(Tab.Mods, pending > 0 ? $"My mods  ({pending} new)" : "My mods");
            TabButton(Tab.Browse, "Browse");
            TabButton(Tab.Players, "Players");
            TabButton(Tab.Sources, "Sources");
            TabButton(Tab.Settings, "Settings");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void TabButton(Tab tab, string label)
        {
            if (GUILayout.Button(label, _tab == tab ? _sTabOn : _sTab, GUILayout.Height(28), GUILayout.MinWidth(110)))
                Defer(() => { _tab = tab; _filter = Filter.All; _scroll = Vector2.zero; });
        }

        /// <summary>Things that need attention whichever tab you are on.</summary>
        private void DrawBanners()
        {
            HashSet<string> waiting = RestartPending;
            if (waiting.Count > 0)
            {
                string names = string.Join(", ", _rows.Where(r => r.Remote != null && waiting.Contains(r.Remote.guid)).Select(r => r.Name).ToArray());
                GUILayout.BeginVertical(_sBanner);
                GUILayout.Label("Restart the game to finish updating" + (names.Length > 0 ? ": " + names : "") + ". Until then the old version keeps running.", TextStyle(13, Warn, FontStyle.Bold, true));
                GUILayout.EndVertical();
                GUILayout.Space(8);
            }
            if (_needsSetup && _tab != Tab.Sources)
            {
                DrawSetup();
                GUILayout.Space(8);
            }
        }

        /// <summary>How the manager signs in to GitHub, with a way to change it (Sources tab).</summary>
        private void DrawLogin()
        {
            GUILayout.Label("GitHub login", _sH2);
            GUILayout.Space(2);
            GUILayout.BeginVertical(_sCard);
            if (_needsSetup) { GUILayout.EndVertical(); DrawSetup(); return; }

            bool hasToken = !string.IsNullOrEmpty(_token.Value);
            GUILayout.Label(hasToken ? "Using your read-only access token." : _allowGitHubCli.Value ? "Using your GitHub CLI login." : "No login. That is fine for public repos (GitHub allows 60 checks per hour).", _sBody);
            if (hasToken) { GUILayout.BeginHorizontal(); if (Button("Remove token", 130, false, true)) Defer(() => _token.Value = ""); GUILayout.FlexibleSpace(); GUILayout.EndHorizontal(); }
            GUILayout.Space(4);
            Toggle(_allowGitHubCli, "Allow GitHub CLI login", "Uses your whole GitHub sign-in if no token is set. Leave off unless you need it.");
            GUILayout.EndVertical();
        }

        // ---- connecting to GitHub --------------------------------------------------------------

        private string _tokenInput = "";

        /// <summary>Shown only while we have no credentials: paste a read-only token, or explicitly allow the GitHub CLI.</summary>
        private void DrawSetup()
        {
            GUILayout.BeginVertical(_sCard);
            GUILayout.Label("Connect to GitHub", _sH2);

            if (string.IsNullOrEmpty(_owner.Value) || string.IsNullOrEmpty(_repo.Value))
            {
                GUILayout.Label($"Owner and Repo aren't set yet. Fill them in under [Repo] in BepInEx\\config\\{Guid}.cfg, then press F6.", _sBody);
                GUILayout.EndVertical();
                return;
            }

            GUILayout.Label($"GitHub won't show {_loginFeed} without a login, so it looks like a private repo and the manager needs an access token for it. " +
                            "Create a fine-grained token on GitHub (Settings > Developer settings > Fine-grained tokens) limited to " +
                            "ONLY this repository with Contents set to Read-only, then paste it here.", _sBody);
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            _tokenInput = GUILayout.PasswordField(_tokenInput, '*', 255, GUILayout.ExpandWidth(true), GUILayout.Height(28));
            if (Button("Save token", 120, true, _tokenInput.Trim().Length > 0))
                Defer(() =>
                {
                    _token.Value = _tokenInput.Trim();   // saved in the config file on this PC
                    _tokenInput = "";
                    StartCoroutine(RefreshRoutine(autoInstall: false));
                });
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("Don't want to make a token? You can let the manager borrow your GitHub CLI (gh) login instead. " +
                            "Be aware: that login is your whole GitHub sign-in, with much broader access than one read-only token. " +
                            "Only allow it if you're comfortable with that. You can turn it off again at the bottom of this window.", _sDim);
            GUILayout.BeginHorizontal();
            if (Button("Allow GitHub CLI login", 200)) Defer(() => _allowGitHubCli.Value = true);
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        // ---- mods tab --------------------------------------------------------------------------

        private enum Filter { All, Updates, Available, Installed }

        private static int SortKey(Row r, Func<Row, bool> needsUpdate)
        {
            if (needsUpdate(r)) return 0;
            if (r.Status == Status.NotInstalled) return 1;
            if (r.Status == Status.Disabled) return 3;
            if (r.Status == Status.LocalOnly) return 4;
            return 2;
        }

        private bool Matches(Row r, Filter f)
        {
            switch (f)
            {
                case Filter.Updates: return NeedsUpdate(r);
                case Filter.Available: return r.Status == Status.NotInstalled;
                case Filter.Installed: return r.Local != null;
                default: return true;
            }
        }

        /// <summary>
        /// "My mods" (browse = false): what you have installed, with updates first.
        /// "Browse" (browse = true): everything the sources you watch offer, grouped by source, so you can pick what to install.
        /// </summary>
        private void DrawMods(bool browse)
        {
            // Loader plugins (ScriptEngine, ...) live in the plugins folder and just get a line at the bottom of My mods.
            List<Row> all = browse ? _rows.Where(r => r.Remote != null).ToList()
                                   : _rows.Where(r => !IsLoader(r) && r.Status != Status.NotInstalled).ToList();

            // Work out the visible list first (from the search text as it was when this pass began), then draw the box.
            Filter second = browse ? Filter.Available : Filter.Updates;
            string query = (_search ?? "").Trim();
            Filter active = _filter == second ? second : Filter.All;
            List<Row> shown = all.Where(r => Matches(r, active) &&
                    (query.Length == 0 || r.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     (r.Description ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(r => SortKey(r, NeedsUpdate)).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

            GUILayout.BeginHorizontal();
            foreach (Filter f in new[] { Filter.All, second })
            {
                int n = all.Count(r => Matches(r, f));
                string label = (f == Filter.Available ? "Not installed" : f.ToString()) + "  " + n;
                Filter chosen = f;
                if (GUILayout.Button(label, f == active ? _sTabOn : _sTab, GUILayout.Height(26))) Defer(() => _filter = chosen);
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label("Search", _sDim, GUILayout.Width(48));
            GUI.SetNextControlName("modsearch");
            _search = GUILayout.TextField(_search ?? "", 40, GUILayout.Width(180), GUILayout.Height(26));
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            if (browse)
            {
                // One heading per source (only when there is more than one); the main source first.
                var groups = shown.GroupBy(r => r.Feed).OrderBy(g => g.Key != null && g.Key.Primary ? 0 : 1).ToList();
                bool headings = _rows.Select(r => r.Feed).Where(f => f != null).Distinct().Count() > 1;
                foreach (var group in groups)
                {
                    if (headings && group.Key != null)
                        GUILayout.Label("From " + group.Key.Label + (group.Key.Primary ? "  (main source)" : ""), _sH2);
                    foreach (Row row in group) DrawCard(row);
                    GUILayout.Space(6);
                }
            }
            else
            {
                foreach (Row row in shown) DrawCard(row);
            }

            if (shown.Count == 0)
            {
                string empty = _busy ? "Checking GitHub..."
                    : browse && !Configured ? "Nothing to browse until a source is set up (Sources tab)."
                    : browse && all.Count == 0 ? "No mods yet. Press Refresh to load what your sources offer."
                    : query.Length > 0 ? $"No mod matches \"{query}\"."
                    : browse && active == Filter.Available ? "You already have every mod your sources offer."
                    : browse ? "Nothing here."
                    : active == Filter.Updates ? "Everything is up to date."
                    : "You have not installed any mods yet. Open Browse to pick some.";
                GUILayout.Label(empty, TextStyle(13, TextDim, FontStyle.Normal, true));
            }

            List<Row> others = _rows.Where(IsLoader).ToList();
            if (!browse && others.Count > 0 && active == Filter.All && query.Length == 0)
            {
                GUILayout.Space(10);
                GUILayout.Label("Loaders (needed for mods to run)", _sDim);
                foreach (Row row in others) GUILayout.Label($"{row.Name}   v{row.LocalVersion}", _sDim);
            }
        }

        // ---- what other players have that you do not ---------------------------------------------

        private string _sourceConfirm;
        private float _sourceConfirmAt;

        /// <summary>Mods other players in this world have that you do not, each with the quickest way to get it.</summary>
        private void DrawMissing()
        {
            if (ZRoutedRpc.instance == null || Player.m_localPlayer == null || _peers.Count == 0) return;
            Dictionary<long, string> people = OtherPlayers();

            // guid -> { name, version, where they got it }, and who has it
            var missing = new Dictionary<string, string[]>();
            var who = new Dictionary<string, List<string>>();
            foreach (var kv in _peers)
            {
                if (!people.TryGetValue(kv.Key, out string person)) continue;
                foreach (var m in kv.Value.Versions)
                {
                    if (_local.Any(l => l.Guid == m.Key)) continue; // you already have it
                    kv.Value.Names.TryGetValue(m.Key, out string name);
                    kv.Value.Sources.TryGetValue(m.Key, out string source);
                    if (!missing.ContainsKey(m.Key)) { missing[m.Key] = new[] { name ?? m.Key, m.Value, source ?? "" }; who[m.Key] = new List<string>(); }
                    who[m.Key].Add(person);
                }
            }
            if (missing.Count == 0) return;

            GUILayout.Label($"Mods other players have that you do not  ({missing.Count})", _sH2);
            GUILayout.Space(2);
            foreach (var kv in missing.OrderBy(k => k.Value[0], StringComparer.OrdinalIgnoreCase))
            {
                string guid = kv.Key;
                Row row = _rows.FirstOrDefault(r => r.Remote != null && r.Remote.guid == guid);
                Feed offered = ParseFeed(kv.Value[2]);
                if (offered != null && Feeds().Any(f => f.Spec == offered.Spec)) offered = null; // already watching it

                GUILayout.BeginVertical(_sCard);
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
                GUILayout.Label(row != null ? row.Name : kv.Value[0], _sName);
                GUILayout.Label("Has it: " + string.Join(", ", who[guid].ToArray()), _sDim);
                GUILayout.EndVertical();

                GUILayout.BeginVertical(GUILayout.Width(190));
                if (row != null && row.Status == Status.NotInstalled)
                {
                    if (Button("Install", 184, true, !_busy)) Defer(() => InstallOne(row));
                }
                else if (offered != null)
                {
                    bool sure = _sourceConfirm == offered.Spec && Time.realtimeSinceStartup - _sourceConfirmAt < 8f;
                    if (Button(sure ? "Yes, I trust them" : "Add " + offered.Label, 184, false, !_busy))
                    {
                        Feed f = offered;
                        Defer(() =>
                        {
                            if (_sourceConfirm == f.Spec && Time.realtimeSinceStartup - _sourceConfirmAt < 8f)
                            {
                                SetYourFeeds(YourFeeds().Select(x => x.Spec).Concat(new[] { f.Spec }));
                                _sourceConfirm = null;
                                StartCoroutine(RefreshRoutine(autoInstall: false));
                            }
                            else { _sourceConfirm = f.Spec; _sourceConfirmAt = Time.realtimeSinceStartup; }
                        });
                    }
                }
                else GUILayout.Label("Not offered by any source you watch", _sDim);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUILayout.Space(6);
            }
            GUILayout.Label("Adding a source lets you install their mods from Browse. Their mods run inside your game, so only add people you trust.", _sDim);
            GUILayout.Space(14);
        }

        private void DrawCard(Row row)
        {
            GUILayout.BeginVertical(_sCard);
            string key = row.Remote != null ? row.Remote.guid : row.Local != null ? row.Local.Guid : row.Name;
            SplitDescription(row.Description, out string summary, out string details);
            GUILayout.BeginHorizontal();
            DrawCover(row);
            GUILayout.Space(10);

            // left: name, version, description, notes
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.BeginHorizontal();
            GUILayout.Label(row.Name, _sName, GUILayout.ExpandWidth(false));
            GUILayout.Space(8);
            GUILayout.BeginVertical();
            GUILayout.Space(2);
            GUILayout.Label(VersionText(row), _sVer, GUILayout.ExpandWidth(false));
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            if (summary.Length > 0) GUILayout.Label(summary, _sDim);
            else GUILayout.Label("No description yet.", _sDim);
            if (row.Feed != null && !row.Feed.Primary)
                GUILayout.Label("From " + row.Feed.Label + "  (not your main source: you are trusting their code)", TextStyle(12, Warn, FontStyle.Normal, true));
            if (row.Remote != null && !string.IsNullOrEmpty(row.Remote.restart) && (row.Status == Status.UpdateAvailable || row.Status == Status.Rebuilt || row.Status == Status.NotInstalled))
                GUILayout.Label("Takes effect after restarting the game. " + row.Remote.restart, TextStyle(12, Warn, FontStyle.Normal, true));
            if (!string.IsNullOrEmpty(row.Notes) && (row.Status == Status.UpdateAvailable || row.Status == Status.NotInstalled))
                GUILayout.Label("New in v" + row.RemoteVersion + ":  " + row.Notes, TextStyle(12, TextMain, FontStyle.Normal, true));
            GUILayout.EndVertical();

            // right: status, then the actions
            GUILayout.BeginVertical(GUILayout.Width(158));
            StatusLook(row, out string pillText, out Color pillColor);
            Pill(pillText, pillColor, 152);
            switch (row.Status)
            {
                case Status.NotInstalled:
                    if (Button("Install", 152, true, !_busy)) Defer(() => InstallOne(row));
                    break;
                case Status.UpdateAvailable:
                    if (Button("Update", 152, true, !_busy)) Defer(() => InstallOne(row));
                    break;
                case Status.Rebuilt:
                    if (!_developerMode.Value)
                    {
                        if (Button("Update", 152, true, !_busy)) Defer(() => InstallOne(row));
                    }
                    else
                    {
                        // Developer Mode: this overwrites your own build, so ask twice (the "sure?" state times out after 5s).
                        bool sure = _confirmRevert == row.Name && Time.realtimeSinceStartup - _confirmRevertAt < 5f;
                        if (Button(sure ? "Really replace mine?" : "Use GitHub copy", 152, false, !_busy))
                        {
                            if (sure) Defer(() => { _confirmRevert = null; InstallOne(row); });
                            else Defer(() => { _confirmRevert = row.Name; _confirmRevertAt = Time.realtimeSinceStartup; });
                        }
                    }
                    break;
            }
            if (row.CanToggle)
            {
                bool disabled = row.Status == Status.Disabled;
                GUILayout.BeginHorizontal();
                if (!disabled && Button("Reload", 74, false, !_busy, small: true)) Defer(() => ReloadOne(row));
                if (Button(disabled ? "Enable" : "Disable", disabled ? 152 : 74, disabled, !_busy, small: !disabled)) Defer(() => SetEnabled(row, disabled));
                GUILayout.EndHorizontal();
            }
            if (details.Length > 0 && Button(_expanded.Contains(key) ? "Hide details" : "Details", 152, false, true, true))
                Defer(() => { if (!_expanded.Remove(key)) _expanded.Add(key); });
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
            if (_expanded.Contains(key) && details.Length > 0)
            {
                GUILayout.Space(6);
                GUILayout.Label(details, _sBody);
            }
            GUILayout.EndVertical(); // the card
            GUILayout.Space(6);
        }

        /// <summary>A loader plugin from the plugins folder (ScriptEngine and friends): not one of "our" mods.</summary>
        private static bool IsLoader(Row row) => row.Status == Status.LocalOnly && row.Local != null && row.Local.InPlugins;

        private static string VersionText(Row row)
        {
            switch (row.Status)
            {
                case Status.UpdateAvailable: return $"v{row.LocalVersion}  ->  v{row.RemoteVersion}";
                case Status.NotInstalled: return $"v{row.RemoteVersion} on GitHub";
                case Status.LocalNewer: return $"v{row.LocalVersion}   (GitHub has v{row.RemoteVersion})";
                default: return $"v{row.LocalVersion}";
            }
        }

        private void StatusLook(Row row, out string text, out Color color)
        {
            if (row.Remote != null && RestartPending.Contains(row.Remote.guid)) { text = "Restart the game"; color = PillAmber; return; }
            switch (row.Status)
            {
                case Status.UpToDate: text = "Up to date"; color = PillGreen; break;
                case Status.UpdateAvailable: text = "Update available"; color = PillAmber; break;
                case Status.NotInstalled: text = "Not installed"; color = PillRed; break;
                case Status.LocalNewer: text = "Newer than GitHub"; color = PillBlue; break;
                case Status.Rebuilt:
                    // Same version number but different bytes: for the mod's author that's just "I rebuilt it and haven't pushed".
                    if (_developerMode.Value) { text = "Unpublished changes"; color = PillBlue; }
                    else { text = "Update available"; color = PillAmber; }
                    break;
                case Status.Disabled: text = "Disabled"; color = PillGrey; break;
                default:
                    // A mod in your scripts folder that isn't on GitHub (yet): fine while you're building it.
                    if (row.Local != null && !row.Local.InPlugins) { text = "Not published yet"; color = PillBlue; }
                    else { text = "Local only"; color = PillGrey; }
                    break;
            }
        }

        // ---- players ---------------------------------------------------------------------------

        private void DrawPlayers()
        {
            DrawMissing();
            GUILayout.Label("Players", _sH2);
            GUILayout.Space(2);

            if (_remote.Length == 0)
            {
                GUILayout.Label("Press Refresh to compare against the versions on GitHub.", _sDim);
                return;
            }
            if (ZRoutedRpc.instance == null || Player.m_localPlayer == null)
            {
                GUILayout.Label("Join a world to see what the other players have.", _sDim);
                return;
            }

            const float nameWidth = 150f;
            float colWidth = Mathf.Clamp((_window.width - nameWidth - 90f) / Mathf.Max(1, _remote.Length), 110f, 220f);

            GUILayout.BeginVertical(_sCard);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Player", _sDim, GUILayout.Width(nameWidth));
            foreach (RemoteMod r in _remote) GUILayout.Label(r.name, _sDim, GUILayout.Width(colWidth));
            GUILayout.EndHorizontal();

            DrawPlayerRow("You", VersionsOf(_local), colWidth, nameWidth);

            Dictionary<long, string> others = OtherPlayers();
            int behind = 0;
            foreach (var kv in others)
            {
                if (_peers.TryGetValue(kv.Key, out PeerMods peer))
                {
                    if (DrawPlayerRow(kv.Value, peer.Versions, colWidth, nameWidth)) behind++;
                }
                else
                {
                    behind++;
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(kv.Value, _sName, GUILayout.Width(nameWidth));
                    Pill("no mod manager yet", PillRed, colWidth * _remote.Length - 6);
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndVertical();

            GUILayout.Space(4);
            if (others.Count == 0) GUILayout.Label("Nobody else is in this world right now.", _sDim);
            else if (behind == 0) GUILayout.Label("Everyone is up to date.", TextStyle(12, Good));
            else GUILayout.Label($"{behind} player(s) need updates. They can press {_hotkey.Value} and click Update all.", TextStyle(12, Warn, FontStyle.Normal, true));
        }

        private static Dictionary<string, string> VersionsOf(IEnumerable<LocalMod> mods)
        {
            var d = new Dictionary<string, string>();
            foreach (LocalMod m in mods) if (!m.Disabled) d[m.Guid] = m.Version; // (a plain loop: tolerates duplicate guids)
            return d;
        }

        /// <summary>One line per player; returns true if they're missing something or behind.</summary>
        private bool DrawPlayerRow(string who, Dictionary<string, string> versions, float colWidth, float nameWidth)
        {
            bool behind = false;
            GUILayout.BeginHorizontal();
            GUILayout.Label(who, _sName, GUILayout.Width(nameWidth));
            foreach (RemoteMod r in _remote)
            {
                if (!versions.TryGetValue(r.guid, out string v)) { Pill("missing", PillRed, colWidth - 6); behind = true; }
                else if (CompareVersions(v, r.version) >= 0) Pill("v" + v, PillGreen, colWidth - 6);
                else { Pill($"v{v} (old)", PillAmber, colWidth - 6); behind = true; }
            }
            GUILayout.EndHorizontal();
            return behind;
        }

        // ---- settings tab ----------------------------------------------------------------------

        private void DrawSettings()
        {
            GUILayout.Label("Settings", _sH2);
            GUILayout.Space(2);
            GUILayout.BeginVertical(_sCard);

            Toggle(_checkOnStart, "Install updates automatically when the game starts", "Only for mods from the main source. Other sources always ask first.");
            Toggle(_notifyOnJoin, "Tell me in chat when updates are waiting", "Shown once when you enter a world.");
            Toggle(_developerMode, "Developer mode", "For people building mods: rebuilt files count as your own changes, not as updates.");
            if (_developerMode.Value) Toggle(_autoReload, "Reload my mods automatically when I rebuild them", null);

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Window size  ({_uiScale.Value:0.00}x)", _sBody, GUILayout.Width(170));
            float scale = GUILayout.HorizontalSlider(_uiScale.Value, 0.75f, 1.5f, GUILayout.Width(220));
            if (Mathf.Abs(scale - _uiScale.Value) > 0.005f) _uiScale.Value = Mathf.Round(scale * 20f) / 20f;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(12);
            GUILayout.Label("Tools", _sH2);
            GUILayout.Space(2);
            GUILayout.BeginVertical(_sCard);
            GUILayout.BeginHorizontal();
            if (Button("Reload all mods", 150, false, true)) Defer(() => { if (!ReloadScripts()) _statusLine = "ScriptEngine not found: press F6 instead."; });
            if (Button("Open mods folder", 150, false, true)) Defer(() => System.Diagnostics.Process.Start("explorer.exe", _scriptsDir));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Label($"Press {_hotkey.Value} to open or close this window (change it in BepInEx\\config\\{Guid}.cfg).", _sDim);
            GUILayout.EndVertical();
        }

        /// <summary>A checkbox with a line of explanation under it. The change is applied from Update, not mid-draw.</summary>
        private void Toggle(BepInEx.Configuration.ConfigEntry<bool> entry, string label, string hint)
        {
            bool now = GUILayout.Toggle(entry.Value, label, _sToggle);
            if (now != entry.Value) { bool value = now; Defer(() => entry.Value = value); }
            if (hint != null) GUILayout.Label(hint, _sDim);
            GUILayout.Space(4);
        }

        // ---- status bar ------------------------------------------------------------------------

        private void DrawStatusBar()
        {
            GUILayout.Box(GUIContent.none, _sRule, GUILayout.ExpandWidth(true));
            bool failed = _statusLine.StartsWith("Check failed") || _statusLine.StartsWith("Download failed") ||
                          _statusLine.StartsWith("Couldn't") || _statusLine.StartsWith("Owner and Repo") || _statusLine.StartsWith("No manifest") ||
                          _statusLine.StartsWith("Can't") || _statusLine.StartsWith("GitHub's limit");
            Color c = failed ? Bad : _busy ? Warn : TextDim;
            string text = _busy ? _statusLine + new string('.', 1 + (int)(Time.realtimeSinceStartup * 2f) % 3) : _statusLine;
            GUILayout.Label(text, TextStyle(12, c, FontStyle.Normal, true));
        }

        // ---- widgets ---------------------------------------------------------------------------

        private void Defer(Action action) => _deferred = action;

        private bool Button(string text, float width, bool primary = false, bool enabled = true, bool small = false)
        {
            bool previous = GUI.enabled;
            GUI.enabled = previous && enabled;
            GUIStyle style = primary ? _sBtnPrimary : small ? _sBtnSmall : _sBtn;
            bool clicked = GUILayout.Button(text, style, GUILayout.Width(width), GUILayout.Height(small ? 26 : 30));
            GUI.enabled = previous;
            return clicked;
        }

        private void Pill(string text, Color background, float width)
        {
            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = background;
            GUILayout.Label(text, _sPill, GUILayout.Width(width), GUILayout.Height(24));
            GUI.backgroundColor = previous;
        }
    }
}
