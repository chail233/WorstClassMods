using System;
using System.Collections.Generic;
using Mirror;

namespace TrickMole
{
    public struct MoleSprintMessage : NetworkMessage
    {
        public const byte Request = 1, Start = 2, Exhausted = 3;
        public byte Kind;
        public ulong StudentUID;
        public double EndsAt;
    }

    internal static class AthleteSprint
    {
        internal const float SpeedMultiplier = 2f;
        private const double Duration = 10d;
        private const double Cooldown = 10d;
        private const float StunDuration = 1f;

        private static readonly Dictionary<ulong, double> readyAtByUid = new Dictionary<ulong, double>();
        private static readonly List<ulong> activePlayers = new List<ulong>();
        private static ulong localSprintUid;
        private static double localEndsAt;

        internal static float ActiveRemaining => (float)Math.Max(0d, localEndsAt - NetRuntime.SessionTime);
        internal static float CooldownRemaining => localSprintUid == 0 || ActiveRemaining > 0f
            ? 0f : (float)Math.Max(0d, localEndsAt + Cooldown - NetRuntime.SessionTime);

        internal static bool HasSkill(RoleDef role) => role?.id == "athlete";

        internal static bool IsActiveFor(ulong uid) => localSprintUid == uid && ActiveRemaining > 0f;

        internal static void RegisterSerializers()
        {
            Writer<MoleSprintMessage>.write = (writer, message) =>
            {
                writer.WriteByte(message.Kind);
                writer.WriteULong(message.StudentUID);
                writer.WriteDouble(message.EndsAt);
            };
            Reader<MoleSprintMessage>.read = reader => new MoleSprintMessage
            {
                Kind = reader.ReadByte(),
                StudentUID = reader.ReadULong(),
                EndsAt = reader.ReadDouble()
            };
        }

        internal static void RegisterClient() => NetworkClient.RegisterHandler<MoleSprintMessage>(OnClientMessage, true);

        internal static void RegisterServer() => NetworkServer.RegisterHandler<MoleSprintMessage>(OnServerRequest, true);

        internal static void Request(Unit student)
        {
            if (!CanAffect(student) || student.IsStun || IsActiveFor(student.UID) || CooldownRemaining > 0f) return;
            if (NetRuntime.IsServer) TryStart(student.UID);
            else if (RoomManager.IsSingletonActive() && NetworkClient.isConnected)
                RoomManager.Instance.SendToServer(new MoleSprintMessage { Kind = MoleSprintMessage.Request });
        }

        private static void OnServerRequest(NetworkConnectionToClient connection, MoleSprintMessage message)
        {
            if (!NetRuntime.IsServer || message.Kind != MoleSprintMessage.Request || !RoomManager.IsSingletonActive()) return;
            TryStart(RoomManager.Instance.GetPlayerUIDByConnection(connection));
        }

        private static void TryStart(ulong uid)
        {
            if (!IsClassing() || !UnitManager.IsSingletonActive()) return;
            if (!ModEntry.assigned.TryGetValue(uid, out RoleDef role) || !HasSkill(role)) return;
            Unit student = UnitManager.Instance.GetUnitByUID(uid);
            if (!CanAffect(student) || student.IsStun) return;
            double now = NetRuntime.SessionTime;
            if (readyAtByUid.TryGetValue(uid, out double readyAt) && now < readyAt) return;
            if (activePlayers.Contains(uid)) return;

            double endsAt = now + Duration;
            readyAtByUid[uid] = endsAt + Cooldown;
            activePlayers.Add(uid);
            var start = new MoleSprintMessage { Kind = MoleSprintMessage.Start, StudentUID = uid, EndsAt = endsAt };
            // 移速由玩家本机计算，只有冲刺者需要起跑时间。
            if (student.isLocalPlayer) ApplyStart(start);
            else RoomManager.Instance.SendToClientByUID(uid, start);
        }

        internal static void Tick()
        {
            if (!IsClassing() || !RoomManager.IsSingletonActive() || RoomManager.Instance.CurrentRoom == null)
            {
                Reset();
                return;
            }
            if (!NetRuntime.IsServer || !UnitManager.IsSingletonActive()) return;

            double now = NetRuntime.SessionTime;
            for (int i = activePlayers.Count - 1; i >= 0; i--)
            {
                ulong uid = activePlayers[i];
                if (now < readyAtByUid[uid] - Cooldown) continue;
                activePlayers.RemoveAt(i);

                Unit student = UnitManager.Instance.GetUnitByUID(uid);
                if (!CanAffect(student)) continue;
                RoomManager.Instance.BroadcastToAllExceptHost(new MoleSprintMessage
                {
                    Kind = MoleSprintMessage.Exhausted,
                    StudentUID = uid
                });
                ApplyExhaustion(student);
            }
        }

        private static void OnClientMessage(MoleSprintMessage message)
        {
            if (NetworkServer.active || !IsClassing() || !UnitManager.IsSingletonActive()) return;
            if (message.Kind == MoleSprintMessage.Start) ApplyStart(message);
            else if (message.Kind == MoleSprintMessage.Exhausted)
                ApplyExhaustion(UnitManager.Instance.GetUnitByUID(message.StudentUID));
        }

        private static void ApplyStart(MoleSprintMessage message)
        {
            if (message.StudentUID != UnitManager.Instance.localPlayerUID || !HasSkill(ModEntry.MyRole)) return;
            localSprintUid = message.StudentUID;
            localEndsAt = message.EndsAt;
        }

        private static void ApplyExhaustion(Unit student)
        {
            if (!CanAffect(student)) return;
            StudentStunComponent stun = student.GetComponent<StudentStunComponent>();
            if (stun == null || stun.RemainingStunTime >= StunDuration) return;
            // 原生精确时长通道拒绝重复眩晕。剩余不足 1 秒时才接续，保留更长的眩晕。
            if (stun.isStuned) stun.InterruptStun();
            stun.TryGetProjectileStun(StunDuration);
        }

        private static bool CanAffect(Unit student)
        {
            if (student == null || !student.isActived || !student.IsStudent || student.IsDead) return false;
            int state = student.CurState();
            return state != (int)EUnitFsmStateType.StudentBeCatched && state != (int)EUnitFsmStateType.StudentPunished;
        }

        private static bool IsClassing() => ClassroomManager.IsSingletonActive()
            && ClassroomManager.Instance.curState == EClassroomState.Classing;

        internal static void ClearLocal()
        {
            localSprintUid = 0;
            localEndsAt = 0d;
        }

        internal static void Reset()
        {
            activePlayers.Clear();
            readyAtByUid.Clear();
            ClearLocal();
        }
    }
}
