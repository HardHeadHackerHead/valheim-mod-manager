using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ModUpdater
{
    /// <summary>
    /// The "Mod settings" tab: every setting of every loaded mod, edited right here instead of in a text file. It reads the mods' own
    /// BepInEx settings (so it works for any mod, not only ours) and changes them live: our mods read their settings as they go, so most
    /// changes apply at once; for the rest there is a Reload button. Each change is saved to the mod's config file a moment later.
    /// </summary>
    public partial class Plugin
    {
        private class ModConfig
        {
            public string Guid, Name;
            public ConfigFile File;
        }

        private List<ModConfig> _msMods = new List<ModConfig>();
        private string _msGuid;
        private string _msSearch = "";
        private Vector2 _msScroll;
        private float _msRefreshAt;

        private ConfigEntryBase _capturing;                                          // the setting waiting for a key press
        private readonly Dictionary<ConfigEntryBase, string> _msText = new Dictionary<ConfigEntryBase, string>(); // what a text box shows while you type
        private readonly HashSet<ConfigFile> _msDirty = new HashSet<ConfigFile>();
        private float _msLastEdit;

        /// <summary>True while a settings text box has focus or a key is being captured, so other mods ignore the keys you type.</summary>
        internal static bool TypingInSettings => Instance != null && (Instance._capturing != null || GUI.GetNameOfFocusedControl().StartsWith("set_"));
        internal static Plugin Instance;

        /// <summary>Escape while a setting is waiting for a key: cancel that instead of closing the window. True if it did.</summary>
        internal static bool CancelCapture()
        {
            if (Instance == null || Instance._capturing == null) return false;
            Instance._capturing = null;
            return true;
        }

        private static readonly PropertyInfo PluginConfig = AccessTools.Property(typeof(BaseUnityPlugin), "Config");

        private bool _msLogged;
        private int _msLastCount = -1;

        private void RefreshModConfigs()
        {
            if (Time.unscaledTime < _msRefreshAt) return;
            _msRefreshAt = Time.unscaledTime + 2f;
            var found = new List<ModConfig>();
            var notes = new List<string>();
            foreach (BaseUnityPlugin plugin in Resources.FindObjectsOfTypeAll<BaseUnityPlugin>()) // (not FindObjectsOfType: BepInEx keeps its plugins on a hidden object, which that call skips)
            {
                if (plugin == null || !plugin.gameObject.scene.IsValid() && plugin.gameObject.hideFlags == HideFlags.None) continue; // a prefab or asset, not a running mod
                string who = plugin.GetType().Name;
                try
                {
                    ConfigFile file = ConfigOf(plugin);
                    BepInPlugin meta = MetadataHelper.GetMetadata(plugin);
                    if (file == null) { notes.Add($"{who}: no settings object"); continue; }
                    if (meta == null) { notes.Add($"{who}: no plugin info"); continue; }
                    if (file.Keys.Count == 0) { notes.Add($"{meta.Name}: no settings"); continue; }
                    if (found.Any(f => f.Guid == meta.GUID)) { notes.Add($"{meta.Name}: a second copy, left out"); continue; }
                    found.Add(new ModConfig { Guid = meta.GUID, Name = meta.Name, File = file });
                }
                catch (Exception e) { notes.Add($"{who}: {e.GetType().Name} {e.Message}"); }
            }
            _msMods = found.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (_msGuid == null || _msMods.All(f => f.Guid != _msGuid)) _msGuid = _msMods.FirstOrDefault()?.Guid;

            if (!_msLogged || _msLastCount != found.Count)
            {
                _msLogged = true;
                _msLastCount = found.Count;
                Logger.LogInfo($"Mod settings: {found.Count} mod(s) with settings ({string.Join(", ", _msMods.Select(m => m.Name + " " + m.File.Keys.Count).ToArray())}); left out: {(notes.Count == 0 ? "none" : string.Join("; ", notes.ToArray()))}");
            }
        }

        /// <summary>A plugin's settings object (BepInEx keeps it in a property, a protected one in some versions).</summary>
        private static ConfigFile ConfigOf(BaseUnityPlugin plugin)
        {
            if (PluginConfig != null) return PluginConfig.GetValue(plugin, null) as ConfigFile;
            FieldInfo field = typeof(BaseUnityPlugin).GetField("<Config>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(plugin) as ConfigFile;
        }

        /// <summary>Save any edited config files once you have stopped changing things for a moment (so dragging a slider is not a file write per frame).</summary>
        private void SaveSettingsSoon()
        {
            if (_msDirty.Count == 0 || Time.unscaledTime - _msLastEdit < 0.8f) return;
            foreach (ConfigFile file in _msDirty) { try { file.Save(); } catch (Exception e) { Logger.LogWarning("Could not save settings: " + e.Message); } }
            _msDirty.Clear();
        }

        private void SetValue(ConfigFile file, ConfigEntryBase entry, object value)
        {
            if (Equals(entry.BoxedValue, value)) return;
            bool before = file.SaveOnConfigSet;
            file.SaveOnConfigSet = false;               // we save later, in one go
            try { entry.BoxedValue = value; } finally { file.SaveOnConfigSet = before; }
            _msDirty.Add(file);
            _msLastEdit = Time.unscaledTime;
        }

        // ---- drawing ---------------------------------------------------------------------------

        /// <summary>Open the Mod settings tab on one mod (from the Settings button on its card).</summary>
        private void OpenSettingsFor(string guid)
        {
            _msGuid = guid;
            _msScroll = Vector2.zero;
            _tab = Tab.ModSettings;
            _scroll = Vector2.zero;
        }

        private bool HasSettings(string guid) => _msMods.Any(m => m.Guid == guid);

        private void DrawModSettings()
        {
            GUILayout.Label("Mod settings", _sH2);
            GUILayout.Label("Pick a mod on the left to see and change its settings. Most changes apply straight away; if one does not, press Reload for that mod. Changes are saved for next time.", _sDim);
            GUILayout.Space(6);

            if (_msMods.Count == 0) { GUILayout.Label("No mod with settings is loaded.", _sDim); return; }

            GUILayout.BeginHorizontal();

            // left: the mods that have settings
            GUILayout.BeginVertical(GUILayout.Width(190));
            foreach (ModConfig m in _msMods)
            {
                int count = m.File.Keys.Count;
                if (GUILayout.Button($"{m.Name}   ({count})", m.Guid == _msGuid ? _sTabOn : _sTab, GUILayout.Height(28)))
                { string g = m.Guid; Defer(() => { _msGuid = g; _msScroll = Vector2.zero; }); }
            }
            GUILayout.EndVertical();
            GUILayout.Space(10);

            // right: the chosen mod's settings
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            ModConfig mod = _msMods.FirstOrDefault(f => f.Guid == _msGuid);
            if (mod != null) DrawOneMod(mod);
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }

        private void DrawOneMod(ModConfig mod)
        {
            GUILayout.Label(mod.Name, _sTitle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", _sDim, GUILayout.Width(48));
            GUI.SetNextControlName("set_search");
            _msSearch = GUILayout.TextField(_msSearch ?? "", 40, GUILayout.Width(200), GUILayout.Height(26));
            GUILayout.FlexibleSpace();
            LocalMod local = _local.FirstOrDefault(l => l.Guid == mod.Guid && !l.InPlugins && !l.Disabled);
            bool restartOnly = local != null && NeedsRestart(local.Guid); // greyed out, and says why
            if (local != null && Button(restartOnly ? "Needs a game restart" : "Reload this mod", 150, false, !_busy && !restartOnly, true))
                Defer(() => ReloadMods(new List<ModFile> { new ModFile { Guid = local.Guid, Path = local.Path } }));
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            string query = (_msSearch ?? "").Trim();
            int index = 0;
            foreach (var section in mod.File.Keys.GroupBy(k => k.Section))
            {
                var entries = section.Select(def => mod.File[def])
                    .Where(e => query.Length == 0 || e.Definition.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                (e.Description.Description ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                if (entries.Count == 0) continue;

                GUILayout.Label(section.Key, _sH2);
                GUILayout.BeginVertical(_sCard);
                foreach (ConfigEntryBase entry in entries) { DrawEntry(mod, entry, index++); GUILayout.Space(6); }
                GUILayout.EndVertical();
                GUILayout.Space(8);
            }
        }

        private void DrawEntry(ModConfig mod, ConfigEntryBase entry, int index)
        {
            Type type = entry.SettingType;
            GUILayout.BeginHorizontal();
            GUILayout.Label(entry.Definition.Key, _sName, GUILayout.Width(210));

            string control = "set_" + index;
            bool known = true;
            if (type == typeof(bool))
            {
                bool value = (bool)entry.BoxedValue;
                if (GUILayout.Button(value ? "ON" : "OFF", value ? _sBtnPrimary : _sBtn, GUILayout.Width(80), GUILayout.Height(26))) { bool v = !value; Defer(() => SetValue(mod.File, entry, v)); }
            }
            else if (type == typeof(int) || type == typeof(float) || type == typeof(double))
            {
                DrawNumber(mod, entry, control);
            }
            else if (type.IsEnum)
            {
                string[] names = Enum.GetNames(type);
                int at = Array.IndexOf(names, entry.BoxedValue.ToString());
                if (GUILayout.Button("<", _sBtnSmall, GUILayout.Width(28), GUILayout.Height(26))) { int n = (at - 1 + names.Length) % names.Length; Defer(() => SetValue(mod.File, entry, Enum.Parse(type, names[n]))); }
                GUILayout.Label(names[Mathf.Max(0, at)], _sBody, GUILayout.Width(150));
                if (GUILayout.Button(">", _sBtnSmall, GUILayout.Width(28), GUILayout.Height(26))) { int n = (at + 1) % names.Length; Defer(() => SetValue(mod.File, entry, Enum.Parse(type, names[n]))); }
            }
            else if (type == typeof(string))
            {
                if (!_msText.TryGetValue(entry, out string text) || GUI.GetNameOfFocusedControl() != control) text = (string)entry.BoxedValue ?? "";
                GUI.SetNextControlName(control);
                string edited = GUILayout.TextField(text, 200, GUILayout.Width(300), GUILayout.Height(26));
                _msText[entry] = edited;
                if (edited != (string)entry.BoxedValue) Defer(() => SetValue(mod.File, entry, edited));
            }
            else if (type == typeof(KeyCode) || type == typeof(KeyboardShortcut))
            {
                DrawKey(mod, entry, type == typeof(KeyboardShortcut));
            }
            else if (type == typeof(Color))
            {
                DrawColor(mod, entry, control);
            }
            else
            {
                known = false;
                GUILayout.Label(entry.GetSerializedValue(), _sDim);
            }

            GUILayout.FlexibleSpace();
            bool isDefault = Equals(entry.BoxedValue, entry.DefaultValue);
            if (known && !isDefault && GUILayout.Button("Reset", _sBtnSmall, GUILayout.Width(56), GUILayout.Height(24))) Defer(() => { _msText.Remove(entry); SetValue(mod.File, entry, entry.DefaultValue); });
            GUILayout.EndHorizontal();

            string description = entry.Description?.Description;
            if (!string.IsNullOrEmpty(description)) GUILayout.Label(description, _sDim);
        }

        private void DrawNumber(ModConfig mod, ConfigEntryBase entry, string control)
        {
            Type type = entry.SettingType;
            object boxed = entry.BoxedValue;
            double current = Convert.ToDouble(boxed, CultureInfo.InvariantCulture);

            // a slider when the setting has a range
            AcceptableValueBase limits = entry.Description?.AcceptableValues;
            double min = double.NaN, max = double.NaN;
            if (limits != null)
            {
                PropertyInfo lo = limits.GetType().GetProperty("MinValue"), hi = limits.GetType().GetProperty("MaxValue");
                if (lo != null && hi != null)
                {
                    min = Convert.ToDouble(lo.GetValue(limits, null), CultureInfo.InvariantCulture);
                    max = Convert.ToDouble(hi.GetValue(limits, null), CultureInfo.InvariantCulture);
                }
            }

            if (!double.IsNaN(min) && max > min)
            {
                float v = GUILayout.HorizontalSlider((float)current, (float)min, (float)max, GUILayout.Width(190));
                if (Math.Abs(v - current) > 1e-6) Defer(() => SetValue(mod.File, entry, FromDouble(type, type == typeof(int) ? Math.Round(v) : Math.Round(v, 3))));
                GUILayout.Space(6);
            }

            if (!_msText.TryGetValue(entry, out string text) || GUI.GetNameOfFocusedControl() != control)
                text = Convert.ToString(boxed, CultureInfo.InvariantCulture);
            GUI.SetNextControlName(control);
            string edited = GUILayout.TextField(text, 12, GUILayout.Width(80), GUILayout.Height(26));
            _msText[entry] = edited;
            if (edited != text && double.TryParse(edited, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                if (!double.IsNaN(min) && max > min) parsed = Math.Max(min, Math.Min(max, parsed));
                Defer(() => SetValue(mod.File, entry, FromDouble(type, parsed)));
            }
        }

        private static object FromDouble(Type type, double value)
        {
            if (type == typeof(int)) return (int)Math.Round(value);
            if (type == typeof(float)) return (float)value;
            return value;
        }

        private void DrawKey(ModConfig mod, ConfigEntryBase entry, bool shortcut)
        {
            bool waiting = _capturing == entry;
            string shown = waiting ? "Press a key..." : entry.GetSerializedValue();
            if (GUILayout.Button(shown, waiting ? _sBtnPrimary : _sBtn, GUILayout.Width(190), GUILayout.Height(26))) Defer(() => _capturing = waiting ? null : entry);

            Event e = Event.current;
            if (waiting && e.type == EventType.KeyDown && e.keyCode != KeyCode.None)
            {
                if (e.keyCode != KeyCode.Escape && !IsModifierKey(e.keyCode))
                {
                    KeyCode key = e.keyCode;
                    bool shift = e.shift, ctrl = e.control, alt = e.alt;
                    ConfigEntryBase target = entry;
                    Defer(() =>
                    {
                        if (shortcut)
                        {
                            var mods = new List<KeyCode>();
                            if (shift) mods.Add(KeyCode.LeftShift);
                            if (ctrl) mods.Add(KeyCode.LeftControl);
                            if (alt) mods.Add(KeyCode.LeftAlt);
                            SetValue(mod.File, target, new KeyboardShortcut(key, mods.ToArray()));
                        }
                        else SetValue(mod.File, target, key);
                        _capturing = null;
                    });
                }
                else if (e.keyCode == KeyCode.Escape) Defer(() => _capturing = null);
                e.Use();
            }
        }

        private static bool IsModifierKey(KeyCode k) =>
            k == KeyCode.LeftShift || k == KeyCode.RightShift || k == KeyCode.LeftControl || k == KeyCode.RightControl || k == KeyCode.LeftAlt || k == KeyCode.RightAlt;

        private void DrawColor(ModConfig mod, ConfigEntryBase entry, string control)
        {
            Color c = (Color)entry.BoxedValue;
            GUILayout.Label("", GUILayout.Width(26), GUILayout.Height(22));
            Rect swatch = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.Repaint) { Color old = GUI.color; GUI.color = new Color(c.r, c.g, c.b, 1f); GUI.DrawTexture(swatch, Texture2D.whiteTexture); GUI.color = old; }

            string hex = "#" + ColorUtility.ToHtmlStringRGBA(c);
            if (!_msText.TryGetValue(entry, out string text) || GUI.GetNameOfFocusedControl() != control) text = hex;
            GUI.SetNextControlName(control);
            string edited = GUILayout.TextField(text, 9, GUILayout.Width(110), GUILayout.Height(26));
            _msText[entry] = edited;
            if (edited != text && ColorUtility.TryParseHtmlString(edited.StartsWith("#") ? edited : "#" + edited, out Color parsed)) Defer(() => SetValue(mod.File, entry, parsed));
        }
    }
}
