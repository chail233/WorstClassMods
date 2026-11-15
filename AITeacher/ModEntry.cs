using System;
using HarmonyLib;
using UnityEngine;
using WorstClassEver.ModAPI;

namespace AITeacher
{
    /// <summary>
    /// AI 老师：电脑控制的老师补足老师位。
    /// 服务端（房主）生成 AI 老师 unit（复用 UnitBorn/pose/FSM/技能的原生广播链，其他玩家无需装 Mod），
    /// TeacherAIComponent 在房主本地驱动巡逻/追击/攻击/抓捕/挂柱。
    /// </summary>
    public sealed class ModEntry : IMod
    {
        internal static Action<string> Log;
        internal static AiTeacherConfig Config;
        internal static string ConfigDirectory;
        private const string HarmonyId = "worstclass.aiteacher";

        public void OnLoad(ModContext context)
        {
            Log = context.Log;
            ConfigDirectory = context.ConfigDirectory;
            Config = AiTeacherConfig.Load(context.ConfigDirectory);

            var harmony = new Harmony(HarmonyId);
            try
            {
                AiTeacherPatches.Apply(harmony);
            }
            catch (Exception ex)
            {
                Log("[错误] Harmony 补丁注册失败：" + ex);
                return;
            }

            var pump = new GameObject("AITeacherPump");
            UnityEngine.Object.DontDestroyOnLoad(pump);
            pump.AddComponent<AiTeacherPanel>();

            Log($"[状态] AI老师已加载：下局 AI 数量 {Config.defaultAiCount}，形态 {FormName(Config.teacherForm)}，配置目录 {context.ConfigDirectory}");
        }

        internal static string FormName(int teacherId)
        {
            switch (teacherId)
            {
                case 1001: return "粉笔老师";
                case 1002: return "剑老师";
                default: return "随机";
            }
        }
    }
}
