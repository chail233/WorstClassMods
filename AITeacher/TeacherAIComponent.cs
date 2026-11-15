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
        private float patrolPickAt;
        private Vector3 patrolPoint;
        private float nextActionAt;   // 反应延迟/节奏停顿
        private float nextAttackAt;
        private float nextCatchAt;    // 抓捕请求节流
        private float nextAgentCheck; // NavMesh 就绪重试节流
        private float baseSpeedCache; // 房间规则移速缓存（1s 刷新）
        private float baseSpeedAt;
        private float noPillarSince;  // 手里没柱子可挂的计时
        private bool movingAnim;

        private AiTeacherConfig Cfg => ModEntry.Config;

        // ===== 生命周期 =====

        public override void OnBindObj(ObjectModel obj)
        {
            base.OnBindObj(obj);
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

            bool classing = ClassroomManager.IsSingletonActive()
                && ClassroomManager.Instance.curState == EClassroomState.Classing;
            if (owner.IsStun || !classing)
            {
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
            RaycastHit[] hits = Physics.RaycastAll(origin, dir, dist, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
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
        /// 目标获取（证据驱动）：视野内违规（手持捣蛋物品/玩电脑）或听见近期捣蛋才追。
        /// 坐着学习/纯路过的无辜学生不追——原逻辑"看见就追"显得莽撞。
        /// 协同开启时跳过已被其他 AI 认领的目标。
        /// </summary>
        private Unit AcquireTarget(out string reason)
        {
            reason = null;
            Unit best = null;
            float bestDist = float.MaxValue;
            foreach (Unit player in UnitManager.Instance.playerList)
            {
                if (!IsValidStudent(player)) continue;
                bool heard = AiTeacherManager.TryHearTrick(player.UID, owner.Position, Cfg.hearingDistance, Cfg.suspicionMemory, out _);
                bool violating = Cfg.observeTrickItems && AiTeacherManager.IsViolating(player.UID);
                if (!heard && !(violating && IsVisible(player))) continue; // 无嫌疑，放过
                if (Cfg.cooperate && AiTeacherManager.IsClaimedByOther(player.UID, owner.UID)) continue;
                float distSq = (player.Position - owner.Position).sqrMagnitude;
                if (distSq < bestDist)
                {
                    best = player;
                    bestDist = distSq;
                    reason = violating ? "发现违规（手持捣蛋物品/玩电脑）" : "听见捣蛋";
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

        private void TickPatrol()
        {
            Unit target = AcquireTarget(out string reason);
            if (target != null)
            {
                BeginChase(target, reason);
                return;
            }

            if (Time.unscaledTime >= patrolPickAt || Reached(patrolPoint, 1.2f))
            {
                patrolPickAt = Time.unscaledTime + Cfg.patrolInterval;
                patrolPoint = PickPatrolPoint();
            }
            MoveTowards(patrolPoint);
        }

        private Vector3 PickPatrolPoint()
        {
            // 热点巡逻：一半概率去最近 20 秒内的捣蛋事发地附近转（捣蛋王的座位区会被反复照顾）
            if (Cfg.hotspotPatrol && UnityEngine.Random.value < 0.5f
                && AiTeacherManager.TryGetLatestTrickPoint(20f, out Vector3 hotspot)
                && NavMesh.SamplePosition(hotspot + InsideCircle(3f), out var hitHot, 1.5f, NavMesh.AllAreas))
            {
                return hitHot.position;
            }
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
                patrolPickAt = 0f;
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

            // 证据刷新：可见/可闻/持续违规都算；认领同步续期
            if (visible || heard || violating)
            {
                RememberSeen(chaseTarget);
                if (Cfg.cooperate) AiTeacherManager.ClaimTarget(chaseTarget.UID, owner.UID);
            }

            // 晕眩了 → 冲刺抓捕
            if (chaseTarget.IsStun)
            {
                if (dist <= ConfigManager.TeacherSetting.arrestDistance)
                {
                    TryCatch(chaseTarget);
                }
                else
                {
                    MoveTowards(chaseTarget.Position);
                }
                return;
            }

            // 攻击射程内且看得见（贴脸 2.5m 内无视角度）→ 打；粉笔按目标速度预判落点
            float attackRange = (owner.ID == 1002) ? 2.4f : Cfg.visionDistance * 0.9f;
            bool inSight = dist <= 2.5f || visible;
            if (Time.unscaledTime >= nextAttackAt && inSight && dist <= attackRange && skill.currentSkillEnergy > Cfg.catchMinEnergy)
            {
                if (owner.ID == 1002)
                {
                    TrySword(headPos);
                }
                else
                {
                    Vector3 aimPos = headPos;
                    if (Cfg.chalkLeadFactor > 0.01f)
                    {
                        var force = chaseTarget.GetComponent<ForceComponent>();
                        Vector3 velocity = force != null ? force.Velocity : Vector3.zero;
                        velocity.y = 0f;
                        if (velocity.sqrMagnitude > 0.01f)
                        {
                            float flyTime = dist / Mathf.Max(1f, ConfigManager.TeacherSetting.chalkSpeed);
                            aimPos += velocity * (flyTime * Cfg.chalkLeadFactor);
                        }
                    }
                    TryChalk(aimPos);
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
                    chaseTarget = null;
                    SetMoving(false);
                }
            }
        }

        // ===== 抓捕与挂柱 =====

        private void TryCatch(Unit student)
        {
            if (catchComp == null || catchComp.catchedUnitUID != 0L) return;
            if (skill.currentSkillEnergy <= 0f) return;
            if (Time.unscaledTime < nextCatchAt) return; // 抓捕请求节流，防服务器拒绝时刷屏
            nextCatchAt = Time.unscaledTime + 1f;
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
                patrolPickAt = 0f;
            }
        }

        // ===== 攻击 =====

        private void TryChalk(Vector3 targetHead)
        {
            Vector3 origin = owner.Position + Vector3.up * 1.5f;
            Vector3 dir = (targetHead - origin);
            if (dir.sqrMagnitude < 0.001f) return; // 先校验再扣体力，避免白扣
            if (!skill.TryUseAttackEnergy(ERoomRuleId.TeacherFBXHTI)) return;
            animComp?.PlayAnim("Throw3");
            dir.Normalize();
            dir = ApplySpread(dir);

            Unit projectile = UnitManager.Instance.CreateCommonUnitDirectly(4002, origin, Vector3.zero, (ref UnitDataNet arg) =>
            {
                arg.CreaterUID = owner.UID;
                arg.ShootForward = dir;
                arg.ShootSpeed = ConfigManager.TeacherSetting.chalkSpeed;
                arg.EffectDuration = ConfigManager.TeacherSetting.chalkLifetime;
                arg.IgnoreSync = true;
            });
            if (projectile == null) return;
            projectile.GetComponent<ProjectileComponent>()?.TrackChalkAchievement(owner.UID);
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
            owner.GetComponent<TeacherMeleeComponent>()?.HandleSlashFire(request);
            float ruleCd = SingletonT<RoomRuleDataManager>.Instance.GetCurrentFloatValue(ERoomRuleId.UseSwordCD);
            nextAttackAt = Time.unscaledTime + Mathf.Max(Cfg.attackCooldown, ruleCd);
        }

        private Vector3 ApplySpread(Vector3 dir)
        {
            float spread = Mathf.Max(0f, Cfg.chalkSpreadDeg);
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
