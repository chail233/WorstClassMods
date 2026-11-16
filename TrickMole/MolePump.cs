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

        // 右上角常驻提示盒：贴在游戏下课倒计时左边（倒计时 UI 取不到时退回右贴边），行数随角色数自适应
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

            // 本地玩家被挂罚站柱 → 技能禁用提示（FSM 状态全端同步；免伤是被动技能，不受此限）
            bool punished = myRole != null
                && (myRole.canBroadcast || myRole.canPush)
                && UnitManager.Instance.GetUnitByUID(UnitManager.Instance.localPlayerUID) is Unit self
                && self.isActived
                && self.CurState() == (int)EUnitFsmStateType.StudentPunished;
            if (punished) lines.Add("被挂罚站中，技能禁用");

            // 只给本地玩家自己的角色追加技能状态；其他人的角色只出现在上面的名单里
            if (myRole != null && state == EClassroomState.Classing)
            {
                if (ModEntry.RoleHasImmunity(myRole))
                {
                    if (ModEntry.MyImmunityCharges > 0) lines.Add("免伤就绪：下一次老师攻击无效（捣蛋即失效）");
                    else lines.Add("静心充能中，还剩 " + Mathf.CeilToInt(ModEntry.MyImmunityRemaining) + " 秒获得免伤");
                }
                if (myRole.speedBoostUnseen > 0f || myRole.speedPenaltySeen > 0f)
                {
                    Unit selfUnit = UnitManager.Instance.GetUnitByUID(UnitManager.Instance.localPlayerUID);
                    if (selfUnit != null && selfUnit.isActived && !selfUnit.IsDead)
                    {
                        if (ModEntry.IsSeenByAnyTeacher(selfUnit, myRole))
                        {
                            if (myRole.speedPenaltySeen > 0f) lines.Add("老师视线内：移速 ×" + myRole.speedPenaltySeen.ToString("0.##"));
                        }
                        else if (myRole.speedBoostUnseen > 0f)
                        {
                            lines.Add("老师视野外：移速 ×" + myRole.speedBoostUnseen.ToString("0.##"));
                        }
                    }
                }
                if (myRole.canBroadcast)
                {
                    if (ModEntry.musicPlaying) lines.Add("广播播放中…再按 " + ModEntry.MusicKey + " 停止");
                    else if (!punished && ModEntry.ClientCooldownRemain > 0f) lines.Add("广播冷却中…");
                    else if (!punished) lines.Add("按 " + ModEntry.MusicKey + " 播放广播（曲库 " + (ModEntry.MusicPool.Length + PmBridge.SharedTrackCount) + " 首）");
                }
                if (myRole.canPush && !punished && ModEntry.PushCooldownRemain > 0f)
                    lines.Add("推倒冷却中… " + Mathf.CeilToInt(ModEntry.PushCooldownRemain) + "s");
            }

            var area = new Rect(Screen.width - 250f, 10f, 250f, 40f + 24f * (lines.Count + 2));
            var battleView = SingletonT<UIManager>.Instance.GetOpenUI(EUIWindowType.BattleMain) as UIBattleMainView;
            if (battleView != null && battleView.countDownContainer is RectTransform countRt && countRt.gameObject.activeInHierarchy)
            {
                var corners = new Vector3[4];
                countRt.GetWorldCorners(corners);
                Canvas canvas = countRt.GetComponentInParent<Canvas>();
                Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                float minX = float.MaxValue;
                foreach (var corner in corners)
                    minX = Mathf.Min(minX, RectTransformUtility.WorldToScreenPoint(cam, corner).x);
                // 倒计时左边留 24px 间隙；窗口过窄放不下就保持右贴边不挪
                if (minX > 260f) area.x = minX - 24f - 250f;
            }
            GUI.Box(area, "");
            GUILayout.BeginArea(new Rect(area.x + 8f, area.y + 8f, area.width - 16f, area.height - 16f));
            GUILayout.BeginVertical(GUI.skin.box);
            foreach (string line in lines) GUILayout.Label(line);
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
            writer.WriteString(message.AssignmentsJson);
        }

        internal static MoleSelectMessage ReadMole(NetworkReader reader)
        {
            return new MoleSelectMessage
            {
                Version = reader.ReadUInt(),
                AssignmentsJson = reader.ReadString()
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
            writer.WriteULong(message.StudentUID);
            writer.WriteULong(message.TeacherUID);
        }

        internal static MolePushMessage ReadPush(NetworkReader reader)
        {
            return new MolePushMessage
            {
                Kind = reader.ReadByte(),
                StudentUID = reader.ReadULong(),
                TeacherUID = reader.ReadULong()
            };
        }

        internal static void WriteImmunity(NetworkWriter writer, MoleImmunityMessage message)
        {
            writer.WriteByte(message.Kind);
            writer.WriteULong(message.StudentUID);
        }

        internal static MoleImmunityMessage ReadImmunity(NetworkReader reader)
        {
            return new MoleImmunityMessage
            {
                Kind = reader.ReadByte(),
                StudentUID = reader.ReadULong()
            };
        }
    }
}
