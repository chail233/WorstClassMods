using System.Collections.Generic;
using UnityEngine;

namespace BioChem
{
    /// <summary>
    /// 小老师教棍：程序化拼装的近战武器视觉，各端本地创建随感染名单驱动（骨架与 HunterKnife 相同）。
    /// 世界棍挂 HandPosition（自己的走 PlayerSelf 层剔除），本地小老师额外挂相机 viewmodel；
    /// 挥动=斜劈公转+拖尾；生命周期：感染 Ensure / 清局 RemoveAll，Tick 兜底清理非感染者残根。
    /// </summary>
    internal static class MiniTeacherMelee
    {
        private const float SwingDuration = 0.28f;     // 一次挥棍的表现时长（与 miniMeleeInterval 0.5s 节奏匹配）
        private const float SwingPivotDistance = 0.28f; // 挥棍支点离握点的距离（沿棍背方向）：绕它扫出大幅斜劈弧线
        private static readonly Vector3 SwingAxis = new Vector3(1f, -0.7f, 0f).normalized; // 斜劈轴：右上举棍→向左下劈出
        private static readonly Color TrailColor = new Color(0.85f, 0.25f, 0.22f); // 挥棍轨迹：深红（老师阵营）
        private const float TrailWidth = 0.045f;
        // 棍体在手部/相机空间的基础位姿（实测可调；世界棍优先对齐 ItemHandPos 参考位）
        private static readonly Vector3 HandLocalPos = new Vector3(0.02f, 0.05f, 0.01f);
        private static readonly Vector3 HandLocalEuler = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 WeaponModelEuler = new Vector3(90f, 0f, 0f); // 棍梢(+Y)指向挂点前方
        private static readonly Vector3 ViewLocalPos = new Vector3(0.34f, -0.32f, 0.60f);
        private static readonly Vector3 ViewLocalEuler = new Vector3(30f, -12f, 8f);

        private sealed class Rig
        {
            public Transform worldWeapon;    // 手骨下的棍根（其他玩家可见）
            public Vector3 worldRestPos;     // 静止位姿（挥棍绕支点公转的基准，结束恢复）
            public Quaternion worldRestRot;
            public Transform viewWeapon;     // 相机下的棍根（仅本地小老师）
            public Vector3 viewRestPos;
            public Quaternion viewRestRot;
            public TrailRenderer worldTrail; // 棍梢拖尾（挥棍轨迹，随棍销毁）
            public TrailRenderer viewTrail;
            public float swingStartAt = -999f;
            public bool swinging;
            public float nextBuildTryAt;     // 构建失败后的重试节流（等模型部件就绪）
        }

        private static readonly Dictionary<ulong, Rig> rigs = new Dictionary<ulong, Rig>();
        private static readonly List<ulong> prune = new List<ulong>();
        private static readonly List<ulong> rebuild = new List<ulong>();

        private static Material shaftMaterial, handleMaterial, tipMaterial;

        // ===== 生命周期（全幂等） =====

        /// <summary>确保该小老师有教棍：模型/手骨未就绪时静默跳过（Tick 自愈会重试）。</summary>
        internal static void Ensure(Unit unit)
        {
            if (unit == null || !unit.isActived || unit.model == null) return;
            if (rigs.TryGetValue(unit.UID, out Rig rig) && rig.worldWeapon != null)
            {
                if (unit.isLocalPlayer && rig.viewWeapon == null) CaptureViewWeapon(rig); // 相机可能晚建
                return;
            }
            Remove(unit.UID); // 清掉半残 rig（比如只剩 viewWeapon）
            rig = new Rig();
            rig.worldWeapon = BuildHandWeapon(unit);
            if (rig.worldWeapon != null)
            {
                rig.worldRestPos = rig.worldWeapon.localPosition;
                rig.worldRestRot = rig.worldWeapon.localRotation;
                rig.worldTrail = rig.worldWeapon.GetComponentInChildren<TrailRenderer>();
                ModEntry.Log?.Invoke("[状态] " + ModEntry.GetName(unit.UID) + " 已持教棍");
            }
            if (unit.isLocalPlayer)
                CaptureViewWeapon(rig);
            rigs[unit.UID] = rig;
        }

        internal static void Remove(ulong uid)
        {
            if (!rigs.TryGetValue(uid, out Rig rig)) return;
            rigs.Remove(uid);
            if (rig.worldWeapon != null) Object.Destroy(rig.worldWeapon.gameObject);
            if (rig.viewWeapon != null) Object.Destroy(rig.viewWeapon.gameObject);
        }

        internal static void RemoveAll()
        {
            foreach (ulong uid in new List<ulong>(rigs.Keys)) Remove(uid);
        }

        /// <summary>挥棍表现入口：攻击者本地预测与其他端收到 MiniSwing 广播共用。</summary>
        internal static void PlaySwing(ulong uid)
        {
            if (!rigs.TryGetValue(uid, out Rig rig)) return;
            rig.swingStartAt = Time.unscaledTime;
            rig.swinging = true;
            WeaponModels.EmitSlashTrail(rig.worldTrail, SwingDuration);
            WeaponModels.EmitSlashTrail(rig.viewTrail, SwingDuration);
            ApplySwingPose(rig, 0f);
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
                Rig rig = pair.Value;
                if (!ModEntry.infected.Contains(uid) || ModEntry.killedTeachers.Contains(uid)) { prune.Add(uid); continue; } // 出局/事件点漏清的兜底
                float t = (now - rig.swingStartAt) / SwingDuration;
                if (t < 1f) ApplySwingPose(rig, t);
                else if (rig.swinging) ResetPose(rig);
                if (now >= rig.nextBuildTryAt)
                {
                    bool needWorld = rig.worldWeapon == null;                 // 世界棍缺失（未建成/模型重载）
                    bool needView = rig.viewWeapon == null                    // viewmodel 缺失（相机晚就绪）
                                    && UnitManager.IsSingletonActive()
                                    && UnitManager.Instance.localPlayerUID == uid;
                    if (needWorld || needView)
                    {
                        rebuild.Add(uid); // Ensure 幂等：缺哪补哪，0.5s 一试防每帧重建
                        rig.nextBuildTryAt = now + 0.5f;
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

        private static void ApplySwingPose(Rig rig, float t)
        {
            float progress = Mathf.Clamp01(t);
            float angle = SwingAngle(progress);
            if (rig.worldWeapon != null)
                WeaponModels.ApplySwingPose(rig.worldWeapon, rig.worldRestPos, rig.worldRestRot, angle, 0f, SwingPivotDistance, SwingAxis);
            if (rig.viewWeapon != null)
                WeaponModels.ApplySwingPose(rig.viewWeapon, rig.viewRestPos, rig.viewRestRot, angle * 0.8f, 0f, SwingPivotDistance, SwingAxis);
        }

        private static void ResetPose(Rig rig)
        {
            rig.swinging = false;
            WeaponModels.StopSlashTrail(rig.worldTrail); // 弧线按 time 自然淡出
            WeaponModels.StopSlashTrail(rig.viewTrail);
            if (rig.worldWeapon != null)
            {
                rig.worldWeapon.localPosition = rig.worldRestPos;
                rig.worldWeapon.localRotation = rig.worldRestRot;
            }
            if (rig.viewWeapon != null)
            {
                rig.viewWeapon.localPosition = rig.viewRestPos;
                rig.viewWeapon.localRotation = rig.viewRestRot;
            }
        }

        // 举起→劈下→回位三段角度曲线（与猎刀同款节奏）
        private static float SwingAngle(float t)
        {
            if (t < 0.3f) return Mathf.Lerp(0f, -55f, t / 0.3f);
            if (t < 0.6f) return Mathf.Lerp(-55f, 95f, Mathf.Clamp01((t - 0.3f) / 0.3f));
            return Mathf.Lerp(95f, 0f, Mathf.Clamp01((t - 0.6f) / 0.4f));
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
            BuildParts(root.transform, Quaternion.Euler(WeaponModelEuler));
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
            root.transform.localPosition = ViewLocalPos;
            root.transform.localRotation = Quaternion.Euler(ViewLocalEuler);
            BuildParts(root.transform, Quaternion.identity);
            return root.transform;
        }

        private static void CaptureViewWeapon(Rig rig)
        {
            Transform weapon = BuildViewModel();
            if (weapon == null) return;
            rig.viewWeapon = weapon;
            rig.viewRestPos = weapon.localPosition;
            rig.viewRestRot = weapon.localRotation;
            rig.viewTrail = weapon.GetComponentInChildren<TrailRenderer>();
        }

        // 教棍造型：root 即握点，拖尾挂棍梢 y≈0.36
        private static void BuildParts(Transform root, Quaternion modelEuler)
        {
            EnsureMaterials();
            var model = new GameObject("model");
            model.transform.SetParent(root, false);
            model.transform.localRotation = modelEuler;
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
