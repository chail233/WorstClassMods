using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FMODUnity;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace PlaygroundMusic
{
    /// <summary>
    /// 联机曲库协商：交换各端本地曲库的文件名 + SHA-256，
    /// 计算全员共有曲目交集；扩展曲目统一用广播柱的 CustomIndex 槽位，
    /// 具体播放哪首由房主经自定义消息广播校验值，各端从本地文件读取，
    /// 以 SessionTime 起点对齐播放进度。
    /// </summary>
    public struct MusicSelectionMessage : NetworkMessage
    {
        public byte Kind;       // 1=Offer 2=Ready 3=Unavailable
        public ulong Pillar;
        public uint Version;
        public string Hash;
        public double Started;
    }

    public struct MusicCatalogMessage : NetworkMessage
    {
        public int Offset;
        public int Total;
        public string[] Hashes;
        public string[] Titles;
    }

    public struct MusicAvailabilityMessage : NetworkMessage
    {
        public int Offset;
        public bool[] Available;
    }

    public struct MusicIntersectionMessage : NetworkMessage
    {
        public bool Complete;
        public int Count;
    }

    public sealed class MusicNetworkPump : MonoBehaviour
    {
        private void Update() { MusicSync.Tick(); }
        private void OnGUI() { MusicSync.DrawStatus(); }
        private void OnDestroy() { MusicSync.Clear(); }
    }

    internal static class MusicSync
    {
        private const byte Offer = 1, Ready = 2, Unavailable = 3;
        private const int BatchSize = 16;
        private const int MaxFileSize = 64 * 1024 * 1024;

        private sealed class Track
        {
            internal string Path, Hash, Title;
            internal int Length;
            internal long Modified;
        }

        private sealed class CatalogEntry
        {
            internal string Hash, Title;
        }

        private sealed class PeerCatalog
        {
            internal NetworkConnectionToClient Connection;
            internal int Sent, Received;
            internal bool ManifestSent, Complete;
            internal readonly HashSet<string> Available = new HashSet<string>();
            internal readonly List<string> Missing = new List<string>();
        }

        private sealed class SongState
        {
            internal MusicSelectionMessage Message;
            internal string Path;
            internal bool Replied;
            internal double Deadline;
            internal readonly Dictionary<int, NetworkConnectionToClient> ReadyPeers = new Dictionary<int, NetworkConnectionToClient>();
        }

        private static volatile Track[] catalog = new Track[0];
        private static volatile bool catalogReady;
        private static readonly Dictionary<int, PeerCatalog> peers = new Dictionary<int, PeerCatalog>();
        private static readonly Dictionary<ulong, SongState> states = new Dictionary<ulong, SongState>();
        private static readonly List<CatalogEntry> hostCatalog = new List<CatalogEntry>();
        private static readonly List<string> clientMissing = new List<string>();
        private static int[] sharedIndices = new int[0];
        private static bool sharedReady, intersectionDirty = true;
        private static int revision;
        private static NetworkConnection clientConnection;
        private static int hostTotal = -1, availabilitySent, clientSharedCount;
        private static bool clientComplete, clientReplied;
        private static bool reportOpen;
        private static Vector2 reportScroll;
        private static string report = "正在检测曲库";

        internal static int HostRevision => revision;

        // 交集就绪时返回共有曲目数，否则 0（随机池暂时只含原版+自定义槽位）
        internal static int HostTrackCount
        {
            get
            {
                if (!catalogReady || !sharedReady) return 0;
                foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
                {
                    PeerCatalog peer;
                    if (Remote(connection) && (!peers.TryGetValue(connection.connectionId, out peer) ||
                        peer.Connection != connection || !peer.Complete)) return 0;
                }
                return sharedIndices.Length;
            }
        }

        internal static int GetHostTrackIndex(int position)
        {
            return position >= 0 && position < sharedIndices.Length ? sharedIndices[position] : -1;
        }

        internal static void Install(Harmony harmony, string[] paths)
        {
            // 自定义消息的手工序列化（消息结构不在 Mirror Weaver 的已知集合里）
            Writer<MusicSelectionMessage>.write = WriteSelection;
            Reader<MusicSelectionMessage>.read = ReadSelection;
            Writer<MusicCatalogMessage>.write = WriteCatalog;
            Reader<MusicCatalogMessage>.read = ReadCatalog;
            Writer<MusicAvailabilityMessage>.write = WriteAvailability;
            Reader<MusicAvailabilityMessage>.read = ReadAvailability;
            Writer<MusicIntersectionMessage>.write = WriteIntersection;
            Reader<MusicIntersectionMessage>.read = ReadIntersection;
            // 挂到游戏自己的消息注册时机之后，保证在连接建立前注册完成
            harmony.Patch(AccessTools.Method(typeof(RoomManager), "RegisterClientMessageHandlers"), postfix: new HarmonyMethod(typeof(MusicSync), "RegisterClient"));
            harmony.Patch(AccessTools.Method(typeof(RoomManager), "RegisterServerMessageHandlers"), postfix: new HarmonyMethod(typeof(MusicSync), "RegisterServer"));
            RegisterClient();
            RegisterServer();
            var pump = new GameObject("操场音乐曲库检测");
            UnityEngine.Object.DontDestroyOnLoad(pump);
            pump.AddComponent<MusicNetworkPump>();
            // 后台建本地曲库索引（SHA-256 校验值）
            Task.Run(delegate
            {
                var tracks = new List<Track>();
                foreach (string path in paths)
                {
                    try
                    {
                        var info = new FileInfo(path);
                        long length = info.Length;
                        long modified = info.LastWriteTimeUtc.Ticks;
                        if (length <= 0 || length > MaxFileSize) { ModEntry.Log("曲目大小不受支持: " + Path.GetFileName(path)); continue; }
                        string hash;
                        using (var input = File.OpenRead(path))
                        using (var digest = SHA256.Create()) hash = BitConverter.ToString(digest.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                        info.Refresh();
                        if (info.Length != length || info.LastWriteTimeUtc.Ticks != modified)
                        { ModEntry.Log("索引期间曲目被修改: " + Path.GetFileName(path)); continue; }
                        string title = Path.GetFileName(path);
                        tracks.Add(new Track { Path = path, Hash = hash, Title = title, Length = (int)length, Modified = modified });
                    }
                    catch (Exception error) { ModEntry.Log("曲目不可用: " + error.Message); }
                }
                catalog = tracks.ToArray();
                catalogReady = true;
                ModEntry.Log("[状态] 本地曲库索引完成: " + catalog.Length + " 首");
            });
        }

        public static void RegisterClient()
        {
            NetworkClient.RegisterHandler<MusicSelectionMessage>(OnSelection, true);
            NetworkClient.RegisterHandler<MusicCatalogMessage>(OnCatalog, true);
            NetworkClient.RegisterHandler<MusicIntersectionMessage>(OnIntersection, true);
        }

        public static void RegisterServer()
        {
            NetworkServer.RegisterHandler<MusicSelectionMessage>(OnServerSelection, true);
            NetworkServer.RegisterHandler<MusicAvailabilityMessage>(OnAvailability, true);
        }

        private static bool Remote(NetworkConnectionToClient connection)
        {
            return connection != null && connection != NetworkServer.localConnection && connection.isAuthenticated;
        }

        private static void EnsureClientConnection()
        {
            if (clientConnection == NetworkClient.connection) return;
            Clear();
            clientConnection = NetworkClient.connection;
        }

        internal static void Tick()
        {
            if (!NetworkClient.isConnected && !NetworkServer.active) { Clear(); return; }
            if (NetworkServer.active) TickServer();
            else
            {
                EnsureClientConnection();
                ReplyToCatalog();
                foreach (SongState state in states.Values) ReplyToSong(state);
            }
        }

        private static void TickServer()
        {
            var disconnected = new List<int>();
            foreach (var pair in peers)
            {
                NetworkConnectionToClient current;
                if (!NetworkServer.connections.TryGetValue(pair.Key, out current) || current != pair.Value.Connection || !Remote(current))
                    disconnected.Add(pair.Key);
            }
            foreach (int id in disconnected) { peers.Remove(id); intersectionDirty = true; }
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                if (!Remote(connection)) continue;
                PeerCatalog peer;
                if (!peers.TryGetValue(connection.connectionId, out peer))
                {
                    peer = new PeerCatalog { Connection = connection };
                    peers.Add(connection.connectionId, peer);
                    intersectionDirty = true;
                    foreach (SongState song in states.Values) connection.Send(song.Message, 0);
                }
                if (catalogReady && !peer.ManifestSent) SendCatalogBatch(peer);
            }
            if (catalogReady && intersectionDirty) RefreshIntersection();
            var expired = new List<MusicSelectionMessage>();
            foreach (SongState state in states.Values)
            {
                TryStart(state);
                if (state.Message.Started <= 0d && Time.realtimeSinceStartupAsDouble > state.Deadline) expired.Add(state.Message);
            }
            foreach (MusicSelectionMessage message in expired) ModEntry.FallbackSong(message.Pillar, message.Version);
        }

        private static void SendCatalogBatch(PeerCatalog peer)
        {
            int count = Math.Min(BatchSize, catalog.Length - peer.Sent);
            var hashes = new string[count];
            var titles = new string[count];
            for (int i = 0; i < count; i++)
            {
                Track track = catalog[peer.Sent + i];
                hashes[i] = track.Hash;
                titles[i] = track.Title;
            }
            peer.Connection.Send(new MusicCatalogMessage { Offset = peer.Sent, Total = catalog.Length, Hashes = hashes, Titles = titles }, 0);
            peer.Sent += count;
            peer.ManifestSent = peer.Sent == catalog.Length;
        }

        private static void OnCatalog(MusicCatalogMessage message)
        {
            if (NetworkServer.active) return;
            EnsureClientConnection();
            if (message.Offset != hostCatalog.Count || message.Total < message.Offset ||
                (message.Hashes.Length == 0 && message.Total != 0) ||
                message.Hashes.Length > message.Total - message.Offset || (hostTotal >= 0 && hostTotal != message.Total)) return;
            hostTotal = message.Total;
            for (int i = 0; i < message.Hashes.Length; i++)
                hostCatalog.Add(new CatalogEntry { Hash = message.Hashes[i], Title = message.Titles[i] });
            ReplyToCatalog();
        }

        private static void ReplyToCatalog()
        {
            if (!catalogReady || hostTotal < 0 || clientReplied) return;
            int count = Math.Min(BatchSize, hostCatalog.Count - availabilitySent);
            if (count == 0 && hostTotal != 0) return;
            var available = new bool[count];
            for (int i = 0; i < count; i++)
            {
                CatalogEntry entry = hostCatalog[availabilitySent + i];
                available[i] = FindLocal(entry.Hash) != null;
                if (!available[i]) clientMissing.Add(entry.Title);
            }
            NetworkClient.Send(new MusicAvailabilityMessage { Offset = availabilitySent, Available = available }, 0);
            availabilitySent += count;
            clientReplied = availabilitySent == hostTotal;
            UpdateClientReport();
        }

        private static void OnAvailability(NetworkConnectionToClient connection, MusicAvailabilityMessage message)
        {
            PeerCatalog peer;
            if (!Remote(connection) || !peers.TryGetValue(connection.connectionId, out peer) || peer.Connection != connection ||
                peer.Complete || message.Offset != peer.Received || message.Available.Length > peer.Sent - peer.Received ||
                (message.Available.Length == 0 && catalog.Length != 0)) return;
            for (int i = 0; i < message.Available.Length; i++)
            {
                Track track = catalog[peer.Received + i];
                if (message.Available[i]) peer.Available.Add(track.Hash);
                else peer.Missing.Add(track.Title);
            }
            peer.Received += message.Available.Length;
            peer.Complete = peer.ManifestSent && peer.Received == catalog.Length;
            if (peer.Complete) intersectionDirty = true;
            else UpdateHostReport();
        }

        private static void RefreshIntersection()
        {
            bool complete = true;
            var availability = new List<HashSet<string>>();
            foreach (PeerCatalog peer in peers.Values)
            {
                complete &= peer.Complete;
                availability.Add(peer.Available);
            }
            var hashes = new string[catalog.Length];
            for (int i = 0; i < hashes.Length; i++) hashes[i] = catalog[i].Hash;
            sharedIndices = complete ? CalculateIntersection(hashes, availability) : new int[0];
            sharedReady = complete;
            revision++;
            intersectionDirty = false;
            var result = new MusicIntersectionMessage { Complete = complete, Count = sharedIndices.Length };
            foreach (PeerCatalog peer in peers.Values) peer.Connection.Send(result, 0);
            UpdateHostReport();
            if (complete)
            {
                ModEntry.Log("[状态] 全员共有曲库就绪: " + sharedIndices.Length + "/" + catalog.Length + " 首本地曲目");
                // 交集变化后，正在播但已不共有的曲目要回退
                var unavailable = new List<MusicSelectionMessage>();
                foreach (SongState state in states.Values)
                    if (!SharedHash(state.Message.Hash)) unavailable.Add(state.Message);
                foreach (MusicSelectionMessage message in unavailable) ModEntry.FallbackSong(message.Pillar, message.Version);
            }
        }

        private static int[] CalculateIntersection(string[] hashes, IEnumerable<HashSet<string>> availability)
        {
            var result = new List<int>();
            for (int i = 0; i < hashes.Length; i++)
            {
                bool shared = true;
                foreach (HashSet<string> files in availability)
                    if (!files.Contains(hashes[i])) { shared = false; break; }
                if (shared) result.Add(i);
            }
            return result.ToArray();
        }

        private static bool SharedHash(string hash)
        {
            foreach (int index in sharedIndices) if (catalog[index].Hash == hash) return true;
            return false;
        }

        private static void OnIntersection(MusicIntersectionMessage message)
        {
            if (NetworkServer.active || message.Count < 0) return;
            EnsureClientConnection();
            clientComplete = message.Complete;
            clientSharedCount = message.Count;
            UpdateClientReport();
        }

        // 房主侧：为广播柱确定共享曲目，等全员 Ready 后给出统一起点
        internal static string Resolve(PlaygroundBroadcastComponent owner, uint version, int hostSelection, out double started)
        {
            started = 0d;
            ulong uid = owner.owner.UID;
            SongState state;
            if (NetworkServer.active && (!states.TryGetValue(uid, out state) || state.Message.Version != version))
            {
                if (hostSelection < 0 || hostSelection >= catalog.Length || !SharedHash(catalog[hostSelection].Hash) || !Unchanged(catalog[hostSelection]))
                { ModEntry.FallbackSong(uid, version); return null; }
                Track track = catalog[hostSelection];
                state = new SongState
                {
                    Path = track.Path,
                    Deadline = Time.realtimeSinceStartupAsDouble + 10d,
                    Message = new MusicSelectionMessage { Kind = Offer, Pillar = uid, Version = version, Hash = track.Hash }
                };
                states[uid] = state;
                foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
                    if (Remote(connection)) connection.Send(state.Message, 0);
                TryStart(state);
            }
            if (!states.TryGetValue(uid, out state) || state.Message.Version != version) return null;
            started = state.Message.Started;
            return started > 0d && NetRuntime.SessionTime >= started ? state.Path : null;
        }

        private static void TryStart(SongState state)
        {
            if (state.Message.Started > 0d) return;
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
            {
                NetworkConnectionToClient ready;
                if (Remote(connection) && (!state.ReadyPeers.TryGetValue(connection.connectionId, out ready) || ready != connection)) return;
            }
            state.Message.Started = NetRuntime.SessionTime + 0.5d;
            foreach (NetworkConnectionToClient connection in NetworkServer.connections.Values)
                if (Remote(connection)) connection.Send(state.Message, 0);
        }

        private static void OnSelection(MusicSelectionMessage message)
        {
            if (NetworkServer.active || message.Kind != Offer || !ValidHash(message.Hash)) return;
            EnsureClientConnection();
            SongState state;
            if (states.TryGetValue(message.Pillar, out state))
            {
                if (unchecked((int)(state.Message.Version - message.Version)) > 0) return;
                if (state.Message.Version == message.Version)
                {
                    if (state.Message.Hash == message.Hash) state.Message = message;
                    return;
                }
            }
            state = new SongState { Message = message };
            states[message.Pillar] = state;
            ReplyToSong(state);
        }

        private static void ReplyToSong(SongState state)
        {
            if (!catalogReady || state.Replied) return;
            state.Path = FindLocal(state.Message.Hash);
            state.Replied = true;
            MusicSelectionMessage response = state.Message;
            response.Kind = state.Path == null ? Unavailable : Ready;
            NetworkClient.Send(response, 0);
        }

        private static void OnServerSelection(NetworkConnectionToClient connection, MusicSelectionMessage message)
        {
            SongState state;
            if (!Remote(connection) || !states.TryGetValue(message.Pillar, out state) ||
                state.Message.Version != message.Version || state.Message.Hash != message.Hash) return;
            if (message.Kind == Ready)
            {
                state.ReadyPeers[connection.connectionId] = connection;
                TryStart(state);
            }
            else if (message.Kind == Unavailable)
            {
                PeerCatalog peer;
                if (peers.TryGetValue(connection.connectionId, out peer))
                {
                    peer.Available.Remove(message.Hash);
                    foreach (Track track in catalog)
                        if (track.Hash == message.Hash && !peer.Missing.Contains(track.Title)) peer.Missing.Add(track.Title);
                    intersectionDirty = true;
                }
                ModEntry.FallbackSong(message.Pillar, message.Version);
            }
        }

        private static string FindLocal(string hash)
        {
            foreach (Track track in catalog) if (track.Hash == hash && Unchanged(track)) return track.Path;
            return null;
        }

        private static bool Unchanged(Track track)
        {
            try
            {
                var info = new FileInfo(track.Path);
                return info.Exists && info.Length == track.Length && info.LastWriteTimeUtc.Ticks == track.Modified;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // ===== 曲库检测报告（OnGUI） =====

        private static string PlayerName(NetworkConnectionToClient connection)
        {
            if (RoomManager.IsSingletonActive() && UnitManager.IsSingletonActive())
            {
                ulong uid = RoomManager.Instance.GetPlayerUIDByConnection(connection);
                Unit unit = UnitManager.Instance.GetUnitByUID(uid);
                if (unit != null) return unit.GetPlayerUnitName();
            }
            return "客机 " + connection.connectionId;
        }

        private static void UpdateHostReport()
        {
            var text = new StringBuilder();
            text.AppendLine(sharedReady ? "全员共有扩展曲目 " + sharedIndices.Length + " 首" : "正在检测各玩家曲库");
            text.AppendLine("房主曲目 " + catalog.Length + " 首");
            foreach (PeerCatalog peer in peers.Values)
            {
                text.AppendLine();
                text.AppendLine(PlayerName(peer.Connection) + (peer.Complete ? " 缺少 " + peer.Missing.Count + " 首" : " 正在检测"));
                foreach (string title in peer.Missing) text.AppendLine("  " + title);
            }
            report = text.ToString();
        }

        private static void UpdateClientReport()
        {
            var text = new StringBuilder();
            text.AppendLine(clientComplete ? "全员共有扩展曲目 " + clientSharedCount + " 首" : "正在检测各玩家曲库");
            text.AppendLine("本机已检测 " + availabilitySent + "/" + Math.Max(0, hostTotal) + " 首，缺少 " + clientMissing.Count + " 首");
            foreach (string title in clientMissing) text.AppendLine("  " + title);
            report = text.ToString();
        }

        internal static void DrawStatus()
        {
            if (!NetworkClient.isConnected && !NetworkServer.active) return;
            if (GUI.Button(new Rect(12, 12, 120, 30), "曲库检测")) reportOpen = !reportOpen;
            if (!reportOpen) return;
            float width = Math.Min(620f, Screen.width - 32f);
            float height = Math.Min(440f, Screen.height - 68f);
            var area = new Rect(12f, 48f, width, height);
            GUI.Box(area, "");
            GUILayout.BeginArea(new Rect(area.x + 10f, area.y + 8f, width - 20f, height - 16f));
            reportScroll = GUILayout.BeginScrollView(reportScroll);
            GUILayout.Label(report);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        internal static void Cancel(ulong pillar) { states.Remove(pillar); }

        internal static void Clear()
        {
            if (peers.Count == 0 && states.Count == 0 && clientConnection == null && !sharedReady && hostTotal < 0) return;
            peers.Clear();
            states.Clear();
            hostCatalog.Clear();
            clientMissing.Clear();
            sharedIndices = new int[0];
            sharedReady = false;
            intersectionDirty = true;
            revision++;
            clientConnection = null;
            hostTotal = -1;
            availabilitySent = 0;
            clientComplete = false;
            clientReplied = false;
            clientSharedCount = 0;
            report = "正在检测曲库";
            ModEntry.ClearSession();
        }

        internal static void PlaybackFailed(ulong pillar, uint version)
        {
            if (NetworkServer.active) { ModEntry.FallbackSong(pillar, version); return; }
            SongState state;
            if (!NetworkClient.isConnected || !states.TryGetValue(pillar, out state) || state.Message.Version != version) return;
            MusicSelectionMessage response = state.Message;
            response.Kind = Unavailable;
            NetworkClient.Send(response, 0);
        }

        private static bool ValidHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char character in value)
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
            return true;
        }

        // ===== Mirror 手工序列化 =====

        private static void WriteSelection(NetworkWriter writer, MusicSelectionMessage message)
        {
            writer.WriteByte(message.Kind);
            writer.WriteULong(message.Pillar);
            writer.WriteUInt(message.Version);
            writer.WriteString(message.Hash);
            writer.WriteDouble(message.Started);
        }

        private static MusicSelectionMessage ReadSelection(NetworkReader reader)
        {
            return new MusicSelectionMessage
            {
                Kind = reader.ReadByte(),
                Pillar = reader.ReadULong(),
                Version = reader.ReadUInt(),
                Hash = reader.ReadString(),
                Started = reader.ReadDouble()
            };
        }

        private static void WriteCatalog(NetworkWriter writer, MusicCatalogMessage message)
        {
            writer.WriteInt(message.Offset);
            writer.WriteInt(message.Total);
            writer.WriteInt(message.Hashes.Length);
            for (int i = 0; i < message.Hashes.Length; i++) { writer.WriteString(message.Hashes[i]); writer.WriteString(message.Titles[i]); }
        }

        private static MusicCatalogMessage ReadCatalog(NetworkReader reader)
        {
            int offset = reader.ReadInt(), total = reader.ReadInt(), count = reader.ReadInt();
            if (offset < 0 || total < offset || count < 0 || count > BatchSize || count > total - offset) throw new InvalidDataException("Invalid music catalog batch");
            var hashes = new string[count];
            var titles = new string[count];
            for (int i = 0; i < count; i++)
            {
                hashes[i] = reader.ReadString();
                titles[i] = reader.ReadString();
                if (!ValidHash(hashes[i]) || titles[i] == null || titles[i].Length > 256) throw new InvalidDataException("Invalid song metadata");
            }
            return new MusicCatalogMessage { Offset = offset, Total = total, Hashes = hashes, Titles = titles };
        }

        private static void WriteAvailability(NetworkWriter writer, MusicAvailabilityMessage message)
        {
            writer.WriteInt(message.Offset);
            writer.WriteInt(message.Available.Length);
            foreach (bool value in message.Available) writer.WriteBool(value);
        }

        private static MusicAvailabilityMessage ReadAvailability(NetworkReader reader)
        {
            int offset = reader.ReadInt(), count = reader.ReadInt();
            if (offset < 0 || count < 0 || count > BatchSize) throw new InvalidDataException("Invalid music availability batch");
            var available = new bool[count];
            for (int i = 0; i < count; i++) available[i] = reader.ReadBool();
            return new MusicAvailabilityMessage { Offset = offset, Available = available };
        }

        private static void WriteIntersection(NetworkWriter writer, MusicIntersectionMessage message)
        {
            writer.WriteBool(message.Complete);
            writer.WriteInt(message.Count);
        }

        private static MusicIntersectionMessage ReadIntersection(NetworkReader reader)
        {
            return new MusicIntersectionMessage { Complete = reader.ReadBool(), Count = reader.ReadInt() };
        }
    }
}
