# WorstClassMods

《Worst Class Ever》的 Mod 集合，基于官方 **WorstClassEver.ModAPI** 框架开发（非注入式加载器），通过 Harmony 补丁 + 自定义 Mirror 网络消息实现，支持 Steam 多人联机。

> 游戏本体：[Worst Class Ever](https://store.steampowered.com/app/5175310/)（Unity 2022.3.62f3c1 中国变体，Mono 后端，Mirror 网络，服务器跑在房主进程里）

## Mod 一览

| Mod | 游戏 ID | 版本 | 说明 |
| --- | --- | --- | --- |
| 🎵 PlaygroundMusic（外高音乐扩展） | `worstclass.playgroundmusic` | 1.0.0 | 扩展操场广播柱曲库，播放本地 MP3 |
| 🐷 TrickMole（混的入） | `worstclass.trickmole` | 1.8.0 | 每节课随机一名"混的入"的内鬼玩法 |

### 🎵 PlaygroundMusic — 操场音乐扩展

扩展操场广播柱曲库：在原版 FMOD 曲目之外，把本地 MP3 文件加入操场广播。

- 把 `.mp3` 直接放进 `PlaygroundMusic/Music/` 目录（文件名支持中文），重启游戏生效；单首上限 64 MiB
- 曲目按文件内容识别（SHA-256），同名但内容不同的文件视为不同歌曲
- 联机时曲库自动协商，**只播全员都有的曲目**，选曲与播放起点经自定义 Mirror 消息同步，全员同曲同进度
- 曲库变化会在游戏内以 OnGUI 覆盖层提示检测结果

### 🐷 TrickMole — 混的入

每节课开始时随机选定一名真人学生为"混的入"，身份全员可见：

- 其正向捣蛋计分 **×2**（负分不乘，被抓照样扣），并单独累计捣蛋值（`moleProgress`，每节课重置）
- 被老师挂上罚站柱时：**总体进度扣除其累计捣蛋值的 2/3、记录归零**（个人贡献分不动），扣分经服务端广播全端一致
- 上课时按 **B 键**开关 3D 广播音乐（跟随玩家移动）；起播前 3 秒宽限不计分，停止后冷却 20 秒
- 广播曲池 = 原版 4 首 + **PlaygroundMusic 全员交集扩展曲**（软依赖：未装该 Mod 或交集为空自动退化为纯原版；扩展曲按统一进度对齐播放，播完自动结束本轮）
- 混的入广播播放期间，操场广播柱静音且暂停计分（分数只算混的入一路）

### BioChem — 生化地图

生化模式中的大老师和小老师拥有无限体力。完整写完一次板书回复生命，默认回复 9 点，可在 F7 设置里调整。

房主在课前按 **F7** 打开「生化模式」，开启模式，再点地图按钮在 **原版校园 → 隔离货场 → 蒸馏厂货场** 间切换。按铃后所有人都是学生，选了货场就直接进图。准备倒计时归零时，按房间的老师人数随机选人，被选中的玩家在原地变成老师，开局感染的小老师也在此时出现。老师和小老师头顶有红色旋转标记。结算后随原生课间流程返回校园，联机玩家需安装同版本生化 Mod。

- **隔离货场**是原创地图，货柜和中央挡板划分地面路线，东侧装卸台可经两端坡道登顶。
- **蒸馏厂货场**有室内仓库、巷道和露天庭院，场景取自 3DAssets.dev 的 [Distillery and Bonded Warehouse](https://3dassets.dev/packs/distillery-and-bonded-warehouse)（CC0）。运行时网格、贴图和碰撞烘入 `BioChem.dll`。
- 两张货场都设有学生出生区、教师复活区、原生黑板、八根罚站柱、两处补给箱和一台贩卖机。每处补给箱刷新一份零食和一件随机道具，贩卖机面向按钮按交互键领取，轮流发放零食和道具。道具包括泡泡糖、烟雾弹、闪光弹和墨水。上课后吃完零食按生化规则补弹。

## 构建与部署

前置：.NET SDK（netstandard2.1 目标）+ 游戏本体（Steam）。

1. 按需修改 `Directory.Build.props` 中的 `GameDir`，指向你的游戏安装目录
2. 构建（构建后自动把 DLL + `mod.json` + `Music/` 部署到 `游戏目录\Mods\<程序集名>\`）：

```bash
dotnet build WorstClassMods.sln
# 或单个 Mod：
dotnet build PlaygroundMusic/PlaygroundMusic.csproj
```

3. **完全重启游戏**加载新 DLL（游戏只在启动时加载 Mod）

游戏内的"Mod 管理"界面可控制本地 Mod 启停与加载顺序。

## 目录结构

```
WorstClassMod/
├── WorstClassMods.sln        # 多 Mod 解决方案，一次构建全部
├── Directory.Build.props     # 共享构建配置：目标框架、游戏路径、程序集引用
├── Directory.Build.targets   # 共享部署逻辑：构建后复制到游戏 Mods 目录
├── AGENTS.md                 # 开发指南（游戏逆向要点、引擎怪癖、工作流）
├── PlaygroundMusic/          # 操场音乐扩展
│   ├── ModEntry.cs           # 广播柱补丁 + FMOD CoreSystem 播放本地 MP3
│   ├── MusicSync.cs          # 联机曲库协商（SHA-256 交集 + 时间对齐）
│   ├── Music/                # 自带/自备曲目（MP3 不入库，玩家自备）
│   └── mod.json
└── TrickMole/                # 混的入
    ├── ModEntry.cs           # 选人/计分/挂柱扣分/3D 广播补丁
    ├── MolePump.cs           # 自定义 Mirror 消息泵
    ├── PmBridge.cs           # 对 PlaygroundMusic 的反射软依赖桥
    └── mod.json
```

## 新增一个 Mod

1. 建文件夹，`csproj` 只写 `AssemblyName` / `RootNamespace`（引用与配置全由 `Directory.Build.props` 提供）
2. 写 `mod.json`（`id` / `assembly` / `entryType` 三处一致）
3. `dotnet sln add <项目>.csproj`
4. 源码里实现 `WorstClassEver.ModAPI.IMod`，入口方法 `OnLoad(ModContext)`

## 注意事项

- **联机 Mod（两个都是）**：`mod.json` 标记 `affectsNetworkMessages: true`，所有联机玩家都需要安装，否则无法加入房间
- **版权**：仓库不含任何音频文件，MP3 请自备并放入对应 `Music/` 目录
- 服务器逻辑跑在房主进程里（Mirror Host 模式），涉及服务端行为的改动需要房主安装对应 Mod 才生效
- `AGENTS.md` 记录了游戏引擎的坑（如第 0 帧清空 DontDestroyOnLoad 场景）与开发工作流，写补丁前建议先读
