using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace TeacherReward
{
    public sealed class TeacherRewardPump : MonoBehaviour
    {
        private void Update()
        {
            ModEntry.Tick();
            RewardPanel.Refresh(); // 面板开着时每帧刷新点数显示与按钮可用态（Text 同值写入不重排，无害）
        }

        private void OnGUI() { DrawHint(); }

        private void OnDestroy() { ModEntry.Cleanup(); }

        // 左上角小信息盒：老师上课中且有点数或技能卡生效时显示（学生不占屏）。
        // 外层无样式 BeginArea + 内层 BeginVertical(GUI.skin.box)，高度按行数自适应（别写死，高 DPI 会裁切）
        private static void DrawHint()
        {
            if (RewardPanel.IsOpen) return;
            if (!ClassroomManager.IsSingletonActive() || ClassroomManager.Instance.curState != EClassroomState.Classing) return;
            if (!ModEntry.IsLocalTeacher) return;
            int points = ModEntry.MyPointsLeft;
            bool swift = ModEntry.LocalHasBuff(ModEntry.BuffSwift);
            bool chalk = ModEntry.LocalHasBuff(ModEntry.BuffChalk);
            bool shield = ModEntry.LocalHasBuff(ModEntry.BuffShield);
            if (points <= 0 && !swift && !chalk && !shield) return;

            var lines = new List<string> { "老师奖励点 " + points + "/" + ModEntry.MaxPoints };
            if (swift) lines.Add("健步如飞生效中：移速 ×" + RewardConfig.Data.swiftSpeedMultiplier.ToString("0.##"));
            if (chalk) lines.Add("重磅粉笔生效中：晕眩时长 +" + Mathf.RoundToInt(RewardConfig.Data.chalkStunBonus * 100f) + "%");
            if (shield) lines.Add("护盾就位：挡下一次晕倒");
            if (points > 0) lines.Add("按 " + RewardConfig.PanelKeyCode + " 打开奖励面板选技能卡");

            var area = new Rect(10f, 10f, 340f, 16f + 30f * lines.Count);
            GUI.Box(area, "");
            GUILayout.BeginArea(new Rect(area.x + 8f, area.y + 8f, area.width - 16f, area.height - 16f));
            GUILayout.BeginVertical(GUI.skin.box);
            foreach (string line in lines) GUILayout.Label(line);
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        // ===== Mirror 手工序列化 =====

        internal static void WriteReward(NetworkWriter writer, TeacherRewardMessage message)
        {
            writer.WriteByte(message.Kind);
            writer.WriteUInt(message.Version);
            writer.WriteULong(message.TeacherUID);
            writer.WriteString(message.Notify ?? "");
            writer.WriteString(message.CardId ?? "");
            // 快照条目按老师逐条交错写（uid+点数+触发掩码+buff掩码+卡表成对），读写顺序一一对应
            int n = message.Uids?.Length ?? 0;
            writer.WriteInt(n);
            for (int i = 0; i < n; i++)
            {
                writer.WriteLong(message.Uids[i]);
                writer.WriteInt(i < message.Points.Length ? message.Points[i] : 0);
                writer.WriteInt(i < message.Masks.Length ? message.Masks[i] : 0);
                writer.WriteInt(i < message.Buffs.Length ? message.Buffs[i] : 0);
                writer.WriteString(i < message.CardLists.Length ? (message.CardLists[i] ?? "") : "");
            }
        }

        internal static TeacherRewardMessage ReadReward(NetworkReader reader)
        {
            var message = new TeacherRewardMessage
            {
                Kind = reader.ReadByte(),
                Version = reader.ReadUInt(),
                TeacherUID = reader.ReadULong(),
                Notify = reader.ReadString(),
                CardId = reader.ReadString(),
            };
            int n = reader.ReadInt();
            var uids = new long[n];
            var points = new int[n];
            var masks = new int[n];
            var buffs = new int[n];
            var cardLists = new string[n];
            for (int i = 0; i < n; i++)
            {
                uids[i] = reader.ReadLong();
                points[i] = reader.ReadInt();
                masks[i] = reader.ReadInt();
                buffs[i] = reader.ReadInt();
                cardLists[i] = reader.ReadString();
            }
            message.Uids = uids;
            message.Points = points;
            message.Masks = masks;
            message.Buffs = buffs;
            message.CardLists = cardLists;
            return message;
        }
    }
}
