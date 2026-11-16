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
  - 角色抽象 `RoleDef.cs`（`[Serializable]` + JsonUtility）：id/name/count（每局名额）/trickMultiplier/canBroadcast/canPush/各技能数值/挂柱扣分比例。配置在 `Configs/trickmole/trickmole.json`（`RoleConfig.Load` 首次运行生成默认，只含"混的入"）；**加已有技能的新角色 = 配置加一个块**，新技能才改代码。服务端配置权威，客机经 `MoleSelectMessage.RoleJson`（JsonUtility 整包同步）拿运行时副本——各端配置可不一致不炸。
  - 状态：`assigned`（uid→RoleDef 字典）+ `progressByUid`（捣蛋累计）+ `pushCooldownUntil`（per-玩家推倒冷却），单 moleUID 时代已终结。
  - 选人：`TryAssignRoles` 按 `ExpandSlots()` 名额展开 + Fisher–Yates 洗牌后逐名额随机填充（学生数少于名额时每个角色等可能出现而非配置顺序靠前者必被填，满员时等价不放回均匀抽样），每名额 `BroadcastAssignment`（version 全局单调递增，重广播也递增覆盖同 uid）；清局 `BroadcastClearAll`（UID=0 → 客机 clear）。
  - 混的入能力：捣蛋分 ×2 并单独累计捣蛋值（`progressByUid`，各端从计分入口 prefix 统计乘后实际计入总进度的量，每节课重置）；被挂罚站柱时总体进度扣累计的 2/3、记录归零（个人贡献分不动；扣分照 PublishTrickCountChange 模式联动全端，客机在挂柱补丁里本地同步归零）；上课按 B 开关 3D 广播（跟随玩家移动，起播 3 秒宽限不计分、停止后冷却 20 秒，数值取广播者角色配置 `broadcastRole`）；老师身后 110° 扇区 2.2m 按 E 推倒 2.5 秒冷却 30 秒（`TeacherStunComponent.TryGetProjectileStun` 借电击枪精确时长通道；`MolePushMessage` 带 StudentUID 只对推手启动冷却）；广播曲池 = 原版 4 首 + PlaygroundMusic 全员交集扩展曲（`PmBridge.cs` 反射软依赖，懒解析——Mod 加载顺序不保证；扩展曲 CoreSystem 播放 + Started 对齐 + mp3 播完自动 StopRequest；Mirror null string 读回 ""，hash 判断必须 IsNullOrEmpty）；广播期间操场广播柱静音且暂停其计分（柱子实例音量 0 压制 + PlaygroundMusic 低级 Channel 反射 setMute + `AwardPlaygroundMusicScore` prefix 拦截非广播者计分）。
  - 书呆子（`id=nerd`，内置默认角色）：捣蛋分 ×0.5、无广播/推倒；连续 `immunityIdleSeconds`(20s) 未得分获得一次免伤（上限 `maxImmunityCharges`=1），得分立即失去；服务端在 `SkillMessageHandler.OnUseSkillMessageRequest`(skillID=3 抓捕)、`StudentStunComponent.GetStun`、`UnitMessageHandler.OnStudentGetStunRequest` 三处拦截老师攻击（排除 Banana 滑倒）并广播 `MoleImmunityMessage`（Gained/Consumed），Consumed 时全员走 `GameNotifyManager.ShowCommonNotify` 弹原生提示；静心计时各端从同一计分入口自行维护，服务端获得/消耗时广播对齐。旧 trickmole.json 缺内置角色时自动补全并写回。右上角提示盒只显示"角色：玩家"，仅本地玩家自己的角色追加冷却/免伤状态行。
  - 体育生（`id=athlete`，内置默认角色）：无计分加成、无广播/推倒/挂柱扣分（`progressPenaltyFraction=0`）；`speedBoostUnseen`(1.5)/`speedPenaltySeen`(0.75) 老师视野外/内移速倍率，postfix `InputComponent.GetCurrentMoveSpeed`（本地玩家移速唯一出口，`CurrentMoveSpeed` 只被本组件移动物理和一个跟随相机用）；"被老师看见"=任一存活未眩晕老师（遍历 `unitsDict` 过滤 `IsTeacher`，AI 老师也算）头顶点距离内 + 老师朝向锥角 + 头顶连线 raycast 无遮挡（口径抄 `StudentNPCComponent.HasObstacleBetween`，排除师生双方碰撞体，layermask -5），结果按帧缓存（`athleteSightCache`）；`actionSpeedMultiplier`(1.5) 进食/救人提速——prefix `StudentItemUseComponent.OnUpdate` 的 `ref iDt` 只加速按住吃东西（FoodComponent/HotPotIngredientComponent，`____isHoldingUse` 字段注入；不影响墨水/手机等按住使用物），postfix `PunishPillarComponent.GetRescueDuration` 缩短救援时长（手动救援进度与无人机救援 `DroneInteractionTarget` 共用此时长；`ReleaseStudentOnServer` 不校验耗时，纯客户端结算）。视锥角度/距离也是角色配置项（`sightAngleDegrees`/`sightDistance`，0 = 默认 110°/10m，借鉴 NPC 对视参数）。
  - 加成补丁在 `ClassroomManager.OnResponseTrickCountData`（游戏全部捣蛋计分的唯一服务端入口，负分不乘）；挂柱扣分补丁在 `PunishPillarComponent.OnResponseBindStudent`；按键用 Rewired 硬件级 `Keyboard.GetKeyDown`（程序集名 `Rewired_Core`；`IGlyphProvider` 在 `Rewired.Interfaces`）；按键提示浮层 `KeyHint.cs`（Rewired 原生按键图标，`IGlyphProvider.TryGetGlyph` 是 **out** 参数；`FontStyle` 在 `UnityEngine.TextRenderingModule`、`JsonUtility` 在 `UnityEngine.JSONSerializeModule`——都已加 props 引用）；FMOD 注意：Studio `EventInstance` 无 getChannel，静音/音量走实例自身 API；低级 `Channel` 是 struct 无 isValid，守卫靠调用方状态
- `BioChem\` — 生化模式（id=`worstclass.biochem`，`affectsNetworkMessages: true`）：挂柱超时（默认 20s）感染变小老师。核心机制 = **Harmony postfix 翻转 `Unit.get_IsTeacher`/`get_IsStudent`**（感染者对全游戏表现为老师：板书准入、NPC 敌我、捕获资格、名牌隐藏全走这两个属性，无需换模型）+ 感染者捣蛋计分 prefix 封锁。服务端权威状态（enabled/infected/hp/stamina）经 `BioChemStateMessage` 全量快照广播（变更即时 + 每 5s 重广播自愈）；大老师 HP/小老师 HP+体力全是 mod 自建（学生预制体无 SkillComponent），大老师写板书加成走 `BlackBoardComponent.UseSuccess` postfix（**坑：`__instance.owner` 是黑板单位不是用者老师**，写作端判定要用 `UnitManager.localPlayer`；`UseSuccess`/`isUsing` 只在写作端为真，`SetUsing(false)` 会把私有字段 `usingTeacher` 置空所以 postfix 里拿不到）→ C2S 上报；**小老师体力 = 本地模拟**（参考原生老师能量：走路每秒消耗 `staminaDrainPerSecond`（位移差判定，仅上课期间）、写板书长按 `BlackBoardComponent.OnUpdate` postfix 期间按 `staminaPerWrite/blackboardHoldTime` 速率缓慢回复、击晕按 F 一次性扣 `staminaCostPerStun`；`selfStamina<0` 表示未初始化，快照同步跳过覆盖自己的体力（本地显示权威），击晕请求 `BioChemStunMessage.Stamina` 带申报值，服务端 clamp 后校验够才放行并记录扣后值，漂移自愈）；挂柱计时补丁在 `PunishPillarComponent.OnResponseBindStudent/OnResponseReleaseStudent`（服务端记录），超时经反射调 internal `SkillMessageHandler.ReleaseStudentOnServer`（rescuer=本人避免救援惩罚）成功后感染；小老师击晕借 `TryGetProjectileStun` 精确时长通道广播各端本地应用；**吹箭筒 = 原生 Taser 物品、按 1 掏出/收起**（游戏里 Taser 表现层就是嘴叼吹箭：`TaserSettings` menuName「Game/吹箭表现」、SubType=613 且 MountPos=15）：学生按 1 发 C2S `BioChemDartMessage.Toggle` → 服务端 `ToggleDart`：未掏出时服务端 `CreateCommonUnitDirectly` 生成真 Taser 物品（**模型异步加载，嘴部挂点就绪前 TryEquipMouthItem 必失败 → `pendingEquip` 队列逐帧重试挂载，成功/超时移出**；弹药无限），已掏出时 `SyncBack` 回收（打光那把原生管线已回收则直接落到掏出分支）；瞄准/弹体/音效/眩晕全走原生管线，命中结算 hook `UnitMessageHandler.OnTaserImpact` postfix（只结算玩家老师：扣 HP+减速+广播 `BioChemDartMessage.Hit`，原生 TryStun 眩晕保留，大老师被吹箭晕=与游戏内一致）；减速层只在被命中者本端衰减（移速 postfix 同 GetCurrentMoveSpeed）；**课程进行中锁定模式开关**（`ClassInProgress()` = Prepare/Classing：`SetEnabled` 服务端拒绝 + 面板按钮禁用，课前/下课后可改）；HP 归零 → 广播 Down（各端 TryGetProjectileStun 晕倒）→ 服务端延时广播 Respawn（办公室=大老师 BornPosition，本人端 `Unit.SetPosition` 传送，复活重置本地体力模拟基准）。UI：**优先嵌入游戏设置窗口 `BioChemGameSettings.cs`**——克隆 UISettingView 最后一枚页签（位置沿相邻页签间距外推、Button 拆掉重建接线 `OnClickTab(newIndex)`、UI_SettingTab 组件保留）、新建 `BioChemSettingPanel : UI_SettingPanel`（RectTransform 抄 panels[0]，SwitchTab 时游戏会调 SetInfo 正好刷数值）追加进公开的 tabList/panelList；F7 = `UIManager.ShowUI(EUIWindowType.Setting)`（返回 UniTask，csproj 需引用 UniTask.dll）+ 轮询 GetOpenUI 后跳页；玩家手动开过设置也会 1Hz 扫描自动挂页；**嵌入失败全程回退自建 UGUI 面板 `BioChemPanel.cs`**（复用游戏设置窗口素材：窗口池找 UISettingView 克隆窗框/按钮图/字体，关闭过的窗口 SetActive(false) 回池不销毁；克隆时剥非 UnityEngine 脚本/Animator/LayoutGroup 系/滑条等残件，Button 持久监听清不掉要 DestroyImmediate 重建，销毁后引用不能再访问）；**自建面板退场用 `HideForEmbedded()` 只藏不拆**——不能发 SetUIInputRule(false)，会把游戏设置窗口刚激活的同名规则关掉；页内容共用 NumFields 步进器（17 项数值，房主课前可改，Unity Text 同值写入不重排可每帧刷）；F7 三通道开关——Rewired（HandleKeys 里 F7 不吃 InputManager 单例门槛，主菜单也可用）+ IMGUI Event 兜底（Pump.OnGUI，**仅 Rewired 未就绪时启用**，否则按住 F7 的 IMGUI 重复 KeyDown 会令面板闪烁）+ 右下角信息盒可点击按钮，`TogglePanel` 0.25s 节流防双触发；打开时按 `UISettingView.OnActive` 同款 `ChangeRuleSet(EInputRule.Rule_UISetting)` 冻结玩家输入，遮罩点击关闭）+ **右下角信息盒集中显示全部状态**（IMGUI，偏左 320px 避开交互按钮区，宽度按提示行自适应）：身份行（老师/小老师/学生）+ 红色 HP 条 + 小老师绿色体力条（Texture2D.whiteTexture + DrawTexture color/border/radius 重载）+ 击晕冷却/体力不足/吹箭/减速提示 + 小老师名单；小老师体力不足时 `TryMiniStun` 本地与服务端（申报值校验）双重门槛都拒绝击晕。**MsgCenter 坑**：游戏自己往 301/2017 上注册了大量监听（黑板/门/抽屉/柱子/抓捕组件），mod 必须用实例目标注册（`MsgHooks` holder）+ `MsgCenter.UnRegister(key, target)` 按目标注销——`UnRegister(key)` 会把该 key 上游戏自己的监听全部清掉；快照同步不做 `Version < version` 门槛（version 静态值跨房间不重置，与 TrickMole v2.3.4 同款教训）。挂柱超时强释的 Tick 遍历 `pillarBinds` 必须用副本（迭代中会删条目）。**公告必须随快照 `Notify` 字段广播**（`NotifyAll` 只在调用端弹，服务端公告客机看不到；Notify 只随本次广播、定时重广播恒空）。**服务端校验用 `ServerClassActive()`**（只看课堂状态），不能拿 `CanClassAct()` 里房主本地的输入冻结态去卡远程玩家的击晕/吹箭结算。克隆设置窗框时 Button 的序列化持久监听 `RemoveAllListeners` 清不掉（目标组件已被剥掉），要 DestroyImmediate 拆掉重建，LayoutGroup 系也要剥掉防重排。；**连发吹箭筒 = 按 1 掏出的唯一吹箭**（原生 Taser 物品、单发模式已移除，`fireInterval` 默认 0.5s）：连发靠 mod 接管开火——prefix `TaserComponent.HandleInput` 吞掉本地玩家的原生单击开火（`___user` 字段注入判持有人，UseItem2 瞄准不受影响，嘴里的吹箭永不被消耗），客户端按住左键每 `fireInterval` 秒反射调原生 `TryBuildShot` 造弹（弹道参数与原版一致）→ 把 shot.itemUID 换成**虚拟弹体 UID**（`0x6000...` 高位标记+玩家UID截29位+序号，防与真实 unit 撞；弹体重叠期唯一 UID 保证注册/命中/销毁互不干扰）→ 反射调 internal `TaserProjectile.PlayFire` 本地渲染 → C2S `MgFire` → 服务端校验（嘴上真挂吹箭+限频 `mgFireUntil`+方向/参数合法）后**反射写原生 `taserShots` 注册表**（索引器覆盖，ValueTuple 反射构造）→ 广播 `MgShot` 让其他端 PlayFire（射手自己跳过本地已预测）→ 弹体命中走原生 `OnTaserImpact`（眩晕/音效/Hit 广播原样生效），`AfterTaserImpact` 自动结算；**命中结算有 `lastImpactWasRegistered` 门槛**（prefix 查 taserShots.ContainsKey，被原生拒绝的弹不扣血）。**数值设置进面板**：状态快照 DTO 带 `cfg` 全量 JSON——房主面板改数值（17 项步进器，两列网格）→ `Clamp()` + 本机落盘 + `BroadcastState`，客机应用后只覆盖运行时 Instance 不落盘；面板双页签「数值设置/玩法规则」，规则文案按当前数值动态生成，步进器仅房主课前可点（课程中锁定）。数值配置 `Configs/biochem/biochem.json`。与 TrickMole 并存：混的入会把小老师当"老师"推（无 TeacherStunComponent 仅浪费冷却，无崩溃）。
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
