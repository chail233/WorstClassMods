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
    /// - 轻击在身前横扫，重击抬高后斜劈。身体挥动动画由攻击者 PlayAnimSync("Throw") 原生同步
    /// - 模型：ModDirectory/Assets/kukri.obj（CC0）运行时解析；缺失/失败回退程序化弯刃（轮廓挤出网格）
    /// </summary>
    internal static class StudentKukri
    {
        private const float ModelLength = 0.5f;          // 军刀模型归一化长度（米）
        private static readonly Color TrailColor = new Color(0.72f, 0.88f, 1f); // 挥刀轨迹：钢青色
        private const float TrailWidth = 0.06f;
        // 世界刀优先对齐 ItemHandPos 参考位
        private static readonly Vector3 HandLocalPos = new Vector3(0.02f, 0.05f, 0.01f);
        private static readonly Vector3 HandLocalEuler = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 BladeModelEuler = new Vector3(90f, 0f, 0f); // 模型层：刀尖(+Y)指向挂点前方(+Z)

        private static readonly Dictionary<ulong, MeleeSwing> rigs = new Dictionary<ulong, MeleeSwing>();
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
            if (!rigs.TryGetValue(unit.UID, out MeleeSwing rig))
            {
                rig = new MeleeSwing(unit);
                rigs.Add(unit.UID, rig);
            }
            if (rig.World == null)
            {
                rig.BindWorld(BuildHandKnife(unit));
                if (rig.World != null)
                    ModEntry.Log?.Invoke("[状态] " + ModEntry.GetName(unit.UID) + " 已持尼泊尔军刀");
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

        /// <summary>挥刀表现入口（uid=攻击者）：攻击者本地预测与其他端收到 Swing 广播共用。</summary>
        internal static void PlaySwing(ulong uid, bool heavy)
        {
            if (!rigs.ContainsKey(uid) && UnitManager.IsSingletonActive())
                Ensure(UnitManager.Instance.GetUnitByUID(uid));
            if (!rigs.TryGetValue(uid, out MeleeSwing rig)) return;
            float interval = heavy ? ModEntry.Config.kukriHeavyCooldown : ModEntry.Config.kukriLightInterval;
            float hitDelay = heavy ? ModEntry.Config.kukriHeavyHitDelay : ModEntry.Config.kukriLightHitDelay;
            // 默认在命中那一刻扫到末端。高攻速配置下压缩动作，保证下一击前能完成横扫。
            float contact = Mathf.Min(hitDelay, interval * 0.85f);
            float recovery = Mathf.Min(heavy ? 0.15f : 0.11f, interval - contact);
            rig.Play(contact, recovery, overhead: heavy);
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
                MeleeSwing rig = pair.Value;
                if (!HoldsKukri(uid)) { prune.Add(uid); continue; }
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

        private static Transform BuildViewModel()
        {
            if (!MonoSingletonT<SceneManager>.IsSingletonActive()) return null;
            Camera camera = MonoSingletonT<SceneManager>.Instance.CameraCtr?.MainCamera;
            if (camera == null) return null;
            if (!EnsureModelTemplate()) return null;
            Transform knife = Object.Instantiate(templateKnife);
            knife.name = "StudentKukriView";
            knife.SetParent(camera.transform, false);
            knife.gameObject.SetActive(true);
            return knife;
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
