using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace BioChem
{
    // 两张货场都保留原版房间、上课铃和课堂结算。房主选图，客机按快照建立同一场地。
    internal static class BioChemArena
    {
        internal static readonly Vector3 Origin = new Vector3(96f, 24f, 96f);

        private sealed class MovedProp
        {
            internal Unit Unit;
            internal Transform Model;
            internal Transform Parent;
            internal int SiblingIndex;
            internal ulong Uid;
            internal Vector3 OriginalPosition;
            internal bool WasActive;
        }

        private static MovedProp[] movedProps = Array.Empty<MovedProp>();
        private static readonly List<Vector3> teacherSpawns = new List<Vector3>();
        private static readonly List<Vector3> studentSpawns = new List<Vector3>();
        private static readonly List<ulong> spawnedPillars = new List<ulong>();
        private static BioChemArenaGeometry geometry;
        private static BioChemArenaLayout layout;
        private static long[] propUids;
        private static float[] propHeights;
        private static uint roundSequence;
        private static uint failedClientRound;
        private static bool attempted;
        private static float nextPlacement;

        internal static bool Active => geometry?.Root != null;
        internal static bool InRoundPhase => ClassroomManager.IsSingletonActive()
            && (ClassroomManager.Instance.curState == EClassroomState.Prepare
                || ClassroomManager.Instance.curState == EClassroomState.Classing);
        internal static uint Round { get; private set; }
        internal static BioChemArenaLayout Layout => Active ? layout : null;

        internal static void WriteState(BioChemStateDto dto)
        {
            if (!Active) return;
            dto.arenaMap = (int)layout.Map;
            dto.arenaRound = Round;
            dto.arenaProps = propUids;
            dto.arenaPropHeights = propHeights;
        }

        internal static void ReadState(BioChemStateDto dto)
        {
            var selected = BioChemArenaLayout.Get((BioChemMap)dto.arenaMap);
            if (selected == null || dto.arenaRound == 0 || dto.arenaProps?.Length != selected.Props.Length
                || dto.arenaPropHeights?.Length != selected.Props.Length)
            {
                Reset();
                return;
            }
            if (dto.arenaRound == failedClientRound) return;
            if (Round != dto.arenaRound || layout != selected)
            {
                Reset();
                movedProps = new MovedProp[selected.Props.Length];
            }
            Round = dto.arenaRound;
            layout = selected;
            propUids = dto.arenaProps;
            propHeights = dto.arenaPropHeights;
            EnsureClientArena();
        }

        internal static void Tick(Action<string> broadcastState)
        {
            if (!NetRuntime.IsConnected)
            {
                if (layout != null || attempted || failedClientRound != 0) Reset();
                return;
            }
            if (!NetworkServer.active)
            {
                // CurrentRoom 只在服务端创建。客机用连接状态和房主快照维护地图生命周期。
                if (layout != null) EnsureClientArena();
                return;
            }
            Room room = RoomManager.IsSingletonActive() ? RoomManager.Instance.CurrentRoom : null;
            if (room == null || !ClassroomManager.IsSingletonActive())
            {
                if (layout != null || attempted) Reset();
                return;
            }

            ClassroomManager classroom = ClassroomManager.Instance;
            if (classroom.curState == EClassroomState.BeforeClass)
            {
                if (layout != null || attempted)
                {
                    bool wasActive = Active;
                    Reset();
                    if (wasActive) broadcastState(null);
                }
                return;
            }

            // ClassFinish 仍有原生放人和结算表现，等 BeforeClass 把玩家送回校园再拆场。
            // 按铃后玩家以学生身份进入货场，教师在准备倒计时结束时揭晓。
            if (!InRoundPhase || !ModEntry.enabled) return;
            var selected = BioChemArenaLayout.Get((BioChemMap)ModEntry.Config.arenaMap);
            if (!attempted && selected != null)
            {
                attempted = true;
                bool opened = false;
                try { opened = TryOpen(selected, classroom.currentFloor, room.GetPlayers().Count); }
                catch (Exception ex) { ModEntry.Log("[状态] 生化地图加载失败：" + ex); }
                string name = BioChemArenaLayout.NameOf(selected.Map);
                if (!opened)
                {
                    Reset();
                    attempted = true;
                }
                string announcement = opened ? "本局生化地图：" + name : name + "加载失败，本局使用原版校园。";
                ModEntry.Log("[状态] " + announcement);
                ModEntry.NotifyAll(announcement);
                broadcastState(announcement); // 可靠快照先于原生传送，客机先建立场地和碰撞。
            }

            if (!Active || Time.unscaledTime < nextPlacement) return;
            nextPlacement = Time.unscaledTime + 0.5f;
            foreach (Unit unit in room.GetPlayers().Values)
            {
                if (!CanPlace(unit) || Contains(unit.Position)) continue;
                if (!unit.isLocalPlayer && room.GetConnectByPlayerUID(unit.UID)?.isReady != true) continue;
                PlacePlayer(unit);
            }
            BioChemArenaSupplies.TickServer();
        }

        internal static void PlaceJoiningPlayer(NetworkConnectionToClient connection, BioChemStateMessage snapshot)
        {
            if (!Active || !InRoundPhase) return;
            ulong uid = RoomManager.Instance.CurrentRoom?.GetPlayerUIDByConnection(connection) ?? 0;
            Unit unit = uid != 0 ? UnitManager.Instance.GetUnitByUID(uid) : null;
            if (!CanPlace(unit)) return;

            connection.Send(snapshot);
            if (!Contains(unit.Position)) PlacePlayer(unit);
            else connection.Send(TeleportMessage(uid, unit.Position, unit.Rotation));
        }

        private static bool CanPlace(Unit unit) => unit != null && unit.isActived && !unit.IsDead && unit.transform != null;

        private static UnitTeleportPositionMessageResponse_S2C TeleportMessage(ulong uid, Vector3 position, Vector3 rotation)
        {
            return new UnitTeleportPositionMessageResponse_S2C { unitUID = uid, position = position, rotation = rotation };
        }

        private static void PlacePlayer(Unit unit)
        {
            bool teacher = unit.UnitSubType == EUnitSubType.Teacher || ModEntry.infected.Contains(unit.UID);
            float yaw = layout.TeacherYaw + (teacher ? 0f : 180f);
            var teleport = TeleportMessage(unit.UID, SpawnPosition(unit.UID, teacher), new Vector3(0f, yaw, 0f));
            RoomManager.Instance.GetUnitMessageHandler().OnUnitTeleportPositionMessageResponse(teleport);
            RoomManager.Instance.BroadcastToAllExceptHost(teleport);
        }

        private static bool TryOpen(BioChemArenaLayout selected, int floor, int players)
        {
            ClassroomConfig school = ClassroomConfig.Active;
            ClassroomItem classroom = school != null ? school.GetClassroomByFloor(floor) : null;
            if (school == null || !UnitManager.IsSingletonActive())
            {
                ModEntry.Log("[状态] 校园场景单位尚未就绪");
                return false;
            }

            Vector3 anchor = classroom != null ? classroom.transform.position : UnitManager.Instance.localPlayer?.Position ?? Vector3.zero;
            var boards = new List<Unit>();
            var pillars = new List<Unit>();
            foreach (Unit unit in UnitManager.Instance.unitsDict.Values)
            {
                if (!CanPlace(unit)) continue;
                if (unit.GetComponent<BlackBoardComponent>() != null) boards.Add(unit);
                else if (unit.GetComponent<PunishPillarComponent>()?.BoundStudentUID == 0) pillars.Add(unit);
            }
            if (boards.Count == 0)
            {
                ModEntry.Log("[状态] 找不到可迁移的原生黑板");
                return false;
            }

            int CompareProps(Unit a, Unit b)
            {
                int visibleOrder = b.gameObject.activeInHierarchy.CompareTo(a.gameObject.activeInHierarchy);
                if (visibleOrder != 0) return visibleOrder;
                int distanceOrder = (a.Position - anchor).sqrMagnitude.CompareTo((b.Position - anchor).sqrMagnitude);
                return distanceOrder != 0 ? distanceOrder : a.UID.CompareTo(b.UID);
            }
            boards.Sort(CompareProps);
            pillars.Sort(CompareProps);
            int propCount = selected.Props.Length;
            int pillarCount = Math.Min(propCount - 1, pillars.Count);
            movedProps = new MovedProp[propCount];
            propUids = new long[propCount];
            propHeights = new float[propCount];
            for (int i = 0; i <= pillarCount; i++)
            {
                Unit unit = i == 0 ? boards[0] : pillars[i - 1];
                propUids[i] = (long)unit.UID;
                Vector3 position = unit.Position;
                if (unit.gameObject.activeInHierarchy && Physics.Raycast(position + Vector3.up * 0.2f,
                    Vector3.down, out RaycastHit hit, 3f, LayerMask.GetMask("Ground", "Default"), QueryTriggerInteraction.Ignore))
                    propHeights[i] = Mathf.Clamp(position.y - hit.point.y, 0f, 2.4f);
                else propHeights[i] = i == 0 ? 1.25f : 0f;
            }

            layout = selected;
            geometry = new BioChemArenaGeometry(layout, Origin, school);
            MoveAvailableProps();
            BuildSpawnPoints(layout.TeacherZone, teacherSpawns);
            BuildSpawnPoints(layout.StudentZone, studentSpawns);
            if (teacherSpawns.Count < players || studentSpawns.Count < players)
            {
                ModEntry.Log("[状态] 生化地图安全出生点不足：教师 " + teacherSpawns.Count + "、学生 " + studentSpawns.Count);
                return false;
            }
            BioChemArenaSupplies.BeginRound();
            if (pillarCount < propCount - 1) SpawnMissingPillars(pillarCount + 1);
            if (++roundSequence == 0) roundSequence++;
            Round = roundSequence;
            ModEntry.Log("[状态] 生化地图出生点已就绪：教师 " + teacherSpawns.Count + "、学生 " + studentSpawns.Count
                + "，罚站柱 " + (pillarCount + spawnedPillars.Count));
            return true;
        }

        private static void SpawnMissingPillars(int firstSlot)
        {
            var pillar = cfg.Tables.TbUnit.DataList.Find(unit => unit.SubType == (int)EUnitSubType.PunishPillar
                && !string.IsNullOrEmpty(unit.ModelPath));
            if (pillar == null) throw new InvalidOperationException("找不到原生罚站柱预制体");

            // 新建的柱子走原生单位生成和入房同步，快照只需要搬移已有场景柱。
            for (int i = firstSlot; i < layout.Props.Length; i++)
            {
                Unit unit = UnitManager.Instance.CreateCommonUnitDirectly(pillar.Id, Origin + layout.Props[i]);
                if (unit == null) throw new InvalidOperationException("货场罚站柱生成失败");
                spawnedPillars.Add(unit.UID);
            }
        }

        private static void BuildSpawnPoints(Rect zone, List<Vector3> spawns)
        {
            spawns.Clear();
            const float spacing = 1.1f;
            for (float z = zone.yMin; z <= zone.yMax + 0.01f; z += spacing)
            for (float x = zone.xMin; x <= zone.xMax + 0.01f; x += spacing)
            {
                Vector3 point = Origin + new Vector3(x, 0.16f, z);
                if (!Physics.CheckCapsule(point + Vector3.up * 0.36f, point + Vector3.up * 1.36f, 0.42f,
                    BioChemArenaGeometry.SolidMask, QueryTriggerInteraction.Ignore))
                    spawns.Add(point);
            }
            Vector3 center = Origin + new Vector3(zone.center.x, 0.16f, zone.center.y);
            spawns.Sort((a, b) => (a - center).sqrMagnitude.CompareTo((b - center).sqrMagnitude));
        }

        private static void EnsureClientArena()
        {
            if (ClassroomConfig.Active == null) return;
            try
            {
                if (!Active) geometry = new BioChemArenaGeometry(layout, Origin, ClassroomConfig.Active);
                MoveAvailableProps();
            }
            catch (Exception ex)
            {
                uint failed = Round;
                Reset();
                failedClientRound = failed;
                ModEntry.Log("[状态] 客机无法建立生化地图：" + ex);
            }
        }

        private static void MoveAvailableProps()
        {
            if (!UnitManager.IsSingletonActive()) return;
            bool movedAny = false;
            for (int i = 0; i < movedProps.Length; i++)
            {
                ulong uid = (ulong)propUids[i];
                Unit unit = uid != 0 ? UnitManager.Instance.GetUnitByUID(uid) : null;
                MovedProp moved = movedProps[i];
                if (moved != null && moved.Uid == uid && moved.Model != null && unit == moved.Unit && unit.transform == moved.Model) continue;
                RestoreProp(i);
                if (!CanPlace(unit)) continue;
                Transform model = unit.transform;
                movedProps[i] = new MovedProp
                {
                    Unit = unit, Uid = uid, Model = model, Parent = model.parent,
                    SiblingIndex = model.GetSiblingIndex(), OriginalPosition = unit.Position, WasActive = model.gameObject.activeSelf,
                };
                // 活动楼层会隐藏其它楼层根节点。重新挂到地图容器才能使用从别层借来的柱子。
                model.SetParent(geometry.Root.transform, true);
                model.gameObject.SetActive(true);
                SetPropPosition(unit, Origin + layout.Props[i] + Vector3.up * propHeights[i]);
                movedAny = true;
            }
            if (movedAny) Physics.SyncTransforms();
        }

        private static void SetPropPosition(Unit unit, Vector3 position)
        {
            unit.GetComponent<SyncTransformComponet>()?.ApplyTransformImmediately(position, unit.Rotation);
            unit.SetPosition(position);
            var body = unit.GetComponent<ForceComponent>()?.rigidBody;
            if (body != null) body.position = position;
        }

        private static void RestoreProp(int index)
        {
            MovedProp moved = movedProps[index];
            if (moved != null && moved.Model != null && moved.Unit.UID == moved.Uid && moved.Unit.transform == moved.Model)
            {
                moved.Model.SetParent(moved.Parent, true);
                moved.Model.SetSiblingIndex(moved.SiblingIndex);
                SetPropPosition(moved.Unit, moved.OriginalPosition);
                moved.Model.gameObject.SetActive(moved.WasActive);
            }
            movedProps[index] = null;
        }

        internal static void Reset()
        {
            BioChemArenaSupplies.Reset();
            if (NetworkServer.active && UnitManager.IsSingletonActive())
            {
                foreach (ulong uid in spawnedPillars)
                    UnitManager.Instance.GetUnitByUID(uid)?.SyncBack();
            }
            spawnedPillars.Clear();
            for (int i = 0; i < movedProps.Length; i++) RestoreProp(i);
            movedProps = Array.Empty<MovedProp>();
            geometry?.Dispose();
            geometry = null;
            layout = null;
            propUids = null;
            propHeights = null;
            teacherSpawns.Clear();
            studentSpawns.Clear();
            Round = 0;
            failedClientRound = 0;
            nextPlacement = 0f;
            attempted = false;
        }

        internal static Vector3 SpawnPosition(ulong uid, bool teacher)
        {
            int seat = Mathf.Max(0, RoomManager.Instance.CurrentRoom?.GetPlayerSeatIndex(uid) ?? 0);
            List<Vector3> spawns = teacher ? teacherSpawns : studentSpawns;
            return spawns[seat % spawns.Count];
        }

        internal static bool Contains(Vector3 position) => Active && layout.Playable.Contains(position - Origin);

        internal static bool IsCoverBetween(Vector3 from, Vector3 to)
        {
            return Contains(from) && Contains(to)
                && Physics.Linecast(from, to, BioChemArenaGeometry.SolidMask, QueryTriggerInteraction.Ignore);
        }
    }
}
