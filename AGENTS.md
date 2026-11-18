# AGENTS.md — WorstClassMod 开发指南

**官方 Mod**（`WorstClassEver.ModAPI` 框架），游戏：**Worst Class Ever**（Unity 2022.3.62f3c1 中国变体，Mono 后端，Steam 多人游戏，Mirror 网络）。

> 2026-11-14 已从 BepInEx 5 全面迁移到官方 Mod 框架。BepInEx 组件备份在 `D:\WorstClassBepInExBackup\`（winhttp.dll + doorstop_config.ini + BepInEx/），如需恢复原样移回游戏根目录即可。

## 项目结构（多 Mod 工作区：一个 Mod 一个文件夹）

- `WorstClassMods.sln` — 解决方案，`dotnet build` 一次构建全部 Mod
- `Directory.Build.props` — 所有 Mod 共享：netstandard2.1、游戏路径、全部程序集引用（ModAPI / 0Harmony / UnityEngine facade / Assembly-CSharp / Mirror / FMODUnity）
- `Directory.Build.targets` — 共享部署：构建后把 DLL + `mod.json` + `Music\` 目录部署到 `游戏Mods\<程序集名>\`。**ModOutDir 必须定义在这里而非 props**（MSBuild 属性定义时立即求值，props 里拿不到子项目的 AssemblyName）
- `PlaygroundMusic\` — 操场广播音乐扩展（id=`worstclass.playgroundmusic`，`affectsNetworkMessages: true`）：
  - `ModEntry.cs` — IMod 入口 + 广播柱补丁（扩展 `MusicSequence` + FMOD CoreSystem 播本地 MP3）
  - `MusicSync.cs` — 联机曲库协商（SHA-256 交集 + 自定义 Mirror 消息 + Started 时间对齐）
  - `mod.json` / `Music\`（自带曲目，如 上春山.mp3）
- `TrickMole\` — 课堂角色（id=`worstclass.trickmole` 保留旧 id 避免启用状态断裂，v2.0 起为**多角色框架**，`affectsNetworkMessages: true`）：
  - 角色抽象 `RoleDef.cs`（`[Serializable]` + JsonUtility）：id/name/count（每局名额）/trickMultiplier/canBroadcast/canPush/各技能数值/挂柱扣分比例。配置在 `Configs/trickmole/trickmole.json`（`RoleConfig.Load` 首次运行生成默认，只含"混的入"）；**加已有技能的新角色 = 配置加一个块**，新技能才改代码。服务端配置权威，客机经 `MoleSelectMessage`（`Uids`/`RoleJsons` Mirror 原生数组逐条携带）拿运行时副本——各端配置可不一致不炸。**⚠️ 引擎坑（2022.3.62f3c1 中国变体）：`JsonUtility` 会静默跳过 `List<自定义类>` 字段**（ToJson 出 `{}`、FromJson 出空表，纯字段类完全正常）——任何 JSON 往返都不许把 `List<自定义类>` 当字段用，v2.1.0-2.3.7 的快照/配置因此从未成功过（v2.3.8 起快照用 Mirror 数组、配置用手工拼+括号配对拆解）；游戏自己的 `ModWorkshopClient` 也是逐行 `WriteLine(ToJson(entry))` 避开这个坑。
  - 状态：`assigned`（uid→RoleDef 字典）+ `progressByUid`（捣蛋累计）+ `pushCooldownUntil`（per-玩家推倒冷却），单 moleUID 时代已终结。
  - 选人：`TryAssignRoles` 按 `ExpandSlots()` 名额展开 + Fisher–Yates 洗牌后逐名额随机填充（学生数少于名额时每个角色等可能出现而非配置顺序靠前者必被填，满员时等价不放回均匀抽样），每名额 `BroadcastAssignment`（version 全局单调递增，重广播也递增覆盖同 uid）；清局 `BroadcastClearAll`（UID=0 → 客机 clear）。
  - 混的入能力：捣蛋分 ×2 并单独累计捣蛋值（`progressByUid`，各端从计分入口 prefix 统计乘后实际计入总进度的量，每节课重置）；被挂罚站柱时总体进度扣累计的 2/3、记录归零（个人贡献分不动；扣分照 PublishTrickCountChange 模式联动全端，客机在挂柱补丁里本地同步归零）；上课按 B 开关 3D 广播（跟随玩家移动，起播 3 秒宽限不计分、停止后冷却 20 秒，数值取广播者角色配置 `broadcastRole`）；老师身后 110° 扇区 2.2m 按 E 推倒 2.5 秒冷却 30 秒（`TeacherStunComponent.TryGetProjectileStun` 借电击枪精确时长通道；`MolePushMessage` 带 StudentUID 只对推手启动冷却）；广播曲池 = 原版 4 首 + PlaygroundMusic 全员交集扩展曲（`PmBridge.cs` 反射软依赖，懒解析——Mod 加载顺序不保证；扩展曲 CoreSystem 播放 + Started 对齐 + mp3 播完自动 StopRequest；Mirror null string 读回 ""，hash 判断必须 IsNullOrEmpty）；广播期间操场广播柱静音且暂停其计分（柱子实例音量 0 压制 + PlaygroundMusic 低级 Channel 反射 setMute + `AwardPlaygroundMusicScore` prefix 拦截非广播者计分）。
  - 书呆子（`id=nerd`，内置默认角色）：捣蛋分 ×0.5、无广播/推倒；连续 `immunityIdleSeconds`(20s) 未得分获得一次免伤（上限 `maxImmunityCharges`=1），得分立即失去；服务端在 `SkillMessageHandler.OnUseSkillMessageRequest`(skillID=3 抓捕)、`StudentStunComponent.GetStun`、`UnitMessageHandler.OnStudentGetStunRequest` 三处拦截老师攻击（排除 Banana 滑倒）并广播 `MoleImmunityMessage`（Gained/Consumed），Consumed 时全员走 `GameNotifyManager.ShowCommonNotify` 弹原生提示；静心计时各端从同一计分入口自行维护，服务端获得/消耗时广播对齐。旧 trickmole.json 缺内置角色时自动补全并写回。右上角提示盒只显示"角色：玩家"，仅本地玩家自己的角色追加冷却/免伤状态行。
  - 体育生（`id=athlete`，内置默认角色）：无计分加成、无广播/推倒/挂柱扣分（`progressPenaltyFraction=0`）；`speedBoostUnseen`(1.5)/`speedPenaltySeen`(0.75) 老师视野外/内移速倍率，postfix `InputComponent.GetCurrentMoveSpeed`（本地玩家移速唯一出口，`CurrentMoveSpeed` 只被本组件移动物理和一个跟随相机用）；"被老师看见"=任一存活未眩晕老师（遍历 `unitsDict` 过滤 `IsTeacher`，AI 老师也算）头顶点距离内 + 老师朝向锥角 + 头顶连线 raycast 无遮挡（口径抄 `StudentNPCComponent.HasObstacleBetween`，排除师生双方碰撞体，layermask -5），结果按帧缓存（`athleteSightCache`）；`actionSpeedMultiplier`(1.5) 进食/救人提速——prefix `StudentItemUseComponent.OnUpdate` 的 `ref iDt` 只加速按住吃东西（FoodComponent/HotPotIngredientComponent，`____isHoldingUse` 字段注入；不影响墨水/手机等按住使用物），postfix `PunishPillarComponent.GetRescueDuration` 缩短救援时长（手动救援进度与无人机救援 `DroneInteractionTarget` 共用此时长；`ReleaseStudentOnServer` 不校验耗时，纯客户端结算）。视锥角度/距离也是角色配置项（`sightAngleDegrees`/`sightDistance`，0 = 默认 110°/10m，借鉴 NPC 对视参数）。
  - 角色配置面板（v2.4.0，照抄生化模式的嵌入结构）：游戏设置窗口「课堂角色」页签——`TrickMoleGameSettings.cs` 每秒扫描设置窗口实例（正开的优先、窗口池兜底）自动挂页（克隆 UISettingView 页签 + `UI_SettingPanel` 子类页追加进 tabList/panelList），**无独立热键、无自建面板，设置页是唯一入口**；页内容 = 角色选择（‹›切换 + 添加/删除/恢复默认，内置角色不可删只能 count=0）+ 名称 InputField + 16 项步进器（名额/倍率/扣比例/广播与推倒全套/免伤/移速/视锥）+ 广播/推倒技能开关。编辑仅课程外开放（`ModEntry.ClassInProgress()` = Prepare/Classing 锁定，改动下节课生效）；**每台机器编辑各自配置，联机角色分配以房主配置为准**（无配置广播，客机经分配消息拿运行时副本）。
  - 加成补丁在 `ClassroomManager.OnResponseTrickCountData`（游戏全部捣蛋计分的唯一服务端入口，负分不乘）；挂柱扣分补丁在 `PunishPillarComponent.OnResponseBindStudent`；按键用 Rewired 硬件级 `Keyboard.GetKeyDown`（程序集名 `Rewired_Core`；`IGlyphProvider` 在 `Rewired.Interfaces`）；按键提示浮层 `KeyHint.cs`（Rewired 原生按键图标，`IGlyphProvider.TryGetGlyph` 是 **out** 参数；`FontStyle` 在 `UnityEngine.TextRenderingModule`、`JsonUtility` 在 `UnityEngine.JSONSerializeModule`——都已加 props 引用）；FMOD 注意：Studio `EventInstance` 无 getChannel，静音/音量走实例自身 API；低级 `Channel` 是 struct 无 isValid，守卫靠调用方状态
- `BioChem\` — 生化模式（id=`worstclass.biochem`，`affectsNetworkMessages: true`）：挂柱超时（默认 10s，CF 节奏）感染变小老师。核心机制 = **Harmony postfix 翻转 `Unit.get_IsTeacher`/`get_IsStudent`**（感染者对全游戏表现为老师：板书准入、NPC 敌我、捕获资格、名牌隐藏全走这两个属性，无需换模型）+ **捣蛋计分整体 prefix 封锁**（`BeforeTrickCount` 在 enabled 时跳过 `OnResponseTrickCountData`——捣蛋进度/StudentTrick 学生胜利/按捣蛋值触发的机制奖励全部冻结，感染者封锁一并覆盖；并存 TrickMole 时其捣蛋值累计/挂柱扣分在生化模式下同样无效）+ **倒计时结束翻转学生胜**（prefix `FinishClass`：teacherWin && outcome==TeacherTimeout → teacherWin=false、outcome=null 走 ClassEnd 同款无结果通道不冒用 StudentTrick 上报 Steam；TeacherCatch 按幸存者规则处理：学生阵营有幸存者（RemainingStudents>0 或 hunters>0）时拦下"抓完全部学生"的结算继续打，真全灭才放行；TeacherTimeout 且有幸存者→teacherWin=false/outcome=null 学生胜（不冒用 StudentTrick）；服务端 Tick `TickStudentWipeCheck` 学生全灭（RemainingStudents==0 且 hunters 空、infected 非空）立即 FinishClass(true) 老师胜；`InfectStudent` 后反射重触发原生 `CheckIfAllStudengPunished`（源码拼写如此）让"抓完+感染清空"即时结算）+ **老师猎手**（剩余学生 ≤ `hunterTriggerStudents`(2) 时学生按 G 变身：hunters 集合并入 IsTeacher/IsStudent 翻转——猎手对全游戏是老师身份不可被抓，模型 ×1.3（`Unit.model.localScale`，hunterOrigScale 兼幂等标记，感染/重置还原）+ 100004 电弧特效；**固定只能用近战**——变身即 `RevokeTaser` 收走吹箭、`ToggleDart` 拒绝猎手、patch `StudentItemUseComponent.GetCmdInput`/`MouthItemUseComponent.GetCmdInput`（MsgCenter 100 两个物品输入总入口）prefix 吞掉猎手全部物品输入（手持书/零食/墨水/手机/火锅 + 嘴部泡泡糖/气球全失效，交互系统不受影响）；近战=**猎刀**（v1.6.0 起有实体武器视觉，`HunterKnife.cs` 各端本地拼装、无网络对象）：**变身发刀**=各端 `ApplyHunterTransformLocal`/快照自愈调 `HunterKnife.Ensure`——世界刀挂 `HandPosition` 手骨（持握位姿优先对齐 `ItemHandPos` 权威参考位），本地猎手额外挂主相机 viewmodel（偏移抄老师剑 `idleCameraOffset`）；自己的世界刀走 `PlayerSelf` 层被本机相机剔除（第一人称 `CameraMask=~PlayerSelf`）、别人的走 `Player` 层；**CreatePrimitive 拼的方块必须 Destroy 自带 Collider**（挡交互/射线）。挥击链路=左键**可空挥** → 本地预测（冷却+`PlayAnimSync("Throw")` 学生动画机确有此 trigger（旧 "Throw1" 不存在等于没动画）+`HunterKnife.PlaySwing`+挥刀音效）→ C2S Strike（**只报挥刀+点击瞬间准星水平方向 Origin/Direction，连发 MgFire 同款模式；判定全在服务端**）→ 服务端**权威扇形扫击**：Origin 过 5m 伪造门槛（抄原生剑击 OnTeacherSlashFire）→ 先广播 S2C `Swing`（其他端摆刀+挥砍音效，攻击者跳过=已预测）→ 以服务端权威位置为圆心、客户端方向为轴做 `HunterAttackRange`(2.2m)×`HunterSlashAngle`(120°) **群体判定**一刀全中（水平距离跳蹲不影响、贴脸不算角度；晕眩目标可补刀，killed/hunter 排除；方向非法兜底身体朝向）；刀摆动=Tick 三段角度曲线（举→劈→回 0.3s），音效复用老师剑 `AudioEvents.sfx_DoubaoSwordSwing_sfx`/`Hit_sfx`（`MonoSingletonT<AudioManager>.Instance.PlayOneShot`）；**命中反馈**（`HunterFeedback.cs`，攻击者本机客户端预测，CF 手感——点击瞬间出反馈不等服务器）=准星四斜块命中标记（矩阵旋转 whiteTexture，击杀红色加大）+命中点伤害浮字（WorldToScreenPoint 投影上浮淡出，击杀红"击杀!"，位置定格不跟人）+复用老师剑 `TeacherSwordFeedback.Play` 命中震屏（HorizontalHit 同款曲线，曲线上优先抄场上老师模型 `TeacherMeleeConfig.settings.horizontalFeedback.hitShake`——**TeacherMeleeConfig 是模型上的 MonoBehaviour 非 IUnitComponent，要走 `unit.gameObject.GetComponent` 而非 `Unit.GetComponent`**）；预测用 `CollectSweepTargets` 与服务端同一口径；命中音效攻击者预测即播，Hit 消息带 `AttackerUID` 让攻击者端跳过广播命中音防双响（老师打猎手填 0 不受影响）；击杀=攻击者预测降调击杀音（FMOD setupAction setPitch 0.75）+全员 `SpawnFx(100002)+(100004)` 双爆发。Tick 自愈重建（模型重载/部件晚绑定兜底）+清非猎手残根。CF 终结者数值：hunterDamage 默认 40 一刀秒小老师(36)、大老师(90)三刀，hunterHp 默认 150；**老师打猎手改扣 HP**——原生剑击弹体过滤 `TeacherSlashProjectile.Student` 只认 IsStudent（postfix 放行猎手）+ `OnChalkHitRequest` prefix 命中猎手改扣血（原生对 IsTeacher 直接 return），小老师击晕 FindStunTarget/OnServerStun 放行猎手、命中=晕眩表现+扣 2 HP；猎手 HP 清零 `DamageHunter`→RevertVisual+`InfectStudent` 直接感染；**猎手击杀的老师永久出局**：`KillTeacher` 广播 Down Kind=3（Kill，不进 downedUntil 复活流程），各端 `TickKilledTeachers` 每帧续晕 2s 压躺（TryGetProjectileStun 在 IsStunned 时拒绝，到期当帧续上无空窗），吹箭/近战/击晕对出局者全部 gate；**老师全灭=学生立即胜**：服务端 Tick `TickWinCheck` 校验 teacher-team（`UnitSubType==Teacher`（绕过翻转属性取原生）|| infected）全在 killedTeachers → 反射调 FinishClass(false,null)；快照 DTO 带 hunters/killed 名单自愈+中途加入端补放大）。服务端权威状态（enabled/infected/hp/stamina）经 `BioChemStateMessage` 全量快照广播（变更即时 + 每 5s 重广播自愈）；大老师 HP/小老师 HP+体力全是 mod 自建（学生预制体无 SkillComponent），大老师写板书加成走 `BlackBoardComponent.UseSuccess` postfix（**坑：`__instance.owner` 是黑板单位不是用者老师**，写作端判定要用 `UnitManager.localPlayer`；`UseSuccess`/`isUsing` 只在写作端为真，`SetUsing(false)` 会把私有字段 `usingTeacher` 置空所以 postfix 里拿不到）→ C2S 上报；**小老师体力 = 本地模拟**（参考原生老师能量：走路每秒消耗 `staminaDrainPerSecond`（位移差判定，仅上课期间）、写板书长按 `BlackBoardComponent.OnUpdate` postfix 期间按 `staminaPerWrite/blackboardHoldTime` 速率缓慢回复、击晕按 F 一次性扣 `staminaCostPerStun`；`selfStamina<0` 表示未初始化，快照同步跳过覆盖自己的体力（本地显示权威），击晕请求 `BioChemStunMessage.Stamina` 带申报值，服务端 clamp 后校验够才放行并记录扣后值，漂移自愈）；挂柱计时补丁在 `PunishPillarComponent.OnResponseBindStudent/OnResponseReleaseStudent`（服务端记录），超时经反射调 internal `SkillMessageHandler.ReleaseStudentOnServer`（rescuer=本人避免救援惩罚）成功后感染；小老师击晕借 `TryGetProjectileStun` 精确时长通道广播各端本地应用；**吹箭筒 = 原生 Taser 物品、按 1 掏出/收起**（游戏里 Taser 表现层就是嘴叼吹箭：`TaserSettings` menuName「Game/吹箭表现」、SubType=613 且 MountPos=15）：学生按 1 发 C2S `BioChemDartMessage.Toggle` → 服务端 `ToggleDart`：未掏出时服务端 `CreateCommonUnitDirectly` 生成真 Taser 物品（**模型异步加载，嘴部挂点就绪前 TryEquipMouthItem 必失败 → `pendingEquip` 队列逐帧重试挂载，成功/超时移出**），已掏出时 `SyncBack` 回收（打光那把原生管线已回收则直接落到掏出分支）；瞄准/弹体/音效/眩晕全走原生管线，命中结算 hook `UnitMessageHandler.OnTaserImpact` postfix（只结算玩家老师：扣 HP+减速+广播 `BioChemDartMessage.Hit`，原生 TryStun 眩晕保留，大老师被吹箭晕=与游戏内一致）；减速层数以消息里的服务端权威值校准被命中者本端计数（本地盲加会在丢包/抖动下与主机漂移，表现成减速只在主机生效），只在本端衰减（移速 postfix 同 GetCurrentMoveSpeed）；**吹箭伤害随已感染学生数递增**（`dartDamageBonusPerInfected` 每个 1 个小老师 +该系数，封顶 +300%，0=关闭；`EffectiveDartDamage()` 服务端结算与 UI 共用同一公式，客机 infected 随感染广播/快照即时对齐；信息盒上课期间全身份显示"吹箭伤害 X（基础 Y，感染加成 +Z%）"）；**课程进行中锁定模式开关**（`ClassInProgress()` = Prepare/Classing：`SetEnabled` 服务端拒绝 + 面板按钮禁用，课前/下课后可改）；HP 归零 → 广播 Down（各端 TryGetProjectileStun 晕倒）→ 服务端延时广播 Respawn（办公室=大老师 BornPosition，各端显式清倒地眩晕——Down 起表比服务端计时晚一个网络延迟，"刚好自然到期"不可靠——再反射调 internal `Unit.ApplyPlayerRelocation` 规范传送（裸 SetPosition 不动刚体/NetData），HP 按 TickDowned 同款公式全端立即恢复不等快照，本人复活重置本地体力模拟基准）。UI：**优先嵌入游戏设置窗口 `BioChemGameSettings.cs`**——克隆 UISettingView 最后一枚页签（位置沿相邻页签间距外推、Button 拆掉重建接线 `OnClickTab(newIndex)`、UI_SettingTab 组件保留）、新建 `BioChemSettingPanel : UI_SettingPanel`（RectTransform 抄 panels[0]，SwitchTab 时游戏会调 SetInfo 正好刷数值）追加进公开的 tabList/panelList；F7 = `UIManager.ShowUI(EUIWindowType.Setting)`（返回 UniTask，csproj 需引用 UniTask.dll）+ 轮询 GetOpenUI 后跳页；玩家手动开过设置也会 1Hz 扫描自动挂页；**嵌入失败全程回退自建 UGUI 面板 `BioChemPanel.cs`**（复用游戏设置窗口素材：窗口池找 UISettingView 克隆窗框/按钮图/字体，关闭过的窗口 SetActive(false) 回池不销毁；克隆时剥非 UnityEngine 脚本/Animator/LayoutGroup 系/滑条等残件，Button 持久监听清不掉要 DestroyImmediate 重建，销毁后引用不能再访问）；**自建面板退场用 `HideForEmbedded()` 只藏不拆**——不能发 SetUIInputRule(false)，会把游戏设置窗口刚激活的同名规则关掉；页内容共用 NumFields 步进器（24 项数值，正好排满两列十二行网格，房主课前可改，Unity Text 同值写入不重排可每帧刷）；F7 三通道开关——Rewired（HandleKeys 里 F7 不吃 InputManager 单例门槛，主菜单也可用）+ IMGUI Event 兜底（Pump.OnGUI，**仅 Rewired 未就绪时启用**，否则按住 F7 的 IMGUI 重复 KeyDown 会令面板闪烁）+ 右下角信息盒可点击按钮，`TogglePanel` 0.25s 节流防双触发；打开时按 `UISettingView.OnActive` 同款 `ChangeRuleSet(EInputRule.Rule_UISetting)` 冻结玩家输入，遮罩点击关闭）+ **右下角信息盒集中显示全部状态**（IMGUI，偏左 320px 避开交互按钮区，宽度按提示行自适应）：身份行（老师/小老师/学生）+ 红色 HP 条 + 小老师绿色体力条（Texture2D.whiteTexture + DrawTexture color/border/radius 重载）+ 击晕冷却/体力不足/吹箭/减速提示 + 小老师名单；小老师体力不足时 `TryMiniStun` 本地与服务端（申报值校验）双重门槛都拒绝击晕。**MsgCenter 坑**：游戏自己往 301/2017 上注册了大量监听（黑板/门/抽屉/柱子/抓捕组件），mod 必须用实例目标注册（`MsgHooks` holder）+ `MsgCenter.UnRegister(key, target)` 按目标注销——`UnRegister(key)` 会把该 key 上游戏自己的监听全部清掉；快照同步不做 `Version < version` 门槛（version 静态值跨房间不重置，与 TrickMole v2.3.4 同款教训）。挂柱超时强释的 Tick 遍历 `pillarBinds` 必须用副本（迭代中会删条目）。**公告必须随快照 `Notify` 字段广播**（`NotifyAll` 只在调用端弹，服务端公告客机看不到；Notify 只随本次广播、定时重广播恒空）。**服务端校验用 `ServerClassActive()`**（只看课堂状态），不能拿 `CanClassAct()` 里房主本地的输入冻结态去卡远程玩家的击晕/吹箭结算。克隆设置窗框时 Button 的序列化持久监听 `RemoveAllListeners` 清不掉（目标组件已被剥掉），要 DestroyImmediate 拆掉重建，LayoutGroup 系也要剥掉防重排。；**连发吹箭筒 = 按 1 掏出的唯一吹箭**（原生 Taser 物品、单发模式已移除，`fireInterval` 默认 0.25s（clamp 下限 0.1s=最高每秒 10 发），**备弹制**：`dartMaxAmmo` 默认 200 发（生化就是要泼水，面板可调到 999），服务端权威计数 `dartAmmo` 懒初始化为满、耗尽拒发，客户端 `selfDartAmmo` 本地乐观扣减 + 每发 `AmmoSync`（Dart 消息 Kind=5，TargetHp 装弹数，无序列化改动）校准，新一局 ClearRoundState 回满；**吃东西补弹** `dartAmmoPerFood` 默认 100（0=关闭）——普通食物 hook `FoodComponent.OnItemUseSuccess` prefix 判最后一口（入口耐久 ∈ (0,1]，与原生一致；香蕉/马桶食物子类都调 base 全覆盖，peel/leftovers 不调 base 天然不触发），火锅食材 hook `HotPotIngredientComponent.Apply` prefix+postfix 判 Consumed 翻转且 `RequestUserUID` 是本人（Apply 有 version 门槛防乱序旧包，postfix 读 `__instance.State` 不读响应体）；C2S `EatRefill`（Kind=6）服务端 `FoodRefillCooldown`(2.5s) 冷却防伪，满弹静默，备弹增加时 AmmoSync 顺带本地弹提示）：连发靠 mod 接管开火——prefix `TaserComponent.HandleInput` 吞掉本地玩家的原生单击开火（`___user` 字段注入判持有人，UseItem2 瞄准不受影响，嘴里的吹箭永不被消耗），客户端按住左键每 `fireInterval` 秒反射调原生 `TryBuildShot` 造弹（弹道参数与原版一致）→ 把 shot.itemUID 换成**虚拟弹体 UID**（`0x6000...` 高位标记+玩家UID截29位+序号，防与真实 unit 撞；弹体重叠期唯一 UID 保证注册/命中/销毁互不干扰）→ 反射调 internal `TaserProjectile.PlayFire` 本地渲染 → C2S `MgFire` → 服务端校验（嘴上真挂吹箭+限频 `mgFireUntil`+**备弹>0**+方向/参数合法）后**反射写原生 `taserShots` 注册表**（索引器覆盖，ValueTuple 反射构造）→ 广播 `MgShot` 让其他端 PlayFire（射手自己跳过本地已预测）→ 弹体命中走原生 `OnTaserImpact`（眩晕/音效/Hit 广播原样生效），`AfterTaserImpact` 自动结算；**命中结算有 `lastImpactWasRegistered` 门槛**（prefix 查 taserShots.ContainsKey，被原生拒绝的弹不扣血）。**AK47 表现层（v1.8.0，`Ak47Weapon.cs`）**：原生 Taser 管线全保留（单发已移除、管线只有 mod 连发在用），整体替换表现层不动网络与平衡——①曳光弹=换装 `ConfigManager.TaserSetting.projectilePrefab`（普通字段）为运行时构建的细长发光方块，**prefab 必须保持激活**（Instantiate inactive prefab 得到隐形实例）停放在地图下方 DDOL；②枪声=Harmony prefix 拦 `AudioManager.PlayOneShot(TaserFire,Vector3,PlaySoundRequest)` 吞原生"啾"声 → FMOD CoreSystem 低级 `createSound(MODE._3D|LOOP_OFF)+playSound+set3DAttributes` 播 `Assets/ak47_shot.wav`（CC-BY 枪声裁自 SKS 单发；缺失时运行时合成 PCM 兜底；本地 PlayFire 与远端 MgShot→PlayFire 都经此 patch 全端覆盖；createSound 失败不置 soundTried 留懒重试）；③枪口起点=TickMachineGun 在 TryBuildShot 后覆写 `shot.origin=枪口世界坐标`（服务端只查 NaN/Inf 不校验距离），本地预测与 MgShot 广播同源；④手持模型=`Assets/ak47.obj`（CC0）运行时 OBJ 解析（逐角展开顶点+fan 三角化、最长轴旋到 +Z+归一化 0.9m、`ModelYawDeg` 朝向常数实测调），缺失/失败回退程序化方块 AK；⑤显隐驱动="嘴上有 Taser 物品"（Tick 扫描）→ 世界枪挂 HandPosition（自己的走 PlayerSelf 层）+本机 viewmodel 挂相机（口焰 quad+PointLight 50ms、后坐冲量衰减）+**Taser 物品模型渲染器隐藏**（渲染器级组件照常跑，无还原逻辑）；⑥弹匣=纯客户端节奏（MagSize=30/ReloadSeconds=2.0 常量，面板满不加项）：上膛从备弹装满、打空按住扳机自动换弹、R 手动换弹、**StartReload 必须带 reloading 守卫**（BlockFire 每帧调用否则 reloadDoneAt 被推后永不完成）、BlockFire 需 equipped 拦截（未装备时不触发自动换弹防状态污染）、AmmoSync 校准时 clamp mag≤备弹、换弹完成 mag=min(30,备弹)——备弹仍是服务端权威，服务端 MgFire 逻辑零改动；⑦**清局 ResetForNewRound 必须显式 Destroy 世界枪**（玩家单位跨局存活，只清注册表=枪永远挂在手上且重新装备时逐局堆积）；⑧OBJ 解析 Split 要 RemoveEmptyEntries（双空格/尾空格产生空 token→零顶点退化三角形）；⑨口焰 quad 用 Sprites/Default 半透明（URP Lit 不透明会渲成实心色块）。**数值设置进面板**：状态快照 DTO 带 `cfg` 全量 JSON——房主面板改数值（24 项步进器，两列十二行网格）→ `Clamp()` + 本机落盘 + `BroadcastState`，客机应用后只覆盖运行时 Instance 不落盘；面板双页签「数值设置/玩法规则」，规则文案按当前数值动态生成，步进器仅房主课前可点（课程中锁定）。数值配置 `Configs/biochem/biochem.json`。**小老师抓取挂柱（v1.7.0，`MiniTeacherCatch.cs`）**：感染即补挂原生 `CatchComponent`（真老师只在单位创建时挂载，`UnitManager.cs` 老师分支）——服务端抓捕（skillID=3）/挂柱校验用 IsTeacher 翻转属性天然通过，补组件即打通 扛走(102)→挂柱(103)→柱超时感染 全链路，挂柱提示/服务端挂柱零 patch；客户端门槛①=DealArrestEffect 查 localPlayer 的 SkillComponent（学生预制体没有）→ prefix 仅 infected 本机接管复刻原方法（含 NPC 人盾分支）去掉能量门槛；**故意不挂 SkillComponent**（会解锁粉笔攻击/提问改变平衡；CatchComponent 无攻击输入零副作用）；抓取不耗体力（CatchUnit 能量扣减经 `SkillComponent?.` 静默跳过）；扛人者被猎手击杀（KillTeacher）/吹箭打倒地（DownUnit）没有原生"被晕放手"路径（依赖 TeacherStunComponent）→ 服务端 `ForceReleaseIfCarrying` 反射 ReleaseStudentOnServer 原地放下（operatorReleasePosition 防"无空位"拦截）；动态补挂错过 ActiveComps 批量唤醒 → 手动 `Wake(true)`，收组件 `Wake(false)`+`RemoveComponent`（Unit.RemoveComponent 只摘链表、不注销 msg 2017，生命周期自己走完）；Tick 1s 自愈兜底（晚加入/快照先于单位生成）；清局/退房收回（补挂过的单位记在 `added` 集合防误删原生组件）；猎手不在 infected 天然不开放抓取。与 TrickMole 并存：混的入会把小老师当"老师"推（无 TeacherStunComponent 仅浪费冷却，无崩溃）。
- **新增一个 Mod**：建文件夹 → csproj 只写 `AssemblyName`/`RootNamespace` → `mod.json`（id/assembly/entryType 三处一致）→ `dotnet sln add` → 源码里 `OnLoad(ModContext)` 入口
- 反编译游戏源码（ilspycmd 8.2 生成，写 hook 前先搜，不要凭空猜类名）：
  - `D:\WorstClassGameCode\` — Assembly-CSharp（游戏主逻辑，1179 个 .cs）
  - `D:\WorstClassGameCodeModAPI\` — WorstClassEver.ModAPI.dll（IMod/ModContext 契约）
  - `D:\WorstClassGameCodeFirstpass\` — Assembly-CSharp-firstpass
- 官方 Mod 实战样板（随包带源码）：`E:\SteamLibrary\steamapps\workshop\content\5175310\3837274608\`（操场广播扩展曲库；已在游戏内禁用，避免与我们自己的 PlaygroundMusic 冲突）

## 构建与部署

```bash
dotnet build D:/WorstClassMod/WorstClassMods.sln
# 或单个 Mod：dotnet build D:/WorstClassMod/PlaygroundMusic/PlaygroundMusic.csproj
```

- 构建成功后自动部署到 `E:\...\Worst Class Ever\Mods\<程序集名>\`（Directory.Build.targets 的 CopyToMods）
- **游戏必须完全重启才会加载新 DLL**
- Rider 的 MCP `build_solution_state` 会误报 `buildIsSuccess: false`——以 `dotnet build` 输出为准
- 验证编译错误：`dotnet build -v q 2>&1 | grep error`

## 创意工坊发布（Workshop/）

- **每个 Mod = 一个独立工坊物品**：`Workshop\<Mod>\item.vdf`（元数据权威源，含 publishedfileid）+ `Workshop\<Mod>\preview.*`；一次命令只动一个 Mod
- 发布：`powershell -ExecutionPolicy Bypass -File Workshop\upload.ps1 <Mod> -Build -ChangeNote "..."`；`-SkipUpload` 只同步校验不连 Steam
- 上传器 `Workshop\Uploader\`（net8 控制台，**不进 sln**，csproj 设 `IsGameMod=false` 跳过共享 targets 部署）：引用游戏自带 Steamworks.NET.dll + steam_api64.dll，复用桌面 Steam 登录会话（`steam_appid.txt` 自动生成）——无 SteamCMD、不输账号密码。首次发布成功自动回填 publishedfileid 到 vdf，之后同一命令即更新
- 为什么不用 SteamCMD：本机网络对 Valve CDN 的 bootstrap 明文 HTTP 劫持（500）且旧引导器无 HTTPS；`Workshop\steamcmd\` 留作挂代理时的备用（vdf 格式与上传器互通）
- vdf 注意：标题/描述每次上传覆盖网页手改；描述支持 BBCode、字面 `\n` 转换行；值内禁英文双引号；content 路径每次上传自动按当前机器写回
- 详细说明见 `Workshop/README.md`

## 官方框架要点

- 入口契约：`public` + 非抽象 + 无参构造 + 实现 `WorstClassEver.ModAPI.IMod`（唯一方法 `OnLoad(ModContext)`）
- `ModContext` 四字段：`Id` / `ModDirectory`（读自己资源）/ `ConfigDirectory`（每 Mod 独立配置目录，自动创建于 LocalLow）/ `Log`（输出自动带 `[Mod:<mod.json 里的 id>]` 前缀）
- 加载管线：`GameRoot` 引导 → 全部游戏系统 Init 之后 → `ModRuntime.InitializeAsync()` 逐个 `Assembly.LoadFrom` → `OnLoad`。**在引擎第 0 帧清场之后**，创建的对象天然存活，无需任何自愈逻辑
- 加载失败降级为 `PartialDataMounted`（JSON 覆盖仍生效、代码不跑）；`IsSafeMode` 跳过全部代码
- **Harmony 完全可用**：游戏用 `ModDllInspector`（Mono.Cecil）扫描 DLL 里的 `[HarmonyPatch]` 特性做 Mod 间冲突编目，不是限制
- 清单 `affectsNetworkMessages: true` 会触发多人兼容性特殊检查——纯客户端功能保持 `false`
- 本地 Mod 启停/顺序由游戏内 Mod 管理界面控制（落盘在 LocalLow `Mods/load-order.json`）

## ⚠️ 引擎怪癖：第 0 帧清场（历史知识，仍要牢记）

这个游戏的引擎在**启动第 0 帧无差别清空 DontDestroyOnLoad 场景**（原生层行为，托管 `Object.Destroy` 探针探不到；曾用金丝雀对象验证）。**官方 Mod 加载晚于清场所以不受影响**——这正是弃用 BepInEx 的核心原因之一。若未来做任何"早期注入"（BepInEx、Doorstop 等），必须在 `OnDestroy` 里自愈重建，否则必死。

## 日志与调试

- **运行时日志**：`C:\Users\ASUS\AppData\LocalLow\Funcore Games\Worst Class Ever\Player.log`（每次启动重写；`Player-prev.log` 是上一会话）。Mod 日志在其中以 `[Mod:<mod id>]` 前缀出现，如 `[Mod:worstclass.playgroundmusic]`
- 断点调试：Rider `Run → Attach to Process`（Ctrl+Alt+P）→ 选 `Worst Class Ever.exe`
- 判断"游戏重启了没有"看 Player.log 头部的启动序列，不要看别的日志文件的时间戳

## csproj 引用注意事项（c1 变体特有）

- `UnityEngine.dll`（facade）**必须保留**——这个变体里 `MonoBehaviour` 定义在 facade 而非 CoreModule，删掉会 CS0012
- UGUI 控件（Image/Button/Text/CanvasScaler/GraphicRaycaster）全在 `UnityEngine.UI.dll`，**无独立 EventSystems.dll**（EventSystems 命名空间也在 UI.dll 里）
- `SceneManager.sceneLoaded/sceneUnloaded/activeSceneChanged` 事件**编译期不可用**（facade 遮蔽，CS0117），运行时才存在；需要的话用运行时反射挂接
- 遇到莫名的 CS0117 先怀疑 facade 与 CoreModule 的类型遮蔽

## 游戏环境事实

- 游戏路径：`E:\SteamLibrary\steamapps\common\Worst Class Ever`
- **Steam 客户端必须先启动并登录**，否则 `SteamAPI_Init() failed`，多人建主机报"Steam 尚未初始化"
- 服务器跑在房主进程里（Mirror Host 模式），无专用服务器；Steam/UOS/Edgegap 三种云服务只做房间管理+中继+校验。改服务器行为 = 房主装 Mod 才生效
- 游戏退出时 `youme_voice_engine` 在进程关闭阶段崩溃一次——游戏自身问题，无害
- Rewired action 警告、FMOD listener 警告、DontDestroyOnLoad 警告都是游戏侧噪音，与 Mod 无关
- 物品是 `ClassroomItem` + 行为组件模式（`BananaComponent`/`BookComponent` 等）；配置表走 Luban（`ConfigManager`），官方 Mod 可用 JSON 覆盖改数据

## 代码约定

- 日志消息用中文，面向玩家可读；确认类日志用 `[状态]` 前缀
- OnGUI 覆盖层（如曲库检测面板）：外层无样式 `BeginArea` + 内层 `BeginVertical(GUI.skin.box)`，盒子高度自适应内容（别写死高度，高 DPI 会裁切）
- 版本号在 `mod.json` 里手动维护（改了记得同步描述）
- 引用游戏程序集只取所需；打补丁直接 `using HarmonyLib`

## 典型工作流

1. 需求 → 在 `D:\WorstClassGameCode` grep 找游戏的类/方法（类名方法名 100% 可信，局部变量名可能失真）
2. 在对应 Mod 项目里写 Harmony 补丁（`AccessTools` + `harmony.Patch(...)`，参考 Workshop 音乐 Mod 的 `Source/`）或读游戏状态
3. `dotnet build` → 让用户重启游戏 → 读 Player.log 验证 `[Mod:<id>]` 日志
4. 可配置项走 `ModContext.ConfigDirectory`（官方每-Mod 配置目录）或 JSON 覆盖（改游戏数据表）
