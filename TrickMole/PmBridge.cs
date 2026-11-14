using System;
using System.Reflection;
using HarmonyLib;

namespace TrickMole
{
    /// <summary>
    /// 对 PlaygroundMusic Mod 的软依赖桥（纯反射，未装该 Mod 时全部安全跳过）。
    /// 复用其联机曲库协商数据：全员交集曲目数、交集第 N 首的 hash、按 hash 找本地文件。
    /// </summary>
    internal static class PmBridge
    {
        private static PropertyInfo hostTrackCountProp;   // internal static int HostTrackCount
        private static MethodInfo getHostTrackIndex;      // internal static int GetHostTrackIndex(int)
        private static FieldInfo catalogField;            // private static Track[] catalog
        private static FieldInfo trackHashField;          // Track.Hash (private string)
        private static MethodInfo findLocalMethod;        // private static string FindLocal(string)
        private static bool resolved;

        // 懒解析：Mod 加载顺序不保证，首次实际使用时（此时两个 Mod 必然都已加载）再解析
        private static void EnsureResolved()
        {
            if (resolved) return;
            resolved = true;
            try
            {
                Type syncType = AccessTools.TypeByName("PlaygroundMusic.MusicSync");
                if (syncType == null) return;
                hostTrackCountProp = AccessTools.Property(syncType, "HostTrackCount");
                getHostTrackIndex = AccessTools.Method(syncType, "GetHostTrackIndex");
                catalogField = AccessTools.Field(syncType, "catalog");
                findLocalMethod = AccessTools.Method(syncType, "FindLocal");
                Type trackType = AccessTools.TypeByName("PlaygroundMusic.MusicSync+Track");
                if (trackType != null) trackHashField = AccessTools.Field(trackType, "Hash");
            }
            catch { /* 软依赖：解析失败按未安装处理 */ }
        }

        // PlaygroundMusic 已安装且联机交集就绪时返回共有扩展曲目数，否则 0
        internal static int SharedTrackCount
        {
            get
            {
                EnsureResolved();
                try { return hostTrackCountProp != null ? (int)hostTrackCountProp.GetValue(null) : 0; }
                catch { return 0; }
            }
        }

        // 交集第 position 首（0 起）的 SHA-256；无效返回 null
        internal static string GetSharedHash(int position)
        {
            EnsureResolved();
            try
            {
                if (getHostTrackIndex == null || catalogField == null || trackHashField == null) return null;
                int catalogIndex = (int)getHostTrackIndex.Invoke(null, new object[] { position });
                if (catalogIndex < 0) return null;
                object track = ((Array)catalogField.GetValue(null)).GetValue(catalogIndex);
                return track == null ? null : (string)trackHashField.GetValue(track);
            }
            catch { return null; }
        }

        // 按 hash 找本机文件（内部含文件未变更检查）；缺歌返回 null
        internal static string FindLocalByHash(string hash)
        {
            EnsureResolved();
            try { return findLocalMethod != null ? findLocalMethod.Invoke(null, new object[] { hash }) as string : null; }
            catch { return null; }
        }
    }
}
