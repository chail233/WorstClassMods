using System;
using System.Collections.Generic;
using Mirror;
using Rewired;
using UnityEngine;

namespace BioChem
{
    public struct BioChemVendingMessage : NetworkMessage
    {
        public uint Round;
        public byte Result; // 0=请求，1=零食出货，2=冷却，3=先取走出货口的物品，4=补给已满，5=道具出货
    }

    internal static class BioChemArenaSupplies
    {
        private const int MaxLooseAndCarried = 48;
        private const float CrateRefillSeconds = 18f;
        private const float VendingCooldown = 6f;
        private const float UseDistance = 2.3f;
        private static readonly List<int> foodIds = new List<int>();
        private static readonly List<int> propIds = new List<int>();
        private static readonly HashSet<ulong> supplyUnits = new HashSet<ulong>();
        private static readonly ulong[] crateStock = new ulong[4];
        private static readonly float[] crateReadyAt = new float[4];
        private static ulong vendingStock;
        private static float vendingReadyAt;
        private static bool vendPropNext;
        private static float nextLocalRequest;
        private static bool showVending;

        internal static void BeginRound()
        {
            foodIds.Clear();
            propIds.Clear();
            foreach (cfg.Unit row in cfg.Tables.TbUnit.DataList)
            {
                if (row.Type != (int)EUnitType.StudentTrickItem || string.IsNullOrEmpty(row.ModelPath)
                    || !(cfg.Tables.TbItem.GetOrDefault(row.Id)?.Durability > 0f)) continue;
                switch ((EUnitSubType)row.SubType)
                {
                    case EUnitSubType.Food when row.Id != 6020 && row.Id != 6101:
                        foodIds.Add(row.Id);
                        break;
                    case EUnitSubType.BubbleGum:
                    case EUnitSubType.Smoke:
                    case EUnitSubType.Flashbang:
                    case EUnitSubType.Ink:
                        propIds.Add(row.Id);
                        break;
                }
            }
            if (foodIds.Count == 0 || propIds.Count == 0)
                throw new InvalidOperationException("货场补给缺少可用的原生零食或道具");
        }

        internal static void TickServer()
        {
            if (foodIds.Count == 0 || !UnitManager.IsSingletonActive()) return;
            var units = UnitManager.Instance;
            supplyUnits.RemoveWhere(uid => !IsPresent(units.GetUnitByUID(uid)));
            for (int i = 0; i < crateStock.Length; i++)
            {
                bool isPropSlot = i % 2 != 0;
                Vector3 point = BioChemArena.Origin + BioChemArena.Layout.Crates[i / 2]
                    + new Vector3(isPropSlot ? 0.37f : -0.37f, 0.83f, 0f);
                if (crateStock[i] != 0)
                {
                    if (IsOnDisplay(units.GetUnitByUID(crateStock[i]), point)) continue;
                    crateStock[i] = 0;
                    crateReadyAt[i] = Time.unscaledTime + CrateRefillSeconds;
                }
                if (Time.unscaledTime < crateReadyAt[i] || supplyUnits.Count >= MaxLooseAndCarried) continue;
                crateReadyAt[i] = Time.unscaledTime + 2f; // 模型或配置加载失败时节流重试
                crateStock[i] = SpawnSupply(isPropSlot ? propIds : foodIds, point, shelf: true);
            }
        }

        internal static void TickLocal()
        {
            showVending = false;
            if (!ModEntry.enabled || !BioChemArena.Active || !BioChemArena.InRoundPhase || !UnitManager.IsSingletonActive()
                || !InputManager.IsSingletonActive() || !ReInput.isReady || ModEntry.InputOverFrozen()
                || InputManager.Instance.FrozedAllWithoutBase) return;

            Unit self = UnitManager.Instance.localPlayer;
            Vector3 button = VendingButton();
            Camera camera = MonoSingletonT<SceneManager>.IsSingletonActive() ? SceneManager.Instance.MainCamera : null;
            if (camera == null || !CanUse(self, button)) return;
            Vector3 aim = button - camera.transform.position;
            if (Vector3.Dot(camera.transform.forward, aim.normalized) < 0.85f) return;
            showVending = true;
            if (Time.unscaledTime < nextLocalRequest || !ReInput.players.GetPlayer(0).GetButtonDown(eCmdType.Interect1.ToString())) return;

            nextLocalRequest = Time.unscaledTime + 0.35f;
            var message = new BioChemVendingMessage { Round = BioChemArena.Round };
            if (NetRuntime.IsServer) OnServerVending(null, message);
            else RoomManager.Instance.SendToServer(message);
        }

        internal static void DrawPrompt()
        {
            if (!showVending || !InputManager.IsSingletonActive()) return;
            string key = InputManager.Instance.BindingName(eCmdType.Interect1);
            GUI.Box(new Rect((Screen.width - 248f) / 2f, Screen.height * 0.62f, 248f, 28f), "[" + key + "] 贩卖机 · 领取补给");
        }

        internal static void OnServerVending(NetworkConnectionToClient connection, BioChemVendingMessage message)
        {
            if (message.Result != 0 || !NetworkServer.active || !ModEntry.enabled || !BioChemArena.Active
                || message.Round != BioChemArena.Round || !BioChemArena.InRoundPhase || !UnitManager.IsSingletonActive()) return;
            ulong uid = ModEntry.ResolveSender(connection);
            if (uid == 0 || !CanUse(UnitManager.Instance.GetUnitByUID(uid), VendingButton())) return;

            Vector3 point = BioChemArena.Origin + BioChemArena.Layout.Vending + new Vector3(0f, 0.59f, 0.8f);
            if (Time.unscaledTime < vendingReadyAt) message.Result = 2;
            else if (vendingStock != 0 && IsOnDisplay(UnitManager.Instance.GetUnitByUID(vendingStock), point)) message.Result = 3;
            else if (supplyUnits.Count >= MaxLooseAndCarried) message.Result = 4;
            else
            {
                vendingStock = SpawnSupply(vendPropNext ? propIds : foodIds, point, shelf: false);
                vendingReadyAt = Time.unscaledTime + (vendingStock == 0 ? 2f : VendingCooldown);
                message.Result = 2;
                if (vendingStock != 0)
                {
                    message.Result = vendPropNext ? (byte)5 : (byte)1;
                    vendPropNext = !vendPropNext;
                }
            }
            if (connection == null || connection == NetworkServer.localConnection) OnClientVending(message);
            else connection.Send(message);
        }

        internal static void OnClientVending(BioChemVendingMessage message)
        {
            if (message.Round != BioChemArena.Round || !BioChemArena.Active) return;
            switch (message.Result)
            {
                case 1: ModEntry.NotifyAll("贩卖机已出货，拾取零食后吃完可补弹"); break;
                case 2: ModEntry.NotifyAll("贩卖机补货中，请稍等"); break;
                case 3: ModEntry.NotifyAll("请先取走出货口的补给"); break;
                case 4: ModEntry.NotifyAll("场地补给已满，请先使用已有物品"); break;
                case 5: ModEntry.NotifyAll("贩卖机已出货，可以拾取道具"); break;
            }
        }

        private static Vector3 VendingButton() => BioChemArena.Origin + BioChemArena.Layout.Vending + new Vector3(0f, 1.15f, 0.45f);

        private static bool CanUse(Unit unit, Vector3 button)
        {
            return unit != null && unit.isActived && unit.IsStudent && !unit.IsDead && !unit.IsStun && !unit.BodyActionsBlocked
                && BioChemArena.Contains(unit.Position) && Vector3.Distance(unit.Position + Vector3.up * 0.8f, button) <= UseDistance
                && !BioChemArena.IsCoverBetween(unit.Position + Vector3.up, button);
        }

        private static bool IsPresent(Unit unit) => unit != null && unit.isActived && !unit.IsDead;

        private static bool IsOnDisplay(Unit unit, Vector3 point)
        {
            return IsPresent(unit) && (unit.GetData<RoleData>()?.NetData.MountingPointNet.MountingUID ?? 0) == 0
                && (unit.Position - point).sqrMagnitude < 1f;
        }

        private static ulong SpawnSupply(List<int> ids, Vector3 point, bool shelf)
        {
            try
            {
                int id = ids[UnityEngine.Random.Range(0, ids.Count)];
                var manager = UnitManager.Instance;
                UnitDataNet data = manager.CreateUnitNetData(id, point, Vector3.zero, syncMsg: false);
                // 校园六楼的食物替换规则依赖当前层；独立货场始终发普通食物。
                data.UseToiletFoodLogic = false;
                data.MountingPointNet = new MountingPointDataNet
                {
                    HaveValue = true, MountingPoint = EUnitPartType.None, IsKinematic = shelf,
                    IsInteraction = true, LayerName = "Interaction", isSync = true,
                };
                Unit unit = manager.CreateCommonUnit(data, isLocal: true);
                if (unit == null) return 0;
                supplyUnits.Add(unit.UID);
                RoomManager.Instance.BroadcastToAllExceptHost(new UnitBornMessageResponse_S2C { unitUID = unit.UID, netData = data });
                return unit.UID;
            }
            catch (Exception ex)
            {
                ModEntry.Log("[状态] 货场补给生成失败：" + ex.Message);
                return 0;
            }
        }

        internal static void Reset()
        {
            if (NetworkServer.active && UnitManager.IsSingletonActive() && RoomManager.IsSingletonActive())
            {
                foreach (ulong uid in supplyUnits)
                {
                    Unit item = UnitManager.Instance.GetUnitByUID(uid);
                    if (IsPresent(item)) item.SyncBack();
                }
            }
            supplyUnits.Clear();
            foodIds.Clear();
            propIds.Clear();
            Array.Clear(crateStock, 0, crateStock.Length);
            Array.Clear(crateReadyAt, 0, crateReadyAt.Length);
            vendingStock = 0;
            vendingReadyAt = 0f;
            vendPropNext = false;
            nextLocalRequest = 0f;
            showVending = false;
        }
    }
}
