using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BioChem
{
    internal static class BioChemRoundRoles
    {
        private static readonly MethodInfo assignTeachers = AccessTools.Method(typeof(ClassroomManager), "AssignClassTeacher");
        private static bool pendingReveal;

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(assignTeachers, prefix: new HarmonyMethod(typeof(BioChemRoundRoles), nameof(BeforeAssignTeachers)));
            harmony.Patch(AccessTools.Method(typeof(ClassroomManager), nameof(ClassroomManager.ChangeState)),
                prefix: new HarmonyMethod(typeof(BioChemRoundRoles), nameof(BeforeChangeState)));
        }

        private static bool BeforeAssignTeachers(ClassroomManager __instance, List<Unit> players, ref bool __result)
        {
            if (!ModEntry.enabled || !NetRuntime.IsServer || __instance.curState != EClassroomState.BeforeClass) return true;

            Reset();
            __result = false;
            var room = RoomManager.Instance.CurrentRoom;
            if (room == null || room.PlayerLimit < 2 || players == null || players.Count < 2) return false;

            // 先统一成学生模型，倒计时结束前无需选人或同步教师身份。
            var handler = RoomManager.Instance.GetRoomMessageHandler();
            foreach (Unit player in players)
            {
                if (player.UnitSubType != EUnitSubType.Teacher) continue;
                Unit student = handler.ChangePlayerIdentifyOnServer(player, isTeacher: false);
                if (student == null || student.UnitSubType != EUnitSubType.Student) return false;
            }
            pendingReveal = true;
            __result = true;
            return false;
        }

        private static bool BeforeChangeState(ClassroomManager __instance, EClassroomState newState)
        {
            if (!NetRuntime.IsServer) return true;
            if (newState == EClassroomState.BeforeClass) Reset();
            if (!pendingReveal || __instance.curState != EClassroomState.Prepare || newState != EClassroomState.Classing) return true;
            Reset();

            var room = RoomManager.Instance.CurrentRoom;
            if (room == null) return false;
            var players = new List<Unit>();
            foreach (Unit unit in room.GetPlayers().Values)
                if (unit != null && unit.isPlayer && unit.isActived && !unit.IsDead) players.Add(unit);

            try
            {
                // 原生分配遵循房间的老师人数。零向量使每个新老师沿用变身前的位置。
                Vector3 position = Vector3.zero;
                if (!ModEntry.enabled)
                {
                    Transform teacherSpawn = ClassroomConfig.Active?.GetClassroomByFloor(__instance.currentFloor)?.teacherPos;
                    if (teacherSpawn != null) position = teacherSpawn.position;
                }
                if (players.Count >= 2)
                {
                    // 新版多了 waitForBusLayout。生化揭晓时立即在原地换身份，无需等待校巴布置。
                    object[] arguments = assignTeachers.GetParameters().Length == 3
                        ? new object[] { players, position, false }
                        : new object[] { players, position };
                    if ((bool)assignTeachers.Invoke(__instance, arguments)) return true;
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log("生化开局分配老师失败：" + ex.GetBaseException().Message);
            }

            ModEntry.NotifyAll("无法分配老师，本局已结束。");
            __instance.ChangeState(EClassroomState.BeforeClass);
            return false;
        }

        internal static void Reset() => pendingReveal = false;
    }
}
