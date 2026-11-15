using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace AITeacher
{
    /// <summary>
    /// AI 老师生命周期与捣蛋雷达：
    /// - 开局按 AssignClassTeacher postfix 补足 AI 老师（数量 = AI 设定，不超过房间规则老师数）
    /// - 记录捣蛋计分事件（OnResponseTrickCountData 是全端计分入口），供 AI"听见"捣蛋
    /// - 下课/清局回收 AI 老师 unit
    /// </summary>
    internal static class AiTeacherManager
    {
        private static readonly HashSet<ulong> aiTeacherUIDs = new HashSet<ulong>();

        private struct TrickEvent
        {
            public float time;
            public Vector3 position;
        }

        // 每个真人学生最近一次捣蛋事件（时间 + 位置快照）
        private static readonly Dictionary<ulong, TrickEvent> trickRadar = new Dictionary<ulong, TrickEvent>();

        // 多 AI 协同：目标认领（目标 uid → (认领的 AI uid, 到期时间)），避免几个 AI 挤同一个学生
        private struct TargetClaim
        {
            public ulong teacherUid;
            public float until;
        }
        private static readonly Dictionary<ulong, TargetClaim> targetClaims = new Dictionary<ulong, TargetClaim>();

        // 共享违规学生集合（手持捣蛋物品/玩电脑），0.25s 刷新一次，所有 AI 共用一份扫描结果
        private static readonly HashSet<ulong> violatingStudents = new HashSet<ulong>();
        private static float violationsUntil;

        /// <summary>下局按铃要补的 AI 老师数量（面板可改，写回配置）</summary>
        internal static int PendingAiCount => Mathf.Clamp(ModEntry.Config != null ? ModEntry.Config.defaultAiCount : 0, 0, 99);

        internal static bool IsAiTeacher(ulong uid) => uid != 0L && aiTeacherUIDs.Contains(uid);

        /// <summary>开局补 AI 老师：teacherPosition 来自 AssignClassTeacher 的讲台位置</summary>
        internal static void SpawnAiTeachers(int count, Vector3 teacherPosition)
        {
            if (count <= 0) return;
            if (!UnitManager.IsSingletonActive() || !NetRuntime.IsServer)
            {
                ModEntry.Log?.Invoke("[警告] 非服务器端不生成 AI 老师");
                return;
            }
            var um = UnitManager.Instance;
            for (int i = 0; i < count; i++)
            {
                int teacherId = ModEntry.Config.RollTeacherId();
                // 讲台旁横向错开，避免多个 AI 老师重叠
                Vector3 pos = teacherPosition + new Vector3(i * 1.4f, 0f, 0f);
                UnitDataNet data = um.CreateUnitNetData(teacherId, pos, Vector3.zero, null, 0uL, syncMsg: false);
                // 先登记 UID：CreateCommonUnit 过程中组件补挂 postfix 要靠它识别 AI 老师
                aiTeacherUIDs.Add(data.UID);
                // 玩家 ID（1001/1002）不走自动 UnitBorn 通道，手动广播出生（客户端按普通 unit 创建，能看不能控）
                RoomManager.Instance.BroadcastToAllExceptHost(new UnitBornMessageResponse_S2C
                {
                    unitUID = data.UID,
                    netData = data
                });
                Unit unit = um.CreateCommonUnit(data, isLocal: true);
                if (unit == null)
                {
                    ModEntry.Log?.Invoke($"[警告] AI 老师（ID {teacherId}）创建失败");
                    aiTeacherUIDs.Remove(data.UID);
                    continue;
                }
                ModEntry.Log?.Invoke($"[状态] 已生成 AI 老师 UID={data.UID} 形态={ModEntry.FormName(teacherId)} 位置={pos}");
            }
        }

        /// <summary>捣蛋雷达：uid 在 pos 捣蛋计分了一次（仅正向计分）</summary>
        internal static void RecordTrickEvent(ulong uid, Vector3 position)
        {
            trickRadar[uid] = new TrickEvent { time = Time.unscaledTime, position = position };
        }

        /// <summary>AI 查询：该学生近期是否在可听范围内捣蛋（AI 老师所在位置为 earPos）</summary>
        internal static bool TryHearTrick(ulong studentUid, Vector3 earPos, float hearingDistance, float memorySeconds, out Vector3 lastPos)
        {
            lastPos = default;
            if (!trickRadar.TryGetValue(studentUid, out var ev)) return false;
            if (Time.unscaledTime - ev.time > memorySeconds) return false;
            if ((ev.position - earPos).sqrMagnitude > hearingDistance * hearingDistance) return false;
            lastPos = ev.position;
            return true;
        }

        /// <summary>捣蛋雷达历史热点：全局最近一次捣蛋事件的位置（巡逻偏转用），maxAge 内有效</summary>
        internal static bool TryGetLatestTrickPoint(float maxAgeSeconds, out Vector3 position)
        {
            position = default;
            float bestTime = -1f;
            foreach (var ev in trickRadar.Values)
            {
                if (ev.time > bestTime)
                {
                    bestTime = ev.time;
                    position = ev.position;
                }
            }
            return bestTime >= 0f && Time.unscaledTime - bestTime <= maxAgeSeconds;
        }

        // ===== 多 AI 协同：目标认领 =====

        internal static void ClaimTarget(ulong targetUid, ulong teacherUid, float duration = 5f)
        {
            if (targetUid == 0L || teacherUid == 0L) return;
            targetClaims[targetUid] = new TargetClaim { teacherUid = teacherUid, until = Time.unscaledTime + duration };
        }

        internal static void ReleaseTarget(ulong targetUid, ulong teacherUid)
        {
            if (targetUid != 0L && targetClaims.TryGetValue(targetUid, out var claim) && claim.teacherUid == teacherUid)
            {
                targetClaims.Remove(targetUid);
            }
        }

        /// <summary>该目标是否已被其他 AI 认领且未过期</summary>
        internal static bool IsClaimedByOther(ulong targetUid, ulong teacherUid)
        {
            if (!targetClaims.TryGetValue(targetUid, out var claim)) return false;
            if (Time.unscaledTime > claim.until) return false;
            return claim.teacherUid != teacherUid;
        }

        // ===== 违规观察（判定口径抄 SurveillanceCameraComponent.CollectViolationStates）=====

        /// <summary>该学生是否正在违规：手持捣蛋物品（手/嘴挂点）或玩电脑</summary>
        internal static bool IsViolating(ulong studentUid)
        {
            RefreshViolations();
            return violatingStudents.Contains(studentUid);
        }

        private static void RefreshViolations()
        {
            float now = Time.unscaledTime;
            if (now < violationsUntil) return;
            violationsUntil = now + 0.25f;
            violatingStudents.Clear();
            if (!UnitManager.IsSingletonActive()) return;

            // 手持捣蛋物品：遍历捣蛋物品单位，看挂点归属（书/笔是学习用品不算；泡泡糖吹泡中不算）
            foreach (Unit value in UnitManager.Instance.unitsDict.Values)
            {
                if (value == null || !value.isActived || value.IsDead || value.UnitType != EUnitType.StudentTrickItem)
                {
                    continue;
                }
                EUnitSubType subType = value.UnitSubType;
                if (subType == EUnitSubType.Book || subType == EUnitSubType.Pen)
                {
                    continue;
                }
                RoleData data = value.GetData<RoleData>();
                if (data == null)
                {
                    continue;
                }
                MountingPointDataNet mounting = data.NetData.MountingPointNet;
                if (!mounting.HaveValue || mounting.MountingUID == 0L)
                {
                    continue;
                }
                if (mounting.MountingPoint != EUnitPartType.HandPosition && mounting.MountingPoint != EUnitPartType.Mouth)
                {
                    continue;
                }
                if (mounting.MountingPoint == EUnitPartType.Mouth)
                {
                    if (value.GetComponent<BubbleGumComponent>() == null)
                    {
                        continue;
                    }
                    Unit holder = UnitManager.Instance.GetUnitByUID(mounting.MountingUID);
                    var mouth = holder != null ? holder.GetComponent<MouthItemUseComponent>() : null;
                    if (mouth != null && mouth.BalloonAvailable)
                    {
                        continue;
                    }
                }
                violatingStudents.Add(mounting.MountingUID);
            }

            // 玩电脑的学生
            foreach (Unit player in UnitManager.Instance.playerList)
            {
                if (player == null || !player.isPlayer || !player.IsStudent || !player.isActived || player.IsDead)
                {
                    continue;
                }
                var expression = player.GetComponent<StudentComputerExpressionComponent>();
                if (expression != null && expression.IsPlayingGame)
                {
                    violatingStudents.Add(player.UID);
                }
            }
        }

        /// <summary>下课/清局回收全部 AI 老师</summary>
        internal static void ClearAll(string reason)
        {
            if (aiTeacherUIDs.Count == 0 && trickRadar.Count == 0 && targetClaims.Count == 0 && violatingStudents.Count == 0) return;
            if (UnitManager.IsSingletonActive())
            {
                var um = UnitManager.Instance;
                foreach (ulong uid in aiTeacherUIDs)
                {
                    Unit unit = um.GetUnitByUID(uid);
                    if (unit != null)
                    {
                        um.BackUnit(uid, true);
                        ModEntry.Log?.Invoke($"[状态] 回收 AI 老师 UID={uid}（{reason}）");
                    }
                }
            }
            aiTeacherUIDs.Clear();
            trickRadar.Clear();
            targetClaims.Clear();
            violatingStudents.Clear();
        }

        /// <summary>退房清理（unit 由游戏统一回收，这里只清登记表）</summary>
        internal static void OnRoomCleared()
        {
            aiTeacherUIDs.Clear();
            trickRadar.Clear();
            targetClaims.Clear();
            violatingStudents.Clear();
        }
    }
}
