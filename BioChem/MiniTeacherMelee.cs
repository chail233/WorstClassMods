using System.Collections.Generic;
using UnityEngine;

namespace BioChem
{
    /// <summary>
    /// 小老师教棍：程序化拼装的近战武器视觉，各端本地创建随感染名单驱动（骨架与 HunterKnife 相同）。
    /// 世界棍挂 HandPosition（自己的走 PlayerSelf 层剔除），本地小老师额外挂相机 viewmodel；
    /// 教棍从身前横扫；生命周期：感染 Ensure / 清局 RemoveAll，Tick 兜底清理非感染者残根。
    /// </summary>
    internal static class MiniTeacherMelee
    {
        private static readonly Color TrailColor = new Color(0.85f, 0.25f, 0.22f); // 挥棍轨迹：深红（老师阵营）
        private const float TrailWidth = 0.045f;
        // 世界棍优先对齐 ItemHandPos 参考位
        private static readonly Vector3 HandLocalPos = new Vector3(0.02f, 0.05f, 0.01f);
        private static readonly Vector3 HandLocalEuler = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 WeaponModelEuler = new Vector3(90f, 0f, 0f); // 棍梢(+Y)指向挂点前方

        private static readonly Dictionary<ulong, MeleeSwing> rigs = new Dictionary<ulong, MeleeSwing>();
        private static readonly List<ulong> prune = new List<ulong>();
        private static readonly List<ulong> rebuild = new List<ulong>();

        private static Material shaftMaterial, handleMaterial, tipMaterial;

        // ===== 生命周期（全幂等） =====

        /// <summary>确保该小老师有教棍：模型/手骨未就绪时静默跳过（Tick 自愈会重试）。</summary>
        internal static void Ensure(Unit unit)
        {
            if (unit == null || !unit.isActived || unit.model == null) return;
            if (!rigs.TryGetValue(unit.UID, out MeleeSwing rig))
            {
                rig = new MeleeSwing(unit);
                rigs.Add(unit.UID, rig);
            }
            if (rig.World == null)
            {
                rig.BindWorld(BuildHandWeapon(unit));
                if (rig.World != null)
                    ModEntry.Log?.Invoke("[状态] " + ModEntry.GetName(unit.UID) + " 已持教棍");
            }
            if (unit.isLocalPlayer && rig.View == null) rig.BindView(BuildViewModel());
        }

        internal static void Remove(ulong uid)
        {
            if (rigs.Remove(uid, out MeleeSwing rig)) rig.Destroy();
        }

        internal static void RemoveAll()
        {
            foreach (ulong uid in new List<ulong>(rigs.Keys)) Remove(uid);
        }

        /// <summary>挥棍表现入口：攻击者本地预测与其他端收到 MiniSwing 广播共用。</summary>
        internal static void PlaySwing(ulong uid)
        {
            if (!rigs.ContainsKey(uid) && UnitManager.IsSingletonActive())
                Ensure(UnitManager.Instance.GetUnitByUID(uid));
            if (!rigs.TryGetValue(uid, out MeleeSwing rig)) return;
            float duration = Mathf.Min(0.28f, ModEntry.Config.miniMeleeInterval);
            rig.Play(duration * 0.60f, duration * 0.40f);
        }

        // ===== 每帧驱动 =====

        internal static void Tick()
        {
            if (rigs.Count == 0) return;
            float now = Time.unscaledTime;
            prune.Clear();
            rebuild.Clear();
            foreach (var pair in rigs)
            {
                ulong uid = pair.Key;
                MeleeSwing rig = pair.Value;
                if (!ModEntry.infected.Contains(uid) || ModEntry.killedTeachers.Contains(uid)) { prune.Add(uid); continue; }
                rig.Tick(now);
                if (now >= rig.NextBuildTryAt)
                {
                    bool needView = rig.View == null && UnitManager.IsSingletonActive()
                        && UnitManager.Instance.localPlayerUID == uid;
                    if (rig.World == null || needView)
                    {
                        rebuild.Add(uid);
                        rig.NextBuildTryAt = now + 0.5f;
                    }
                }
            }
            foreach (ulong uid in prune) Remove(uid);
            foreach (ulong uid in rebuild)
            {
                if (!UnitManager.IsSingletonActive()) continue;
                Unit unit = UnitManager.Instance.GetUnitByUID(uid);
                if (unit != null && unit.isActived && unit.model != null) Ensure(unit);
            }
        }

        // ===== 棍体拼装 =====

        // 世界棍挂 HandPosition 手骨，位姿优先对齐 ItemHandPos；自己的走 PlayerSelf，别人的走 Player
        private static Transform BuildHandWeapon(Unit unit)
        {
            Transform hand = unit.GetPartRoot(EUnitPartType.HandPosition);
            if (hand == null) return null; // 模型部件还没绑好
            var root = new GameObject("MiniTeacherWeapon_" + unit.UID);
            root.transform.SetParent(hand, false);
            Transform itemRef = unit.GetPartRoot(EUnitPartType.ItemHandPos);
            if (itemRef != null)
            {
                root.transform.position = itemRef.position;
                root.transform.rotation = itemRef.rotation;
            }
            else
            {
                root.transform.localPosition = HandLocalPos;
                root.transform.localRotation = Quaternion.Euler(HandLocalEuler);
            }
            BuildParts(root.transform);
            int layer = LayerMask.NameToLayer(unit.isLocalPlayer ? "PlayerSelf" : "Player");
            if (layer >= 0) SetLayerRecursive(root, layer); // 部件挂完再统一设层
            return root.transform;
        }

        // 本地小老师的第一人称 viewmodel：挂主相机，无层调整（Default 层本机可见）
        private static Transform BuildViewModel()
        {
            if (!MonoSingletonT<SceneManager>.IsSingletonActive()) return null;
            Camera camera = MonoSingletonT<SceneManager>.Instance.CameraCtr?.MainCamera;
            if (camera == null) return null;
            var root = new GameObject("MiniTeacherWeaponView");
            root.transform.SetParent(camera.transform, false);
            BuildParts(root.transform);
            return root.transform;
        }

        // 教棍造型：root 即握点，拖尾挂棍梢 y≈0.36
        private static void BuildParts(Transform root)
        {
            EnsureMaterials();
            var model = new GameObject("model");
            model.transform.SetParent(root, false);
            model.transform.localRotation = Quaternion.Euler(WeaponModelEuler);
            CreatePart(model.transform, "shaft", new Vector3(0f, 0.16f, 0f), new Vector3(0.036f, 0.36f, 0.036f), shaftMaterial);
            CreatePart(model.transform, "tip", new Vector3(0f, 0.355f, 0f), new Vector3(0.046f, 0.045f, 0.046f), tipMaterial);
            CreatePart(model.transform, "handle", new Vector3(0f, -0.06f, 0f), new Vector3(0.03f, 0.12f, 0.03f), handleMaterial);
            WeaponModels.AttachSlashTrail(model.transform, new Vector3(0f, 0.36f, 0f), TrailColor, TrailWidth);
        }

        private static void CreatePart(Transform parent, string name, Vector3 localPos, Vector3 size, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(go.GetComponent<Collider>()); // 纯视觉：Collider 会挡交互/射线，必须剥掉
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            if (material != null)
                go.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static void EnsureMaterials()
        {
            if (shaftMaterial != null) return;
            shaftMaterial = WeaponModels.Tint(new Color(0.55f, 0.14f, 0.13f), 0.45f);  // 深红棍身
            tipMaterial = WeaponModels.Tint(new Color(0.75f, 0.78f, 0.82f), 0.7f);     // 银色棍梢箍
            handleMaterial = WeaponModels.Tint(new Color(0.14f, 0.12f, 0.11f), 0.25f); // 深色握柄
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursive(child.gameObject, layer);
        }
    }
}
