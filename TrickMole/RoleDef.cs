using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TrickMole
{
    /// <summary>
    /// 角色定义：由 ConfigDirectory/trickmole.json 配置（JsonUtility 序列化）。
    /// 未配置时内置默认角色"混的入"；服务端配置为权威，客机经 MoleSelectMessage.RoleJson 同步运行时副本。
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
    }

    [Serializable]
    internal sealed class RoleConfigFile
    {
        public List<RoleDef> roles = new List<RoleDef>();
    }

    internal static class RoleConfig
    {
        internal static List<RoleDef> Roles = Defaults();
        private const string FileName = "trickmole.json";

        // 内置角色：配置文件里缺哪个就按默认补哪个（count=0 仍可禁用）
        private static List<RoleDef> Defaults()
        {
            return new List<RoleDef> { Mole(), Nerd() };
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

        // 读 ConfigDirectory/trickmole.json；不存在则生成默认，解析失败回退内置角色
        internal static void Load(string configDir, Action<string> log)
        {
            try
            {
                Directory.CreateDirectory(configDir);
                string path = Path.Combine(configDir, FileName);
                if (File.Exists(path))
                {
                    RoleConfigFile parsed = JsonUtility.FromJson<RoleConfigFile>(File.ReadAllText(path));
                    if (parsed?.roles != null && parsed.roles.Count > 0)
                    {
                        Roles = parsed.roles;
                        if (EnsureBuiltIns(log)) File.WriteAllText(path, JsonUtility.ToJson(new RoleConfigFile { roles = Roles }, true));
                        log("[状态] 角色配置已加载：" + Roles.Count + " 种角色（" + string.Join("、", Roles.ConvertAll(r => r.name)) + "）");
                        return;
                    }
                    log("角色配置解析为空，使用内置默认角色");
                }
                else
                {
                    Roles = Defaults();
                    File.WriteAllText(path, JsonUtility.ToJson(new RoleConfigFile { roles = Roles }, true));
                    log("[状态] 已生成默认角色配置: " + path);
                }
            }
            catch (Exception e) { log?.Invoke("角色配置加载失败，使用内置默认：" + e.Message); }
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
