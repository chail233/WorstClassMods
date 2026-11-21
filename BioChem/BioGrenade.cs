using System;
using System.Collections.Generic;
using UnityEngine;

namespace BioChem
{
    /// <summary>
    /// 生化手雷（按 4）表现层：程序化弹体（橄榄绿弹体+红带+引信帽+握片）+ 本地 Rigidbody 物理模拟（抛物线/反弹）。
    /// 结算模型与原生闪光弹同构：出手后各端各自模拟弹道（纯表现），服务端（房主）用自己的物理模拟在引信到点时
    /// 以权威位置广播 Explode——各端销毁本地弹体并播放爆炸（SpawnFx 100005 闪光弹爆炸特效 + sfx_Flash_sfx 原生音效），
    /// 范围扣血由服务端在广播前结算。Seq 用于匹配销毁（同一玩家弹体重叠期唯一）。
    /// </summary>
    internal static class BioGrenade
    {
        private const float BodyRadius = 0.085f;
        private const float ClientSimMaxLife = 10f; // 客户端模拟兜底寿命（Explode 丢失也不留飞行残骸）
        private const float ShakeRange = 8f;        // 爆炸震屏的最大距离

        private sealed class LiveGrenade
        {
            public ulong owner;
            public uint seq;
            public GameObject body;
            public Rigidbody rigid;
            public bool hostSim;        // 服务端权威模拟（引信结算用它的位置）
            public double explodeAt;    // hostSim：引信到点时刻
            public float spawnAt;
        }

        private static readonly List<LiveGrenade> grenades = new List<LiveGrenade>();
        private static Material bodyMaterial, bandMaterial, capMaterial, leverMaterial;

        // ===== 出手：各端本地模拟 =====

        /// <summary>生成一颗本地模拟手雷。hostSim=true 时带引信（服务端到点回调 TickServerFuse）。</summary>
        internal static void SpawnLocal(ulong owner, uint seq, Vector3 origin, Vector3 velocity, bool hostSim, float fuseSeconds)
        {
            if (!UnitManager.IsSingletonActive()) return;
            var live = new LiveGrenade
            {
                owner = owner,
                seq = seq,
                hostSim = hostSim,
                explodeAt = Time.unscaledTimeAsDouble + Mathf.Max(0.1f, fuseSeconds),
                spawnAt = Time.unscaledTime
            };
            var go = new GameObject("BioGrenade_" + owner + "_" + seq);
            go.transform.position = origin;
            go.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            BuildBody(go.transform);

            var collider = go.AddComponent<SphereCollider>();
            collider.radius = BodyRadius;
            var rigid = go.AddComponent<Rigidbody>();
            rigid.mass = 0.4f;
            rigid.interpolation = RigidbodyInterpolation.Interpolate;
            rigid.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rigid.velocity = velocity;
            live.body = go;
            live.rigid = rigid;
            grenades.Add(live);

            // 出手瞬间忽略与投掷者的碰撞（原生 DropItemTowards 同款体验）
            Unit thrower = UnitManager.Instance.GetUnitByUID(owner);
            if (thrower != null)
                foreach (var col in thrower.gameObject.GetComponentsInChildren<Collider>())
                    Physics.IgnoreCollision(collider, col);
        }

        /// <summary>爆炸表现+销毁匹配弹体：position 是服务端权威爆炸点（本地模拟位置可能略有偏差）。</summary>
        internal static void ExplodeLocal(ulong owner, uint seq, Vector3 position)
        {
            RemoveGrenade(owner, seq);
            PlayExplosion(position);
        }

        private static void RemoveGrenade(ulong owner, uint seq)
        {
            for (int i = grenades.Count - 1; i >= 0; i--)
            {
                var live = grenades[i];
                if (live.owner != owner) continue;
                // Seq 匹配优先；丢失 Spawn 的情况下至少不残留该玩家的旧弹体
                if (live.seq != seq && seq != 0) continue;
                if (live.body != null) UnityEngine.Object.Destroy(live.body);
                grenades.RemoveAt(i);
            }
        }

        private static void PlayExplosion(Vector3 position)
        {
            Vector3 center = position + Vector3.up * 0.2f;
            SingletonT<FxManager>.Instance.SpawnFx(100005, center, Vector3.zero, 0uL); // 原生闪光弹爆炸特效
            if (MonoSingletonT<AudioManager>.IsSingletonActive())
                MonoSingletonT<AudioManager>.Instance.PlayOneShot(AudioEvents.sfx_Flash_sfx, position); // 原生闪光弹爆音
            // 近距离小震屏（复用老师剑命中震屏曲线，随距离衰减）
            if (MonoSingletonT<SceneManager>.IsSingletonActive())
            {
                Camera camera = MonoSingletonT<SceneManager>.Instance.MainCamera;
                if (camera != null)
                {
                    float dist = Vector3.Distance(camera.transform.position, position);
                    if (dist < ShakeRange) HunterFeedback.PlayShake(0.6f * (1f - dist / ShakeRange));
                }
            }
        }

        // ===== 每帧 =====

        private static float heldScanAt;

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            TickHeld(now);
            for (int i = grenades.Count - 1; i >= 0; i--)
            {
                var live = grenades[i];
                if (live.body == null) { grenades.RemoveAt(i); continue; } // 场景切换等外部销毁
                if (!live.hostSim && now - live.spawnAt > ClientSimMaxLife)
                {
                    UnityEngine.Object.Destroy(live.body); // 客户端模拟兜底回收（Explode 丢失）
                    grenades.RemoveAt(i);
                }
            }
        }

        // ===== 手持模型（选中投掷物且还有雷的玩家手上，其余玩家可见；本地额外挂 viewmodel） =====

        private sealed class HeldRig
        {
            public Transform world;
            public Transform view;
            public float nextBuildTryAt;
        }

        private static readonly Dictionary<ulong, HeldRig> held = new Dictionary<ulong, HeldRig>();
        private static readonly Vector3 HeldHandPos = new Vector3(0.02f, 0.05f, 0.01f);
        private static readonly Vector3 HeldHandEuler = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 HeldViewPos = new Vector3(0.34f, -0.30f, 0.55f);
        private static readonly Vector3 HeldViewEuler = new Vector3(10f, -15f, 0f);

        private static bool HoldsGrenade(ulong uid)
        {
            if (!ModEntry.enabled || !UnitManager.IsSingletonActive()) return false;
            Unit unit = UnitManager.Instance.GetUnitByUID(uid);
            if (unit == null || !unit.isActived || unit.IsDead) return false;
            if (unit.isLocalPlayer)
                return StudentWeapons.Selected == StudentWeaponSlot.Throwable
                    && StudentWeapons.CanSelect(unit) && ModEntry.SelfGrenadeCount > 0;
            return StudentWeapons.RemoteSlotOf(uid) == StudentWeaponSlot.Throwable && ModEntry.GrenadeCountView(uid) > 0;
        }

        private static void TickHeld(float now)
        {
            if (now >= heldScanAt)
            {
                heldScanAt = now + 0.5f;
                if (ModEntry.enabled && UnitManager.IsSingletonActive())
                    foreach (Unit unit in UnitManager.Instance.playerList)
                        if (unit != null && unit.isActived && unit.model != null && HoldsGrenade(unit.UID))
                            EnsureHeld(unit);
            }
            foreach (ulong uid in new List<ulong>(held.Keys))
            {
                if (!HoldsGrenade(uid)) { RemoveHeld(uid); continue; } // 投完/切槽漏清的兜底
                if (held.TryGetValue(uid, out var rig) && now >= rig.nextBuildTryAt && (rig.world == null || (rig.view == null && IsLocal(uid))))
                {
                    rig.nextBuildTryAt = now + 0.5f;
                    if (UnitManager.IsSingletonActive())
                    {
                        Unit unit = UnitManager.Instance.GetUnitByUID(uid);
                        if (unit != null && unit.isActived && unit.model != null) EnsureHeld(unit);
                    }
                }
            }
        }

        private static bool IsLocal(ulong uid)
            => UnitManager.IsSingletonActive() && UnitManager.Instance.localPlayerUID == uid;

        private static void EnsureHeld(Unit unit)
        {
            if (!held.TryGetValue(unit.UID, out HeldRig rig)) { rig = new HeldRig(); held[unit.UID] = rig; }
            if (rig.world == null)
            {
                Transform hand = unit.GetPartRoot(EUnitPartType.HandPosition);
                if (hand != null)
                {
                    var root = new GameObject("BioGrenadeHeld_" + unit.UID);
                    root.transform.SetParent(hand, false);
                    root.transform.localPosition = HeldHandPos;
                    root.transform.localRotation = Quaternion.Euler(HeldHandEuler);
                    BuildBody(root.transform);
                    root.SetActive(true);
                    int layer = LayerMask.NameToLayer(unit.isLocalPlayer ? "PlayerSelf" : "Player");
                    if (layer >= 0) WeaponModels.SetLayerRecursive(root, layer);
                    rig.world = root.transform;
                }
            }
            if (unit.isLocalPlayer && rig.view == null && MonoSingletonT<SceneManager>.IsSingletonActive())
            {
                Camera camera = MonoSingletonT<SceneManager>.Instance.CameraCtr?.MainCamera
                    ?? MonoSingletonT<SceneManager>.Instance.MainCamera;
                if (camera != null)
                {
                    var root = new GameObject("BioGrenadeHeldView");
                    root.transform.SetParent(camera.transform, false);
                    root.transform.localPosition = HeldViewPos;
                    root.transform.localRotation = Quaternion.Euler(HeldViewEuler);
                    BuildBody(root.transform);
                    root.SetActive(true);
                    rig.view = root.transform;
                }
            }
        }

        private static void RemoveHeld(ulong uid)
        {
            if (!held.TryGetValue(uid, out HeldRig rig)) return;
            held.Remove(uid);
            if (rig.world != null) UnityEngine.Object.Destroy(rig.world.gameObject);
            if (rig.view != null) UnityEngine.Object.Destroy(rig.view.gameObject);
        }

        /// <summary>服务端引信结算：到点的 host 模拟弹体取权威位置并销毁，逐颗回调（ModEntry 做 AOE 结算+广播）。</summary>
        internal static void TickServerFuse(Action<ulong, uint, Vector3> onExplode)
        {
            double now = Time.unscaledTimeAsDouble;
            for (int i = grenades.Count - 1; i >= 0; i--)
            {
                var live = grenades[i];
                if (!live.hostSim || live.body == null || live.rigid == null) continue;
                if (now < live.explodeAt) continue;
                Vector3 position = live.rigid.position;
                UnityEngine.Object.Destroy(live.body);
                grenades.RemoveAt(i);
                onExplode?.Invoke(live.owner, live.seq, position);
            }
        }

        // ===== 生命周期 =====

        internal static void RemoveOwner(ulong uid)
        {
            for (int i = grenades.Count - 1; i >= 0; i--)
            {
                if (grenades[i].owner != uid) continue;
                if (grenades[i].body != null) UnityEngine.Object.Destroy(grenades[i].body);
                grenades.RemoveAt(i);
            }
            RemoveHeld(uid); // 手持模型一并收
        }

        internal static void ResetForNewRound()
        {
            foreach (var live in grenades)
                if (live.body != null) UnityEngine.Object.Destroy(live.body);
            grenades.Clear();
            foreach (ulong uid in new List<ulong>(held.Keys)) RemoveHeld(uid);
        }

        // ===== 弹体拼装（程序化：橄榄绿弹体+红带+引信帽+握片，无 Collider——碰撞体在根节点） =====

        private static void BuildBody(Transform root)
        {
            EnsureMaterials();
            var body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            UnityEngine.Object.Destroy(body.GetComponent<Collider>()); // 碰撞体在根节点统一管
            body.name = "body";
            body.transform.SetParent(root, false);
            body.transform.localScale = new Vector3(BodyRadius * 2f, BodyRadius * 2.3f, BodyRadius * 2f);
            body.GetComponent<MeshRenderer>().sharedMaterial = bodyMaterial;
            WeaponModels.CreatePart(root, "band", new Vector3(0f, 0.01f, 0f), new Vector3(BodyRadius * 2.05f, 0.02f, BodyRadius * 2.05f), bandMaterial);
            var cap = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            UnityEngine.Object.Destroy(cap.GetComponent<Collider>());
            cap.name = "cap";
            cap.transform.SetParent(root, false);
            cap.transform.localPosition = new Vector3(0f, BodyRadius * 1.15f, 0f);
            cap.transform.localScale = new Vector3(0.045f, 0.012f, 0.045f);
            cap.GetComponent<MeshRenderer>().sharedMaterial = capMaterial;
            WeaponModels.CreatePart(root, "lever", new Vector3(0.03f, BodyRadius * 1.05f, 0f), new Vector3(0.012f, 0.05f, 0.024f), leverMaterial, new Vector3(0f, 0f, 18f));
        }

        private static void EnsureMaterials()
        {
            if (bodyMaterial != null) return;
            bodyMaterial = WeaponModels.Tint(new Color(0.24f, 0.32f, 0.18f), 0.35f); // 橄榄绿弹体
            bandMaterial = WeaponModels.Tint(new Color(0.75f, 0.15f, 0.12f), 0.4f);  // 红色识别带
            capMaterial = WeaponModels.Tint(new Color(0.55f, 0.52f, 0.45f), 0.5f);   // 金属引信帽
            leverMaterial = WeaponModels.Tint(new Color(0.62f, 0.60f, 0.55f), 0.5f); // 握片
        }
    }
}
