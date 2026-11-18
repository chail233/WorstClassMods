using System.Collections.Generic;
using UnityEngine;

namespace BioChem
{
    /// <summary>
    /// 猎手猎刀：程序化拼装的近战武器视觉（方块拼一把菜刀，各端本地创建，随猎手状态消息驱动，无网络对象）。
    /// - 世界刀挂在猎手 HandPosition 手骨下（其他玩家可见；自己那把用 PlayerSelf 层被本机相机剔除，不与 viewmodel 重复）
    /// - 本地猎手额外挂一把相机 viewmodel（第一人称可见，偏移参考老师剑 idleCameraOffset）
    /// - 挥动：PlaySwing 记时间戳，Tick 按进度转刀（举起→劈下→回位）；身体挥动动画由攻击者 PlayAnimSync("Throw") 原生同步
    /// 生命周期：变身 Ensure / 还原·退房 Remove / 清局 RemoveAll；Tick 自愈重建（模型重载/部件晚绑定兜底）。
    /// 刀模型随父级（手骨/相机）销毁，注册表只在事件点与 Tick 兜底里清理。
    /// </summary>
    internal static class HunterKnife
    {
        private const float SwingDuration = 0.3f;      // 一次挥刀的表现时长（与 hunterAttackInterval 0.4s 节奏匹配）
        private const float SwingWindupDeg = -55f;     // 前段举刀角度
        private const float SwingSlashDeg = 95f;       // 劈下终点角度
        // 刀体在手部/相机空间的基础位姿（实测可调；世界刀优先对齐 ItemHandPos 参考位）
        private static readonly Vector3 HandLocalPos = new Vector3(0.02f, 0.05f, 0.01f);
        private static readonly Vector3 HandLocalEuler = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 KnifeModelEuler = new Vector3(90f, 0f, 0f); // 刀刃(+Y)指向挂点前方
        private static readonly Vector3 ViewLocalPos = new Vector3(0.34f, -0.32f, 0.60f); // 参考老师剑 idleCameraOffset (0.35, -0.38, 0.7)
        private static readonly Vector3 ViewLocalEuler = new Vector3(30f, -12f, 8f);

        private sealed class Rig
        {
            public Transform worldKnife;   // 手骨下的刀根（其他玩家可见）
            public Quaternion worldRestRot;
            public Transform viewKnife;    // 相机下的刀根（仅本地猎手）
            public Quaternion viewRestRot;
            public float swingStartAt = -999f;
            public bool swinging;
            public float nextBuildTryAt;   // 构建失败后的重试节流（等模型部件就绪）
        }

        private static readonly Dictionary<ulong, Rig> rigs = new Dictionary<ulong, Rig>();
        private static readonly List<ulong> prune = new List<ulong>();
        private static readonly List<ulong> rebuild = new List<ulong>();

        private static Material bladeMaterial, handleMaterial, guardMaterial;

        // ===== 生命周期（全幂等） =====

        /// <summary>确保该猎手有刀：模型/手骨未就绪时静默跳过（Tick 自愈会重试）。</summary>
        internal static void Ensure(Unit unit)
        {
            if (unit == null || !unit.isActived || unit.model == null) return;
            if (rigs.TryGetValue(unit.UID, out Rig rig) && rig.worldKnife != null)
            {
                if (unit.isLocalPlayer && rig.viewKnife == null) rig.viewKnife = CaptureViewKnife(rig); // 相机可能晚建
                return;
            }
            Remove(unit.UID); // 清掉半残 rig（比如只剩 viewKnife）
            rig = new Rig();
            rig.worldKnife = BuildHandKnife(unit);
            if (rig.worldKnife != null)
            {
                rig.worldRestRot = rig.worldKnife.localRotation;
                ModEntry.Log?.Invoke("[状态] " + ModEntry.GetName(unit.UID) + " 已持猎刀");
            }
            if (unit.isLocalPlayer)
                rig.viewKnife = CaptureViewKnife(rig);
            rigs[unit.UID] = rig;
        }

        internal static void Remove(ulong uid)
        {
            if (!rigs.TryGetValue(uid, out Rig rig)) return;
            rigs.Remove(uid);
            if (rig.worldKnife != null) Object.Destroy(rig.worldKnife.gameObject);
            if (rig.viewKnife != null) Object.Destroy(rig.viewKnife.gameObject);
        }

        internal static void RemoveAll()
        {
            foreach (ulong uid in new List<ulong>(rigs.Keys)) Remove(uid);
        }

        /// <summary>挥刀表现入口：攻击者本地预测与其他端收到 Swing 广播共用。</summary>
        internal static void PlaySwing(ulong uid)
        {
            if (!rigs.TryGetValue(uid, out Rig rig)) return;
            rig.swingStartAt = Time.unscaledTime;
            rig.swinging = true;
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
                if (!ModEntry.hunters.Contains(uid)) { prune.Add(uid); continue; } // 事件点漏清的兜底
                float t = (now - rig.swingStartAt) / SwingDuration;
                if (t < 1f) ApplySwingPose(rig, t);
                else if (rig.swinging) ResetPose(rig);
                if (now >= rig.nextBuildTryAt)
                {
                    bool needWorld = rig.worldKnife == null;                 // 世界刀缺失（未建成/模型重载）
                    bool needView = rig.viewKnife == null                    // viewmodel 缺失（相机晚就绪）——
                                    && UnitManager.IsSingletonActive()       // 房主没有快照自愈通道，只能靠这里补
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
            float angle = SwingAngle(t);
            if (rig.worldKnife != null)
                rig.worldKnife.localRotation = rig.worldRestRot * Quaternion.Euler(angle, 0f, 0f);
            if (rig.viewKnife != null)
                rig.viewKnife.localRotation = rig.viewRestRot * Quaternion.Euler(angle * 0.8f, 0f, 0f);
        }

        private static void ResetPose(Rig rig)
        {
            rig.swinging = false;
            if (rig.worldKnife != null) rig.worldKnife.localRotation = rig.worldRestRot;
            if (rig.viewKnife != null) rig.viewKnife.localRotation = rig.viewRestRot;
        }

        // 举起→劈下→回位三段角度曲线
        private static float SwingAngle(float t)
        {
            if (t < 0.3f) return Mathf.Lerp(0f, SwingWindupDeg, t / 0.3f);
            if (t < 0.6f) return Mathf.Lerp(SwingWindupDeg, SwingSlashDeg, Mathf.Clamp01((t - 0.3f) / 0.3f));
            return Mathf.Lerp(SwingSlashDeg, 0f, Mathf.Clamp01((t - 0.6f) / 0.4f));
        }

        // ===== 刀体拼装 =====

        // 世界刀：挂 HandPosition 手骨，持握位姿优先对齐 ItemHandPos（手持物品的权威参考位，书/食物都落在那），
        // 缺省用手骨局部常数；自己那把走 PlayerSelf（本机相机剔除），别人的走 Player（对端可见）
        private static Transform BuildHandKnife(Unit unit)
        {
            Transform hand = unit.GetPartRoot(EUnitPartType.HandPosition);
            if (hand == null) return null; // 模型部件还没绑好
            var root = new GameObject("HunterKnife_" + unit.UID);
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
            BuildParts(root.transform, Quaternion.Euler(KnifeModelEuler));
            int layer = LayerMask.NameToLayer(unit.isLocalPlayer ? "PlayerSelf" : "Player");
            if (layer >= 0) SetLayerRecursive(root, layer); // 部件挂完再统一设层
            return root.transform;
        }

        // 本地猎手的第一人称 viewmodel：挂主相机，无层调整（Default 层本机可见）
        private static Transform BuildViewModel()
        {
            if (!MonoSingletonT<SceneManager>.IsSingletonActive()) return null;
            Camera camera = MonoSingletonT<SceneManager>.Instance.CameraCtr?.MainCamera;
            if (camera == null) return null;
            var root = new GameObject("HunterKnifeView");
            root.transform.SetParent(camera.transform, false);
            root.transform.localPosition = ViewLocalPos;
            root.transform.localRotation = Quaternion.Euler(ViewLocalEuler);
            BuildParts(root.transform, Quaternion.identity);
            return root.transform;
        }

        private static Transform CaptureViewKnife(Rig rig)
        {
            Transform knife = BuildViewModel();
            if (knife != null) rig.viewRestRot = knife.localRotation;
            return knife;
        }

        // 菜刀造型：宽刃+刀镡+短柄，root 即握点；modelEuler 控制刀刃朝向（调位姿只动这层）
        private static void BuildParts(Transform root, Quaternion modelEuler)
        {
            EnsureMaterials();
            var model = new GameObject("model");
            model.transform.SetParent(root, false);
            model.transform.localRotation = modelEuler;
            CreatePart(model.transform, "blade", new Vector3(0f, 0.17f, 0f), new Vector3(0.06f, 0.24f, 0.006f), bladeMaterial);
            CreatePart(model.transform, "guard", new Vector3(0f, 0.04f, 0f), new Vector3(0.075f, 0.025f, 0.022f), guardMaterial);
            CreatePart(model.transform, "handle", new Vector3(0f, -0.05f, 0f), new Vector3(0.03f, 0.12f, 0.03f), handleMaterial);
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
            if (bladeMaterial != null) return;
            bladeMaterial = Tint(new Color(0.72f, 0.75f, 0.80f), 0.7f);   // 银灰刀刃
            guardMaterial = Tint(new Color(0.20f, 0.20f, 0.24f), 0.4f);   // 深灰刀镡
            handleMaterial = Tint(new Color(0.16f, 0.13f, 0.11f), 0.25f); // 深棕刀柄
        }

        private static Material Tint(Color color, float smoothness)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit"); // 游戏 URP 管线
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return null; // 全找不到就留默认材质
            var material = new Material(shader);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            return material;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursive(child.gameObject, layer);
        }
    }
}
