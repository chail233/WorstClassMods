using System;
using System.Collections.Generic;
using System.Reflection;
using FMOD;
using FMOD.Studio;
using FMODUnity;
using HarmonyLib;
using Mirror;
using Rewired;
using WorstClassEver.ModAPI;
using UnityEngine;

namespace TrickMole
{
    /// <summary>
    /// 混的入：每节课开始时随机选定一名真人学生，全员可见身份，
    /// 其正向捣蛋计分乘以加成倍数（负分不乘，被抓照样扣）。
    /// 服务端选定后经自定义 Mirror 消息广播，所有端本地显示。
    /// </summary>
    /// <summary>角色分配：服务端全量快照广播（Version 全局单调递增），客机整体替换分配表，杜绝增量漂移（重连残留/错过清场都会被下一条全量自愈）；空列表=清空。</summary>
    public struct MoleSelectMessage : NetworkMessage
    {
        public uint Version;
        public string AssignmentsJson; // 全量分配快照（MoleAssignList 序列化）；服务端权威，客机直接用消息里的副本
    }

    [Serializable]
    public class MoleAssignEntry
    {
        public long uid;        // ulong 经 long 承载（JsonUtility 对 ulong 支持不稳）
        public string roleJson; // RoleDef 序列化
    }

    [Serializable]
    public class MoleAssignList
    {
        public List<MoleAssignEntry> entries = new List<MoleAssignEntry>();
    }

    /// <summary>Kind: 1=Start（C2S 请求/S2C 广播，S2C 带曲目与起点） 2=Stop（仅 S2C） 3=StopRequest（仅 C2S，手动停止/播完）</summary>
    public struct MoleMusicMessage : NetworkMessage
    {
        public const byte Start = 1, Stop = 2, StopRequest = 3;
        public byte Kind;
        public ulong UnitUID;
        public uint AudioEvent;     // 原版曲的 AudioEvents 枚举值；扩展曲填 0
        public string Hash;         // 扩展曲 SHA-256（原版曲为 null）
        public double Started;      // 统一播放起点（NetRuntime.SessionTime），扩展曲进度对齐用
    }

    /// <summary>Kind: 1=Request（仅 C2S，学生→老师推倒请求） 2=Apply（仅 S2C，服务端校验通过后广播）</summary>
    public struct MolePushMessage : NetworkMessage
    {
        public const byte Request = 1, Apply = 2;
        public byte Kind;
        public ulong StudentUID; // 推手（Apply 里用于只对推手启动冷却）
        public ulong TeacherUID;
    }

    public sealed class ModEntry : IMod
    {
        internal static Action<string> Log;

        internal static readonly KeyCode MusicKey = KeyCode.B;
        internal static readonly KeyCode PushKey = KeyCode.E;
        internal static readonly AudioEvents[] MusicPool = { AudioEvents.bgm_YuAi, AudioEvents.bgm_Hajimi1, AudioEvents.bgm_Hajimi2, AudioEvents.bgm_YuAids };
        private const string HarmonyId = "worstclass.trickmole";
        private const int ClassStateChangedMsgId = 301;

        // 本局角色分配：uid → 角色（服务端由配置分配，客机经 MoleSelectMessage.AssignmentsJson 全量快照同步运行时副本）
        internal static readonly Dictionary<ulong, RoleDef> assigned = new Dictionary<ulong, RoleDef>();
        // 捣蛋值累计（per-玩家，口径 = 该角色倍数后实际计入总进度的量）；被挂柱扣 2/3 并归零
        internal static readonly Dictionary<ulong, float> progressByUid = new Dictionary<ulong, float>();
        // 推倒冷却（服务端 per-玩家，绝对截止时间）；version 单调递增供客户端丢弃过期消息
        private static readonly Dictionary<ulong, float> pushCooldownUntil = new Dictionary<ulong, float>();
        internal static uint version;
        private static float nextRepublish;
        private static bool registeredStateHook;

        // 广播播放状态：服务端权威计时，各端本地播放表现；数值取自开始广播者的角色配置
        internal static bool musicPlaying;
        private static RoleDef broadcastRole;
        private static float musicGraceRemain;  // 服务端：起播宽限（不计分）
        private static float musicCooldownRemain; // 服务端：停止后冷却
        private static float trickElapsed;
        private static AudioEvents localPlayingMusic = AudioEvents.None;
        private static ulong localPlayingUID;
        private static float clientCooldownRemain; // 客机本地冷却倒计时（仅 UI 显示）

        // 扩展曲（mp3）低级播放状态：跟随玩家移动，播完自动结束本轮广播
        private static Sound moleSound;
        private static Channel moleChannel;
        private static bool moleHasSound, moleHasChannel;
        private static string pendingPath;          // 等 Started 到点的待播扩展曲
        private static double pendingStarted;
        private static bool localCompleted;         // 播完上报防重发

        // 推倒冷却：客机本地玩家自己的倒计时（收到自己发起的 Apply 时启动）
        private static float clientPushCooldownRemain;

        // 本地玩家的角色 / 本地玩家的捣蛋累计
        internal static RoleDef MyRole
        {
            get
            {
                if (!UnitManager.IsSingletonActive()) return null;
                return assigned.TryGetValue(UnitManager.Instance.localPlayerUID, out RoleDef role) ? role : null;
            }
        }

        internal static float MyProgress
        {
            get
            {
                if (!UnitManager.IsSingletonActive()) return 0f;
                return progressByUid.TryGetValue(UnitManager.Instance.localPlayerUID, out float v) ? v : 0f;
            }
        }

        // 供 UI 遍历本局分配快照
        internal static int AssignedCount => assigned.Count;

        internal static List<KeyValuePair<ulong, RoleDef>> GetAssignedList()
        {
            return new List<KeyValuePair<ulong, RoleDef>>(assigned);
        }

        // UI 用的冷却剩余（房主读服务端权威值，客机读本地倒计时）
        internal static float ClientCooldownRemain => NetRuntime.IsServer ? musicCooldownRemain : clientCooldownRemain;

        internal static float PushCooldownRemain
        {
            get
            {
                if (NetRuntime.IsServer)
                {
                    if (!UnitManager.IsSingletonActive()) return 0f;
                    return pushCooldownUntil.TryGetValue(UnitManager.Instance.localPlayerUID, out float until)
                        ? Mathf.Max(0f, until - Time.unscaledTime) : 0f;
                }
                return clientPushCooldownRemain;
            }
        }

        // ===== 操场特判：混的入广播期间静音广播柱（状态机不动，恢复零成本） =====

        // 柱子原版曲的 Studio 实例（private 字段）
        private static readonly FieldInfo PillarMusicInstanceField = AccessTools.Field(typeof(PlaygroundBroadcastComponent), "musicInstance");
        // PlaygroundMusic Mod 的低级 Channel（软依赖：未装该 Mod 时为 null，静默跳过）
        private static FieldInfo pmHasChannelField, pmChannelField;
        private static bool pmResolved;

        // 懒解析：Mod 加载顺序不保证，首次实际使用时再解析
        private static void EnsurePmResolved()
        {
            if (pmResolved) return;
            pmResolved = true;
            try
            {
                Type pmType = AccessTools.TypeByName("PlaygroundMusic.ModEntry"); // 软依赖：未装 PlaygroundMusic 时为 null
                pmHasChannelField = pmType == null ? null : AccessTools.Field(pmType, "hasChannel");
                pmChannelField = pmType == null ? null : AccessTools.Field(pmType, "channel");
            }
            catch { /* 软依赖：静默 */ }
        }

        public void OnLoad(ModContext context)
        {
            Log = context.Log;
            RoleConfig.Load(context.ConfigDirectory, Log);
            var harmony = new Harmony(HarmonyId);
            try
            {
                // 唯一计分入口：正向加分时给混的入乘倍数（原方法照常执行）
                harmony.Patch(
                    AccessTools.Method(typeof(ClassroomManager), "OnResponseTrickCountData"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeTrickCount"));
                // 罚站柱绑定真正生效的点：混的入被挂柱扣固定捣蛋度
                harmony.Patch(
                    AccessTools.Method(typeof(PunishPillarComponent), "OnResponseBindStudent"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterBindToPillar"));
                // 操场特判：广播柱实例每帧跟随静音/恢复；柱子计分在混的入广播期间只算一路
                harmony.Patch(
                    AccessTools.Method(typeof(PlaygroundBroadcastComponent), "OnUpdate"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterPillarUpdate"));
                harmony.Patch(
                    AccessTools.Method(typeof(ClassroomMessageHandler), "AwardPlaygroundMusicScore"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeAwardMusicScore"));
                // 消息注册挂到游戏自己的注册时机之后（防重注册清掉）
                harmony.Patch(AccessTools.Method(typeof(RoomManager), "RegisterClientMessageHandlers"), postfix: new HarmonyMethod(typeof(ModEntry), "RegisterClient"));
                harmony.Patch(AccessTools.Method(typeof(RoomManager), "RegisterServerMessageHandlers"), postfix: new HarmonyMethod(typeof(ModEntry), "RegisterServer"));
                Writer<MoleSelectMessage>.write = MolePump.WriteMole;
                Reader<MoleSelectMessage>.read = MolePump.ReadMole;
                Writer<MoleMusicMessage>.write = MolePump.WriteMusic;
                Reader<MoleMusicMessage>.read = MolePump.ReadMusic;
                Writer<MolePushMessage>.write = MolePump.WritePush;
                Reader<MolePushMessage>.read = MolePump.ReadPush;
                RegisterClient();
                RegisterServer();

                // 课堂状态变化：进入 Prepare 清空上局选择（轮询会重新选人），下课后通知全员清除显示
                MsgCenter.Register<EClassroomState>(ClassStateChangedMsgId, OnClassStateChanged);
                registeredStateHook = true;

                var pump = new GameObject("混的入提示");
                UnityEngine.Object.DontDestroyOnLoad(pump);
                pump.AddComponent<MolePump>();
            }
            catch
            {
                harmony.UnpatchAll(HarmonyId);
                throw;
            }
            Log("[状态] 课堂角色 Mod 已加载：" + RoleConfig.Roles.Count + " 种角色（" + string.Join("、", RoleConfig.Roles.ConvertAll(r => r.name)) + "）");
        }

        // ===== 计分加成与捣蛋累计（per-角色倍数） =====

        public static void BeforeTrickCount(ulong uid, ref float count)
        {
            if (count <= 0f) return;
            if (assigned.TryGetValue(uid, out RoleDef role))
            {
                count *= role.trickMultiplier;
                progressByUid[uid] = (progressByUid.TryGetValue(uid, out float v) ? v : 0f) + count; // 记录实际计入总进度的量
            }
        }

        // ===== 挂柱惩罚（对所有有捣蛋累计的角色生效） =====

        // 学生被挂罚站柱的 FSM 状态（EUnitFsmStateType.StudentPunished），FSM 全端同步
        private static bool IsPunished(Unit unit)
        {
            return unit != null && unit.isActived && unit.CurState() == (int)EUnitFsmStateType.StudentPunished;
        }

        // PunishPillarComponent.OnResponseBindStudent 是绑定真正生效的点（版本校验通过才调用，
        // 全项目唯一调用处是服务端的绑定确认链路），同一绑定事件只会执行一次
        public static void AfterBindToPillar(Unit student)
        {
            if (student == null) return;
            if (!NetRuntime.IsServer)
            {
                // 客机：角色扮演者被挂 → 本地记录同步归零（扣分本身由服务端经计分广播同步）
                if (student.UID != 0 && progressByUid.ContainsKey(student.UID)) progressByUid[student.UID] = 0f;
                return;
            }
            if (!assigned.ContainsKey(student.UID)) return;
            PunishMole(student.UID);
        }

        private static void PunishMole(ulong uid)
        {
            if (!ClassroomManager.IsSingletonActive() || !RoomManager.IsSingletonActive()) return;
            StopMusicServer(); // 挂柱罚站，广播立刻停
            // 总体进度扣该角色捣蛋累计的 2/3，记录归零（个人贡献分不动）
            if (!assigned.TryGetValue(uid, out RoleDef role)) return;
            float deduct = (progressByUid.TryGetValue(uid, out float progress) ? progress : 0f) * role.progressPenaltyFraction;
            progressByUid[uid] = 0f;
            if (deduct <= 0f) return;
            var response = default(TrickCountChangeMessageResponse_S2C);
            response.unitUID = uid;
            response.changedCount = 0f - deduct;
            response.playgroundMusic = false;
            // 照游戏自己的 PublishTrickCountChange 模式：广播客机 + 服务端本机应用（UI 同步跳字）
            if (!ClassroomManager.Instance.DebugFreezeTrickCount)
            {
                RoomManager.Instance.BroadcastToAllExceptHost(response);
                RoomManager.Instance.GetClassroomMessageHandler().OnTrickCountChangeMessageResponse(response);
            }
            Log("[状态] " + role.name + "被挂上罚站柱，总体进度 -" + deduct.ToString("0.#") + "（捣蛋累计的 " + (role.progressPenaltyFraction * 100f).ToString("0") + "%）");
        }

        // ===== 操场特判实现 =====

        // 每帧把柱子当前 Studio 实例的静音状态对齐到 musicPlaying（幂等，切歌/新实例下一帧自动跟上）
        public static void AfterPillarUpdate(PlaygroundBroadcastComponent __instance)
        {
            SetStudioInstanceMuted(PillarMusicInstanceField, __instance, musicPlaying);
        }

        private static void SetStudioInstanceMuted(FieldInfo field, object owner, bool muted)
        {
            try
            {
                EventInstance? inst = field?.GetValue(owner) as EventInstance?;
                if (!inst.HasValue || !inst.Value.isValid()) return;
                inst.Value.setVolume(muted ? 0f : 1f); // Studio 实例无低级 Channel 可取，用实例音量实现静音
            }
            catch { /* 反射或 FMOD 异常不干扰主逻辑 */ }
        }

        // PlaygroundMusic Mod 的低级 Channel 静音（未装该 Mod 时字段解析不到，跳过）
        private static void MuteLowLevelChannels(bool muted)
        {
            EnsurePmResolved();
            try
            {
                if (pmHasChannelField == null || pmChannelField == null) return;
                if (!(bool)pmHasChannelField.GetValue(null)) return;
                var channel = (Channel)pmChannelField.GetValue(null);
                channel.setMute(muted); // 低级 API 对无效句柄返回错误码不崩溃；hasChannel 守卫下通常有效
            }
            catch { /* 软依赖：静默 */ }
        }

        // 混的入广播期间，广播柱的音乐计分只算一路：非广播者的加分一律跳过
        public static bool BeforeAwardMusicScore(ulong studentUID)
        {
            return !(musicPlaying && studentUID != localPlayingUID);
        }

        // ===== 角色分配（仅服务端）：按配置名额展开，逐名额独立随机 =====

        private static void TryAssignRoles()
        {
            if (!NetRuntime.IsServer || assigned.Count > 0) return;
            if (!ClassroomManager.IsSingletonActive()) return;
            var state = ClassroomManager.Instance.curState;
            if (state != EClassroomState.Prepare && state != EClassroomState.Classing) return;
            if (!UnitManager.IsSingletonActive()) return;
            // 公平选人：Prepare 期间房间名册还有人没出生（场景加载中）就不选，避免先加载完的玩家稳拿角色；
            // 倒计时结束进入 Classing 后兜底照选（缺席者这局没份，下局再算）
            if (state == EClassroomState.Prepare && HasMissingRosterUnit()) return;

            List<RoleDef> slots = RoleConfig.ExpandSlots();
            if (slots.Count == 0) return;
            var students = new List<Unit>();
            foreach (var unit in UnitManager.Instance.playerList)
                if (unit != null && unit.isActived && unit.IsStudent && unit.isPlayer) students.Add(unit);
            if (students.Count == 0) return;

            foreach (RoleDef role in slots)
            {
                if (students.Count == 0) break;
                int pick = UnityEngine.Random.Range(0, students.Count);
                Unit chosen = students[pick];
                students.RemoveAt(pick);
                assigned[chosen.UID] = role;
                progressByUid[chosen.UID] = 0f;
                Log("[状态] 本局角色已分配：" + role.name + " → " + chosen.GetPlayerUnitName());
            }
            BroadcastAssignments();
        }

        // 名册里还有玩家没出生（Unit 为空或未激活）= 有人在加载，选人不公平
        private static bool HasMissingRosterUnit()
        {
            if (!RoomManager.IsSingletonActive()) return false;
            Room room = RoomManager.Instance.CurrentRoom;
            if (room == null) return false;
            foreach (var unit in room.GetPlayers().Values)
                if (unit == null || !unit.isActived) return true;
            return false;
        }

        // 全量快照广播（Version 全局单调递增；空表=清空，重广播也递增让客户端无条件替换）
        private static void BroadcastAssignments()
        {
            version++;
            if (!RoomManager.IsSingletonActive() || !NetworkServer.active) return;
            var snapshot = new MoleAssignList();
            foreach (var pair in assigned)
                snapshot.entries.Add(new MoleAssignEntry { uid = (long)pair.Key, roleJson = JsonUtility.ToJson(pair.Value) });
            RoomManager.Instance.BroadcastToAllExceptHost(
                new MoleSelectMessage { Version = version, AssignmentsJson = JsonUtility.ToJson(snapshot) }, 0);
        }

        private static void OnClassStateChanged(EClassroomState state)
        {
            // 每节课开始重选；下课/课前清空并通知全员撤掉显示
            // 冷却也一并清零：上局下课触发的 StopMusicServer 会残留冷却，不清会让新局开场无法广播
            if (state == EClassroomState.Prepare)
            {
                assigned.Clear();
                progressByUid.Clear();
                pushCooldownUntil.Clear();
                musicCooldownRemain = 0f;
                clientCooldownRemain = 0f;
                clientPushCooldownRemain = 0f;
                // Prepare 也广播清场（全量空表）：防客机错过上局清场后残留旧角色
                if (NetRuntime.IsServer) BroadcastAssignments();
                return;
            }
            if (state == EClassroomState.BeforeClass || state == EClassroomState.ClassFinish)
            {
                assigned.Clear();
                progressByUid.Clear();
                pushCooldownUntil.Clear();
                musicCooldownRemain = 0f;
                clientCooldownRemain = 0f;
                clientPushCooldownRemain = 0f;
                if (NetRuntime.IsServer) StopMusicServer();
                if (NetRuntime.IsServer) BroadcastAssignments();
            }
        }

        // ===== 混的入广播（按键触发，3D 音乐 + 持续计分） =====

        // 服务端：处理播放请求（Start=开始，StopRequest=手动停止）；请求者必须是带广播技能的角色
        private static void OnServerMusic(NetworkConnectionToClient connection, MoleMusicMessage message)
        {
            if (!NetRuntime.IsServer) return;
            ulong senderUID = connection == null || connection == NetworkServer.localConnection
                ? MonoSingletonT<UnitManager>.Instance.localPlayerUID
                : RoomManager.Instance.GetPlayerUIDByConnection(connection);
            if (moleUIDIsEmpty() || !assigned.TryGetValue(senderUID, out RoleDef role) || !role.canBroadcast) return;
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            if (!UnitManager.IsSingletonActive()) return;
            if (IsPunished(UnitManager.Instance.GetUnitByUID(senderUID))) return; // 被挂柱禁用技能
            if (message.Kind == MoleMusicMessage.Start)
            {
                if (musicPlaying || musicCooldownRemain > 0f) return;
                StartMusicServer(senderUID, role);
            }
            else if (message.Kind == MoleMusicMessage.StopRequest)
            {
                StopMusicServer();
            }
        }

        private static bool moleUIDIsEmpty()
        {
            return assigned.Count == 0;
        }

        private static void StartMusicServer(ulong playerUID, RoleDef role)
        {
            // 曲池 = 原版 4 首 + PlaygroundMusic 全员交集扩展曲（未装/交集空时自动退化为纯原版）
            AudioEvents pickAudio = AudioEvents.None;
            string pickHash = null;
            int shared = PmBridge.SharedTrackCount;
            int pick = UnityEngine.Random.Range(0, MusicPool.Length + shared);
            if (pick < MusicPool.Length)
            {
                pickAudio = MusicPool[pick];
            }
            else
            {
                string hash = PmBridge.GetSharedHash(pick - MusicPool.Length);
                // 服务端自己的文件也要在且未变更，否则回退原版曲
                if (hash != null && PmBridge.FindLocalByHash(hash) != null) pickHash = hash;
                else pickAudio = MusicPool[0];
            }
            musicPlaying = true;
            broadcastRole = role;
            musicGraceRemain = role.scoreGraceSeconds;
            musicCooldownRemain = 0f;
            trickElapsed = 0f;
            double started = NetRuntime.SessionTime + 0.5d; // 扩展曲统一播放起点
            var broadcast = new MoleMusicMessage { Kind = MoleMusicMessage.Start, UnitUID = playerUID, AudioEvent = (uint)pickAudio, Hash = pickHash, Started = started };
            if (RoomManager.IsSingletonActive() && NetworkServer.active) RoomManager.Instance.BroadcastToAllExceptHost(broadcast, 0);
            StartLocalPlayback(playerUID, pickAudio, pickHash, started);
            Log("[状态] " + role.name + "开始播放广播" + (pickHash != null ? "（扩展曲）" : "") + "，前 " + role.scoreGraceSeconds + " 秒不计分");
        }

        private static void StopMusicServer()
        {
            if (!musicPlaying) return;
            musicPlaying = false;
            musicCooldownRemain = broadcastRole != null ? broadcastRole.musicCooldownSeconds : 20f;
            var broadcast = new MoleMusicMessage { Kind = MoleMusicMessage.Stop, UnitUID = 0, AudioEvent = 0 };
            if (RoomManager.IsSingletonActive() && NetworkServer.active) RoomManager.Instance.BroadcastToAllExceptHost(broadcast, 0);
            StopMusicLocal();
            Log("[状态] 广播结束，冷却 " + musicCooldownRemain.ToString("0.#") + " 秒");
        }

        // 服务端每帧：宽限 → 计分；停止后冷却倒计时
        private static void TickServerMusic(float dt)
        {
            if (!musicPlaying)
            {
                if (musicCooldownRemain > 0f) musicCooldownRemain = Math.Max(0f, musicCooldownRemain - dt);
                return;
            }
            if (musicGraceRemain > 0f)
            {
                musicGraceRemain = Math.Max(0f, musicGraceRemain - dt);
                return;
            }
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing || localPlayingUID == 0) return;
            trickElapsed += dt;
            float interval = ConfigManager.GameSetting.PlaygroundMusicScoreTickInterval;
            if (float.IsNaN(interval) || float.IsInfinity(interval) || interval < 0.05f) interval = 1f;
            if (trickElapsed >= interval)
            {
                trickElapsed %= interval;
                RoomManager.Instance.GetClassroomMessageHandler().AwardPlaygroundMusicScore(localPlayingUID);
            }
        }

        // 各端本地播放入口：原版曲立即播（循环曲起点无意义），扩展曲先找本地文件再等 Started 到点
        // 注意 Mirror 把 null 字符串序列化读回为 ""，所以判断必须用 IsNullOrEmpty
        private static void StartLocalPlayback(ulong uid, AudioEvents audio, string hash, double started)
        {
            if (!UnitManager.IsSingletonActive()) return;
            Unit unit = UnitManager.Instance.GetUnitByUID(uid);
            if (unit == null || unit.transform == null) return;
            StopMusicLocal();
            localPlayingUID = uid;
            if (!string.IsNullOrEmpty(hash))
            {
                // 扩展曲：按 hash 找本机文件，缺歌则本轮静默（计分仍由服务端权威驱动）
                pendingPath = PmBridge.FindLocalByHash(hash);
                pendingStarted = pendingPath != null ? started : 0d;
                return;
            }
            var request = default(PlaySoundRequest);
            request.UserUID = uid;
            request.AudioName = audio;
            request.PlaySoundType = EPlaySoundType.None;
            MonoSingletonT<AudioManager>.Instance.Play(audio, unit.gameObject, request, true);
            localPlayingMusic = audio;
        }

        // 游戏当前生效的音乐音量 = 音乐设置 × 主音量（AudioBus.SetVolume 的同一口径）。
        // 低级 Channel 挂进 bus:/BGM 的 channelGroup 后不吃 Studio Bus.setVolume，必须自己跟
        private static float GameMusicVolume()
        {
            if (!MonoSingletonT<AudioManager>.IsSingletonActive()) return 1f;
            var audio = MonoSingletonT<AudioManager>.Instance;
            return Mathf.Clamp01(audio.GetMusicVolume() * audio._mainVolum);
        }

        // 扩展曲低级播放（照 PlaygroundMusic 模式：挂 BGM 总线、3D 定位、按 Started 偏移进度）
        private static void PlayExtendedLocal(string path, double started, ulong uid)
        {
            Unit unit = UnitManager.IsSingletonActive() ? UnitManager.Instance.GetUnitByUID(uid) : null;
            if (unit == null || unit.transform == null) return;
            RESULT result = RuntimeManager.CoreSystem.createSound(path, MODE.CREATESTREAM | MODE._3D | MODE.LOOP_OFF, out moleSound);
            if (result != RESULT.OK) { Log("混的入广播 createSound 失败: " + result); return; }
            moleHasSound = true;
            uint length;
            moleSound.getLength(out length, TIMEUNIT.MS);
            double elapsed = Math.Max(0d, NetRuntime.SessionTime - started) * 1000d;
            if (elapsed >= length) { moleSound.release(); moleHasSound = false; return; } // 已播到头，本轮作废
            ChannelGroup group = default(ChannelGroup);
            try
            {
                var bus = RuntimeManager.GetBus("bus:/BGM");
                bus.lockChannelGroup();
                bus.getChannelGroup(out group);
                bus.unlockChannelGroup();
            }
            catch (Exception e) { Log("BGM 总线不可用: " + e.Message); }
            result = RuntimeManager.CoreSystem.playSound(moleSound, group, true, out moleChannel);
            if (result != RESULT.OK) { Log("混的入广播 playSound 失败: " + result); return; }
            moleHasChannel = true;
            moleChannel.setVolume(GameMusicVolume()); // 跟随游戏音乐音量（之后每帧继续同步，滑条实时生效）
            if (elapsed > 0d) moleChannel.setPosition((uint)elapsed, TIMEUNIT.MS);
            var p = unit.Position;
            var pos = new VECTOR { x = p.x, y = p.y, z = p.z };
            var vel = new VECTOR();
            moleChannel.set3DAttributes(ref pos, ref vel);
            moleChannel.setPaused(false);
            localCompleted = false;
        }

        // 每帧：pending 扩展曲到点播放 + 3D 跟随混的入 + 播完自动结束本轮
        internal static void TickMusicPlayback()
        {
            if (pendingPath != null && NetRuntime.SessionTime >= pendingStarted)
            {
                string path = pendingPath;
                double started = pendingStarted;
                pendingPath = null;
                pendingStarted = 0d;
                if (musicPlaying) PlayExtendedLocal(path, started, localPlayingUID);
            }
            if (!moleHasChannel) return;
            bool playing;
            RESULT r = moleChannel.isPlaying(out playing);
            if (r != RESULT.OK)
            {
                moleHasChannel = false;
                ReleaseMoleSound();
                NotifyPlaybackFinished();
                return;
            }
            if (playing)
            {
                moleChannel.setVolume(GameMusicVolume()); // 每帧同步音量，设置滑条实时生效
                if (UnitManager.IsSingletonActive())
                {
                    Unit unit = UnitManager.Instance.GetUnitByUID(localPlayingUID);
                    if (unit != null && unit.transform != null)
                    {
                        var p = unit.Position;
                        var pos = new VECTOR { x = p.x, y = p.y, z = p.z };
                        var vel = new VECTOR();
                        moleChannel.set3DAttributes(ref pos, ref vel);
                    }
                }
            }
            else if (!localCompleted)
            {
                moleHasChannel = false;
                ReleaseMoleSound();
                NotifyPlaybackFinished(); // mp3 播完 = 本轮广播结束
            }
        }

        private static void ReleaseMoleSound()
        {
            if (moleHasSound) { moleSound.release(); moleHasSound = false; }
        }

        private static void NotifyPlaybackFinished()
        {
            if (!musicPlaying) return;
            if (NetRuntime.IsServer) StopMusicServer();
            else if (RoomManager.IsSingletonActive()) RoomManager.Instance.SendToServer(new MoleMusicMessage { Kind = MoleMusicMessage.StopRequest });
        }

        private static void StopMusicLocal()
        {
            pendingPath = null;
            pendingStarted = 0d;
            if (moleHasChannel)
            {
                moleChannel.stop();
                moleHasChannel = false;
            }
            ReleaseMoleSound();
            if (localPlayingMusic != AudioEvents.None)
            {
                if (MonoSingletonT<AudioManager>.IsSingletonActive())
                    MonoSingletonT<AudioManager>.Instance.StopAudio(localPlayingMusic, localPlayingUID);
                localPlayingMusic = AudioEvents.None;
            }
            localPlayingUID = 0;
        }

        // 客机：收到广播 → 本地播放/停止（musicPlaying 标志供 UI 与按键防重复使用）
        private static void OnClientMusic(MoleMusicMessage message)
        {
            if (NetworkServer.active) return;
            if (message.Kind == MoleMusicMessage.Start)
            {
                musicPlaying = true;
                StartLocalPlayback(message.UnitUID, (AudioEvents)message.AudioEvent, message.Hash, message.Started);
            }
            else
            {
                musicPlaying = false;
                clientCooldownRemain = assigned.TryGetValue(message.UnitUID, out RoleDef role)
                    ? role.musicCooldownSeconds : 20f;
                StopMusicLocal();
            }
        }

        // ===== 推倒老师技能（背后 + 按 E） =====

        // 服务端：校验推倒请求（身份/角色技能/状态/冷却/目标合法/距离/背后）
        private static void OnServerPush(NetworkConnectionToClient connection, MolePushMessage message)
        {
            if (!NetRuntime.IsServer || message.Kind != MolePushMessage.Request) return;
            ulong senderUID = connection == null || connection == NetworkServer.localConnection
                ? MonoSingletonT<UnitManager>.Instance.localPlayerUID
                : RoomManager.Instance.GetPlayerUIDByConnection(connection);
            if (!assigned.TryGetValue(senderUID, out RoleDef role) || !role.canPush) return;
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            if (pushCooldownUntil.TryGetValue(senderUID, out float until) && Time.unscaledTime < until) return;
            if (!UnitManager.IsSingletonActive()) return;
            Unit student = UnitManager.Instance.GetUnitByUID(senderUID);
            Unit teacher = UnitManager.Instance.GetUnitByUID(message.TeacherUID);
            if (student == null || !student.isActived || teacher == null || !teacher.isActived) return;
            if (IsPunished(student)) return; // 被挂柱禁用技能
            if (!teacher.IsTeacher || teacher.IsDead || teacher.IsStun) return;
            if (!IsBehindAndClose(student, teacher, role)) return;

            pushCooldownUntil[senderUID] = Time.unscaledTime + role.pushCooldownSeconds;
            var broadcast = new MolePushMessage { Kind = MolePushMessage.Apply, StudentUID = senderUID, TeacherUID = teacher.UID };
            if (RoomManager.IsSingletonActive() && NetworkServer.active) RoomManager.Instance.BroadcastToAllExceptHost(broadcast, 0);
            PushApplyLocal(teacher.UID, role);
            Log("[状态] " + role.name + "从背后推倒了老师");
        }

        // 背后且近距离：学生位于老师身后约 110° 扇区内且距离 ≤ 角色配置的触发距离
        private static bool IsBehindAndClose(Unit student, Unit teacher, RoleDef role)
        {
            Vector3 toStudent = student.Position - teacher.Position;
            toStudent.y = 0f;
            if (toStudent.magnitude > role.pushRange) return false;
            Vector3 fwd = teacher.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) return true; // 朝向缺失时不额外限制
            return Vector3.Dot(fwd.normalized, toStudent.normalized) < role.pushBehindDot;
        }

        // 找本地玩家身后最近的老师（按键预判定与 UI 提示共用，服务端会再完整校验）
        internal static bool HasPushTarget() => FindPushTarget() != null;

        private static Unit FindPushTarget()
        {
            if (!UnitManager.IsSingletonActive() || !ClassroomManager.IsSingletonActive()) return null;
            Unit self = UnitManager.Instance.GetUnitByUID(UnitManager.Instance.localPlayerUID);
            if (self == null || !self.isActived || IsPunished(self)) return null; // 被挂柱禁用技能（按键提示也随之隐藏）
            if (!assigned.TryGetValue(self.UID, out RoleDef role) || !role.canPush) return null;
            Unit best = null;
            float bestDist = role.pushRange;
            foreach (var unit in UnitManager.Instance.playerList)
            {
                if (unit == null || !unit.isActived || !unit.IsTeacher || unit.IsDead || unit.IsStun) continue;
                if (!IsBehindAndClose(self, unit, role)) continue;
                float dist = Vector3.Distance(self.Position, unit.Position);
                if (dist < bestDist) { bestDist = dist; best = unit; }
            }
            return best;
        }

        // 各端应用：老师倒地指定时长（TryGetProjectileStun 带精确时长，Stun 动画与滑倒一致，tip 自动清理）
        private static void PushApplyLocal(ulong teacherUID, RoleDef role)
        {
            if (!UnitManager.IsSingletonActive()) return;
            Unit teacher = UnitManager.Instance.GetUnitByUID(teacherUID);
            if (teacher == null || !teacher.IsTeacher) return;
            teacher.GetComponent<TeacherStunComponent>()?.TryGetProjectileStun(role.pushStunSeconds);
        }

        // 客机：收到 Apply → 本地应用表现；推手若是本地玩家则启动自己的冷却
        private static void OnClientPush(MolePushMessage message)
        {
            if (NetworkServer.active) return;
            if (message.Kind != MolePushMessage.Apply) return;
            if (UnitManager.IsSingletonActive() && message.StudentUID == UnitManager.Instance.localPlayerUID
                && assigned.TryGetValue(message.StudentUID, out RoleDef role))
                clientPushCooldownRemain = role.pushCooldownSeconds;
            PushApplyLocal(message.TeacherUID, assigned.TryGetValue(message.StudentUID, out RoleDef r) ? r : new RoleDef());
        }

        // 本地玩家持有推倒技能时，按 E 对背后老师发起推倒（房主直接服务端处理）
        private static void HandlePushKey()
        {
            if (!UnitManager.IsSingletonActive()) return;
            ulong self = UnitManager.Instance.localPlayerUID;
            if (!assigned.TryGetValue(self, out RoleDef role) || !role.canPush) return;
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            if (IsPunished(UnitManager.Instance.GetUnitByUID(self))) return; // 被挂柱禁用技能
            if (NetRuntime.IsServer
                ? (pushCooldownUntil.TryGetValue(self, out float until) && Time.unscaledTime < until)
                : clientPushCooldownRemain > 0f) return;
            if (!MonoSingletonT<InputManager>.IsSingletonActive() || MonoSingletonT<InputManager>.Instance.FrozedPlayerInput || MonoSingletonT<InputManager>.Instance.FrozedAllInput) return;
            if (!ReInput.isReady) return;
            Keyboard keyboard = ReInput.players.GetPlayer(0).controllers.Keyboard;
            if (keyboard == null || !keyboard.GetKeyDown(PushKey)) return;
            Unit target = FindPushTarget();
            if (target == null) return;
            if (NetRuntime.IsServer)
            {
                pushCooldownUntil[self] = Time.unscaledTime + role.pushCooldownSeconds;
                var broadcast = new MolePushMessage { Kind = MolePushMessage.Apply, StudentUID = self, TeacherUID = target.UID };
                if (RoomManager.IsSingletonActive() && NetworkServer.active) RoomManager.Instance.BroadcastToAllExceptHost(broadcast, 0);
                PushApplyLocal(target.UID, role);
                Log("[状态] " + role.name + "从背后推倒了老师");
            }
            else
            {
                RoomManager.Instance.SendToServer(new MolePushMessage { Kind = MolePushMessage.Request, TeacherUID = target.UID });
            }
        }

        // ===== 消息注册与处理 =====

        public static void RegisterClient()
        {
            NetworkClient.RegisterHandler<MoleSelectMessage>(OnMoleSelect, true);
            NetworkClient.RegisterHandler<MoleMusicMessage>(OnClientMusic, true);
            NetworkClient.RegisterHandler<MolePushMessage>(OnClientPush, true);
        }

        public static void RegisterServer()
        {
            NetworkServer.RegisterHandler<MoleMusicMessage>(OnServerMusic, true);
            NetworkServer.RegisterHandler<MolePushMessage>(OnServerPush, true);
        }

        private static void OnMoleSelect(MoleSelectMessage message)
        {
            if (NetworkServer.active) return;
            if (message.Version < version) return; // 过期消息
            List<MoleAssignEntry> entries;
            try
            {
                entries = string.IsNullOrEmpty(message.AssignmentsJson)
                    ? new List<MoleAssignEntry>()
                    : JsonUtility.FromJson<MoleAssignList>(message.AssignmentsJson).entries;
            }
            catch { return; } // 畸形包不应用，等下一轮全量重广播自愈
            version = message.Version;
            // 全量替换：客机分配表与服务端严格一致，不会残留旧角色
            assigned.Clear();
            foreach (MoleAssignEntry entry in entries)
            {
                if (entry.uid == 0) continue;
                RoleDef role = ParseRole(entry.roleJson);
                if (role != null) assigned[(ulong)entry.uid] = role;
            }
            // 失去角色的捣蛋累计一并清掉（本地 UI 口径）
            var stale = new List<ulong>();
            foreach (ulong uid in progressByUid.Keys)
                if (!assigned.ContainsKey(uid)) stale.Add(uid);
            foreach (ulong uid in stale) progressByUid.Remove(uid);
        }

        private static RoleDef ParseRole(string json)
        {
            try { return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<RoleDef>(json); }
            catch { return null; }
        }

        // ===== 服务端每帧驱动 =====

        internal static void Tick()
        {
            if (NetRuntime.IsServer)
            {
                // Prepare 进入瞬间若还没有学生，轮询兜底补选
                TryAssignRoles();
                if (assigned.Count > 0 && Time.unscaledTime >= nextRepublish)
                {
                    nextRepublish = Time.unscaledTime + 10f;
                    // 重连/中途加入的玩家靠全量重广播自愈（整表替换，Version 递增）
                    BroadcastAssignments();
                }
                TickServerMusic(Time.unscaledDeltaTime);
            }
            else
            {
                if (clientCooldownRemain > 0f) clientCooldownRemain = Math.Max(0f, clientCooldownRemain - Time.unscaledDeltaTime);
                if (clientPushCooldownRemain > 0f) clientPushCooldownRemain = Math.Max(0f, clientPushCooldownRemain - Time.unscaledDeltaTime);
            }
            MuteLowLevelChannels(musicPlaying);
            TickMusicPlayback();
            HandleMusicKey();
            HandlePushKey();
        }

        // 本地玩家持有广播技能时，按 B 开关广播（房主直接本地处理）
        private static void HandleMusicKey()
        {
            if (!UnitManager.IsSingletonActive()) return;
            ulong self = UnitManager.Instance.localPlayerUID;
            if (!assigned.TryGetValue(self, out RoleDef role) || !role.canBroadcast) return;
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            if (IsPunished(UnitManager.Instance.GetUnitByUID(self))) return; // 被挂柱禁用技能
            if (!MonoSingletonT<InputManager>.IsSingletonActive() || MonoSingletonT<InputManager>.Instance.FrozedPlayerInput || MonoSingletonT<InputManager>.Instance.FrozedAllInput) return;
            if (!ReInput.isReady) return;
            Keyboard keyboard = ReInput.players.GetPlayer(0).controllers.Keyboard;
            if (keyboard == null || !keyboard.GetKeyDown(MusicKey)) return;
            if (NetRuntime.IsServer)
            {
                if (musicPlaying) StopMusicServer();
                else if (musicCooldownRemain <= 0f) StartMusicServer(self, role);
            }
            else if (musicPlaying)
            {
                RoomManager.Instance.SendToServer(new MoleMusicMessage { Kind = MoleMusicMessage.StopRequest });
            }
            else if (clientCooldownRemain <= 0f)
            {
                RoomManager.Instance.SendToServer(new MoleMusicMessage { Kind = MoleMusicMessage.Start });
            }
        }

        internal static void Cleanup()
        {
            if (registeredStateHook) MsgCenter.UnRegister(ClassStateChangedMsgId);
            registeredStateHook = false;
        }
    }
}
