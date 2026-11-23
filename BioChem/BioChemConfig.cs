using System;
using System.IO;
using UnityEngine;

namespace BioChem
{
    [Serializable]
    public class BioChemConfig
    {
        internal const int MaxInitialInfectedStudents = 50;

        public int arenaMap = -1;                 // -1 表示旧版文件，按 useQuarantineDepot 迁移
        public bool useQuarantineDepot;
        public int initialInfectedStudents = 1;  // 正式上课时随机感染的人数，0 = 关闭
        public float pillarInfectSeconds = 10f;   // 挂柱多久后被感染（CF 节奏：抓到很快滚雪球）
        public int teacherMaxHp = 90;             // 大老师 HP
        public int teacherHpPerWrite = 9;         // 大老师和小老师每次写完板书回复的 HP
        public int miniMaxHp = 36;                // 小老师 HP
        public float miniStunSeconds = 2.5f;      // 小老师教棍命中学生的击晕时长
        public float miniStunRange = 2.2f;        // 小老师教棍攻击距离
        public float miniMeleeInterval = 0.5f;    // 小老师教棍挥击间隔（攻速）
        public float miniMeleeAngle = 120f;       // 小老师教棍扇形总角度（面前群体判定，与猎刀同口径）
        public int dartDamage = 1;                // 吹箭伤害（基础值）
        public float dartDamageBonusPerInfected = 0.25f; // 每个已感染学生（小老师）附加的吹箭伤害系数：伤害=基础×(1+min(感染数×本值,3))；0=关闭递增
        public int dartMaxAmmo = 200;             // 吹箭备弹上限；耗尽后无法射击，新一局自动回满
        public int dartAmmoPerFood = 100;         // 每吃完一份食物补充的备弹数（火锅食材也计入）；0=吃东西不补弹
        public float fireInterval = 0.25f;        // 连发吹箭筒按住左键的射击间隔（下限 0.1s=最高每秒 10 发）
        public float dartSlowPerStack = 0.2f;     // 每层减速比例（1/5）
        public int dartSlowMaxStacks = 4;         // 减速最大层数（4/5）
        public float dartSlowSeconds = 0.5f;      // 每层减速持续时间
        public float downSeconds = 5f;            // HP 归零后晕倒时长，之后办公室复活
        public int hunterTriggerStudents = 2;     // 剩余学生数 ≤ 该值时解锁 G 变身老师猎手
        public int hunterHp = 150;                // 老师猎手 HP（CF 终结者：能被围殴一阵）
        public int hunterDamage = 40;             // 猎手近战伤害：一刀秒小老师(36)，大老师(90)三刀
        public float hunterAttackInterval = 0.4f; // 猎手近战攻击间隔（前摇短、攻速快）
        public int teacherAttackDamage = 2;       // 老师阵营攻击力：粉笔/剑击/小老师击晕命中猎手时扣的 HP

        // ===== 学生副武器手枪（按 2）：与 AK47 同管线（嘴叼 Taser 承载+虚拟弹体），弹匣节奏独立、无感染伤害加成 =====
        public int pistolDamage = 2;              // 手枪单发伤害
        public int pistolMagSize = 12;            // 手枪弹匣容量
        public int pistolMaxAmmo = 48;            // 手枪备弹上限；耗尽后无法射击，新一局自动回满
        public int pistolAmmoPerFood = 12;        // 吃东西随机命中手枪时补充的备弹数
        public float pistolFireInterval = 0.3f;   // 手枪按住左键的射击间隔（比 AK 稍慢）

        // ===== 学生近战尼泊尔军刀（按 3）：CF 军刀手感——轻击快、可长按连击，重击更远更疼；
        // 命中判定在挥刀动作末端（出手后 HitDelay 秒服务端才做扇形判定） =====
        public int kukriLightDamage = 6;          // 轻击伤害（左键单击/长按连击）
        public int kukriHeavyDamage = 15;         // 重击伤害（右键）
        public float kukriLightInterval = 0.4f;   // 轻击连击间隔
        public float kukriHeavyCooldown = 1.5f;   // 重击冷却（重击期间轻击也不可用）
        public float kukriLightRange = 2.2f;      // 轻击扇形半径
        public float kukriHeavyRange = 3.0f;      // 重击扇形半径（CF 重击距离更长）
        public float kukriLightHitDelay = 0.25f;  // 轻击命中延迟=挥刀末端
        public float kukriHeavyHitDelay = 0.55f;  // 重击命中延迟=挥刀末端

        // ===== 投掷物生化手雷（按 4）：每人最多 1 颗，扔出去引信到点爆炸，范围伤害老师阵营（CF 生化手雷不打人类） =====
        public int grenadeDamage = 50;            // 爆炸中心伤害（随距离线性衰减到 40%）
        public float grenadeRadius = 4f;          // 爆炸半径（米）
        public float grenadeFuseSeconds = 3f;     // 引信时长（从出手计时，服务端权威）

        public static BioChemConfig Instance;
        private static string s_ConfigPath;
        private static int localArenaSetting;

        public static void Load(string configDir, Action<string> log)
        {
            var config = new BioChemConfig();
            try
            {
                s_ConfigPath = Path.Combine(configDir, "biochem.json");
                if (File.Exists(s_ConfigPath))
                {
                    var loaded = new BioChemConfig();
                    JsonUtility.FromJsonOverwrite(File.ReadAllText(s_ConfigPath), loaded);
                    config = loaded;
                }
                else
                {
                    File.WriteAllText(s_ConfigPath, JsonUtility.ToJson(config, prettyPrint: true));
                    log("已生成默认配置：" + s_ConfigPath);
                }
            }
            catch (Exception e)
            {
                log("配置读取失败，使用默认值：" + e.Message);
            }
            config.Clamp();
            localArenaSetting = config.arenaMap;
            Instance = config;
        }

        // 面板编辑后写回磁盘（只有房主本机的文件需要持久化）
        public static void Save(Action<string> log)
        {
            if (Instance == null || s_ConfigPath == null) return;
            localArenaSetting = Instance.arenaMap;
            try
            {
                File.WriteAllText(s_ConfigPath, JsonUtility.ToJson(Instance, prettyPrint: true));
            }
            catch (Exception e)
            {
                log("配置保存失败：" + e.Message);
            }
        }

        internal static void RestoreLocalArenaSetting() => Instance.arenaMap = localArenaSetting;

        // 数值边界。Load / 面板编辑 / 客机应用房主数值后都要走一遍
        public void Clamp()
        {
            if (arenaMap == -1) arenaMap = useQuarantineDepot ? (int)BioChemMap.QuarantineDepot : (int)BioChemMap.Campus;
            if (arenaMap < (int)BioChemMap.Campus || arenaMap > (int)BioChemMap.DistilleryYard) arenaMap = (int)BioChemMap.Campus;
            initialInfectedStudents = Mathf.Clamp(initialInfectedStudents, 0, MaxInitialInfectedStudents);
            pillarInfectSeconds = Mathf.Max(1f, pillarInfectSeconds);
            teacherMaxHp = Mathf.Max(1, teacherMaxHp);
            teacherHpPerWrite = Mathf.Max(0, teacherHpPerWrite);
            miniMaxHp = Mathf.Max(1, miniMaxHp);
            miniStunSeconds = Mathf.Clamp(miniStunSeconds, 0.5f, 10f);
            miniStunRange = Mathf.Clamp(miniStunRange, 0.5f, 10f);
            miniMeleeInterval = Mathf.Clamp(miniMeleeInterval, 0.1f, 10f);
            miniMeleeAngle = Mathf.Clamp(miniMeleeAngle, 30f, 360f);
            dartDamage = Mathf.Max(1, dartDamage);
            dartDamageBonusPerInfected = Mathf.Clamp(dartDamageBonusPerInfected, 0f, 3f);
            dartMaxAmmo = Mathf.Clamp(dartMaxAmmo, 1, 9999);
            dartAmmoPerFood = Mathf.Clamp(dartAmmoPerFood, 0, 9999);
            fireInterval = Mathf.Clamp(fireInterval, 0.1f, 10f);
            dartSlowPerStack = Mathf.Clamp(dartSlowPerStack, 0.05f, 0.8f);
            dartSlowMaxStacks = Mathf.Clamp(dartSlowMaxStacks, 1, 10);
            dartSlowSeconds = Mathf.Clamp(dartSlowSeconds, 0.1f, 10f);
            downSeconds = Mathf.Clamp(downSeconds, 1f, 30f);
            hunterTriggerStudents = Mathf.Clamp(hunterTriggerStudents, 1, 50);
            hunterHp = Mathf.Clamp(hunterHp, 1, 999);
            hunterDamage = Mathf.Clamp(hunterDamage, 1, 99);
            hunterAttackInterval = Mathf.Clamp(hunterAttackInterval, 0.1f, 10f);
            teacherAttackDamage = Mathf.Clamp(teacherAttackDamage, 1, 99);
            pistolDamage = Mathf.Max(1, pistolDamage);
            pistolMagSize = Mathf.Clamp(pistolMagSize, 1, 60);
            pistolMaxAmmo = Mathf.Clamp(pistolMaxAmmo, 1, 9999);
            pistolAmmoPerFood = Mathf.Clamp(pistolAmmoPerFood, 0, 9999);
            pistolFireInterval = Mathf.Clamp(pistolFireInterval, 0.1f, 10f);
            kukriLightDamage = Mathf.Max(1, kukriLightDamage);
            kukriHeavyDamage = Mathf.Max(1, kukriHeavyDamage);
            kukriLightInterval = Mathf.Clamp(kukriLightInterval, 0.15f, 10f);
            kukriHeavyCooldown = Mathf.Clamp(kukriHeavyCooldown, 0.2f, 10f);
            kukriLightRange = Mathf.Clamp(kukriLightRange, 0.5f, 10f);
            kukriHeavyRange = Mathf.Clamp(kukriHeavyRange, 0.5f, 10f);
            kukriLightHitDelay = Mathf.Clamp(kukriLightHitDelay, 0.05f, 2f);
            kukriHeavyHitDelay = Mathf.Clamp(kukriHeavyHitDelay, 0.05f, 3f);
            grenadeDamage = Mathf.Max(1, grenadeDamage);
            grenadeRadius = Mathf.Clamp(grenadeRadius, 1f, 20f);
            grenadeFuseSeconds = Mathf.Clamp(grenadeFuseSeconds, 1f, 10f);
        }
    }
}
