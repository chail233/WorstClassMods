using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mirror;
using Rewired;
using WorstClassEver.ModAPI;
using UnityEngine;

namespace BioChem
{
    /// <summary>全量状态快照（enabled + 感染名单 + HP + 体力）。服务端权威，变更即时广播 + 每 5 秒重广播自愈。
    /// Notify 非空时各端弹一条提示（只随本次广播，定时重广播恒为空）——公告必须走消息，否则只有房主自己看得到。</summary>
    public struct BioChemStateMessage : NetworkMessage
    {
        public uint Version;
        public string Json;
        public string Notify;
    }

    /// <summary>
    /// Kind: 2=Hit（S2C，原生吹箭命中的生化结算：扣血+减速；弹道/音效表现走游戏原生 ApplyHit）
    /// 3=MgFire（C2S，连发吹箭的一发：带弹道参数，服务端注册 taserShots 后广播）
    /// 4=MgShot（S2C，连发吹箭的弹体广播：各端 TaserProjectile.PlayFire 渲染，射手自己跳过——本地已预测渲染）
    /// 5=AmmoSync（S2C，备弹同步：TargetUID=学生 TargetHp=剩余备弹；射击扣减/掏出/吃东西补弹后广播）
    /// 6=EatRefill（C2S，吃完一份食物上报补弹；服务端冷却校验）
    /// 弹体 UID 是高位虚拟 UID（VirtualShotUidBase 起），绝不与真实 unit UID 撞。
    /// </summary>
    public struct BioChemDartMessage : NetworkMessage
    {
        public const byte Hit = 2, MgFire = 3, MgShot = 4, AmmoSync = 5, EatRefill = 6;
        public byte Kind;
        public ulong TargetUID;    // Hit=目标玩家 / MgFire·MgShot=虚拟弹体 UID
        public int TargetHp;       // Hit 用
        public byte SlowStacks;    // Hit 用
        public ulong UserUID;      // MgFire·MgShot 用：射手
        public Vector3 Origin;     // MgFire·MgShot 用：弹道参数（与原生 TaserFired_S2C 一致）
        public Vector3 Direction;
        public float Speed;
        public float Range;
        public float Radius;
        public float StunDuration;
    }

    /// <summary>Kind: 1=Request（C2S，小老师击晕请求，Stamina=申报体力供服务端校验扣减） 2=Apply（S2C，服务端校验通过后广播）</summary>
    public struct BioChemStunMessage : NetworkMessage
    {
        public const byte Request = 1, Apply = 2;
        public byte Kind;
        public ulong TargetUID;
        public float Duration;
        public float Stamina;
    }

    /// <summary>写板书回报（本地写完 → 服务端加成）。Kind: 1=大老师回 HP 2=小老师回体力</summary>
    public struct BioChemWriteMessage : NetworkMessage
    {
        public const byte TeacherHp = 1;
        public byte Kind;
    }

    /// <summary>Kind: 1=Down（HP 归零晕倒） 2=Respawn（办公室复活，位置随消息） 3=Kill（猎手击杀：永久倒地出局，不复活）</summary>
    public struct BioChemDownMessage : NetworkMessage
    {
        public const byte Down = 1, Respawn = 2, Kill = 3;
        public byte Kind;
        public ulong UnitUID;
        public Vector3 Position;
    }

    /// <summary>老师猎手。Kind: 1=TransformRequest（C2S，按 G 请求变身） 2=Transform（S2C，变身广播：变大+特效+发刀）
    /// 3=Strike（C2S，挥刀上报：Origin/Direction=点击瞬间准星方向，扇形判定全在服务端，UnitUID 保留不用）
    /// 4=Hit（S2C，扣血结算：UnitUID=目标 Hp=扣血后 HP，双向——老师打猎手 / 猎手打老师共用；
    /// AttackerUID=猎手攻击时填攻击者（其本机已预测命中音效，跳过广播音效防双响），老师打猎手=0）
    /// 5=RevertVisual（S2C，还原外观：猎手被感染等）
    /// 6=Swing（S2C，挥刀广播：UnitUID=猎手，其他端驱动持刀摆动+挥砍音效；攻击者本地已预测，
    /// 身体挥动动画走原生 PlayAnimSync 不经此消息）</summary>
    public struct BioChemHunterMessage : NetworkMessage
    {
        public const byte TransformRequest = 1, Transform = 2, Strike = 3, Hit = 4, RevertVisual = 5, Swing = 6;
        public byte Kind;
        public ulong UnitUID;
        public int Hp;
        public Vector3 Origin;     // Strike 用：挥刀瞬间上报位置（服务端做 5m 伪造门槛，抄原生剑击）
        public Vector3 Direction;  // Strike 用：点击瞬间准星方向（服务端扇形判定轴，快速甩刀跟手）
        public ulong AttackerUID;  // Hit 用：猎手攻击=攻击者 UID（本机预测过音效就不再播广播音效）
    }

    [Serializable]
    public class BioChemStateDto
    {
        public bool enabled;
        public List<long> infected = new List<long>();
        public List<long> hunters = new List<long>();
        public List<long> killed = new List<long>();
        public List<long> hpUids = new List<long>();
        public List<int> hpVals = new List<int>();
        public List<long> stUids = new List<long>();
        public List<float> stVals = new List<float>();
        public List<long> evolutionUids = new List<long>();
        public List<int> evolutionStages = new List<int>();
        public string cfg;  // 房主数值配置全量 JSON，客机应用后各端行为/显示一致
    }

    public sealed class ModEntry : IMod
    {
        internal static Action<string> Log;

        internal static readonly KeyCode StunKey = KeyCode.F;       // 小老师击晕
        internal const KeyCode PanelKey = KeyCode.F7;               // 管理面板开关
        private const string HarmonyId = "worstclass.biochem";
        private const int ClassStateChangedMsgId = 301;
        private const int PlayerRemovedMsgId = 2017;

        internal static BioChemConfig Config => BioChemConfig.Instance;
        internal static string ModDirectory; // mod 部署目录（读自身资源，如 AK47 模型/枪声）

        // ===== 全局模式状态（服务端权威，快照广播对齐） =====
        internal static bool enabled;
        internal static readonly HashSet<ulong> infected = new HashSet<ulong>();
        internal static readonly HashSet<ulong> hunters = new HashSet<ulong>();      // 老师猎手（学生阵营，对游戏表现为老师）
        internal static readonly HashSet<ulong> killedTeachers = new HashSet<ulong>(); // 被猎手击杀出局（永久倒地）的老师
        internal static readonly Dictionary<ulong, int> hp = new Dictionary<ulong, int>();
        internal static readonly Dictionary<ulong, float> stamina = new Dictionary<ulong, float>();
        private static readonly Dictionary<ulong, int> teacherEvolution = new Dictionary<ulong, int>();
        internal const int MaxEvolutionStage = 3;
        internal const int EvolutionHpPercentPerStage = 20;
        // 猎手外观：各端记录变身前模型缩放，感染/回合重置时还原
        private static readonly Dictionary<ulong, Vector3> hunterOrigScale = new Dictionary<ulong, Vector3>();
        private static readonly Dictionary<ulong, float> hunterStrikeReadyAt = new Dictionary<ulong, float>(); // 服务端近战冷却
        private static float localHunterStrikeReadyAt;  // 客户端近战冷却
        private static readonly List<Unit> sweepBuffer = new List<Unit>(); // 扇形扫击目标复用缓冲（服务端判定/客户端预测共用）
        private static bool hunterUnlockAnnounced;      // 解锁变身公告每局只发一次（服务端）
        internal const float HunterScale = 1.3f;        // 猎手模型放大倍数（视觉+受击盒一起变大）
        internal const float HunterAttackRange = 2.2f;  // 猎手近战范围（水平距离）
        internal const float HunterSlashAngle = 120f;   // 猎手挥刀扇形总角度（CF 生化近战手感：面前群体判定）
        internal const int HunterTakenDamage = 2;       // 老师剑击/小老师击晕命中猎手的扣血
        internal static uint version;
        private static float nextRepublish;

        // ===== 服务端瞬态 =====
        private static bool initialInfectionApplied;
        // 柱子 UID → (被挂学生 UID, 绑定起始时间)；绑定/释放钩子维护
        private static readonly Dictionary<ulong, (ulong student, float start)> pillarBinds = new Dictionary<ulong, (ulong, float)>();
        private static readonly Dictionary<ulong, float> pillarRetryAt = new Dictionary<ulong, float>();
        private static readonly Dictionary<ulong, float> stunCooldownUntil = new Dictionary<ulong, float>();
        private static readonly Dictionary<ulong, float> downedUntil = new Dictionary<ulong, float>();
        // 吹箭减速层数（服务端计数，广播层数给目标端做本地衰减）
        private static readonly Dictionary<ulong, List<float>> serverSlowExpiries = new Dictionary<ulong, List<float>>();

        // ===== 主武器 AK47：以原生 Taser 物品承载挂载和弹道，备弹由服务端管理 =====
        private static int taserItemId = -1;
        private static readonly Dictionary<ulong, ulong> issuedTaser = new Dictionary<ulong, ulong>(); // 学生 UID → 已掏出的 Taser 物品 UID
        // 挂载重试：物品模型异步加载完成后嘴部挂点才存在，逐帧重试 TryEquipMouthItem
        private static readonly Dictionary<ulong, (ulong student, float deadline)> pendingEquip = new Dictionary<ulong, (ulong student, float deadline)>(); // 物品 UID → 学生 UID
        // 备弹：服务端权威计数（GetDartAmmo 懒初始化为满），射击扣减/吃完食物补充，AmmoSync 广播对齐射手端
        private static readonly Dictionary<ulong, int> dartAmmo = new Dictionary<ulong, int>();
        private static readonly Dictionary<ulong, float> foodRefillReadyAt = new Dictionary<ulong, float>(); // 补弹防伪冷却（吃完一份食物至少要吃几秒）
        private const float FoodRefillCooldown = 2.5f;

        // ===== 客机本地 =====
        private static readonly List<float> selfSlowExpiries = new List<float>(); // 本地玩家被吹箭命中的减速层（各自 0.5s 过期）
        private static int selfDartAmmo; // 本地玩家备弹（服务端 AmmoSync 权威校准；ClearRoundState 重置为满）
        private static float localStunReadyAt;   // 击晕本地冷却
        private static bool registeredHooks;
        // 本地玩家自己的体力模拟（走路消耗 + 写板书回复），服务端只在击晕结算时按申报值扣减
        private static float selfStamina = -1f;  // <0 = 未初始化/非小老师
        private static Vector3 lastSelfPos;
        private static bool lastSelfPosValid;

        internal static readonly MethodInfo ReleaseStudentOnServerMethod =
            AccessTools.Method(typeof(SkillMessageHandler), "ReleaseStudentOnServer");
        // 原生规范传送（internal）：NetData.Position + SyncTransform 立即生效 + 刚体落位 + 输入复位，
        // 裸 Unit.SetPosition 只动 transform 不动刚体，复活/传送类场景必须走这个
        private static readonly MethodInfo ApplyPlayerRelocationMethod =
            AccessTools.Method(typeof(Unit), "ApplyPlayerRelocation");
        private static readonly MethodInfo TeacherClearStunMethod =
            AccessTools.Method(typeof(TeacherStunComponent), "ClearStun");

        // ===== AK47 连发 =====
        // 原生吹箭一发一物（TryConsumeShot 消耗+回收），连发靠抑制原生单击开火 + 定时造弹：
        // 原生 TryBuildShot 提供弹体参数，AK 枪口和屏幕准星确定弹道 → PlayFire 本地渲染 → C2S MgFire →
        // 服务端反射写原生 taserShots 注册表 → 命中走原生 OnTaserImpact（眩晕/音效/Hit 广播原样生效）
        private static float mgNextFireAt;       // 连发下次可射击时间（本地节流）
        private static uint mgShotSeq;           // 虚拟弹体 UID 序号
        private const ulong VirtualShotUidBase = 0x6000000000000000UL; // 虚拟弹体 UID 高位标记（真实 UID 远小于此）
        private static readonly Dictionary<ulong, float> mgFireUntil = new Dictionary<ulong, float>(); // 服务端射击限频
        private static bool lastImpactWasRegistered; // 本发是否注册过 taserShots（postfix 只结算已注册的弹）
        private static bool suppressVanillaStun;     // 本次原生 OnTaserImpact 结算期间屏蔽 TryStun
        private static bool renderingAkShot;
        private static readonly TaserBallistics akBallistics = new TaserBallistics();
        private static readonly MethodInfo TaserTryBuildShotMethod =
            AccessTools.Method(typeof(TaserComponent), "TryBuildShot");
        private static readonly MethodInfo TaserPlayFireMethod =
            AccessTools.Method(typeof(TaserProjectile), "PlayFire");
        private static readonly FieldInfo TaserShotsField =
            AccessTools.Field(typeof(UnitMessageHandler), "taserShots");

        // ===== 入口 =====

        public void OnLoad(ModContext context)
        {
            Log = context.Log;
            ModDirectory = context.ModDirectory;
            BioChemConfig.Load(context.ConfigDirectory, Log);
            Ak47Weapon.Init(ModDirectory); // 曳光弹换装 + AK 枪声预载（内部懒重试）
            var harmony = new Harmony(HarmonyId);
            try
            {
                // 身份翻转核心：感染者对所有游戏逻辑表现为老师（板书准入、NPC 敌我、捕获资格、名牌隐藏全走这两个属性）
                harmony.Patch(AccessTools.PropertyGetter(typeof(Unit), "IsTeacher"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterIsTeacher"));
                harmony.Patch(AccessTools.PropertyGetter(typeof(Unit), "IsStudent"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterIsStudent"));
                // 生化模式捣蛋计分整体封锁（捣蛋进度/学生捣蛋胜利/机制奖励全部冻结）
                harmony.Patch(AccessTools.Method(typeof(ClassroomManager), "OnResponseTrickCountData"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeTrickCount"));
                // 倒计时结束原版判老师胜（TeacherTimeout）→ 生化模式翻转为学生幸存获胜
                harmony.Patch(AccessTools.Method(typeof(ClassroomManager), "FinishClass"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeFinishClass"));
                // 吃东西补弹：普通食物吃完最后一口（判定与原生一致：入口耐久 ∈ (0,1]）→ 客户端上报服务端补弹。
                // 香蕉/马桶食物等子类都走 base.OnItemUseSuccess，补丁基类即可全覆盖；peel/leftovers 不调 base=不吃，天然不触发
                harmony.Patch(AccessTools.Method(typeof(FoodComponent), "OnItemUseSuccess"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeFoodFinishingBite"));
                // 火锅食材被吃光：Apply 广播状态翻转 Consumed 且请求者是自己 → 上报补弹（prefix 抓翻转前状态防旧包误判）
                harmony.Patch(AccessTools.Method(typeof(HotPotIngredientComponent), "Apply"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeHotPotApply"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterHotPotApply"));
                // 老师剑击打猎手：原生弹体命中过滤只认 IsStudent（猎手已翻转成 IsTeacher 天然免疫）——
                // postfix 放行猎手进命中名单，服务端 ChalkHit 结算 prefix 把"晕学生"改写成"扣猎手 HP"
                harmony.Patch(AccessTools.Method(typeof(TeacherSlashProjectile), "Student"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterSlashStudentFilter"));
                harmony.Patch(AccessTools.Method(typeof(UnitMessageHandler), "OnChalkHitRequest"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeChalkHit"));
                // 猎手固定只能用近战：吞掉手持物品（书/零食/墨水/手机/火锅等）与嘴部物品（吹箭/泡泡糖/气球）的全部输入
                harmony.Patch(AccessTools.Method(typeof(StudentItemUseComponent), "GetCmdInput"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeHunterStudentItemInput"));
                harmony.Patch(AccessTools.Method(typeof(MouthItemUseComponent), "GetCmdInput"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeHunterMouthItemInput"));
                // 小老师抓取：DealArrestEffect 查 localPlayer 的 SkillComponent（学生预制体没有）——
                // prefix 仅在 localPlayer 感染时接管（复刻原方法去掉能量门槛），真老师走原方法，猎手不在 infected 不开放
                harmony.Patch(AccessTools.Method(typeof(StudentInterectComponent), "DealArrestEffect"),
                    prefix: new HarmonyMethod(typeof(MiniTeacherCatch), nameof(MiniTeacherCatch.BeforeDealArrestEffect)));
                Writer<BioChemHunterMessage>.write = BioChemPump.WriteHunter;
                Reader<BioChemHunterMessage>.read = BioChemPump.ReadHunter;
                // 挂柱计时：绑定开始记录，释放清除；超时由服务端 Tick 强制释放并感染
                harmony.Patch(AccessTools.Method(typeof(PunishPillarComponent), "OnResponseBindStudent"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterBindToPillar"));
                harmony.Patch(AccessTools.Method(typeof(PunishPillarComponent), nameof(PunishPillarComponent.RewardTeacherForRealStudent)),
                    postfix: new HarmonyMethod(typeof(ModEntry), nameof(AfterPillarReward)));
                harmony.Patch(AccessTools.Method(typeof(PunishPillarComponent), "OnResponseReleaseStudent"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterReleaseFromPillar"));
                // 写板书：大老师回 HP（写完板书只在写作端结算，上报服务端）
                harmony.Patch(AccessTools.Method(typeof(BlackBoardComponent), "UseSuccess"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterBoardWrite"));
                // 小老师体力：长按板书读条期间缓慢回复（与原生老师能量同节奏）
                harmony.Patch(AccessTools.Method(typeof(BlackBoardComponent), "OnUpdate"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterBoardTick"));
                // 吹箭减速：本地玩家移速唯一出口
                harmony.Patch(AccessTools.Method(typeof(InputComponent), "GetCurrentMoveSpeed"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterMoveSpeed"));
                // 原生吹箭（Taser）命中：服务端结算生化伤害/减速（弹道、眩晕、音效全走原生）
                harmony.Patch(AccessTools.Method(typeof(UnitMessageHandler), nameof(UnitMessageHandler.OnTaserImpact)),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeTaserImpact"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterTaserImpact"));
                // 我们的吹箭没有原版眩晕：只在本次原生结算窗口里让 TryStun 返回 false（学生被打=完全无效）
                harmony.Patch(AccessTools.Method(typeof(TaserProjectile), "TryStun"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeTaserTryStun"));
                // 连发模式吞掉原生单击开火（否则一发就把嘴里的吹箭消耗掉），由 mod 连发循环接管；瞄准不受影响
                harmony.Patch(AccessTools.Method(typeof(TaserComponent), "HandleInput"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeTaserHandleInput"));
                // 消息注册挂到游戏自己的注册时机之后（防重注册清掉）
                harmony.Patch(AccessTools.Method(typeof(RoomManager), "RegisterClientMessageHandlers"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "RegisterClient"));
                harmony.Patch(AccessTools.Method(typeof(RoomManager), "RegisterServerMessageHandlers"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "RegisterServer"));
                // PlayFire 生成 AK 弹体时，用枪声替换原生发射音效。
                harmony.Patch(AccessTools.Method(typeof(AudioManager), "PlayOneShotAtPosition"),
                    prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforeAkLaunchSound)));
                Writer<BioChemStateMessage>.write = BioChemPump.WriteState;
                Reader<BioChemStateMessage>.read = BioChemPump.ReadState;
                Writer<BioChemDartMessage>.write = BioChemPump.WriteDart;
                Reader<BioChemDartMessage>.read = BioChemPump.ReadDart;
                Writer<BioChemWeaponSlotMessage>.write = BioChemPump.WriteWeaponSlot;
                Reader<BioChemWeaponSlotMessage>.read = BioChemPump.ReadWeaponSlot;
                Writer<BioChemStunMessage>.write = BioChemPump.WriteStun;
                Reader<BioChemStunMessage>.read = BioChemPump.ReadStun;
                Writer<BioChemWriteMessage>.write = BioChemPump.WriteWrite;
                Reader<BioChemWriteMessage>.read = BioChemPump.ReadWrite;
                Writer<BioChemDownMessage>.write = BioChemPump.WriteDown;
                Reader<BioChemDownMessage>.read = BioChemPump.ReadDown;
                RegisterClient();
                RegisterServer();

                MsgCenter.Register<EClassroomState>(ClassStateChangedMsgId, Hooks.OnClassStateChanged);
                MsgCenter.Register<ulong, bool>(PlayerRemovedMsgId, Hooks.OnPlayerUnitRemoved);
                registeredHooks = true;

                var pump = new GameObject("生化模式");
                UnityEngine.Object.DontDestroyOnLoad(pump);
                pump.AddComponent<BioChemPump>();
            }
            catch
            {
                harmony.UnpatchAll(HarmonyId);
                throw;
            }
            Log("[状态] 生化模式 Mod 已加载（按 F7 打开管理面板，房主可开关生化模式）");
        }

        // ===== 身份翻转（感染 = 对全游戏表现为老师） =====

        public static void AfterIsTeacher(Unit __instance, ref bool __result)
        {
            if (__result || !enabled || __instance == null) return;
            if (infected.Contains(__instance.UID) || hunters.Contains(__instance.UID)) __result = true;
        }

        public static void AfterIsStudent(Unit __instance, ref bool __result)
        {
            if (!__result || !enabled || __instance == null) return;
            if (infected.Contains(__instance.UID) || hunters.Contains(__instance.UID)) __result = false;
        }

        // ===== 捣蛋计分封锁 =====

        // 生化模式下捣蛋值整体不生效：OnResponseTrickCountData 是游戏全部捣蛋计分的唯一服务端入口，
        // 整体跳过后捣蛋进度冻结在 0——学生捣蛋胜利(StudentTrick)、按捣蛋值触发的机制奖励、捣蛋 UI 全部不走，
        // 感染者封锁也由此一并覆盖（并存 TrickMole 的捣蛋值累计/挂柱扣分在生化模式下同样无效）
        public static bool BeforeTrickCount(ulong uid)
        {
            return !enabled;
        }

        // CF 终结者规则：只要学生阵营还有幸存者（未抓学生或存活猎手），老师就赢不了——
        // 原版"抓完全部学生"TeacherCatch 被拦下（猎手活着=没抓完），倒计时到点也翻转为学生胜；
        // 学生阵营真全灭（全感染且猎手死绝）时 TeacherCatch 放行=老师胜。outcome 置 null 不冒用 StudentTrick 上报 Steam
        public static bool BeforeFinishClass(ref bool teacherWin, ref SteamMatchOutcome? outcome)
        {
            if (!enabled || !teacherWin) return true;
            bool studentTeamAlive = RemainingStudents() > 0 || hunters.Count > 0;
            if (outcome == SteamMatchOutcome.TeacherCatch) return !studentTeamAlive; // 有幸存者拦下结算，真全灭放行
            if (outcome == SteamMatchOutcome.TeacherTimeout && studentTeamAlive)
            {
                teacherWin = false; // 幸存到倒计时结束：学生获胜
                outcome = null;
            }
            return true;
        }

        // ===== 挂柱计时 → 感染 =====

        // OnResponseBindStudent 是绑定真正生效的点（版本校验通过才调用）；只在服务端记录计时
        public static void AfterBindToPillar(PunishPillarComponent __instance, Unit student)
        {
            if (!NetRuntime.IsServer || !enabled || student == null || student.UID == 0) return;
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            if (student.IsDead || !student.isPlayer || !student.IsStudent) return;
            ulong pillarUID = __instance.owner != null ? __instance.owner.UID : 0;
            if (pillarUID == 0) return;
            pillarBinds[pillarUID] = (student.UID, Time.unscaledTime);
            pillarRetryAt[pillarUID] = Time.unscaledTime + Config.pillarInfectSeconds;
            Log("[状态] " + student.GetPlayerUnitName() + " 被挂上罚站柱，" + Config.pillarInfectSeconds.ToString("0") + " 秒后感染");
        }

        // 原生挂柱响应通过版本检查并完成绑定后，才会对实际挂柱者调用奖励入口。
        public static void AfterPillarReward(Unit teacher, Unit student)
        {
            if (!NetRuntime.IsServer || !enabled || !ServerClassActive()) return;
            if (student == null || !student.isPlayer || student.IsDead || !student.IsStudent) return;
            if (!IsLivingTeacherOpponent(teacher) || !teacher.isPlayer || downedUntil.ContainsKey(teacher.UID)) return;

            ulong uid = teacher.UID;
            int stage = GetEvolutionStage(uid);
            if (stage >= MaxEvolutionStage) return;

            bool isMini = infected.Contains(uid);
            teacherEvolution[uid] = stage + 1;
            int maxHp = GetTeacherMaxHp(uid, !isMini);
            hp[uid] = maxHp;
            if (isMini) stamina[uid] = Config.staminaMax;
            serverSlowExpiries.Remove(uid);
            if (teacher.isLocalPlayer) RestoreLocalTeacherCondition(teacher, isMini);

            string announce = GetName(uid) + " 进化到 " + (stage + 1) + " 阶，生命上限 " + maxHp + "，生命和体力已回满！";
            NotifyAll(announce);
            BroadcastState(announce);
            Log("[状态] " + announce);
        }

        public static void AfterReleaseFromPillar(PunishPillarComponent __instance)
        {
            if (!NetRuntime.IsServer || __instance.owner == null) return;
            pillarBinds.Remove(__instance.owner.UID);
            pillarRetryAt.Remove(__instance.owner.UID);
        }

        // 服务端 Tick：挂柱超时 → 强制释放 → 感染
        private static void TickPillarInfection()
        {
            if (!enabled || pillarBinds.Count == 0) return;
            if (!RoomManager.IsSingletonActive() || !ClassroomManager.IsSingletonActive()) return;
            // 遍历副本：超时/过期条目会在迭代中移除，直接 foreach 字典会 InvalidOperationException
            foreach (var pair in new List<KeyValuePair<ulong, (ulong student, float start)>>(pillarBinds))
            {
                ulong pillarUID = pair.Key;
                var (studentUID, start) = pair.Value;
                if (Time.unscaledTime - start < Config.pillarInfectSeconds) continue;
                float retryAt = pillarRetryAt.TryGetValue(pillarUID, out float retry) ? retry : 0f;
                if (Time.unscaledTime < retryAt) continue;
                pillarRetryAt[pillarUID] = Time.unscaledTime + 1f; // 失败重试节流
                Unit pillar = UnitManager.Instance.GetUnitByUID(pillarUID);
                var pillarComp = pillar != null ? pillar.GetComponent<PunishPillarComponent>() : null;
                if (pillarComp == null || pillarComp.bindStudent == null || pillarComp.bindStudent.UID != studentUID)
                {
                    pillarBinds.Remove(pillarUID);
                    pillarRetryAt.Remove(pillarUID);
                    continue;
                }
                var request = new StudentReleaseMessageRequest_C2S { pillarUID = pillarUID, studentUID = studentUID, teacherUID = 0 };
                // internal 方法走反射：返回 true = 已完全释放（rescuer=本人 → 不触发救援惩罚）
                object result = ReleaseStudentOnServerMethod.Invoke(
                    RoomManager.Instance.GetSkillMessageHandler(),
                    new object[] { studentUID, request, false, null });
                if (result is bool released && released)
                {
                    pillarBinds.Remove(pillarUID);
                    pillarRetryAt.Remove(pillarUID);
                    InfectStudent(studentUID);
                }
            }
        }

        private static void TryApplyInitialInfection()
        {
            if (initialInfectionApplied || !ServerClassActive() || !ClassroomManager.Instance.classing) return;
            if (!UnitManager.IsSingletonActive()) return;
            initialInfectionApplied = true;
            if (Config.initialInfectedStudents == 0) return;

            var candidates = new List<ulong>();
            foreach (Unit unit in UnitManager.Instance.playerList)
                if (IsRemainingStudent(unit)) candidates.Add(unit.UID);

            int count = Math.Min(Config.initialInfectedStudents, candidates.Count - 1);
            if (count <= 0) return;
            for (int i = 0; i < count; i++)
            {
                int pick = UnityEngine.Random.Range(i, candidates.Count);
                ulong uid = candidates[pick];
                candidates[pick] = candidates[i];
                candidates[i] = uid;
                TryInfectStudent(uid);
            }

            string announce = "上课了，" + count + " 名学生在开局变成了小老师！";
            NotifyAll(announce);
            BroadcastState(announce);
        }

        private static bool TryInfectStudent(ulong uid)
        {
            if (!infected.Add(uid)) return false;
            hp[uid] = GetTeacherMaxHp(uid, false);
            stamina[uid] = Config.staminaMax;
            StudentWeapons.RemovePlayer(uid);
            MiniTeacherCatch.EnsureCatchAbility(uid); // 感染即补挂原生 CatchComponent（抓取/挂柱能力）
            if (UnitManager.IsSingletonActive() && uid == UnitManager.Instance.localPlayerUID)
            {
                selfStamina = Config.staminaMax;
            }
            string name = GetName(uid);
            Log("[状态] " + name + " 被感染为小老师（HP " + hp[uid] + "）");
            return true;
        }

        private static void InfectStudent(ulong uid)
        {
            if (!TryInfectStudent(uid)) return;
            string announce = GetName(uid) + " 被感染成了小老师！";
            NotifyAll(announce);            // 房主本机弹
            BroadcastState(announce);       // 客机随快照弹（消息上带 Notify 才能到客机）
            // 学生阵营可能就此清空（最后一名普通学生转阵营）：重触发原版"全员被抓"检查，
            // 被抓+感染清空时 FinishClass(TeacherCatch) 会被我们的 FinishClass prefix 按幸存者规则放行
            if (NetRuntime.IsServer && MonoSingletonT<ClassroomManager>.IsSingletonActive())
                checkAllPunishedMethod?.Invoke(MonoSingletonT<ClassroomManager>.Instance, null);
        }

        // ===== 写板书：大老师回 HP =====

        // UseSuccess 只在写作端（本地玩家）触发——注意 owner 是黑板单位不是老师，
        // 游戏内部也是直接用 UnitManager.localPlayer 结算；上报服务端加成。
        // 小老师体力不走这里（OnUpdate 里长按期间缓慢回复）
        public static void AfterBoardWrite(BlackBoardComponent __instance)
        {
            if (!enabled || !UnitManager.IsSingletonActive()) return;
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            Unit self = UnitManager.Instance.localPlayer;
            if (self == null || !self.isActived || self.IsDead || !self.IsTeacher) return;
            ulong selfUid = self.UID;
            if (infected.Contains(selfUid) || hunters.Contains(selfUid)) return; // 猎手不走大老师板书回血
            if (NetRuntime.IsServer) ApplyWrite(BioChemWriteMessage.TeacherHp, selfUid);
            else if (RoomManager.IsSingletonActive()) RoomManager.Instance.SendToServer(new BioChemWriteMessage { Kind = BioChemWriteMessage.TeacherHp });
        }

        // 长按板书读条期间：小老师体力缓慢回复（速率 = staminaPerWrite 摊到 blackboardHoldTime，与原生老师能量同节奏）。
        // isUsing 只在写作端为真（SetUsing(true) 仅本地交互入口调用），故直接取 localPlayer
        public static void AfterBoardTick(BlackBoardComponent __instance, float iDt)
        {
            if (!enabled || iDt <= 0f || !__instance.isUsing) return;
            if (!UnitManager.IsSingletonActive()) return;
            Unit self = UnitManager.Instance.localPlayer;
            if (self == null || !self.isActived) return;
            if (!infected.Contains(self.UID)) return;
            float holdTime = Mathf.Max(0.1f, ConfigManager.LevelInteractionSetting.blackboardHoldTime);
            selfStamina = Mathf.Min(Config.staminaMax, Mathf.Max(0f, selfStamina) + Config.staminaPerWrite / holdTime * iDt);
        }

        private static void ApplyWrite(byte kind, ulong uid)
        {
            Unit unit = UnitManager.IsSingletonActive() ? UnitManager.Instance.GetUnitByUID(uid) : null;
            if (unit == null || !unit.isPlayer || !unit.IsTeacher || infected.Contains(uid) || hunters.Contains(uid)) return;
            // 大老师按大老师上限取当前值：无记录时 GetHp(uid, false) 会错误初始化成小老师 HP
            hp[uid] = (int)Math.Min(GetTeacherMaxHp(uid, true), (long)GetHp(uid, true) + Config.teacherHpPerWrite);
            BroadcastState();
        }

        // ===== 连发与补弹请求 =====

        private static void OnServerDart(NetworkConnectionToClient connection, BioChemDartMessage message)
        {
            if (!NetRuntime.IsServer) return;
            if (message.Kind == BioChemDartMessage.MgFire) OnServerMgFire(connection, message);
            else if (message.Kind == BioChemDartMessage.EatRefill) OnServerEatRefill(connection);
        }

        // ===== 备弹 =====

        // 服务端权威备弹：未记录过视为满（懒初始化）
        private static int GetDartAmmo(ulong uid)
        {
            if (!dartAmmo.TryGetValue(uid, out int value)) return dartAmmo[uid] = Config.dartMaxAmmo;
            return value;
        }

        private static void BroadcastAmmo(ulong uid)
        {
            var sync = new BioChemDartMessage { Kind = BioChemDartMessage.AmmoSync, TargetUID = uid, TargetHp = GetDartAmmo(uid) };
            Broadcast(sync);
            ApplyAmmoSyncLocal(sync);
        }

        // 各端应用备弹同步；本人备弹增加（吃东西）时本地弹提示
        private static void ApplyAmmoSyncLocal(BioChemDartMessage message)
        {
            if (!UnitManager.IsSingletonActive() || message.TargetUID != UnitManager.Instance.localPlayerUID) return;
            int before = selfDartAmmo;
            selfDartAmmo = Mathf.Max(0, message.TargetHp);
            Ak47Weapon.OnAmmoSynced(); // 备弹权威校准：弹匣 clamp 到备弹内（备弹空则取消换弹）
            if (before >= 0 && selfDartAmmo > before)
                NotifySelf("食物补充了吹箭弹药：" + selfDartAmmo + "/" + Config.dartMaxAmmo);
        }

        // 吃完一份食物 → 服务端补弹（冷却防伪：吃完一份食物至少要吃几秒，刷不了）
        private static void OnServerEatRefill(NetworkConnectionToClient connection)
        {
            if (!NetRuntime.IsServer || !enabled || !ServerClassActive() || Config.dartAmmoPerFood <= 0) return;
            ulong sender = ResolveSender(connection);
            if (sender == 0 || infected.Contains(sender)) return; // 感染者没有吹箭筒
            if (Time.unscaledTime < (foodRefillReadyAt.TryGetValue(sender, out float readyAt) ? readyAt : 0f)) return;
            if (!UnitManager.IsSingletonActive()) return;
            Unit student = UnitManager.Instance.GetUnitByUID(sender);
            if (student == null || !student.isActived || student.IsDead || !student.IsStudent) return;
            foodRefillReadyAt[sender] = Time.unscaledTime + FoodRefillCooldown;
            if (GetDartAmmo(sender) >= Config.dartMaxAmmo) return; // 满弹：静默吃掉
            dartAmmo[sender] = Mathf.Min(Config.dartMaxAmmo, GetDartAmmo(sender) + Config.dartAmmoPerFood);
            BroadcastAmmo(sender);
            Log("[状态] " + GetName(sender) + " 吃东西补充吹箭弹药至 " + dartAmmo[sender] + "/" + Config.dartMaxAmmo);
        }

        internal static ulong EnsureAkEquipped(Unit student)
        {
            ulong uid = student.UID;
            var mouth = student.GetComponent<MouthItemUseComponent>();
            if (mouth == null) return 0;
            if (issuedTaser.TryGetValue(uid, out ulong itemUid))
            {
                Unit item = UnitManager.Instance.GetUnitByUID(itemUid);
                var mount = item?.GetData<RoleData>()?.NetData.MountingPointNet ?? default;
                if (item != null && item.isActived && (pendingEquip.ContainsKey(itemUid) || mouth.currentMouthItem == item
                    || (mount.HaveValue && mount.MountingUID == uid && mount.MountingPoint == EUnitPartType.Mouth)))
                    return itemUid;
                RevokeTaser(uid);
            }

            if (mouth.currentMouthItem != null) return 0;

            int id = TaserItemId();
            if (id <= 0)
            {
                Log("找不到吹箭筒物品配置（SubType=613 MountPos=15）");
                return 0;
            }
            try
            {
                Unit item = UnitManager.Instance.CreateCommonUnitDirectly(id, student.Position + Vector3.up);
                if (item == null) return 0;
                issuedTaser[uid] = item.UID;
                // 先向持有者确认物品 UID，再由 Tick 挂载，避免挂载和确认之间走原生单发。
                pendingEquip[item.UID] = (uid, Time.unscaledTime + 5f);
                BroadcastAmmo(uid);
                return item.UID;
            }
            catch (Exception e)
            {
                RevokeTaser(uid);
                Log("装备 AK47 失败：" + e.Message);
                return 0;
            }
        }

        // 每帧处理待挂载队列：模型加载完成后挂点才存在，成功或超时移出
        private static void TickDartEquip()
        {
            if (pendingEquip.Count == 0) return;
            foreach (ulong uid in new List<ulong>(pendingEquip.Keys))
                TickDartEquipOnce(uid);
        }

        private static void TickDartEquipOnce(ulong uid)
        {
            if (!pendingEquip.TryGetValue(uid, out var entry) || !UnitManager.IsSingletonActive()) return;
            Unit student = UnitManager.Instance.GetUnitByUID(entry.student);
            Unit item = UnitManager.Instance.GetUnitByUID(uid);
            if (!StudentWeapons.CanSelect(student) || item == null || !item.isActived)
            {
                RevokeTaser(entry.student);
                return;
            }
            if (Time.unscaledTime > entry.deadline)
            {
                RevokeTaser(entry.student);
                Log("[状态] " + GetName(entry.student) + " 吹箭筒挂载超时，已回收");
                return;
            }
            var mouth = student.GetComponent<MouthItemUseComponent>();
            if (mouth == null || mouth.currentMouthItem != null) return; // 嘴被占用，等下一帧
            if (mouth.TryEquipMouthItem(item))
            {
                pendingEquip.Remove(uid);
                Log("[状态] " + GetName(entry.student) + " 掏出吹箭筒");
            }
        }

        private static int TaserItemId()
        {
            if (taserItemId > 0) return taserItemId;
            if (cfg.Tables.TbUnit == null || cfg.Tables.TbItem == null) return -1;
            foreach (cfg.Unit unit in cfg.Tables.TbUnit.DataList)
            {
                if (unit.Type != 6 || unit.SubType != (int)EUnitSubType.Taser) continue;
                cfg.Item item = cfg.Tables.TbItem.GetOrDefault(unit.Id);
                if (item != null && item.MountPos == 15) // 嘴部叼持的吹箭
                {
                    taserItemId = unit.Id;
                    return taserItemId;
                }
            }
            return -1;
        }

        // 收起 AK47，同时取消模型尚未加载完成的挂载。
        internal static void RevokeTaser(ulong uid)
        {
            if (issuedTaser.Remove(uid, out ulong itemUid))
            {
                pendingEquip.Remove(itemUid);
                if (UnitManager.IsSingletonActive() && RoomManager.IsSingletonActive())
                {
                    Unit item = UnitManager.Instance.GetUnitByUID(itemUid);
                    // 原生服务端销毁入口会先在房主端解绑，再通知客机，快速切回主武器时嘴部已空出。
                    if (item != null && item.isActived)
                        RoomManager.Instance.GetUnitMessageHandler().OnUnitDestroyMessageRequest(null,
                            new UnitDestroyMessageRequest_C2S { unitUID = itemUid });
                }
            }
        }

        // ===== AK47 客户端造弹循环 =====

        // 本地玩家按住左键连发：构造准星弹道，按虚拟 UID 本地渲染 + C2S 上报。
        // 射手自己不发 S2C 回包（本地已预测渲染），服务端广播给其他端渲染
        private static void TickMachineGun()
        {
            if (!enabled || StudentWeapons.Selected != StudentWeaponSlot.Primary || !CanClassAct()) return;
            if (!Input.GetMouseButton(0)) return; // 按住左键
            if (Ak47Weapon.BlockFire()) return;   // 换弹中/空匣不发（空匣且还有备弹时按住扳机自动换弹）
            if (Time.unscaledTime < mgNextFireAt) return;
            if (selfDartAmmo <= 0) return; // 备弹耗尽（服务端 AmmoSync 权威校准，本地先扣保证手感）
            Unit self = UnitManager.Instance.localPlayer;
            if (!StudentWeapons.CanSelect(self) || self.IsStun) return;
            Unit mouthItem = self.GetComponent<MouthItemUseComponent>()?.currentMouthItem;
            if (!StudentWeapons.IsLocalAk(mouthItem) || !mouthItem.isActived) return;
            TaserComponent taser = mouthItem.GetComponent<TaserComponent>();
            if (taser == null || TaserTryBuildShotMethod == null) return;
            if (!MonoSingletonT<SceneManager>.IsSingletonActive()) return;
            Camera cam = MonoSingletonT<SceneManager>.Instance.MainCamera;
            if (cam == null) return;

            var args = new object[] { self, cam.transform, null };
            if (!(TaserTryBuildShotMethod.Invoke(taser, args) is bool ok) || !ok) return;
            var shot = (TaserFired_S2C)args[2];
            AimAkShot(cam, ref shot);

            mgNextFireAt = Time.unscaledTime + Config.fireInterval;
            if (selfDartAmmo > 0) selfDartAmmo--; // 本地乐观扣减，服务端 AmmoSync 随后校准
            // 虚拟弹体 UID：bit62-61 高位标记 + bit60-32 玩家 UID（截 29 位）+ bit31-0 序号，
            // 绝不与真实 unit UID 撞；间隔小于弹体飞行时间时弹体重叠，唯一 UID 保证注册/命中/销毁互不干扰
            ulong shotUid = VirtualShotUidBase
                | ((self.UID & 0x1FFFFFFFUL) << 32)
                | (uint)(++mgShotSeq);
            shot.accepted = true;
            shot.userUID = self.UID;
            shot.itemUID = shotUid;

            PlayFireShot(shot); // 本地预测渲染（曳光弹已换装为 AK 曳光）
            Ak47Weapon.OnShotFired(); // 弹匣-1 + 口焰 + 后坐
            var msg = new BioChemDartMessage
            {
                Kind = BioChemDartMessage.MgFire,
                TargetUID = shotUid,
                UserUID = self.UID,
                Origin = shot.origin,
                Direction = shot.direction,
                Speed = shot.speed,
                Range = shot.range,
                Radius = shot.radius,
                StunDuration = shot.stunDuration
            };
            if (NetRuntime.IsServer) OnServerMgFire(null, msg);
            else if (RoomManager.IsSingletonActive()) RoomManager.Instance.SendToServer(msg);
        }

        private static void AimAkShot(Camera camera, ref TaserFired_S2C shot)
        {
            Ray aimRay = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
            Vector3 aimDelta = aimRay.direction * shot.range;
            ulong shooterUid = shot.userUID;
            ulong itemUid = shot.itemUID;
            bool Accepts(Collider collider) => TaserBallistics.Accepts(collider, shooterUid, itemUid);
            int mask = TaserBallistics.CollisionMask;
            Vector3 aimPoint = akBallistics.TryHit(aimRay.origin, aimDelta, 0.001f, mask, Accepts, out _, out Vector3 hitPoint)
                ? hitPoint : aimRay.origin + aimDelta;

            Vector3 muzzle = Ak47Weapon.GetMuzzleWorldPos(shot.origin);
            Vector3 toMuzzle = muzzle - shot.origin;
            // 贴墙时把起点留在遮挡的近侧；准星目标太近时沿用原生起点，避免枪口后方的目标引起倒射。
            if (akBallistics.TryHit(shot.origin, toMuzzle, shot.radius, mask, Accepts, out _, out Vector3 blockedAt))
            {
                float travel = Mathf.Max(0f, Vector3.Distance(shot.origin, blockedAt) - 0.001f);
                muzzle = shot.origin + toMuzzle.normalized * travel;
            }
            if (Vector3.Dot(aimPoint - muzzle, aimRay.direction) > 0.001f) shot.origin = muzzle;
            Vector3 direction = aimPoint - shot.origin;
            shot.direction = Vector3.Dot(direction, aimRay.direction) > 0.001f ? direction.normalized : aimRay.direction;
        }

        private static void PlayFireShot(TaserFired_S2C shot)
        {
            bool wasRenderingAkShot = renderingAkShot;
            renderingAkShot = true;
            try { TaserPlayFireMethod?.Invoke(null, new object[] { shot }); }
            finally { renderingAkShot = wasRenderingAkShot; }
        }

        public static bool BeforeAkLaunchSound(AudioEvents eventName, Vector3 position)
        {
            return !renderingAkShot || eventName != ConfigManager.TaserSetting.launchSound
                || !Ak47Weapon.TryPlayShotAt(position);
        }

        private static TaserFired_S2C BuildShot(BioChemDartMessage m)
        {
            return new TaserFired_S2C
            {
                accepted = true,
                itemUID = m.TargetUID,
                userUID = m.UserUID,
                origin = m.Origin,
                direction = m.Direction.normalized,
                speed = m.Speed,
                range = m.Range,
                radius = m.Radius,
                stunDuration = m.StunDuration
            };
        }

        // ===== 连发吹箭筒：服务端结算 =====

        // 校验后把这一发注册进原生 taserShots（命中校验/眩晕/Hit 广播全走原生），再广播弹体渲染
        private static void OnServerMgFire(NetworkConnectionToClient connection, BioChemDartMessage message)
        {
            if (!NetRuntime.IsServer || message.Kind != BioChemDartMessage.MgFire) return;
            try
            {
                if (!enabled || !ServerClassActive() || !UnitManager.IsSingletonActive()) return;
                ulong sender = ResolveSender(connection);
                if (sender == 0 || infected.Contains(sender)) return; // 感染者没有吹箭筒
                if (Time.unscaledTime < (mgFireUntil.TryGetValue(sender, out float until) ? until : 0f)) return;
                if (message.TargetUID < VirtualShotUidBase) return; // 虚拟 UID 必须带高位标记
                if (GetDartAmmo(sender) <= 0) return; // 备弹耗尽：服务端拒发（客户端 UI 已提示吃东西补弹）
                if (float.IsNaN(message.Origin.x) || float.IsInfinity(message.Origin.x)
                    || float.IsNaN(message.Direction.x) || float.IsInfinity(message.Direction.x)) return;
                float dirMag = message.Direction.sqrMagnitude;
                if (dirMag < 0.5f || dirMag > 1.5f || message.Speed <= 0f || message.Range <= 0f || message.Radius <= 0f) return;
                // 射手嘴上必须真挂着吹箭（防伪造）
                Unit shooter = UnitManager.Instance.GetUnitByUID(sender);
                Unit mouthItem = shooter != null && shooter.isActived && !shooter.IsDead && shooter.IsStudent
                    ? shooter.GetComponent<MouthItemUseComponent>()?.currentMouthItem : null;
                if (mouthItem == null || !mouthItem.isActived || !issuedTaser.TryGetValue(sender, out ulong itemUid)
                    || mouthItem.UID != itemUid) return;

                mgFireUntil[sender] = Time.unscaledTime + Config.fireInterval;
                dartAmmo[sender] = GetDartAmmo(sender) - 1;
                RecordTaserShot(message.TargetUID, sender, message.StunDuration, message.Range, message.Speed);
                PlayFireShot(BuildShot(message)); // 服务器本机渲染（host 视角）；host 射手时 PlayFire 去重跳过
                Broadcast(new BioChemDartMessage
                {
                    Kind = BioChemDartMessage.MgShot,
                    TargetUID = message.TargetUID,
                    UserUID = sender,
                    Origin = message.Origin,
                    Direction = message.Direction,
                    Speed = message.Speed,
                    Range = message.Range,
                    Radius = message.Radius,
                    StunDuration = message.StunDuration
                });
                BroadcastAmmo(sender); // 每发校准射手端备弹，防网络抖动下与本地乐观扣减漂移
            }
            catch (Exception e)
            {
                Log("连发吹箭结算失败：" + e.Message);
            }
        }

        // 反射写原生 taserShots：值是 ValueTuple<ulong,float,int,EClassroomState,double>，索引器写入=存在则覆盖
        private static void RecordTaserShot(ulong shotUid, ulong shooterUid, float stunDuration, float range, float speed)
        {
            if (TaserShotsField == null || !RoomManager.IsSingletonActive() || !ClassroomManager.IsSingletonActive()) return;
            object handler = RoomManager.Instance.GetUnitMessageHandler();
            object dict = TaserShotsField.GetValue(handler);
            if (dict == null) return;
            Type tupleType = TaserShotsField.FieldType.GetGenericArguments()[1];
            object entry = Activator.CreateInstance(tupleType,
                shooterUid, Mathf.Max(0f, stunDuration),
                ClassroomManager.Instance.currentFloor, ClassroomManager.Instance.curState,
                Time.unscaledTimeAsDouble + (double)(range / Mathf.Max(0.1f, speed)) + 30.0);
            TaserShotsField.FieldType.GetProperty("Item")!.SetValue(dict, entry, new object[] { shotUid });
        }

        // ===== 原生吹箭命中：生化结算 =====

        internal const float DartDamageBonusCap = 3f; // 感染加成系数硬上限（×4 伤害封顶）

        // 吹箭伤害加成系数：每个已感染学生（小老师）+dartDamageBonusPerInfected，封顶 DartDamageBonusCap
        internal static float DartDamageBonus()
        {
            if (Config.dartDamageBonusPerInfected <= 0f) return 0f;
            return Mathf.Min(infected.Count * Config.dartDamageBonusPerInfected, DartDamageBonusCap);
        }

        // 当前有效吹箭伤害（服务端结算与各端 UI 显示共用同一公式；客机 infected 随快照/感染广播即时对齐）
        internal static int EffectiveDartDamage()
        {
            return Mathf.Max(1, Mathf.RoundToInt(Config.dartDamage * (1f + DartDamageBonus())));
        }

        // TaserProjectile.TryStun 是服务端/客机共用的眩晕入口；只在我们吹箭的原生结算窗口里拦
        public static bool BeforeTaserTryStun(ref bool __result)
        {
            if (!suppressVanillaStun) return true;
            __result = false;
            return false;
        }

        // 连发吹箭的虚拟弹体 UID（高位 0b011）；真实 unit UID 远小于该段
        private static bool IsModDartUid(ulong uid)
        {
            return (uid & 0xE000000000000000UL) == VirtualShotUidBase;
        }

        // prefix：记录这一发是否注册过 taserShots（原生校验通过才会注册）。
        // postfix 结算以此为门槛——被原生拒绝的弹（过期/未注册/伪造）不再扣血扣减速
        public static void BeforeTaserImpact(UnitMessageHandler __instance, TaserImpact_C2S impact)
        {
            lastImpactWasRegistered = false;
            // 我们模式的吹箭不再有原版眩晕：只在这一发原生结算期间让 TryStun 直接返回 false，
            // 学生被打中=完全无效，老师/小老师走下面的扣血+减速
            suppressVanillaStun = enabled && IsModDartUid(impact.itemUID);
            try
            {
                object dict = TaserShotsField?.GetValue(__instance);
                if (dict != null)
                    lastImpactWasRegistered = (bool)dict.GetType().GetMethod("ContainsKey")
                        .Invoke(dict, new object[] { impact.itemUID });
            }
            catch { lastImpactWasRegistered = false; }
        }

        // 服务端在原生 OnTaserImpact 之后调用：目标是我们模式里的老师玩家才扣 HP+减速；
        // 眩晕已由原生 TryStun 处理（大老师被吹箭晕 = 与游戏内行为一致）
        private static void AfterTaserImpact(UnitMessageHandler __instance, TaserImpact_C2S impact)
        {
            suppressVanillaStun = false;
            try
            {
                if (!enabled || !NetRuntime.IsServer || !impact.collided) return;
                if (!lastImpactWasRegistered) return; // 未注册的弹（原生已拒绝）不结算
                if (!UnitManager.IsSingletonActive()) return;
            Unit target = UnitManager.Instance.GetUnitByUID(impact.targetUID);
            if (target == null || !target.isActived || target.IsDead) return;
            if (!target.isPlayer || !target.IsTeacher) return;
            if (downedUntil.ContainsKey(target.UID)) return;
            if (hunters.Contains(target.UID) || killedTeachers.Contains(target.UID)) return; // 吹箭打不到猎手，出局的老师也不再结算
            if (!ServerClassActive()) return;

                var expiries = PruneServerSlow(target.UID);
                if (expiries.Count < Config.dartSlowMaxStacks) // 层数封顶，与服务端移速口径一致
                    expiries.Add(Time.unscaledTime + Config.dartSlowSeconds);
                byte stacks = (byte)Mathf.Min(Config.dartSlowMaxStacks, expiries.Count);
                int damage = EffectiveDartDamage();
                int newHp = GetHp(target.UID, !infected.Contains(target.UID)) - damage;
                hp[target.UID] = Mathf.Max(0, newHp);
                var hit = new BioChemDartMessage
                {
                    TargetUID = target.UID,
                    TargetHp = hp[target.UID],
                    SlowStacks = stacks
                };
                Broadcast(hit);
                ApplyDartHit(hit);
                Log("[状态] 吹箭命中 " + GetName(target.UID) + "（伤害 " + damage + "，感染加成 +" + (DartDamageBonus() * 100f).ToString("0") + "%），HP " + hp[target.UID] + "，减速 " + stacks + "/" + Config.dartSlowMaxStacks);
                if (hp[target.UID] <= 0) DownUnit(target.UID);
            }
            catch (Exception e)
            {
                Log("吹箭命中结算失败：" + e.Message);
            }
        }

        // 各端数据同步：被命中者把减速层数校准到服务端权威值（消息里带 SlowStacks，服务端封顶+过期裁剪；
        // 旧版本地盲加一层，层数在丢包/抖动下与主机漂移，表现成"减速只在主机生效"）。
        // 弹道/音效/特效由原生 ApplyHit 播放
        private static void ApplyDartHit(BioChemDartMessage message)
        {
            if (message.TargetHp >= 0) hp[message.TargetUID] = message.TargetHp;
            if (UnitManager.IsSingletonActive() && message.TargetUID == UnitManager.Instance.localPlayerUID)
            {
                PruneSelfSlow();
                while (selfSlowExpiries.Count < message.SlowStacks)
                    selfSlowExpiries.Add(Time.unscaledTime + Config.dartSlowSeconds);
                while (selfSlowExpiries.Count > message.SlowStacks)
                    selfSlowExpiries.RemoveAt(0);
                NotifySelf("你被吹箭命中！剩余 HP " + message.TargetHp);
                Log("[状态] 本端命中同步：HP " + message.TargetHp + "，减速 " + selfSlowExpiries.Count + " 层");
            }
        }

        // ===== 吃东西补弹检测 =====

        // 普通食物吃完最后一口（判定与原生 FoodComponent.OnItemUseSuccess 一致：入口耐久 ∈ (0,1]）→ 上报补弹。
        // 香蕉/马桶食物子类的吃法都调 base，补丁基类全覆盖；peel/leftovers 分支不调 base（不是吃），天然不触发
        public static void BeforeFoodFinishingBite(FoodComponent __instance, Unit user)
        {
            if (!enabled || user == null || !user.isLocalPlayer) return;
            ItemData data = __instance.owner != null ? __instance.owner.GetData<ItemData>() : null;
            if (data == null || data.CurrentDurability <= 0f || data.CurrentDurability > 1f) return;
            SendEatRefill();
        }

        // 火锅食材被吃光：服务端 Eat 操作把 RemainingDurability 扣到 0 → Consumed 翻转广播回各端，
        // 请求者本人端上报补弹（prefix 抓翻转前状态；postfix 用 __instance.State 判翻转——Apply 对旧包有 version 门槛，直接读响应会被乱序旧包误判）
        private static bool hotPotApplyPrevConsumed;

        public static void BeforeHotPotApply(HotPotIngredientComponent __instance, HotPotState_S2C response)
        {
            hotPotApplyPrevConsumed = __instance.State.Consumed;
        }

        public static void AfterHotPotApply(HotPotIngredientComponent __instance, HotPotState_S2C response)
        {
            if (!enabled || hotPotApplyPrevConsumed || !__instance.State.Consumed) return;
            if (!UnitManager.IsSingletonActive() || response.RequestUserUID != UnitManager.Instance.localPlayerUID) return;
            SendEatRefill();
        }

        private static void SendEatRefill()
        {
            if (NetRuntime.IsServer) OnServerEatRefill(null);
            else if (RoomManager.IsSingletonActive()) RoomManager.Instance.SendToServer(new BioChemDartMessage { Kind = BioChemDartMessage.EatRefill });
        }

        // 减速：教师团队本地玩家移速乘 (1 - 层数×0.2)
        public static void AfterMoveSpeed(InputComponent __instance, ref float __result)
        {
            if (__result <= 0f || selfSlowExpiries.Count == 0 || !enabled) return;
            Unit owner = __instance.owner;
            if (owner == null || !owner.isLocalPlayer || owner.IsDead || !owner.IsTeacher) return;
            PruneSelfSlow();
            __result *= Mathf.Max(0.05f, 1f - Config.dartSlowPerStack * selfSlowExpiries.Count);
        }

        // ===== 小老师击晕 =====

        internal static void TryMiniStun()
        {
            if (!CanClassAct() || !enabled) return;
            if (!UnitManager.IsSingletonActive()) return;
            ulong self = UnitManager.Instance.localPlayerUID;
            if (!infected.Contains(self)) return;
            if (Time.unscaledTime < localStunReadyAt) return;
            float have = SelfStamina();
            if (have < Config.staminaCostPerStun) return;
            Unit selfUnit = UnitManager.Instance.GetUnitByUID(self);
            if (selfUnit == null || !selfUnit.isActived || selfUnit.IsDead || selfUnit.IsStun) return;
            Unit target = FindStunTarget(selfUnit);
            if (target == null) return;
            localStunReadyAt = Time.unscaledTime + Config.miniStunCooldownSeconds;
            // 本地模拟立即扣减；服务端按申报值校验后记录权威值，漂移自愈
            selfStamina = Mathf.Max(0f, have - Config.staminaCostPerStun);
            var message = new BioChemStunMessage { Kind = BioChemStunMessage.Request, TargetUID = target.UID, Stamina = have };
            if (NetRuntime.IsServer) OnServerStun(null, message);
            else if (RoomManager.IsSingletonActive()) RoomManager.Instance.SendToServer(message);
        }

        private static Unit FindStunTarget(Unit self)
        {
            Unit best = null;
            float bestDist = Config.miniStunRange;
            foreach (Unit unit in UnitManager.Instance.playerList)
            {
                if (unit == null || !unit.isActived || unit.IsDead || unit.IsStun) continue;
                bool targetable = unit.IsStudent || hunters.Contains(unit.UID); // 猎手对游戏已是老师身份，单独放行
                if (!targetable) continue;
                float dist = Vector3.Distance(self.Position, unit.Position);
                if (dist < bestDist) { bestDist = dist; best = unit; }
            }
            return best;
        }

        private static void OnServerStun(NetworkConnectionToClient connection, BioChemStunMessage message)
        {
            if (!NetRuntime.IsServer || message.Kind != BioChemStunMessage.Request) return;
            if (!enabled || !ServerClassActive()) return;
            ulong senderUID = ResolveSender(connection);
            if (!infected.Contains(senderUID)) return;
            if (Time.unscaledTime < (stunCooldownUntil.TryGetValue(senderUID, out float until) ? until : 0f)) return;
            // 体力以申报值为准（各端本地模拟），clamp 后校验够不够；不够直接拒绝
            float claimed = Mathf.Clamp(message.Stamina, 0f, Config.staminaMax);
            if (claimed < Config.staminaCostPerStun) return;
            if (!UnitManager.IsSingletonActive()) return;
            Unit sender = UnitManager.Instance.GetUnitByUID(senderUID);
            Unit target = UnitManager.Instance.GetUnitByUID(message.TargetUID);
            if (sender == null || !sender.isActived || sender.IsDead || sender.IsStun) return;
            bool targetIsHunter = hunters.Contains(message.TargetUID);
            if (target == null || !target.isActived || target.IsDead || target.IsStun) return;
            if (!target.IsStudent && !targetIsHunter) return; // 学生或猎手（猎手对游戏已是老师身份，单独放行）
            if (Vector3.Distance(sender.Position, target.Position) > Config.miniStunRange + 0.5f) return;

            stamina[senderUID] = Mathf.Max(0f, claimed - Config.staminaCostPerStun);
            stunCooldownUntil[senderUID] = Time.unscaledTime + Config.miniStunCooldownSeconds;
            var apply = new BioChemStunMessage { Kind = BioChemStunMessage.Apply, TargetUID = target.UID, Duration = Config.miniStunSeconds };
            Broadcast(apply);
            ApplyStunLocal(apply.TargetUID, apply.Duration);
            if (targetIsHunter) DamageHunter(target.UID, HunterTakenDamage, senderUID); // 晕猎手=晕住+扣血
            BroadcastState();
            Log("[状态] 小老师 " + GetName(senderUID) + (targetIsHunter ? " 击晕了猎手 " : " 击晕了 ") + GetName(target.UID));
        }

        // 各端应用：学生眩晕指定时长（带精确时长的投射物眩晕通道，与电击枪表现一致）
        private static void ApplyStunLocal(ulong targetUID, float duration)
        {
            if (!UnitManager.IsSingletonActive()) return;
            Unit target = UnitManager.Instance.GetUnitByUID(targetUID);
            if (target == null || !target.isActived) return;
            target.GetComponent<StudentStunComponent>()?.TryGetProjectileStun(duration);
            if (target.isPlayer && target.UID == UnitManager.Instance.localPlayerUID)
                NotifySelf("你被小老师击晕了！");
        }

        // ===== 倒地与办公室复活 =====

        private static void DownUnit(ulong uid)
        {
            MiniTeacherCatch.ForceReleaseIfCarrying(uid); // 小老师无 TeacherStunComponent，原生"被晕放手"不生效——强制放下
            downedUntil[uid] = Time.unscaledTime + Config.downSeconds;
            var down = new BioChemDownMessage { Kind = BioChemDownMessage.Down, UnitUID = uid, Position = FindOfficePosition(uid) };
            Broadcast(down);
            ApplyDownLocal(down);
            Log("[状态] " + GetName(uid) + " HP 归零晕倒，" + Config.downSeconds.ToString("0") + " 秒后在办公室复活");
        }

        // 找办公室复活点：大老师的出生点（老师开局在办公室）；找不到退回本人出生点。
        // 必须用原生 UnitSubType 判定——用翻转后的 IsTeacher 会把猎手当成锚点（猎手出生在课桌不在办公室）
        private static Vector3 FindOfficePosition(ulong uid)
        {
            if (UnitManager.IsSingletonActive())
            {
                foreach (Unit unit in UnitManager.Instance.playerList)
                {
                    if (unit != null && unit.isActived && unit.isPlayer && unit.UnitSubType == EUnitSubType.Teacher && !infected.Contains(unit.UID))
                        return unit.BornPosition;
                }
                Unit self = UnitManager.Instance.GetUnitByUID(uid);
                if (self != null) return self.BornPosition;
            }
            return Vector3.zero;
        }

        // 各端应用晕倒表现；被晕倒者自己的端计时后等待 Respawn 传送
        private static void ApplyDownLocal(BioChemDownMessage message)
        {
            if (!UnitManager.IsSingletonActive()) return;
            Unit unit = UnitManager.Instance.GetUnitByUID(message.UnitUID);
            if (unit == null || !unit.isActived)
            {
                Log("[状态] 倒地表现未应用：" + GetName(message.UnitUID) + "（单位不存在或未激活）");
                return;
            }
            var teacherStun = unit.GetComponent<TeacherStunComponent>();
            if (teacherStun != null) teacherStun.TryGetProjectileStun(Config.downSeconds);
            else unit.GetComponent<StudentStunComponent>()?.TryGetProjectileStun(Config.downSeconds);
            NotifyAll(GetName(message.UnitUID) + " 倒下了！将在办公室复活");
            Log("[状态] 倒地表现已应用：" + GetName(message.UnitUID) + "（眩晕 " + Config.downSeconds.ToString("0.#") + "s）");
        }

        private static void TickDowned()
        {
            if (downedUntil.Count == 0) return;
            List<ulong> respawned = null;
            foreach (var pair in downedUntil)
            {
                if (Time.unscaledTime < pair.Value) continue;
                (respawned ??= new List<ulong>()).Add(pair.Key);
            }
            if (respawned == null) return;
            foreach (ulong uid in respawned)
            {
                downedUntil.Remove(uid);
                bool isMini = infected.Contains(uid);
                hp[uid] = GetTeacherMaxHp(uid, !isMini);
                if (isMini) stamina[uid] = Config.staminaMax;
                stunCooldownUntil.Remove(uid);
                serverSlowExpiries.Remove(uid);
                var respawn = new BioChemDownMessage { Kind = BioChemDownMessage.Respawn, UnitUID = uid, Position = FindOfficePosition(uid) };
                Broadcast(respawn);
                ApplyRespawnLocal(respawn);
            }
            BroadcastState();
        }

        // 复活：清掉倒地眩晕，走原生传送，并立即回满 HP 和体力。
        // 眩晕不能指望"刚好自然到期"：Down 消息被收到时才起表，比服务端的 downedUntil 晚一个网络延迟，
        // 复活瞬间被传送者往往还带着零点几秒的残晕（躺着被传走）；残晕又压着移速，看起来就是"复活后状态没刷新"。
        // HP 不能只等 5 秒快照：TickDowned 只重置服务端字典，客机要等下一份快照 HP 条才满上。
        private static void ApplyRespawnLocal(BioChemDownMessage message)
        {
            if (!UnitManager.IsSingletonActive()) return;
            Unit unit = UnitManager.Instance.GetUnitByUID(message.UnitUID);
            if (unit == null || !unit.isActived)
            {
                Log("[状态] 复活未应用：" + GetName(message.UnitUID) + "（单位不存在或未激活）");
                return;
            }
            var teacherStun = unit.GetComponent<TeacherStunComponent>();
            if (teacherStun != null)
            {
                try { TeacherClearStunMethod?.Invoke(teacherStun, new object[] { false }); }
                catch (Exception e) { Log("清除老师眩晕失败：" + e.Message); }
            }
            else unit.GetComponent<StudentStunComponent>()?.InterruptStun();
            try { ApplyPlayerRelocationMethod?.Invoke(unit, new object[] { message.Position }); }
            catch (Exception e) { Log("复活传送失败：" + e.Message); }
            bool isMini = infected.Contains(message.UnitUID);
            hp[message.UnitUID] = GetTeacherMaxHp(message.UnitUID, !isMini);
            if (isMini) stamina[message.UnitUID] = Config.staminaMax;
            if (message.UnitUID == UnitManager.Instance.localPlayerUID)
            {
                RestoreLocalTeacherCondition(unit, isMini);
                localStunReadyAt = 0f;
                NotifySelf("已在办公室复活，生命和体力已回满");
            }
            Log("[状态] 已应用复活：" + GetName(message.UnitUID) + "（清眩晕+传送+HP " + hp[message.UnitUID] + "）");
        }

        private static void RestoreLocalTeacherCondition(Unit teacher, bool isMini)
        {
            selfSlowExpiries.Clear();
            if (isMini)
            {
                selfStamina = Config.staminaMax;
                lastSelfPosValid = false;
            }
            else if (teacher.UnitSubType == EUnitSubType.Teacher && teacher.GetComponent<SkillComponent>() is SkillComponent skill)
                skill.AddSkillEnergy(ConfigManager.TeacherSetting.maxSkillEnergy - skill.currentSkillEnergy);
        }

        // ===== 老师猎手 =====

        internal static bool LocalIsHunter
        {
            get
            {
                if (!UnitManager.IsSingletonActive() || !enabled) return false;
                return hunters.Contains(UnitManager.Instance.localPlayerUID);
            }
        }

        // 变身已解锁（剩余学生 ≤ 阈值）：信息盒提示用
        internal static bool HunterUnlocked => enabled && UnitManager.IsSingletonActive() && RemainingStudents() <= Config.hunterTriggerStudents;

        internal static int GetHunterHp(ulong uid)
        {
            return hp.TryGetValue(uid, out int value) ? value : Config.hunterHp;
        }

        // 剩余可变身学生数：存活、未被抓（103=罚站）、未感染、未变身的真人学生
        internal static int RemainingStudents()
        {
            if (!UnitManager.IsSingletonActive()) return 0;
            int count = 0;
            foreach (Unit unit in UnitManager.Instance.playerList)
                if (IsRemainingStudent(unit)) count++;
            return count;
        }

        private static bool IsRemainingStudent(Unit unit)
        {
            return unit != null && unit.isPlayer && unit.isActived && !unit.IsDead
                && unit.UnitSubType == EUnitSubType.Student
                && !infected.Contains(unit.UID) && !hunters.Contains(unit.UID) && unit.CurState() != 103;
        }

        private static void OnServerHunter(NetworkConnectionToClient connection, BioChemHunterMessage message)
        {
            if (!NetRuntime.IsServer) return;
            ulong sender = ResolveSender(connection);
            if (message.Kind == BioChemHunterMessage.TransformRequest) TryTransformHunter(sender);
            else if (message.Kind == BioChemHunterMessage.Strike) TryHunterStrike(sender, message);
        }

        // 按 G 变身：剩余学生 ≤ hunterTriggerStudents 时，未感染未被抓的存活学生可变身
        private static void TryTransformHunter(ulong uid)
        {
            if (!enabled || !ServerClassActive() || uid == 0) return;
            if (infected.Contains(uid) || hunters.Contains(uid) || killedTeachers.Contains(uid)) return;
            if (!UnitManager.IsSingletonActive()) return;
            Unit student = UnitManager.Instance.GetUnitByUID(uid);
            if (student == null || !student.isActived || student.IsDead || student.IsStun) return;
            if (student.UnitSubType != EUnitSubType.Student || student.CurState() == 103) return;
            if (RemainingStudents() > Config.hunterTriggerStudents) return;
            hunters.Add(uid);
            hp[uid] = Config.hunterHp;
            StudentWeapons.RemovePlayer(uid);
            var message = new BioChemHunterMessage { Kind = BioChemHunterMessage.Transform, UnitUID = uid };
            Broadcast(message);
            ApplyHunterTransformLocal(message);
            BroadcastState();
            Log("[状态] " + GetName(uid) + " 变身成了老师猎手（HP " + Config.hunterHp + "）");
        }

        // 各端变身表现：先入 hunters（身份翻转/物品封禁立刻生效，模型未绑定的过渡帧也不能丢身份），
        // 再做放大+特效+发刀（模型未就绪时视觉部分跳过，快照自愈/Tick 兜底）
        private static void ApplyHunterTransformLocal(BioChemHunterMessage message)
        {
            if (!UnitManager.IsSingletonActive()) return;
            hunters.Add(message.UnitUID);
            Unit unit = UnitManager.Instance.GetUnitByUID(message.UnitUID);
            if (unit == null || !unit.isActived || unit.model == null) return;
            if (!hunterOrigScale.ContainsKey(message.UnitUID))
            {
                hunterOrigScale[message.UnitUID] = unit.model.gameObject.transform.localScale;
                unit.model.gameObject.transform.localScale = unit.model.gameObject.transform.localScale * HunterScale;
            }
            unit.GetComponent<EffectComponent>()?.AddEffect(100004, 1f, Vector3.up, unit.transform, 2f);
            HunterKnife.Ensure(unit); // 变身即发刀（各端本地建视觉；手骨未就绪时 Tick 自愈补建）
            if (message.UnitUID == UnitManager.Instance.localPlayerUID)
            {
                StudentWeapons.RemovePlayer(message.UnitUID);
                // 变身时若正按住使用物品，输入被吞后松开事件也收不到——主动停用防"物品自动续用"卡滞
                unit.GetComponent<StudentItemUseComponent>()?.StopUseItem();
            }
            NotifyAll(GetName(message.UnitUID) + " 变身成了老师猎手！");
        }

        // 猎手被感染/还原：外观缩回原样（infected 名单走快照同步）
        private static void RevertHunterVisual(ulong uid)
        {
            var message = new BioChemHunterMessage { Kind = BioChemHunterMessage.RevertVisual, UnitUID = uid };
            Broadcast(message);
            ApplyRevertVisualLocal(message);
        }

        private static void ApplyRevertVisualLocal(BioChemHunterMessage message)
        {
            hunters.Remove(message.UnitUID);
            HunterKnife.Remove(message.UnitUID); // 猎手身份没了，收刀（挂在手上随模型还在，但不再属于猎手）
            if (!UnitManager.IsSingletonActive()) return;
            Unit unit = UnitManager.Instance.GetUnitByUID(message.UnitUID);
            if (hunterOrigScale.TryGetValue(message.UnitUID, out Vector3 origScale))
            {
                if (unit != null && unit.model != null)
                    unit.model.gameObject.transform.localScale = origScale;
                hunterOrigScale.Remove(message.UnitUID);
            }
        }

        // 猎手近战（服务端权威扇形扫击，CF 生化手感）：客户端只上报挥刀+点击瞬间准星水平方向，
        // 服务端以权威位置为圆心做 范围×扇形 群体判定——一刀挥到几个算几个，判定跟准星走。
        // Origin 过 5m 伪造门槛（抄原生剑击 OnTeacherSlashFire）；晕眩目标可补刀；空挥只做表现不结算
        private static void TryHunterStrike(ulong hunterUid, BioChemHunterMessage message)
        {
            if (!enabled || !ServerClassActive() || !hunters.Contains(hunterUid)) return;
            if (Time.unscaledTime < (hunterStrikeReadyAt.TryGetValue(hunterUid, out float readyAt) ? readyAt : 0f)) return;
            if (!UnitManager.IsSingletonActive()) return;
            Unit hunter = UnitManager.Instance.GetUnitByUID(hunterUid);
            if (hunter == null || !hunter.isActived || hunter.IsDead || hunter.IsStun) return;
            Vector3 origin = message.Origin;
            if ((new Vector3(origin.x, 0f, origin.z) - new Vector3(hunter.Position.x, 0f, hunter.Position.z)).sqrMagnitude > 25f) return;
            hunterStrikeReadyAt[hunterUid] = Time.unscaledTime + Config.hunterAttackInterval;
            var swing = new BioChemHunterMessage { Kind = BioChemHunterMessage.Swing, UnitUID = hunterUid };
            Broadcast(swing);
            ApplyHunterSwingLocal(swing); // 房主本机（Broadcast 不含房主）；身体动画由攻击者 PlayAnimSync 原生同步
            Vector3 forward = message.Direction;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) { forward = hunter.transform.forward; forward.y = 0f; }
            forward.Normalize();
            CollectSweepTargets(hunter, forward, sweepBuffer);
            foreach (Unit target in sweepBuffer)
                DamageTeacherTeam(target.UID, Config.hunterDamage, hunterUid);
            TryFinishHunterWin();
        }

        // 扇形扫击目标收集（服务端判定与攻击者本地预测共用同一口径）：
        // 老师阵营、非猎手、未出局、存活（晕眩可补刀）；水平距离 HunterAttackRange 内、
        // 以 forward 为轴的 HunterSlashAngle 扇形内；贴脸（≈同位置）不做角度检查必中
        private static void CollectSweepTargets(Unit hunter, Vector3 forward, List<Unit> results)
        {
            results.Clear();
            float maxDistSqr = HunterAttackRange * HunterAttackRange;
            foreach (Unit unit in UnitManager.Instance.playerList)
            {
                if (!IsLivingTeacherOpponent(unit)) continue;
                Vector3 to = unit.Position - hunter.Position;
                to.y = 0f; // 水平距离判定：跳起/蹲下不影响挥刀命中
                if (to.sqrMagnitude > maxDistSqr) continue;
                if (to.sqrMagnitude > 0.0001f && Vector3.Angle(forward, to) > HunterSlashAngle * 0.5f) continue;
                results.Add(unit);
            }
        }

        private static bool IsLivingTeacherOpponent(Unit unit)
        {
            return unit != null && unit.isActived && !unit.IsDead
                && (unit.UnitSubType == EUnitSubType.Teacher || infected.Contains(unit.UID))
                && !hunters.Contains(unit.UID) && !killedTeachers.Contains(unit.UID);
        }

        // 各端挥刀表现：攻击者本地已预测（跳过），其他端摆刀 + 在猎手位置播挥砍音效
        private static void ApplyHunterSwingLocal(BioChemHunterMessage message)
        {
            if (message.UnitUID == (UnitManager.IsSingletonActive() ? UnitManager.Instance.localPlayerUID : 0)) return;
            HunterKnife.PlaySwing(message.UnitUID);
            if (!UnitManager.IsSingletonActive()) return;
            Unit hunter = UnitManager.Instance.GetUnitByUID(message.UnitUID);
            if (hunter != null && hunter.isActived)
                PlayHunterSound(AudioEvents.sfx_DoubaoSwordSwing_sfx, hunter.Position);
        }

        // 猎手打老师：扣 HP，归零=永久击杀（不复活）；没死走 Hit 广播同步
        private static void DamageTeacherTeam(ulong targetUid, int amount, ulong attackerUid)
        {
            int newHp = Mathf.Max(0, GetTeacherTeamHp(targetUid) - amount);
            hp[targetUid] = newHp;
            Log("[状态] 猎手 " + GetName(attackerUid) + " 近战命中 " + GetName(targetUid) + "，HP " + newHp);
            if (newHp <= 0)
            {
                KillTeacher(targetUid);
                return;
            }
            var hit = new BioChemHunterMessage { Kind = BioChemHunterMessage.Hit, UnitUID = targetUid, Hp = newHp, AttackerUID = attackerUid };
            Broadcast(hit);
            ApplyHunterHitLocal(hit);
        }

        // 老师队伍（大老师+小老师）被打的扣血入口：大老师扣大老师上限、小老师扣小老师上限
        private static int GetTeacherTeamHp(ulong uid)
        {
            bool isBig = UnitManager.IsSingletonActive()
                && UnitManager.Instance.GetUnitByUID(uid)?.UnitSubType == EUnitSubType.Teacher;
            return GetHp(uid, isBig);
        }

        // 击杀出局：永久倒地（各端持续续晕压躺），不进 downedUntil 复活流程
        private static void KillTeacher(ulong uid)
        {
            downedUntil.Remove(uid); // 猎刀补掉枪击倒地的老师时，取消已排队的复活。
            killedTeachers.Add(uid);
            try { MiniTeacherCatch.ForceReleaseIfCarrying(uid); }
            catch (Exception e) { Log("[状态] 出局老师放下学生失败：" + e.Message); }
            var kill = new BioChemDownMessage { Kind = BioChemDownMessage.Kill, UnitUID = uid, Position = Vector3.zero };
            Broadcast(kill);
            ApplyKillLocal(kill);
            BroadcastState();
            Log("[状态] " + GetName(uid) + " 被猎手击杀，出局！");
        }

        private static void ApplyKillLocal(BioChemDownMessage message)
        {
            if (!UnitManager.IsSingletonActive()) return;
            killedTeachers.Add(message.UnitUID);
            Unit unit = UnitManager.Instance.GetUnitByUID(message.UnitUID);
            if (unit == null || !unit.isActived) return;
            unit.GetComponent<TeacherStunComponent>()?.TryGetProjectileStun(Config.downSeconds);
            unit.GetComponent<StudentStunComponent>()?.TryGetProjectileStun(Config.downSeconds);
            // 击杀特效：命中喷溅+电弧双重爆发（全员可见，配合永久倒地表现）
            SingletonT<FxManager>.Instance.SpawnFx(100002, unit.Position + Vector3.up, 0uL);
            SingletonT<FxManager>.Instance.SpawnFx(100004, unit.Position + Vector3.up, 0uL);
            NotifyAll(GetName(message.UnitUID) + " 被猎手击杀了！");
        }

        // 出局老师每帧续晕：stun 到期当帧立即续上（剩 2 秒，无站立空窗）
        private static void TickKilledTeachers()
        {
            if (killedTeachers.Count == 0 || !UnitManager.IsSingletonActive()) return;
            foreach (ulong uid in killedTeachers)
            {
                Unit unit = UnitManager.Instance.GetUnitByUID(uid);
                if (unit == null || !unit.isActived) continue;
                unit.GetComponent<TeacherStunComponent>()?.TryGetProjectileStun(2f);
                unit.GetComponent<StudentStunComponent>()?.TryGetProjectileStun(2f);
            }
        }

        // 老师全灭 → 直接判学生胜利（大老师+全部小老师都被猎手击杀）
        private static void TryFinishHunterWin()
        {
            if (!enabled || !NetRuntime.IsServer || !ServerClassActive() || killedTeachers.Count == 0) return;
            if (!UnitManager.IsSingletonActive()) return;
            foreach (Unit unit in UnitManager.Instance.playerList)
                if (IsLivingTeacherOpponent(unit)) return;

            ClassroomManager.Instance.ClassEnd(false);
            Log("[状态] 老师阵营全灭，判学生胜利！");
            NotifyAll("老师阵营全灭，学生胜利！");
        }

        // 游戏源码拼写如此（Studeng）
        private static readonly MethodInfo checkAllPunishedMethod =
            AccessTools.Method(typeof(ClassroomManager), "CheckIfAllStudengPunished");

        // CF 规则：学生阵营全灭（无未感染学生、无存活猎手）→ 老师立即胜利
        private static void TickStudentWipeCheck()
        {
            if (!enabled || !NetRuntime.IsServer || !ServerClassActive() || infected.Count == 0) return;
            if (RemainingStudents() > 0 || hunters.Count > 0) return;
            if (!UnitManager.IsSingletonActive()) return;
            Log("[状态] 学生全部被感染，老师胜利！");
            NotifyAll("学生全部被感染，老师胜利！");
            ClassroomManager.Instance.ClassEnd(true);
        }

        // 猎手固定只能用近战武器：两个物品输入总入口（MsgCenter 100）对猎手全部吞掉
        private static bool HunterItemInputBlocked(Unit owner)
        {
            if (!enabled || owner == null || !owner.isPlayer) return false;
            return hunters.Contains(owner.UID);
        }

        public static bool BeforeHunterStudentItemInput(StudentItemUseComponent __instance)
        {
            return !HunterItemInputBlocked(__instance.owner);
        }

        public static bool BeforeHunterMouthItemInput(MouthItemUseComponent __instance)
        {
            return !HunterItemInputBlocked(__instance.owner);
        }

        // 老师剑击弹体命中过滤放行猎手（原生只认 IsStudent）
        public static void AfterSlashStudentFilter(UnityEngine.Collider collider, ref Unit __result)
        {
            if (__result != null || !enabled || !UnitManager.IsSingletonActive()) return;
            ObjectModel componentInParent = collider != null ? collider.GetComponentInParent<ObjectModel>() : null;
            Unit unit = componentInParent != null ? UnitManager.Instance.GetUnitByUID(componentInParent.UID) : null;
            if (unit != null && unit.isActived && !unit.IsDead && hunters.Contains(unit.UID)) __result = unit;
        }

        // 服务端剑击结算：命中猎手 → 扣 HP（原生对 IsTeacher 目标会直接 return）。
        // prefix 跳过了原生的上报者/位置校验，这里补上同款防伪：上报者=老师本人、命中点距猎手 ≤3m
        public static bool BeforeChalkHit(UnitMessageHandler __instance, NetworkConnectionToClient connectionToClient, ChalkHitRequest_C2S request)
        {
            if (!enabled || !NetRuntime.IsServer || !hunters.Contains(request.UnitUID)) return true;
            if (killedTeachers.Contains(request.UnitUID)) return false;
            ulong sender = ResolveSender(connectionToClient);
            if (sender != request.TeacherUID) return true;
            Unit hunter = UnitManager.IsSingletonActive() ? UnitManager.Instance.GetUnitByUID(request.UnitUID) : null;
            if (hunter == null || !hunter.isActived || hunter.IsDead) return false;
            if ((hunter.Position - request.Position).sqrMagnitude > 9f) return true; // 与原生同款 3m 门槛，伪造上报交回原生拒绝
            DamageHunter(request.UnitUID, HunterTakenDamage, request.TeacherUID);
            return false;
        }

        // 猎手被打（老师剑击/小老师击晕）：扣 HP，归零=直接感染
        private static void DamageHunter(ulong uid, int amount, ulong attackerUid)
        {
            int newHp = Mathf.Max(0, GetHunterHp(uid) - amount);
            hp[uid] = newHp;
            var hit = new BioChemHunterMessage { Kind = BioChemHunterMessage.Hit, UnitUID = uid, Hp = newHp };
            Broadcast(hit);
            ApplyHunterHitLocal(hit);
            Log("[状态] 猎手 " + GetName(uid) + " 被 " + GetName(attackerUid) + " 命中，HP " + newHp);
            if (newHp <= 0)
            {
                hunters.Remove(uid);
                RevertHunterVisual(uid);
                InfectStudent(uid); // HP 没了直接感染成小老师（内部公告+快照）
            }
        }

        private static void ApplyHunterHitLocal(BioChemHunterMessage message)
        {
            hp[message.UnitUID] = message.Hp;
            if (!UnitManager.IsSingletonActive()) return;
            Unit unit = UnitManager.Instance.GetUnitByUID(message.UnitUID);
            if (unit != null && unit.isActived)
            {
                SingletonT<FxManager>.Instance.SpawnFx(ConfigManager.TaserSetting.characterHitEffectID, unit.Position + Vector3.up, Vector3.zero, 0uL);
                // 攻击者本机已在挥刀预测时即时播过命中音（AttackerUID 标记），跳过防双响；其余端正常播
                bool selfAttacked = message.AttackerUID != 0 && message.AttackerUID == UnitManager.Instance.localPlayerUID;
                if (!selfAttacked)
                    PlayHunterSound(AudioEvents.sfx_DoubaoSwordHit_sfx, unit.Position); // 猎刀命中音效（复用老师剑）
            }
            if (message.UnitUID == UnitManager.Instance.localPlayerUID)
                NotifySelf("你被击中了！剩余 HP " + message.Hp);
        }

        // ===== 模式开关与状态同步 =====

        internal static void SetEnabled(bool value)
        {
            if (!NetRuntime.IsServer || enabled == value) return;
            // 课程进行中锁定：中途改模式会破坏感染/HP 状态，只能课前或下课后改
            if (value && ClassInProgress())
            {
                Log("[状态] 课程进行中，拒绝更改生化模式");
                return;
            }
            enabled = value;
            if (!value) ClearRoundState();
            string announce = value
                ? "生化模式已启用：被挂上罚站柱超过 " + Config.pillarInfectSeconds.ToString("0") + " 秒会被感染！"
                : "生化模式已关闭";
            NotifyAll(announce);        // 房主本机弹
            BroadcastState(announce);   // 客机随快照弹
            Log("[状态] 生化模式 " + (value ? "启用" : "关闭"));
        }

        internal static bool ClassInProgress()
        {
            if (!MonoSingletonT<ClassroomManager>.IsSingletonActive()) return false;
            EClassroomState state = MonoSingletonT<ClassroomManager>.Instance.curState;
            return state == EClassroomState.Prepare || state == EClassroomState.Classing;
        }

        // 面板改数值后调用：落盘 + 全量快照广播（cfg 随快照同步客机，各端行为/显示一致）
        internal static void OnConfigEdited()
        {
            if (!NetRuntime.IsServer) return;
            BioChemConfig.Save(Log);
            BroadcastState();
        }

        private static void ClearRoundState()
        {
            initialInfectionApplied = false;
            infected.Clear();
            hunters.Clear();
            killedTeachers.Clear();
            hp.Clear();
            stamina.Clear();
            teacherEvolution.Clear();
            pillarBinds.Clear();
            pillarRetryAt.Clear();
            stunCooldownUntil.Clear();
            StudentWeapons.ResetRound();
            downedUntil.Clear();
            serverSlowExpiries.Clear();
            selfSlowExpiries.Clear();
            dartAmmo.Clear();
            foodRefillReadyAt.Clear();
            selfDartAmmo = Config.dartMaxAmmo; // 新一局备弹回满（与服务端懒初始化口径一致）
            Ak47Weapon.ResetForNewRound(); // AK 全收：世界枪/viewmodel/弹匣/换弹状态归零
            selfStamina = -1f;
            localStunReadyAt = 0f;
            mgNextFireAt = 0f;
            mgFireUntil.Clear();
            lastSelfPosValid = false;
            // 猎手外观还原（回合重置/换房时所有端各自还原本端放大的模型）
            foreach (var pair in hunterOrigScale)
            {
                if (!UnitManager.IsSingletonActive()) break;
                Unit unit = UnitManager.Instance.GetUnitByUID(pair.Key);
                if (unit != null && unit.model != null) unit.model.gameObject.transform.localScale = pair.Value;
            }
            hunterOrigScale.Clear();
            hunterStrikeReadyAt.Clear();
            localHunterStrikeReadyAt = 0f;
            hunterUnlockAnnounced = false;
            HunterKnife.RemoveAll(); // 猎刀随回合重置全收（模式关闭也走这里）
            MiniTeacherCatch.RemoveAll(); // 收回小老师抓取组件（学生状态由原生回合重置负责）
        }

        private static void OnClassStateChanged(EClassroomState state)
        {
            if (state == EClassroomState.Prepare || state == EClassroomState.BeforeClass || state == EClassroomState.ClassFinish)
            {
                ClearRoundState();
                if (NetRuntime.IsServer) BroadcastState();
            }
        }

        private static void OnPlayerUnitRemoved(ulong playerUID, bool isStudentPlayer)
        {
            if (playerUID == 0) return;
            if (infected.Remove(playerUID)) BroadcastStateIfServer();
            hp.Remove(playerUID);
            stamina.Remove(playerUID);
            teacherEvolution.Remove(playerUID);
            stunCooldownUntil.Remove(playerUID);
            StudentWeapons.RemovePlayer(playerUID);
            downedUntil.Remove(playerUID);
            serverSlowExpiries.Remove(playerUID);
            dartAmmo.Remove(playerUID);
            foodRefillReadyAt.Remove(playerUID);
            mgFireUntil.Remove(playerUID);
            hunters.Remove(playerUID);
            killedTeachers.Remove(playerUID);
            hunterStrikeReadyAt.Remove(playerUID);
            HunterKnife.Remove(playerUID);
            MiniTeacherCatch.RemoveCatchAbility(playerUID); // 断线清理：注销组件上的 msg 2017 监听
            if (hunterOrigScale.Remove(playerUID) && UnitManager.IsSingletonActive())
            {
                Unit removedUnit = UnitManager.Instance.GetUnitByUID(playerUID);
                if (removedUnit != null && removedUnit.model != null) removedUnit.model.gameObject.transform.localScale = Vector3.one;
            }
            foreach (var key in new List<ulong>(pillarBinds.Keys))
                if (pillarBinds[key].student == playerUID) { pillarBinds.Remove(key); pillarRetryAt.Remove(key); }
        }

        private static void BroadcastStateIfServer()
        {
            if (NetRuntime.IsServer) BroadcastState();
        }

        private static void BroadcastState(string notify = null)
        {
            version++;
            var dto = new BioChemStateDto { enabled = enabled, cfg = JsonUtility.ToJson(BioChemConfig.Instance) };
            foreach (ulong uid in infected) dto.infected.Add((long)uid);
            foreach (ulong uid in hunters) dto.hunters.Add((long)uid);
            foreach (ulong uid in killedTeachers) dto.killed.Add((long)uid);
            foreach (var pair in hp) { dto.hpUids.Add((long)pair.Key); dto.hpVals.Add(pair.Value); }
            foreach (var pair in stamina) { dto.stUids.Add((long)pair.Key); dto.stVals.Add(pair.Value); }
            foreach (var pair in teacherEvolution)
            {
                dto.evolutionUids.Add((long)pair.Key);
                dto.evolutionStages.Add(pair.Value);
            }
            // Notify 只随本次广播：客户端用它弹公告（服务端已 NotifyAll 过，自己不会再收到）。
            // cfg 随每份快照带上：房主数值改动即时同步，定时重广播兜底
            var message = new BioChemStateMessage { Version = version, Json = JsonUtility.ToJson(dto), Notify = notify ?? "" };
            if (RoomManager.IsSingletonActive() && NetworkServer.active) RoomManager.Instance.BroadcastToAllExceptHost(message, 0);
        }

        private static void OnClientState(BioChemStateMessage message)
        {
            if (NetworkServer.active) return;
            // Version 只作诊断：可靠有序通道内不会收到过期消息；version 静态值跨房间不重置，
            // 绝不能拿它当过期门槛（否则换房/重开局后客机永久拒收状态）
            BioChemStateDto dto;
            try
            {
                dto = string.IsNullOrEmpty(message.Json) ? null : JsonUtility.FromJson<BioChemStateDto>(message.Json);
            }
            catch { return; }
            if (dto == null) return;
            version = message.Version;
            enabled = dto.enabled;
            // 房主数值配置随快照应用（各端行为/显示一致；只覆盖运行时值，不动本地磁盘文件）
            if (!string.IsNullOrEmpty(dto.cfg))
            {
                try
                {
                    var cfg = JsonUtility.FromJson<BioChemConfig>(dto.cfg);
                    if (cfg != null) { cfg.Clamp(); BioChemConfig.Instance = cfg; }
                }
                catch { /* 畸形配置不应用，等下一份快照 */ }
            }
            infected.Clear();
            foreach (long uid in dto.infected)
            {
                if (uid == 0) continue;
                infected.Add((ulong)uid);
                MiniTeacherCatch.EnsureCatchAbility((ulong)uid); // 快照即补抓取组件（单位未就绪时 Tick 自愈兜底）
            }
            hunters.Clear();
            foreach (long uid in dto.hunters)
                if (uid != 0) hunters.Add((ulong)uid);
            killedTeachers.Clear();
            foreach (long uid in dto.killed)
                if (uid != 0) killedTeachers.Add((ulong)uid);
            // 快照兜底：中途加入的端对已有猎手补放大+发刀（均幂等），出局的老师由 TickKilledTeachers 持续压躺
            foreach (ulong uid in new List<ulong>(hunters))
            {
                if (!UnitManager.IsSingletonActive()) break;
                Unit unit = UnitManager.Instance.GetUnitByUID(uid);
                if (unit == null || !unit.isActived || unit.model == null) continue;
                if (!hunterOrigScale.ContainsKey(uid))
                {
                    hunterOrigScale[uid] = unit.model.gameObject.transform.localScale;
                    unit.model.gameObject.transform.localScale = unit.model.gameObject.transform.localScale * HunterScale;
                }
                HunterKnife.Ensure(unit);
            }
            hp.Clear();
            for (int i = 0; i < dto.hpUids.Count && i < dto.hpVals.Count; i++)
                if (dto.hpUids[i] != 0) hp[(ulong)dto.hpUids[i]] = dto.hpVals[i];
            stamina.Clear();
            for (int i = 0; i < dto.stUids.Count && i < dto.stVals.Count; i++)
                if (dto.stUids[i] != 0) stamina[(ulong)dto.stUids[i]] = dto.stVals[i];
            ulong selfUid = UnitManager.IsSingletonActive() ? UnitManager.Instance.localPlayerUID : 0;
            if (selfUid != 0 && (!enabled || infected.Contains(selfUid) || hunters.Contains(selfUid)))
                StudentWeapons.RemovePlayer(selfUid);
            int previousStage = GetEvolutionStage(selfUid);
            teacherEvolution.Clear();
            if (dto.evolutionUids != null && dto.evolutionStages != null)
            {
                for (int i = 0; i < dto.evolutionUids.Count && i < dto.evolutionStages.Count; i++)
                    if (dto.evolutionUids[i] != 0)
                        teacherEvolution[(ulong)dto.evolutionUids[i]] = Mathf.Clamp(dto.evolutionStages[i], 0, MaxEvolutionStage);
            }
            if (selfUid != 0 && GetEvolutionStage(selfUid) > previousStage)
            {
                Unit self = UnitManager.Instance.GetUnitByUID(selfUid);
                if (self != null && self.isActived) RestoreLocalTeacherCondition(self, infected.Contains(selfUid));
            }
            if (selfUid != 0 && infected.Contains(selfUid))
            {
                // 自己的体力走本地模拟，不被快照覆盖；刚被感染从满体力开始
                if (selfStamina < 0f) selfStamina = Config.staminaMax;
                else stamina[selfUid] = selfStamina;
            }
            if (!string.IsNullOrEmpty(message.Notify)) NotifyAll(message.Notify);
        }

        // ===== 每帧驱动 =====

        internal static void Tick()
        {
            CountdownVoice.Tick();
            if (enabled && NetRuntime.IsServer)
            {
                TryApplyInitialInfection();
                TryFinishHunterWin();
                TickStudentWipeCheck();
            }
            float dt = Time.unscaledDeltaTime;
            PruneSelfSlow();
            TickSelfStamina(dt);
            HandleKeys();
            Ak47Weapon.Tick();
            HunterKnife.Tick(); // 挥刀驱动+自愈（无猎手时零开销早退）
            if (enabled)
            {
                TickHunterMelee();
                MiniTeacherCatch.TickSelfHeal(); // 感染名单里的抓取组件自愈（1s 节流）
                TickMachineGun();
                TickKilledTeachers();
                if (NetRuntime.IsServer) TickHunterUnlockAnnounce();
            }
            if (NetRuntime.IsServer)
            {
                TickPillarInfection();
                TickDowned();
                TickDartEquip();
                if (enabled && Time.unscaledTime >= nextRepublish)
                {
                    nextRepublish = Time.unscaledTime + 5f;
                    BroadcastState(); // 重连/中途加入靠全量重广播自愈
                }
            }
        }

        // 变身解锁公告：剩余学生首次降到阈值时全端提示一次（服务端）
        private static void TickHunterUnlockAnnounce()
        {
            if (hunterUnlockAnnounced || !ServerClassActive()) return;
            int remaining = RemainingStudents();
            if (remaining == 0 || remaining > Config.hunterTriggerStudents) return;
            hunterUnlockAnnounced = true;
            string announce = "最后 " + remaining + " 名学生可以按 G 变身老师猎手！";
            NotifyAll(announce);
            BroadcastState(announce);
            Log("[状态] " + announce);
        }

        private static void PruneSelfSlow()
        {
            for (int i = selfSlowExpiries.Count - 1; i >= 0; i--)
                if (Time.unscaledTime >= selfSlowExpiries[i]) selfSlowExpiries.RemoveAt(i);
        }

        // ===== 小老师体力：本地模拟（走路消耗 + 写板书读条回复）=====

        // 位移差判定移动；仅上课期间消耗，复活传送/回合重置时 lastSelfPosValid 归位
        private static void TickSelfStamina(float dt)
        {
            if (!enabled || dt <= 0f || !UnitManager.IsSingletonActive()) return;
            Unit self = UnitManager.Instance.localPlayer;
            if (self == null || !self.isActived || !infected.Contains(self.UID)) return;
            if (selfStamina < 0f)
                selfStamina = stamina.TryGetValue(self.UID, out float s) ? Mathf.Clamp(s, 0f, Config.staminaMax) : Config.staminaMax;
            if (selfStamina <= 0f)
            {
                lastSelfPosValid = false;
                return;
            }
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            Vector3 pos = self.Position;
            if (lastSelfPosValid && (pos - lastSelfPos).sqrMagnitude > 0.0001f)
                selfStamina = Mathf.Max(0f, selfStamina - Config.staminaDrainPerSecond * dt);
            lastSelfPos = pos;
            lastSelfPosValid = true;
        }

        // 本地玩家体力读取：小老师走本地模拟（权威显示值），否则回退快照字典
        internal static float SelfStamina()
        {
            if (!UnitManager.IsSingletonActive()) return 0f;
            ulong selfUid = UnitManager.Instance.localPlayerUID;
            if (infected.Contains(selfUid))
            {
                if (selfStamina < 0f)
                    selfStamina = stamina.TryGetValue(selfUid, out float s) ? Mathf.Clamp(s, 0f, Config.staminaMax) : Config.staminaMax;
                return selfStamina;
            }
            return stamina.TryGetValue(selfUid, out float v) ? v : 0f;
        }

        private static List<float> PruneServerSlow(ulong uid)
        {
            if (!serverSlowExpiries.TryGetValue(uid, out List<float> list))
                serverSlowExpiries[uid] = list = new List<float>();
            for (int i = list.Count - 1; i >= 0; i--)
                if (Time.unscaledTime >= list[i]) list.RemoveAt(i);
            return list;
        }

        // ===== 按键 =====

        private static void HandleKeys()
        {
            // F7 面板：主菜单里 InputManager 可能还没创建，单独放行不跟游戏内按键共用门槛
            if (MonoSingletonT<InputManager>.IsSingletonActive() && ReInput.isReady)
            {
                Keyboard keyboard = ReInput.players.GetPlayer(0).controllers.Keyboard;
                if (keyboard != null && keyboard.GetKeyDown(PanelKey)) TogglePanel();
            }
            if (!enabled) return;
            if (!MonoSingletonT<InputManager>.IsSingletonActive() || !ReInput.isReady) return;
            Keyboard kb = ReInput.players.GetPlayer(0).controllers.Keyboard;
            if (kb == null) return;
            if (InputOverFrozen()) return;
            // 课外仍可切换。射击沿用课堂进行中的门槛。
            StudentWeaponSlot slot = StudentWeapons.ReadSlotKey(kb);
            if (slot != StudentWeaponSlot.None && NetRuntime.IsConnected && UnitManager.IsSingletonActive()
                && StudentWeapons.CanSelect(UnitManager.Instance.localPlayer))
                SelectWeaponSlot(slot);
            if (kb.GetKeyDown(StunKey)) TryMiniStun();
            if (kb.GetKeyDown(KeyCode.R) && StudentWeapons.Selected == StudentWeaponSlot.Primary) Ak47Weapon.TryReload();
            // G 变身老师猎手：剩余学生 ≤ hunterTriggerStudents 时解锁（服务端会复验）
            if (kb.GetKeyDown(HunterKey) && CanTransformHunter()) SendTransformRequest();
        }

        internal static readonly KeyCode HunterKey = KeyCode.G;   // 学生变身为老师猎手

        private static bool CanTransformHunter()
        {
            if (!enabled || !CanClassAct() || !UnitManager.IsSingletonActive()) return false;
            ulong self = UnitManager.Instance.localPlayerUID;
            if (infected.Contains(self) || hunters.Contains(self)) return false;
            Unit selfUnit = UnitManager.Instance.GetUnitByUID(self);
            if (selfUnit == null || !selfUnit.isActived || selfUnit.IsDead || selfUnit.IsStun) return false;
            if (selfUnit.UnitSubType != EUnitSubType.Student || selfUnit.CurState() == 103) return false;
            return RemainingStudents() <= Config.hunterTriggerStudents;
        }

        private static void SendTransformRequest()
        {
            var message = new BioChemHunterMessage { Kind = BioChemHunterMessage.TransformRequest };
            if (NetRuntime.IsServer) OnServerHunter(null, message);
            else if (RoomManager.IsSingletonActive()) RoomManager.Instance.SendToServer(message);
        }

        // 猎手近战：左键挥刀（可空挥）。本地预测表现立即生效；目标选择与结算全在服务端——
        // 上报点击瞬间的准星方向（CF 手感核心：判定跟准星走），服务端扇形群体判定
        private static void TickHunterMelee()
        {
            if (!enabled || !hunters.Contains(UnitManager.IsSingletonActive() ? UnitManager.Instance.localPlayerUID : 0)) return;
            if (!Input.GetMouseButtonDown(0)) return; // 单击挥砍（连点受冷却限制）
            if (Time.unscaledTime < localHunterStrikeReadyAt) return;
            if (!CanClassAct() || !UnitManager.IsSingletonActive()) return;
            ulong self = UnitManager.Instance.localPlayerUID;
            Unit selfUnit = UnitManager.Instance.GetUnitByUID(self);
            if (selfUnit == null || !selfUnit.isActived || selfUnit.IsDead || selfUnit.IsStun) return;
            localHunterStrikeReadyAt = Time.unscaledTime + Config.hunterAttackInterval;
            selfUnit.GetComponent<AnimationComponent>()?.PlayAnimSync("Throw"); // 学生动画机有 Throw（投掷挥臂），原生同步全员可见
            HunterKnife.PlaySwing(self); // 本地预测：自己的刀先摆（viewmodel）
            PlayHunterSound(AudioEvents.sfx_DoubaoSwordSwing_sfx, selfUnit.Position);
            Vector3 direction = Vector3.zero;
            if (MonoSingletonT<SceneManager>.IsSingletonActive())
            {
                Camera cam = MonoSingletonT<SceneManager>.Instance.MainCamera;
                if (cam != null) direction = cam.transform.forward;
            }
            PredictHunterFeedback(selfUnit, direction); // 命中反馈即时出（CF 手感核心）：预测命中+音效，伤害仍等服务端
            var message = new BioChemHunterMessage
            {
                Kind = BioChemHunterMessage.Strike,
                Origin = selfUnit.Position,
                Direction = direction // 零向量时服务端兜底身体朝向
            };
            if (NetRuntime.IsServer) OnServerHunter(null, message);
            else if (RoomManager.IsSingletonActive()) RoomManager.Instance.SendToServer(message);
        }

        // 攻击者本地预测：与服务端同一口径收集扇内目标，命中标记/浮字/震屏/命中音效全部即时出。
        // 伤害与全局表现仍走服务端广播（预测只做"手感"，不替服务端结算）
        private static void PredictHunterFeedback(Unit hunter, Vector3 direction)
        {
            Vector3 forward = direction;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) { forward = hunter.transform.forward; forward.y = 0f; }
            forward.Normalize();
            CollectSweepTargets(hunter, forward, sweepBuffer);
            foreach (Unit target in sweepBuffer)
            {
                bool killed = GetTeacherTeamHp(target.UID) <= Config.hunterDamage; // 按当前权威 HP 预测致死
                HunterFeedback.OnPredictedHit(target, killed);
                if (killed) PlayKillSound(target.Position);
                else PlayHunterSound(AudioEvents.sfx_DoubaoSwordHit_sfx, target.Position);
            }
        }

        // 击杀音效：命中音降调加重（仅攻击者本机预测时播；其他端靠击杀广播的视觉+原生表现感知）
        private static void PlayKillSound(Vector3 position)
        {
            if (!MonoSingletonT<AudioManager>.IsSingletonActive()) return;
            MonoSingletonT<AudioManager>.Instance.PlayOneShot(AudioEvents.sfx_DoubaoSwordHit_sfx, position,
                instance => instance.setPitch(0.75f));
        }

        // 猎刀音效复用老师剑的 FMOD 事件（挥砍 Swing / 命中 Hit）
        internal static void PlayHunterSound(AudioEvents sound, Vector3 position)
        {
            if (!MonoSingletonT<AudioManager>.IsSingletonActive()) return;
            MonoSingletonT<AudioManager>.Instance.PlayOneShot(sound, position);
        }

        private static void SelectWeaponSlot(StudentWeaponSlot slot)
        {
            if (!RoomManager.IsSingletonActive()) return;
            StudentWeapons.SelectLocal(slot);
            var message = new BioChemWeaponSlotMessage { Slot = (byte)slot };
            if (NetRuntime.IsServer) OnServerWeaponSlot(null, message);
            else RoomManager.Instance.SendToServer(message);
        }

        private static void OnServerWeaponSlot(NetworkConnectionToClient connection, BioChemWeaponSlotMessage message)
        {
            if (!enabled || !NetRuntime.IsServer || !UnitManager.IsSingletonActive() || !StudentWeapons.IsValid(message.Slot)) return;
            ulong uid = ResolveSender(connection);
            if (uid == 0) return;
            Unit student = UnitManager.Instance.GetUnitByUID(uid);
            if (!StudentWeapons.CanSelect(student)) return;

            message.ItemUID = StudentWeapons.SelectOnServer(student, (StudentWeaponSlot)message.Slot);
            if (connection == null || connection == NetworkServer.localConnection) StudentWeapons.ApplyIssuedItem(message);
            else connection.Send(message);
        }

        // 已发放的 AK 用连发管线。切槽后等待同步回收期间，也拦住新发起的瞄准。
        public static bool BeforeTaserHandleInput(TaserComponent __instance, eCmdType command, eClickType click, Unit ___user)
        {
            if (___user == null || !___user.isLocalPlayer || !StudentWeapons.IsLocalAk(__instance.owner)) return true;
            if (command == eCmdType.UseItem) return false;
            return command != eCmdType.UseItem2 || click == eClickType.Release || StudentWeapons.Selected == StudentWeaponSlot.Primary;
        }

        // F7 有 Rewired 与 IMGUI 两条通道，同一帧可能都触发，节流防双开关。
        // 优先复用游戏设置窗口（打开并跳到「生化模式」页），嵌不进去走自建面板
        private static float lastPanelToggleAt;
        internal static void TogglePanel()
        {
            if (Time.unscaledTime - lastPanelToggleAt < 0.25f) return;
            lastPanelToggleAt = Time.unscaledTime;
            if (BioChemGameSettings.TryOpen()) return;
            BioChemPanel.Toggle();
            Log("[状态] 自建管理面板" + (BioChemPanel.IsOpen ? "已打开" : "已关闭"));
        }

        private static bool InputOverFrozen()
        {
            return MonoSingletonT<InputManager>.IsSingletonActive()
                && (MonoSingletonT<InputManager>.Instance.FrozedPlayerInput || MonoSingletonT<InputManager>.Instance.FrozedAllInput);
        }

        // 学生可行动：生化开启 + 上课中 + 本地玩家是普通学生（感染者 IsStudent 已翻转）
        private static bool CanClassAct()
        {
            if (!ClassroomManager.IsSingletonActive() || !UnitManager.IsSingletonActive()) return false;
            if (ClassroomManager.Instance.curState != EClassroomState.Classing) return false;
            return !InputOverFrozen();
        }

        // 服务端校验专用：只看课堂状态。不能拿房主本地的输入冻结态去卡远程玩家
        // （房主开个聊天/设置界面，客机的击晕请求和吹箭结算就全被拒了）
        private static bool ServerClassActive()
        {
            return ClassroomManager.IsSingletonActive()
                && ClassroomManager.Instance.curState == EClassroomState.Classing;
        }

        // ===== 消息注册与处理 =====

        public static void RegisterClient()
        {
            NetworkClient.RegisterHandler<BioChemStateMessage>(OnClientState, true);
            NetworkClient.RegisterHandler<BioChemDartMessage>(OnClientDart, true);
            NetworkClient.RegisterHandler<BioChemWeaponSlotMessage>(StudentWeapons.ApplyIssuedItem, true);
            NetworkClient.RegisterHandler<BioChemStunMessage>(OnClientStun, true);
            NetworkClient.RegisterHandler<BioChemDownMessage>(OnClientDown, true);
            NetworkClient.RegisterHandler<BioChemHunterMessage>(OnClientHunter, true);
            // 重进房间 = 全新连接：清掉上一局残留（enabled/infected 等待 ≤5s 快照重建）。
            // 房主也会走 InitClient，但 NetworkServer.active 已为 true，不会误清服务端权威状态
            if (!NetworkServer.active)
            {
                enabled = false;
                ClearRoundState();
                StudentWeapons.ResetLocalItem();
            }
        }

        public static void RegisterServer()
        {
            NetworkServer.RegisterHandler<BioChemDartMessage>(OnServerDart, true);
            NetworkServer.RegisterHandler<BioChemWeaponSlotMessage>(OnServerWeaponSlot, true);
            NetworkServer.RegisterHandler<BioChemStunMessage>(OnServerStun, true);
            NetworkServer.RegisterHandler<BioChemWriteMessage>(OnServerWrite, true);
            NetworkServer.RegisterHandler<BioChemHunterMessage>(OnServerHunter, true);
            // 新建房间：清掉上一局服务端瞬态（enabled 是房主设置，跨房保留）
            ClearRoundState();
            StudentWeapons.ResetLocalItem();
        }

        private static void OnClientDart(BioChemDartMessage message)
        {
            if (NetworkServer.active) return;
            if (message.Kind == BioChemDartMessage.Hit) ApplyDartHit(message);
            else if (message.Kind == BioChemDartMessage.AmmoSync) ApplyAmmoSyncLocal(message);
            else if (message.Kind == BioChemDartMessage.MgShot)
            {
                // 射手自己跳过（本地已预测渲染）；其他端渲染弹体（原生拖尾/音效由 PlayFire 播放）
                if (UnitManager.IsSingletonActive() && message.UserUID == UnitManager.Instance.localPlayerUID) return;
                PlayFireShot(BuildShot(message));
            }
        }

        private static void OnClientStun(BioChemStunMessage message)
        {
            if (NetworkServer.active) return;
            if (message.Kind == BioChemStunMessage.Apply) ApplyStunLocal(message.TargetUID, message.Duration);
        }

        private static void OnServerWrite(NetworkConnectionToClient connection, BioChemWriteMessage message)
        {
            if (!NetRuntime.IsServer || !enabled) return;
            ApplyWrite(message.Kind, ResolveSender(connection));
        }

        private static void OnClientDown(BioChemDownMessage message)
        {
            if (NetworkServer.active) return;
            if (message.Kind == BioChemDownMessage.Down) ApplyDownLocal(message);
            else if (message.Kind == BioChemDownMessage.Respawn) ApplyRespawnLocal(message);
            else ApplyKillLocal(message);
        }

        private static void OnClientHunter(BioChemHunterMessage message)
        {
            if (NetworkServer.active) return;
            if (message.Kind == BioChemHunterMessage.Transform) ApplyHunterTransformLocal(message);
            else if (message.Kind == BioChemHunterMessage.Hit) ApplyHunterHitLocal(message);
            else if (message.Kind == BioChemHunterMessage.RevertVisual) ApplyRevertVisualLocal(message);
            else if (message.Kind == BioChemHunterMessage.Swing) ApplyHunterSwingLocal(message);
        }

        // ===== 工具 =====

        private static ulong ResolveSender(NetworkConnectionToClient connection)
        {
            if (!RoomManager.IsSingletonActive()) return 0;
            return connection == null || connection == NetworkServer.localConnection
                ? UnitManager.Instance.localPlayerUID
                : RoomManager.Instance.GetPlayerUIDByConnection(connection);
        }

        internal static int GetHp(ulong uid, bool isBigTeacher)
        {
            if (hp.TryGetValue(uid, out int value)) return value;
            int max = GetTeacherMaxHp(uid, isBigTeacher);
            hp[uid] = max;
            return max;
        }

        internal static int GetEvolutionStage(ulong uid)
        {
            return teacherEvolution.TryGetValue(uid, out int stage) ? stage : 0;
        }

        internal static int GetTeacherMaxHp(ulong uid, bool isBigTeacher)
        {
            int baseHp = isBigTeacher ? Config.teacherMaxHp : Config.miniMaxHp;
            long bonusPerStage = (baseHp * (long)EvolutionHpPercentPerStage + 99) / 100;
            return (int)Math.Min(int.MaxValue, baseHp + bonusPerStage * GetEvolutionStage(uid));
        }

        private static void Broadcast<T>(T message) where T : struct, NetworkMessage
        {
            if (RoomManager.IsSingletonActive() && NetworkServer.active) RoomManager.Instance.BroadcastToAllExceptHost(message, 0);
        }

        internal static string GetName(ulong uid)
        {
            if (!UnitManager.IsSingletonActive()) return "玩家";
            Unit unit = UnitManager.Instance.GetUnitByUID(uid);
            return unit != null ? unit.GetPlayerUnitName() : "已离开";
        }

        private static void NotifyAll(string text)
        {
            if (!MonoSingletonT<GameNotifyManager>.IsSingletonActive()) return;
            MonoSingletonT<GameNotifyManager>.Instance.ShowCommonNotify(text, null, 3f);
        }

        private static void NotifySelf(string text)
        {
            NotifyAll(text);
        }

        // 供 UI 查询：本地玩家的教师团队身份与状态
        internal static bool LocalIsBigTeacher
        {
            get
            {
                if (!UnitManager.IsSingletonActive() || !enabled) return false;
                Unit self = UnitManager.Instance.GetUnitByUID(UnitManager.Instance.localPlayerUID);
                return self != null && self.IsTeacher && !infected.Contains(self.UID);
            }
        }

        internal static bool LocalIsInfected
        {
            get
            {
                if (!UnitManager.IsSingletonActive() || !enabled) return false;
                return infected.Contains(UnitManager.Instance.localPlayerUID);
            }
        }

        internal static float StunCooldownRemain => Mathf.Max(0f, localStunReadyAt - Time.unscaledTime);
        internal static int SelfSlowStacks { get { PruneSelfSlow(); return selfSlowExpiries.Count; } }
        internal static int SelfDartAmmo => selfDartAmmo;

        internal static void Cleanup()
        {
            CountdownVoice.Cleanup();
            BioChemPanel.Teardown();
            if (registeredHooks)
            {
                // 必须按目标注销：UnRegister(key) 会把该 key 上游戏自己的监听
                // （黑板/门/抽屉/柱子/抓捕组件等都注册了 301/2017）全部清掉
                MsgCenter.UnRegister(ClassStateChangedMsgId, Hooks);
                MsgCenter.UnRegister(PlayerRemovedMsgId, Hooks);
            }
            registeredHooks = false;
        }

        // MsgCenter 按 delegate.Target 精确注销，需要实例目标承载静态处理器的转发
        private sealed class MsgHooks
        {
            public void OnClassStateChanged(EClassroomState state) => ModEntry.OnClassStateChanged(state);
            public void OnPlayerUnitRemoved(ulong playerUID, bool isStudentPlayer) => ModEntry.OnPlayerUnitRemoved(playerUID, isStudentPlayer);
        }

        private static readonly MsgHooks Hooks = new MsgHooks();
    }
}
