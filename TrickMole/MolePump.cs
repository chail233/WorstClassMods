using Mirror;
using UnityEngine;

namespace TrickMole
{
    public sealed class MolePump : MonoBehaviour
    {
        private void Update() { ModEntry.Tick(); }

        private void OnGUI() { Draw(); }

        private void OnDestroy() { ModEntry.Cleanup(); }

        // 右上角常驻提示盒（避开左上角曲库检测按钮）
        private static void Draw()
        {
            if (ModEntry.moleUID == 0) return;
            if (!ClassroomManager.IsSingletonActive()) return;
            var state = ClassroomManager.Instance.curState;
            if (state != EClassroomState.Prepare && state != EClassroomState.Classing) return;
            if (!UnitManager.IsSingletonActive()) return;

            Unit unit = UnitManager.Instance.GetUnitByUID(ModEntry.moleUID);
            string name = unit != null ? unit.GetPlayerUnitName() : "已离开";
            bool isSelf = UnitManager.Instance.localPlayerUID == ModEntry.moleUID;

            // 外层 Area 高度留裕量（最多 4 行内容），内容用 box 自适应，防高 DPI 裁切
            var area = new Rect(Screen.width - 250f, 10f, 240f, 110f);
            GUI.Box(area, "");
            GUILayout.BeginArea(new Rect(area.x + 8f, area.y + 8f, area.width - 16f, area.height - 16f));
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(isSelf ? "你是本局的混的入！" : "本局混的入：" + name);
            GUILayout.Label("捣蛋分数 ×" + ModEntry.BonusMultiplier + "　被挂总进度扣累计 2/3");
            if (isSelf) GUILayout.Label("捣蛋累计：" + ModEntry.moleProgress.ToString("0.#"));
            if (ModEntry.musicPlaying) GUILayout.Label(isSelf ? "广播播放中…再按 " + ModEntry.MusicKey.ToString() + " 停止" : "广播播放中…");
            else if (isSelf && state == EClassroomState.Classing && ModEntry.ClientCooldownRemain > 0f) GUILayout.Label("广播冷却中…");
            else if (isSelf && state == EClassroomState.Classing) GUILayout.Label("按 " + ModEntry.MusicKey.ToString() + " 播放广播（曲库 " + (ModEntry.MusicPool.Length + PmBridge.SharedTrackCount) + " 首）");
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        // ===== Mirror 手工序列化 =====

        internal static void WriteMole(NetworkWriter writer, MoleSelectMessage message)
        {
            writer.WriteUInt(message.Version);
            writer.WriteULong(message.UnitUID);
        }

        internal static MoleSelectMessage ReadMole(NetworkReader reader)
        {
            return new MoleSelectMessage { Version = reader.ReadUInt(), UnitUID = reader.ReadULong() };
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
    }
}
