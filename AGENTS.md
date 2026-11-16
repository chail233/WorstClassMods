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
- `AITeacher\` — AI 老师（id=`worstclass.aiteacher`，`affectsNetworkMessages: false`，**纯房主侧 Mod：其他玩家不装也能看到 AI 老师移动/攻击/抓人**）：
  - 原理：房主 `CreateUnitNetData(1001/1002, syncMsg:false)` + 手动广播 `UnitBornMessageResponse_S2C`（玩家 ID 被排除在自动 UnitBorn 外，`UnitManager.cs:570`）+ `CreateCommonUnit(isLocal:true)`；客户端收到 UnitBorn 走 `CreateUnitAuto` 非玩家路由，模型/pose/动画走原生链路自动同步（`IsUnitPoseSender` 对 host 本地连接只要 `unit.isLocal` 即放行）。`GenerateComponentByType` postfix 按 UID 登记表给 AI unit 补挂老师组件 + `TeacherAIComponent`（**UID 登记必须在 CreateCommonUnit 之前**，postfix 在创建过程中就要查）。
  - 单人开局：prefix 放行 `SendStartClassMsg` 的 `Unit_CantSingleStart` 拦截；`GetCurrentIntValue(ClassTeacherCount)` postfix 减 AI 设定让原版少分真人老师；`AssignClassTeacher` prefix 全 AI 时接管、postfix 按"规则数−真人老师数"差额补 AI。F8 面板改数量/形态（写回 `Configs/aiteacher/aiteacher.json`），下局按铃生效。
  - AI 行为（`TeacherAIComponent.cs`，v1.1 证据驱动）：**课前倒计时就走位进教室**（出生点 teacherPos 在办公室/玄关，教室外强制把巡逻点定在教室内，卡死走传送），巡逻（50% 偏向捣蛋热点）→ 目标获取必须**有嫌疑**才追——视野内违规（手持捣蛋物品/玩电脑，判定口径抄 `SurveillanceCameraComponent.CollectViolationStates`，Manager 里 0.25s 共享缓存）或听见捣蛋雷达事件，无辜学生不追 → 追击（Chase 态 agent.speed × `chaseSpeedBoost` 冲刺）→ 粉笔按目标 `ForceComponent.Velocity` 预判落点（自建 4002 投射物；`ShootChalk` 依赖相机不能直接用）/ 剑气直调 AI 自己的 `TeacherMeleeComponent.HandleSlashFire`（**不能走 `OnTeacherSlashFire`：`TeacherSlashSender` 按 connection 解析会解析成房主本人**）→ 抓捕发 `UseSkillMessageRequest_C2S{skillID=3}`（host 直调，无连接归属校验）→ 挂柱自建 `TeacherBindStudentMessageRequest_C2S`（`BindStudentToPillar` 写死 localPlayerUID 不能用）。多 AI 协同 = Manager 目标认领表去重；移速默认 0 = 读房间规则 `TeacherSpeed`（与真人老师同速）；调查阶段有两个查看点。F8 面板难度四档（宽松/标准/严格/地狱=听力全图）一键应用预设。
  - ⚠️ 网络身份坑：`OnChalkHitRequest` 服务器要求"连接推导 UID == 消息 TeacherUID"，AI 的粉笔/剑气击晕上报必被拒——prefix 接管 AI 命中按原版服务端逻辑直接 `StudentStunComponent.GetStun`。AI 模型 rigidbody 置 kinematic 防 NavMesh 对抗（`OnDisBindObj` 还原防污染模型池）；教室 NavMesh 可能晚于 AI 出生才烘焙，`EnsureAgent` 每秒重试。csproj 额外引用 `UnityEngine.AIModule`（NavMeshAgent）+ `UnityEngine.PhysicsModule`（RaycastAll）+ `Luban.Runtime`（GameSetting 基类 BeanBase）。
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
