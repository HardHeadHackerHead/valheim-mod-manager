using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace ModUpdater
{
    /// <summary>
    /// Reload individual mods without touching the others.
    /// ScriptEngine only offers "reload everything", but its internals can load a single DLL, and this
    /// mimics exactly what its own full reload does, one mod at a time: destroy the old copy, then load the
    /// DLL (the new copy appears a frame later). If anything about ScriptEngine looks different, we fall back
    /// to its normal full reload.
    /// </summary>
    public partial class Plugin
    {
        /// <summary>One mod to (re)load: its plugin GUID and the DLL in BepInEx\scripts.</summary>
        private class ModFile { public string Guid, Path; }

        // ---- ScriptEngine plumbing -------------------------------------------------------------

        private bool TryScriptEngine(out BaseUnityPlugin engine, out GameObject manager, out MethodInfo loadDll)
        {
            engine = null; manager = null; loadDll = null;
            if (!Chainloader.PluginInfos.TryGetValue(ScriptEngineGuid, out var info) || info.Instance == null) return false;

            engine = info.Instance;
            manager = AccessTools.Field(engine.GetType(), "scriptManager")?.GetValue(engine) as GameObject;
            loadDll = AccessTools.Method(engine.GetType(), "LoadDLL", new[] { typeof(string), typeof(GameObject) });
            return manager != null && loadDll != null;
        }

        /// <summary>Destroy the running copy of a script mod (if any). False if it isn't one ScriptEngine manages.</summary>
        private bool UnloadScriptMod(string guid, GameObject manager)
        {
            if (Chainloader.PluginInfos.TryGetValue(guid, out var info))
            {
                BaseUnityPlugin instance = info.Instance;
                if (instance != null && instance.gameObject != manager) return false; // a normal plugin from the plugins folder: not ours to touch
                Chainloader.PluginInfos.Remove(guid); // ScriptEngine refuses to load a GUID that's still registered
                if (instance != null) Destroy(instance);
            }
            return true;
        }

        /// <summary>Unload and reload just these mods. This copy of the manager goes last if it's in the list.</summary>
        private bool ReloadMods(List<ModFile> mods)
        {
            if (mods.Count == 0) return true;
            try
            {
                if (!TryScriptEngine(out var engine, out var manager, out var loadDll)) return ReloadScripts();

                foreach (ModFile mod in mods.OrderBy(m => m.Guid == Guid ? 1 : 0))
                {
                    if (!File.Exists(mod.Path)) continue;
                    if (!UnloadScriptMod(mod.Guid, manager)) return ReloadScripts();
                    MarkLoaded(mod.Path);
                    loadDll.Invoke(engine, new object[] { mod.Path, manager });
                }
                return true;
            }
            catch (Exception e)
            {
                Logger.LogWarning("Per-mod reload failed, doing a full reload instead: " + (e.InnerException ?? e).Message);
                return ReloadScripts();
            }
        }

        /// <summary>Unload (not reload) these mods, e.g. when the user disables one.</summary>
        private bool UnloadMods(List<ModFile> mods)
        {
            try
            {
                if (!TryScriptEngine(out _, out var manager, out _)) return ReloadScripts();
                foreach (ModFile mod in mods)
                    if (!UnloadScriptMod(mod.Guid, manager)) return ReloadScripts();
                return true;
            }
            catch (Exception e)
            {
                Logger.LogWarning("Unloading failed, doing a full reload instead: " + e.Message);
                return ReloadScripts();
            }
        }

        private ModFile FileOf(RemoteMod mod)
        {
            string dll = (mod.files ?? new string[0]).FirstOrDefault(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            return dll == null ? null : new ModFile { Guid = mod.guid, Path = Path.Combine(_scriptsDir, dll) };
        }

        // ---- noticing rebuilt mods ---------------------------------------------------------------

        private class Watch { public DateTime Stamp, Candidate; public float Since; }

        private readonly Dictionary<string, Watch> _watch = new Dictionary<string, Watch>();
        private float _nextWatch;

        /// <summary>The time a mod was last built: the newer of its .dll and .pdb (they're copied one after the other).</summary>
        private static DateTime StampOf(string dll)
        {
            DateTime t = File.GetLastWriteTimeUtc(dll);
            string pdb = System.IO.Path.ChangeExtension(dll, ".pdb");
            if (File.Exists(pdb)) { DateTime p = File.GetLastWriteTimeUtc(pdb); if (p > t) t = p; }
            return t;
        }

        /// <summary>Remember that this DLL, as it is on disk right now, is what's loaded.</summary>
        private void MarkLoaded(string dll)
        {
            DateTime now = StampOf(dll);
            _watch[dll] = new Watch { Stamp = now, Candidate = now };
        }

        private void BaselineWatch()
        {
            foreach (string dll in Directory.GetFiles(_scriptsDir, "*.dll")) MarkLoaded(dll);
        }

        /// <summary>
        /// Developer Mode: when a mod's DLL changes on disk (you rebuilt it), reload just that mod once the file
        /// has stopped changing, so we never read a half-copied build.
        /// </summary>
        private void WatchScripts()
        {
            if (Time.realtimeSinceStartup < _nextWatch) return;
            _nextWatch = Time.realtimeSinceStartup + 1f;
            if (!_autoReload.Value || !_developerMode.Value || _busy) return;

            var changed = new List<ModFile>();
            foreach (string dll in Directory.GetFiles(_scriptsDir, "*.dll"))
            {
                DateTime now = StampOf(dll);
                if (!_watch.TryGetValue(dll, out Watch w))
                    _watch[dll] = w = new Watch { Stamp = DateTime.MinValue, Candidate = DateTime.MinValue }; // a brand-new mod

                if (now == w.Stamp) { w.Candidate = now; continue; }                                         // nothing new
                if (now != w.Candidate) { w.Candidate = now; w.Since = Time.realtimeSinceStartup; continue; } // still being written
                if (Time.realtimeSinceStartup - w.Since < 1.5f) continue;                                    // wait for it to settle

                LocalMod mod = ReadPlugin(dll);
                w.Stamp = now;
                if (mod != null) changed.Add(new ModFile { Guid = mod.Guid, Path = dll });
            }

            if (changed.Count == 0) return;

            ScanLocal();
            BuildRows();
            string names = string.Join(", ", changed.Select(m => System.IO.Path.GetFileNameWithoutExtension(m.Path)));
            _statusLine = $"Reloaded {names} (rebuilt)";
            Say(_statusLine);
            ReloadMods(changed); // last: this may replace the running copy of the manager
        }

        private void ReloadOne(Row row)
        {
            if (row.Local == null || row.Local.InPlugins || row.Local.Disabled) return;
            ScanLocal();
            BuildRows();
            _statusLine = $"Reloaded {row.Name}";
            Say(_statusLine);
            ReloadMods(new List<ModFile> { new ModFile { Guid = row.Local.Guid, Path = row.Local.Path } });
        }
    }
}
