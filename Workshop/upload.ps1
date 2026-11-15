# WorstClassMods 创意工坊发布脚手架
# 每个 Mod 是一个独立创意工坊物品：独立 content 暂存目录 + 独立 item.vdf，互不掺和。
# 用法（在仓库根或任意目录）：
#   powershell -ExecutionPolicy Bypass -File Workshop\upload.ps1 PlaygroundMusic -Build -ChangeNote "v1.0.1 修复xxx"
#   powershell -ExecutionPolicy Bypass -File Workshop\upload.ps1 TrickMole          # 只同步不上传
# 参数：
#   -Mod          必填：Mod 文件夹名（PlaygroundMusic / TrickMole / AITeacher，一个 Mod 一条命令）
#   -Build        上传前先 dotnet build 该 Mod（部署到游戏 Mods 目录后再同步）
#   -ChangeNote   本次更新说明（写进 vdf 的 changenote；首次发布可省略）
#   -SkipUpload   只同步 content 并校验，不连接 Steam 不上传
# 前提：桌面 Steam 客户端已启动并登录，当前账号拥有 Worst Class Ever。

param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Mod,
    [string]$ChangeNote = "",
    [switch]$Build,
    [switch]$SkipUpload
)

$ErrorActionPreference = 'Stop'

# 防呆：只允许单段文件夹名，避免把多个 Mod 或路径混进来
if ($Mod -match '[\\/]') { throw "-Mod 只能是单个 Mod 文件夹名（如 PlaygroundMusic），不能带路径" }

$repoRoot    = Split-Path -Parent $PSScriptRoot
$workshopDir = Join-Path $repoRoot 'Workshop'
$modRoot     = Join-Path $workshopDir $Mod

# 游戏路径与 Directory.Build.props 保持同步，不写死
$props = Get-Content -Raw -Encoding UTF8 (Join-Path $repoRoot 'Directory.Build.props')
if ($props -notmatch '<GameDir>(.*?)</GameDir>') { throw '未能从 Directory.Build.props 解析 GameDir' }
$gameDir = $Matches[1]

$modSource = Join-Path $gameDir "Mods\$Mod"
if (-not (Test-Path (Join-Path $modSource 'mod.json'))) {
    $known = (Get-ChildItem (Join-Path $gameDir 'Mods') -Directory -ErrorAction SilentlyContinue |
              Select-Object -ExpandProperty Name) -join ', '
    throw "找不到已部署的 Mod：$modSource（mod.json 不存在）。先 dotnet build，或检查 -Mod 拼写。已部署：$known"
}
$modJson = Get-Content -Raw -Encoding UTF8 (Join-Path $modSource 'mod.json') | ConvertFrom-Json
Write-Host ("[状态] 目标 Mod：{0}（{1} v{2}）" -f $Mod, $modJson.name, $modJson.version)

# 1. 可选：先构建（构建后自动部署到游戏 Mods 目录）
if ($Build) {
    Write-Host "[状态] 构建 $Mod ……"
    dotnet build (Join-Path $repoRoot "$Mod\$Mod.csproj") -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败（退出码 $LASTEXITCODE）" }
}

# 2. 同步 content：每个 Mod 独立暂存目录，/MIR 镜像保证与部署产物完全一致
$contentDir = Join-Path $workshopDir ".content\$Mod"
if (Test-Path $contentDir) { Remove-Item -Recurse -Force $contentDir }
robocopy $modSource $contentDir /MIR /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy 同步失败（退出码 $LASTEXITCODE）" }
Write-Host "[状态] content 已同步：$modSource -> $contentDir"

# 3. item.vdf：不存在则按 mod.json 生成骨架（已有则只更新动态字段，title/description 以 vdf 为准）
$vdfPath = Join-Path $modRoot 'item.vdf'
if (-not (Test-Path $vdfPath)) {
    if (-not (Test-Path $modRoot)) { New-Item -ItemType Directory -Path $modRoot | Out-Null }
    $desc = ($modJson.description -replace "`r?`n", "\n")
    $vdf = @"
"workshopitem"
{
    "appid"           "5175310"
    "publishedfileid" ""
    "content"         "$contentDir"
    "title"           "$($modJson.name)"
    "description"     "$desc"
    "visibility"      "0"
    "previewfile"     "$(Join-Path $modRoot 'preview.png')"
    "changenote"      "更新"
}
"@
    [IO.File]::WriteAllText($vdfPath, $vdf, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "[状态] 已生成骨架 $vdfPath（publishedfileid 为空 = 首次发布将创建新物品）"
}

function Set-VdfKey([string]$Path, [string]$Key, [string]$Value) {
    $text = Get-Content -Raw -Encoding UTF8 $Path
    $pattern = '(?m)^(\s*"' + $Key + '"\s+")[^"]*(")'
    if (-not [Regex]::IsMatch($text, $pattern)) { throw "vdf 里找不到键 '$Key'：$Path" }
    $new = [Regex]::Replace($text, $pattern, ('${1}' + $Value.Replace('$', '$$') + '${2}'))
    [IO.File]::WriteAllText($Path, $new, (New-Object System.Text.UTF8Encoding($false)))
}

# content 路径每次上传都按当前机器写回（换机器/换盘不用手改）
Set-VdfKey $vdfPath 'content' $contentDir

# 预览图：preview.png/jpg 三选一，首次发布必需
$preview = Get-ChildItem -Path $modRoot -File |
    Where-Object { $_.Name -match '^preview\.(jpg|jpeg|png)$' } |
    Sort-Object Name | Select-Object -First 1
if ($preview) {
    if ($preview.Length -gt 1MB) { Write-Warning "预览图 $($preview.Name) 超过 1MB，Steam 可能拒绝" }
    Set-VdfKey $vdfPath 'previewfile' $preview.FullName
} else {
    throw "缺少预览图：请放一张 $modRoot\preview.png（或 preview.jpg；jpg/png，≤1MB，建议 800x600）"
}

if ($ChangeNote -ne '') { Set-VdfKey $vdfPath 'changenote' $ChangeNote }

$existingId = ((Get-Content -Raw -Encoding UTF8 $vdfPath) | Select-String -Pattern '"publishedfileid"\s+"(\d+)"').Matches
if ($existingId.Count -gt 0 -and $existingId[0].Groups[1].Value -ne '') {
    Write-Host ("[状态] 工坊物品：已存在（PublishedFileId={0}），本次为更新" -f $existingId[0].Groups[1].Value)
} else {
    Write-Host "[状态] 工坊物品：首次发布（将创建新物品并自动回填 ID）"
}

# 4. 上传（或仅校验）
if ($SkipUpload) {
    Write-Host "[状态] SkipUpload：同步与校验完成，未连接 Steam。"
    exit 0
}

$upProj = Join-Path $workshopDir 'Uploader\WorkshopUploader.csproj'
$upExe  = Join-Path $workshopDir 'Uploader\bin\Release\net8.0\WorkshopUploader.exe'
if (-not (Test-Path $upExe)) {
    Write-Host '[状态] 编译上传器 ……'
    dotnet build $upProj -c Release -v q
    if ($LASTEXITCODE -ne 0) { throw '上传器编译失败' }
}

Write-Host '[状态] 开始上传（需要桌面 Steam 客户端已登录）……'
if ($ChangeNote -ne '') {
    & $upExe --vdf $vdfPath --change-note $ChangeNote
} else {
    & $upExe --vdf $vdfPath
}
exit $LASTEXITCODE
