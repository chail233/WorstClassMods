using System.Collections.Generic;
using System.IO;
using FMOD;
using FMODUnity;
using UnityEngine;

namespace BioChem
{
    /// <summary>
    /// 学生副武器手枪表现层（按 2）：管线与 Ak47Weapon 同源——嘴叼 Taser 物品承载 + 原生 TryBuildShot 弹道 +
    /// taserShots 注册表命中，区别只在弹匣节奏（12 发/1.2s 换弹）、射击间隔、模型与枪声，无感染伤害加成。
    /// - 模型：ModDirectory/Assets/pistol.obj（CC0）运行时解析；缺失/失败回退程序化拼装
    /// - 枪声：Assets/pistol_shot.wav（FMOD CoreSystem 3D）；缺失时回退 ak47_shot.wav 变调（setPitch 1.35）
    /// - 显隐：选中副武器且嘴上挂着已发放的承载物 = 手持手枪（世界枪挂 HandPosition + 本机 viewmodel），
    ///   远端玩家按全员槽位广播判定（RemoteSlotOf==Secondary），嘴叼吹箭模型渲染器仍由 Ak47Weapon 统一隐藏
    /// </summary>
    internal static class PistolWeapon
    {
        internal const float ReloadSeconds = 1.2f;
        private const float WorldScanInterval = 0.25f;
        private const float FlashDuration = 0.05f;
        private const float RecoilPosKick = 0.04f;
        private const float RecoilRotKick = 9f;
        private const float ModelLength = 0.32f;     // 手枪模型归一化长度（米）
        private const float FallbackPitch = 1.35f;   // 回退用 AK 枪声的变调（手枪更尖）
        // viewmodel 在相机空间的位姿（比 AK 更靠中下，短枪贴手）
        private static readonly Vector3 ViewLocalPos = new Vector3(0.28f, -0.24f, 0.48f);
        private static readonly Vector3 ViewLocalEuler = new Vector3(3f, 4f, 0f);
        private static readonly Vector3 HandLocalPos = new Vector3(0.02f, 0.05f, 0.02f);
        private static readonly Vector3 HandLocalEuler = new Vector3(0f, 90f, 0f);

        private sealed class Rig
        {
            public Transform gun;
            public Quaternion restRot;
        }

        private static string modDir;
        private static Transform templateGun;
        private static readonly Dictionary<ulong, Rig> worldGuns = new Dictionary<ulong, Rig>();
        private static Transform viewGun, viewMuzzle, viewFlashQuad, viewFlashLight;
        private static Quaternion viewRestRot;
        private static Vector3 viewRestPos;
        private static float recoilPos, recoilRot, flashUntil;
        private static bool equipped;
        private static int mag;
        private static bool magInitialized;
        private static bool reloading;
        private static float reloadDoneAt;
        private static float worldScanAt;
        private static bool modelTried;

        private static FMOD.Sound shotSound;
        private static bool soundTried, soundReady, usedFallbackSound;

        // ===== 对 ModEntry 的接口 =====

        internal static int MagSize => Mathf.Max(1, ModEntry.Config.pistolMagSize);
        internal static int Mag => mag;
        internal static bool Reloading => reloading;
        internal static bool Equipped => equipped;
        internal static float ReloadRemain => Mathf.Max(0f, reloadDoneAt - Time.unscaledTime);

        /// <summary>连发 gate：换弹中/空匣不发；空匣且还有备弹时触发自动换弹。未装备时返回 true 拦住。</summary>
        internal static bool BlockFire()
        {
            if (reloading) return true;
            if (!equipped) return true;
            if (mag > 0) return false;
            if (ModEntry.SelfPistolAmmo > 0) StartReload();
            return true;
        }

        internal static void OnShotFired()
        {
            mag = Mathf.Max(0, mag - 1);
            recoilPos = RecoilPosKick;
            recoilRot = RecoilRotKick;
            flashUntil = Time.unscaledTime + FlashDuration;
            SetFlashVisible(true);
            if (mag <= 0 && ModEntry.SelfPistolAmmo > 0) StartReload();
        }

        internal static bool TryReload()
        {
            if (!equipped || reloading || mag >= MagSize || ModEntry.SelfPistolAmmo <= 0) return false;
            StartReload();
            return true;
        }

        private static void StartReload()
        {
            if (reloading) return;
            reloading = true;
            reloadDoneAt = Time.unscaledTime + ReloadSeconds;
        }

        internal static void OnAmmoSynced()
        {
            if (mag > ModEntry.SelfPistolAmmo) mag = ModEntry.SelfPistolAmmo;
            if (reloading && ModEntry.SelfPistolAmmo <= 0) reloading = false;
        }

        internal static Vector3 GetMuzzleWorldPos(Vector3 fallback)
        {
            return viewMuzzle != null ? viewMuzzle.position : fallback;
        }

        /// <summary>回合重置/模式关闭：全收（世界枪显式销毁，玩家单位跨局存活）。</summary>
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

        internal static void Init(string modDirectory)
        {
            modDir = modDirectory;
        }

        // ===== 枪声（手枪专用 wav 优先，缺失回退 AK 枪声变调） =====

        private static void TryLoadShotSound()
        {
            if (soundTried || soundReady) return;
            if (string.IsNullOrEmpty(modDir)) return;
            try
            {
                string path = Path.Combine(modDir, "Assets", "pistol_shot.wav");
                if (File.Exists(path))
                {
                    usedFallbackSound = false;
                }
                else
                {
                    path = Path.Combine(modDir, "Assets", "ak47_shot.wav"); // 回退：AK 枪声变调当手枪声
                    usedFallbackSound = true;
                    if (!File.Exists(path)) { soundTried = true; return; }
                }
                RESULT result = RuntimeManager.CoreSystem.createSound(path, MODE._3D | MODE.LOOP_OFF, out shotSound);
                if (result == RESULT.OK)
                {
                    soundReady = true;
                    soundTried = true;
                }
                else
                {
                    ModEntry.Log?.Invoke("[状态] 手枪枪声加载失败（" + result + "），下一枪重试");
                }
            }
            catch (System.Exception e)
            {
                ModEntry.Log?.Invoke("[状态] 手枪枪声加载异常（下一枪重试）：" + e.Message);
            }
        }

        internal static bool TryPlayShotAt(Vector3 position)
        {
            TryLoadShotSound();
            if (!soundReady) return false;
            try
            {
                AudioManager audio = MonoSingletonT<AudioManager>.Instance;
                float volume = 0.5f * audio._mainVolum * audio.GetSFXVolume();
                if (RuntimeManager.CoreSystem.playSound(shotSound, default(ChannelGroup), true, out Channel channel) != RESULT.OK)
                    return false;
                VECTOR pos = new VECTOR { x = position.x, y = position.y, z = position.z };
                VECTOR vel = default(VECTOR);
                if (channel.set3DAttributes(ref pos, ref vel) == RESULT.OK
                    && channel.set3DMinMaxDistance(3f, 60f) == RESULT.OK
                    && channel.setVolume(volume) == RESULT.OK
                    && (usedFallbackSound ? channel.setPitch(FallbackPitch) == RESULT.OK : true)
                    && channel.setPaused(false) == RESULT.OK)
                    return true;

                channel.stop();
                return false;
            }
            catch (System.Exception e)
            {
                ModEntry.Log?.Invoke("[状态] 手枪枪声播放异常：" + e.Message);
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
            float now = Time.unscaledTime;
            Unit self = UnitManager.Instance.localPlayer;
            Unit mouthItem = self?.GetComponent<MouthItemUseComponent>()?.currentMouthItem;
            bool holdingPistol = StudentWeapons.Selected == StudentWeaponSlot.Secondary && StudentWeapons.CanSelect(self)
                && StudentWeapons.IsLocalGun(mouthItem) && mouthItem.isActived;
            if (holdingPistol != equipped)
            {
                equipped = holdingPistol;
                if (equipped && !magInitialized)
                {
                    mag = Mathf.Min(MagSize, ModEntry.SelfPistolAmmo);
                    magInitialized = true;
                }
                if (!equipped)
                {
                    reloading = false;
                    if (StudentWeapons.IsLocalGun(mouthItem)) mouthItem.GetComponent<TaserComponent>()?.OnStopHeld(self);
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
                mag = Mathf.Min(MagSize, ModEntry.SelfPistolAmmo);
            }
        }

        private static bool MouthHasTaser(Unit unit)
        {
            if (unit == null || !unit.isActived || unit.IsDead) return false;
            Unit mouthItem = unit.GetComponent<MouthItemUseComponent>()?.currentMouthItem;
            return mouthItem != null && mouthItem.isActived && mouthItem.UnitSubType == EUnitSubType.Taser;
        }

        // ===== 世界枪（当前槽位是副武器手枪的玩家的手部模型） =====

        private static void ScanWorldGuns()
        {
            var seen = new HashSet<ulong>();
            foreach (Unit unit in UnitManager.Instance.playerList)
            {
                if (unit == null || !unit.isActived) continue;
                bool hasPistol = unit.isLocalPlayer
                    ? equipped
                    : MouthHasTaser(unit) && StudentWeapons.RemoteSlotOf(unit.UID) == StudentWeaponSlot.Secondary;
                if (hasPistol)
                {
                    EnsureWorldGun(unit);
                    seen.Add(unit.UID);
                }
            }
            foreach (var pair in new Dictionary<ulong, Rig>(worldGuns))
                if (!seen.Contains(pair.Key)) RemoveWorldGun(pair.Key);
        }

        private static void EnsureWorldGun(Unit unit)
        {
            if (worldGuns.ContainsKey(unit.UID)) return;
            Transform hand = unit.GetPartRoot(EUnitPartType.HandPosition);
            if (hand == null) return;
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

        // ===== viewmodel =====

        private static void UpdateViewmodel(float now)
        {
            if (!equipped)
            {
                if (viewGun != null) HideViewmodel();
                return;
            }
            if (viewGun == null) ShowViewmodel();
            if (viewGun == null) return;
            recoilPos = Mathf.MoveTowards(recoilPos, 0f, Time.unscaledDeltaTime * 0.55f);
            recoilRot = Mathf.MoveTowards(recoilRot, 0f, Time.unscaledDeltaTime * 70f);
            viewGun.localPosition = viewRestPos + Vector3.back * recoilPos;
            viewGun.localRotation = viewRestRot * Quaternion.Euler(-recoilRot, 0f, 0f);
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
            var quad = flash.AddComponent<MeshFilter>();
            quad.mesh = new Mesh
            {
                vertices = new[] { new Vector3(0.13f, 0.13f, 0), new Vector3(-0.13f, 0.13f, 0), new Vector3(-0.13f, -0.13f, 0), new Vector3(0.13f, -0.13f, 0) },
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
                flashMat.color = new Color(1f, 0.8f, 0.4f, 0.9f);
                if (flashMat.HasProperty("_BaseColor")) flashMat.SetColor("_BaseColor", new Color(1f, 0.8f, 0.4f, 0.9f));
                mr.sharedMaterial = flashMat;
            }
            else
            {
                mr.sharedMaterial = WeaponModels.Tint(new Color(1f, 0.8f, 0.4f), 0f);
            }
            flash.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            flash.SetActive(false);
            viewFlashQuad = flash.transform;
            var lightGo = new GameObject("muzzleLight");
            lightGo.transform.SetParent(viewMuzzle, false);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 3f;
            light.intensity = 2f;
            light.color = new Color(1f, 0.75f, 0.35f);
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
                string objPath = string.IsNullOrEmpty(modDir) ? null : Path.Combine(modDir, "Assets", "pistol.obj");
                templateGun = objPath != null && File.Exists(objPath)
                    ? WeaponModels.LoadObj(objPath, "PistolModel", new Color(0.12f, 0.12f, 0.13f), 0.5f, ModelLength, 0f, null, out _,
                        textureFile: string.IsNullOrEmpty(modDir) ? null : Path.Combine(modDir, "Assets", "pistol.png"))
                    : null; // Quaternius 枪包的枪口沿 +X，yaw -90° 旋到 +Z；口部坐标未知走包围盒最前端兜底
                if (templateGun != null)
                    ModEntry.Log?.Invoke("[状态] 手枪模型已加载（OBJ）");
            }
            catch (System.Exception e)
            {
                ModEntry.Log?.Invoke("[状态] 手枪模型加载失败，回退程序化拼装：" + e.Message);
                templateGun = null;
            }
            if (templateGun == null) templateGun = BuildProceduralPistol();
            templateGun.gameObject.SetActive(false);
            Object.DontDestroyOnLoad(templateGun);
            return templateGun != null;
        }

        // 程序化兜底手枪：滑套/枪管/准星/握把/扳机护圈，枪口 +Z 前方
        private static Transform BuildProceduralPistol()
        {
            var root = new GameObject("PistolModel");
            var metal = WeaponModels.Tint(new Color(0.13f, 0.13f, 0.14f), 0.55f);
            var dark = WeaponModels.Tint(new Color(0.10f, 0.10f, 0.11f), 0.3f);
            // 滑套（上半截，+Z 向前）
            WeaponModels.CreatePart(root.transform, "slide", new Vector3(0f, 0.025f, 0.02f), new Vector3(0.034f, 0.05f, 0.19f), metal);
            WeaponModels.CreatePart(root.transform, "barrel", new Vector3(0f, 0.03f, 0.135f), new Vector3(0.02f, 0.02f, 0.05f), dark);
            WeaponModels.CreatePart(root.transform, "frontSight", new Vector3(0f, 0.062f, 0.10f), new Vector3(0.008f, 0.018f, 0.012f), metal);
            WeaponModels.CreatePart(root.transform, "rearSight", new Vector3(0f, 0.062f, -0.07f), new Vector3(0.026f, 0.014f, 0.014f), metal);
            // 下机匣+握把（后倾）
            WeaponModels.CreatePart(root.transform, "frame", new Vector3(0f, -0.02f, 0.0f), new Vector3(0.03f, 0.03f, 0.16f), dark);
            WeaponModels.CreatePart(root.transform, "grip", new Vector3(0f, -0.095f, -0.06f), new Vector3(0.03f, 0.12f, 0.045f), dark, new Vector3(-18f, 0f, 0f));
            WeaponModels.CreatePart(root.transform, "magBase", new Vector3(0f, -0.155f, -0.093f), new Vector3(0.032f, 0.012f, 0.05f), metal, new Vector3(-18f, 0f, 0f));
            WeaponModels.CreatePart(root.transform, "triggerGuard", new Vector3(0f, -0.05f, 0.035f), new Vector3(0.012f, 0.045f, 0.05f), dark);
            var muzzle = new GameObject("muzzle");
            muzzle.transform.SetParent(root.transform, false);
            muzzle.transform.localPosition = new Vector3(0f, 0.03f, 0.165f);
            return root.transform;
        }
    }
}
