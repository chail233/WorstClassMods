using System;
using System.IO;
using UnityEngine;

namespace TeacherReward
{
    /// <summary>
    /// 配置：奖励面板热键。纯字段平面类直接走 JsonUtility（实测可靠）；
    /// 引擎坑：本机 JsonUtility 会静默跳过 List&lt;自定义类&gt; 字段，配置永不引入此类结构。
    /// </summary>
    [Serializable]
    internal sealed class RewardConfigData
    {
        public string panelKey = "H";

        // 技能卡数值
        public float swiftSpeedMultiplier = 1.15f; // 健步如飞：移速倍率
        public float chalkStunBonus = 0.30f;       // 重磅粉笔：晕眩时长加成（0.30 = +30%）
    }

    internal static class RewardConfig
    {
        internal static RewardConfigData Data = new RewardConfigData();
        internal static KeyCode PanelKeyCode = KeyCode.H;
        private static string s_Path;

        internal static void Load(string configDir, Action<string> log)
        {
            try
            {
                Directory.CreateDirectory(configDir);
                s_Path = Path.Combine(configDir, "reward.json");
                if (File.Exists(s_Path))
                {
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(s_Path), Data);
                }
                else
                {
                    Save();
                    log("[状态] 已生成默认配置：" + s_Path);
                }
            }
            catch (Exception e)
            {
                log("[警告] 配置读取失败，使用默认值：" + e.Message);
            }
            // 热键解析：忽略大小写；解析失败回落 H
            if (string.IsNullOrWhiteSpace(Data.panelKey)
                || !Enum.TryParse(Data.panelKey.Trim(), true, out PanelKeyCode)
                || PanelKeyCode == KeyCode.None)
            {
                PanelKeyCode = KeyCode.H;
            }
            // 卡片数值边界兜底
            Data.swiftSpeedMultiplier = Mathf.Clamp(Data.swiftSpeedMultiplier, 1f, 2f);
            Data.chalkStunBonus = Mathf.Clamp(Data.chalkStunBonus, 0f, 3f);
            log("[状态] 奖励面板热键：" + PanelKeyCode
                + "；健步如飞 ×" + Data.swiftSpeedMultiplier.ToString("0.##")
                + "；重磅粉笔 +" + Mathf.RoundToInt(Data.chalkStunBonus * 100f) + "%");
        }

        internal static void Save()
        {
            if (string.IsNullOrEmpty(s_Path)) return;
            File.WriteAllText(s_Path, JsonUtility.ToJson(Data, prettyPrint: true));
        }
    }
}
