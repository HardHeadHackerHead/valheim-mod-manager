using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace YourName.Shared
{
    /// <summary>
    /// A mod's words in the player's language, through the game's own translations (Localization), so they follow the game's Language
    /// setting like everything else. Each mod keeps translations/English.json (every key, the words the mod was written with) and one file
    /// per language (German.json, Russian.json...: the game's language names), built into its DLL. A player can add or correct any language
    /// with a file of the same name in BepInEx/config/translations/&lt;mod GUID&gt;/ (it wins over the built-in one, line by line).
    ///
    ///   In Awake:      Translations.Load(this);      In OnDestroy:  Translations.Unload();
    ///   In code:       T("recycler_button")  or  T("recycler_recycled", name, list)   (the English.json text, with {0} {1} filled in)
    ///   In the game's own fields (a piece's name, a hover text with keys): "$recycler_piece": the game translates it there.
    ///
    /// Keys start with the mod's name ("recycler_" for Recycler), so two mods never share one. A missing translation shows the English
    /// text, never "[key]".
    /// From BepInEx/claude/templates (Claude Tools); the copy our own mods run is mods/Shared/Translations.cs in
    /// https://github.com/HardHeadHackerHead/valheim-mods. Here it is Shared code: Directory.Build.props compiles it into every mod
    /// that has a translations/English.json, and builds that mod's translations/*.json in (nothing to add to the .csproj).
    /// </summary>
    internal static class Translations
    {
        /// <summary>The languages every mod ships (Valheim's names). Others come from players' own files.</summary>
        public static readonly string[] Shipped = { "English", "Russian", "German", "Chinese", "Portuguese_Brazilian", "Spanish", "French", "Polish", "Turkish" };

        private static readonly Dictionary<string, Dictionary<string, string>> Languages = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> _english = new Dictionary<string, string>();
        private static string _overrideDir;
        private static bool _hooked;
        private static readonly MethodInfo AddWord = AccessTools.Method(typeof(Localization), "AddWord");
        private static readonly FieldInfo Cache = AccessTools.Field(typeof(Localization), "m_cache");

        /// <summary>Read this mod's translations and give the game the words for the current language (and again whenever it changes).</summary>
        public static void Load(BaseUnityPlugin plugin)
        {
            Languages.Clear();
            Assembly asm = plugin.GetType().Assembly;
            foreach (string res in asm.GetManifestResourceNames().Where(n => n.StartsWith("translations/") && n.EndsWith(".json")))
                using (var reader = new StreamReader(asm.GetManifestResourceStream(res)))
                    Languages[Path.GetFileNameWithoutExtension(res.Substring("translations/".Length))] = Parse(reader.ReadToEnd());
            _english = Languages.TryGetValue("English", out var en) ? en : new Dictionary<string, string>();
            _overrideDir = Path.Combine(Paths.ConfigPath, "translations", plugin.Info.Metadata.GUID);
            if (!_hooked) { Localization.OnLanguageChange += Apply; _hooked = true; }
            Apply();
        }

        public static void Unload()
        {
            if (_hooked) { Localization.OnLanguageChange -= Apply; _hooked = false; }
        }

        /// <summary>This mod's text for a key in the player's language, with {0}, {1}... filled in.</summary>
        public static string T(string key, params object[] args)
        {
            string text = Localization.instance != null ? Localization.instance.Localize("$" + key) : null;
            if (string.IsNullOrEmpty(text) || text == "[" + key + "]") text = _english.TryGetValue(key, out var en) ? en : key;
            if (args == null || args.Length == 0) return text;
            try { return string.Format(text, args); }
            catch (FormatException) { return _english.TryGetValue(key, out var en) ? SafeFormat(en, args) : text; }   // (a translation that broke a {n})
        }

        private static string SafeFormat(string text, object[] args) { try { return string.Format(text, args); } catch (FormatException) { return text; } }

        /// <summary>English first (every key), then the built-in language, then the player's own file: each a layer over the last.</summary>
        private static void Apply()
        {
            Localization loc = Localization.instance;
            if (loc == null || AddWord == null) return;
            string language = loc.GetSelectedLanguage();
            var words = new Dictionary<string, string>(_english);
            if (Languages.TryGetValue(language, out var built)) foreach (var kv in built) words[kv.Key] = kv.Value;
            try
            {
                string own = Path.Combine(_overrideDir, language + ".json");
                if (File.Exists(own)) foreach (var kv in Parse(File.ReadAllText(own))) words[kv.Key] = kv.Value;
            }
            catch (Exception e) { Debug.LogWarning($"Translations: couldn't read the player's {language} file: {e.Message}"); }
            foreach (var kv in words) AddWord.Invoke(loc, new object[] { kv.Key, kv.Value });
            // texts already looked up (and cached as "[key]") are looked up again
            object cache = Cache?.GetValue(loc);
            cache?.GetType().GetMethod("EvictAll")?.Invoke(cache, null);
        }

        /// <summary>A flat JSON object of strings ({"key": "text", ...}); no library needed.</summary>
        private static Dictionary<string, string> Parse(string json)
        {
            var result = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(json, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                result[Unescape(m.Groups[1].Value)] = Unescape(m.Groups[2].Value);
            return result;
        }

        private static string Unescape(string s) => Regex.Replace(s, @"\\(u[0-9a-fA-F]{4}|.)", m =>
        {
            string e = m.Groups[1].Value;
            switch (e[0])
            {
                case 'n': return "\n";
                case 't': return "\t";
                case 'u': return ((char)Convert.ToInt32(e.Substring(1), 16)).ToString();
                default: return e;   // \" \\ \/
            }
        });
    }
}
