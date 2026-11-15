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

        // 上课离座追踪：uid → 最近一次确认离座的时刻；坐下即移除。
        // 仅非操场教室启用（操场学生站着活动是正常的，见 IsPlaygroundFloor）
        private static readonly Dictionary<ulong, float> outOfSeatSince = new Dictionary<ulong, float>();
        private static float outOfSeatScanUntil;

        // 巡逻锚点：座位/电脑椅位置缓存（进教室首次巡一次）+ 讲台位
        private static readonly List<Vector3> seatPoints = new List<Vector3>();
        private static bool seatCacheBuilt;
        private static Vector3 podiumPoint;
        private static bool podiumKnown;
        private static Vector3 classCenterCache; // 本班教室中心（有人坐的椅子质心）
        private static bool classCenterKnown;

        /// <summary>下局按铃要补的 AI 老师数量（面板可改，写回配置）</summary>
        internal static int PendingAiCount => Mathf.Clamp(ModEntry.Config != null ? ModEntry.Config.defaultAiCount : 0, 0, 99);

        internal static bool IsAiTeacher(ulong uid) => uid != 0L && aiTeacherUIDs.Contains(uid);

        /// <summary>开局补 AI 老师：teacherPosition 来自 AssignClassTeacher 的老师传送点（与原版真人老师出生一致）</summary>
        internal static void SpawnAiTeachers(int count, Vector3 teacherPosition)
        {
            if (count <= 0) return;
            if (!UnitManager.IsSingletonActive() || !NetRuntime.IsServer)
            {
                ModEntry.Log?.Invoke("[警告] 非服务器端不生成 AI 老师");
                return;
            }
            // 巡逻锚点用座位区质心（比办公室门口更有巡视价值）；
            // 出生点不用它——v1.2.1 实测质心出生会整只老师消失，出生必须与原版一致。
            // 质心只取出生点周围 30m 内的座位（校舍地图全校椅子都叫 Chair，不过滤会算到别的楼去）
            Vector3 anchor = teacherPosition;
            if (TryGetSeatCentroid(out Vector3 centroid, teacherPosition, 30f))
            {
                anchor = centroid;
            }
            SetPodium(anchor);
            var um = UnitManager.Instance;
            for (int i = 0; i < count; i++)
            {
                int teacherId = ModEntry.Config.RollTeacherId();
                // 出生点旁横向错开，避免多个 AI 老师重叠
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
            ModEntry.Log?.Invoke($"[状态] AI 老师出生点={teacherPosition} 巡逻锚点={anchor} 全图座位={GetSeatPoints().Count} 本教室座位={GetSeatPointsNear(anchor, 30f).Count}");
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

        // ===== 巡逻锚点 =====

        /// <summary>记录讲台位（开局 AssignClassTeacher postfix 的 teacherPosition）</summary>
        internal static void SetPodium(Vector3 position)
        {
            podiumPoint = position;
            podiumKnown = true;
        }

        internal static bool TryGetPodium(out Vector3 position)
        {
            position = podiumPoint;
            return podiumKnown;
        }

        /// <summary>
        /// 教室座位/电脑椅位置（操场等无座位地图返回空表）。
        /// 座位是静态家具，进教室缓存一次，清局失效。
        /// ⚠️ 校舍类自定义地图一个场景里有全校的椅子，直接用会选到别的楼——
        /// 巡逻/锚点必须走 GetSeatPointsNear 过滤到当前教室附近。
        /// </summary>
        internal static List<Vector3> GetSeatPoints()
        {
            if (seatCacheBuilt) return seatPoints;
            seatCacheBuilt = true;
            if (!UnitManager.IsSingletonActive()) return seatPoints;
            foreach (Unit value in UnitManager.Instance.unitsDict.Values)
            {
                if (value == null || !value.isActived) continue;
                EUnitSubType subType = value.UnitSubType;
                if (subType != EUnitSubType.Chair && subType != EUnitSubType.ComputerChair) continue;
                if (value.GetComponent<ToiletComponent>() != null) continue; // 厕位不算教室座位
                seatPoints.Add(value.Position);
            }
            return seatPoints;
        }

        /// <summary>当前教室附近的座位点：校舍地图里全校家具都叫 Chair，只取 center 周围 maxDist 内的</summary>
        internal static List<Vector3> GetSeatPointsNear(Vector3 center, float maxDist)
        {
            List<Vector3> all = GetSeatPoints();
            List<Vector3> near = new List<Vector3>();
            float maxDistSq = maxDist * maxDist;
            foreach (Vector3 p in all)
            {
                if ((p - center).sqrMagnitude <= maxDistSq)
                {
                    near.Add(p);
                }
            }
            return near;
        }

        /// <summary>当前教室是否操场：操场无座位，学生站着活动属正常，离座判定必须关闭</summary>
        internal static bool IsPlaygroundFloor()
        {
            return ClassroomManager.IsSingletonActive()
                && ClassroomManager.Instance.currentFloor == ClassroomManager.PlaygroundClassroomFloor;
        }

        /// <summary>学生聚集地质心：校舍地图里学生在哪教室就在哪（真实学生优先，NPC 兜底）</summary>
        internal static bool TryGetStudentCentroid(out Vector3 centroid)
        {
            centroid = default;
            if (!UnitManager.IsSingletonActive()) return false;
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (Unit player in UnitManager.Instance.playerList)
            {
                if (player != null && player.isPlayer && player.IsStudent && player.isActived && !player.IsDead)
                {
                    sum += player.Position;
                    count++;
                }
            }
            if (count == 0)
            {
                // 没有真人学生（极端情况）：拿 NPC 学生采样
                foreach (Unit value in UnitManager.Instance.unitsDict.Values)
                {
                    if (value != null && value.isActived && value.UnitSubType == EUnitSubType.Student_NPC)
                    {
                        sum += value.Position;
                        count++;
                        if (count >= 10) break; // 够采样就行
                    }
                }
            }
            if (count == 0) return false;
            centroid = sum / count;
            return true;
        }

        /// <summary>
        /// 本班教室中心，三级定位：
        /// ①真人学生坐的椅子（游戏把他们放到哪，教室就在哪，最权威；缓存后本局锁定）
        /// ②"有人坐着"的椅子质心（NPC 坐位；校舍地图 30 把椅子散布全校，必须有定位）
        /// ③学生质心兜底。
        /// </summary>
        internal static bool TryGetClassCenter(out Vector3 center)
        {
            // ① 真人学生的座位：缓存一次就不再变（学生下课乱走不影响定位）
            if (!classCenterKnown && UnitManager.IsSingletonActive())
            {
                foreach (Unit player in UnitManager.Instance.playerList)
                {
                    if (player == null || !player.isPlayer || !player.IsStudent || !player.isActived || player.IsDead) continue;
                    if (!player.IsSitting || player.currentSitUnit == null) continue;
                    ChairComponent chair = player.currentSitUnit.GetComponent<ChairComponent>();
                    if (chair == null || player.currentSitUnit.GetComponent<ToiletComponent>() != null) continue;
                    classCenterCache = player.currentSitUnit.Position;
                    classCenterKnown = true;
                    ModEntry.Log?.Invoke($"[状态] 教室定位：真人学生座位 {classCenterCache}");
                    break;
                }
            }
            // ② NPC 坐着的椅子质心
            if (!classCenterKnown && UnitManager.IsSingletonActive())
            {
                Vector3 sum = Vector3.zero;
                int n = 0;
                foreach (Unit value in UnitManager.Instance.unitsDict.Values)
                {
                    if (value == null || !value.isActived || value.UnitSubType != EUnitSubType.Chair) continue;
                    ChairComponent chair = value.GetComponent<ChairComponent>();
                    if (chair == null || chair.sitter == null) continue;
                    sum += value.Position;
                    n++;
                }
                if (n >= 2)
                {
                    classCenterCache = sum / n;
                    classCenterKnown = true;
                    ModEntry.Log?.Invoke($"[状态] 教室定位：{n} 把有人坐的椅子质心 {classCenterCache}");
                }
            }
            // ③ 缓存 → 学生质心
            if (classCenterKnown)
            {
                center = classCenterCache;
                return true;
            }
            if (TryGetStudentCentroid(out center))
            {
                classCenterCache = center;
                classCenterKnown = true;
                return true;
            }
            return false;
        }

        /// <summary>座位区质心（center 周围 maxDist 内的座位），AI 巡逻锚点用；无座位返回 false</summary>
        internal static bool TryGetSeatCentroid(out Vector3 centroid, Vector3 center, float maxDist)
        {
            centroid = default;
            List<Vector3> seats = GetSeatPointsNear(center, maxDist);
            if (seats.Count == 0) return false;
            Vector3 sum = Vector3.zero;
            foreach (Vector3 p in seats)
            {
                sum += p;
            }
            centroid = sum / seats.Count;
            return true;
        }

        // ===== 上课离座追踪 =====

        private static void RefreshOutOfSeat()
        {
            float now = Time.unscaledTime;
            if (now < outOfSeatScanUntil) return;
            outOfSeatScanUntil = now + 0.25f;
            if (!UnitManager.IsSingletonActive()) return;
            // 真人学生扫描：非坐/床/被抓/挂柱/罚做操 → 记录离座起点；坐下即移除
            foreach (Unit player in UnitManager.Instance.playerList)
            {
                if (player == null || !player.isPlayer || !player.IsStudent || !player.isActived || player.IsDead)
                {
                    continue;
                }
                int state = player.CurState();
                bool seated = player.IsSitting || player.IsOnBed || state == 102 || state == 103 || state == 106;
                if (seated)
                {
                    outOfSeatSince.Remove(player.UID);
                }
                else if (!outOfSeatSince.ContainsKey(player.UID))
                {
                    outOfSeatSince[player.UID] = now;
                }
            }
        }

        /// <summary>该学生是否离座超过宽限期（操场地图调用方负责关闭，坐下/上厕所坐着即解除）</summary>
        internal static bool IsOutOfSeat(ulong studentUid, float graceSeconds)
        {
            RefreshOutOfSeat();
            return outOfSeatSince.TryGetValue(studentUid, out float since)
                && Time.unscaledTime - since >= graceSeconds;
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
            if (aiTeacherUIDs.Count == 0 && trickRadar.Count == 0 && targetClaims.Count == 0
                && violatingStudents.Count == 0 && outOfSeatSince.Count == 0 && !seatCacheBuilt && !podiumKnown) return;
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
            outOfSeatSince.Clear();
            seatPoints.Clear();
            seatCacheBuilt = false;
            podiumKnown = false;
            classCenterKnown = false;
        }

        /// <summary>退房清理（unit 由游戏统一回收，这里只清登记表）</summary>
        internal static void OnRoomCleared()
        {
            aiTeacherUIDs.Clear();
            trickRadar.Clear();
            targetClaims.Clear();
            violatingStudents.Clear();
            outOfSeatSince.Clear();
            seatPoints.Clear();
            seatCacheBuilt = false;
            podiumKnown = false;
            classCenterKnown = false;
        }
    }
}
