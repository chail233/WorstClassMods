# 创意工坊发布脚手架

每个 Mod 是一个**独立创意工坊物品**（独立 `item.vdf` + 独立 content 暂存目录），一次命令只动一个 Mod。

## 前提

- 桌面 Steam 客户端已启动并登录（上传器直接复用其会话，**不输入账号密码**，无需 SteamCMD）
- 当前账号拥有 Worst Class Ever（AppID 5175310）
- 该账号首次向本游戏上传时，Steam 可能要求先到工坊物品页面接受一次法律协议

## 目录结构

```
Workshop/
├── upload.ps1              # 一键发布脚本（同步 content + 校验 + 调用上传器）
├── Uploader/               # 自建上传器（引用游戏自带 Steamworks.NET，net8 控制台）
├── PlaygroundMusic/
│   ├── item.vdf            # 工坊物品元数据（标题/描述/可见性，发布的权威源）
│   └── preview.png         # 预览图（jpg/png ≤1MB 建议 800x600，当前是占位图，发布前请替换）
├── TrickMole/              # 同上
├── AITeacher/              # 同上
├── .content/<Mod>/         # 构建产物暂存（脚本自动 robocopy /MIR 镜像，已 gitignore）
└── steamcmd/               # SteamCMD 备用环境（当前网络 bootstrap 被劫持不可用，见文末）
```

## 首次发布

```powershell
powershell -ExecutionPolicy Bypass -File Workshop\upload.ps1 PlaygroundMusic -Build -ChangeNote "首次发布"
```

脚本流程：构建 → 从游戏 `Mods\<Mod>\` 镜像同步到 `.content\<Mod>\` → 校验预览图 → 调上传器创建物品 → **自动把返回的 PublishedFileId 回填进 item.vdf**。之后同一命令自动变为更新。

发布后到物品页确认可见性（默认 `visibility "0"` 公开；想先自查改 `2`）与描述排版。

## 更新

```powershell
powershell -ExecutionPolicy Bypass -File Workshop\upload.ps1 TrickMole -Build -ChangeNote "v2.0.1 修复 xxx"
```

- 改版本号/描述 → 手动同步改各 Mod 的 `mod.json`（与 vdf 描述保持一致）
- 标题/描述/可见性以 `item.vdf` 为权威源：**每次上传都会覆盖网页上的手动编辑**，要改就改 vdf
- vdf 描述支持 BBCode（`[b]` `[list][*]` 等），字面 `\n` 会转成换行；值内不要用英文双引号

## 注意

- `Music\` 里的音频会随工坊包上传，注意版权（git 不跟踪 mp3，但上传不受影响）
- 联机类型 Mod（`affectsNetworkMessages: true`）在描述里已注明"所有玩家需安装"；AITeacher 标注"仅房主安装"
- 上传进度/结果看脚本控制台输出；失败按提示的 EResult 处理（AccessDenied → 接受法律协议或账号权限）
- 游戏内 Mod 面板会自动识别订阅的工坊 Mod（工坊包格式与本地部署完全同构）

## SteamCMD 备用路径（当前不可用）

`steamcmd/` 已安装但 bootstrap 自更新被当前网络劫持（明文 HTTP 拿 500，旧引导器不支持 HTTPS）。若日后网络可用（挂代理），可改用 `steamcmd +login <账号> +workshop_build_item Workshop\<Mod>\item.vdf +quit`——vdf 格式与上传器完全一致，两种工具可互相替换。
