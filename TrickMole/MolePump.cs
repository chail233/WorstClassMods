using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace TrickMole
{
    public sealed class MolePump : MonoBehaviour
    {
        private void Update() { ModEntry.Tick(); }

        private void OnGUI() { Draw(); }

        private void OnDestroy() { ModEntry.Cleanup(); }

        // 右上角常驻提示盒（避开左上角曲库检测按钮），行数随角色数自适应
        private static void Draw()
        {
            if (ModEntry.AssignedCount == 0) return;
            if (!ClassroomManager.IsSingletonActive()) return;
            var state = ClassroomManager.Instance.curState;
            if (state != EClassroomState.Prepare && state != EClassroomState.Classing) return;
            if (!UnitManager.IsSingletonActive()) return;

            RoleDef myRole = ModEntry.MyRole;
            var lines = new List<string>();
            foreach (var pair in ModEntry.GetAssignedList())
            {
                Unit unit = UnitManager.Instance.GetUnitByUID(pair.Key);
                string who = pair.Key == UnitManager.Instance.localPlayerUID
                    ? "你"
                    : (unit != null ? unit.GetPlayerUnitName() : "已离开");
                lines.Add(pair.Value.name + "：" + who);
            }

            var area = new Rect(Screen.width - 250f, 10f, 250f, 40f + 24f * (lines.Count + 2));
            GUI.Box(area, "");
            GUILayout.BeginArea(new Rect(area.x + 8f, area.y + 8f, area.width - 16f, area.height - 16f));
            GUILayout.BeginVertical(GUI.skin.box);
            foreach (string line in lines) GUILayout.Label(line);
            if (myRole != null)
            {
                GUILayout.Label("捣蛋分数 ×" + myRole.trickMultiplier.ToString("0.#") + "　被挂扣累计 " + (myRole.progressPenaltyFraction * 100f).ToString("0") + "%");
                GUILayout.Label("捣蛋累计：" + ModEntry.MyProgress.ToString("0.#"));
                if (ModEntry.musicPlaying) GUILayout.Label(myRole.canBroadcast ? "广播播放中…再按 " + ModEntry.MusicKey.ToString() + " 停止" : "广播播放中…");
                else if (myRole.canBroadcast && state == EClassroomState.Classing && ModEntry.ClientCooldownRemain > 0f) GUILayout.Label("广播冷却中…");
                else if (myRole.canBroadcast && state == EClassroomState.Classing) GUILayout.Label("按 " + ModEntry.MusicKey.ToString() + " 播放广播（曲库 " + (ModEntry.MusicPool.Length + PmBridge.SharedTrackCount) + " 首）");
                if (myRole.canPush && state == EClassroomState.Classing && ModEntry.PushCooldownRemain > 0f) GUILayout.Label("推倒冷却中… " + Mathf.CeilToInt(ModEntry.PushCooldownRemain) + "s");
            }
            GUILayout.EndVertical();
            GUILayout.EndArea();

            // 可推时按键浮层（原生键帽样式），冷却/不满足条件不显示
            if (myRole != null && myRole.canPush && state == EClassroomState.Classing && ModEntry.PushCooldownRemain <= 0f && ModEntry.HasPushTarget())
                KeyHint.Prompt(ModEntry.PushKey, "推倒老师");
        }

        // ===== Mirror 手工序列化 =====

        internal static void WriteMole(NetworkWriter writer, MoleSelectMessage message)
        {
            writer.WriteUInt(message.Version);
            writer.WriteULong(message.UnitUID);
            writer.WriteString(message.RoleJson);
        }

        internal static MoleSelectMessage ReadMole(NetworkReader reader)
        {
            return new MoleSelectMessage
            {
                Version = reader.ReadUInt(),
                UnitUID = reader.ReadULong(),
                RoleJson = reader.ReadString()
            };
        }

        internal static void WriteMusic(NetworkWriter writer, MoleMusicMessage message)
        {
            writer.WriteByte(message.Kind);
            writer.WriteULong(message.UnitUID);
            writer.WriteUInt(message.AudioEvent);
            writer.WriteString(message.Hash);
            writer.WriteDouble(message.Started);
        }

        internal static MoleMusicMessage ReadMusic(NetworkReader reader)
        {
            return new MoleMusicMessage
            {
                Kind = reader.ReadByte(),
                UnitUID = reader.ReadULong(),
                AudioEvent = reader.ReadUInt(),
                Hash = reader.ReadString(),
                Started = reader.ReadDouble()
            };
        }

        internal static void WritePush(NetworkWriter writer, MolePushMessage message)
        {
            writer.WriteByte(message.Kind);
            writer.WriteULong(message.TeacherUID);
        }

        internal static MolePushMessage ReadPush(NetworkReader reader)
        {
            return new MolePushMessage
            {
                Kind = reader.ReadByte(),
                TeacherUID = reader.ReadULong()
            };
        }
    }
}
