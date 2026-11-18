using Mirror;
using Rewired;
using UnityEngine;

namespace BioChem
{
    public sealed class BioChemPump : MonoBehaviour
    {
        private void Update()
        {
            ModEntry.Tick();
            BioChemGameSettings.Tick(); // 嵌入游戏设置窗口：开窗轮询跳页 + 场景实例扫描 + 数值刷新
            BioChemPanel.Refresh();
        }

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
            {
                DrawBars();
                HunterFeedback.DrawOverlay(); // 猎刀命中标记/伤害浮字（攻击者本机，空状态零开销早退）
            }
        }

        private void OnDestroy() { ModEntry.Cleanup(); }

        // ===== 右下角信息盒：身份 + HP/体力条 + 全部状态提示（往左偏移，避开右下角交互按钮区） =====

        private static GUIStyle barTextStyle;

        private static GUIStyle BarTextStyle
        {
            get
            {
                if (barTextStyle == null)
                {
                    barTextStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 12 };
                    barTextStyle.normal.textColor = Color.white;
                }
                return barTextStyle;
            }
        }

        private static void DrawBars()
        {
            if (!ClassroomManager.IsSingletonActive() || !UnitManager.IsSingletonActive()) return;
            var state = ClassroomManager.Instance.curState;
            if (state != EClassroomState.Prepare && state != EClassroomState.Classing) return;
            Unit self = UnitManager.Instance.GetUnitByUID(UnitManager.Instance.localPlayerUID);
            if (self == null || !self.isActived) return;

            bool big = ModEntry.LocalIsBigTeacher;
            bool mini = ModEntry.LocalIsInfected;
            bool hunter = ModEntry.LocalIsHunter;
            string identity;
            int bars = 0;
            if (hunter) { identity = "身份：老师猎手"; bars = 1; }
            else if (big) { identity = "身份：老师"; bars = 1; }
            else if (mini) { identity = "身份：小老师"; bars = 2; }
            else if (self.IsStudent) { identity = "身份：学生"; }
            else return; // 观战等非师生状态不画

            var lines = new System.Collections.Generic.List<string>();
            if (hunter)
            {
                lines.Add("猎刀：左键挥砍面前扇形内的老师（伤害 " + ModEntry.Config.hunterDamage + "，间隔 " + ModEntry.Config.hunterAttackInterval.ToString("0.#") + "s，可群伤/空挥）");
            }
            else if (mini)
            {
                if (ModEntry.StunCooldownRemain > 0f)
                    lines.Add("击晕冷却中…" + Mathf.CeilToInt(ModEntry.StunCooldownRemain) + "s");
                else if (ModEntry.SelfStamina() < ModEntry.Config.staminaCostPerStun)
                    lines.Add("体力不足，无法击晕（需 " + ModEntry.Config.staminaCostPerStun.ToString("0.#") + "）");
                else
                    lines.Add("按 F 击晕身边的学生（耗体力 " + ModEntry.Config.staminaCostPerStun.ToString("0.#") + "）");
                lines.Add("靠近被眩晕的学生按交互键抓取，扛到罚站柱挂上（携带超时会自动放下）");
            }
            else if (big)
            {
                if (ModEntry.SelfSlowStacks > 0) lines.Add("被吹箭减速 ×" + ModEntry.SelfSlowStacks + "（移速 -" + (ModEntry.SelfSlowStacks * 20) + "%）");
            }
            else if (state == EClassroomState.Classing)
            {
                if (ModEntry.HunterUnlocked)
                    lines.Add("按 G 变身老师猎手！");
                // 备弹：耗尽时提醒吃东西补弹（服务端拒发，连发循环也会停）
                if (ModEntry.SelfDartAmmo <= 0)
                    lines.Add("备弹耗尽！吃东西补充弹药");
                else
                    lines.Add("备弹 " + ModEntry.SelfDartAmmo + "/" + ModEntry.Config.dartMaxAmmo + "（吃东西补充）");
                Unit selfUnit = UnitManager.IsSingletonActive() ? UnitManager.Instance.GetUnitByUID(UnitManager.Instance.localPlayerUID) : null;
                // 嘴部叼的是吹箭才提示操作；被气球/泡泡糖占用时不误导（按 1/2 会被服务端拒绝）
                Unit mouthItem = selfUnit != null && selfUnit.isActived
                    ? selfUnit.GetComponent<MouthItemUseComponent>()?.currentMouthItem : null;
                if (mouthItem != null && mouthItem.UnitSubType == EUnitSubType.Taser)
                    lines.Add("连发吹箭筒：右键瞄准、按住左键连发（按 1 收起）");
                else if (mouthItem == null)
                    lines.Add("按 1 掏出连发吹箭筒");
            }
            // 吹箭伤害随小老师人数递增：所有身份上课期间都显示当前有效伤害与感染加成（房主把加伤设 0 时只显示基础伤害）
            if (state == EClassroomState.Classing)
            {
                float bonus = ModEntry.DartDamageBonus();
                lines.Add(bonus > 0f
                    ? "吹箭伤害 " + ModEntry.EffectiveDartDamage() + "（基础 " + ModEntry.Config.dartDamage + "，感染加成 +" + (bonus * 100f).ToString("0") + "%）"
                    : "吹箭伤害 " + ModEntry.Config.dartDamage);
            }
            if (ModEntry.infected.Count > 0)
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (ulong uid in ModEntry.infected) names.Add(ModEntry.GetName(uid));
                lines.Add("小老师：" + string.Join("、", names));
            }

            const float width = 220f, barHeight = 16f, gap = 4f, topPad = 4f, identityHeight = 20f, lineHeight = 18f, bottomPad = 6f;
            float textWidth = 0f;
            foreach (string line in lines)
                textWidth = Mathf.Max(textWidth, BarTextStyle.CalcSize(new GUIContent(line)).x);
            float boxWidth = Mathf.Clamp(textWidth + 20f, width + 12f, 460f);
            float height = topPad + identityHeight + gap + bars * barHeight + Mathf.Max(0, bars - 1) * gap + lines.Count * lineHeight + lineHeight + 6f + bottomPad;
            float x = Screen.width - boxWidth - 320f;
            float y = Screen.height - height - 16f;
            GUI.Box(new Rect(x, y, boxWidth, height), GUIContent.none);

            GUI.Label(new Rect(x, y + topPad, boxWidth, identityHeight), identity, BarTextStyle);
            float cy = y + topPad + identityHeight + gap;
            if (hunter)
                DrawBar(x, cy, width, barHeight, ModEntry.GetHunterHp(self.UID), ModEntry.Config.hunterHp, "HP", new Color(0.85f, 0.2f, 0.2f));
            else if (big)
                DrawBar(x, cy, width, barHeight, ModEntry.GetHp(self.UID, true), ModEntry.Config.teacherMaxHp, "HP", new Color(0.85f, 0.2f, 0.2f));
            else if (mini)
            {
                DrawBar(x, cy, width, barHeight, ModEntry.GetHp(self.UID, false), ModEntry.Config.miniMaxHp, "HP", new Color(0.85f, 0.2f, 0.2f));
                cy += barHeight + gap;
                DrawBar(x, cy, width, barHeight, ModEntry.SelfStamina(), ModEntry.Config.staminaMax, "体力", new Color(0.25f, 0.75f, 0.3f));
            }
            cy += bars * barHeight + Mathf.Max(0, bars - 1) * gap;
            foreach (string line in lines)
            {
                GUI.Label(new Rect(x, cy, boxWidth, lineHeight), line, BarTextStyle);
                cy += lineHeight;
            }
            // 面板开关：F7 之外给个可点击入口
            if (GUI.Button(new Rect(x + 6f, cy, boxWidth - 12f, lineHeight + 4f), "管理面板（F7 / 点击）"))
                ModEntry.TogglePanel();
        }

        private static void DrawBar(float x, float y, float width, float height, float value, float max, string label, Color color)
        {
            var back = new Rect(x + 6f, y, width - 12f, height);
            GUI.DrawTexture(back, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(0f, 0f, 0f, 0.6f), 1f, 3f);
            float fraction = max > 0f ? Mathf.Clamp01(value / max) : 0f;
            if (fraction > 0f)
                GUI.DrawTexture(new Rect(back.x + 1f, back.y + 1f, (back.width - 2f) * fraction, back.height - 2f),
                    Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, color, 0f, 2f);
            GUI.Label(new Rect(x, y, width, height), label + " " + value.ToString("0.#") + "/" + max.ToString("0.#"), BarTextStyle);
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
