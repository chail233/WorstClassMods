using System;
using System.IO;
using UnityEngine;

namespace BioChem
{
    [Serializable]
    public class BioChemConfig
    {
        internal const int MaxInitialInfectedStudents = 50;

        public int initialInfectedStudents = 1;  // 正式上课时随机感染的人数，0 = 关闭
        public float pillarInfectSeconds = 10f;   // 挂柱多久后被感染（CF 节奏：抓到很快滚雪球）
        public int teacherMaxHp = 90;             // 大老师 HP
        public int teacherHpPerWrite = 9;         // 大老师每次写完板书回复 HP（上限高了回复量配套上调）
        public int miniMaxHp = 36;                // 小老师 HP
        public float staminaMax = 10f;            // 小老师体力上限
        public float staminaDrainPerSecond = 0.5f;// 小老师走路每秒耗体力
        public float staminaCostPerStun = 2f;     // 小老师每次击晕耗体力
        public float staminaPerWrite = 2f;        // 小老师每次完整写完板书回复的体力（长按读条期间缓慢回满这个量）
        public float miniStunSeconds = 2.5f;      // 小老师击晕时长
        public float miniStunCooldownSeconds = 10f;
        public float miniStunRange = 2.2f;        // 小老师击晕触发距离
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

        public static BioChemConfig Instance;
        private static string s_ConfigPath;

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
            Instance = config;
        }

        // 面板编辑后写回磁盘（只有房主本机的文件需要持久化）
        public static void Save(Action<string> log)
        {
            if (Instance == null || s_ConfigPath == null) return;
            try
            {
                File.WriteAllText(s_ConfigPath, JsonUtility.ToJson(Instance, prettyPrint: true));
            }
            catch (Exception e)
            {
                log("配置保存失败：" + e.Message);
            }
        }

        // 数值边界。Load / 面板编辑 / 客机应用房主数值后都要走一遍
        public void Clamp()
        {
            initialInfectedStudents = Mathf.Clamp(initialInfectedStudents, 0, MaxInitialInfectedStudents);
            pillarInfectSeconds = Mathf.Max(1f, pillarInfectSeconds);
            teacherMaxHp = Mathf.Max(1, teacherMaxHp);
            teacherHpPerWrite = Mathf.Max(0, teacherHpPerWrite);
            miniMaxHp = Mathf.Max(1, miniMaxHp);
            staminaMax = Mathf.Max(1f, staminaMax);
            staminaDrainPerSecond = Mathf.Clamp(staminaDrainPerSecond, 0f, 5f);
            staminaCostPerStun = Mathf.Max(0f, staminaCostPerStun);
            staminaPerWrite = Mathf.Max(0f, staminaPerWrite);
            miniStunSeconds = Mathf.Clamp(miniStunSeconds, 0.5f, 10f);
            miniStunCooldownSeconds = Mathf.Max(0.5f, miniStunCooldownSeconds);
            miniStunRange = Mathf.Clamp(miniStunRange, 0.5f, 10f);
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
        }
    }
}
