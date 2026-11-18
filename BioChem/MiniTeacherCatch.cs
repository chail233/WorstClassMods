using System.Collections.Generic;
using UnityEngine;

namespace BioChem
{
    /// <summary>
    /// 小老师抓取能力：给感染玩家补挂原生 CatchComponent（真老师只在单位创建时挂载，UnitManager.cs 老师分支），
    /// 打通"抓眩晕学生→扛走→挂柱"原生链路——服务端抓捕/挂柱校验用 IsTeacher 翻转属性天然通过，
    /// 补上组件即全部通过（抓捕提示③/服务端挂柱④连 patch 都不用）。
    /// - 客户端门槛①：DealArrestEffect 查 localPlayer 的 SkillComponent（学生预制体没有）→ prefix 接管，
    ///   复刻原方法去掉能量门槛。故意不挂 SkillComponent：会解锁粉笔攻击/提问技能改变平衡；
    ///   CatchComponent 无攻击输入零副作用
    /// - 猎手不在 infected 集合，天然不获得抓取（他们是学生阵营）
    /// - 扛人者出局没有原生"老师被晕放手"路径（TeacherStunComponent.ApplyStun→ReleaseStunnedCatch，
    ///   小老师无此组件）：ForceReleaseIfCarrying 服务端反射 ReleaseStudentOnServer 原地放下
    /// 生命周期：感染应用点 + Tick 1s 自愈 Ensure（晚加入/快照先于单位生成都能补上）；
    /// 清局/退房 Wake(false)+RemoveComponent 收组件（Unit.RemoveComponent 只摘链表不做生命周期，需手动 OnPassive）。
    /// 抓取消耗：不耗体力（小老师体力管击晕，抓取免费与原生老师一致——能量扣减在 CatchComponent.CatchUnit
    /// 里经 SkillComponent?.UseSkillEnergy 静默跳过）
    /// </summary>
    internal static class MiniTeacherCatch
    {
        private static readonly HashSet<ulong> added = new HashSet<ulong>(); // 我们补挂过组件的单位（防误删原生组件）
        private static float nextSelfHeal;                                    // Tick 自愈节流

        // ===== 组件供给（幂等） =====

        internal static void EnsureCatchAbility(ulong uid)
        {
            if (!ModEntry.enabled || !UnitManager.IsSingletonActive()) return;
            if (!ModEntry.infected.Contains(uid) || added.Contains(uid)) return;
            Unit unit = UnitManager.Instance.GetUnitByUID(uid);
            if (unit == null || !unit.isActived) return; // 单位还没生成，Tick 自愈会重试
            if (unit.GetComponent<CatchComponent>() != null)
            {
                added.Add(uid); // 已有组件（理论上不会发生）只记账不重复挂
                return;
            }
            var comp = ObjectManager.comPools.Get<CatchComponent>();
            unit.AddComponent(comp); // 镜像原生老师分支挂载方式（UnitManager.cs:679）
            comp.Wake(true);         // 单位已激活，动态补挂错过 ActiveComps 批量唤醒——手动补（注册 msg 2017 断线清理）
            added.Add(uid);
            ModEntry.Log?.Invoke("[状态] " + ModEntry.GetName(uid) + "（小老师）获得了抓取能力");
        }

        internal static void RemoveCatchAbility(ulong uid)
        {
            if (!added.Remove(uid)) return; // 没挂过我们的组件（原生老师）不动
            if (!UnitManager.IsSingletonActive()) return;
            Unit unit = UnitManager.Instance.GetUnitByUID(uid);
            var comp = unit != null && unit.isActived ? unit.GetComponent<CatchComponent>() : null;
            if (comp == null) return;
            comp.Wake(false); // OnPassive：注销 msg 2017（RemoveComponent 只摘链表，生命周期要自己走完）
            unit.RemoveComponent(comp);
        }

        internal static void RemoveAll()
        {
            foreach (ulong uid in new List<ulong>(added)) RemoveCatchAbility(uid);
        }

        /// <summary>Tick 自愈：感染名单里的玩家单位就绪即补挂（1s 节流，覆盖晚加入/快照先于单位生成）。</summary>
        internal static void TickSelfHeal()
        {
            if (Time.unscaledTime < nextSelfHeal) return;
            nextSelfHeal = Time.unscaledTime + 1f;
            foreach (ulong uid in new List<ulong>(ModEntry.infected)) EnsureCatchAbility(uid);
        }

        // ===== 扛人者出局：强制放下学生（服务端） =====

        /// <summary>携带学生的小老师被猎手击杀/吹箭打倒地时调用——原生"被晕放手"依赖 TeacherStunComponent，小老师没有。</summary>
        internal static void ForceReleaseIfCarrying(ulong uid)
        {
            if (!NetRuntime.IsServer || !UnitManager.IsSingletonActive()) return;
            Unit unit = UnitManager.Instance.GetUnitByUID(uid);
            var catchComp = unit != null && unit.isActived ? unit.GetComponent<CatchComponent>() : null;
            if (catchComp == null || catchComp.catchedUnit == null) return;
            ulong studentUid = catchComp.catchedUnit.UID;
            var request = new StudentReleaseMessageRequest_C2S { teacherUID = uid, studentUID = studentUid, pillarUID = 0 };
            // internal 方法走反射（与柱子超时强释同款）；rescuer=本人不触发救援惩罚，
            // operatorReleasePosition=原地放下避免"附近没有位置"被拦
            object result = ModEntry.ReleaseStudentOnServerMethod.Invoke(
                RoomManager.Instance.GetSkillMessageHandler(),
                new object[] { uid, request, false, unit.Position });
            ModEntry.Log?.Invoke("[状态] " + ModEntry.GetName(uid) + " 出局，原地放下了学生 " + ModEntry.GetName(studentUid)
                + (result is bool ok && ok ? "" : "（释放未完成，由携带超时兜底）"));
        }

        // ===== 客户端门槛①：DealArrestEffect 去掉 SkillComponent 门槛（仅 infected 本机接管） =====

        /// <summary>prefix：小老师本机复刻原方法（去掉 SkillComponent 检查），其余情况放行原方法。</summary>
        public static bool BeforeDealArrestEffect(StudentInterectComponent __instance)
        {
            if (!ModEntry.enabled || !UnitManager.IsSingletonActive()) return true;
            Unit local = MonoSingletonT<UnitManager>.Instance.localPlayer;
            if (local == null || !local.isActived) return true;
            if (!ModEntry.infected.Contains(local.UID)) return true; // 真老师/普通学生/猎手走原方法
            if (local.GetComponent<SkillComponent>() != null) return true; // 双保险：有组件走原生逻辑
            Unit target = __instance.owner;
            if (target == null || !target.isActived) return true;
            if (local.CurrentCatchUnit != null) return true;      // 正扛着人：原方法同款静默拦截
            if (!local.IsTeacher) return true;                    // 身份兜底（infected 必为真，防御性保留）
            Vector3 a = local.Position;
            Vector3 b = target.Position;
            a.y = 0f;
            b.y = 0f;
            if (Vector3.Distance(b, a) > ConfigManager.TeacherSetting.arrestDistance) return true; // 太远：原方法同款静默失败
            BeCatchedComponent beCatched = target.GetComponent<BeCatchedComponent>();
            if (beCatched != null && beCatched.IsNpcShield)
            {
                // NPC 人盾分支照抄原方法：翻转后 CanTeacherTakeNpcShield（IsTeacher+CatchComponent）通过，可接管
                if (BeCatchedComponent.CanTeacherTakeNpcShield(local, target))
                {
                    var shield = new NpcShieldRequest_C2S { carrierUID = local.UID, npcUID = target.UID, takeByTeacher = true };
                    if (MonoSingletonT<NetManager>.Instance.IsHost())
                        MonoSingletonT<RoomManager>.Instance.GetSkillMessageHandler().OnNpcShieldRequest(null, shield);
                    else
                        MonoSingletonT<RoomManager>.Instance.SendToServer(shield);
                }
            }
            else
            {
                var arrest = new UseSkillMessageRequest_C2S { skillID = 3, targetUID = target.UID, userUID = local.UID };
                if (MonoSingletonT<NetManager>.Instance.IsHost())
                    MonoSingletonT<RoomManager>.Instance.GetSkillMessageHandler().OnUseSkillMessageRequest(
                        MonoSingletonT<RoomManager>.Instance.GetConnectByPlayerUID(local.UID), arrest);
                else
                    MonoSingletonT<RoomManager>.Instance.SendToServer(arrest);
            }
            return false; // 原方法已接管完成，跳过（否则会在 SkillComponent==null 处静默失败）
        }
    }
}
