using System.Collections.Generic;
using System.IO;
using FMOD;
using FMODUnity;
using UnityEngine;

namespace BioChem
{
    /// <summary>
    /// 学生连发吹箭的 AK47 表现层：手持枪模型（世界枪+第一人称 viewmodel）、口焰、后坐、曳光弹、真枪声、30 发弹匣+R 换弹。
    /// 核心原则：原生 Taser 管线全保留（物品/TryBuildShot/taserShots 注册表/OnTaserImpact 伤害减速/AmmoSync）——
    /// 原生单发已移除，Taser 管线只有 mod 连发在用，因此整体替换表现层不动网络与平衡：
    /// - 弹道视觉：换装 ConfigManager.TaserSetting.projectilePrefab 为本地构建的曳光弹（PlayFire 实例化/飞行/命中特效走原生）
    /// - 枪声：PlayFire 生成 AK 弹体时，由 FMOD CoreSystem 以 3D 方式播放打包的枪声（文件缺失时合成 PCM）。
    /// - 枪口起点：射击方向由当前枪口和屏幕中心射线确定，本地预测与远端曳光同源。
    /// - 弹匣：纯客户端表现层（MagSize 发打空自动换弹、R 手动换弹、AmmoSync 校准时 clamp），备弹仍是服务端权威
    /// - 模型：ModDirectory/Assets/ak47.obj（CC0，OpenGameArt Low Poly Weapon Pack）运行时解析；缺失/失败回退程序化拼装
    /// 显隐规则：嘴上挂着 Taser 物品 = 手持 AK（世界枪挂 HandPosition + 本机 viewmodel），同时 Taser 模型渲染器隐藏。
    /// </summary>
    internal static class Ak47Weapon
    {
        internal const int MagSize = 30;
        internal const float ReloadSeconds = 2.0f;
        private const float WorldScanInterval = 0.25f;
        private const float FlashDuration = 0.05f;
        private const float RecoilPosKick = 0.05f;   // viewmodel 后坐（沿 -Z 位移冲量）
        private const float RecoilRotKick = 7f;      // 后坐上抬角度
        private const float ModelLength = 0.9f;      // 模型归一化长度（米）
        // 打包 OBJ（CS1.6 模型）文件坐标里的枪口环中心：枪口沿 -Z，LoadObj 按 180° yaw 转到 +Z（再 roll 180° 正过来）
        private static readonly Vector3 ObjMuzzle = new Vector3(-0.069f, 2.473f, -18.290f);
        // viewmodel 在相机空间的位姿（参考老师剑 idleCameraOffset (0.35, -0.38, 0.7)）
        private static readonly Vector3 ViewLocalPos = new Vector3(0.30f, -0.26f, 0.55f);
        private static readonly Vector3 ViewLocalEuler = new Vector3(3f, 4f, 0f);
        // 世界枪在手骨的兜底位姿（有 ItemHandPos 时优先对齐）
        private static readonly Vector3 HandLocalPos = new Vector3(0.02f, 0.05f, 0.02f);
        private static readonly Vector3 HandLocalEuler = new Vector3(0f, 90f, 0f); // 枪口(+Z)朝身体侧前方

        private sealed class Rig
        {
            public Transform gun;
            public Quaternion restRot;
        }

        private static string modDir;
        private static Transform templateGun;   // 模型模板（隐藏常驻；实例化给各端各枪）
        private static readonly Dictionary<ulong, Rig> worldGuns = new Dictionary<ulong, Rig>();
        private static Transform viewGun, viewMuzzle, viewFlashQuad, viewFlashLight;
        private static Quaternion viewRestRot;
        private static Vector3 viewRestPos;
        private static float recoilPos, recoilRot, flashUntil;
        private static bool equipped;            // 本地玩家选中主武器且已同步挂载
        private static int mag;
        private static bool magInitialized;
        private static bool reloading;
        private static float reloadDoneAt;
        private static float worldScanAt;
        private static bool modelTried;

        // 曳光弹换装 + 枪声
        private static FMOD.Sound shotSound;
        private static bool tracerSwapped, soundTried, soundReady;

        // ===== 对 ModEntry 的接口 =====

        internal static int Mag => mag;
        internal static bool Reloading => reloading;
        internal static bool Equipped => equipped;
        internal static float ReloadRemain => Mathf.Max(0f, reloadDoneAt - Time.unscaledTime);

        /// <summary>连发 gate：换弹中/空匣不发；空匣且还有备弹时触发自动换弹（按住扳机即换）。未装备时返回 true 拦住（防止未装备就污染弹匣状态）。</summary>
        internal static bool BlockFire()
        {
            if (reloading) return true;
            if (!equipped) return true; // 嘴上还没挂上吹箭（物品异步加载中）：不能走弹匣逻辑
            if (mag > 0) return false;
            if (ModEntry.SelfDartAmmo > 0) StartReload(); // 打空自动换弹（CF 式：按住左键自动续）
            return true;
        }

        internal static void OnShotFired()
        {
            mag = Mathf.Max(0, mag - 1);
            recoilPos = RecoilPosKick;
            recoilRot = RecoilRotKick;
            flashUntil = Time.unscaledTime + FlashDuration;
            SetFlashVisible(true);
            if (mag <= 0 && ModEntry.SelfDartAmmo > 0) StartReload();
        }

        internal static bool TryReload() // R 键手动换弹
        {
            if (!equipped || reloading || mag >= MagSize || ModEntry.SelfDartAmmo <= 0) return false;
            StartReload();
            return true;
        }

        private static void StartReload()
        {
            if (reloading) return; // BlockFire 每帧调用：守卫防 reloadDoneAt 被无限推后
            reloading = true;
            reloadDoneAt = Time.unscaledTime + ReloadSeconds;
        }

        internal static void OnAmmoSynced() // 备弹权威校准：弹匣不得多于备弹
        {
            if (mag > ModEntry.SelfDartAmmo) mag = ModEntry.SelfDartAmmo;
            if (reloading && ModEntry.SelfDartAmmo <= 0) reloading = false;
        }

        internal static Vector3 GetMuzzleWorldPos(Vector3 fallback)
        {
            return viewMuzzle != null ? viewMuzzle.position : fallback;
        }

        /// <summary>回合重置/模式关闭：全收。玩家单位跨局存活，世界枪必须显式销毁（只清注册表会让枪一直挂在手上，重新装备时重复堆积）。</summary>
        internal static void ResetForNewRound()
        {
            foreach (var pair in worldGuns)
                if (pair.Value.gun != null) Object.Destroy(pair.Value.gun.gameObject);
            worldGuns.Clear();
            HideViewmodel();
            equipped = false;
            mag = 0;
            magInitialized = false;
            reloading = false;
        }

        // ===== 初始化（OnLoad 调用；内部各步均带懒重试） =====

        internal static void Init(string modDirectory)
        {
            modDir = modDirectory;
            TrySwapTracerPrefab();
            TryLoadShotSound();
        }

        private static void TrySwapTracerPrefab()
        {
            if (tracerSwapped) return;
            var setting = ConfigManager.TaserSetting;
            if (setting == null) return; // ConfigManager 未就绪时下帧重试
            var go = new GameObject("AkTracer");
            GameObject.CreatePrimitive(PrimitiveType.Cube).transform.SetParent(go.transform, false);
            foreach (var col in go.GetComponentsInChildren<Collider>(true)) Object.Destroy(col);
            go.transform.GetChild(0).localScale = new Vector3(0.03f, 0.03f, 0.7f); // 细长曳光
            var renderer = go.GetComponentInChildren<MeshRenderer>();
            renderer.sharedMaterial = WeaponModels.Tint(new Color(1f, 0.8f, 0.35f), 0f); // 亮黄曳光
            // 保持激活（Instantiate inactive prefab 会得到隐形实例），停放到地图下方防穿帮
            go.transform.position = new Vector3(0f, -500f, 0f);
            Object.DontDestroyOnLoad(go);
            setting.projectilePrefab = go; // 原生单发已移除：Taser 管线只有 mod 连发在用，整体换装安全
            tracerSwapped = true;
        }

        private static void TryLoadShotSound()
        {
            if (soundTried || soundReady) return;
            if (string.IsNullOrEmpty(modDir)) return;
            try
            {
                string path = Path.Combine(modDir, "Assets", "ak47_shot.wav");
                if (!File.Exists(path))
                {
                    path = BuildSynthWav(); // 没有打包素材：运行时合成一发放兜底
                    if (path == null) { soundTried = true; return; }
                }
                RESULT result = RuntimeManager.CoreSystem.createSound(path, MODE._3D | MODE.LOOP_OFF, out shotSound);
                if (result == RESULT.OK)
                {
                    soundReady = true;
                    soundTried = true;
                }
                else
                {
                    ModEntry.Log?.Invoke("[状态] AK47 枪声加载失败（" + result + "），下一枪重试");
                }
            }
            catch (System.Exception e)
            {
                ModEntry.Log?.Invoke("[状态] AK47 枪声加载异常（下一枪重试）：" + e.Message);
            }
        }

        internal static bool TryPlayShotAt(Vector3 position)
        {
            TryLoadShotSound();
            if (!soundReady) return false;
            try
            {
                AudioManager audio = MonoSingletonT<AudioManager>.Instance;
                float volume = 0.6f * audio._mainVolum * audio.GetSFXVolume();
                if (RuntimeManager.CoreSystem.playSound(shotSound, default(ChannelGroup), true, out Channel channel) != RESULT.OK)
                    return false;
                VECTOR pos = new VECTOR { x = position.x, y = position.y, z = position.z };
                VECTOR vel = default(VECTOR);
                if (channel.set3DAttributes(ref pos, ref vel) == RESULT.OK
                    && channel.set3DMinMaxDistance(3f, 60f) == RESULT.OK
                    && channel.setVolume(volume) == RESULT.OK
                    && channel.setPaused(false) == RESULT.OK)
                    return true;

                channel.stop();
                return false;
            }
            catch (System.Exception e)
            {
                ModEntry.Log?.Invoke("[状态] AK47 枪声播放异常：" + e.Message);
                return false;
            }
        }

        // ===== 每帧 =====

        internal static void Tick()
        {
            if (!ModEntry.enabled || !UnitManager.IsSingletonActive())
            {
                if (worldGuns.Count > 0 || magInitialized) ResetForNewRound();
                return;
            }
            TrySwapTracerPrefab();
            float now = Time.unscaledTime;
            Unit self = UnitManager.Instance.localPlayer;
            Unit mouthItem = self?.GetComponent<MouthItemUseComponent>()?.currentMouthItem;
            bool holdingAk = StudentWeapons.Selected == StudentWeaponSlot.Primary && StudentWeapons.CanSelect(self)
                && StudentWeapons.IsLocalAk(mouthItem) && mouthItem.isActived;
            if (holdingAk != equipped)
            {
                equipped = holdingAk;
                if (equipped && !magInitialized)
                {
                    mag = Mathf.Min(MagSize, ModEntry.SelfDartAmmo);
                    magInitialized = true;
                }
                if (!equipped)
                {
                    // 切槽取消正在进行的换弹，保留弹匣余量。
                    reloading = false;
                    if (StudentWeapons.IsLocalAk(mouthItem)) mouthItem.GetComponent<TaserComponent>()?.OnStopHeld(self);
                    HideViewmodel();
                }
            }
            UpdateViewmodel(now);
            if (now >= worldScanAt)
            {
                worldScanAt = now + WorldScanInterval;
                ScanWorldGuns();
            }
            if (reloading && now >= reloadDoneAt)
            {
                reloading = false;
                mag = Mathf.Min(MagSize, ModEntry.SelfDartAmmo); // 换弹完成：从备弹装满
            }
        }

        private static bool MouthHasTaser(Unit unit)
        {
            if (unit == null || !unit.isActived || unit.IsDead) return false;
            Unit mouthItem = unit.GetComponent<MouthItemUseComponent>()?.currentMouthItem;
            return mouthItem != null && mouthItem.isActived && mouthItem.UnitSubType == EUnitSubType.Taser;
        }

        // ===== 世界枪（当前槽位是主武器 AK47 的玩家的手部模型，含自己——自己的走 PlayerSelf 层被本机相机剔除） =====

        private static void ScanWorldGuns()
        {
            var seen = new HashSet<ulong>();
            foreach (Unit unit in UnitManager.Instance.playerList)
            {
                if (unit == null || !unit.isActived) continue;
                // 远端玩家：嘴上有吹箭承载物且当前槽位不是副武器（手枪由 PistolWeapon 画）才显示 AK；
                // 自己看 equipped（Selected==Primary 且已同步挂载）
                bool hasTaser = unit.isLocalPlayer
                    ? equipped
                    : MouthHasTaser(unit) && StudentWeapons.RemoteSlotOf(unit.UID) != StudentWeaponSlot.Secondary;
                if (hasTaser)
                {
                    EnsureWorldGun(unit);
                    HideMouthTaserVisual(unit);
                    seen.Add(unit.UID);
                }
            }
            foreach (var pair in new Dictionary<ulong, Rig>(worldGuns))
                if (!seen.Contains(pair.Key)) RemoveWorldGun(pair.Key);
        }

        // 隐藏嘴叼吹箭模型（渲染器级：TaserComponent 照常工作；原生单发已移除，
        // 全游戏不再有嘴叼吹箭的合法视觉，故无需还原逻辑——组件回收复用后仍是隐藏的，正好）
        private static void HideMouthTaserVisual(Unit unit)
        {
            Unit mouthItem = unit.GetComponent<MouthItemUseComponent>()?.currentMouthItem;
            if (mouthItem == null || mouthItem.model == null) return;
            foreach (var r in mouthItem.model.gameObject.GetComponentsInChildren<Renderer>(true))
                if (r.enabled) r.enabled = false;
        }

        private static void EnsureWorldGun(Unit unit)
        {
            if (worldGuns.ContainsKey(unit.UID)) return;
            Transform hand = unit.GetPartRoot(EUnitPartType.HandPosition);
            if (hand == null) return;                 // 模型部件未就绪，下次扫描重试
            if (!EnsureModelTemplate()) return;
            Transform gun = Object.Instantiate(templateGun);
            gun.SetParent(hand, false);
            Transform itemRef = unit.GetPartRoot(EUnitPartType.ItemHandPos);
            if (itemRef != null)
            {
                gun.position = itemRef.position;
                gun.rotation = itemRef.rotation;
            }
            else
            {
                gun.localPosition = HandLocalPos;
                gun.localRotation = Quaternion.Euler(HandLocalEuler);
            }
            gun.gameObject.SetActive(true);
            int layer = LayerMask.NameToLayer(unit.isLocalPlayer ? "PlayerSelf" : "Player");
            if (layer >= 0) WeaponModels.SetLayerRecursive(gun.gameObject, layer);
            worldGuns[unit.UID] = new Rig { gun = gun, restRot = gun.localRotation };
        }

        private static void RemoveWorldGun(ulong uid)
        {
            if (!worldGuns.TryGetValue(uid, out Rig rig)) return;
            worldGuns.Remove(uid);
            if (rig.gun != null) Object.Destroy(rig.gun.gameObject);
        }

        // ===== viewmodel（本地第一人称持枪） =====

        private static void UpdateViewmodel(float now)
        {
            if (!equipped)
            {
                if (viewGun != null) HideViewmodel();
                return;
            }
            if (viewGun == null) ShowViewmodel(); // 相机晚就绪时逐帧重试
            if (viewGun == null) return;
            // 后坐衰减（位置回位 + 上抬回位）
            recoilPos = Mathf.MoveTowards(recoilPos, 0f, Time.unscaledDeltaTime * 0.5f);
            recoilRot = Mathf.MoveTowards(recoilRot, 0f, Time.unscaledDeltaTime * 60f);
            viewGun.localPosition = viewRestPos + Vector3.back * recoilPos;
            viewGun.localRotation = viewRestRot * Quaternion.Euler(-recoilRot, 0f, 0f);
            // 口焰计时
            bool flashOn = now < flashUntil;
            if (viewFlashQuad != null && viewFlashQuad.gameObject.activeSelf != flashOn)
            {
                viewFlashQuad.gameObject.SetActive(flashOn);
                if (viewFlashLight != null) viewFlashLight.gameObject.SetActive(flashOn);
            }
        }

        private static void ShowViewmodel()
        {
            if (viewGun != null || !MonoSingletonT<SceneManager>.IsSingletonActive()) return;
            Camera camera = MonoSingletonT<SceneManager>.Instance.MainCamera;
            if (camera == null) return;
            if (!EnsureModelTemplate()) return;
            viewGun = Object.Instantiate(templateGun);
            viewGun.SetParent(camera.transform, false);
            viewGun.localPosition = ViewLocalPos;
            viewGun.localRotation = Quaternion.Euler(ViewLocalEuler);
            viewRestPos = viewGun.localPosition;
            viewRestRot = viewGun.localRotation;
            viewGun.gameObject.SetActive(true);
            viewMuzzle = viewGun.Find("muzzle");
            BuildViewFlash();
        }

        private static void HideViewmodel()
        {
            if (viewGun != null) Object.Destroy(viewGun.gameObject);
            viewGun = null;
            viewMuzzle = null;
            viewFlashQuad = null;
            viewFlashLight = null;
        }

        private static void BuildViewFlash()
        {
            if (viewMuzzle == null) return;
            var flash = new GameObject("muzzleFlash");
            flash.transform.SetParent(viewMuzzle, false);
            var quad = flash.AddComponent<MeshFilter>(); // Quad 面片：口焰光斑
            quad.mesh = new Mesh
            {
                vertices = new[] { new Vector3(0.18f, 0.18f, 0), new Vector3(-0.18f, 0.18f, 0), new Vector3(-0.18f, -0.18f, 0), new Vector3(0.18f, -0.18f, 0) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
                uv = new[] { new Vector2(1, 1), new Vector2(0, 1), new Vector2(0, 0), new Vector2(1, 0) }
            };
            quad.mesh.RecalculateNormals();
            var mr = flash.AddComponent<MeshRenderer>();
            // 口焰用 Sprites/Default（半透明混合）：URP Lit 是不透明的，会渲成实心色块
            Shader flashShader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Transparent");
            if (flashShader != null)
            {
                var flashMat = new Material(flashShader);
                flashMat.color = new Color(1f, 0.75f, 0.3f, 0.9f);
                if (flashMat.HasProperty("_BaseColor")) flashMat.SetColor("_BaseColor", new Color(1f, 0.75f, 0.3f, 0.9f));
                mr.sharedMaterial = flashMat;
            }
            else
            {
                mr.sharedMaterial = WeaponModels.Tint(new Color(1f, 0.75f, 0.3f), 0f);
            }
            flash.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // Quad 单面，翻向相机
            flash.SetActive(false);
            viewFlashQuad = flash.transform;
            var lightGo = new GameObject("muzzleLight");
            lightGo.transform.SetParent(viewMuzzle, false);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 4f;
            light.intensity = 2.5f;
            light.color = new Color(1f, 0.7f, 0.3f);
            lightGo.SetActive(false);
            viewFlashLight = lightGo.transform;
        }

        private static void SetFlashVisible(bool on)
        {
            if (viewFlashQuad != null) viewFlashQuad.gameObject.SetActive(on);
            if (viewFlashLight != null) viewFlashLight.gameObject.SetActive(on);
        }

        // ===== 模型模板（OBJ 优先，程序化兜底） =====

        private static bool EnsureModelTemplate()
        {
            if (templateGun != null) return true;
            if (modelTried) return false;
            modelTried = true;
            try
            {
                string objPath = string.IsNullOrEmpty(modDir) ? null : Path.Combine(modDir, "Assets", "ak47.obj");
                templateGun = objPath != null && File.Exists(objPath)
                    ? WeaponModels.LoadObj(objPath, "Ak47Model", new Color(0.18f, 0.17f, 0.16f), 0.45f, ModelLength, 180f, ObjMuzzle, out _,
                        textureFile: string.IsNullOrEmpty(modDir) ? null : Path.Combine(modDir, "Assets", "ak47.png"),
                        modelRollDeg: 180f) // CS1.6 转出的 OBJ 上下颠倒：绕枪管轴 roll 180° 正过来
                    : null;
                if (templateGun != null)
                    ModEntry.Log?.Invoke("[状态] AK47 模型已加载（OBJ）");
            }
            catch (System.Exception e)
            {
                ModEntry.Log?.Invoke("[状态] AK47 模型加载失败，回退程序化拼装：" + e.Message);
                templateGun = null;
            }
            if (templateGun == null) templateGun = BuildProceduralAk();
            templateGun.gameObject.SetActive(false);
            Object.DontDestroyOnLoad(templateGun);
            return templateGun != null;
        }

        // 程序化兜底 AK：机匣/枪管/导气管/准星/木护木/斜弹匣/握把/枪托，枪口 +Z 前方
        private static Transform BuildProceduralAk()
        {
            var root = new GameObject("Ak47Model");
            Transform Build(string name, Vector3 pos, Vector3 size, Color color, float smoothness, Vector3? euler = null)
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(go.GetComponent<Collider>());
                go.name = name;
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = pos;
                go.transform.localRotation = Quaternion.Euler(euler ?? Vector3.zero);
                go.transform.localScale = size;
                go.GetComponent<MeshRenderer>().sharedMaterial = WeaponModels.Tint(color, smoothness);
                return go.transform;
            }
            Color gunmetal = new Color(0.16f, 0.16f, 0.17f);
            Color wood = new Color(0.42f, 0.26f, 0.14f);
            // 机匣与枪管（+Z 向前）
            Build("receiver", new Vector3(0f, 0f, 0f), new Vector3(0.05f, 0.075f, 0.30f), gunmetal, 0.5f);
            Build("barrel", new Vector3(0f, 0.012f, 0.42f), new Vector3(0.022f, 0.022f, 0.34f), gunmetal, 0.55f);
            Build("gasTube", new Vector3(0f, 0.055f, 0.36f), new Vector3(0.018f, 0.018f, 0.22f), gunmetal, 0.5f);
            Build("frontSight", new Vector3(0f, 0.085f, 0.55f), new Vector3(0.012f, 0.05f, 0.012f), gunmetal, 0.5f);
            Build("rearSight", new Vector3(0f, 0.06f, 0.05f), new Vector3(0.04f, 0.02f, 0.03f), gunmetal, 0.5f);
            // 木护木与枪托
            Build("handguard", new Vector3(0f, -0.02f, 0.30f), new Vector3(0.055f, 0.06f, 0.18f), wood, 0.25f);
            Build("stock", new Vector3(0f, -0.03f, -0.34f), new Vector3(0.045f, 0.09f, 0.34f), wood, 0.25f, new Vector3(6f, 0f, 0f));
            // 握把与斜弹匣（弯弹匣=三段递进角度）
            Build("grip", new Vector3(0f, -0.09f, -0.10f), new Vector3(0.035f, 0.09f, 0.045f), wood, 0.25f, new Vector3(-20f, 0f, 0f));
            Build("mag1", new Vector3(0f, -0.10f, 0.04f), new Vector3(0.035f, 0.09f, 0.05f), gunmetal, 0.4f, new Vector3(22f, 0f, 0f));
            Build("mag2", new Vector3(0f, -0.175f, 0.075f), new Vector3(0.035f, 0.09f, 0.05f), gunmetal, 0.4f, new Vector3(42f, 0f, 0f));
            Build("mag3", new Vector3(0f, -0.245f, 0.135f), new Vector3(0.033f, 0.08f, 0.05f), gunmetal, 0.4f, new Vector3(62f, 0f, 0f));
            var muzzle = new GameObject("muzzle");
            muzzle.transform.SetParent(root.transform, false);
            muzzle.transform.localPosition = new Vector3(0f, 0.012f, 0.60f);
            return root.transform;
        }

        // ===== 合成枪声兜底（无打包素材时生成 PCM WAV 到临时文件） =====

        private static string BuildSynthWav()
        {
            try
            {
                int rate = 44100;
                int frames = (int)(0.28f * rate);
                var data = new List<byte>(frames * 2);
                var rng = new System.Random();
                for (int i = 0; i < frames; i++)
                {
                    float t = i / (float)rate;
                    float noise = (float)(rng.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-t * 32f) * 0.8f;
                    float thump = Mathf.Sin(2f * Mathf.PI * 95f * t) * Mathf.Exp(-t * 20f) * 0.6f;
                    float crack = Mathf.Exp(-t * 120f) * 0.5f;
                    float v = Mathf.Clamp((noise + thump + crack) * 0.9f, -1f, 1f);
                    short s = (short)(v * 32767f * 0.85f);
                    data.Add((byte)(s & 0xFF));
                    data.Add((byte)((s >> 8) & 0xFF));
                }
                string path = Path.Combine(Path.GetTempPath(), "ak47_shot_synth.wav");
                using (var fs = new FileStream(path, FileMode.Create))
                using (var bw = new BinaryWriter(fs))
                {
                    int dataLen = data.Count;
                    bw.Write(0x46464952); // "RIFF"
                    bw.Write(36 + dataLen);
                    bw.Write(0x45564157); // "WAVE"
                    bw.Write(0x20746D66); // "fmt "
                    bw.Write(16); bw.Write((short)1); bw.Write((short)1);
                    bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
                    bw.Write(0x61746164); // "data"
                    bw.Write(dataLen);
                    bw.Write(data.ToArray());
                }
                return path;
            }
            catch { return null; }
        }
    }
}
