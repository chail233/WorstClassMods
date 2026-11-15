// WorstClassMods 创意工坊上传器
// 直接读取 upload.ps1 同一套 item.vdf（appid/content/title/description/visibility/previewfile/changenote），
// 通过游戏自带的 Steamworks.NET 复用桌面 Steam 客户端会话创建/更新创意工坊物品。
// 首次发布（publishedfileid 为空）成功后自动把物品 ID 回填进 vdf，之后同一命令即变成更新。

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Steamworks;

namespace WorkshopUploader;

internal static class Program
{
    private const uint AppId = 5175310; // Worst Class Ever

    private static readonly Dictionary<string, string> Vdf = new(StringComparer.Ordinal);

    private static string _vdfPath;
    private static volatile bool _callDone;
    private static volatile bool _ioFailure;

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* 个别控制台不支持，忽略 */ }
        try
        {
            return Run(args);
        }
        catch (ModUploaderException ex)
        {
            Console.Error.WriteLine("[错误] " + ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[错误] 未预期异常：" + ex);
            return 3;
        }
        finally
        {
            try { SteamAPI.Shutdown(); } catch { /* 未初始化时忽略 */ }
        }
    }

    private sealed class ModUploaderException : Exception
    {
        public ModUploaderException(string message) : base(message) { }
    }

    private static int Run(string[] args)
    {
        string vdfPath = null;
        string changeNoteOverride = null;
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--vdf": vdfPath = args[++i]; break;
                case "--change-note": changeNoteOverride = args[++i]; break;
                case "--dry-run": dryRun = true; break;
                default: throw new ModUploaderException("未知参数：" + args[i] + "（支持 --vdf / --change-note / --dry-run）");
            }
        }

        if (string.IsNullOrEmpty(vdfPath))
            throw new ModUploaderException("用法：WorkshopUploader --vdf <item.vdf> [--change-note <说明>] [--dry-run]");

        _vdfPath = Path.GetFullPath(vdfPath);
        if (!File.Exists(_vdfPath))
            throw new ModUploaderException("找不到 vdf：" + _vdfPath);

        ParseVdf(_vdfPath);

        if (!Vdf.TryGetValue("appid", out var appidStr) || appidStr.Trim() != AppId.ToString(CultureInfo.InvariantCulture))
            throw new ModUploaderException("vdf 的 appid 必须是 " + AppId + "（Worst Class Ever），实际：" + appidStr);

        var content = Path.GetFullPath(RequireKey("content"));
        var modJsonPath = Path.Combine(content, "mod.json");
        if (!File.Exists(modJsonPath))
            throw new ModUploaderException("content 目录缺少 mod.json（content 应为 mod 包根目录）：" + content);

        var title = Unescape(Vdf.TryGetValue("title", out var t) ? t : "");
        var description = Unescape(Vdf.TryGetValue("description", out var d) ? d : "");
        var preview = Vdf.TryGetValue("previewfile", out var p) && p.Trim().Length > 0 ? Path.GetFullPath(p.Trim()) : null;
        var changeNote = changeNoteOverride ?? (Vdf.TryGetValue("changenote", out var cn) ? Unescape(cn) : "");
        var visibility = ParseVisibility(Vdf.TryGetValue("visibility", out var vis) ? vis.Trim() : "0");

        if (preview != null && !File.Exists(preview))
            throw new ModUploaderException("预览图不存在：" + preview + "（jpg/png，≤1MB，建议 800x600）");

        var idStr = (Vdf.TryGetValue("publishedfileid", out var id) ? id.Trim() : "");
        var hasFileId = ulong.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fileId) && fileId != 0;

        Console.WriteLine("[状态] Mod 包：" + modJsonPath);
        Console.WriteLine("[状态] 模式：" + (hasFileId ? "更新物品 " + fileId : "首次发布（将创建新物品）"));
        Console.WriteLine("[状态] 标题：" + title);
        Console.WriteLine("[状态] 可见性：" + (visibility == ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic ? "公开" : visibility));

        if (dryRun)
        {
            Console.WriteLine("[状态] dry-run 校验通过，未连接 Steam，未上传。");
            return 0;
        }

        // steam_appid.txt 必须与 exe 同目录，SteamAPI 才能以 5175310 身份挂上桌面会话
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        EnsureAppIdFile();

        Console.WriteLine("[状态] 连接桌面 Steam 客户端……");
        if (!SteamAPI.Init())
            throw new ModUploaderException("SteamAPI 初始化失败。请确认：桌面 Steam 客户端已启动并登录；当前账号拥有 Worst Class Ever。");
        if (!SteamUser.BLoggedOn())
            throw new ModUploaderException("Steam 未登录（离线模式），创意工坊上传需要在线账号。");

        if (!hasFileId)
        {
            Console.WriteLine("[创建] 调用 CreateItem ……");
            var createResult = WaitFor<CreateItemResult_t>(SteamUGC.CreateItem(new AppId_t(AppId), EWorkshopFileType.k_EWorkshopFileTypeCommunity));
            if (createResult == null)
                throw new ModUploaderException("CreateItem 网络 IO 失败，请重试。");
            if (createResult.Value.m_eResult != EResult.k_EResultOK)
                throw new ModUploaderException("创建物品失败：" + createResult.Value.m_eResult + HintFor(createResult.Value.m_eResult));

            fileId = createResult.Value.m_nPublishedFileId.m_PublishedFileId;
            PatchVdfKey("publishedfileid", fileId.ToString(CultureInfo.InvariantCulture));
            Console.WriteLine("[状态] 物品已创建，PublishedFileId=" + fileId + "（已回填 " + Path.GetFileName(_vdfPath) + "，之后同一命令即变为更新）");
            if (createResult.Value.m_bUserNeedsToAcceptWorkshopLegalAgreement)
                Console.WriteLine("[提示] 该账号首次向此游戏上传，可能需先到工坊物品页面接受法律协议，否则稍后提交会报 AccessDenied。");
        }

        Console.WriteLine("[状态] 准备更新内容包 ……");
        var handle = SteamUGC.StartItemUpdate(new AppId_t(AppId), new PublishedFileId_t(fileId));
        if (handle.m_UGCUpdateHandle == 0)
            throw new ModUploaderException("StartItemUpdate 返回空句柄。");

        if (!SteamUGC.SetItemContent(handle, content))
            throw new ModUploaderException("SetItemContent 失败：" + content);
        if (title.Length > 0 && !SteamUGC.SetItemTitle(handle, title))
            throw new ModUploaderException("SetItemTitle 失败。");
        if (description.Length > 0 && !SteamUGC.SetItemDescription(handle, description))
            throw new ModUploaderException("SetItemDescription 失败。");
        if (preview != null && !SteamUGC.SetItemPreview(handle, preview))
            throw new ModUploaderException("SetItemPreview 失败：" + preview);
        SteamUGC.SetItemVisibility(handle, visibility);
        SteamUGC.SetItemUpdateLanguage(handle, "schinese");

        Console.WriteLine("[上传] 提交中（changenote：" + (changeNote.Length > 0 ? changeNote : "(无)") + "）");
        var submitResult = SubmitAndWait(handle, changeNote);

        if (submitResult == null)
            throw new ModUploaderException("SubmitItemUpdate 网络 IO 失败，内容可能未完整上传，请重试。");
        if (submitResult.Value.m_eResult != EResult.k_EResultOK)
            throw new ModUploaderException("上传失败：" + submitResult.Value.m_eResult + HintFor(submitResult.Value.m_eResult));

        Console.WriteLine();
        Console.WriteLine("[完成] 上传成功！");
        Console.WriteLine("物品页：https://steamcommunity.com/sharedfiles/filedetails/?id=" + fileId);
        if (submitResult.Value.m_bUserNeedsToAcceptWorkshopLegalAgreement)
            Console.WriteLine("[提示] 需先到物品页接受法律协议后物品才会正常显示。");
        Console.WriteLine("[提示] 请到物品页确认可见性与描述排版；item.vdf 里的 title/description 每次上传都会覆盖网页上的手动编辑。");
        return 0;
    }

    // ---------- vdf ----------

    private static void ParseVdf(string path)
    {
        foreach (var line in File.ReadAllLines(path))
        {
            var m = Regex.Match(line, @"^\s*""([^""]+)""\s+""(.*)""\s*$");
            if (m.Success)
                Vdf[m.Groups[1].Value] = m.Groups[2].Value;
        }
        if (Vdf.Count == 0)
            throw new ModUploaderException("vdf 解析结果为空（键值行需写成 \"key\" \"value\" 格式）：" + path);
    }

    private static string RequireKey(string key)
    {
        if (!Vdf.TryGetValue(key, out var value) || value.Trim().Length == 0)
            throw new ModUploaderException("vdf 缺少必需键：" + key);
        return value.Trim();
    }

    // vdf 单行值里的字面 \n 转成真换行（描述排版用）；注意路径里不要出现 \n 字面序列
    private static string Unescape(string s) => s.Replace("\\n", "\n");

    private static ERemoteStoragePublishedFileVisibility ParseVisibility(string s)
    {
        switch (s)
        {
            case "0": return ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPublic;
            case "1": return ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityFriendsOnly;
            case "2": return ERemoteStoragePublishedFileVisibility.k_ERemoteStoragePublishedFileVisibilityPrivate;
            default:
                throw new ModUploaderException("visibility 只支持 0=公开 / 1=仅好友 / 2=私密，实际：" + s);
        }
    }

    private static void PatchVdfKey(string key, string value)
    {
        var text = File.ReadAllText(_vdfPath);
        var pattern = @"(?m)^(\s*""" + key + @"""\s+"")[^""]*("")";
        // ${1}/${2} 显式定界：值以数字开头时 "$1"+数字 会被解析成超长捕获组引用
        var replaced = Regex.Replace(text, pattern, "${1}" + value + "${2}");
        if (replaced == text)
            throw new ModUploaderException("vdf 回填失败：找不到键 \"" + key + "\"：" + _vdfPath);
        File.WriteAllText(_vdfPath, replaced, new UTF8Encoding(false));
    }

    // ---------- Steam 调用等待 ----------

    private static T? WaitFor<T>(SteamAPICall_t call) where T : struct
    {
        T result = default;
        _callDone = false;
        _ioFailure = false;
        var callResult = CallResult<T>.Create();
        callResult.Set(call, (r, ioFail) => { result = r; _ioFailure = ioFail; _callDone = true; });
        while (!_callDone)
        {
            SteamAPI.RunCallbacks();
            Thread.Sleep(100);
        }
        callResult.Dispose();
        return _ioFailure ? null : (T?)result;
    }

    private static SubmitItemUpdateResult_t? SubmitAndWait(UGCUpdateHandle_t handle, string changeNote)
    {
        SubmitItemUpdateResult_t result = default;
        _callDone = false;
        _ioFailure = false;
        var callResult = CallResult<SubmitItemUpdateResult_t>.Create();
        callResult.Set(SteamUGC.SubmitItemUpdate(handle, changeNote), (r, ioFail) => { result = r; _ioFailure = ioFail; _callDone = true; });

        var lastLine = "";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!_callDone)
        {
            SteamAPI.RunCallbacks();
            if (sw.ElapsedMilliseconds % 500 < 120)
            {
                var status = SteamUGC.GetItemUpdateProgress(handle, out var processed, out var total);
                var line = "\r[上传] " + status.ToString().Replace("k_EItemUpdateStatus", "");
                if (total > 0)
                    line += string.Format(CultureInfo.InvariantCulture, " {0:0.0}/{1:0.1} MB ({2:0}%)",
                        processed / 1048576.0, total / 1048576.0, 100.0 * processed / total);
                line = line.PadRight(lastLine.Length > line.Length ? lastLine.Length : line.Length);
                lastLine = line;
                Console.Write(line);
            }
            if (sw.Elapsed.TotalMinutes >= 40)
                throw new ModUploaderException("上传超时（40 分钟），Steam 服务可能异常，请重试。");
            Thread.Sleep(100);
        }
        callResult.Dispose();
        Console.WriteLine();
        return _ioFailure ? null : (SubmitItemUpdateResult_t?)result;
    }

    private static void EnsureAppIdFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
        if (!File.Exists(path))
            File.WriteAllText(path, AppId.ToString(CultureInfo.InvariantCulture));
    }

    private static string HintFor(EResult result)
    {
        switch (result)
        {
            case EResult.k_EResultAccessDenied:
                return "（无权限：确认账号拥有本游戏且未受限；首次上传需先到工坊页面接受法律协议）";
            case EResult.k_EResultRateLimitExceeded:
                return "（上传过于频繁，稍等几分钟再试）";
            case EResult.k_EResultLimitExceeded:
                return "（账号创意工坊物品数达上限）";
            case EResult.k_EResultInsufficientPrivilege:
                return "（账号受限：社区功能被封禁或家庭监护限制）";
            case EResult.k_EResultTimeout:
            case EResult.k_EResultServiceUnavailable:
                return "（Steam 服务波动，稍后重试）";
            case EResult.k_EResultInvalidParam:
                return "（参数无效：常见于 content 路径不存在或预览图格式不符）";
            default:
                return "";
        }
    }
}
