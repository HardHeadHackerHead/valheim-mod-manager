using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace ModUpdater
{
    /// <summary>
    /// Cover images. A mod can ship a small picture (cover.png or cover.jpg in its folder; publish.ps1 puts it in dist and names it in
    /// manifest.json). The manager downloads each one once, keeps it on disk named after its file hash, and shows it on the mod's card.
    /// </summary>
    public partial class Plugin
    {
        private const long MaxCoverBytes = 1500 * 1024;

        private readonly Dictionary<string, Texture2D> _covers = new Dictionary<string, Texture2D>(); // mod guid -> picture
        private readonly Dictionary<string, string> _coverSha = new Dictionary<string, string>();     // mod guid -> hash it was loaded from
        private bool _coversBusy;

        private static string CoverDir => Path.Combine(Paths.CachePath, "ModUpdater", "covers");

        /// <summary>The cover file name from a manifest, or null if it is not a plain png/jpg file name.</summary>
        private static string CoverName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return null;
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" ? name : null;
        }

        private string CoverUrl(Feed feed, string file) =>
            feed.Primary && ActiveToken.Length > 0
                ? $"{feed.Api}/{file}?ref={feed.Branch}"   // a private repo needs the API and the token
                : $"https://raw.githubusercontent.com/{feed.Owner}/{feed.Repo}/{feed.Branch}/{feed.Folder}/{file}"; // public: no request limit

        /// <summary>Fetch whatever covers are missing or changed, one at a time. Pictures already on disk cost nothing.</summary>
        private IEnumerator LoadCovers()
        {
            if (_coversBusy) yield break;
            _coversBusy = true;
            foreach (RemoteMod mod in _remote.ToArray())
            {
                if (mod.cover == null) continue;
                string key = ShaKey(mod.Feed, mod.cover);
                if (!_remoteSha.TryGetValue(key, out string sha)) continue;
                if (_remoteSize.TryGetValue(key, out long size) && size > MaxCoverBytes) continue;
                if (_covers.ContainsKey(mod.guid) && _coverSha.TryGetValue(mod.guid, out string have) && have == sha) continue;

                string path = Path.Combine(CoverDir, sha + ".img");
                byte[] data = null;
                if (File.Exists(path)) { try { data = File.ReadAllBytes(path); } catch { data = null; } }
                if (data == null)
                {
                    string error = null;
                    bool useToken = mod.Feed.Primary && ActiveToken.Length > 0;
                    yield return Get(CoverUrl(mod.Feed, mod.cover), "application/vnd.github.raw+json", (t, b, e) => { data = b; error = e; }, useToken);
                    if (error != null || data == null || data.Length > MaxCoverBytes) continue;
                    try { Directory.CreateDirectory(CoverDir); File.WriteAllBytes(path, data); } catch { /* just not cached */ }
                }

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
                if (!LoadImage(texture, data) || texture.width * texture.height > 4096 * 4096 / 4) { Destroy(texture); continue; } // not a picture, or huge
                if (_covers.TryGetValue(mod.guid, out Texture2D old) && old != null) Destroy(old);
                _covers[mod.guid] = texture;
                _coverSha[mod.guid] = sha;
            }
            _coversBusy = false;
        }

        // Unity moved image loading into its own module, which does not compile against our references; call it by name instead.
        private static readonly System.Reflection.MethodInfo LoadImageMethod =
            Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule")?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });

        private static bool LoadImage(Texture2D texture, byte[] data)
        {
            try { return LoadImageMethod != null && (bool)LoadImageMethod.Invoke(null, new object[] { texture, data }); }
            catch { return false; }
        }

        private void DestroyCovers()
        {
            foreach (Texture2D t in _covers.Values) if (t != null) Destroy(t);
            _covers.Clear();
            _coverSha.Clear();
        }

        /// <summary>The picture on the left of a card: the cover if there is one, otherwise a tile with the mod's first letter.</summary>
        private void DrawCover(Row row)
        {
            const float w = 128f, h = 72f;
            Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
            if (Event.current.type != EventType.Repaint) return;

            string guid = row.Remote != null ? row.Remote.guid : row.Local != null ? row.Local.Guid : null;
            if (guid != null && _covers.TryGetValue(guid, out Texture2D texture) && texture != null)
            {
                GUI.DrawTexture(r, texture, ScaleMode.ScaleAndCrop);
                return;
            }

            // no picture: a tile in a colour taken from the name, so cards stay easy to tell apart
            int hue = (row.Name ?? "?").Aggregate(7, (acc, c) => acc * 31 + c) & 0x7fffffff;
            Color old = GUI.color;
            GUI.color = Color.HSVToRGB((hue % 360) / 360f, 0.35f, 0.38f);
            GUI.DrawTexture(r, _texWhite);
            GUI.color = old;
            GUI.Label(r, string.IsNullOrEmpty(row.Name) ? "?" : row.Name.Substring(0, 1).ToUpperInvariant(), _sTile);
        }
    }
}
