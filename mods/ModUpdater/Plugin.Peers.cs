using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace ModUpdater
{
    /// <summary>
    /// Shares "which mods/versions do I have" with the other players in the same world, using Valheim's own
    /// routed RPC messages (the server just relays them, so no server mod is needed).
    /// </summary>
    public partial class Plugin
    {
        private const string RpcVersions = "DHack_ModVersions";
        private const string RpcRequest = "DHack_ModRequest";

        /// <summary>mod guid -> version, for one player.</summary>
        private class PeerMods
        {
            public Dictionary<string, string> Versions = new Dictionary<string, string>();
            public Dictionary<string, string> Sources = new Dictionary<string, string>(); // guid -> where they got it (owner/repo), if they know
            public Dictionary<string, string> Names = new Dictionary<string, string>();
        }

        private readonly Dictionary<long, PeerMods> _peers = new Dictionary<long, PeerMods>();
        private ZRoutedRpc _rpcRegisteredOn;
        private bool _announcePending;

        /// <summary>Runs every frame: (re)registers the message handlers each time we join a world.</summary>
        private void UpdatePeers()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null) { _rpcRegisteredOn = null; return; }

            // After a hot reload the previous copy removes its handlers at the end of the frame; wait a moment so
            // we never register first and then get our own handlers removed.
            if (Time.frameCount <= _awakeFrame + 2) return;

            if (rpc != _rpcRegisteredOn)
            {
                _rpcRegisteredOn = rpc;
                _peers.Clear();
                UnregisterRpc(); // Valheim throws if a handler name is registered twice
                rpc.Register<string>(RpcVersions, OnVersionsReceived);
                rpc.Register(RpcRequest, OnVersionsRequested);
                _announcePending = true;
            }

            // Wait until we're actually in the world (our network id is set by then) before announcing ourselves.
            if (_announcePending && Player.m_localPlayer != null)
            {
                _announcePending = false;
                ScanLocal();
                BroadcastVersions();
            }
        }

        /// <summary>Valheim has no "unregister", so remove our handlers from its table directly (needed for hot reload).</summary>
        private static void UnregisterRpc()
        {
            if (ZRoutedRpc.instance == null) return;
            var table = AccessTools.Field(typeof(ZRoutedRpc), "m_functions").GetValue(ZRoutedRpc.instance) as System.Collections.IDictionary;
            if (table == null) return;
            table.Remove(RpcVersions.GetStableHashCode());
            table.Remove(RpcRequest.GetStableHashCode());
        }

        private static long MyId => ZDOMan.GetSessionID();

        private string BuildPayload()
        {
            var sb = new StringBuilder();
            foreach (LocalMod m in _local.Where(l => !l.Disabled))
            {
                Row row = _rows.FirstOrDefault(r => r.Local == m || (r.Local != null && r.Local.Guid == m.Guid));
                string source = row != null && row.Feed != null ? row.Feed.Spec : "";
                sb.Append(Clean(m.Guid)).Append('|').Append(Clean(m.Version)).Append('|').Append(Clean(source)).Append('|').Append(Clean(m.Name)).Append(';');
            }
            return sb.ToString();
        }

        private static string Clean(string s) => (s ?? "").Replace(';', ' ').Replace('|', ' ');

        /// <summary>"guid|version" (older managers) or "guid|version|source|name".</summary>
        private static PeerMods ParsePayload(string payload)
        {
            var result = new PeerMods();
            foreach (string part in (payload ?? "").Split(';'))
            {
                string[] f = part.Split('|');
                if (f.Length < 2 || f[0].Length == 0) continue;
                result.Versions[f[0]] = f[1];
                if (f.Length >= 4) { if (f[2].Length > 0) result.Sources[f[0]] = f[2]; if (f[3].Length > 0) result.Names[f[0]] = f[3]; }
            }
            return result;
        }

        /// <summary>Tell everyone in the world what we have.</summary>
        private void BroadcastVersions()
        {
            if (ZRoutedRpc.instance == null || Player.m_localPlayer == null) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcVersions, BuildPayload()); }
            catch (System.Exception e) { Logger.LogWarning("Could not share mod versions: " + e.Message); }
        }

        /// <summary>Ask everyone in the world to tell us what they have (used by Refresh).</summary>
        private void RequestPeerVersions()
        {
            if (ZRoutedRpc.instance == null || Player.m_localPlayer == null) return;
            try { ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcRequest); }
            catch (System.Exception e) { Logger.LogWarning("Could not ask other players for their mod versions: " + e.Message); }
        }

        private void OnVersionsReceived(long sender, string payload)
        {
            if (sender == MyId) return;
            bool firstTime = !_peers.ContainsKey(sender);
            _peers[sender] = ParsePayload(payload);

            // Someone new told us what they have: tell them what we have, directly (so it doesn't loop).
            if (firstTime && ZRoutedRpc.instance != null)
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcVersions, BuildPayload());
        }

        private void OnVersionsRequested(long sender)
        {
            if (sender == MyId || ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcVersions, BuildPayload());
        }

        /// <summary>Players currently in the world (excluding us): network id -> name.</summary>
        private Dictionary<long, string> OtherPlayers()
        {
            var result = new Dictionary<long, string>();
            if (ZNet.instance == null) return result;
            foreach (ZNet.PlayerInfo p in ZNet.instance.GetPlayerList())
            {
                long id = p.m_characterID.UserID;
                if (id != 0 && id != MyId) result[id] = p.m_name;
            }
            return result;
        }
    }
}
