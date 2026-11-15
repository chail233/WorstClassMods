using System;
using System.Collections.Generic;
using System.Reflection;
using FMOD;
using FMOD.Studio;
using FMODUnity;
using HarmonyLib;
using Mirror;
using Rewired;
using WorstClassEver.ModAPI;
using UnityEngine;

namespace TrickMole
{
    /// <summary>
    /// 混的入：每节课开始时随机选定一名真人学生，全员可见身份，
    /// 其正向捣蛋计分乘以加成倍数（负分不乘，被抓照样扣）。
    /// 服务端选定后经自定义 Mirror 消息广播，所有端本地显示。
    /// </summary>
    public struct MoleSelectMessage : NetworkMessage
    {
        public uint Version;
        public ulong UnitUID;
    }

    /// <summary>Kind: 1=Start（C2S 请求/S2C 广播，S2C 带曲目与起点） 2=Stop（仅 S2C） 3=StopRequest（仅 C2S，手动停止/播完）</summary>
    public struct MoleMusicMessage : NetworkMessage
    {
        public const byte Start = 1, Stop = 2, StopRequest = 3;
        public byte Kind;
        public ulong UnitUID;
        public uint AudioEvent;     // 原版曲的 AudioEvents 枚举值；扩展曲填 0
        public string Hash;         // 扩展曲 SHA-256（原版曲为 null）
        public double Started;      // 统一播放起点（NetRuntime.SessionTime），扩展曲进度对齐用
    }

    /// <summary>Kind: 1=Request（仅 C2S，学生→老师推倒请求） 2=Apply（仅 S2C，服务端校验通过后广播）</summary>
    public struct MolePushMessage : NetworkMessage
    {
        public const byte Request = 1, Apply = 2;
        public byte Kind;
        public ulong TeacherUID;
    }

    public sealed class ModEntry : IMod
    {
        internal static Action<string> Log;

        internal const float BonusMultiplier = 2f;
        internal const float ScoreGraceSeconds = 3f;   // 起播后前几秒不计分
        internal const float MusicCooldownSeconds = 20f; // 停止后的冷却
        internal const float PushCooldownSeconds = 30f;  // 推倒老师的冷却
        internal const float PushStunSeconds = 2.5f;     // 老师倒地时长
        internal const float PushRange = 2.2f;           // 触发距离
        internal const float PushBehindDot = -0.35f;     // 背后判定：学生方向与老师朝向点积上限（≈110°身后扇区）
        internal static readonly KeyCode MusicKey = KeyCode.B;
        internal static readonly KeyCode PushKey = KeyCode.E;
        internal static readonly AudioEvents[] MusicPool = { AudioEvents.bgm_YuAi, AudioEvents.bgm_Hajimi1, AudioEvents.bgm_Hajimi2, AudioEvents.bgm_YuAids };
        private const string HarmonyId = "worstclass.trickmole";
        private const int ClassStateChangedMsgId = 301;

        // 本局状态：version 单调递增，客户端据此丢弃过期消息
        internal static uint version;
        internal static ulong moleUID;
        private static float nextRepublish;
        private static bool registeredStateHook;

        // 广播播放状态：服务端权威计时，各端本地播放表现
        internal static bool musicPlaying;
        private static float musicGraceRemain;  // 服务端：起播宽限（不计分）
        private static float musicCooldownRemain; // 服务端：停止后冷却
        private static float trickElapsed;
        private static AudioEvents localPlayingMusic = AudioEvents.None;
        private static ulong localPlayingUID;
        private static float clientCooldownRemain; // 客机本地冷却倒计时（仅 UI 显示）

        // 扩展曲（mp3）低级播放状态：跟随玩家移动，播完自动结束本轮广播
        private static Sound moleSound;
        private static Channel moleChannel;
        private static bool moleHasSound, moleHasChannel;
        private static string pendingPath;          // 等 Started 到点的待播扩展曲
        private static double pendingStarted;
        private static bool localCompleted;         // 播完上报防重发

        // 混的入捣蛋值累计（各端本地独立统计，统计口径 = 实际计入总进度的量，即 ×2 后）：
        // 被挂柱 → 总体进度扣其 2/3，记录归零；每节课开始重置
        internal static float moleProgress;

        // 推倒技能冷却：服务端权威 + 客机本地倒计时（收到 Apply 时启动）
        private static float pushCooldownRemain;
        private static float clientPushCooldownRemain;

        // UI 用的冷却剩余（房主读服务端权威值，客机读本地倒计时）
        internal static float ClientCooldownRemain => NetRuntime.IsServer ? musicCooldownRemain : clientCooldownRemain;
        internal static float PushCooldownRemain => NetRuntime.IsServer ? pushCooldownRemain : clientPushCooldownRemain;

        // ===== 操场特判：混的入广播期间静音广播柱（状态机不动，恢复零成本） =====

        // 柱子原版曲的 Studio 实例（private 字段）
        private static readonly FieldInfo PillarMusicInstanceField = AccessTools.Field(typeof(PlaygroundBroadcastComponent), "musicInstance");
        // PlaygroundMusic Mod 的低级 Channel（软依赖：未装该 Mod 时为 null，静默跳过）
        private static FieldInfo pmHasChannelField, pmChannelField;
        private static bool pmResolved;

        // 懒解析：Mod 加载顺序不保证，首次实际使用时再解析
        private static void EnsurePmResolved()
        {
            if (pmResolved) return;
            pmResolved = true;
            try
            {
                Type pmType = AccessTools.TypeByName("PlaygroundMusic.ModEntry"); // 软依赖：未装 PlaygroundMusic 时为 null
                pmHasChannelField = pmType == null ? null : AccessTools.Field(pmType, "hasChannel");
                pmChannelField = pmType == null ? null : AccessTools.Field(pmType, "channel");
            }
            catch { /* 软依赖：静默 */ }
        }

        public void OnLoad(ModContext context)
        {
            Log = context.Log;
            var harmony = new Harmony(HarmonyId);
            try
            {
                // 唯一计分入口：正向加分时给混的入乘倍数（原方法照常执行）
                harmony.Patch(
                    AccessTools.Method(typeof(ClassroomManager), "OnResponseTrickCountData"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeTrickCount"));
                // 罚站柱绑定真正生效的点：混的入被挂柱扣固定捣蛋度
                harmony.Patch(
                    AccessTools.Method(typeof(PunishPillarComponent), "OnResponseBindStudent"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterBindToPillar"));
                // 操场特判：广播柱实例每帧跟随静音/恢复；柱子计分在混的入广播期间只算一路
                harmony.Patch(
                    AccessTools.Method(typeof(PlaygroundBroadcastComponent), "OnUpdate"),
                    postfix: new HarmonyMethod(typeof(ModEntry), "AfterPillarUpdate"));
                harmony.Patch(
                    AccessTools.Method(typeof(ClassroomMessageHandler), "AwardPlaygroundMusicScore"),
                    prefix: new HarmonyMethod(typeof(ModEntry), "BeforeAwardMusicScore"));
                // 消息注册挂到游戏自己的注册时机之后（防重注册清掉）
                harmony.Patch(AccessTools.Method(typeof(RoomManager), "RegisterClientMessageHandlers"), postfix: new HarmonyMethod(typeof(ModEntry), "RegisterClient"));
                harmony.Patch(AccessTools.Method(typeof(RoomManager), "RegisterServerMessageHandlers"), postfix: new HarmonyMethod(typeof(ModEntry), "RegisterServer"));
                Writer<MoleSelectMessage>.write = MolePump.WriteMole;
                Reader<MoleSelectMessage>.read = MolePump.ReadMole;
                Writer<MoleMusicMessage>.write = MolePump.WriteMusic;
                Reader<MoleMusicMessage>.read = MolePump.ReadMusic;
                Writer<MolePushMessage>.write = MolePump.WritePush;
                Reader<MolePushMessage>.read = MolePump.ReadPush;
                RegisterClient();
                RegisterServer();

                // 课堂状态变化：进入 Prepare 清空上局选择（轮询会重新选人），下课后通知全员清除显示
                MsgCenter.Register<EClassroomState>(ClassStateChangedMsgId, OnClassStateChanged);
                registeredStateHook = true;

                var pump = new GameObject("混的入提示");
                UnityEngine.Object.DontDestroyOnLoad(pump);
                pump.AddComponent<MolePump>();
            }
            catch
            {
                harmony.UnpatchAll(HarmonyId);
                throw;
            }
            Log("[状态] 混的入 Mod 已加载：每节课随机一名学生，捣蛋分数 ×" + BonusMultiplier);
        }

        // ===== 计分加成与捣蛋累计 =====

        public static void BeforeTrickCount(ulong uid, ref float count)
        {
            if (count <= 0f) return;
            if (uid == moleUID)
            {
                count *= BonusMultiplier;
                moleProgress += count; // 记录实际计入总进度的量（×2 后）
            }
        }

        // ===== 挂柱惩罚 =====

        // PunishPillarComponent.OnResponseBindStudent 是绑定真正生效的点（版本校验通过才调用，
        // 全项目唯一调用处是服务端的绑定确认链路），同一绑定事件只会执行一次
        public static void AfterBindToPillar(Unit student)
        {
            if (student == null) return;
            if (!NetRuntime.IsServer)
            {
                // 客机：混的入被挂 → 本地记录同步归零（扣分本身由服务端经计分广播同步）
                if (moleUID != 0 && student.UID == moleUID) moleProgress = 0f;
                return;
            }
            if (moleUID == 0 || student.UID != moleUID) return;
            PunishMole();
        }

        private static void PunishMole()
        {
            if (!ClassroomManager.IsSingletonActive() || !RoomManager.IsSingletonActive()) return;
            StopMusicServer(); // 挂柱罚站，广播立刻停
            // 总体进度扣混的入捣蛋累计的 2/3，记录归零（个人贡献分不动）
            float deduct = moleProgress * (2f / 3f);
            moleProgress = 0f;
            if (deduct <= 0f) return;
            var response = default(TrickCountChangeMessageResponse_S2C);
            response.unitUID = moleUID;
            response.changedCount = 0f - deduct;
            response.playgroundMusic = false;
            // 照游戏自己的 PublishTrickCountChange 模式：广播客机 + 服务端本机应用（UI 同步跳字）
            if (!ClassroomManager.Instance.DebugFreezeTrickCount)
            {
                RoomManager.Instance.BroadcastToAllExceptHost(response);
                RoomManager.Instance.GetClassroomMessageHandler().OnTrickCountChangeMessageResponse(response);
            }
            Log("[状态] 混的入被挂上罚站柱，总体进度 -" + deduct.ToString("0.#") + "（捣蛋累计的 2/3）");
        }

        // ===== 操场特判实现 =====

        // 每帧把柱子当前 Studio 实例的静音状态对齐到 musicPlaying（幂等，切歌/新实例下一帧自动跟上）
        public static void AfterPillarUpdate(PlaygroundBroadcastComponent __instance)
        {
            SetStudioInstanceMuted(PillarMusicInstanceField, __instance, musicPlaying);
        }

        private static void SetStudioInstanceMuted(FieldInfo field, object owner, bool muted)
        {
            try
            {
                EventInstance? inst = field?.GetValue(owner) as EventInstance?;
                if (!inst.HasValue || !inst.Value.isValid()) return;
                inst.Value.setVolume(muted ? 0f : 1f); // Studio 实例无低级 Channel 可取，用实例音量实现静音
            }
            catch { /* 反射或 FMOD 异常不干扰主逻辑 */ }
        }

        // PlaygroundMusic Mod 的低级 Channel 静音（未装该 Mod 时字段解析不到，跳过）
        private static void MuteLowLevelChannels(bool muted)
        {
            EnsurePmResolved();
            try
            {
                if (pmHasChannelField == null || pmChannelField == null) return;
                if (!(bool)pmHasChannelField.GetValue(null)) return;
                var channel = (Channel)pmChannelField.GetValue(null);
                channel.setMute(muted); // 低级 API 对无效句柄返回错误码不崩溃；hasChannel 守卫下通常有效
            }
            catch { /* 软依赖：静默 */ }
        }

        // 混的入广播期间，广播柱的音乐计分只算一路：非混的入的加分一律跳过
        public static bool BeforeAwardMusicScore(ulong studentUID)
        {
            return !(musicPlaying && studentUID != moleUID);
        }

        // ===== 选人与广播（仅服务端） =====

        private static void TrySelectMole()
        {
            if (!NetRuntime.IsServer || moleUID != 0) return;
            if (!ClassroomManager.IsSingletonActive()) return;
            var state = ClassroomManager.Instance.curState;
            if (state != EClassroomState.Prepare && state != EClassroomState.Classing) return;
            if (!UnitManager.IsSingletonActive()) return;

            var students = new List<Unit>();
            foreach (var unit in UnitManager.Instance.playerList)
                if (unit != null && unit.isActived && unit.IsStudent && unit.isPlayer) students.Add(unit);
            if (students.Count == 0) return;

            Unit pick = students[UnityEngine.Random.Range(0, students.Count)];
            version++;
            moleUID = pick.UID;
            BroadcastState();
            Log("[状态] 本局混的入已选定");
        }

        private static void OnClassStateChanged(EClassroomState state)
        {
            // 每节课开始重选；下课/课前清空并通知全员撤掉显示
            // 冷却也一并清零：上局下课触发的 StopMusicServer 会残留 20s 冷却，不清会让新局开场无法广播
            if (state == EClassroomState.Prepare)
            {
                moleUID = 0;
                moleProgress = 0f;
                musicCooldownRemain = 0f;
                clientCooldownRemain = 0f;
                pushCooldownRemain = 0f;
                clientPushCooldownRemain = 0f;
                return;
            }
            if (state == EClassroomState.BeforeClass || state == EClassroomState.ClassFinish)
            {
                moleUID = 0;
                moleProgress = 0f;
                musicCooldownRemain = 0f;
                clientCooldownRemain = 0f;
                pushCooldownRemain = 0f;
                clientPushCooldownRemain = 0f;
                if (NetRuntime.IsServer) StopMusicServer();
                if (NetRuntime.IsServer) BroadcastState();
            }
        }

        // ===== 混的入广播（按键触发，3D 音乐 + 持续计分） =====

        // 服务端：处理混的入的播放请求（Start=开始，StopRequest=手动停止）
        private static void OnServerMusic(NetworkConnectionToClient connection, MoleMusicMessage message)
        {
            if (!NetRuntime.IsServer) return;
            ulong senderUID = connection == null || connection == NetworkServer.localConnection
                ? MonoSingletonT<UnitManager>.Instance.localPlayerUID
                : RoomManager.Instance.GetPlayerUIDByConnection(connection);
            if (senderUID != moleUID || moleUID == 0) return;
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            if (message.Kind == MoleMusicMessage.Start)
            {
                if (musicPlaying || musicCooldownRemain > 0f) return;
                StartMusicServer();
            }
            else if (message.Kind == MoleMusicMessage.StopRequest)
            {
                StopMusicServer();
            }
        }

        private static void StartMusicServer()
        {
            // 曲池 = 原版 4 首 + PlaygroundMusic 全员交集扩展曲（未装/交集空时自动退化为纯原版）
            AudioEvents pickAudio = AudioEvents.None;
            string pickHash = null;
            int shared = PmBridge.SharedTrackCount;
            int pick = UnityEngine.Random.Range(0, MusicPool.Length + shared);
            if (pick < MusicPool.Length)
            {
                pickAudio = MusicPool[pick];
            }
            else
            {
                string hash = PmBridge.GetSharedHash(pick - MusicPool.Length);
                // 服务端自己的文件也要在且未变更，否则回退原版曲
                if (hash != null && PmBridge.FindLocalByHash(hash) != null) pickHash = hash;
                else pickAudio = MusicPool[0];
            }
            musicPlaying = true;
            musicGraceRemain = ScoreGraceSeconds;
            trickElapsed = 0f;
            double started = NetRuntime.SessionTime + 0.5d; // 扩展曲统一播放起点
            var broadcast = new MoleMusicMessage { Kind = MoleMusicMessage.Start, UnitUID = moleUID, AudioEvent = (uint)pickAudio, Hash = pickHash, Started = started };
            if (RoomManager.IsSingletonActive() && NetworkServer.active) RoomManager.Instance.BroadcastToAllExceptHost(broadcast, 0);
            StartLocalPlayback(moleUID, pickAudio, pickHash, started);
            Log("[状态] 混的入开始播放广播" + (pickHash != null ? "（扩展曲）" : "") + "，前 " + ScoreGraceSeconds + " 秒不计分");
        }

        private static void StopMusicServer()
        {
            if (!musicPlaying) return;
            musicPlaying = false;
            musicCooldownRemain = MusicCooldownSeconds;
            var broadcast = new MoleMusicMessage { Kind = MoleMusicMessage.Stop, UnitUID = moleUID, AudioEvent = 0 };
            if (RoomManager.IsSingletonActive() && NetworkServer.active) RoomManager.Instance.BroadcastToAllExceptHost(broadcast, 0);
            StopMusicLocal();
            Log("[状态] 混的入广播结束，冷却 " + MusicCooldownSeconds + " 秒");
        }

        // 服务端每帧：宽限 → 计分；停止后冷却倒计时
        private static void TickServerMusic(float dt)
        {
            if (!musicPlaying)
            {
                if (musicCooldownRemain > 0f) musicCooldownRemain = Math.Max(0f, musicCooldownRemain - dt);
                return;
            }
            if (musicGraceRemain > 0f)
            {
                musicGraceRemain = Math.Max(0f, musicGraceRemain - dt);
                return;
            }
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing || moleUID == 0) return;
            trickElapsed += dt;
            float interval = ConfigManager.GameSetting.PlaygroundMusicScoreTickInterval;
            if (float.IsNaN(interval) || float.IsInfinity(interval) || interval < 0.05f) interval = 1f;
            if (trickElapsed >= interval)
            {
                trickElapsed %= interval;
                RoomManager.Instance.GetClassroomMessageHandler().AwardPlaygroundMusicScore(moleUID);
            }
        }

        // 各端本地播放入口：原版曲立即播（循环曲起点无意义），扩展曲先找本地文件再等 Started 到点
        // 注意 Mirror 把 null 字符串序列化读回为 ""，所以判断必须用 IsNullOrEmpty
        private static void StartLocalPlayback(ulong uid, AudioEvents audio, string hash, double started)
        {
            if (!UnitManager.IsSingletonActive()) return;
            Unit unit = UnitManager.Instance.GetUnitByUID(uid);
            if (unit == null || unit.transform == null) return;
            StopMusicLocal();
            localPlayingUID = uid;
            if (!string.IsNullOrEmpty(hash))
            {
                // 扩展曲：按 hash 找本机文件，缺歌则本轮静默（计分仍由服务端权威驱动）
                pendingPath = PmBridge.FindLocalByHash(hash);
                pendingStarted = pendingPath != null ? started : 0d;
                return;
            }
            var request = default(PlaySoundRequest);
            request.UserUID = uid;
            request.AudioName = audio;
            request.PlaySoundType = EPlaySoundType.None;
            MonoSingletonT<AudioManager>.Instance.Play(audio, unit.gameObject, request, true);
            localPlayingMusic = audio;
        }

        // 扩展曲低级播放（照 PlaygroundMusic 模式：挂 BGM 总线、3D 定位、按 Started 偏移进度）
        private static void PlayExtendedLocal(string path, double started, ulong uid)
        {
            Unit unit = UnitManager.IsSingletonActive() ? UnitManager.Instance.GetUnitByUID(uid) : null;
            if (unit == null || unit.transform == null) return;
            RESULT result = RuntimeManager.CoreSystem.createSound(path, MODE.CREATESTREAM | MODE._3D | MODE.LOOP_OFF, out moleSound);
            if (result != RESULT.OK) { Log("混的入广播 createSound 失败: " + result); return; }
            moleHasSound = true;
            uint length;
            moleSound.getLength(out length, TIMEUNIT.MS);
            double elapsed = Math.Max(0d, NetRuntime.SessionTime - started) * 1000d;
            if (elapsed >= length) { moleSound.release(); moleHasSound = false; return; } // 已播到头，本轮作废
            ChannelGroup group = default(ChannelGroup);
            try
            {
                var bus = RuntimeManager.GetBus("bus:/BGM");
                bus.lockChannelGroup();
                bus.getChannelGroup(out group);
                bus.unlockChannelGroup();
            }
            catch (Exception e) { Log("BGM 总线不可用: " + e.Message); }
            result = RuntimeManager.CoreSystem.playSound(moleSound, group, true, out moleChannel);
            if (result != RESULT.OK) { Log("混的入广播 playSound 失败: " + result); return; }
            moleHasChannel = true;
            if (elapsed > 0d) moleChannel.setPosition((uint)elapsed, TIMEUNIT.MS);
            var p = unit.Position;
            var pos = new VECTOR { x = p.x, y = p.y, z = p.z };
            var vel = new VECTOR();
            moleChannel.set3DAttributes(ref pos, ref vel);
            moleChannel.setPaused(false);
            localCompleted = false;
        }

        // 每帧：pending 扩展曲到点播放 + 3D 跟随混的入 + 播完自动结束本轮
        internal static void TickMusicPlayback()
        {
            if (pendingPath != null && NetRuntime.SessionTime >= pendingStarted)
            {
                string path = pendingPath;
                double started = pendingStarted;
                pendingPath = null;
                pendingStarted = 0d;
                if (musicPlaying) PlayExtendedLocal(path, started, localPlayingUID);
            }
            if (!moleHasChannel) return;
            bool playing;
            RESULT r = moleChannel.isPlaying(out playing);
            if (r != RESULT.OK)
            {
                moleHasChannel = false;
                ReleaseMoleSound();
                NotifyPlaybackFinished();
                return;
            }
            if (playing)
            {
                if (UnitManager.IsSingletonActive())
                {
                    Unit unit = UnitManager.Instance.GetUnitByUID(localPlayingUID);
                    if (unit != null && unit.transform != null)
                    {
                        var p = unit.Position;
                        var pos = new VECTOR { x = p.x, y = p.y, z = p.z };
                        var vel = new VECTOR();
                        moleChannel.set3DAttributes(ref pos, ref vel);
                    }
                }
            }
            else if (!localCompleted)
            {
                moleHasChannel = false;
                ReleaseMoleSound();
                NotifyPlaybackFinished(); // mp3 播完 = 本轮广播结束
            }
        }

        private static void ReleaseMoleSound()
        {
            if (moleHasSound) { moleSound.release(); moleHasSound = false; }
        }

        private static void NotifyPlaybackFinished()
        {
            if (!musicPlaying) return;
            if (NetRuntime.IsServer) StopMusicServer();
            else if (RoomManager.IsSingletonActive()) RoomManager.Instance.SendToServer(new MoleMusicMessage { Kind = MoleMusicMessage.StopRequest });
        }

        private static void StopMusicLocal()
        {
            pendingPath = null;
            pendingStarted = 0d;
            if (moleHasChannel)
            {
                moleChannel.stop();
                moleHasChannel = false;
            }
            ReleaseMoleSound();
            if (localPlayingMusic != AudioEvents.None)
            {
                if (MonoSingletonT<AudioManager>.IsSingletonActive())
                    MonoSingletonT<AudioManager>.Instance.StopAudio(localPlayingMusic, localPlayingUID);
                localPlayingMusic = AudioEvents.None;
            }
            localPlayingUID = 0;
        }

        // 客机：收到广播 → 本地播放/停止（musicPlaying 标志供 UI 与按键防重复使用）
        private static void OnClientMusic(MoleMusicMessage message)
        {
            if (NetworkServer.active) return;
            if (message.Kind == MoleMusicMessage.Start)
            {
                musicPlaying = true;
                StartLocalPlayback(message.UnitUID, (AudioEvents)message.AudioEvent, message.Hash, message.Started);
            }
            else
            {
                musicPlaying = false;
                clientCooldownRemain = MusicCooldownSeconds;
                StopMusicLocal();
            }
        }

        internal static void BroadcastState()
        {
            if (!RoomManager.IsSingletonActive() || !NetworkServer.active) return;
            RoomManager.Instance.BroadcastToAllExceptHost(new MoleSelectMessage { Version = version, UnitUID = moleUID }, 0);
        }

        // ===== 推倒老师技能（背后 + 按 E） =====

        // 服务端：校验推倒请求（身份/状态/冷却/目标合法/距离/背后）
        private static void OnServerPush(NetworkConnectionToClient connection, MolePushMessage message)
        {
            if (!NetRuntime.IsServer || message.Kind != MolePushMessage.Request) return;
            ulong senderUID = connection == null || connection == NetworkServer.localConnection
                ? MonoSingletonT<UnitManager>.Instance.localPlayerUID
                : RoomManager.Instance.GetPlayerUIDByConnection(connection);
            if (senderUID != moleUID || moleUID == 0) return;
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            if (pushCooldownRemain > 0f) return;
            if (!UnitManager.IsSingletonActive()) return;
            Unit student = UnitManager.Instance.GetUnitByUID(senderUID);
            Unit teacher = UnitManager.Instance.GetUnitByUID(message.TeacherUID);
            if (student == null || !student.isActived || teacher == null || !teacher.isActived) return;
            if (!teacher.IsTeacher || teacher.IsDead || teacher.IsStun) return;
            if (!IsBehindAndClose(student, teacher)) return;

            pushCooldownRemain = PushCooldownSeconds;
            var broadcast = new MolePushMessage { Kind = MolePushMessage.Apply, TeacherUID = teacher.UID };
            if (RoomManager.IsSingletonActive() && NetworkServer.active) RoomManager.Instance.BroadcastToAllExceptHost(broadcast, 0);
            PushApplyLocal(teacher.UID);
            Log("[状态] 混的入从背后推倒了老师");
        }

        // 背后且近距离：学生位于老师身后约 110° 扇区内且距离 ≤ PushRange
        private static bool IsBehindAndClose(Unit student, Unit teacher)
        {
            Vector3 toStudent = student.Position - teacher.Position;
            toStudent.y = 0f;
            if (toStudent.magnitude > PushRange) return false;
            Vector3 fwd = teacher.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) return true; // 朝向缺失时不额外限制
            return Vector3.Dot(fwd.normalized, toStudent.normalized) < PushBehindDot;
        }

        // 找本地玩家身后最近的老师（按键预判定，服务端会再完整校验）
        private static Unit FindPushTarget()
        {
            if (!UnitManager.IsSingletonActive() || !ClassroomManager.IsSingletonActive()) return null;
            Unit self = UnitManager.Instance.GetUnitByUID(UnitManager.Instance.localPlayerUID);
            if (self == null || !self.isActived) return null;
            Unit best = null;
            float bestDist = PushRange;
            foreach (var unit in UnitManager.Instance.playerList)
            {
                if (unit == null || !unit.isActived || !unit.IsTeacher || unit.IsDead || unit.IsStun) continue;
                if (!IsBehindAndClose(self, unit)) continue;
                float dist = Vector3.Distance(self.Position, unit.Position);
                if (dist < bestDist) { bestDist = dist; best = unit; }
            }
            return best;
        }

        // 各端应用：老师倒地 2.5 秒（TryGetProjectileStun 带精确时长，Stun 动画与滑倒一致，tip 自动清理）
        private static void PushApplyLocal(ulong teacherUID)
        {
            if (!UnitManager.IsSingletonActive()) return;
            Unit teacher = UnitManager.Instance.GetUnitByUID(teacherUID);
            if (teacher == null || !teacher.IsTeacher) return;
            teacher.GetComponent<TeacherStunComponent>()?.TryGetProjectileStun(PushStunSeconds);
        }

        // 客机：收到 Apply → 本地应用 + 启动冷却倒计时（服务端本机走 PushApplyLocal）
        private static void OnClientPush(MolePushMessage message)
        {
            if (NetworkServer.active) return;
            if (message.Kind != MolePushMessage.Apply) return;
            clientPushCooldownRemain = PushCooldownSeconds;
            PushApplyLocal(message.TeacherUID);
        }

        // 本地玩家是混的入时，按 E 对背后老师发起推倒（房主直接服务端处理）
        private static void HandlePushKey()
        {
            if (moleUID == 0) return;
            if (!UnitManager.IsSingletonActive() || UnitManager.Instance.localPlayerUID != moleUID) return;
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            if (NetRuntime.IsServer ? pushCooldownRemain > 0f : clientPushCooldownRemain > 0f) return;
            if (!MonoSingletonT<InputManager>.IsSingletonActive() || MonoSingletonT<InputManager>.Instance.FrozedPlayerInput || MonoSingletonT<InputManager>.Instance.FrozedAllInput) return;
            if (!ReInput.isReady) return;
            Keyboard keyboard = ReInput.players.GetPlayer(0).controllers.Keyboard;
            if (keyboard == null || !keyboard.GetKeyDown(PushKey)) return;
            Unit target = FindPushTarget();
            if (target == null) return;
            if (NetRuntime.IsServer)
            {
                pushCooldownRemain = PushCooldownSeconds;
                var broadcast = new MolePushMessage { Kind = MolePushMessage.Apply, TeacherUID = target.UID };
                if (RoomManager.IsSingletonActive() && NetworkServer.active) RoomManager.Instance.BroadcastToAllExceptHost(broadcast, 0);
                PushApplyLocal(target.UID);
                Log("[状态] 混的入从背后推倒了老师");
            }
            else
            {
                RoomManager.Instance.SendToServer(new MolePushMessage { Kind = MolePushMessage.Request, TeacherUID = target.UID });
            }
        }

        // ===== 消息注册与处理 =====

        public static void RegisterClient()
        {
            NetworkClient.RegisterHandler<MoleSelectMessage>(OnMoleSelect, true);
            NetworkClient.RegisterHandler<MoleMusicMessage>(OnClientMusic, true);
            NetworkClient.RegisterHandler<MolePushMessage>(OnClientPush, true);
        }

        public static void RegisterServer()
        {
            NetworkServer.RegisterHandler<MoleMusicMessage>(OnServerMusic, true);
            NetworkServer.RegisterHandler<MolePushMessage>(OnServerPush, true);
        }

        private static void OnMoleSelect(MoleSelectMessage message)
        {
            if (NetworkServer.active) return;
            if (message.Version <= version && moleUID != 0) return;
            version = message.Version;
            moleUID = message.UnitUID;
        }

        // ===== 服务端每帧驱动 =====

        internal static void Tick()
        {
            if (NetRuntime.IsServer)
            {
                // Prepare 进入瞬间若还没有学生，轮询兜底补选
                TrySelectMole();
                if (moleUID != 0 && Time.unscaledTime >= nextRepublish)
                {
                    nextRepublish = Time.unscaledTime + 10f;
                    BroadcastState(); // 中途加入的玩家靠重广播补齐
                }
                TickServerMusic(Time.unscaledDeltaTime);
                if (pushCooldownRemain > 0f) pushCooldownRemain = Math.Max(0f, pushCooldownRemain - Time.unscaledDeltaTime);
            }
            else
            {
                if (clientCooldownRemain > 0f) clientCooldownRemain = Math.Max(0f, clientCooldownRemain - Time.unscaledDeltaTime);
                if (clientPushCooldownRemain > 0f) clientPushCooldownRemain = Math.Max(0f, clientPushCooldownRemain - Time.unscaledDeltaTime);
            }
            MuteLowLevelChannels(musicPlaying);
            TickMusicPlayback();
            HandleMusicKey();
            HandlePushKey();
        }

        // 本地玩家是混的入时，按 B 开关广播（房主直接本地处理）
        private static void HandleMusicKey()
        {
            if (moleUID == 0) return;
            if (!UnitManager.IsSingletonActive() || UnitManager.Instance.localPlayerUID != moleUID) return;
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            if (!MonoSingletonT<InputManager>.IsSingletonActive() || MonoSingletonT<InputManager>.Instance.FrozedPlayerInput || MonoSingletonT<InputManager>.Instance.FrozedAllInput) return;
            if (!ReInput.isReady) return;
            Keyboard keyboard = ReInput.players.GetPlayer(0).controllers.Keyboard;
            if (keyboard == null || !keyboard.GetKeyDown(MusicKey)) return;
            if (NetRuntime.IsServer)
            {
                if (musicPlaying) StopMusicServer();
                else if (musicCooldownRemain <= 0f) StartMusicServer();
            }
            else if (musicPlaying)
            {
                RoomManager.Instance.SendToServer(new MoleMusicMessage { Kind = MoleMusicMessage.StopRequest });
            }
            else if (clientCooldownRemain <= 0f)
            {
                RoomManager.Instance.SendToServer(new MoleMusicMessage { Kind = MoleMusicMessage.Start });
            }
        }

        internal static void Cleanup()
        {
            if (registeredStateHook) MsgCenter.UnRegister(ClassStateChangedMsgId);
            registeredStateHook = false;
        }
    }
}
