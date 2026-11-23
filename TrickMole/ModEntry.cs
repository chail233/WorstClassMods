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
    /// <summary>角色分配：服务端全量快照广播（Version 全局单调递增），客机整体替换分配表，杜绝增量漂移（重连残留/错过清场都会被下一条全量自愈）；空数组=清空。</summary>
    /// <summary>本机引擎（2022.3.62f3c1 中国变体）的 JsonUtility 会静默跳过 List&lt;自定义类&gt; 字段（实测 ToJson 返回 {}），所以分配数据不走 JSON 包装，
    /// 直接放 Mirror 结构体的原生数组里逐条序列化；每条角色参数是纯字段类的独立 JSON（实测可靠，BioChemConfig 同型已验证）。</summary>
    public struct MoleSelectMessage : NetworkMessage
    {
        public uint Version;
        public long[] Uids;        // 分配单位的 UID（ulong 经 long 承载，与 RoleJsons 一一对应）
        public string[] RoleJsons; // 每项 = 单个 RoleDef 的 JSON
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

    /// <summary>Kind: 1=Gained（免伤就绪） 2=Consumed（挡下一次老师攻击后消耗）</summary>
    public struct MoleImmunityMessage : NetworkMessage
    {
        public const byte Gained = 1, Consumed = 2;
        public byte Kind;
        public ulong StudentUID;
    }

    public struct NerdRescueScoreMessage : NetworkMessage
    {
        public ulong RescuerUID;
    }

    public sealed class ModEntry : IMod
    {
        internal static Action<string> Log;

        internal static readonly KeyCode RoleBKey = KeyCode.B;
        internal static readonly KeyCode PushKey = KeyCode.E;
        internal static readonly AudioEvents[] MusicPool = { AudioEvents.bgm_YuAi, AudioEvents.bgm_Hajimi1, AudioEvents.bgm_Hajimi2, AudioEvents.bgm_YuAids };
        private const string HarmonyId = "worstclass.trickmole";
        private const int ClassStateChangedMsgId = 301;
        private const float NerdRescueScore = 10f;
        private static ulong pendingRescueScoreUid;

        // 本局角色分配：uid → 角色（服务端由配置分配，客机经 MoleSelectMessage.AssignmentsJson 全量快照同步运行时副本）
        internal static readonly Dictionary<ulong, RoleDef> assigned = new Dictionary<ulong, RoleDef>();
        // 捣蛋值累计（per-玩家，口径 = 该角色倍数后实际计入总进度的量）；被挂柱扣 2/3 并归零
        internal static readonly Dictionary<ulong, float> progressByUid = new Dictionary<ulong, float>();
        // 推倒冷却（服务端 per-玩家，绝对截止时间）；version 单调递增供客户端丢弃过期消息
        private static readonly Dictionary<ulong, float> pushCooldownUntil = new Dictionary<ulong, float>();
        // 免伤（书呆子类角色）：静心计时（秒）+ 免伤次数。各端按同一条计分事件流本地维护，
        // 服务端在获得/消耗时广播，客机以此对齐
        private static readonly Dictionary<ulong, float> immunityIdle = new Dictionary<ulong, float>();
        private static readonly Dictionary<ulong, int> immunityCharges = new Dictionary<ulong, int>();
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

        internal static bool RoleHasImmunity(RoleDef role)
        {
            return role != null && role.maxImmunityCharges > 0 && role.immunityIdleSeconds > 0f;
        }

        // 本地玩家免伤次数 / 静心剩余（UI 用；各端本地维护，服务端获得与消耗时广播对齐）
        internal static int MyImmunityCharges
        {
            get
            {
                if (!UnitManager.IsSingletonActive()) return 0;
                return immunityCharges.TryGetValue(UnitManager.Instance.localPlayerUID, out int v) ? v : 0;
            }
        }

        internal static float MyImmunityRemaining
        {
            get
            {
                if (!UnitManager.IsSingletonActive()) return 0f;
                ulong uid = UnitManager.Instance.localPlayerUID;
                if (immunityCharges.TryGetValue(uid, out int charges) && charges > 0) return 0f;
                RoleDef role = MyRole;
                if (!RoleHasImmunity(role)) return 0f;
                float idle = immunityIdle.TryGetValue(uid, out float v) ? v : 0f;
                return Mathf.Max(0f, role.immunityIdleSeconds - idle);
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
                // 唯一计分入口：正向加分时按角色倍数缩放（混的入 ×2、书呆子 ×0.5，原方法照常执行）
                harmony.Patch(
                    AccessTools.Method(typeof(ClassroomManager), "OnResponseTrickCountData"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeTrickCount"));
                harmony.Patch(
                    AccessTools.Method(typeof(SkillMessageHandler), "ReleaseStudentOnServer"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterStudentRescue"));
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
                // 老师攻击落到书呆子身上：消耗一次免伤并拦下攻击（抓捕 / 粉笔剑气 / 客机眩晕上报）
                harmony.Patch(
                    AccessTools.Method(typeof(SkillMessageHandler), "OnUseSkillMessageRequest"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeUseSkillRequest"));
                harmony.Patch(
                    AccessTools.Method(typeof(StudentStunComponent), "GetStun"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeStudentStun"));
                harmony.Patch(
                    AccessTools.Method(typeof(UnitMessageHandler), "OnStudentGetStunRequest"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeStudentStunRequest"));
                // 体育生：视线内外移速 + 进食/救人提速（本地结算，各端算各端的人物）
                harmony.Patch(
                    AccessTools.Method(typeof(InputComponent), "GetCurrentMoveSpeed"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterMoveSpeed"));
                harmony.Patch(
                    AccessTools.Method(typeof(StudentItemUseComponent), "OnUpdate"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeItemUseTick"));
                harmony.Patch(
                    AccessTools.Method(typeof(PunishPillarComponent), "GetRescueDuration"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterRescueDuration"));
                // 消息注册挂到游戏自己的注册时机之后（防重注册清掉）
                harmony.Patch(AccessTools.Method(typeof(RoomManager), "RegisterClientMessageHandlers"), postfix: new HarmonyMethod(typeof(ModEntry), "RegisterClient"));
                harmony.Patch(AccessTools.Method(typeof(RoomManager), "RegisterServerMessageHandlers"), postfix: new HarmonyMethod(typeof(ModEntry), "RegisterServer"));
                Writer<MoleSelectMessage>.write = MolePump.WriteMole;
                Reader<MoleSelectMessage>.read = MolePump.ReadMole;
                Writer<MoleMusicMessage>.write = MolePump.WriteMusic;
                Reader<MoleMusicMessage>.read = MolePump.ReadMusic;
                Writer<MolePushMessage>.write = MolePump.WritePush;
                Reader<MolePushMessage>.read = MolePump.ReadPush;
                Writer<MoleImmunityMessage>.write = MolePump.WriteImmunity;
                Reader<MoleImmunityMessage>.read = MolePump.ReadImmunity;
                Writer<NerdRescueScoreMessage>.write = (writer, message) => writer.WriteULong(message.RescuerUID);
                Reader<NerdRescueScoreMessage>.read = reader => new NerdRescueScoreMessage { RescuerUID = reader.ReadULong() };
                AthleteSprint.RegisterSerializers();
                RegisterClient();
                RegisterServer();

                // 课堂状态变化：进入 Prepare 清空上局选择（轮询会重新选人），下课后通知全员清除显示
                MsgCenter.Register<EClassroomState>(ClassStateChangedMsgId, OnClassStateChanged);
                registeredStateHook = true;

                var pump = new GameObject("课堂角色提示");
                UnityEngine.Object.DontDestroyOnLoad(pump);
                pump.AddComponent<MolePump>();
            }
            catch
            {
                harmony.UnpatchAll(HarmonyId);
                throw;
            }
            Log("[状态] 课堂角色 Mod 已加载：" + RoleConfig.Roles.Count + " 种角色（" + string.Join("、", RoleConfig.Roles.ConvertAll(r => r.name)) + "）；在游戏设置-课堂角色页配置角色");
        }

        // ===== 计分加成与捣蛋累计（per-角色倍数） =====

        public static void BeforeTrickCount(ulong uid, ref float count)
        {
            if (count <= 0f) return;
            // 固定救援奖励只跳过这一次倍率，仍计入累计并重置免伤计时。
            bool isRescueReward = pendingRescueScoreUid != 0 && pendingRescueScoreUid == uid;
            if (isRescueReward) pendingRescueScoreUid = 0;
            if (assigned.TryGetValue(uid, out RoleDef role))
            {
                if (!isRescueReward) count *= role.trickMultiplier;
                progressByUid[uid] = (progressByUid.TryGetValue(uid, out float v) ? v : 0f) + count; // 记录实际计入总进度的量
                // 捣蛋即失去免伤并重新静心（各端从同一计分入口执行，状态天然同步）
                if (RoleHasImmunity(role))
                {
                    immunityIdle[uid] = 0f;
                    immunityCharges[uid] = 0;
                }
            }
        }

        // 原生方法只在罚站柱上的学生已成功释放时返回 true，手动和无人机救援共用。
        public static void AfterStudentRescue(ulong rescuerUID, StudentReleaseMessageRequest_C2S request, bool __result)
        {
            if (!__result || !NetRuntime.IsServer || rescuerUID == 0 || rescuerUID == request.studentUID) return;
            if (!assigned.TryGetValue(rescuerUID, out RoleDef role) || role.id != "nerd") return;
            if (!ClassroomManager.IsSingletonActive() || !RoomManager.IsSingletonActive()) return;
            ClassroomManager classroom = ClassroomManager.Instance;
            if (classroom.curState != EClassroomState.Classing || classroom.DebugFreezeTrickCount) return;
            Unit rescuer = UnitManager.Instance.GetUnitByUID(rescuerUID);
            if (rescuer == null || !rescuer.isPlayer || !rescuer.IsStudent) return;

            RoomManager.Instance.BroadcastToAllExceptHost(new NerdRescueScoreMessage { RescuerUID = rescuerUID });
            ApplyNerdRescueScore(rescuerUID);
        }

        private static void ApplyNerdRescueScore(ulong uid)
        {
            if (!ClassroomManager.IsSingletonActive() || !UnitManager.IsSingletonActive()) return;
            pendingRescueScoreUid = uid;
            try { ClassroomManager.Instance.OnResponseTrickCountData(uid, NerdRescueScore); }
            finally { pendingRescueScoreUid = 0; }
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
            if (student == null || student.UID == 0) return;
            // 被老师制服：免伤一并作废，之后从静心重新积攒
            immunityIdle.Remove(student.UID);
            immunityCharges.Remove(student.UID);
            if (!NetRuntime.IsServer)
            {
                // 客机：角色扮演者被挂 → 本地记录同步归零（扣分本身由服务端经计分广播同步）
                if (progressByUid.ContainsKey(student.UID)) progressByUid[student.UID] = 0f;
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

        // ===== 角色分配（仅服务端）：名额洗牌后逐名额随机填充 =====

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
            // 洗牌：学生数少于名额时，被填的是随机一批名额而非配置顺序靠前的名额，
            // 每个角色等可能出现在本局（满员时洗牌不改变分布，仍是不放回均匀抽样）
            for (int i = slots.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (slots[i], slots[j]) = (slots[j], slots[i]);
            }
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

        // 全量快照广播（Version 全局单调递增；空数组=清空，重广播也递增让客户端无条件替换）
        private static void BroadcastAssignments()
        {
            version++;
            if (!RoomManager.IsSingletonActive() || !NetworkServer.active) return;
            var uids = new List<long>(assigned.Count);
            var roleJsons = new List<string>(assigned.Count);
            foreach (var pair in assigned)
            {
                uids.Add((long)pair.Key);
                roleJsons.Add(BuildRoleJsonSafe(pair.Value));
            }
            RoomManager.Instance.BroadcastToAllExceptHost(
                new MoleSelectMessage { Version = version, Uids = uids.ToArray(), RoleJsons = roleJsons.ToArray() }, 0);
            // 诊断：条数变化必打，稳定重广播每 60 秒打一次；回读=用与客机同一套 JsonUtility 解析首条角色，失败即客机也必然失败
            if (uids.Count != lastBroadcastCount || Time.unscaledTime >= nextBroadcastLog)
            {
                lastBroadcastCount = uids.Count;
                nextBroadcastLog = Time.unscaledTime + 60f;
                string roundtrip = "无条目";
                if (roleJsons.Count > 0)
                {
                    try
                    {
                        RoleDef check = JsonUtility.FromJson<RoleDef>(roleJsons[0]);
                        roundtrip = check != null ? "OK(id=" + check.id + ")" : "null";
                    }
                    catch (Exception e) { roundtrip = "抛异常:" + e.Message; }
                }
                Log("[状态] 广播角色快照 v" + version + "：" + uids.Count + " 条，回读=" + roundtrip
                    + (uids.Count > 0 && roleJsons.Exists(string.IsNullOrEmpty) ? "（异常：有条目但角色 JSON 为空！）" : ""));
            }
        }

        private static int lastBroadcastCount = -1;
        private static float nextBroadcastLog;

        // 单个角色的 JSON：纯字段类，JsonUtility 可靠（BioChemConfig 同型已验证）；异常时手工拼装兜底
        private static string BuildRoleJsonSafe(RoleDef role)
        {
            string json = null;
            try { json = JsonUtility.ToJson(role); } catch { }
            if (string.IsNullOrEmpty(json)) json = BuildRoleJson(role);
            return json;
        }

        // 手工拼 RoleDef：字段名/顺序与 JsonUtility 一致，仅作 BuildRoleJsonSafe 的兜底；
        // float 必须固定用不变文化输出（区域设置带逗号小数点的机器会拼出非法 JSON）
        private static string BuildRoleJson(RoleDef role)
        {
            if (role == null) return "{}";
            var sb = new System.Text.StringBuilder(256);
            sb.Append("{\"id\":").Append(EncodeJsonString(role.id));
            sb.Append(",\"name\":").Append(EncodeJsonString(role.name));
            sb.Append(",\"count\":").Append(role.count);
            sb.Append(",\"trickMultiplier\":").Append(JsonFloat(role.trickMultiplier));
            sb.Append(",\"canBroadcast\":").Append(role.canBroadcast ? "true" : "false");
            sb.Append(",\"canPush\":").Append(role.canPush ? "true" : "false");
            sb.Append(",\"scoreGraceSeconds\":").Append(JsonFloat(role.scoreGraceSeconds));
            sb.Append(",\"musicCooldownSeconds\":").Append(JsonFloat(role.musicCooldownSeconds));
            sb.Append(",\"pushStunSeconds\":").Append(JsonFloat(role.pushStunSeconds));
            sb.Append(",\"pushCooldownSeconds\":").Append(JsonFloat(role.pushCooldownSeconds));
            sb.Append(",\"pushRange\":").Append(JsonFloat(role.pushRange));
            sb.Append(",\"pushBehindDot\":").Append(JsonFloat(role.pushBehindDot));
            sb.Append(",\"progressPenaltyFraction\":").Append(JsonFloat(role.progressPenaltyFraction));
            sb.Append(",\"immunityIdleSeconds\":").Append(JsonFloat(role.immunityIdleSeconds));
            sb.Append(",\"maxImmunityCharges\":").Append(role.maxImmunityCharges);
            sb.Append(",\"speedBoostUnseen\":").Append(JsonFloat(role.speedBoostUnseen));
            sb.Append(",\"speedPenaltySeen\":").Append(JsonFloat(role.speedPenaltySeen));
            sb.Append(",\"actionSpeedMultiplier\":").Append(JsonFloat(role.actionSpeedMultiplier));
            sb.Append(",\"sightAngleDegrees\":").Append(JsonFloat(role.sightAngleDegrees));
            sb.Append(",\"sightDistance\":").Append(JsonFloat(role.sightDistance));
            return sb.ToString();
        }

        // float 必须固定用不变文化输出（区域设置带逗号小数点的机器会拼出非法 JSON）
        private static string JsonFloat(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return "0";
            return value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        // JSON 字符串字面量（含首尾引号）：只转义反斜杠/双引号/控制字符，中文原样输出
        private static string EncodeJsonString(string value)
        {
            if (value == null) return "\"\"";
            var sb = new System.Text.StringBuilder(value.Length + 8);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static void OnClassStateChanged(EClassroomState state)
        {
            if (state != EClassroomState.Classing) AthleteSprint.Reset();
            // 每节课开始重选；下课/课前清空并通知全员撤掉显示
            // 冷却也一并清零：上局下课触发的 StopMusicServer 会残留冷却，不清会让新局开场无法广播
            if (state == EClassroomState.Prepare)
            {
                assigned.Clear();
                progressByUid.Clear();
                pushCooldownUntil.Clear();
                immunityIdle.Clear();
                immunityCharges.Clear();
                athleteSightCache.Clear();
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
                immunityIdle.Clear();
                immunityCharges.Clear();
                athleteSightCache.Clear();
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
            // 模型未绑定的过渡帧（NPC 老师异步加载等）连 transform 都没有，位置/朝向都不可信
            Transform teacherRoot = teacher.transform;
            if (teacherRoot == null || student.transform == null) return false;
            Vector3 toStudent = student.Position - teacher.Position;
            toStudent.y = 0f;
            if (toStudent.magnitude > role.pushRange) return false;
            Vector3 fwd = teacherRoot.forward;
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

        // ===== 免伤（书呆子类角色）：静心一段时间获得一次"免疫老师攻击" =====

        // 各端本地推进静心计时；服务端负责消费，获得/消耗都靠广播对齐
        private static void TickImmunity(float dt)
        {
            if (assigned.Count == 0) return;
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            foreach (var pair in assigned)
            {
                RoleDef role = pair.Value;
                if (!RoleHasImmunity(role)) continue;
                ulong uid = pair.Key;
                int cap = Mathf.Max(1, role.maxImmunityCharges);
                int charges = immunityCharges.TryGetValue(uid, out int c) ? c : 0;
                if (charges >= cap) continue;
                float idle = (immunityIdle.TryGetValue(uid, out float v) ? v : 0f) + dt;
                if (idle < role.immunityIdleSeconds)
                {
                    immunityIdle[uid] = idle;
                    continue;
                }
                immunityIdle[uid] = 0f;
                immunityCharges[uid] = charges + 1;
                if (NetRuntime.IsServer) BroadcastImmunity(MoleImmunityMessage.Gained, uid);
            }
        }

        // 服务端：消耗一次免伤；返回 true 表示调用方应拦下这次老师攻击
        private static bool TryConsumeImmunity(ulong studentUID)
        {
            if (!NetRuntime.IsServer) return false;
            if (!assigned.TryGetValue(studentUID, out RoleDef role) || !RoleHasImmunity(role)) return false;
            int charges = immunityCharges.TryGetValue(studentUID, out int c) ? c : 0;
            if (charges <= 0) return false;
            immunityCharges[studentUID] = charges - 1;
            immunityIdle[studentUID] = 0f;
            BroadcastImmunity(MoleImmunityMessage.Consumed, studentUID);
            Log("[状态] " + role.name + "免疫了一次老师攻击（剩余免伤 " + (charges - 1) + " 次）");
            return true;
        }

        private static void BroadcastImmunity(byte kind, ulong uid)
        {
            var message = new MoleImmunityMessage { Kind = kind, StudentUID = uid };
            if (RoomManager.IsSingletonActive() && NetworkServer.active)
                RoomManager.Instance.BroadcastToAllExceptHost(message, 0);
            ApplyImmunity(message);
        }

        private static void ApplyImmunity(MoleImmunityMessage message)
        {
            ulong uid = message.StudentUID;
            if (message.Kind == MoleImmunityMessage.Gained)
            {
                if (assigned.TryGetValue(uid, out RoleDef role) && RoleHasImmunity(role))
                    immunityCharges[uid] = Mathf.Max(1, role.maxImmunityCharges);
                immunityIdle[uid] = 0f;
                return;
            }
            immunityCharges[uid] = 0;
            immunityIdle[uid] = 0f;
            if (!MonoSingletonT<GameNotifyManager>.IsSingletonActive()) return;
            MonoSingletonT<GameNotifyManager>.Instance.ShowCommonNotify("好学生下手要轻点啊！", null, 3f);
        }

        // ===== 老师攻击拦截（仅服务端；拦下后全员都不会进入被抓/眩晕流程） =====

        // 抓捕（skillID=3）：OnUseSkillMessageRequest 是唯一的服务端入口
        public static bool BeforeUseSkillRequest(UseSkillMessageRequest_C2S request)
        {
            if (!NetRuntime.IsServer || request.skillID != 3) return true;
            if (!IsValidCatch(request.userUID, request.targetUID)) return true;
            return !TryConsumeImmunity(request.targetUID);
        }

        // 复刻原方法的关键校验，避免伪造请求白吃掉书呆子的免伤
        private static bool IsValidCatch(ulong teacherUID, ulong studentUID)
        {
            if (!UnitManager.IsSingletonActive()) return false;
            Unit teacher = UnitManager.Instance.GetUnitByUID(teacherUID);
            Unit student = UnitManager.Instance.GetUnitByUID(studentUID);
            return teacher != null && student != null && teacher != student
                && teacher.isActived && student.isActived && teacher.IsTeacher && student.IsStudent
                && !teacher.IsDead && !student.IsDead;
        }

        // 粉笔/剑气/橡皮擦：房主端直接结算时走 GetStun（香蕉皮滑倒不属于老师攻击）
        public static bool BeforeStudentStun(StudentStunComponent __instance, EStunType stunType)
        {
            if (!NetRuntime.IsServer || stunType == EStunType.Banana) return true;
            Unit owner = __instance.owner;
            if (owner == null || !owner.IsStudent) return true;
            return !TryConsumeImmunity(owner.UID);
        }

        // 客机老师客户端命中后上报的眩晕请求，服务端广播前拦下
        public static bool BeforeStudentStunRequest(StudentGetStunRequest_C2S request)
        {
            if (!request.isStun || request.stunType == EStunType.Banana) return true;
            return !TryConsumeImmunity(request.unitUID);
        }

        // ===== 体育生：冲刺、视线内外移速 + 进食/救人提速 =====

        private static readonly Dictionary<ulong, (int frame, bool seen)> athleteSightCache = new Dictionary<ulong, (int, bool)>();
        private static RaycastHit[] sightHits = new RaycastHit[16];

        // 体育生加成只覆盖对局窗口（Prepare/Classing）：下课、回大厅、中途退出后 assigned 残留时不再加成
        private static bool InClassWindow()
        {
            if (!ClassroomManager.IsSingletonActive()) return false;
            EClassroomState state = ClassroomManager.Instance.curState;
            return state == EClassroomState.Prepare || state == EClassroomState.Classing;
        }

        // 移速唯一出口在本地玩家自己的 InputComponent 上（各端算各端的人物，无需广播）
        public static void AfterMoveSpeed(InputComponent __instance, ref float __result)
        {
            if (__result <= 0f) return;
            if (!InClassWindow()) return;
            Unit owner = __instance.owner;
            if (owner == null || !owner.isLocalPlayer || owner.IsDead || !owner.IsStudent) return;
            if (!assigned.TryGetValue(owner.UID, out RoleDef role)) return;
            if (AthleteSprint.HasSkill(role) && AthleteSprint.IsActiveFor(owner.UID))
            {
                __result *= AthleteSprint.SpeedMultiplier;
                return;
            }
            if (role.speedBoostUnseen <= 0f && role.speedPenaltySeen <= 0f) return;
            if (IsSeenByAnyTeacher(owner, role))
            {
                if (role.speedPenaltySeen > 0f) __result *= role.speedPenaltySeen;
            }
            else if (role.speedBoostUnseen > 0f)
            {
                __result *= role.speedBoostUnseen;
            }
        }

        // 进食节奏：仅加速"按住吃东西"的进度累积，墨水/手机等其他按住使用物不受影响
        public static void BeforeItemUseTick(StudentItemUseComponent __instance, bool ____isHoldingUse, ref float iDt)
        {
            if (!____isHoldingUse || iDt <= 0f) return;
            if (!InClassWindow()) return;
            Unit owner = __instance.owner;
            if (owner == null || !owner.isLocalPlayer) return;
            Unit held = __instance.currentHoldUnit;
            if (held == null || (held.GetComponent<FoodComponent>() == null && held.GetComponent<HotPotIngredientComponent>() == null)) return;
            if (!assigned.TryGetValue(owner.UID, out RoleDef role) || role.actionSpeedMultiplier <= 0f) return;
            iDt *= role.actionSpeedMultiplier;
        }

        // 救人时长：手动救援与无人机救援共用 GetRescueDuration，按倍率缩短即提速
        public static void AfterRescueDuration(ref float __result)
        {
            if (__result <= 0f) return;
            if (!InClassWindow()) return;
            if (!assigned.TryGetValue(UnitManager.Instance.localPlayerUID, out RoleDef role) || role.actionSpeedMultiplier <= 0f) return;
            __result /= role.actionSpeedMultiplier;
        }

        // 供提示盒查询本地玩家当前是否被老师看见（结果缓存到帧）
        internal static bool IsSeenByAnyTeacher(Unit student, RoleDef role)
        {
            int frame = Time.frameCount;
            if (athleteSightCache.TryGetValue(student.UID, out var cached) && cached.frame == frame) return cached.seen;
            bool seen = ComputeTeacherSight(student, role);
            athleteSightCache[student.UID] = (frame, seen);
            return seen;
        }

        // 视线口径借鉴 StudentNPCComponent 的师生对视检测：任一存活未眩晕的老师，
        // 头顶点距离内 + 与老师朝向夹角在锥内 + 头顶连线无墙体遮挡
        private static bool ComputeTeacherSight(Unit student, RoleDef role)
        {
            if (!UnitManager.IsSingletonActive()) return false;
            // LogicObject.transform 在模型未绑定/已回收时返回 null（NPC 老师模型异步加载的空窗期常见），
            // 此时连 forward 都取不到，必须整段跳过
            Transform studentRoot = student.transform;
            if (studentRoot == null) return false;
            Transform studentHead = student.GetPartRoot(EUnitPartType.HeadPosition);
            Vector3 studentPos = studentHead != null ? studentHead.position : student.Position + Vector3.up;
            float halfAngle = Mathf.Max(1f, role.sightAngleDegrees > 0f ? role.sightAngleDegrees : 110f) * 0.5f * Mathf.Deg2Rad;
            float cosHalf = Mathf.Cos(halfAngle);
            float maxDistance = role.sightDistance > 0f ? role.sightDistance : 10f;
            // 场景切换 teardown 期间单例可能被 Instance getter 重建但 OnInit 未跑（unitsDict 仍为 null）
            var units = UnitManager.Instance.unitsDict;
            if (units == null) return false;
            foreach (Unit teacher in units.Values)
            {
                if (teacher == null || teacher == student) continue;
                if (!teacher.isActived || teacher.IsDead || !teacher.IsTeacher || teacher.IsStun) continue;
                Transform teacherRoot = teacher.transform;
                if (teacherRoot == null) continue; // isActived 已置位但 model 还没绑定的过渡帧
                Transform teacherHead = teacher.GetPartRoot(EUnitPartType.HeadPosition);
                Vector3 origin = teacherHead != null ? teacherHead.position : teacher.Position + Vector3.up;
                Vector3 delta = studentPos - origin;
                float distance = delta.magnitude;
                if (distance < 0.01f || distance > maxDistance) continue;
                if (Vector3.Dot(teacherRoot.forward, delta / distance) < cosHalf) continue;
                if (HasObstacleBetween(origin, delta, distance, teacherRoot, studentRoot)) continue;
                return true;
            }
            return false;
        }

        // 命中师生双方自身碰撞体的不算遮挡（其余任意碰撞体挡住视线即不可见）
        private static bool HasObstacleBetween(Vector3 origin, Vector3 delta, float distance, Transform teacherRoot, Transform studentRoot)
        {
            Vector3 direction = delta.normalized;
            int count;
            while ((count = Physics.RaycastNonAlloc(origin, direction, sightHits, distance, -5, QueryTriggerInteraction.Ignore)) == sightHits.Length)
            {
                Array.Resize(ref sightHits, sightHits.Length * 2);
            }
            for (int i = 0; i < count; i++)
            {
                Transform hit = sightHits[i].transform;
                if (hit == null) continue;
                if (!hit.IsChildOf(teacherRoot) && !hit.IsChildOf(studentRoot)) return true;
            }
            return false;
        }

        // ===== 消息注册与处理 =====

        public static void RegisterClient()
        {
            NetworkClient.RegisterHandler<MoleSelectMessage>(OnMoleSelect, true);
            NetworkClient.RegisterHandler<MoleMusicMessage>(OnClientMusic, true);
            NetworkClient.RegisterHandler<MolePushMessage>(OnClientPush, true);
            NetworkClient.RegisterHandler<MoleImmunityMessage>(OnClientImmunity, true);
            NetworkClient.RegisterHandler<NerdRescueScoreMessage>(message =>
            {
                if (!NetworkServer.active) ApplyNerdRescueScore(message.RescuerUID);
            }, true);
            AthleteSprint.RegisterClient();
        }

        public static void RegisterServer()
        {
            NetworkServer.RegisterHandler<MoleMusicMessage>(OnServerMusic, true);
            NetworkServer.RegisterHandler<MolePushMessage>(OnServerPush, true);
            AthleteSprint.RegisterServer();
        }

        private static void OnMoleSelect(MoleSelectMessage message)
        {
            if (NetworkServer.active) return;
            version = message.Version;
            int count = message.Uids?.Length ?? 0;
            assigned.Clear();
            for (int i = 0; i < count; i++)
            {
                ulong uid = (ulong)message.Uids[i];
                if (uid == 0) continue;
                string roleJson = i < (message.RoleJsons?.Length ?? 0) ? message.RoleJsons[i] : null;
                RoleDef role = ParseRole(roleJson);
                if (role != null) assigned[uid] = role;
            }
            if (!AthleteSprint.HasSkill(MyRole)) AthleteSprint.ClearLocal();
            // 诊断：条数/应用数任一变化必打（同内容重广播不打防刷屏）
            if (count != lastLoggedEntryCount || assigned.Count != lastLoggedApplied)
            {
                lastLoggedEntryCount = count;
                lastLoggedApplied = assigned.Count;
                Log("[状态] 角色同步 v" + message.Version + "：收到 " + count + " 条 → 应用 " + assigned.Count + " 条"
                    + (count > 0 && assigned.Count == 0 ? "（异常：有条目但角色全解析失败！）" : "")
                    + (count == 0 ? "（清场）" : ""));
            }
            // 失去角色的捣蛋累计与免伤状态一并清掉（本地 UI 口径）
            var stale = new HashSet<ulong>();
            foreach (ulong uid in progressByUid.Keys)
                if (!assigned.ContainsKey(uid)) stale.Add(uid);
            foreach (ulong uid in immunityIdle.Keys)
                if (!assigned.ContainsKey(uid)) stale.Add(uid);
            foreach (ulong uid in immunityCharges.Keys)
                if (!assigned.ContainsKey(uid)) stale.Add(uid);
            foreach (ulong uid in athleteSightCache.Keys)
                if (!assigned.ContainsKey(uid)) stale.Add(uid);
            foreach (ulong uid in stale)
            {
                progressByUid.Remove(uid);
                immunityIdle.Remove(uid);
                immunityCharges.Remove(uid);
                athleteSightCache.Remove(uid);
            }
        }

        private static int lastLoggedEntryCount = -1;
        private static int lastLoggedApplied = -1;

        private static string lastBadRolePrefix;

        private static RoleDef ParseRole(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json)) return null;
                RoleDef role = JsonUtility.FromJson<RoleDef>(json);
                if (role == null && json != lastBadRolePrefix)
                {
                    lastBadRolePrefix = json;
                    Log("[警告] 角色条目解析为 null：len=" + json.Length + "，前缀：" + json.Substring(0, Math.Min(160, json.Length)));
                }
                return role;
            }
            catch (Exception e)
            {
                string prefix = json != null ? json.Substring(0, Math.Min(160, json.Length)) : "null";
                if (prefix != lastBadRolePrefix)
                {
                    lastBadRolePrefix = prefix;
                    Log("[警告] 角色条目解析异常：" + e.Message + "，前缀：" + prefix);
                }
                return null;
            }
        }

        // 客机：收到免伤状态变更（获得/消耗），本地对齐并弹提示
        private static void OnClientImmunity(MoleImmunityMessage message)
        {
            if (NetworkServer.active) return;
            ApplyImmunity(message);
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
            TickImmunity(Time.unscaledDeltaTime);
            MuteLowLevelChannels(musicPlaying);
            TickMusicPlayback();
            AthleteSprint.Tick();
            HandleRoleBKey();
            HandlePushKey();
        }

        // 课程进行中（Prepare/Classing）：角色配置面板锁定编辑（分配在上课瞬间定型）
        internal static bool ClassInProgress()
        {
            if (!ClassroomManager.IsSingletonActive()) return false;
            var state = ClassroomManager.Instance.curState;
            return state == EClassroomState.Prepare || state == EClassroomState.Classing;
        }

        // B 键按角色分派：体育生冲刺，其他持有广播技能的角色开关广播。
        private static void HandleRoleBKey()
        {
            if (!UnitManager.IsSingletonActive()) return;
            ulong self = UnitManager.Instance.localPlayerUID;
            if (!assigned.TryGetValue(self, out RoleDef role)) return;
            bool isAthlete = AthleteSprint.HasSkill(role);
            if (!isAthlete && !role.canBroadcast) return;
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            Unit player = UnitManager.Instance.GetUnitByUID(self);
            if (IsPunished(player)) return; // 被挂柱禁用技能
            if (!MonoSingletonT<InputManager>.IsSingletonActive() || MonoSingletonT<InputManager>.Instance.FrozedPlayerInput || MonoSingletonT<InputManager>.Instance.FrozedAllInput) return;
            if (!ReInput.isReady) return;
            Keyboard keyboard = ReInput.players.GetPlayer(0).controllers.Keyboard;
            if (keyboard == null || !keyboard.GetKeyDown(RoleBKey)) return;
            if (isAthlete)
            {
                AthleteSprint.Request(player);
                return;
            }
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
            AthleteSprint.Reset();
            if (registeredStateHook) MsgCenter.UnRegister(ClassStateChangedMsgId);
            registeredStateHook = false;
        }
    }
}
