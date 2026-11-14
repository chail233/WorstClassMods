using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FMOD;
using FMODUnity;
using HarmonyLib;
using Mirror;
using WorstClassEver.ModAPI;
using UnityEngine;

namespace PlaygroundMusic
{
    /// <summary>
    /// 操场音乐扩展：扩展操场广播柱曲库。
    /// 原版 5 首 FMOD 事件之外，增加一个自定义曲目槽位（CustomIndex），
    /// 用 FMOD CoreSystem 低级 API 直接播放本地 MP3，联机时通过 SHA-256
    /// 曲库协商保证全员播放同一首（见 MusicSync）。
    /// </summary>
    public sealed class ModEntry : IMod
    {
        internal static string DirectoryPath;
        internal static Action<string> Log;

        // 广播柱私有成员的反射句柄
        internal static FieldInfo Sequence = AccessTools.Field(typeof(PlaygroundBroadcastComponent), "MusicSequence");
        internal static FieldInfo Index = AccessTools.Field(typeof(PlaygroundBroadcastComponent), "musicIndex");
        internal static FieldInfo Bound = AccessTools.Field(typeof(PlaygroundBroadcastComponent), "bound");
        private static readonly FieldInfo Version = AccessTools.Field(typeof(PlaygroundBroadcastComponent), "version");
        private static readonly FieldInfo TrickElapsed = AccessTools.Field(typeof(PlaygroundBroadcastComponent), "trickElapsed");
        private static readonly MethodInfo PlayCurrentMusicOriginal = AccessTools.Method(typeof(PlaygroundBroadcastComponent), "PlayCurrentMusic");
        private static readonly MethodInfo CreateStateOriginal = AccessTools.Method(typeof(PlaygroundBroadcastComponent), "CreateState");
        private static readonly MethodInfo StopOriginal = AccessTools.Method(typeof(PlaygroundBroadcastComponent), "StopMusic");

        internal static int OriginalCount;
        internal static int CustomIndex => OriginalCount;

        // FMOD 低级播放状态（同一时刻只有一个广播柱在放自定义曲目）
        internal static Sound sound;
        internal static Channel channel;
        internal static PlaygroundBroadcastComponent playingOwner;
        internal static int playingIndex = -1;
        internal static bool hasSound;
        internal static bool hasChannel;
        private static bool scoreSettingsLogged;
        private static uint playingVersion;
        private static PlaygroundBroadcastComponent completedOwner;
        private static uint completedVersion;
        private static PlaygroundBroadcastComponent waitingOwner;
        private static uint waitingVersion;

        // 每根广播柱的选歌状态袋（不重复洗牌池）
        private sealed class MusicBag
        {
            internal List<int> Remaining = new List<int>();
            internal int Count, Revision = -1, Last = -1, Custom = -1;
        }

        private static readonly Dictionary<ulong, MusicBag> bags = new Dictionary<ulong, MusicBag>();

        public void OnLoad(ModContext context)
        {
            DirectoryPath = context.ModDirectory;
            Log = context.Log;

            // 曲目来源：Mod 自带目录 + 玩家本地目录（每-Mod 配置目录）
            var paths = new List<string>();
            paths.AddRange(FindTracks(Path.Combine(DirectoryPath, "Music")));
            int bundledCount = paths.Count;
            string userDirectory = Path.Combine(context.ConfigDirectory, "Music");
            try
            {
                Directory.CreateDirectory(userDirectory);
                paths.AddRange(FindTracks(userDirectory));
            }
            catch (Exception error) { Log("无法读取本地曲目目录: " + error.Message); }

            // 扩展曲库数组：原版 N 首 + 1 个自定义槽位（假枚举值，FMOD Studio 里不存在）
            var old = (AudioEvents[])Sequence.GetValue(null);
            OriginalCount = old.Length;
            var extended = new AudioEvents[CustomIndex + 1];
            Array.Copy(old, extended, OriginalCount);
            for (int i = OriginalCount; i < extended.Length; i++) extended[i] = (AudioEvents)(0xF0000000u + (uint)i);

            var harmony = new Harmony("worstclass.playgroundmusic");
            try
            {
                Sequence.SetValue(null, extended);
                harmony.Patch(AccessTools.Method(typeof(PlaygroundBroadcastComponent), "PlayCurrentMusic"), prefix: new HarmonyMethod(typeof(ModEntry), "BeforePlay"));
                harmony.Patch(AccessTools.Method(typeof(PlaygroundBroadcastComponent), "StopMusic"), prefix: new HarmonyMethod(typeof(ModEntry), "BeforeStop"));
                harmony.Patch(AccessTools.Method(typeof(PlaygroundBroadcastComponent), "OnUpdate"), postfix: new HarmonyMethod(typeof(ModEntry), "AfterUpdate"));
                harmony.Patch(AccessTools.Method(typeof(PlaygroundBroadcastComponent), "CompleteSwitch"), prefix: new HarmonyMethod(typeof(ModEntry), "ResetScoreClock"));
                harmony.Patch(AccessTools.Method(typeof(PlaygroundBroadcastComponent), "UpdateTrickCount"), prefix: new HarmonyMethod(typeof(ModEntry), "UpdateScore"));
                harmony.Patch(AccessTools.Method(typeof(PlaygroundBroadcastComponent), "SelectNextTrickMusic"), prefix: new HarmonyMethod(typeof(ModEntry), "SelectMusic"));
                MusicSync.Install(harmony, paths.ToArray());
            }
            catch
            {
                // 装补丁失败要完整回滚，避免半残状态
                harmony.UnpatchAll("worstclass.playgroundmusic");
                Sequence.SetValue(null, old);
                throw;
            }
            Log($"[状态] 操场音乐扩展已加载：Mod 自带曲目 {bundledCount} 首，本地曲目 {paths.Count - bundledCount} 首，原版曲目 {OriginalCount} 首");
            Log("[状态] 本地曲目目录: " + userDirectory);
        }

        private static string[] FindTracks(string folder)
        {
            if (!Directory.Exists(folder)) return new string[0];
            string[] files = Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.Ordinal);
            var tracks = new List<string>();
            foreach (string file in files)
            {
                if (!string.Equals(Path.GetExtension(file), ".mp3", StringComparison.OrdinalIgnoreCase)) continue;
                tracks.Add(file);
            }
            return tracks.ToArray();
        }

        // ===== 广播计分（扩展曲目也要计分） =====

        public static void ResetScoreClock(ref float ___trickElapsed) { ___trickElapsed = 0f; }

        public static bool UpdateScore(PlaygroundBroadcastComponent __instance, float __0, byte ___musicIndex, ulong ___lastStudentUID, ref float ___trickElapsed)
        {
            if (!NetRuntime.IsServer || !ClassroomManager.IsSingletonActive() ||
                ClassroomManager.Instance.curState != EClassroomState.Classing || ___musicIndex == 0 || ___lastStudentUID == 0)
            { ___trickElapsed = 0f; return false; }
            // 扩展曲目只有在实际出声时才计分
            if (___musicIndex == CustomIndex && (playingOwner != __instance || !hasChannel))
            { ___trickElapsed = 0f; return false; }
            if (!scoreSettingsLogged)
            {
                scoreSettingsLogged = true;
                Log("[状态] 广播计分间隔=" + ConfigManager.GameSetting.PlaygroundMusicScoreTickInterval +
                    "；每次计分=" + ConfigManager.GameSetting.PlaygroundMusicScorePerTick);
            }
            if (AdvanceScoreClock(ref ___trickElapsed, __0, ConfigManager.GameSetting.PlaygroundMusicScoreTickInterval))
                RoomManager.Instance.GetClassroomMessageHandler().AwardPlaygroundMusicScore(___lastStudentUID);
            return false;
        }

        private static bool AdvanceScoreClock(ref float elapsed, float delta, float interval)
        {
            if (float.IsNaN(interval) || float.IsInfinity(interval) || interval < 0.05f) interval = 1f;
            if (float.IsNaN(elapsed) || float.IsInfinity(elapsed) || elapsed < 0f || elapsed >= interval) elapsed = 0f;
            if (float.IsNaN(delta) || float.IsInfinity(delta) || delta <= 0f) return false;
            elapsed += Math.Min(delta, 0.25f);
            if (elapsed < interval) return false;
            elapsed %= interval;
            return true;
        }

        // ===== 选歌（把扩展曲目并入不重复洗牌池） =====

        public static bool SelectMusic(PlaygroundBroadcastComponent __instance, ref byte __result)
        {
            MusicBag bag;
            ulong uid = __instance.owner.UID;
            if (!bags.TryGetValue(uid, out bag)) { bag = new MusicBag(); bags.Add(uid, bag); }
            int count = CustomIndex + MusicSync.HostTrackCount;
            if (bag.Count != count || bag.Revision != MusicSync.HostRevision)
            {
                bag.Count = count;
                bag.Revision = MusicSync.HostRevision;
                bag.Remaining.Clear();
                bag.Last = -1;
            }
            if (bag.Remaining.Count == 0)
                for (int i = 1; i < count; i++) bag.Remaining.Add(i);
            int previous = bag.Remaining.IndexOf(bag.Last);
            bool excludePrevious = bag.Remaining.Count > 1 && previous >= 0;
            int choice = UnityEngine.Random.Range(0, bag.Remaining.Count - (excludePrevious ? 1 : 0));
            if (excludePrevious && choice >= previous) choice++;
            int selected = bag.Remaining[choice];
            bag.Remaining.RemoveAt(choice);
            bag.Last = selected;
            bag.Custom = selected >= CustomIndex ? MusicSync.GetHostTrackIndex(selected - CustomIndex) : -1;
            __result = (byte)Math.Min(selected, CustomIndex);
            return false;
        }

        // ===== 播放 =====

        public static void BeforeStop(PlaygroundBroadcastComponent __instance)
        {
            if (playingOwner == __instance) Stop();
        }

        public static bool BeforePlay(PlaygroundBroadcastComponent __instance)
        {
            int index = (byte)Index.GetValue(__instance);
            uint version = (uint)Version.GetValue(__instance);
            if (index < OriginalCount)
            {
                // 原版曲目走游戏自己的 FMOD Studio 事件
                if (playingOwner == __instance) Stop();
                MusicSync.Cancel(__instance.owner == null ? 0 : __instance.owner.UID);
                return true;
            }
            if (index > CustomIndex) return false;
            if (!ClassroomManager.IsSingletonActive() ||
                (ClassroomManager.Instance.curState != EClassroomState.Prepare && ClassroomManager.Instance.curState != EClassroomState.Classing) ||
                !(bool)Bound.GetValue(__instance) || __instance.owner == null || __instance.owner.transform == null)
                return false;
            if (completedOwner == __instance && completedVersion == version) return false;
            MusicBag bag;
            int selected = bags.TryGetValue(__instance.owner.UID, out bag) ? bag.Custom : -1;
            double started;
            string path = MusicSync.Resolve(__instance, version, selected, out started);
            if ((byte)Index.GetValue(__instance) != index || (uint)Version.GetValue(__instance) != version) return false;
            if (path == null)
            {
                // 协商还没完成：静音等待，不做任何播放
                if (waitingOwner != __instance || waitingVersion != version)
                {
                    StopOriginal.Invoke(__instance, null);
                    Stop();
                    waitingOwner = __instance;
                    waitingVersion = version;
                }
                return false;
            }
            if (playingOwner == __instance && playingIndex == index && playingVersion == version && hasChannel)
            {
                bool active;
                if (channel.isPlaying(out active) == RESULT.OK && active) return false;
                completedOwner = __instance;
                completedVersion = version;
                Stop();
                return false;
            }
            StopOriginal.Invoke(__instance, null);
            Stop();
            playingOwner = __instance;
            playingIndex = index;
            playingVersion = version;
            RESULT result = RuntimeManager.CoreSystem.createSound(path, MODE.CREATESTREAM | MODE._3D | MODE.LOOP_OFF, out sound);
            if (result != RESULT.OK) { Fail(index, "createSound", result); return false; }
            hasSound = true;
            ChannelGroup group = default(ChannelGroup);
            try
            {
                var bus = RuntimeManager.GetBus("bus:/BGM");
                bus.lockChannelGroup();
                bus.getChannelGroup(out group);
                bus.unlockChannelGroup();
            }
            catch (Exception e) { Log("BGM 总线不可用: " + e.Message); }
            result = RuntimeManager.CoreSystem.playSound(sound, group, true, out channel);
            if (result != RESULT.OK) { Fail(index, "playSound", result); return false; }
            hasChannel = true;
            // 联机对齐：从协商的统一起点继续播
            if (started > 0d)
            {
                uint length;
                sound.getLength(out length, TIMEUNIT.MS);
                double elapsed = Math.Max(0d, NetRuntime.SessionTime - started) * 1000d;
                if (elapsed >= length) { completedOwner = __instance; completedVersion = version; Stop(); return false; }
                channel.setPosition((uint)elapsed, TIMEUNIT.MS);
            }
            var p = __instance.owner.Position;
            VECTOR pos = new VECTOR { x = p.x, y = p.y, z = p.z };
            VECTOR vel = new VECTOR();
            channel.set3DAttributes(ref pos, ref vel);
            channel.set3DMinMaxDistance(5f, 60f);
            channel.setPaused(false);
            Log("[状态] 正在播放操场曲目: " + Path.GetFileName(path));
            return false;
        }

        public static void AfterUpdate(PlaygroundBroadcastComponent __instance)
        {
            // 协商等待期需要每帧重试解析
            if ((byte)Index.GetValue(__instance) == CustomIndex) BeforePlay(__instance);
            if (playingOwner != __instance || !hasChannel) return;
            bool active;
            if (channel.isPlaying(out active) != RESULT.OK || !active)
            { completedOwner = __instance; completedVersion = playingVersion; Stop(); return; }
            if (__instance.owner == null) { Stop(); return; }
            var p = __instance.owner.Position;
            VECTOR pos = new VECTOR { x = p.x, y = p.y, z = p.z };
            VECTOR vel = new VECTOR();
            channel.set3DAttributes(ref pos, ref vel);
        }

        private static void Fail(int index, string operation, RESULT result)
        {
            Log("播放操场音乐 " + operation + " 失败: " + result);
            var owner = playingOwner;
            Stop();
            playingOwner = owner;
            completedOwner = owner;
            completedVersion = playingVersion;
            if (index == CustomIndex && owner != null && owner.owner != null)
                MusicSync.PlaybackFailed(owner.owner.UID, playingVersion);
        }

        private static void Stop()
        {
            if (hasChannel) channel.stop();
            if (hasSound) sound.release();
            hasChannel = false;
            hasSound = false;
            playingOwner = null;
            playingIndex = -1;
        }

        internal static void ClearSession()
        {
            bags.Clear();
            waitingOwner = null;
            completedOwner = null;
            Stop();
        }

        // 房间回退：共享曲目不可用时把广播柱切回一首原版曲
        internal static void FallbackSong(ulong pillar, uint version)
        {
            if (!NetworkServer.active) return;
            Unit unit = UnitManager.Instance.GetUnitByUID(pillar);
            PlaygroundBroadcastComponent component = unit == null ? null : unit.GetComponent<PlaygroundBroadcastComponent>();
            if (component == null || (uint)Version.GetValue(component) != version || (byte)Index.GetValue(component) != CustomIndex) return;
            MusicSync.Cancel(pillar);
            Index.SetValue(component, (byte)1);
            Version.SetValue(component, version + 1u);
            TrickElapsed.SetValue(component, 0f);
            PlayCurrentMusicOriginal.Invoke(component, null);
            var state = (PlaygroundBroadcastState_S2C)CreateStateOriginal.Invoke(component, new object[] { false });
            RoomManager.Instance.BroadcastToAllExceptHost(state, 0);
            Log("[状态] 共享曲目不可用，房间回退到原版操场音乐。");
        }
    }
}
