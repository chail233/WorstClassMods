using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace AITeacher
{
    /// <summary>
    /// AI 老师行为组件（仅房主本地驱动，仅对我们生成的 AI 老师 unit 挂载）。
    /// 阶段机：巡逻 → 追击（听见捣蛋/视野发现）→ 攻击（粉笔弹道 / 剑气）→ 抓捕晕眩学生 → 送罚站柱。
    /// 移动走 NavMeshAgent（照抄 NPC 学生的 host 门禁惯例），无 NavMesh 时退化为直线位移；
    /// 位置/朝向经 SyncTransformComponet 原生链路自动同步给所有客户端。
    /// </summary>
    public sealed class TeacherAIComponent : UnitComponent
    {
        private enum AiState { Patrol, Investigate, Chase, BindPillar }

        private NavMeshAgent agent;
        private SkillComponent skill;
        private CatchComponent catchComp;
        private AnimationComponent animComp;

        private AiState state = AiState.Patrol;
        private Unit chaseTarget;
        private Vector3 lastSeenPos;
        private float lastSeenAt;
        private Vector3 investigatePos;
        private float investigateUntil;
        private int investigateStage;      // 调查阶段：0=第一查看点 1=第二查看点
        private float investigateStageAt;
        private bool hasPatrolPoint;
        private Vector3 patrolPoint;
        private float patrolLegUntil;   // 当前巡逻点走不到就换点的超时
        private float patrolDwellUntil; // 到点后的驻足观察时长
        private Vector3 patrolStuckRefPos; // 巡逻卡死检测参考点
        private float patrolStuckSince;    // 巡逻卡死起始时刻
        private Vector3 chaseStuckRefPos;  // 追击卡死检测参考点
        private float chaseStuckSince;     // 追击卡死起始时刻
        private float nextActionAt;   // 反应延迟/节奏停顿
        private float nextAttackAt;
        private float nextCatchAt;    // 抓捕请求节流
        private float nextFocusAt;    // 凝视技能冷却
        private ulong focusedUid;     // 凝视中的学生（0=无）
        private float focusedUntil;   // 凝视安全超时（防 AI 卡死导致学生永久冻结）
        private float nextAgentCheck; // NavMesh 就绪重试节流
        private float baseSpeedCache; // 房间规则移速缓存（1s 刷新）
        private float baseSpeedAt;
        private float noPillarSince;  // 手里没柱子可挂的计时
        private bool movingAnim;

        private bool forceClassroomPoint;  // 下一次选点必须落在教室里（出生在办公室/走廊时先走进教室）
        private bool enteredClassroom;     // 本局已进过教室（进过就不再强制回教室，热点巡逻可正常出教室）
        private bool enterClassLogged;     // "在教室外"日志只打一次
        private Vector3 prepDest;          // 课前走向教室的落脚点
        private float prepDestUntil;       // 课前落脚点重选超时
        private bool prepDestValid;
        private Vector3 prepStuckRefPos;   // 课前卡死检测参考点
        private float prepStuckSince;

        private const float EnterClassroomRadius = 12f; // 离教室中心这么近就算已进教室

        private AiTeacherConfig Cfg => ModEntry.Config;

        // ===== 生命周期 =====

        public override void OnBindObj(ObjectModel obj)
        {
            base.OnBindObj(obj);
            // 组件来自对象池：回合状态必须重置，否则上一局"已进过教室"会带进新一局，老师又不进教室了
            state = AiState.Patrol;
            enteredClassroom = false;
            forceClassroomPoint = false;
            enterClassLogged = false;
            hasPatrolPoint = false;
            patrolLegUntil = 0f;
            patrolDwellUntil = 0f;
            prepDestValid = false;
            prepDestUntil = 0f;
            prepStuckSince = 0f;
            if (obj == null || owner == null || !owner.isLocal || !NetRuntime.IsServer) return;
            // 老师模型 prefab 不带 NavMeshAgent，host 侧动态加（同 NPC 学生的 host 门禁惯例）
            agent = obj.GetComponent<NavMeshAgent>();
            if (agent == null) agent = obj.gameObject.AddComponent<NavMeshAgent>();
            agent.speed = Cfg.moveSpeed;
            agent.angularSpeed = 360f;
            agent.acceleration = 14f;
            agent.avoidancePriority = (int)(owner.UID % 100);
            // 移动交给 NavMesh，物理体设为运动学，避免刚体对抗（回收时还原，防污染模型池）
            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = true;
            if (NavMesh.SamplePosition(obj.transform.position, out var hit, 2.5f, NavMesh.AllAreas))
            {
                agent.enabled = true;
                agent.Warp(hit.position);
            }
            else
            {
                agent.enabled = false; // 当前层无 NavMesh（如大厅）：走直线位移兜底
            }
        }

        public override void OnDisBindObj(ObjectModel obj)
        {
            if (agent != null)
            {
                if (agent.enabled && agent.isOnNavMesh) agent.isStopped = true;
                agent.enabled = false;
            }
            Rigidbody rb = (obj != null) ? obj.GetComponent<Rigidbody>() : null;
            if (rb != null) rb.isKinematic = false; // 模型回收进池前还原物理
            if (NetRuntime.IsServer && focusedUid != 0L)
            {
                ReleaseFocus("AI 老师回收"); // 别把被凝视的学生永久冻在原地
            }
            base.OnDisBindObj(obj);
        }

        public override void OnUpdate(float iDt)
        {
            if (!NetRuntime.IsServer || owner == null || !owner.isActived || owner.IsDead) return;
            if (skill == null)
            {
                skill = owner.GetComponent<SkillComponent>();
                catchComp = owner.GetComponent<CatchComponent>();
                animComp = owner.GetComponent<AnimationComponent>();
                if (skill == null) return;
            }

            RegenerateEnergy(iDt);
            EnsureAgent();

            // 凝视安全超时：AI 卡死/状态异常时自动放人，学生不能被永久冻结
            if (focusedUid != 0L && Time.unscaledTime >= focusedUntil)
            {
                ReleaseFocus("凝视超时");
            }

            EClassroomState clsState = ClassroomManager.IsSingletonActive()
                ? ClassroomManager.Instance.curState
                : EClassroomState.BeforeClass;
            bool classing = clsState == EClassroomState.Classing;
            bool preparing = clsState == EClassroomState.Prepare;
            if (owner.IsStun || (!classing && !preparing))
            {
                ReleaseFocus(owner.IsStun ? "老师被晕眩" : "下课");
                StopAgent();
                SetMoving(false);
                return;
            }

            // 手里有学生 → 送罚站柱（抓捕响应链会置 catchedUnitUID）
            if (catchComp != null && catchComp.catchedUnitUID != 0L)
            {
                TickBindPillar();
                return;
            }

            // 课前倒计时：只提前走进教室站好位，不抓人（倒计时里学生可能还没坐齐）
            if (!classing)
            {
                TickPrepareMove();
                return;
            }

            if (Time.unscaledTime < nextActionAt)
            {
                StopAgent(); // 反应延迟：站在原地"注意到"目标
                SetMoving(false);
                return;
            }

            switch (state)
            {
                case AiState.Patrol: TickPatrol(); break;
                case AiState.Investigate: TickInvestigate(); break;
                case AiState.Chase: TickChase(); break;
            }
        }

        // ===== 感知 =====

        private static bool IsValidStudent(Unit unit)
        {
            if (unit == null || !unit.isActived || unit.IsDead || unit.transform == null) return false;
            if (!unit.isPlayer || !unit.IsStudent) return false; // 只把真人学生当目标，无视 NPC 学生（抓错扣体力）
            if (unit.CurState() == 103) return false;            // 已被罚站
            var beCatched = unit.GetComponent<BeCatchedComponent>();
            if (beCatched != null && beCatched.catcher != null) return false; // 已被别的老师抓着
            return true;
        }

        private bool IsObstructed(Vector3 origin, Vector3 dir, float dist, Unit target)
        {
            // 层掩码与粉笔实际碰撞一致（ChalkColliderMask：排除 Interaction/Player/PlayerSelf）
            RaycastHit[] hits = Physics.RaycastAll(origin, dir, dist, ConstantDefine.ChalkColliderMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Transform t = hits[i].collider.transform;
                if (t.IsChildOf(target.transform)) continue;
                if (t.IsChildOf(owner.transform)) continue; // 自己的模型不算遮挡
                return true;
            }
            return false;
        }

        /// <summary>单学生可见性：距离 + 视锥（贴脸 2.5m 内无视角度）+ 射线遮挡</summary>
        private bool IsVisible(Unit player)
        {
            Vector3 eye = owner.Position + Vector3.up * 1.6f;
            Transform headRoot = player.GetPartRoot(EUnitPartType.HeadPosition);
            Vector3 head = (headRoot != null) ? headRoot.position : (player.Position + Vector3.up * 1.4f);
            Vector3 to = head - eye;
            float dist = to.magnitude;
            if (dist < 0.001f || dist > Cfg.visionDistance) return false;
            Vector3 forward = owner.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            forward.Normalize();
            float cosHalf = Mathf.Cos(Mathf.Clamp(Cfg.visionAngleDeg, 10f, 179f) * 0.5f * Mathf.Deg2Rad);
            if (dist > 2.5f && Vector3.Dot(forward, to / dist) < cosHalf) return false;
            if (IsObstructed(eye, to / dist, dist, player)) return false;
            return true;
        }

        /// <summary>
        /// 目标获取（证据驱动）：视野内违规（手持捣蛋物品/玩电脑/上课离座）或听见近期捣蛋才追。
        /// 坐着学习/纯路过的无辜学生不追——原逻辑"看见就追"显得莽撞。
        /// 离座在操场地图不算（学生本来就没座位）。
        /// 协同开启时跳过已被其他 AI 认领的目标。
        /// </summary>
        private Unit AcquireTarget(out string reason)
        {
            reason = null;
            Unit best = null;
            float bestDist = float.MaxValue;
            bool leaveSeatEnabled = Cfg.observeLeaveSeat && !AiTeacherManager.IsPlaygroundFloor();
            foreach (Unit player in UnitManager.Instance.playerList)
            {
                if (!IsValidStudent(player)) continue;
                bool heard = AiTeacherManager.TryHearTrick(player.UID, owner.Position, Cfg.hearingDistance, Cfg.suspicionMemory, out _);
                bool violating = Cfg.observeTrickItems && AiTeacherManager.IsViolating(player.UID);
                bool outSeat = leaveSeatEnabled && AiTeacherManager.IsOutOfSeat(player.UID, Cfg.leaveSeatGrace);
                bool needVision = violating || outSeat;
                bool visible = needVision && IsVisible(player);
                if (violating && !visible) violating = false;
                if (outSeat && !visible) outSeat = false;
                if (!heard && !violating && !outSeat) continue; // 无嫌疑，放过
                if (Cfg.cooperate && AiTeacherManager.IsClaimedByOther(player.UID, owner.UID)) continue;
                float distSq = (player.Position - owner.Position).sqrMagnitude;
                if (distSq < bestDist)
                {
                    best = player;
                    bestDist = distSq;
                    reason = violating ? "发现违规（手持捣蛋物品/玩电脑）"
                        : outSeat ? "发现违规（上课离座）"
                        : "听见捣蛋";
                }
            }
            return best;
        }

        private void RememberSeen(Unit target)
        {
            lastSeenPos = target.Position;
            lastSeenAt = Time.unscaledTime;
        }

        // ===== 阶段：巡逻 =====

        /// <summary>
        /// 课前倒计时：像真人老师一样从办公室走进教室站好位（这段时间只走位不抓人）。
        /// 否则整段倒计时都站在办公室，开课后还要再走过去，前半节课等于没有老师。
        /// </summary>
        private void TickPrepareMove()
        {
            // 教室中心还没定位（学生没坐齐）时退到讲台锚点：它出生时就是按座位区质心算的
            if (!AiTeacherManager.TryGetClassCenter(out Vector3 classCenter)
                && !AiTeacherManager.TryGetPodium(out classCenter))
            {
                StopAgent();
                SetMoving(false);
                return;
            }
            if (DistXZ(owner.Position, classCenter) <= EnterClassroomRadius)
            {
                enteredClassroom = true;
                prepDestValid = false;
                StopAgent();
                SetMoving(false);
                return;
            }
            float now = Time.unscaledTime;
            if (!prepDestValid || Reached(prepDest, 1.2f) || now >= prepDestUntil)
            {
                prepDest = PickClassroomPoint(classCenter);
                prepDestUntil = now + Cfg.patrolTimeout;
                prepDestValid = true;
                ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）课前走向教室 {prepDest}");
            }

            // 课前卡死自救：路线被门/家具堵住，4 秒没挪出 0.35m 就直接传进教室（学生已经在教室等着了）
            if (DistXZ(owner.Position, prepStuckRefPos) > 0.35f)
            {
                prepStuckRefPos = owner.Position;
                prepStuckSince = now;
            }
            else if (now - prepStuckSince > 4f)
            {
                prepStuckSince = now;
                prepStuckRefPos = owner.Position;
                if (DistXZ(owner.Position, prepDest) <= 80f && Mathf.Abs(prepDest.y - owner.Position.y) <= 3f)
                {
                    ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）课前走不进教室，直接传送至 {prepDest}");
                    if (agent != null && agent.enabled && agent.isOnNavMesh)
                    {
                        agent.Warp(prepDest);
                    }
                    else
                    {
                        owner.SetPosition(prepDest);
                    }
                    return;
                }
            }
            MoveTowards(prepDest);
        }

        private void TickPatrol()
        {
            Unit target = AcquireTarget(out string reason);
            if (target != null)
            {
                BeginChase(target, reason);
                return;
            }

            float now = Time.unscaledTime;

            // 出生在办公室/玄关：先走进教室。教室外的热点巡逻和原地兜底随机点都只在出生点附近打转，
            // 老师一整节课都出不了办公室。进过一次教室后就不再强制（热点巡逻可以出教室追）。
            Vector3 classCenter = default;
            bool classCenterKnown = AiTeacherManager.TryGetClassCenter(out classCenter);
            bool farFromClass = !enteredClassroom && classCenterKnown
                && DistXZ(owner.Position, classCenter) > EnterClassroomRadius;
            if (farFromClass)
            {
                forceClassroomPoint = true;
                patrolDwellUntil = 0f; // 去教室路上不驻足
                if (!hasPatrolPoint || now >= patrolLegUntil)
                {
                    patrolPoint = PickClassroomPoint(classCenter);
                    patrolLegUntil = now + Cfg.patrolTimeout;
                    hasPatrolPoint = true;
                }
                if (!enterClassLogged)
                {
                    enterClassLogged = true;
                    ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）在教室外，前往教室 {classCenter}");
                }
            }
            else
            {
                if (classCenterKnown) enteredClassroom = true;
                if (forceClassroomPoint)
                {
                    forceClassroomPoint = false;
                    enterClassLogged = false;
                    ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）已到教室区域 {owner.Position}");
                }
            }
            if (now < patrolDwellUntil)
            {
                // 驻足观察：站在巡逻点上看一会再走，像老师在巡视而不是赶路
                StopAgent();
                SetMoving(false);
                return;
            }

            bool arrived = hasPatrolPoint && Reached(patrolPoint, 1.0f);
            if (arrived)
            {
                patrolPoint = PickPatrolPoint();
                patrolLegUntil = now + Cfg.patrolTimeout;
                hasPatrolPoint = true;
                float dwell = Mathf.Max(0.4f, Cfg.patrolDwell);
                patrolDwellUntil = now + UnityEngine.Random.Range(dwell * 0.6f, dwell * 1.4f);
                StopAgent();
                SetMoving(false);
                return;
            }

            // 卡死自救：巡逻想走但 3.5 秒没挪出 0.35m（路径失败、楔进家具缝等）。
            // 传送必须谨慎：目标超过 12m 或跨层（|Δy|>2m）绝不传送——校舍地图乱传会把老师传丢
            if (hasPatrolPoint)
            {
                if (DistXZ(owner.Position, patrolStuckRefPos) > 0.35f)
                {
                    patrolStuckRefPos = owner.Position;
                    patrolStuckSince = now;
                }
                else if (now - patrolStuckSince > 3.5f)
                {
                    patrolStuckSince = now;
                    patrolStuckRefPos = owner.Position;
                    // 传送保险：≤12m 随便传；更远的点只有落在学生聚集区（本教室，30m 内）才允许传——
                    // 校舍地图办公室离教室很远，去教室的路若走不通只能传，但绝不传去别的楼
                    bool inClassroom = AiTeacherManager.TryGetStudentCentroid(out Vector3 studentCenter)
                        && (patrolPoint - studentCenter).sqrMagnitude <= 900f;
                    bool warpOk = Mathf.Abs(patrolPoint.y - owner.Position.y) <= 2f
                        && (DistXZ(owner.Position, patrolPoint) <= 12f || (inClassroom && DistXZ(owner.Position, patrolPoint) <= 60f));
                    if (warpOk)
                    {
                        patrolLegUntil = now + Cfg.patrolTimeout;
                        ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）巡逻卡住，传送至巡逻点 {patrolPoint}");
                        if (agent != null && agent.enabled && agent.isOnNavMesh)
                        {
                            agent.Warp(patrolPoint);
                        }
                        else
                        {
                            owner.SetPosition(patrolPoint);
                        }
                    }
                    else
                    {
                        // 目标太远/跨层/不在教室区：八成是选到了别的楼，换一个近的巡逻点重新走
                        patrolPoint = PickPatrolPoint();
                        patrolLegUntil = now + Cfg.patrolTimeout;
                    }
                    return;
                }
            }

            bool timedOut = hasPatrolPoint && now >= patrolLegUntil;
            if (!hasPatrolPoint || timedOut)
            {
                patrolPoint = PickPatrolPoint();
                patrolLegUntil = now + Cfg.patrolTimeout;
                hasPatrolPoint = true;
                if (timedOut)
                {
                    // 超时放弃旧点：驻足片刻再出发，像老师犹豫了一下换了个方向
                    float dwell2 = Mathf.Max(0.4f, Cfg.patrolDwell);
                    patrolDwellUntil = now + UnityEngine.Random.Range(dwell2 * 0.6f, dwell2 * 1.4f);
                    StopAgent();
                    SetMoving(false);
                    return;
                }
            }
            MoveTowards(patrolPoint);
        }

        private Vector3 PickPatrolPoint()
        {
            // 老师还在教室外：目标一律定在教室里，别在办公室附近转
            if (forceClassroomPoint && AiTeacherManager.TryGetClassCenter(out Vector3 forceCenter))
            {
                return PickClassroomPoint(forceCenter);
            }

            // 热点巡逻：一半概率去最近 20 秒内的捣蛋事发地附近转（捣蛋王的座位区会被反复照顾）
            if (Cfg.hotspotPatrol && UnityEngine.Random.value < 0.5f
                && AiTeacherManager.TryGetLatestTrickPoint(20f, out Vector3 hotspot)
                && NavMesh.SamplePosition(hotspot + InsideCircle(3f), out var hitHot, 1.5f, NavMesh.AllAreas))
            {
                return hitHot.position;
            }

            // 座位区巡视：以本班教室为中心找座位——教室定位优先用"有学生坐着的椅子"质心
            //（单个真人学生到处晃会带偏学生质心，校舍地图教室可能离办公室 45m+）
            if (AiTeacherManager.TryGetClassCenter(out Vector3 classCenter))
            {
                List<Vector3> seats = AiTeacherManager.GetSeatPointsNear(classCenter, 30f);
                if (seats.Count > 0)
                {
                    for (int i = 0; i < 6; i++)
                    {
                        Vector3 seat = seats[UnityEngine.Random.Range(0, seats.Count)];
                        if (NavMesh.SamplePosition(seat + RingOffset(0.9f, 1.6f), out var hit, 1.2f, NavMesh.AllAreas)
                            && DistXZ(hit.position, seat) > 0.55f)
                        {
                            ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）巡逻 → 教室座位旁 {hit.position}");
                            return hit.position;
                        }
                    }
                }
                else if (TryGetStudentWatchPoint(out Vector3 watch))
                {
                    // 本班没有椅子（操场等）：点名式巡视——走向随机学生身边看一眼
                    ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）巡逻 → 走近学生 {watch}");
                    return watch;
                }
            }

            // 偶尔回讲台（巡逻原点，路线有收有放）
            if (UnityEngine.Random.value < 0.25f && AiTeacherManager.TryGetPodium(out Vector3 podium)
                && NavMesh.SamplePosition(podium + InsideCircle(1.2f), out var hitPodium, 1.2f, NavMesh.AllAreas))
            {
                return hitPodium.position;
            }

            // 兜底：当前位置周围随机
            for (int i = 0; i < 6; i++)
            {
                Vector3 candidate = owner.Position + InsideCircle(Cfg.patrolRadius);
                if (NavMesh.SamplePosition(candidate, out var hit, 1.5f, NavMesh.AllAreas))
                {
                    return hit.position;
                }
            }
            return owner.Position; // 采样失败就原地站桩
        }

        /// <summary>教室内的落脚点：座位旁走道优先（能巡视），其次讲台，最后教室中心</summary>
        private Vector3 PickClassroomPoint(Vector3 classCenter)
        {
            // 先只取教室尺寸内的座位（8m）：校舍地图全校椅子都叫 Chair，30m 会选到隔壁房间
            List<Vector3> seats = AiTeacherManager.GetSeatPointsNear(classCenter, 8f);
            if (seats.Count == 0) seats = AiTeacherManager.GetSeatPointsNear(classCenter, 30f);
            for (int i = 0; i < 6 && seats.Count > 0; i++)
            {
                Vector3 seat = seats[UnityEngine.Random.Range(0, seats.Count)];
                if (NavMesh.SamplePosition(seat + RingOffset(1.2f, 2.2f), out var hit, 1.5f, NavMesh.AllAreas)
                    && DistXZ(hit.position, seat) > 0.55f)
                {
                    return hit.position;
                }
            }
            if (AiTeacherManager.TryGetPodium(out Vector3 podium) && DistXZ(podium, classCenter) <= 12f
                && NavMesh.SamplePosition(podium, out var hitPodium, 2f, NavMesh.AllAreas))
            {
                return hitPodium.position;
            }
            if (NavMesh.SamplePosition(classCenter, out var hitCenter, 2.5f, NavMesh.AllAreas))
            {
                return hitCenter.position;
            }
            return classCenter;
        }

        /// <summary>操场巡视点：随机挑一个学生，走向 TA 身边 1.2~2.2m 处</summary>
        private static bool TryGetStudentWatchPoint(out Vector3 watch)
        {
            watch = default;
            if (!UnitManager.IsSingletonActive()) return false;
            var um = UnitManager.Instance;
            Unit pick = null;
            foreach (Unit player in um.playerList)
            {
                if (player != null && player.isPlayer && player.IsStudent && player.isActived && !player.IsDead
                    && player.CurState() != 103)
                {
                    pick = player;
                    if (UnityEngine.Random.value < 0.35f) break; // 别总挑第一个
                }
            }
            if (pick == null)
            {
                foreach (Unit value in um.unitsDict.Values)
                {
                    if (value != null && value.isActived && value.UnitSubType == EUnitSubType.Student_NPC)
                    {
                        pick = value;
                        if (UnityEngine.Random.value < 0.15f) break;
                    }
                }
            }
            if (pick == null) return false;
            if (NavMesh.SamplePosition(pick.Position + RingOffset(1.2f, 2.2f), out var hit, 2f, NavMesh.AllAreas))
            {
                watch = hit.position;
                return true;
            }
            return false;
        }

        /// <summary>环形偏移：min~max 距离的随机水平偏移（比满盘随机更容易落在走道而不是座位上）</summary>
        private static Vector3 RingOffset(float min, float max)
        {
            float angle = UnityEngine.Random.value * Mathf.PI * 2f;
            float r = UnityEngine.Random.Range(min, max);
            return new Vector3(Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);
        }

        private static Vector3 InsideCircle(float radius)
        {
            Vector2 circle = UnityEngine.Random.insideUnitCircle * radius;
            return new Vector3(circle.x, 0f, circle.y);
        }

        // ===== 阶段：调查（跟丢后到事发地翻找，两个查看点）=====

        private void TickInvestigate()
        {
            Unit target = AcquireTarget(out string reason);
            if (target != null)
            {
                BeginChase(target, reason);
                return;
            }
            if (investigateStage == 0 && (Reached(investigatePos, 1.2f) || Time.unscaledTime >= investigateUntil))
            {
                // 第一个查看点结束：去附近第二个点再看一眼
                investigateStage = 1;
                investigateStageAt = Time.unscaledTime;
                investigatePos = lastSeenPos + InsideCircle(2.5f);
                return;
            }
            if (investigateStage == 1 && (Reached(investigatePos, 1.2f) || Time.unscaledTime - investigateStageAt > 3f))
            {
                state = AiState.Patrol;
                investigateStage = 0;
                hasPatrolPoint = false;
                SetMoving(false);
                return;
            }
            MoveTowards(investigatePos);
        }

        // ===== 阶段：追击/攻击/抓捕 =====

        private void BeginChase(Unit target, string reason)
        {
            chaseTarget = target;
            state = AiState.Chase;
            investigateStage = 0;
            RememberSeen(target);
            if (Cfg.cooperate) AiTeacherManager.ClaimTarget(target.UID, owner.UID);
            nextActionAt = Time.unscaledTime + Cfg.reactionDelay; // 发现到行动的反应延迟
            ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）锁定目标 UID={target.UID}（{reason ?? "发现异常"}）");
        }

        private void TickChase()
        {
            if (chaseTarget == null || !IsValidStudent(chaseTarget))
            {
                AiTeacherManager.ReleaseTarget(chaseTarget != null ? chaseTarget.UID : 0L, owner.UID);
                ReleaseFocus("目标失效");
                // 目标失效：若近内有别的线索就接上，否则去调查最后目击点
                Unit next = AcquireTarget(out string reason);
                if (next != null)
                {
                    chaseTarget = next;
                    RememberSeen(next);
                    if (Cfg.cooperate) AiTeacherManager.ClaimTarget(next.UID, owner.UID);
                }
                else if (Time.unscaledTime - lastSeenAt < Cfg.suspicionMemory + 2f && lastSeenPos != Vector3.zero)
                {
                    investigatePos = lastSeenPos;
                    investigateUntil = Time.unscaledTime + 3f;
                    investigateStage = 0;
                    state = AiState.Investigate;
                    chaseTarget = null;
                    SetMoving(false);
                }
                else
                {
                    state = AiState.Patrol;
                    chaseTarget = null;
                    SetMoving(false);
                }
                return;
            }

            Vector3 headPos = GetHead(chaseTarget);
            Vector3 toTarget = headPos - (owner.Position + Vector3.up * 1.5f);
            float dist = toTarget.magnitude;
            bool visible = IsVisible(chaseTarget);
            bool heard = AiTeacherManager.TryHearTrick(chaseTarget.UID, owner.Position, Cfg.hearingDistance, Cfg.suspicionMemory, out _);
            bool violating = Cfg.observeTrickItems && AiTeacherManager.IsViolating(chaseTarget.UID);
            bool outSeat = Cfg.observeLeaveSeat && !AiTeacherManager.IsPlaygroundFloor()
                && AiTeacherManager.IsOutOfSeat(chaseTarget.UID, Cfg.leaveSeatGrace);

            // 证据刷新：可见/可闻/持续违规都算；认领同步续期
            if (visible || heard || violating || outSeat)
            {
                RememberSeen(chaseTarget);
                if (Cfg.cooperate) AiTeacherManager.ClaimTarget(chaseTarget.UID, owner.UID);
            }

            // 追击卡死自救：想追但 3.5 秒没挪出 0.35m（最后几米路径不通、被桌椅卡住等）。
            // 贴身传送到目标旁边——凝视/晕眩的目标本来就跑不了，传送是唯一可靠的开抓手段；
            // 太远（>15m）或跨层传不了 → 放弃去调查，别把老师丢在自己到不了的地方
            if (DistXZ(owner.Position, chaseStuckRefPos) > 0.35f)
            {
                chaseStuckRefPos = owner.Position;
                chaseStuckSince = Time.unscaledTime;
            }
            else if (Time.unscaledTime - chaseStuckSince > 3.5f)
            {
                chaseStuckSince = Time.unscaledTime;
                chaseStuckRefPos = owner.Position;
                bool canWarpClose = dist <= 15f
                    && Mathf.Abs(chaseTarget.Position.y - owner.Position.y) <= 2f;
                NavMeshHit warpHit = default;
                if (canWarpClose)
                {
                    canWarpClose = NavMesh.SamplePosition(chaseTarget.Position + RingOffset(1.2f, 2.2f), out warpHit, 2f, NavMesh.AllAreas)
                        && Mathf.Abs(warpHit.position.y - owner.Position.y) <= 2f;
                }
                if (canWarpClose)
                {
                    ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）追击卡住，贴身传送至目标 UID={chaseTarget.UID} 附近 {warpHit.position}");
                    if (agent != null && agent.enabled && agent.isOnNavMesh)
                    {
                        agent.Warp(warpHit.position);
                    }
                    else
                    {
                        owner.SetPosition(warpHit.position);
                    }
                    return;
                }
                ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）追不上目标 UID={chaseTarget.UID}，放弃去调查");
                AiTeacherManager.ReleaseTarget(chaseTarget.UID, owner.UID);
                ReleaseFocus("追不上");
                investigatePos = lastSeenPos;
                investigateUntil = Time.unscaledTime + 2f;
                investigateStage = 0;
                state = AiState.Investigate;
                chaseTarget = null;
                SetMoving(false);
                return;
            }

            // 打晕才抓：原版抓捕服务端不校验晕眩也不校验距离（全靠客户端自觉），
            // AI 讲武德：必须打晕 + 走到跟前（抓距封顶 2.5m，防房间规则抓距过大把人隔空拽过来）才拎
            if (chaseTarget.IsStun && dist <= Mathf.Min(ConfigManager.TeacherSetting.arrestDistance, 2.5f))
            {
                TryCatch(chaseTarget);
                return;
            }

            // 凝视：看得见但够不着的站立目标 → 冻结防跑（坐着/挂柱的学生 FSM 锁不上，跳过）
            if (focusedUid == 0L && !chaseTarget.IsStun && visible && chaseTarget.CurState() != 100
                && dist <= ConfigManager.TeacherSetting.focusDistance
                && Time.unscaledTime >= nextFocusAt
                && skill.currentSkillEnergy > Cfg.catchMinEnergy + ConfigManager.TeacherSetting.focusEnergyCost
                && skill.UseSkillEnergy(ConfigManager.TeacherSetting.focusEnergyCost))
            {
                SendToSkillHandler(new UseSkillMessageRequest_C2S
                {
                    skillID = 2,
                    targetUID = chaseTarget.UID,
                    userUID = owner.UID
                });
                focusedUid = chaseTarget.UID;
                focusedUntil = Time.unscaledTime + Cfg.focusMaxSeconds;
                nextFocusAt = Time.unscaledTime + Cfg.focusMaxSeconds + 3f;
                ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）凝视学生 UID={chaseTarget.UID}");
            }

            // 攻击：被凝视的目标是冻住的活靶子更要打（打晕才能抓）；火线被桌椅挡住就别打（省体力），贴过去再打。
            // 剑老师近战但剑气本身有射程，攻击距离放到 4m；粉笔老师按视野距离打
            float attackRange = (owner.ID == 1002) ? 4f : Cfg.visionDistance * 0.9f;
            bool inSight = dist <= 2.5f || visible;
            if (Time.unscaledTime >= nextAttackAt && inSight && dist <= attackRange && skill.currentSkillEnergy > Cfg.catchMinEnergy)
            {
                if (owner.ID == 1002)
                {
                    if (IsFireLineClear(chaseTarget, headPos))
                    {
                        TrySword(headPos);
                    }
                }
                else
                {
                    float chalkSpeed = ResolveChalkSpeed();
                    Vector3 aimPos = headPos;
                    if (Cfg.chalkLeadFactor > 0.01f)
                    {
                        var force = chaseTarget.GetComponent<ForceComponent>();
                        Vector3 velocity = force != null ? force.Velocity : Vector3.zero;
                        velocity.y = 0f;
                        if (velocity.sqrMagnitude > 0.01f)
                        {
                            float flyTime = dist / Mathf.Max(1f, chalkSpeed);
                            aimPos += velocity * (flyTime * Cfg.chalkLeadFactor);
                        }
                    }
                    if (IsFireLineClear(chaseTarget, aimPos))
                    {
                        TryChalk(aimPos, chalkSpeed);
                    }
                }
                if (Time.unscaledTime < nextAttackAt) return; // 攻击成功，进入冷却
            }

            // 追：看得见/听得见就贴身，否则追最后目击点；证据彻底过期 → 放弃去调查
            if (Time.unscaledTime - lastSeenAt < 2.5f)
            {
                MoveTowards(chaseTarget.Position);
            }
            else
            {
                MoveTowards(lastSeenPos);
                if (Reached(lastSeenPos, 1.2f))
                {
                    investigatePos = lastSeenPos;
                    investigateUntil = Time.unscaledTime + 2f;
                    investigateStage = 0;
                    state = AiState.Investigate;
                    AiTeacherManager.ReleaseTarget(chaseTarget.UID, owner.UID);
                    ReleaseFocus("跟丢");
                    chaseTarget = null;
                    SetMoving(false);
                }
            }
        }

        // ===== 抓捕与挂柱 =====

        private void TryCatch(Unit student)
        {
            if (catchComp == null || catchComp.catchedUnitUID != 0L) return;
            if (skill.currentSkillEnergy <= 0f) return; // 对齐原版 CheckCatchEnergy：>0 即可抓
            if (Time.unscaledTime < nextCatchAt) return; // 抓捕请求节流，防服务器拒绝时刷屏
            nextCatchAt = Time.unscaledTime + 1f;
            // 被凝视锁 FSM 的学生进不了 BeCatched 态：先解除（同连接消息有序，学生端先收到解锁再收到抓捕）
            if (focusedUid == student.UID)
            {
                ReleaseFocus("转抓捕");
            }
            var request = new UseSkillMessageRequest_C2S
            {
                skillID = 3,
                targetUID = student.UID,
                userUID = owner.UID
            };
            SendToSkillHandler(request);
            chaseTarget = null; // 目标已在手里，交给挂柱流程；同时停止追击加速
            ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）抓捕学生 UID={student.UID}");
        }

        /// <summary>解除凝视：TeacherStopWatch 响应会把老师 returnView 置真，被凝视学生的 FSM 随即解锁</summary>
        private void ReleaseFocus(string why)
        {
            if (focusedUid == 0L) return;
            ulong uid = focusedUid;
            focusedUid = 0L;
            if (!MonoSingletonT<NetManager>.Instance.IsHost()) return; // AI 仅房主侧驱动
            var connection = MonoSingletonT<RoomManager>.Instance.GetConnectByPlayerUID(MonoSingletonT<UnitManager>.Instance.localPlayerUID);
            MonoSingletonT<RoomManager>.Instance.GetSkillMessageHandler().OnTeacherStopWatchMessageRequest(connection, new TeacherStopWatchMessageRequest_C2S
            {
                teacherUID = owner.UID
            });
            ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）解除凝视 UID={uid}（{why}）");
        }

        private void TickBindPillar()
        {
            Unit student = UnitManager.Instance.GetUnitByUID(catchComp.catchedUnitUID);
            if (student == null) return; // 响应链会自行放人

            Unit bestPillar = null;
            float bestDist = float.MaxValue;
            foreach (Unit value in UnitManager.Instance.unitsDict.Values)
            {
                if (value == null || !value.isActived) continue;
                var pillar = value.GetComponent<PunishPillarComponent>();
                if (pillar == null || pillar.bindStudent != null) continue;
                float dist = (value.Position - owner.Position).sqrMagnitude;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestPillar = value;
                }
            }

            if (bestPillar == null)
            {
                // 没有空柱子：等一会，还不行就放下学生
                if (noPillarSince <= 0f) noPillarSince = Time.unscaledTime;
                if (Time.unscaledTime - noPillarSince > 8f)
                {
                    ModEntry.Log?.Invoke("[状态] AI 老师：无空罚站柱，放下学生");
                    skill.DropStudent();
                    noPillarSince = 0f;
                    state = AiState.Patrol;
                    chaseTarget = null;
                    hasPatrolPoint = false;
                }
                return;
            }
            noPillarSince = 0f;

            MoveTowards(bestPillar.Position);
            if (DistXZ(bestPillar.Position, owner.Position) <= 1.8f && catchComp.CanReleaseOrBindStudent)
            {
                var request = new TeacherBindStudentMessageRequest_C2S
                {
                    teacherUID = owner.UID,
                    studentUID = catchComp.catchedUnitUID,
                    pillarUID = bestPillar.UID
                };
                SendBindRequest(request);
                ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）把学生 UID={catchComp.catchedUnitUID} 挂上罚站柱 UID={bestPillar.UID}");
                state = AiState.Patrol;
                chaseTarget = null;
                hasPatrolPoint = false;
            }
        }

        // ===== 攻击 =====

        private void TryChalk(Vector3 targetHead, float chalkSpeed)
        {
            Vector3 origin = owner.Position + Vector3.up * 1.5f;
            Vector3 dir = (targetHead - origin);
            if (dir.sqrMagnitude < 0.001f) return; // 先校验再扣体力，避免白扣
            if (!skill.TryUseAttackEnergy(ERoomRuleId.TeacherFBXHTI)) return;
            animComp?.PlayAnim("Throw3");
            dir.Normalize();
            dir = ApplySpread(dir, Vector3.Distance(targetHead, origin));

            Unit projectile = UnitManager.Instance.CreateCommonUnitDirectly(4002, origin, Vector3.zero, (ref UnitDataNet arg) =>
            {
                arg.CreaterUID = owner.UID;
                arg.ShootForward = dir;
                arg.ShootSpeed = chalkSpeed;
                arg.EffectDuration = ConfigManager.TeacherSetting.chalkLifetime;
                arg.IgnoreSync = true;
            });
            if (projectile == null) return;
            projectile.GetComponent<ProjectileComponent>()?.TrackChalkAchievement(owner.UID);
            ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）投粉笔 → 瞄准 {targetHead}");
            var sound = new PlaySoundRequest
            {
                AudioName = AudioEvents.sfx_ChalkThrow_sfx,
                IsTrickVoice = false,
                PlaySoundType = EPlaySoundType.OneShoot,
                Position = owner.Position,
                UserUID = owner.UID
            };
            MonoSingletonT<AudioManager>.Instance.PlayOneShot(AudioEvents.sfx_ChalkThrow_sfx, owner.Position, sound);
            float ruleCd = SingletonT<RoomRuleDataManager>.Instance.GetCurrentFloatValue(ERoomRuleId.UseFBCD);
            nextAttackAt = Time.unscaledTime + Mathf.Max(Cfg.attackCooldown, ruleCd);
        }

        private void TrySword(Vector3 targetHead)
        {
            var gameSetting = ConfigManager.GameSetting; // cfg.GameSetting，var 避免引用其命名空间
            Vector3 origin = owner.Position + Vector3.up * ConfigManager.TeacherSetting.meleeHeight;
            Vector3 dir = (targetHead - origin);
            if (dir.sqrMagnitude < 0.001f) return; // 先校验再扣体力，避免白扣
            if (!skill.TryUseAttackEnergy(ERoomRuleId.TeacherHorizontalEnergyCost)) return;
            animComp?.PlayAnim("Throw1");
            dir.Normalize();
            origin += dir * Mathf.Max(0f, gameSetting.TeacherSlashLaunchForwardOffset);

            var request = new TeacherSlashFire_C2S
            {
                sequence = TeacherSlashProjectile.NextSequence(),
                vertical = false,
                origin = origin,
                direction = dir
            };
            // 注意：不能走 UnitMessageHandler.OnTeacherSlashFire——TeacherSlashSender 按 connection
            // 解析老师，AI 没有连接会被解析成房主本人。直调 AI unit 自己的组件：
            // HandleSlashFire 内部校验（序列/距离/体力）+ 广播 TeacherSlashFired_S2C，全端弹道确定性模拟
            var melee = owner.GetComponent<TeacherMeleeComponent>();
            if (melee == null)
            {
                ModEntry.Log?.Invoke("[警告] AI 老师挥剑失败：TeacherMeleeComponent 缺失");
            }
            else
            {
                // 剑模型/配置来自老师模型 prefab，没绑上会被 HandleSlashFire 静默拒绝——打日志定位
                if (AccessTools.Field(typeof(TeacherMeleeComponent), "sword")?.GetValue(melee) == null
                    || AccessTools.Field(typeof(TeacherMeleeComponent), "config")?.GetValue(melee) == null)
                {
                    ModEntry.Log?.Invoke("[警告] AI 老师挥剑异常：剑模型(sword)或近战配置(config)未绑定，剑气会被拒绝");
                }
                ModEntry.Log?.Invoke($"[状态] AI 老师（UID={owner.UID}）挥剑 → 起点 {origin} 方向 {dir}");
            }
            melee?.HandleSlashFire(request);
            float ruleCd = SingletonT<RoomRuleDataManager>.Instance.GetCurrentFloatValue(ERoomRuleId.UseSwordCD);
            nextAttackAt = Time.unscaledTime + Mathf.Max(Cfg.attackCooldown, ruleCd);
        }

        /// <summary>粉笔飞行速度：对齐原版 ProjectileThrowSpeed——操场地图用操场专用速度</summary>
        private static float ResolveChalkSpeed()
        {
            return AiTeacherManager.IsPlaygroundFloor()
                ? ConfigManager.TeacherSetting.playgroundChalkSpeed
                : ConfigManager.TeacherSetting.chalkSpeed;
        }

        /// <summary>火线检查：用与粉笔一致的碰撞层（ChalkColliderMask）预演射线，桌椅/其他学生挡道就不打</summary>
        private bool IsFireLineClear(Unit target, Vector3 aimPos)
        {
            Vector3 origin = owner.Position + Vector3.up * 1.5f;
            Vector3 to = aimPos - origin;
            float dist = to.magnitude;
            if (dist < 0.01f) return false;
            return !IsObstructed(origin, to / dist, dist, target);
        }

        private Vector3 ApplySpread(Vector3 dir, float dist)
        {
            // 贴脸必中、远处才散布：按距离线性放大到配置值
            float spread = Mathf.Max(0f, Cfg.chalkSpreadDeg) * Mathf.Clamp01(dist / 8f);
            if (spread <= 0.01f) return dir;
            Quaternion rot = Quaternion.Euler(
                UnityEngine.Random.Range(-spread, spread) * 0.5f,
                UnityEngine.Random.Range(-spread, spread),
                0f);
            return rot * dir;
        }

        // ===== 通用 =====

        private void RegenerateEnergy(float dt)
        {
            float max = ConfigManager.GameSetting.TeacherMaxSkillEnergy;
            if (skill.currentSkillEnergy < max)
            {
                skill.AddSkillEnergy(Mathf.Min(Cfg.energyRegenPerSecond * dt, max - skill.currentSkillEnergy));
            }
        }

        /// <summary>
        /// 教室 NavMesh 可能是 NPC 学生生成时才动态烘焙的，AI 出生时未必就绪——
        /// 每秒重试一次：不在 NavMesh 上就采样当前位置尝试启用/Warp。
        /// </summary>
        private void EnsureAgent()
        {
            if (agent == null) return;
            float now = Time.unscaledTime;
            if (now < nextAgentCheck) return;
            nextAgentCheck = now + 1f;
            if (agent.enabled && agent.isOnNavMesh) return;
            if (!NavMesh.SamplePosition(owner.transform.position, out var hit, 2.5f, NavMesh.AllAreas)) return;
            agent.enabled = true;
            agent.Warp(hit.position);
        }

        /// <summary>
        /// 基础移速：配置 0 = 跟随房间规则 TeacherSpeed（与真人老师同速，追得上是"不笨"的前提）。
        /// </summary>
        private float ResolveBaseSpeed()
        {
            float now = Time.unscaledTime;
            if (now - baseSpeedAt > 1f)
            {
                baseSpeedAt = now;
                if (Cfg.moveSpeed > 0.01f)
                {
                    baseSpeedCache = Cfg.moveSpeed;
                }
                else
                {
                    float rule = SingletonT<RoomRuleDataManager>.Instance.GetCurrentFloatValue(ERoomRuleId.TeacherSpeed);
                    baseSpeedCache = rule > 0.1f ? rule : 3.2f;
                }
            }
            return baseSpeedCache;
        }

        /// <summary>当前 agent 速度：追击状态乘冲刺倍率（抓捕冲刺也在 Chase 态内）</summary>
        private float CurrentAgentSpeed()
        {
            bool sprinting = state == AiState.Chase && chaseTarget != null;
            return ResolveBaseSpeed() * (sprinting ? Mathf.Max(1f, Cfg.chaseSpeedBoost) : 1f);
        }

        private void MoveTowards(Vector3 dest)
        {
            float speed = CurrentAgentSpeed();
            if (agent != null && agent.enabled && agent.isOnNavMesh)
            {
                if (Mathf.Abs(agent.speed - speed) > 0.01f) agent.speed = speed;
                if (!agent.hasPath || (agent.destination - dest).sqrMagnitude > 0.25f)
                {
                    agent.isStopped = false;
                    agent.SetDestination(dest);
                }
                SetMoving(agent.velocity.sqrMagnitude > 0.04f);
            }
            else
            {
                // 无 NavMesh 兜底：host 权威直线位移（pose 经 SyncTransform 原生链自动同步）
                Vector3 pos = owner.transform.position;
                Vector3 delta = dest - pos;
                delta.y = 0f;
                float step = speed * Time.deltaTime;
                if (delta.magnitude > step) delta = delta.normalized * step;
                if (delta.sqrMagnitude > 0.000001f)
                {
                    owner.SetPosition(pos + delta);
                    SetMoving(true);
                }
                else
                {
                    SetMoving(false);
                }
            }
        }

        private void StopAgent()
        {
            if (agent != null && agent.enabled && agent.isOnNavMesh && !agent.isStopped)
            {
                agent.isStopped = true;
            }
        }

        private void SetMoving(bool moving)
        {
            if (animComp == null || moving == movingAnim) return;
            movingAnim = moving;
            animComp.PlayAnimSync("IsMoving", ESyncAnimType.Bool, moving.ToString());
        }

        private static Vector3 GetHead(Unit unit)
        {
            Transform root = unit.GetPartRoot(EUnitPartType.HeadPosition);
            return (root != null) ? root.position : (unit.Position + Vector3.up * 1.4f);
        }

        private bool Reached(Vector3 pos, float tolerance)
        {
            return DistXZ(pos, owner.Position) <= tolerance;
        }

        private static float DistXZ(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        // ===== 房主本地直调消息入口（同 SkillComponent.DealCallEffect 的 host 惯例）=====

        private static void SendToSkillHandler(UseSkillMessageRequest_C2S request)
        {
            if (MonoSingletonT<NetManager>.Instance.IsHost())
            {
                var connection = MonoSingletonT<RoomManager>.Instance.GetConnectByPlayerUID(MonoSingletonT<UnitManager>.Instance.localPlayerUID);
                MonoSingletonT<RoomManager>.Instance.GetSkillMessageHandler().OnUseSkillMessageRequest(connection, request);
            }
            else
            {
                MonoSingletonT<RoomManager>.Instance.SendToServer(request);
            }
        }

        private static void SendBindRequest(TeacherBindStudentMessageRequest_C2S request)
        {
            if (MonoSingletonT<NetManager>.Instance.IsHost())
            {
                var connection = MonoSingletonT<RoomManager>.Instance.GetConnectByPlayerUID(MonoSingletonT<UnitManager>.Instance.localPlayerUID);
                MonoSingletonT<RoomManager>.Instance.GetSkillMessageHandler().OnTeacherBindStudentMessageRequest(connection, request);
            }
            else
            {
                MonoSingletonT<RoomManager>.Instance.SendToServer(request);
            }
        }
    }
}
