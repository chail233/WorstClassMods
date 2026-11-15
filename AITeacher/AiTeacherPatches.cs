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
            if (request.StunType != 0 && request.StunType != EStunType.Sword) return false;
            Unit target = MonoSingletonT<UnitManager>.Instance.GetUnitByUID(request.UnitUID);
            if (target == null || target.IsTeacher) return false;
            if ((target.Position - request.Position).sqrMagnitude > 9f)
            {
                ModEntry.Log?.Invoke($"[警告] AI 老师命中位置校验失败：目标UID={request.UnitUID}，距离超 3m");
                return false;
            }
            target.GetComponent<StudentStunComponent>()?.GetStun(request.StunType, request.HorizontalSlashStunReduction);
            return false;
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
