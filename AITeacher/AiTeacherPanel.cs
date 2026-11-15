using Rewired;
using UnityEngine;

namespace AITeacher
{
    /// <summary>
    /// F8 配置面板：设置下局 AI 老师数量与形态（仅房主可改，改动写回 aiteacher.json）。
    /// OnGUI 按 AGENTS.md 约定：外层无样式 BeginArea + 内层 BeginVertical(GUI.skin.box)，高度自适应内容。
    /// </summary>
    public sealed class AiTeacherPanel : MonoBehaviour
    {
        private static bool visible;

        private void Update()
        {
            if (!ReInput.isReady) return;
            Keyboard keyboard = ReInput.players.GetPlayer(0).controllers.Keyboard;
            if (keyboard == null) return;
            if (keyboard.GetKeyDown(KeyCode.F8))
            {
                visible = !visible;
            }
        }

        private void OnGUI()
        {
            if (!visible) return;
            Draw();
        }

        private static void Draw()
        {
            bool inRoom = UnitManager.IsSingletonActive() && ClassroomManager.IsSingletonActive();
            bool host = NetRuntime.IsServer;

            int rows = inRoom ? 7 : 3;
            var area = new Rect(12f, Screen.height - (40f + 24f * rows) - 12f, 340f, 40f + 24f * rows);
            GUI.Box(area, "");
            GUILayout.BeginArea(new Rect(area.x + 8f, area.y + 8f, area.width - 16f, area.height - 16f));
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.Label("AI 老师设置（F8 关闭）");

            if (!inRoom)
            {
                GUILayout.Label("进入房间后可查看房间规则与玩家数。");
                GUILayout.EndVertical();
                GUILayout.EndArea();
                return;
            }

            int ruleCount = SingletonT<RoomRuleDataManager>.Instance.GetCurrentIntValue(ERoomRuleId.ClassTeacherCount);
            int aiSetting = AiTeacherManager.PendingAiCount;
            int playerCount = UnitManager.Instance.playerList.Count;

            if (host)
            {
                // 难度行：一键应用预设（视野/听距/反应/移速/散布整套）
                GUILayout.BeginHorizontal();
                GUILayout.Label("难度", GUILayout.Width(64f));
                DrawDifficultyButton(0, "宽松");
                DrawDifficultyButton(1, "标准");
                DrawDifficultyButton(2, "严格");
                DrawDifficultyButton(3, "地狱");
                GUILayout.EndHorizontal();

                // AI 数量行：[-] N [+]
                GUILayout.BeginHorizontal();
                GUILayout.Label("AI 老师数量：" + aiSetting + " / 规则上限 " + ruleCount, GUILayout.Width(230f));
                if (GUILayout.Button("-", GUILayout.Width(32f)) && aiSetting > 0)
                {
                    SetAiCount(aiSetting - 1, ruleCount);
                }
                if (GUILayout.Button("+", GUILayout.Width(32f)) && aiSetting < ruleCount)
                {
                    SetAiCount(aiSetting + 1, ruleCount);
                }
                GUILayout.EndHorizontal();

                // 形态行
                GUILayout.BeginHorizontal();
                GUILayout.Label("形态", GUILayout.Width(64f));
                DrawFormButton(0, "随机");
                DrawFormButton(1001, "粉笔");
                DrawFormButton(1002, "剑");
                GUILayout.EndHorizontal();

                GUILayout.Label("玩家数 " + playerCount + "（AI 与真人老师共存，总数不超规则）");
                GUILayout.Label("改动立即写盘，难度/数量下局按铃生效。");
            }
            else
            {
                GUILayout.Label("仅房主可修改 AI 老师设置。");
                GUILayout.Label("房主设定：AI 老师 " + aiSetting + " / 规则老师数 " + ruleCount);
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        private static void DrawDifficultyButton(int level, string label)
        {
            var config = ModEntry.Config;
            bool selected = config != null && config.difficulty == level;
            string text = selected ? "[" + label + "]" : label;
            if (GUILayout.Button(text, GUILayout.Width(60f)) && config != null)
            {
                config.ApplyDifficulty(level);
                config.Save(ModEntry.ConfigDirectory);
                ModEntry.Log?.Invoke("[状态] 面板设置 AI 难度：" + AiTeacherConfig.PresetName(level));
            }
        }

        private static void DrawFormButton(int formId, string label)
        {
            var config = ModEntry.Config;
            bool selected = config != null && config.teacherForm == formId;
            string text = selected ? "[ " + label + " ]" : label;
            if (GUILayout.Button(text, GUILayout.Width(72f)) && config != null)
            {
                config.teacherForm = formId;
                config.Save(ModEntry.ConfigDirectory);
                ModEntry.Log?.Invoke("[状态] 面板设置 AI 老师形态：" + ModEntry.FormName(formId));
            }
        }

        private static void SetAiCount(int value, int ruleCount)
        {
            var config = ModEntry.Config;
            if (config == null) return;
            config.defaultAiCount = Mathf.Clamp(value, 0, ruleCount);
            config.Save(ModEntry.ConfigDirectory);
            ModEntry.Log?.Invoke("[状态] 面板设置下局 AI 老师数量：" + config.defaultAiCount);
        }
    }
}
