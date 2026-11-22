using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using Rewired;
using WorstClassEver.ModAPI;
using UnityEngine;

namespace TeacherReward
{
    /// <summary>
    /// 老师奖励：一节课内最多 3 个奖励点（每个老师各自计）——
    /// ①第一次把真人学生挂上惩罚柱（挂柱老师 +1）②全班捣蛋进度到达目标一半（当时全体老师各 +1）
    /// ③老师本人第一次被晕倒（任何原因，被晕者 +1）。
    /// 点数服务端（房主）权威：自定义 Mirror 消息全量快照同步 + 每 5s 定时重广播自愈。
    /// 有剩余点数按 H 打开奖励面板选技能卡：突击检查（即时）/ 健步如飞、重磅粉笔（常驻到下课）/ 稳如泰山（护盾），
    /// 拖堂、整顿纪律为占位卡。
    /// </summary>
    /// <summary>快照数据直接放 Mirror 结构体原生数组逐条序列化（本机引擎 JsonUtility 会静默跳过 List&lt;自定义类&gt; 字段，不碰 JSON 包装）。</summary>
    public struct TeacherRewardMessage : NetworkMessage
    {
        public const byte KindState = 1;  // S2C 全量快照（变更即时 + 每 5s 重广播）
        public const byte KindSelect = 3; // C2S 选卡请求
        public byte Kind;
        public uint Version;
        public ulong TeacherUID;   // 定向提示目标（仅自己可见的提示；0=无目标）
        public string Notify;      // 定向提示文案（只随本次广播，定时重广播恒空）
        public string CardId;      // Kind=3：要使用的技能卡 id
        public long[] Uids;        // Kind=1：老师 UID 列表
        public int[] Points;       // 剩余奖励点（与 Uids 一一对应）
        public int[] Masks;        // 已获触发位掩码（与 Uids 一一对应）
        public int[] Buffs;        // 技能卡 buff 位掩码（与 Uids 一一对应）
        public string[] CardLists; // 每位老师已用技能卡 id，'|' 分隔
    }

    /// <summary>技能卡定义。写死在代码里；新增卡时在此扩展 + ModEntry.TryApplyCardEffect 分派效果。</summary>
    internal sealed class RewardCardDef
    {
        public string Id;
        public string Name;
        public string Desc;
    }

    internal sealed class RewardState
    {
        public int PointsLeft;
        public int EarnedMask; // bit0=挂柱 bit1=捣蛋半程 bit2=被晕
        public int BuffMask;   // bit0=健步如飞 bit1=重磅粉笔 bit2=护盾
        public List<string> Cards = new List<string>();
    }

    public sealed class ModEntry : IMod
    {
        internal static Action<string> Log;

        private const string HarmonyId = "worstclass.teacherreward";
        private const int ClassStateChangedMsgId = 301; // OnClassroomStateChanged（各端广播）
        internal const int MaxPoints = 3; // 三个触发点各 1 次

        // 触发位掩码（RewardState.EarnedMask 用）
        private const int ReasonBind = 1;      // 第一次挂真人学生上柱
        private const int ReasonTrickHalf = 2; // 捣蛋进度到达一半
        private const int ReasonStunned = 4;   // 老师本人第一次被晕倒

        // 技能卡 buff 位掩码（RewardState.BuffMask 用；随快照同步，到下课或被消耗为止）
        internal const int BuffSwift = 1;  // 健步如飞：移速 ×swiftSpeedMultiplier
        internal const int BuffChalk = 2;  // 重磅粉笔：粉笔/教鞭晕眩时长 +chalkStunBonus
        internal const int BuffShield = 4; // 稳如泰山：护盾 1 层，挡下一次晕倒

        // 技能卡 id（面板按钮名与效果分派共用）
        internal const string CardSurprise = "card_3"; // 突击检查
        internal const string CardSwift = "card_4";    // 健步如飞
        internal const string CardChalk = "card_5";    // 重磅粉笔
        internal const string CardShield = "card_6";   // 稳如泰山

        internal static readonly RewardCardDef[] Cards =
        {
            new RewardCardDef { Id = "card_1", Name = "拖堂", Desc = "占位卡：效果待设计" },
            new RewardCardDef { Id = "card_2", Name = "整顿纪律", Desc = "占位卡：效果待设计" },
            new RewardCardDef { Id = CardSurprise, Name = "突击检查", Desc = "随机一名真人学生当场被晕眩" },
            new RewardCardDef { Id = CardSwift, Name = "健步如飞", Desc = "移动速度 ×1.15，直到下课" },
            new RewardCardDef { Id = CardChalk, Name = "重磅粉笔", Desc = "粉笔/教鞭晕眩时长 +30%，直到下课" },
            new RewardCardDef { Id = CardShield, Name = "稳如泰山", Desc = "获得护盾：挡下一次晕倒（直到被消耗）" },
        };

        // 常驻卡描述带数值，配置加载后重算（面板开时读的是这份）
        private static void RefreshCardDescs()
        {
            foreach (RewardCardDef card in Cards)
            {
                if (card.Id == CardSwift)
                    card.Desc = "移动速度 ×" + RewardConfig.Data.swiftSpeedMultiplier.ToString("0.##") + "，直到下课";
                else if (card.Id == CardChalk)
                    card.Desc = "粉笔/教鞭晕眩时长 +" + Mathf.RoundToInt(RewardConfig.Data.chalkStunBonus * 100f) + "%，直到下课";
            }
        }

        // 点数状态：服务端权威读写，客机整体替换自快照。
        // 不做 version 门槛（静态 version 跨房间不重置，全量替换杜绝重连残留/错过清场的漂移）
        internal static readonly Dictionary<ulong, RewardState> states = new Dictionary<ulong, RewardState>();
        private static uint version;
        private static float nextRepublish;
        private static bool trickHalfGranted; // 本节课捣蛋半程已发过（服务端 latch，分跌回去不重发）
        private static bool registeredStateHook;

        // 选卡等待：本端发出选卡请求时记下当前点数，快照回来点数减少 = 服务端接受 → 关面板；
        // 点数没变 = 被拒（定向提示已弹，面板留着让老师重选）
        private static bool pendingSelect;
        private static int pendingPoints;
        private static float pendingDeadline;

        private static readonly MsgHooks Hooks = new MsgHooks();

        private sealed class MsgHooks
        {
            public void OnClassStateChanged(EClassroomState state) => ModEntry.OnClassStateChanged(state);
        }

        public void OnLoad(ModContext context)
        {
            Log = context.Log;
            RewardConfig.Load(context.ConfigDirectory, Log);
            RefreshCardDescs();
            var harmony = new Harmony(HarmonyId);
            try
            {
                // ① 挂柱：绑定响应真正应用后（柱组件 BoundStudentUID 已写入）判定被挂的是真人学生 → 给挂柱老师发点
                harmony.Patch(
                    AccessTools.Method(typeof(SkillMessageHandler), "OnTeacherBindStudentMessageResponse"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterBindStudentResponse"));
                // ② 捣蛋半程：唯一计分入口累计后判定过半（全体老师各 +1，latch 到下节课重置）
                harmony.Patch(
                    AccessTools.Method(typeof(ClassroomManager), "OnResponseTrickCountData"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterTrickCount"));
                // ③ 老师被晕：TeacherStunComponent.ApplyStun 是老师晕眩的统一入口
                //   （香蕉滑倒 ApplySlip→GetSlip、吹箭 TryGetProjectileStun、其余原因全汇到这里；
                //    香蕉滑倒是 S2C 广播各端都跑，房主进程能看到远程老师的晕倒）
                harmony.Patch(
                    AccessTools.Method(typeof(TeacherStunComponent), "ApplyStun"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterTeacherStun"));
                // 健步如飞：本地玩家移速唯一出口（TrickMole 体育生同款通道；移速本地结算，位置随网络同步）
                harmony.Patch(
                    AccessTools.Method(typeof(InputComponent), "GetCurrentMoveSpeed"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterMoveSpeed"));
                // 重磅粉笔：粉笔/教鞭晕眩时长唯一来源（DealStunEffect 各端本地计算，全端一致加成才不漂移）
                harmony.Patch(
                    AccessTools.Method(typeof(StudentNumBalanceConfig), "GetTeacherAttackStunDuration"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterAttackStunDuration"));
                // 稳如泰山护盾：老师被晕的两个原生入口——香蕉滑倒（整段效果含镜头一并拦）、吹箭晕眩
                harmony.Patch(
                    AccessTools.Method(typeof(BananaComponent), "ApplySlip"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeBananaSlip"));
                harmony.Patch(
                    AccessTools.Method(typeof(TeacherStunComponent), "TryGetProjectileStun"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeTeacherProjectileStun"));
                // 消息注册挂到游戏自己的注册时机之后（防重注册清掉）
                harmony.Patch(AccessTools.Method(typeof(RoomManager), "RegisterClientMessageHandlers"), postfix: new HarmonyMethod(typeof(ModEntry), "RegisterClient"));
                harmony.Patch(AccessTools.Method(typeof(RoomManager), "RegisterServerMessageHandlers"), postfix: new HarmonyMethod(typeof(ModEntry), "RegisterServer"));
                Writer<TeacherRewardMessage>.write = TeacherRewardPump.WriteReward;
                Reader<TeacherRewardMessage>.read = TeacherRewardPump.ReadReward;
                RegisterClient();
                RegisterServer();

                // 课堂状态：进 Prepare 重置本节课奖励点并广播清场快照
                MsgCenter.Register<EClassroomState>(ClassStateChangedMsgId, Hooks.OnClassStateChanged);
                registeredStateHook = true;

                var pump = new GameObject("老师奖励");
                UnityEngine.Object.DontDestroyOnLoad(pump);
                pump.AddComponent<TeacherRewardPump>();
            }
            catch
            {
                harmony.UnpatchAll(HarmonyId);
                throw;
            }
            Log("[状态] 老师奖励 Mod 已加载：" + Cards.Length + " 张技能卡（突击检查/健步如飞/重磅粉笔/稳如泰山已实装，拖堂/整顿纪律占位）"
                + "（挂真人柱 / 捣蛋过半 / 被晕倒各 1 点），面板热键 " + RewardConfig.PanelKeyCode);
        }

        // ===== 三个触发点（全部服务端权威 + 上课中才有效） =====

        // ① 挂柱响应真正应用（BoundStudentUID 已写入柱组件）才判定：被挂的是真人学生 → 挂柱老师 +1
        private static void AfterBindStudentResponse(TeacherBindStudentMessageResponse_S2C response)
        {
            if (!NetRuntime.IsServer || response.studentUID == 0 || response.teacherUID == 0) return;
            if (!MonoSingletonT<UnitManager>.IsSingletonActive()) return;
            Unit pillar = MonoSingletonT<UnitManager>.Instance.GetUnitByUID(response.pillarUID);
            PunishPillarComponent pillarComp = pillar != null ? pillar.GetComponent<PunishPillarComponent>() : null;
            if (pillarComp == null || pillarComp.BoundStudentUID != response.studentUID) return; // 过期响应/绑定未生效
            Unit student = MonoSingletonT<UnitManager>.Instance.GetUnitByUID(response.studentUID);
            if (student == null || !student.isPlayer || !student.IsStudent) return; // 只认真人学生（NPC 学生不算）
            Grant(response.teacherUID, ReasonBind, "首次将真人学生挂上惩罚柱");
        }

        // ② 捣蛋进度过半（目标值的一半；一次性 latch）
        private static void AfterTrickCount(ClassroomManager __instance)
        {
            if (!NetRuntime.IsServer || trickHalfGranted) return;
            float target = __instance.StudentTargetScore;
            if (target <= 0f || __instance.currentTrickCount < target * 0.5f) return;
            trickHalfGranted = true;
            Log("[状态] 捣蛋进度过半：" + __instance.currentTrickCount.ToString("0.#") + " / " + target.ToString("0.#"));
            GrantAllTeachers(ReasonTrickHalf, "捣蛋进度到达一半");
        }

        // ③ 老师本人被晕倒（任何原因；被晕的老师自己 +1）
        private static void AfterTeacherStun(TeacherStunComponent __instance)
        {
            if (!NetRuntime.IsServer) return;
            Unit owner = __instance.owner;
            if (owner == null || !owner.isPlayer || !owner.IsTeacher) return; // AI 老师不算
            Grant(owner.UID, ReasonStunned, "第一次被晕倒");
        }

        // ===== 技能卡效果（健步如飞 / 重磅粉笔 / 稳如泰山） =====

        // 健步如飞：持有者本机移速 ×倍率（移速本地结算，位置随网络同步；体育生同款通道）
        public static void AfterMoveSpeed(InputComponent __instance, ref float __result)
        {
            if (__result <= 0f || !ClassActive()) return;
            Unit owner = __instance.owner;
            if (owner == null || !owner.isLocalPlayer || owner.IsDead || !owner.IsTeacher) return;
            if (!states.TryGetValue(owner.UID, out RewardState st) || (st.BuffMask & BuffSwift) == 0) return;
            __result *= RewardConfig.Data.swiftSpeedMultiplier;
        }

        // 重磅粉笔：任意老师持有 buff 即加成（DealStunEffect 在各端本地计算时长，全局判定保证各端一致；
        // AI 老师的粉笔也会被加长，属可接受偏差）
        public static void AfterAttackStunDuration(ref float __result)
        {
            if (__result <= 0f || !ClassActive()) return;
            if (!AnyTeacherHasBuff(BuffChalk)) return;
            __result *= 1f + RewardConfig.Data.chalkStunBonus;
        }

        // 稳如泰山：香蕉滑倒——整段效果（含滑倒镜头）一并拦下。
        // 注意原生 GetSlip 有 projectileStun 守卫：正被吹箭晕着时滑香蕉本来就不会再晕，
        // 这种情况护盾不该被消耗（不能用 IsStunned 判断——滑倒晕眩中滑第二根香蕉原生会重新晕，护盾应生效）
        private static readonly System.Reflection.FieldInfo ProjectileStunField =
            AccessTools.Field(typeof(TeacherStunComponent), "projectileStun");

        public static bool BeforeBananaSlip(BananaSlip_S2C message)
        {
            if (!ClassActive() || !MonoSingletonT<UnitManager>.IsSingletonActive()) return true;
            Unit unit = MonoSingletonT<UnitManager>.Instance.GetUnitByUID(message.playerUID);
            TeacherStunComponent comp = unit != null ? unit.GetComponent<TeacherStunComponent>() : null;
            if (comp != null && ProjectileStunField != null
                && ProjectileStunField.GetValue(comp) is bool projectileStun && projectileStun)
                return true; // 原生本来就不会晕（正被吹箭晕眩中），护盾不消耗
            return ConsumeShieldIfNeeded(unit);
        }

        // 稳如泰山：吹箭晕眩——返回 false = 未被晕（调用方按"晕眩未应用"处理）。
        // 必须先镜像原方法的拒绝条件（IsStunned/duration 非法时原方法本来就不晕），
        // 否则已晕老师被吹箭再蹭一下、或房主拦截后 ApplyHit 的 duration=0 二次调用都会白耗护盾
        public static bool BeforeTeacherProjectileStun(TeacherStunComponent __instance, float duration)
        {
            if (!ClassActive()) return true;
            if (__instance == null || __instance.IsStunned
                || duration <= 0f || float.IsNaN(duration) || float.IsInfinity(duration)) return true;
            return ConsumeShieldIfNeeded(__instance.owner);
        }

        // 有护盾 → 全端本地消耗并拦下晕眩；服务端广播定向提示（各端随 ≤5s 快照对齐）
        private static bool ConsumeShieldIfNeeded(Unit owner)
        {
            if (owner == null || !owner.isPlayer || !owner.IsTeacher) return true;
            if (!states.TryGetValue(owner.UID, out RewardState st) || (st.BuffMask & BuffShield) == 0) return true;
            st.BuffMask &= ~BuffShield;
            if (NetRuntime.IsServer)
            {
                Log("[状态] 护盾消耗：" + NameOf(owner.UID) + " 挡下了一次晕倒");
                BroadcastState("护盾挡下了一次晕倒！", owner.UID);
            }
            return false; // 跳过原方法 = 晕眩未发生
        }

        private static bool AnyTeacherHasBuff(int bit)
        {
            // 只看当前在线老师：已退出玩家的残留条目不该继续影响全场（中途退房边缘情况）
            if (!MonoSingletonT<UnitManager>.IsSingletonActive()) return false;
            foreach (Unit player in MonoSingletonT<UnitManager>.Instance.playerList)
            {
                if (player == null || !player.isPlayer || !player.IsTeacher) continue;
                if (states.TryGetValue(player.UID, out RewardState st) && (st.BuffMask & bit) != 0) return true;
            }
            return false;
        }

        internal static bool LocalHasBuff(int bit)
        {
            if (!MonoSingletonT<UnitManager>.IsSingletonActive()) return false;
            ulong self = MonoSingletonT<UnitManager>.Instance.localPlayerUID;
            return states.TryGetValue(self, out RewardState st) && (st.BuffMask & bit) != 0;
        }

        // ===== 服务端发放 =====

        // 只看课堂状态（curState 全端同步）；不能拿房主本地输入冻结态去卡远程老师的奖励结算
        internal static bool ClassActive()
        {
            return ClassroomManager.IsSingletonActive()
                && ClassroomManager.Instance.curState == EClassroomState.Classing;
        }

        private static void Grant(ulong uid, int bit, string reason)
        {
            if (!ClassActive()) return;
            RewardState st = GetOrCreate(uid);
            if ((st.EarnedMask & bit) != 0) return; // 每项每节课一次
            st.EarnedMask |= bit;
            st.PointsLeft++;
            Log("[状态] 奖励点发放：" + reason + " → " + NameOf(uid) + "，剩余 " + st.PointsLeft + "/" + MaxPoints);
            BroadcastState("获得奖励点（" + st.PointsLeft + "/" + MaxPoints + "）：" + reason, uid);
        }

        private static void GrantAllTeachers(int bit, string reason)
        {
            if (!MonoSingletonT<UnitManager>.IsSingletonActive()) return;
            foreach (Unit player in MonoSingletonT<UnitManager>.Instance.playerList)
            {
                if (player == null || !player.isPlayer || !player.IsTeacher) continue;
                Grant(player.UID, bit, reason);
            }
        }

        private static RewardState GetOrCreate(ulong uid)
        {
            if (!states.TryGetValue(uid, out RewardState st))
            {
                st = new RewardState();
                states[uid] = st;
            }
            return st;
        }

        internal static string NameOf(ulong uid)
        {
            if (MonoSingletonT<UnitManager>.IsSingletonActive())
            {
                Unit unit = MonoSingletonT<UnitManager>.Instance.GetUnitByUID(uid);
                if (unit != null) return unit.GetPlayerUnitName();
            }
            return "uid=" + uid;
        }

        // ===== 快照广播：变更即时 + 每 5s 重广播自愈（重连/中途加入靠全量重建） =====

        internal static void BroadcastState(string notify = null, ulong notifyUid = 0)
        {
            if (!NetworkServer.active) return;
            version++;
            TeacherRewardMessage message = BuildStateMessage(notify, notifyUid);
            if (MonoSingletonT<RoomManager>.IsSingletonActive())
                MonoSingletonT<RoomManager>.Instance.BroadcastToAllExceptHost(message, 0);
            ApplyStateMessage(message, replaceStates: false); // 房主本地联动（定向提示/面板关闭）
        }

        // 快照只带当前在线老师（有状态条目的），防止已退出玩家残留条目膨胀
        private static TeacherRewardMessage BuildStateMessage(string notify, ulong notifyUid)
        {
            var live = new List<ulong>();
            if (MonoSingletonT<UnitManager>.IsSingletonActive())
            {
                foreach (Unit player in MonoSingletonT<UnitManager>.Instance.playerList)
                {
                    if (player == null || !player.isPlayer || !player.IsTeacher) continue;
                    if (states.ContainsKey(player.UID)) live.Add(player.UID);
                }
            }
            var uids = new long[live.Count];
            var points = new int[live.Count];
            var masks = new int[live.Count];
            var buffs = new int[live.Count];
            var cardLists = new string[live.Count];
            for (int i = 0; i < live.Count; i++)
            {
                RewardState st = states[live[i]];
                uids[i] = (long)live[i];
                points[i] = st.PointsLeft;
                masks[i] = st.EarnedMask;
                buffs[i] = st.BuffMask;
                cardLists[i] = st.Cards.Count > 0 ? string.Join("|", st.Cards) : "";
            }
            return new TeacherRewardMessage
            {
                Kind = TeacherRewardMessage.KindState,
                Version = version,
                TeacherUID = notifyUid,
                Notify = notify,
                Uids = uids,
                Points = points,
                Masks = masks,
                Buffs = buffs,
                CardLists = cardLists,
            };
        }

        private static void ApplyStateMessage(TeacherRewardMessage message, bool replaceStates)
        {
            if (replaceStates)
            {
                states.Clear();
                int n = message.Uids?.Length ?? 0;
                for (int i = 0; i < n; i++)
                {
                    ulong uid = (ulong)message.Uids[i];
                    if (uid == 0) continue;
                    var st = new RewardState
                    {
                        PointsLeft = i < message.Points.Length ? message.Points[i] : 0,
                        EarnedMask = i < message.Masks.Length ? message.Masks[i] : 0,
                        BuffMask = i < message.Buffs.Length ? message.Buffs[i] : 0,
                    };
                    string cards = i < message.CardLists.Length ? message.CardLists[i] : null;
                    if (!string.IsNullOrEmpty(cards)) st.Cards.AddRange(cards.Split('|'));
                    states[uid] = st;
                }
            }
            if (!string.IsNullOrEmpty(message.Notify)
                && MonoSingletonT<UnitManager>.IsSingletonActive()
                && (message.TeacherUID == 0 || message.TeacherUID == MonoSingletonT<UnitManager>.Instance.localPlayerUID))
            {
                NotifyLocal(message.Notify);
            }
            if (pendingSelect)
            {
                if (MyPointsLeft < pendingPoints)
                {
                    pendingSelect = false;
                    RewardPanel.Close(); // 服务端接受了选卡（自己点数减少）
                }
                else if (Time.unscaledTime > pendingDeadline)
                {
                    pendingSelect = false; // 超时放弃等待（服务端可能没收到请求）
                }
            }
        }

        private static void NotifyLocal(string text)
        {
            if (!MonoSingletonT<GameNotifyManager>.IsSingletonActive()) return;
            MonoSingletonT<GameNotifyManager>.Instance.ShowCommonNotify(text, null, 3.5f);
        }

        // ===== 消息注册与处理 =====

        public static void RegisterClient()
        {
            NetworkClient.RegisterHandler<TeacherRewardMessage>(OnClientReward, true);
            // 重进房间=全新连接：客机残留状态清掉，等 ≤5s 快照重建
            // （房主也走 InitClient 但 NetworkServer.active 已 true，不误清）
            if (!NetworkServer.active)
            {
                states.Clear();
                pendingSelect = false;
                RewardPanel.Close();
            }
        }

        public static void RegisterServer()
        {
            NetworkServer.RegisterHandler<TeacherRewardMessage>(OnServerReward, true);
        }

        private static void OnClientReward(TeacherRewardMessage message)
        {
            if (NetworkServer.active) return; // 房主走广播后的本地直调
            if (message.Kind == TeacherRewardMessage.KindState)
                ApplyStateMessage(message, replaceStates: true);
        }

        private static void OnServerReward(NetworkConnectionToClient connection, TeacherRewardMessage message)
        {
            if (!NetRuntime.IsServer) return;
            if (message.Kind != TeacherRewardMessage.KindSelect) return;
            ulong sender = connection == null || connection == NetworkServer.localConnection
                ? MonoSingletonT<UnitManager>.Instance.localPlayerUID
                : MonoSingletonT<RoomManager>.Instance.GetPlayerUIDByConnection(connection);
            ServerSelect(sender, message.CardId);
        }

        // ===== 选卡流程 =====

        /// <summary>面板点卡入口（面板在老师本机；扣点服务端权威）。</summary>
        internal static void SelectCard(string cardId)
        {
            if (FindCard(cardId) == null) return;
            pendingPoints = MyPointsLeft;
            pendingSelect = true;
            pendingDeadline = Time.unscaledTime + 5f;
            if (NetRuntime.IsServer)
            {
                ServerSelect(MonoSingletonT<UnitManager>.Instance.localPlayerUID, cardId);
            }
            else if (MonoSingletonT<RoomManager>.IsSingletonActive())
            {
                MonoSingletonT<RoomManager>.Instance.SendToServer(new TeacherRewardMessage
                {
                    Kind = TeacherRewardMessage.KindSelect,
                    CardId = cardId,
                });
            }
        }

        private static void ServerSelect(ulong uid, string cardId)
        {
            RewardCardDef card = FindCard(cardId);
            if (card == null) return;
            RewardState st;
            if (!ClassActive() || !states.TryGetValue(uid, out st) || st.PointsLeft <= 0)
            {
                Log("[警告] 选卡被拒：uid=" + uid + " card=" + cardId + "（点数不足或不在上课中）");
                BroadcastState("奖励点不可用（点数不足或不在上课中）", uid);
                return;
            }
            if (!TryApplyCardEffect(uid, card, st, out string failReason, out string successSuffix))
            {
                // 效果没生效就不扣点（如无可突击目标/重复购买），提示老师
                Log("[状态] 选卡退还：" + card.Name + " ← " + NameOf(uid) + "：" + failReason);
                BroadcastState(failReason + "（奖励点已退还）", uid);
                return;
            }
            st.PointsLeft--;
            st.Cards.Add(card.Id);
            Log("[状态] 奖励点使用：" + card.Name + " ← " + NameOf(uid) + "，剩余 " + st.PointsLeft + "/" + MaxPoints);
            BroadcastState("已使用奖励点：" + card.Name + successSuffix, uid);
        }

        private static RewardCardDef FindCard(string id)
        {
            foreach (RewardCardDef card in Cards)
                if (card.Id == id) return card;
            return null;
        }

        /// <summary>技能卡效果分派：成功返回 true（调用方扣点）；失败带原因（调用方退还点数）。</summary>
        private static bool TryApplyCardEffect(ulong uid, RewardCardDef card, RewardState st, out string failReason, out string successSuffix)
        {
            failReason = null;
            successSuffix = "";
            switch (card.Id)
            {
                case CardSurprise:
                {
                    // 突击检查：随机一名存活、未被挂柱的真人学生当场被晕眩（服务端随机，GetStun 全端广播）
                    Unit picked = PickRandomStunnableStudent();
                    if (picked == null)
                    {
                        failReason = "教室里没有可突击的学生";
                        return false;
                    }
                    picked.GetComponent<StudentStunComponent>()?.GetStun(EStunType.Chalk);
                    Log("[状态] 突击检查：" + NameOf(picked.UID) + " 被随机晕眩（发起者 " + NameOf(uid) + "）");
                    successSuffix = " → " + NameOf(picked.UID) + " 当场被晕眩！";
                    return true;
                }
                case CardSwift:
                {
                    if ((st.BuffMask & BuffSwift) != 0)
                    {
                        failReason = "健步如飞已经在生效中";
                        return false;
                    }
                    st.BuffMask |= BuffSwift;
                    return true;
                }
                case CardChalk:
                {
                    if ((st.BuffMask & BuffChalk) != 0)
                    {
                        failReason = "重磅粉笔已经在生效中";
                        return false;
                    }
                    st.BuffMask |= BuffChalk;
                    return true;
                }
                case CardShield:
                {
                    if ((st.BuffMask & BuffShield) != 0)
                    {
                        failReason = "护盾已经就位（挡下一次晕倒后才能再买）";
                        return false;
                    }
                    st.BuffMask |= BuffShield;
                    successSuffix = "（护盾已就位）";
                    return true;
                }
                default:
                    // 占位卡（拖堂/整顿纪律）：效果待设计，先照常扣点
                    successSuffix = "（效果占位，敬请期待）";
                    return true;
            }
        }

        private static Unit PickRandomStunnableStudent()
        {
            if (!MonoSingletonT<UnitManager>.IsSingletonActive()) return null;
            var candidates = new List<Unit>();
            foreach (Unit player in MonoSingletonT<UnitManager>.Instance.playerList)
            {
                if (player == null || !player.isPlayer || !player.IsStudent) continue; // 真人学生
                if (!player.isActived || player.IsDead) continue;
                if (player.CurState() == (int)EUnitFsmStateType.StudentPunished) continue; // 已挂柱的晕了也没意义
                candidates.Add(player);
            }
            if (candidates.Count == 0) return null;
            return candidates[UnityEngine.Random.Range(0, candidates.Count)];
        }

        internal static bool IsCardOwned(string id)
        {
            if (id == CardSwift && LocalHasBuff(BuffSwift)) return true;
            if (id == CardChalk && LocalHasBuff(BuffChalk)) return true;
            if (id == CardShield && LocalHasBuff(BuffShield)) return true;
            return false;
        }

        // ===== 本地视图与按键 =====

        internal static int MyPointsLeft
        {
            get
            {
                if (!MonoSingletonT<UnitManager>.IsSingletonActive()) return 0;
                ulong self = MonoSingletonT<UnitManager>.Instance.localPlayerUID;
                return states.TryGetValue(self, out RewardState st) ? st.PointsLeft : 0;
            }
        }

        internal static bool IsLocalTeacher
        {
            get
            {
                if (!MonoSingletonT<UnitManager>.IsSingletonActive()) return false;
                Unit self = MonoSingletonT<UnitManager>.Instance.localPlayer;
                return self != null && self.IsTeacher;
            }
        }

        private static void HandlePanelKey()
        {
            if (!MonoSingletonT<InputManager>.IsSingletonActive()
                || MonoSingletonT<InputManager>.Instance.FrozedPlayerInput
                || MonoSingletonT<InputManager>.Instance.FrozedAllInput) return;
            if (!ReInput.isReady) return;
            Keyboard keyboard = ReInput.players.GetPlayer(0).controllers.Keyboard;
            if (keyboard == null || !keyboard.GetKeyDown(RewardConfig.PanelKeyCode)) return;
            if (RewardPanel.IsOpen)
            {
                RewardPanel.Close(); // 面板开着时 H 也能关
                return;
            }
            // 开面板门槛：老师 + 上课中 + 有剩余点数
            if (!ClassActive()) return;
            if (!IsLocalTeacher || MyPointsLeft <= 0) return;
            RewardPanel.Open();
        }

        // ===== 每帧驱动 =====

        internal static void Tick()
        {
            if (NetRuntime.IsServer)
            {
                // 已有老师拿过点的课上：每 5s 全量重广播自愈（重连/中途加入）
                if (states.Count > 0 && Time.unscaledTime >= nextRepublish)
                {
                    nextRepublish = Time.unscaledTime + 5f;
                    BroadcastState();
                }
            }
            HandlePanelKey();
        }

        // ===== 课堂生命周期 =====

        private static void OnClassStateChanged(EClassroomState state)
        {
            if (state != EClassroomState.Prepare) return;
            // 新一节课准备：清空上节课的奖励点与选卡记录
            pendingSelect = false;
            RewardPanel.Close();
            if (NetRuntime.IsServer)
            {
                states.Clear();
                trickHalfGranted = false;
                nextRepublish = 0f;
                BroadcastState(); // 清场快照（空数组 → 客机整体替换为空）
                Log("[状态] 新课程准备：奖励点已重置");
            }
            else
            {
                states.Clear();
            }
        }

        internal static void Cleanup()
        {
            // 必须按目标注销：UnRegister(key) 会把该 key 上游戏自己的监听（黑板/门/柱子等）全部清掉
            if (registeredStateHook) MsgCenter.UnRegister(ClassStateChangedMsgId, Hooks);
            registeredStateHook = false;
        }
    }
}
