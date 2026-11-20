using System;
using System.Collections.Generic;
using Mirror;
using Rewired;
using UnityEngine;

namespace BioChem
{
    internal enum StudentWeaponSlot : byte
    {
        None,
        Primary,
        Secondary,
        Melee,
        Throwable
    }

    // C2S 只请求栏位。S2C 带服务端发放的物品 UID，客户端据此启用武器输入。
    public struct BioChemWeaponSlotMessage : NetworkMessage
    {
        public byte Slot;
        public ulong ItemUID;
    }

    internal static class StudentWeapons
    {
        private static readonly (KeyCode key, string label, string weapon, Func<Unit, ulong> equip, Action<ulong> holster)[] slots =
        {
            (KeyCode.None, "", null, null, null),
            (KeyCode.Alpha1, "主武器", "AK47", ModEntry.EnsureAkEquipped, ModEntry.RevokeTaser),
            (KeyCode.Alpha2, "副武器", null, null, null),
            (KeyCode.Alpha3, "近战武器", null, null, null),
            (KeyCode.Alpha4, "投掷物", null, null, null)
        };

        private static readonly Dictionary<ulong, StudentWeaponSlot> serverSelection = new Dictionary<ulong, StudentWeaponSlot>();
        private static ulong localPrimaryItemUID;

        internal static StudentWeaponSlot Selected { get; private set; }
        internal static string SelectedWeapon => WeaponName(Selected);
        internal static string SlotName(StudentWeaponSlot slot) => slots[(int)slot].label;
        internal static string WeaponName(StudentWeaponSlot slot) => slots[(int)slot].weapon;

        internal static bool CanSelect(Unit student) => student != null && student.isPlayer && student.isActived
            && !student.IsDead && student.IsStudent && !ModEntry.infected.Contains(student.UID)
            && !ModEntry.hunters.Contains(student.UID);

        internal static bool IsValid(byte slot) => slot > (byte)StudentWeaponSlot.None && slot < slots.Length;

        internal static StudentWeaponSlot ReadSlotKey(Keyboard keyboard)
        {
            for (int i = 1; i < slots.Length; i++)
                if (keyboard.GetKeyDown(slots[i].key)) return (StudentWeaponSlot)i;
            return StudentWeaponSlot.None;
        }

        internal static ulong SelectOnServer(Unit student, StudentWeaponSlot slot)
        {
            serverSelection.TryGetValue(student.UID, out var previous);
            if (previous != slot)
            {
                slots[(int)previous].holster?.Invoke(student.UID);
                serverSelection[student.UID] = slot;
            }

            // 同槽再按只补齐缺失的装备，例如上次被嘴部道具占用。
            return slots[(int)slot].equip?.Invoke(student) ?? 0;
        }

        internal static void SelectLocal(StudentWeaponSlot slot) => Selected = slot;

        internal static void ApplyIssuedItem(BioChemWeaponSlotMessage message)
        {
            // 等原生收回消息销毁旧物品。在此之前仍要拦住旧 AK 的原生单击开火。
            if (message.Slot == (byte)StudentWeaponSlot.Primary && message.ItemUID != 0)
                localPrimaryItemUID = message.ItemUID;
        }

        internal static bool IsLocalAk(Unit item) => localPrimaryItemUID != 0 && item != null && item.UID == localPrimaryItemUID;

        internal static void ResetLocalItem() => localPrimaryItemUID = 0;

        internal static void RemovePlayer(ulong uid)
        {
            if (serverSelection.Remove(uid, out var slot)) slots[(int)slot].holster?.Invoke(uid);
            if (UnitManager.IsSingletonActive() && uid == UnitManager.Instance.localPlayerUID)
                Selected = StudentWeaponSlot.None;
        }

        internal static void ResetRound()
        {
            foreach (ulong uid in new List<ulong>(serverSelection.Keys)) RemovePlayer(uid);
            Selected = StudentWeaponSlot.None;
        }

    }
}
