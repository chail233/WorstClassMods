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

// C2S 只请求栏位（UserUID 忽略），Slot=None=收起当前武器。服务端结算后向全员广播：
// UserUID=玩家、Slot=新槽位、ItemUID=发放的物品 UID（近战/投掷物无实体=0）。
// 各端记录全员槽位（远端世界模型显隐依据）；UserUID 是自己时按槽位记录物品 UID，启用对应武器的输入。
public struct BioChemWeaponSlotMessage : NetworkMessage
    {
        public ulong UserUID;
        public byte Slot;
        public ulong ItemUID;
    }

    internal static class StudentWeapons
    {
        private static readonly (KeyCode key, string label, string weapon, Func<Unit, ulong> equip, Action<ulong> holster)[] slots =
        {
            (KeyCode.None, "", null, null, null),
            (KeyCode.Alpha1, "主武器", "AK47", ModEntry.EnsureAkEquipped, ModEntry.RevokeTaser),
            (KeyCode.Alpha2, "副武器", "手枪", ModEntry.EnsurePistolEquipped, ModEntry.RevokeTaser),
            (KeyCode.Alpha3, "近战武器", "尼泊尔军刀", ModEntry.EnsureMeleeEquipped, null),
            (KeyCode.Alpha4, "投掷物", "生化手雷", ModEntry.EnsureGrenadeEquipped, null)
        };

        private static readonly Dictionary<ulong, StudentWeaponSlot> serverSelection = new Dictionary<ulong, StudentWeaponSlot>();
        // 全员槽位视图：服务端广播 + 快照对齐。远端世界模型（AK/手枪/军刀/手雷）显隐都以此为准
        private static readonly Dictionary<ulong, StudentWeaponSlot> slotByUid = new Dictionary<ulong, StudentWeaponSlot>();
        // 本地玩家各槽位最近一次发放的物品 UID（主/副武器用）。切槽后旧物品等原生收回消息销毁，
        // 期间仍要拦住它的原生单击开火——所以按槽位各留一份，不互相覆盖
        private static readonly ulong[] localSlotItemUid = new ulong[8];

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

        // ===== 服务端 =====

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

        // 服务端记录的玩家当前槽位（连发结算按它区分 AK/手枪节奏与备弹）
        internal static StudentWeaponSlot ServerSelectionOf(ulong uid)
            => serverSelection.TryGetValue(uid, out var slot) ? slot : StudentWeaponSlot.None;

        internal static IEnumerable<KeyValuePair<ulong, StudentWeaponSlot>> ServerSelections => serverSelection;

        // ===== 客户端 =====

        internal static void SelectLocal(StudentWeaponSlot slot) => Selected = slot;

        internal static StudentWeaponSlot RemoteSlotOf(ulong uid)
            => slotByUid.TryGetValue(uid, out var slot) ? slot : StudentWeaponSlot.None;

        // 各端应用槽位广播/快照。Slot=None=收起广播：只清全员槽位视图（localSlotItemUid 保留，回收窗口仍要拦原生开火）
        internal static void ApplyIssuedItem(BioChemWeaponSlotMessage message)
        {
            if (message.Slot == (byte)StudentWeaponSlot.None)
            {
                if (message.UserUID != 0) slotByUid[message.UserUID] = StudentWeaponSlot.None;
                return;
            }
            if (!IsValid(message.Slot)) return;
            if (message.UserUID != 0) slotByUid[message.UserUID] = (StudentWeaponSlot)message.Slot;
            if (UnitManager.IsSingletonActive() && message.UserUID == UnitManager.Instance.localPlayerUID
                && (message.Slot == (byte)StudentWeaponSlot.Primary || message.Slot == (byte)StudentWeaponSlot.Secondary))
                localSlotItemUid[message.Slot] = message.ItemUID;
        }

        // 快照兜底（中途加入/漏包自愈）：全量槽位视图重建
        internal static void ApplySnapshot(List<long> uids, List<int> vals)
        {
            slotByUid.Clear();
            for (int i = 0; i < uids.Count && i < vals.Count; i++)
                if (uids[i] != 0 && IsValid((byte)vals[i]))
                    slotByUid[(ulong)uids[i]] = (StudentWeaponSlot)vals[i];
        }

        internal static void ResetLocalItem()
        {
            Array.Clear(localSlotItemUid, 0, localSlotItemUid.Length);
        }

        // 当前槽位的枪（嘴叼 Taser 承载物）是不是本端已发放的那把——射击循环门禁
        internal static bool IsLocalGun(Unit item)
        {
            if (item == null) return false;
            byte slot = (byte)Selected;
            return slot < localSlotItemUid.Length && localSlotItemUid[slot] != 0 && item.UID == localSlotItemUid[slot];
        }

        // 这把嘴叼物品是不是本端任意槽位已发放的枪——BeforeTaserHandleInput 用它吞原生单击开火。
        // 切槽的回收窗口里旧枪还挂在嘴上（Selected 已切走），必须照样吞，否则原生单发会把吹箭物品消耗掉
        internal static bool IsLocalGunItem(Unit item)
        {
            if (item == null) return false;
            byte primary = (byte)StudentWeaponSlot.Primary, secondary = (byte)StudentWeaponSlot.Secondary;
            return (localSlotItemUid[primary] != 0 && item.UID == localSlotItemUid[primary])
                || (localSlotItemUid[secondary] != 0 && item.UID == localSlotItemUid[secondary]);
        }

        // 主武器专用判定（等价于 Selected==Primary 时的 IsLocalGun；保留给 Ak47Weapon 内部门禁）
        internal static bool IsLocalAk(Unit item)
        {
            if (item == null || Selected != StudentWeaponSlot.Primary) return false;
            byte primary = (byte)StudentWeaponSlot.Primary;
            return localSlotItemUid[primary] != 0 && item.UID == localSlotItemUid[primary];
        }

        internal static void RemovePlayer(ulong uid)
        {
            if (serverSelection.Remove(uid, out var slot)) slots[(int)slot].holster?.Invoke(uid);
            slotByUid.Remove(uid);
            if (UnitManager.IsSingletonActive() && uid == UnitManager.Instance.localPlayerUID)
                Selected = StudentWeaponSlot.None;
        }

        internal static void ResetRound()
        {
            foreach (ulong uid in new List<ulong>(serverSelection.Keys)) RemovePlayer(uid);
            slotByUid.Clear();
            Selected = StudentWeaponSlot.None;
        }

    }
}
