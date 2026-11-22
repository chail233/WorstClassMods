using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BioChem
{
    /// <summary>
    /// 学生近战尼泊尔军刀（按 3）：CF 军刀手感的表现层——左键轻击（可长按连击，快弧线）、右键重击（慢上段大弧线）。
    /// 各端本地创建，无网络对象；显隐按全员槽位广播（RemoteSlotOf==Melee），与 HunterKnife 同一套骨架：
    /// - 世界刀挂 HandPosition 手骨（自己的走 PlayerSelf 层被本机相机剔除，不与 viewmodel 重复）
    /// - 本地玩家选中近战时额外挂相机 viewmodel
    /// - 挥动：PlaySwing(uid, heavy) 记时间戳，Tick 按进度转刀（轻击 0.3s 快弧线 / 重击 0.6s 上段大弧线）；
    ///   身体挥动动画由攻击者 PlayAnimSync("Throw") 原生同步
    /// - 模型：ModDirectory/Assets/kukri.obj（CC0）运行时解析；缺失/失败回退程序化弯刃（轮廓挤出网格）
    /// </summary>
    internal static class StudentKukri
    {
        private const float LightSwingDuration = 0.3f;   // 轻击表现时长（与 kukriLightHitDelay 0.25s 对齐：命中在弧线末端）
        private const float HeavySwingDuration = 0.6f;   // 重击表现时长（与 kukriHeavyHitDelay 0.55s 对齐）
        private const float LightWindupDeg = -40f;
        private const float LightSlashDeg = 85f;
        private const float HeavyWindupDeg = -85f;       // 重击大幅上举过头
        private const float HeavySlashDeg = 115f;
        private const float ModelLength = 0.5f;          // 军刀模型归一化长度（米）
        private const float LightPivotDistance = 0.30f;  // 轻击挥刀支点离刀根的距离（沿刀背方向）
        private const float HeavyPivotDistance = 0.38f;  // 重击支点更远：斜劈弧线更大
        private static readonly Vector3 SwingAxis = new Vector3(1f, -0.7f, 0f).normalized; // 斜劈轴：右上举刀→向左下劈出
        private static readonly Color TrailColor = new Color(0.72f, 0.88f, 1f); // 挥刀轨迹：钢青色
        private const float TrailWidth = 0.06f;
        // 刀体在手部/相机空间的基础位姿（世界刀优先对齐 ItemHandPos 参考位）
        private static readonly Vector3 HandLocalPos = new Vector3(0.02f, 0.05f, 0.01f);
        private static readonly Vector3 HandLocalEuler = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 BladeModelEuler = new Vector3(90f, 0f, 0f); // 模型层：刀尖(+Y)指向挂点前方(+Z)
        private static readonly Vector3 ViewLocalPos = new Vector3(0.34f, -0.32f, 0.60f);
        private static readonly Vector3 ViewLocalEuler = new Vector3(28f, -14f, 6f);

        private sealed class Rig
        {
            public Transform worldKnife;
            public Vector3 worldRestPos;   // 静止位姿（挥刀绕支点公转的基准，结束恢复）
            public Quaternion worldRestRot;
            public Transform viewKnife;
            public Vector3 viewRestPos;
            public Quaternion viewRestRot;
            public TrailRenderer worldTrail; // 刀尖拖尾（挥刀轨迹，随刀销毁）
            public TrailRenderer viewTrail;
            public float swingStartAt = -999f;
            public bool swinging;
            public bool heavy;          // 本次挥击是重击（决定曲线）
            public float nextBuildTryAt;
        }

        private static readonly Dictionary<ulong, Rig> rigs = new Dictionary<ulong, Rig>();
        private static readonly List<ulong> prune = new List<ulong>();
        private static readonly List<ulong> rebuild = new List<ulong>();

        private static string modDir;
        private static Transform templateKnife;
        private static bool modelTried;

        internal static void Init(string modDirectory)
        {
            modDir = modDirectory;
        }

        // ===== 生命周期（全幂等） =====

        /// <summary>确保该玩家有军刀：模型/手骨未就绪时静默跳过（Tick 自愈会重试）。</summary>
        internal static void Ensure(Unit unit)
        {
            if (unit == null || !unit.isActived || unit.model == null) return;
            if (rigs.TryGetValue(unit.UID, out Rig rig) && rig.worldKnife != null)
            {
                if (unit.isLocalPlayer && rig.viewKnife == null) CaptureViewKnife(rig);
                return;
            }
            Remove(unit.UID); // 清掉半残 rig（比如只剩 viewKnife）
            rig = new Rig();
            rig.worldKnife = BuildHandKnife(unit);
            if (rig.worldKnife != null)
            {
                rig.worldRestPos = rig.worldKnife.localPosition;
                rig.worldRestRot = rig.worldKnife.localRotation;
                rig.worldTrail = rig.worldKnife.GetComponentInChildren<TrailRenderer>();
                ModEntry.Log?.Invoke("[状态] " + ModEntry.GetName(unit.UID) + " 已持尼泊尔军刀");
            }
            if (unit.isLocalPlayer)
                CaptureViewKnife(rig);
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

        /// <summary>挥刀表现入口（uid=攻击者）：攻击者本地预测与其他端收到 Swing 广播共用。</summary>
        internal static void PlaySwing(ulong uid, bool heavy)
        {
            if (!rigs.TryGetValue(uid, out Rig rig)) return;
            rig.swingStartAt = Time.unscaledTime;
            rig.swinging = true;
            rig.heavy = heavy;
            float duration = heavy ? HeavySwingDuration : LightSwingDuration;
            WeaponModels.EmitSlashTrail(rig.worldTrail, duration); // 拖尾寿命=挥刀时长：弧线全程可见，停手淡出
            WeaponModels.EmitSlashTrail(rig.viewTrail, duration);
            ApplySwingPose(rig, 0f, heavy);
        }

        // ===== 每帧驱动 =====

        private static float scanAt;

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now >= scanAt)
            {
                scanAt = now + 0.5f;
                ScanHolders(); // 周期扫描持刀者：切槽/晚加入/广播补发都能建刀（Ensure 幂等）
            }
            if (rigs.Count == 0) return;
            prune.Clear();
            rebuild.Clear();
            foreach (var pair in rigs)
            {
                ulong uid = pair.Key;
                Rig rig = pair.Value;
                if (!HoldsKukri(uid)) { prune.Add(uid); continue; } // 切槽/感染/退房漏清的兜底
                float duration = rig.heavy ? HeavySwingDuration : LightSwingDuration;
                float t = (now - rig.swingStartAt) / duration;
                if (t < 1f) ApplySwingPose(rig, t, rig.heavy);
                else if (rig.swinging) ResetPose(rig);
                if (now >= rig.nextBuildTryAt)
                {
                    bool needWorld = rig.worldKnife == null;
                    bool needView = rig.viewKnife == null
                                    && UnitManager.IsSingletonActive()
                                    && UnitManager.Instance.localPlayerUID == uid;
                    if (needWorld || needView)
                    {
                        rebuild.Add(uid);
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

        // 周期扫描：给所有当前应持军刀的玩家补建刀（幂等，已有的跳过）
        private static void ScanHolders()
        {
            if (!ModEntry.enabled || !UnitManager.IsSingletonActive()) return;
            foreach (Unit unit in UnitManager.Instance.playerList)
            {
                if (unit == null || !unit.isActived || unit.model == null) continue;
                if (HoldsKukri(unit.UID)) Ensure(unit);
            }
        }

        // 该玩家当前是否应持军刀：本地看 Selected，远端看全员槽位视图
        private static bool HoldsKukri(ulong uid)
        {
            if (!ModEntry.enabled || !UnitManager.IsSingletonActive()) return false;
            Unit unit = UnitManager.Instance.GetUnitByUID(uid);
            if (unit == null || !unit.isActived) return false;
            return unit.isLocalPlayer
                ? StudentWeapons.Selected == StudentWeaponSlot.Melee && StudentWeapons.CanSelect(unit)
                : StudentWeapons.RemoteSlotOf(uid) == StudentWeaponSlot.Melee;
        }

        private static void ApplySwingPose(Rig rig, float t, bool heavy)
        {
            float angle = SwingAngle(t, heavy);
            float zTilt = heavy ? angle * -0.25f : 0f;
            float pivotDist = heavy ? HeavyPivotDistance : LightPivotDistance;
            if (rig.worldKnife != null)
                WeaponModels.ApplySwingPose(rig.worldKnife, rig.worldRestPos, rig.worldRestRot, angle, zTilt, pivotDist, SwingAxis);
            if (rig.viewKnife != null)
                WeaponModels.ApplySwingPose(rig.viewKnife, rig.viewRestPos, rig.viewRestRot, angle * 0.8f, heavy ? angle * -0.2f : 0f, pivotDist, SwingAxis);
        }

        private static void ResetPose(Rig rig)
        {
            rig.swinging = false;
            WeaponModels.StopSlashTrail(rig.worldTrail); // 弧线按 time 自然淡出
            WeaponModels.StopSlashTrail(rig.viewTrail);
            if (rig.worldKnife != null)
            {
                rig.worldKnife.localPosition = rig.worldRestPos;
                rig.worldKnife.localRotation = rig.worldRestRot;
            }
            if (rig.viewKnife != null)
            {
                rig.viewKnife.localPosition = rig.viewRestPos;
                rig.viewKnife.localRotation = rig.viewRestRot;
            }
        }

        // 轻击：快弧线（举→劈→回位）；重击：先大幅上举过头再劈下（CF 军刀重击的上段动作）
        private static float SwingAngle(float t, bool heavy)
        {
            if (heavy)
            {
                float windup = HeavyWindupDeg, slash = HeavySlashDeg;
                if (t < 0.35f) return Mathf.Lerp(0f, windup, t / 0.35f);
                if (t < 0.7f) return Mathf.Lerp(windup, slash, Mathf.Clamp01((t - 0.35f) / 0.35f));
                return Mathf.Lerp(slash, 0f, Mathf.Clamp01((t - 0.7f) / 0.3f));
            }
            float lWindup = LightWindupDeg, lSlash = LightSlashDeg;
            if (t < 0.25f) return Mathf.Lerp(0f, lWindup, t / 0.25f);
            if (t < 0.55f) return Mathf.Lerp(lWindup, lSlash, Mathf.Clamp01((t - 0.25f) / 0.3f));
            return Mathf.Lerp(lSlash, 0f, Mathf.Clamp01((t - 0.55f) / 0.45f));
        }

        // ===== 刀体拼装 =====

        private static Transform BuildHandKnife(Unit unit)
        {
            Transform hand = unit.GetPartRoot(EUnitPartType.HandPosition);
            if (hand == null) return null;
            if (!EnsureModelTemplate()) return null;
            Transform root = Object.Instantiate(templateKnife);
            root.name = "StudentKukri_" + unit.UID;
            root.SetParent(hand, false);
            Transform itemRef = unit.GetPartRoot(EUnitPartType.ItemHandPos);
            if (itemRef != null)
            {
                root.position = itemRef.position;
                root.rotation = itemRef.rotation;
            }
            else
            {
                root.localPosition = HandLocalPos;
                root.localRotation = Quaternion.Euler(HandLocalEuler);
            }
            root.gameObject.SetActive(true);
            int layer = LayerMask.NameToLayer(unit.isLocalPlayer ? "PlayerSelf" : "Player");
            if (layer >= 0) WeaponModels.SetLayerRecursive(root.gameObject, layer);
            return root;
        }

        private static void CaptureViewKnife(Rig rig)
        {
            if (!MonoSingletonT<SceneManager>.IsSingletonActive()) return;
            Camera camera = MonoSingletonT<SceneManager>.Instance.CameraCtr?.MainCamera;
            if (camera == null) return;
            if (!EnsureModelTemplate()) return;
            Transform knife = Object.Instantiate(templateKnife);
            knife.name = "StudentKukriView";
            knife.SetParent(camera.transform, false);
            knife.localPosition = ViewLocalPos;
            knife.localRotation = Quaternion.Euler(ViewLocalEuler);
            knife.gameObject.SetActive(true);
            rig.viewKnife = knife;
            rig.viewRestPos = knife.localPosition;
            rig.viewRestRot = knife.localRotation;
            rig.viewTrail = knife.GetComponentInChildren<TrailRenderer>();
        }

        private static bool EnsureModelTemplate()
        {
            if (templateKnife != null) return true;
            if (modelTried) return false;
            modelTried = true;
            try
            {
                string objPath = string.IsNullOrEmpty(modDir) ? null : Path.Combine(modDir, "Assets", "kukri.obj");
                templateKnife = objPath != null && File.Exists(objPath)
                    ? WeaponModels.LoadObj(objPath, "KukriModel", new Color(0.72f, 0.75f, 0.80f), 0.7f, ModelLength, 0f, null, out _,
                        textureFile: string.IsNullOrEmpty(modDir) ? null : Path.Combine(modDir, "Assets", "kukri.png"))
                    : null;
                if (templateKnife != null)
                    ModEntry.Log?.Invoke("[状态] 尼泊尔军刀模型已加载（OBJ）");
            }
            catch (System.Exception e)
            {
                ModEntry.Log?.Invoke("[状态] 军刀模型加载失败，回退程序化弯刃：" + e.Message);
                templateKnife = null;
            }
            if (templateKnife == null) templateKnife = BuildProceduralKukri();
            AttachTemplateTrail(); // 拖尾挂模板随实例走，刀尖取网格包围盒 +Z 端
            templateKnife.gameObject.SetActive(false);
            Object.DontDestroyOnLoad(templateKnife);
            return templateKnife != null;
        }

        private static void AttachTemplateTrail()
        {
            MeshRenderer renderer = templateKnife.GetComponentInChildren<MeshRenderer>();
            if (renderer == null) return;
            Bounds bounds = renderer.bounds;
            Vector3 tip = templateKnife.InverseTransformPoint(bounds.center + Vector3.forward * bounds.extents.z);
            WeaponModels.AttachSlashTrail(templateKnife, tip, TrailColor, TrailWidth);
        }

        // 程序化兜底军刀：宽腹弯刃（侧影轮廓挤出）+ 刀镡 + 弯柄。模型层 Euler(90,0,0) 把刀尖(+Y)转到 +Z 前方
        private static Transform BuildProceduralKukri()
        {
            var root = new GameObject("KukriModel");
            var model = new GameObject("model");
            model.transform.SetParent(root.transform, false);
            model.transform.localRotation = Quaternion.Euler(BladeModelEuler);
            var blade = new GameObject("blade");
            blade.transform.SetParent(model.transform, false);
            blade.transform.localPosition = new Vector3(0f, 0.02f, 0f);
            blade.AddComponent<MeshFilter>().sharedMesh = WeaponModels.BuildExtrudedMesh(new[]
            {
                new Vector2(-0.012f, 0.00f),  // 脊侧根部（沿 +Y 到刀尖，+X 为开刃侧）
                new Vector2(-0.014f, 0.08f),
                new Vector2(-0.014f, 0.16f),
                new Vector2(-0.011f, 0.24f),
                new Vector2(-0.005f, 0.30f),
                new Vector2(0.000f, 0.365f),  // 刀尖
                new Vector2(0.012f, 0.31f),   // 刃侧回程
                new Vector2(0.024f, 0.25f),
                new Vector2(0.033f, 0.18f),   // 肚腹最宽（库里克弯腹）
                new Vector2(0.034f, 0.11f),
                new Vector2(0.025f, 0.045f),
                new Vector2(0.014f, 0.00f)
            }, 0.012f);
            blade.AddComponent<MeshRenderer>().sharedMaterial = WeaponModels.Tint(new Color(0.72f, 0.75f, 0.80f), 0.7f);
            // 刃口亮线（贴在开刃侧的细条）
            WeaponModels.CreatePart(model.transform, "edge", new Vector3(0.030f, 0.16f, 0f), new Vector3(0.004f, 0.30f, 0.014f),
                WeaponModels.Tint(new Color(0.88f, 0.90f, 0.94f), 0.85f), new Vector3(6f, 0f, 0f));
            WeaponModels.CreatePart(model.transform, "guard", new Vector3(0f, 0.005f, 0f), new Vector3(0.06f, 0.018f, 0.036f),
                WeaponModels.Tint(new Color(0.20f, 0.20f, 0.24f), 0.4f));
            WeaponModels.CreatePart(model.transform, "handle", new Vector3(0f, -0.055f, 0f), new Vector3(0.028f, 0.11f, 0.032f),
                WeaponModels.Tint(new Color(0.16f, 0.13f, 0.11f), 0.25f), new Vector3(4f, 0f, 0f));
            WeaponModels.CreatePart(model.transform, "pommel", new Vector3(0f, -0.113f, 0f), new Vector3(0.034f, 0.02f, 0.038f),
                WeaponModels.Tint(new Color(0.35f, 0.30f, 0.18f), 0.5f));
            return root.transform;
        }
    }
}
