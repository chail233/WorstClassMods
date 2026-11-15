using System;
using System.IO;
using UnityEngine;

namespace AITeacher
{
    /// <summary>
    /// AI 老师配置：Configs/aiteacher/aiteacher.json（首次运行生成默认）。
    /// 面板改动（AI 数量/形态/难度）会写回此文件，跨局记忆。
    /// </summary>
    [Serializable]
    public sealed class AiTeacherConfig
    {
        // ===== 面板可改（写回 JSON） =====
        public int difficulty = 1;       // 难度档：0宽松 1标准 2严格 3地狱（应用预设值；手改字段后仍以此显示档名）
        public int defaultAiCount = 1;   // 下局按铃要补的 AI 老师数量（0 = 关闭，走原版分配）
        public int teacherForm = 0;      // 老师 ID：0=每局随机 1001/1002，1001=粉笔老师，1002=剑老师

        // ===== 行为参数 =====
        public float moveSpeed = 0f;           // 巡逻移速：0 = 跟随房间规则 TeacherSpeed（与真人老师同速）
        public float chaseSpeedBoost = 1.25f;  // 追击/抓捕冲刺速度倍率
        public float visionDistance = 9f;      // 视野距离（米）
        public float visionAngleDeg = 120f;    // 视野角度（度，以面朝方向为中轴）
        public float hearingDistance = 14f;    // 听力距离：捣蛋计分事件在此范围内会被"听见"
        public float suspicionMemory = 4f;     // 捣蛋事件的记忆时长（秒），过期后不再追踪
        public float reactionDelay = 0.5f;     // 决定追击/攻击前的反应延迟（秒）
        public float attackCooldown = 1.5f;    // 攻击间隔下限（秒，粉笔与剑通用；剑另有房规 CD）
        public float catchMinEnergy = 15f;     // 体力低于此值不再攻击（保底抓捕）
        public float energyRegenPerSecond = 2f;// AI 体力回复速率（AI 无原版被动消耗，仅攻击扣）
        public float patrolInterval = 4f;      // 巡逻换点间隔（秒）
        public float patrolRadius = 6f;        // 巡逻点在当前位置周围的半径（米）
        public float chalkSpreadDeg = 4f;      // 粉笔散布角度（度，0=百发百中）
        public float chalkLeadFactor = 0.85f;  // 粉笔瞄准提前量系数（按目标速度预判落点，0=关闭）
        public bool observeTrickItems = true;  // 视野内手持捣蛋物品/玩电脑 = 违规，立即追
        public bool hotspotPatrol = true;      // 巡逻偏向历史捣蛋热点
        public bool cooperate = true;          // 多 AI 协同：目标认领去重，不挤同一个学生

        private const string FileName = "aiteacher.json";

        // ===== 难度预设 =====

        private static readonly string[] PresetNames = { "宽松", "标准", "严格", "地狱" };

        public static string PresetName(int level)
        {
            return (level >= 0 && level < PresetNames.Length) ? PresetNames[level] : "自定义";
        }

        /// <summary>应用难度预设到行为字段（不动数量/形态/体力规则）</summary>
        public void ApplyDifficulty(int level)
        {
            difficulty = Mathf.Clamp(level, 0, PresetNames.Length - 1);
            switch (difficulty)
            {
                case 0: // 宽松
                    visionDistance = 7f; visionAngleDeg = 100f; hearingDistance = 10f;
                    suspicionMemory = 3f; reactionDelay = 0.9f; chaseSpeedBoost = 1.1f; chalkSpreadDeg = 6f;
                    chalkLeadFactor = 0.5f; break;
                case 1: // 标准
                    visionDistance = 9f; visionAngleDeg = 120f; hearingDistance = 14f;
                    suspicionMemory = 4f; reactionDelay = 0.5f; chaseSpeedBoost = 1.25f; chalkSpreadDeg = 4f;
                    chalkLeadFactor = 0.85f; break;
                case 2: // 严格
                    visionDistance = 12f; visionAngleDeg = 150f; hearingDistance = 20f;
                    suspicionMemory = 6f; reactionDelay = 0.3f; chaseSpeedBoost = 1.4f; chalkSpreadDeg = 2.5f;
                    chalkLeadFactor = 1f; break;
                case 3: // 地狱：听力全图=雷达老师
                    visionDistance = 16f; visionAngleDeg = 170f; hearingDistance = 999f;
                    suspicionMemory = 8f; reactionDelay = 0.15f; chaseSpeedBoost = 1.55f; chalkSpreadDeg = 1.5f;
                    chalkLeadFactor = 1.1f; break;
            }
        }

        // ===== 存取 =====

        public static AiTeacherConfig Load(string configDirectory)
        {
            var config = new AiTeacherConfig();
            try
            {
                string path = Path.Combine(configDirectory, FileName);
                if (File.Exists(path))
                {
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(path), config);
                }
                File.WriteAllText(path, JsonUtility.ToJson(config, true));
            }
            catch (Exception ex)
            {
                ModEntry.Log?.Invoke("[警告] 读取 aiteacher.json 失败，使用默认配置：" + ex.Message);
            }
            return config;
        }

        public void Save(string configDirectory)
        {
            try
            {
                string path = Path.Combine(configDirectory, FileName);
                File.WriteAllText(path, JsonUtility.ToJson(this, true));
            }
            catch (Exception ex)
            {
                ModEntry.Log?.Invoke("[警告] 写回 aiteacher.json 失败：" + ex.Message);
            }
        }

        /// <summary>本局实际使用的老师 ID：0/其他 → 随机</summary>
        public int RollTeacherId()
        {
            if (teacherForm == 1001 || teacherForm == 1002) return teacherForm;
            return UnityEngine.Random.Range(0, 2) == 0 ? 1001 : 1002;
        }
    }
}
