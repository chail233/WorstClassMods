using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace AITeacher
{
    /// <summary>
    /// 全部 Harmony 补丁：
    /// 1. SendStartClassMsg prefix：AI 设定 ≥1 时放行单人开局拦截
    /// 2. GetCurrentIntValue postfix：AssignClassTeacher 读老师数规则时减去 AI 设定（真人少分）
    /// 3. AssignClassTeacher prefix/postfix：全 AI 时接管；postfix 按差额补 AI 老师
    /// 4. BackClassSetting / ResetClassState / ClearRoomState postfix：回收 AI 老师
    /// 5. OnResponseTrickCountData postfix：捣蛋雷达
    /// 6. GenerateComponentByType postfix：给 AI 老师 unit 补挂老师组件 + AI 行为组件
    /// </summary>
    internal static class AiTeacherPatches
    {
        internal static void Apply(Harmony harmony)
        {
            // ---- 单人开局放行（仅房主、AI 设定 ≥1）----
            harmony.Patch(
                AccessTools.Method(typeof(ClassroomManager), "SendStartClassMsg"),
                prefix: new HarmonyMethod(typeof(AiTeacherPatches), "BeforeSendStartClassMsg"));

            // ---- 规则读取减法：老师数规则 − AI 设定（真人老师少分）----
            harmony.Patch(
                AccessTools.Method(typeof(RoomRuleDataManager), "GetCurrentIntValue"),
                postfix: new HarmonyMethod(typeof(AiTeacherPatches), "AfterGetCurrentIntValue"));

            // ---- 老师分配接管 + 差额补 AI ----
            harmony.Patch(
                AccessTools.Method(typeof(ClassroomManager), "AssignClassTeacher"),
                prefix: new HarmonyMethod(typeof(AiTeacherPatches), "BeforeAssignClassTeacher"),
                postfix: new HarmonyMethod(typeof(AiTeacherPatches), "AfterAssignClassTeacher"));

            // ---- 清局回收 ----
            harmony.Patch(
                AccessTools.Method(typeof(ClassroomManager), "BackClassSetting"),
                postfix: new HarmonyMethod(typeof(AiTeacherPatches), "AfterBackClassSetting"));
            harmony.Patch(
                AccessTools.Method(typeof(ClassroomManager), "ResetClassState"),
                postfix: new HarmonyMethod(typeof(AiTeacherPatches), "AfterResetClassState"));
            harmony.Patch(
                AccessTools.Method(typeof(UnitManager), "ClearRoomState"),
                postfix: new HarmonyMethod(typeof(AiTeacherPatches), "AfterClearRoomState"));

            // ---- 捣蛋雷达（全端计分入口）----
            harmony.Patch(
                AccessTools.Method(typeof(ClassroomManager), "OnResponseTrickCountData"),
                postfix: new HarmonyMethod(typeof(AiTeacherPatches), "AfterOnResponseTrickCountData"));

            // ---- AI 老师的粉笔/剑气命中上报接管（原版按连接归属校验必拒）----
            harmony.Patch(
                AccessTools.Method(typeof(UnitMessageHandler), "OnChalkHitRequest"),
                prefix: new HarmonyMethod(typeof(AiTeacherPatches), "BeforeOnChalkHitRequest"));

            // ---- 诊断：粉笔弹道物理触发（弹道到底有没有碰到学生）----
            harmony.Patch(
                AccessTools.Method(typeof(ProjectileComponent), "OnTriggerEnter", new[] { typeof(Unit), typeof(Collider) }),
                postfix: new HarmonyMethod(typeof(AiTeacherPatches), "AfterProjectileTriggerEnter"));

            // ---- 诊断：晕眩最终应用（上报有没有变成真晕眩）----
            harmony.Patch(
                AccessTools.Method(typeof(StudentStunComponent), "DealStunEffect"),
                prefix: new HarmonyMethod(typeof(AiTeacherPatches), "BeforeDealStunEffect"));

            // ---- 诊断 + 修复：剑气命中检测只在拥有者客户端跑（locallyOwned），AI 不是任何客户端的
            //      localPlayer，全房间没人模拟命中 → 剑气永远打不中人。 ApplyFire 后把 AI 弹道标记为
            //      本机拥有，host 即可跑 AdvanceOwned → ReportStudentHit → 走 AI 命中补丁 ----
            harmony.Patch(
                AccessTools.Method(typeof(TeacherSlashProjectile), "ApplyFire"),
                postfix: new HarmonyMethod(typeof(AiTeacherPatches), "AfterSlashApplyFire"));

            // ---- 诊断：剑气命中上报 ----
            harmony.Patch(
                AccessTools.Method(typeof(TeacherSlashProjectile), "ReportStudentHit"),
                prefix: new HarmonyMethod(typeof(AiTeacherPatches), "BeforeSlashReportStudentHit"));

            // ---- AI 老师 unit 组件补挂（host 创建路径）----
            harmony.Patch(
                AccessTools.Method(typeof(UnitManager), "GenerateComponentByType"),
                postfix: new HarmonyMethod(typeof(AiTeacherPatches), "AfterGenerateComponentByType"));
        }

        // ===== 1. 单人放行 =====

        private static bool BeforeSendStartClassMsg(ulong ringUID)
        {
            if (AiTeacherManager.PendingAiCount <= 0) return true;
            if (!MonoSingletonT<NetManager>.Instance.IsHost()) return true;
            if (MonoSingletonT<UnitManager>.Instance.playerList.Count > 1) return true;
            // 单人 + AI 老师：跳过"Unit_CantSingleStart"拦截，直接走开课流程
            ModEntry.Log?.Invoke("[状态] 单人开局：AI 老师补位，放行按铃");
            if (ClassroomManager.IsSingletonActive())
            {
                ClassroomManager.Instance.ClassPrepare(ringUID);
            }
            return false;
        }

        // ===== 2. 规则读取减法 =====

        private static void AfterGetCurrentIntValue(ERoomRuleId ruleId, ref int __result)
        {
            if (ruleId == ERoomRuleId.ClassTeacherCount && AiTeacherManager.PendingAiCount > 0)
            {
                __result = Mathf.Max(0, __result - AiTeacherManager.PendingAiCount);
            }
        }

        // ===== 3. 老师分配 =====

        private static bool BeforeAssignClassTeacher(List<Unit> players, ref bool __result)
        {
            int ai = AiTeacherManager.PendingAiCount;
            if (ai <= 0 || !NetRuntime.IsServer) return true;

            // prefix 里 GetCurrentIntValue 已被减过 ai，加回来得到原始规则值
            int rule = SingletonT<RoomRuleDataManager>.Instance.GetCurrentIntValue(ERoomRuleId.ClassTeacherCount) + ai;
            int maxReal = (players == null || players.Count < 2)
                ? 0
                : Mathf.Min(players.Count - 1, MonoSingletonT<RoomManager>.Instance.PlayerLimit - 1);
            int desiredReal = Mathf.Clamp(rule - ai, 0, maxReal);
            if (desiredReal > 0) return true; // 真人老师位存在，原版逻辑分配（规则已被 postfix 减过）

            // 全 AI（或人数不足分不出真人老师）：跳过原方法，postfix 补 AI 老师
            __result = true;
            return false;
        }

        private static void AfterAssignClassTeacher(List<Unit> players, Vector3 teacherPosition)
        {
            int ai = AiTeacherManager.PendingAiCount;
            if (ai <= 0 || !NetRuntime.IsServer) return;

            int rule = SingletonT<RoomRuleDataManager>.Instance.GetCurrentIntValue(ERoomRuleId.ClassTeacherCount) + ai;
            int realTeachers = 0;
            var roomPlayers = MonoSingletonT<RoomManager>.Instance.CurrentRoom?.GetPlayers();
            if (roomPlayers != null)
            {
                foreach (var pair in roomPlayers)
                {
                    Unit unit = pair.Value;
                    if (unit != null && unit.isPlayer && unit.isActived && !unit.IsDead && unit.IsTeacher)
                    {
                        realTeachers++;
                    }
                }
            }
            int spawn = Mathf.Clamp(ai, 0, rule - realTeachers);
            if (spawn > 0)
            {
                AiTeacherManager.SpawnAiTeachers(spawn, teacherPosition);
            }
            ModEntry.Log?.Invoke($"[状态] 老师分配完成：真人老师 {realTeachers}，规则老师数 {rule}，本局 AI 设定 {ai}");
        }

        // ===== 4. 清局回收 =====

        private static void AfterBackClassSetting() => AiTeacherManager.ClearAll("下课清场");

        private static void AfterResetClassState() => AiTeacherManager.ClearAll("清局重置");

        private static void AfterClearRoomState() => AiTeacherManager.OnRoomCleared();

        // ===== 5. 捣蛋雷达 =====

        private static void AfterOnResponseTrickCountData(ulong uid, float count)
        {
            if (count <= 0f) return;
            if (!UnitManager.IsSingletonActive() || !NetRuntime.IsServer) return;
            Unit unit = UnitManager.Instance.GetUnitByUID(uid);
            if (unit != null)
            {
                AiTeacherManager.RecordTrickEvent(uid, unit.Position);
            }
        }

        // ===== 6. AI 老师的粉笔/剑气命中上报 =====

        /// <summary>
        /// 原版 OnChalkHitRequest 要求"连接推导的玩家 UID == 消息 TeacherUID"；
        /// AI 老师没有真实连接，host 上报时 num=房主 UID ≠ AI UID，命中必被拒。
        /// AI 老师的命中在这里按原版服务端逻辑直接应用（保留目标与 3m 位置校验）。
        /// </summary>
        private static bool BeforeOnChalkHitRequest(ChalkHitRequest_C2S request)
        {
            if (!NetRuntime.IsServer || !AiTeacherManager.IsAiTeacher(request.TeacherUID)) return true;
            ModEntry.Log?.Invoke($"[诊断] AI 命中上报到达：目标={request.UnitUID} 类型={request.StunType}");
            if (request.StunType != 0 && request.StunType != EStunType.Sword) return false;
            Unit target = MonoSingletonT<UnitManager>.Instance.GetUnitByUID(request.UnitUID);
            if (target == null || target.IsTeacher)
            {
                ModEntry.Log?.Invoke("[诊断] AI 命中被拒：目标不存在或是老师");
                return false;
            }
            if ((target.Position - request.Position).sqrMagnitude > 9f)
            {
                ModEntry.Log?.Invoke($"[警告] AI 老师命中位置校验失败：目标UID={request.UnitUID}，距离超 3m");
                return false;
            }
            ModEntry.Log?.Invoke("[诊断] AI 命中校验通过 → 应用 GetStun");
            target.GetComponent<StudentStunComponent>()?.GetStun(request.StunType, request.HorizontalSlashStunReduction);
            return false;
        }

        /// <summary>粉笔弹道物理触发：弹道碰到任何东西都会打这一笔，用于判断"没命中"卡在哪一环</summary>
        private static void AfterProjectileTriggerEnter(ProjectileComponent __instance, Unit otherUnit, Collider other)
        {
            if (__instance?.owner == null || __instance.owner.ID != 4002) return;
            ModEntry.Log?.Invoke($"[诊断] 粉笔弹道触发：目标unit={otherUnit?.UID.ToString() ?? "非单位"} collider={other?.name} 层={other?.gameObject.layer}");
        }

        /// <summary>晕眩最终应用：走到这一步说明命中上报已生效，学生应该真的晕了</summary>
        private static void BeforeDealStunEffect(StudentStunComponent __instance, EStunType stunType)
        {
            ModEntry.Log?.Invoke($"[诊断] 晕眩应用：unit={__instance.owner?.UID} 类型={stunType}");
        }

        /// <summary>
        /// 剑气弹道命中检测只在拥有者客户端跑（locallyOwned = teacherUID == localPlayerUID）。
        /// AI 老师 UID 永远不等于房主玩家 UID → 全房间没人跑 AdvanceOwned，剑气永远打不中人。
        /// 这里把 AI 的剑气弹道在本机（host）标记为拥有者，host 便会模拟命中并走 AI 命中补丁。
        /// </summary>
        private static void AfterSlashApplyFire(TeacherSlashFired_S2C message)
        {
            try
            {
                if (!NetRuntime.IsServer || !AiTeacherManager.IsAiTeacher(message.teacherUID)) return;
                var active = Traverse.Create(typeof(TeacherSlashProjectile)).Field("active")
                    .GetValue<System.Collections.Generic.IDictionary<(ulong, uint), TeacherSlashProjectile>>();
                if (active != null && active.TryGetValue((message.teacherUID, message.sequence), out var projectile)
                    && projectile != null)
                {
                    Traverse.Create(projectile).Field("locallyOwned").SetValue(true);
                    ModEntry.Log?.Invoke($"[状态] AI 剑气弹道已标记本机模拟（seq={message.sequence}）");
                }
            }
            catch (System.Exception ex)
            {
                ModEntry.Log?.Invoke("[警告] AI 剑气弹道拥有者标记失败：" + ex.Message);
            }
        }

        /// <summary>剑气命中上报：走到这一步说明弹道真的碰到了学生</summary>
        private static void BeforeSlashReportStudentHit(TeacherSlashProjectile __instance, Unit target)
        {
            ulong teacherUid = 0UL;
            var shot = Traverse.Create(__instance).Field("shot").GetValue<TeacherSlashFired_S2C>();
            if (shot.teacherUID != 0UL) teacherUid = shot.teacherUID;
            ModEntry.Log?.Invoke($"[诊断] 剑气命中上报：老师={teacherUid} 目标={target?.UID}（AI={AiTeacherManager.IsAiTeacher(teacherUid)}）");
        }

        // ===== 7. AI 老师组件补挂（对齐 CreatePlayer 的老师分支，时机在 SetAwake 前）=====

        private static void AfterGenerateComponentByType(Unit unit)
        {
            if (unit == null || !NetRuntime.IsServer || !AiTeacherManager.IsAiTeacher(unit.UID)) return;
            var pools = ObjectManager.comPools;
            if (unit.GetComponent<FsmComponent>() == null) unit.AddComponent(pools.Get<FsmComponent>());
            unit.AddComponent(pools.Get<SkillComponent>());
            if (unit.ID == 1002 && unit.GetComponent<TeacherMeleeComponent>() == null)
            {
                unit.AddComponent(pools.Get<TeacherMeleeComponent>());
            }
            if (unit.GetComponent<CatchComponent>() == null) unit.AddComponent(pools.Get<CatchComponent>());
            if (unit.GetComponent<TeacherStunComponent>() == null) unit.AddComponent(pools.Get<TeacherStunComponent>());
            if (unit.GetComponent<TeacherExhaustionComponent>() == null) unit.AddComponent(pools.Get<TeacherExhaustionComponent>());
            if (unit.GetComponent<TeacherAIComponent>() == null) unit.AddComponent(pools.Get<TeacherAIComponent>());
        }
    }
}
