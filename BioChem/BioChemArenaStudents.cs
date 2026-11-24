using System;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Mirror;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace BioChem
{
    internal static class BioChemArenaStudents
    {
        private static readonly MethodInfo spawnStudent = AccessTools.Method(typeof(StudentNPCManager), "SpawnStudentNpc");
        private static readonly List<ulong> students = new List<ulong>();
        private static readonly NavMeshPath spawnPath = new NavMeshPath();
        private static NavMeshSurface surface;
        private static PlaygroundNavigationArea navigation;
        private static NavMeshQueryFilter filter;
        private static Bounds bounds;
        private static Vector3 anchor;
        private static int targetCount;
        private static float nextSpawn;

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(StudentNPCManager), nameof(StudentNPCManager.CreateNPCsAsync)),
                prefix: new HarmonyMethod(typeof(BioChemArenaStudents), nameof(BeforeCreateCampusStudents)));
        }

        private static bool BeforeCreateCampusStudents(IList<StudentNPCSpawner> spawners, ref UniTask __result)
        {
            if (navigation?.IsValid != true || !BioChemArena.InRoundPhase) return true;
            StudentNPCSpawner ringSpawner = ClassroomManager.Instance.currentRing?.model?.GetComponent<StudentNPCSpawner>();
            if (ringSpawner == null || spawners?.Contains(ringSpawner) != true) return true;
            __result = UniTask.CompletedTask;
            return false;
        }

        internal static void BeginRound(GameObject root, Bounds playable, List<Vector3> studentSpawns)
        {
            try
            {
                targetCount = ConfigManager.GameSetting.NpcDefaultSpawnCount;
                if (targetCount <= 0) return;
                int area = NavMesh.GetAreaFromName("PlaygroundArea");
                if (area < 0 || spawnStudent == null) throw new InvalidOperationException("找不到原生操场 AI 配置");

                surface = root.AddComponent<NavMeshSurface>();
                surface.collectObjects = CollectObjects.Volume;
                surface.center = playable.center;
                surface.size = playable.size;
                surface.layerMask = BioChemArenaGeometry.SolidMask;
                surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                surface.defaultArea = area;
                surface.BuildNavMesh();

                bounds = new Bounds(root.transform.TransformPoint(playable.center), playable.size);
                filter = new NavMeshQueryFilter { agentTypeID = surface.agentTypeID, areaMask = 1 << area };
                foreach (Vector3 spawn in studentSpawns)
                {
                    if (!NavMesh.SamplePosition(spawn, out NavMeshHit hit, 1f, filter) || !bounds.Contains(hit.position)) continue;
                    anchor = hit.position;
                    navigation = new PlaygroundNavigationArea(surface, area);
                    ModEntry.Log("[状态] 生化地图 AI 学生导航已就绪，本局生成 " + targetCount + " 名");
                    return;
                }
                throw new InvalidOperationException("学生入口附近没有可用的导航网格");
            }
            catch (Exception ex)
            {
                Reset();
                ModEntry.Log("[状态] 生化地图 AI 学生导航初始化失败：" + ex.GetBaseException().Message);
            }
        }

        internal static void TickServer()
        {
            if (navigation?.IsValid != true || students.Count >= targetCount || Time.unscaledTime < nextSpawn) return;
            nextSpawn = Time.unscaledTime + Random.Range(0.2f, 0.6f);
            if (!TryFindSpawn(out Vector3 position)) return;
            try
            {
                var rotation = new Vector3(0f, Random.Range(0f, 360f), 0f);
                var unit = (Unit)spawnStudent.Invoke(null, new object[] { position, rotation });
                if (unit == null) throw new InvalidOperationException("原生 AI 学生创建失败");
                students.Add(unit.UID);
                // 复用操场的随机目的地、动画和被释放后的移动恢复。模型加载完毕时原生组件会启动 AI。
                unit.GetComponent<StudentNPCComponent>().SetPlaygroundArea(navigation);
            }
            catch (Exception ex)
            {
                targetCount = students.Count;
                ModEntry.Log("[状态] 生化地图 AI 学生生成失败：" + ex.GetBaseException().Message);
            }
        }

        private static bool TryFindSpawn(out Vector3 position)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            for (int i = 0; i < 16; i++)
            {
                var candidate = new Vector3(Random.Range(min.x, max.x), anchor.y, Random.Range(min.z, max.z));
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 0.8f, filter) || !bounds.Contains(hit.position)
                    || !NavMesh.CalculatePath(anchor, hit.position, filter, spawnPath)
                    || spawnPath.status != NavMeshPathStatus.PathComplete) continue;
                position = hit.position;
                return true;
            }
            position = default;
            return false;
        }

        internal static void Reset()
        {
            if (NetworkServer.active && UnitManager.IsSingletonActive())
            {
                foreach (ulong uid in students)
                {
                    Unit unit = UnitManager.Instance.GetUnitByUID(uid);
                    if (unit == null) continue;
                    unit.GetComponent<StudentNPCComponent>()?.StopNPCAction();
                    var despawn = new UnitDestroyMessageResponse_S2C { unitUID = uid };
                    RoomManager.Instance.GetUnitMessageHandler().OnUnitDestroyMessageResponse(despawn);
                    RoomManager.Instance.BroadcastToAllExceptHost(despawn);
                }
            }
            students.Clear();
            if (surface != null)
            {
                surface.RemoveData();
                UnityEngine.Object.Destroy(surface.navMeshData);
                surface.navMeshData = null;
                UnityEngine.Object.Destroy(surface);
            }
            surface = null;
            navigation = null;
            nextSpawn = 0f;
        }
    }
}
