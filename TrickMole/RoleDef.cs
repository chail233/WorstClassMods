using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TrickMole
{
    /// <summary>
    /// 角色定义：由 ConfigDirectory/trickmole.json 配置（JsonUtility 序列化）。
    /// 未配置时内置默认角色（混的入/书呆子/体育生）；服务端配置为权威，客机经 MoleSelectMessage.RoleJson 同步运行时副本。
    /// </summary>
    [Serializable]
    internal sealed class RoleDef
    {
        public string id = "mole";
        public string name = "混的入";
        public int count = 1;                             // 每局名额数（0 = 禁用该角色）
        public float trickMultiplier = 2f;                // 捣蛋计分加成倍数
        public bool canBroadcast = true;                  // 按 B 开关 3D 广播
        public bool canPush = true;                       // 背后按 E 推倒老师
        public float scoreGraceSeconds = 3f;              // 广播起播宽限（不计分）
        public float musicCooldownSeconds = 20f;          // 广播停止后的冷却
        public float pushStunSeconds = 2.5f;              // 推倒后老师倒地时长
        public float pushCooldownSeconds = 30f;           // 推倒冷却
        public float pushRange = 2.2f;                    // 推倒触发距离
        public float pushBehindDot = -0.35f;              // 背后判定点积阈值（≈110° 身后扇区）
        public float progressPenaltyFraction = 2f / 3f;   // 被挂柱时总体进度扣捣蛋累计的比例
        public float immunityIdleSeconds = 0f;            // >0 且 maxImmunityCharges>0：静心该时长获得一次"免老师攻击"
        public int maxImmunityCharges = 0;                // 免伤次数上限（0 = 无免伤能力）
        public float speedBoostUnseen = 1f;               // 老师视线外移速倍率（0 = 不生效；旧配置缺字段 JsonUtility 读回 0）
        public float speedPenaltySeen = 1f;               // 老师视线内移速倍率（0 = 不生效）
        public float actionSpeedMultiplier = 1f;          // 进食/救人速度倍率：对应时长按此缩短（0 = 不生效）
        public float sightAngleDegrees = 110f;            // 老师视线锥总角度（0 = 默认 110°）
        public float sightDistance = 10f;                 // 老师视线最大距离（0 = 默认 10m）
    }

    internal static class RoleConfig
    {
        internal static List<RoleDef> Roles = Defaults();
        private const string FileName = "trickmole.json";
        private static string s_ConfigPath;

        // 内置角色：配置文件里缺哪个就按默认补哪个（count=0 仍可禁用）
        private static List<RoleDef> Defaults()
        {
            return new List<RoleDef> { Mole(), Nerd(), Athlete() };
        }

        private static RoleDef Mole() => new RoleDef(); // 字段默认值即混的入

        private static RoleDef Nerd()
        {
            return new RoleDef
            {
                id = "nerd",
                name = "书呆子",
                count = 1,
                trickMultiplier = 0.5f,
                canBroadcast = false,
                canPush = false,
                immunityIdleSeconds = 20f,
                maxImmunityCharges = 1,
            };
        }

        private static RoleDef Athlete()
        {
            return new RoleDef
            {
                id = "athlete",
                name = "体育生",
                count = 1,
                trickMultiplier = 1f,
                canBroadcast = false,
                canPush = false,
                progressPenaltyFraction = 0f,
                speedBoostUnseen = 1.5f,
                speedPenaltySeen = 0.75f,
                actionSpeedMultiplier = 1.5f,
            };
        }

        // 读 ConfigDirectory/trickmole.json；不存在则生成默认，解析失败回退内置角色
        // 注意：本机引擎的 JsonUtility 会静默跳过 List<自定义类> 字段（实测 ToJson 出 {}、FromJson 出空表），
        // 所以这里的文件读写都是"整体手工拼 + 逐对象拆解"，只有单个 RoleDef（纯字段类）走 JsonUtility
        internal static void Load(string configDir, Action<string> log)
        {
            try
            {
                Directory.CreateDirectory(configDir);
                string path = Path.Combine(configDir, FileName);
                s_ConfigPath = path;
                if (File.Exists(path))
                {
                    List<RoleDef> parsed = SplitRoleObjects(File.ReadAllText(path));
                    if (parsed.Count > 0)
                    {
                        Roles = parsed;
                        if (EnsureBuiltIns(log)) File.WriteAllText(path, WriteRolesJson(Roles));
                        log("[状态] 角色配置已加载：" + Roles.Count + " 种角色（" + string.Join("、", Roles.ConvertAll(r => r.name)) + "）");
                        return;
                    }
                    log("角色配置解析为空，按内置默认重写");
                }
                else
                {
                    Roles = Defaults();
                    log("[状态] 已生成默认角色配置: " + path);
                }
                File.WriteAllText(path, WriteRolesJson(Roles));
            }
            catch (Exception e) { log?.Invoke("角色配置加载失败，使用内置默认：" + e.Message); }
        }

        // 面板编辑后写回磁盘（每台机器各自的配置；联机角色分配以房主配置为准）
        internal static void Save(Action<string> log)
        {
            if (s_ConfigPath == null) return;
            try { File.WriteAllText(s_ConfigPath, WriteRolesJson(Roles)); }
            catch (Exception e) { log?.Invoke("角色配置保存失败：" + e.Message); }
        }

        internal static void ResetToDefaults(Action<string> log)
        {
            Roles = Defaults();
            Save(log);
            log?.Invoke("[状态] 角色配置已恢复内置默认");
        }

        // 手工拼 {"roles":[...]}：文件里仍是人类可读的单个 JSON 对象，但组装不经过会被跳过 List 字段的序列化器
        private static string WriteRolesJson(List<RoleDef> roles)
        {
            var sb = new System.Text.StringBuilder(256 + roles.Count * 512);
            sb.Append("{\n  \"roles\": [");
            for (int i = 0; i < roles.Count; i++)
            {
                sb.Append(i == 0 ? "\n" : ",\n").Append("    ").Append(JsonUtility.ToJson(roles[i]));
            }
            sb.Append("\n  ]\n}");
            return sb.ToString();
        }

        // 从 {"roles":[...]} 里按括号配对拆出每个角色对象（字符串字面量里的大括号/引号已跳过），逐个走纯字段解析
        private static List<RoleDef> SplitRoleObjects(string json)
        {
            var roles = new List<RoleDef>();
            if (string.IsNullOrEmpty(json)) return roles;
            int arrayStart = json.IndexOf('[');
            int arrayEnd = json.LastIndexOf(']');
            if (arrayStart < 0 || arrayEnd <= arrayStart) return roles;
            bool inString = false;
            int depth = 0;
            int objStart = -1;
            for (int i = arrayStart + 1; i < arrayEnd; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (c == '\\') i++;           // 跳过转义对
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') { if (depth == 0) objStart = i; depth++; }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        try
                        {
                            RoleDef role = JsonUtility.FromJson<RoleDef>(json.Substring(objStart, i - objStart + 1));
                            if (role != null) roles.Add(role);
                        }
                        catch { } // 单个坏对象不拖累其余角色
                        objStart = -1;
                    }
                }
            }
            return roles;
        }

        // 旧配置（只有混的入）自动补上内置角色，并写回文件让玩家看得见可调项
        private static bool EnsureBuiltIns(Action<string> log)
        {
            bool added = false;
            foreach (RoleDef builtin in Defaults())
            {
                if (Roles.Exists(r => r != null && r.id == builtin.id)) continue;
                Roles.Add(builtin);
                added = true;
                log("[状态] 配置缺少内置角色「" + builtin.name + "」，已按默认补充（count=" + builtin.count + "，可设为 0 禁用）");
            }
            return added;
        }

        // 展开本局全部名额（每个角色 count 个，count<=0 表示该角色本局不出现）
        internal static List<RoleDef> ExpandSlots()
        {
            var slots = new List<RoleDef>();
            foreach (RoleDef role in Roles)
                for (int i = 0; i < Math.Max(0, role.count); i++) slots.Add(role);
            return slots;
        }
    }
}
