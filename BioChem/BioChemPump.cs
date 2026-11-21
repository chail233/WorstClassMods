using Mirror;
using Rewired;
using UnityEngine;

namespace BioChem
{
    public sealed class BioChemPump : MonoBehaviour
    {
        private readonly BioChemHud hud = new BioChemHud();

        private void Update()
        {
            ModEntry.Tick();
            BioChemGameSettings.Tick(); // 嵌入游戏设置窗口：开窗轮询跳页 + 场景实例扫描 + 数值刷新
            BioChemPanel.Refresh();
        }

        private void LateUpdate() => hud.Tick();

        private void OnGUI()
        {
            // F7 兜底通道：主菜单里游戏输入系统（InputManager/Rewired 玩家）未必激活，IMGUI 事件总能收到按键。
            // Rewired 就绪时走 HandleKeys 的硬件级边沿检测，此通道关闭，避免按住 F7 的 IMGUI 重复 KeyDown 造成面板闪烁
            if (!MonoSingletonT<InputManager>.IsSingletonActive() || !ReInput.isReady)
            {
                if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F7)
                {
                    ModEntry.TogglePanel();
                    Event.current.Use();
                }
            }
            if (ModEntry.enabled)
                HunterFeedback.DrawOverlay(); // 猎刀命中标记/伤害浮字（攻击者本机，空状态零开销早退）
        }

        private void OnDestroy()
        {
            hud.Dispose();
            ModEntry.Cleanup();
        }

        // ===== Mirror 手工序列化 =====

        internal static void WriteState(NetworkWriter writer, BioChemStateMessage message)
        {
            writer.WriteUInt(message.Version);
            writer.WriteString(message.Json);
            writer.WriteString(message.Notify);
        }

        internal static BioChemStateMessage ReadState(NetworkReader reader)
        {
            return new BioChemStateMessage
            {
                Version = reader.ReadUInt(),
                Json = reader.ReadString(),
                // Mirror 对 null string 会读回 ""，公告判空必须用 IsNullOrEmpty
                Notify = reader.ReadString()
            };
        }

        internal static void WriteDart(NetworkWriter writer, BioChemDartMessage message)
        {
            writer.WriteByte(message.Kind);
            writer.WriteULong(message.TargetUID);
            writer.WriteInt(message.TargetHp);
            writer.WriteByte(message.SlowStacks);
            writer.WriteULong(message.UserUID);
            writer.WriteVector3(message.Origin);
            writer.WriteVector3(message.Direction);
            writer.WriteFloat(message.Speed);
            writer.WriteFloat(message.Range);
            writer.WriteFloat(message.Radius);
            writer.WriteFloat(message.StunDuration);
        }

        internal static BioChemDartMessage ReadDart(NetworkReader reader)
        {
            return new BioChemDartMessage
            {
                Kind = reader.ReadByte(),
                TargetUID = reader.ReadULong(),
                TargetHp = reader.ReadInt(),
                SlowStacks = reader.ReadByte(),
                UserUID = reader.ReadULong(),
                Origin = reader.ReadVector3(),
                Direction = reader.ReadVector3(),
                Speed = reader.ReadFloat(),
                Range = reader.ReadFloat(),
                Radius = reader.ReadFloat(),
                StunDuration = reader.ReadFloat()
            };
        }

        internal static void WriteWeaponSlot(NetworkWriter writer, BioChemWeaponSlotMessage message)
        {
            writer.WriteULong(message.UserUID);
            writer.WriteByte(message.Slot);
            writer.WriteULong(message.ItemUID);
        }

        internal static BioChemWeaponSlotMessage ReadWeaponSlot(NetworkReader reader)
        {
            return new BioChemWeaponSlotMessage { UserUID = reader.ReadULong(), Slot = reader.ReadByte(), ItemUID = reader.ReadULong() };
        }

        internal static void WriteMelee(NetworkWriter writer, BioChemMeleeMessage message)
        {
            writer.WriteByte(message.Kind);
            writer.WriteBool(message.Heavy);
            writer.WriteULong(message.UnitUID);
            writer.WriteInt(message.Hp);
            writer.WriteVector3(message.Origin);
            writer.WriteVector3(message.Direction);
            writer.WriteULong(message.AttackerUID);
        }

        internal static BioChemMeleeMessage ReadMelee(NetworkReader reader)
        {
            return new BioChemMeleeMessage
            {
                Kind = reader.ReadByte(),
                Heavy = reader.ReadBool(),
                UnitUID = reader.ReadULong(),
                Hp = reader.ReadInt(),
                Origin = reader.ReadVector3(),
                Direction = reader.ReadVector3(),
                AttackerUID = reader.ReadULong()
            };
        }

        internal static void WriteGrenade(NetworkWriter writer, BioChemGrenadeMessage message)
        {
            writer.WriteByte(message.Kind);
            writer.WriteULong(message.UserUID);
            writer.WriteUInt(message.Seq);
            writer.WriteInt(message.Count);
            writer.WriteVector3(message.Origin);
            writer.WriteVector3(message.Velocity);
            writer.WriteVector3(message.Position);
        }

        internal static BioChemGrenadeMessage ReadGrenade(NetworkReader reader)
        {
            return new BioChemGrenadeMessage
            {
                Kind = reader.ReadByte(),
                UserUID = reader.ReadULong(),
                Seq = reader.ReadUInt(),
                Count = reader.ReadInt(),
                Origin = reader.ReadVector3(),
                Velocity = reader.ReadVector3(),
                Position = reader.ReadVector3()
            };
        }

        internal static void WriteStun(NetworkWriter writer, BioChemStunMessage message)
        {
            writer.WriteByte(message.Kind);
            writer.WriteULong(message.TargetUID);
            writer.WriteFloat(message.Duration);
            writer.WriteFloat(message.Stamina);
        }

        internal static BioChemStunMessage ReadStun(NetworkReader reader)
        {
            return new BioChemStunMessage
            {
                Kind = reader.ReadByte(),
                TargetUID = reader.ReadULong(),
                Duration = reader.ReadFloat(),
                Stamina = reader.ReadFloat()
            };
        }

        internal static void WriteWrite(NetworkWriter writer, BioChemWriteMessage message)
        {
            writer.WriteByte(message.Kind);
        }

        internal static BioChemWriteMessage ReadWrite(NetworkReader reader)
        {
            return new BioChemWriteMessage { Kind = reader.ReadByte() };
        }

        internal static void WriteDown(NetworkWriter writer, BioChemDownMessage message)
        {
            writer.WriteByte(message.Kind);
            writer.WriteULong(message.UnitUID);
            writer.WriteVector3(message.Position);
        }

        internal static BioChemDownMessage ReadDown(NetworkReader reader)
        {
            return new BioChemDownMessage
            {
                Kind = reader.ReadByte(),
                UnitUID = reader.ReadULong(),
                Position = reader.ReadVector3()
            };
        }

        internal static void WriteHunter(NetworkWriter writer, BioChemHunterMessage message)
        {
            writer.WriteByte(message.Kind);
            writer.WriteULong(message.UnitUID);
            writer.WriteInt(message.Hp);
            writer.WriteVector3(message.Origin);
            writer.WriteVector3(message.Direction);
            writer.WriteULong(message.AttackerUID);
        }

        internal static BioChemHunterMessage ReadHunter(NetworkReader reader)
        {
            return new BioChemHunterMessage
            {
                Kind = reader.ReadByte(),
                UnitUID = reader.ReadULong(),
                Hp = reader.ReadInt(),
                Origin = reader.ReadVector3(),
                Direction = reader.ReadVector3(),
                AttackerUID = reader.ReadULong()
            };
        }
    }
}
