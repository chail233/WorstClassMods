using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TrickMole
{
    // 角色配置页内容：挂在游戏设置窗口（UISettingView）的「课堂角色」页里（TrickMoleGameSettings 负责嵌入）。
    // 素材（窗框图/按钮图/字体/墨色）从窗口池里的设置窗口实例抽取；数值编辑仅课程外开放
    // （Prepare/Classing 锁定，改动下节课生效）；每台机器编辑各自的配置，联机以房主配置为准。
    internal static class TrickMolePanel
    {
        private static Button selectorPrev, selectorNext;
        private static Text selectorLabel;
        private static Button addBtn, removeBtn, resetBtn;
        private static InputField nameInput;
        private static Text noteText;

        private static Sprite bgSprite;
        private static GameObject bgNode;
        private static Sprite btnSprite;
        private static Font font;
        private static int baseFontSize = 24;
        private static Color vanillaInk = new Color(0.12f, 0.11f, 0.10f, 1f); // 从设置窗口文字里抠出来的墨色
        private static Color bodyInk = Color.white;                            // 当前面板正文色（换宿主时设置）
        private static bool lightBg = true;                                    // 当前宿主是不是浅色底（决定数值强调色）

        // ===== 编辑门槛：课程进行中锁定（角色分配在上课瞬间定型，中途改配置容易误解）=====

        internal static bool Editable => !ModEntry.ClassInProgress();

        // ===== 素材抽取（照搬生化模式面板的启发式）=====

        private static void LocateAssets()
        {
            if (bgSprite != null && btnSprite != null && font != null) return;

            UIWindow window = FindWindow();
            if (window != null)
            {
                Canvas rootCanvas = window.GetComponent<Canvas>();
                if (rootCanvas == null) rootCanvas = window.GetComponentInParent<Canvas>();
                if (rootCanvas != null) rootCanvas = rootCanvas.rootCanvas;
                Rect screen = rootCanvas != null && rootCanvas.GetComponent<RectTransform>() != null
                    ? rootCanvas.GetComponent<RectTransform>().rect
                    : new Rect(0f, 0f, 1920f, 1080f);

                Image bestBg = null;
                float bestScore = 0f;
                foreach (Image img in window.GetComponentsInChildren<Image>(true))
                {
                    Sprite s = img.sprite;
                    if (s == null) continue;
                    Rect rect = img.rectTransform.rect;
                    float w = Mathf.Abs(rect.width), h = Mathf.Abs(rect.height);
                    if (w > screen.width * 0.85f && h > screen.height * 0.85f) continue; // 全屏遮罩不是窗框
                    if (w < 200f || h < 150f) continue;
                    bool sliced = s.border.x + s.border.y + s.border.z + s.border.w > 0.5f;
                    string n = img.gameObject.name.ToLowerInvariant();
                    bool nameHit = n.Contains("bg") || n.Contains("frame") || n.Contains("panel")
                        || n.Contains("window") || n.Contains("box") || n.Contains("kuang");
                    float score = w * h * (sliced ? 2f : 0.2f) * (nameHit ? 1.5f : 1f);
                    if (score > bestScore) { bestScore = score; bestBg = img; }
                }
                if (bestBg != null)
                {
                    bgSprite = bestBg.sprite;
                    bgNode = bestBg.gameObject;
                }

                foreach (Button btn in window.GetComponentsInChildren<Button>(true))
                {
                    Image graphic = btn.targetGraphic as Image;
                    if (graphic == null || graphic.sprite == null) continue;
                    Rect rect = graphic.rectTransform.rect;
                    if (Mathf.Abs(rect.width) > screen.width * 0.85f && Mathf.Abs(rect.height) > screen.height * 0.85f) continue;
                    bool hasText = btn.GetComponentInChildren<Text>(true) != null;
                    if (hasText) { btnSprite = graphic.sprite; break; }
                    if (btnSprite == null) btnSprite = graphic.sprite;
                }

                foreach (Text t in window.GetComponentsInChildren<Text>(true))
                {
                    if (t.font == null) continue;
                    if (font == null)
                    {
                        font = t.font;
                        if (t.fontSize > 0) baseFontSize = Mathf.Clamp(t.fontSize, 18, 32);
                    }
                    Color c = t.color;
                    if (c.a > 0.5f && (c.r + c.g + c.b) * 0.333f < 0.4f) vanillaInk = c;
                }
            }

            if (font == null)
            {
                foreach (Text t in Resources.FindObjectsOfTypeAll<Text>())
                {
                    if (t.font == null) continue;
                    font = t.font;
                    baseFontSize = t.fontSize > 0 ? Mathf.Clamp(t.fontSize, 18, 32) : 24;
                    break;
                }
            }
            if (btnSprite == null) btnSprite = bgSprite;
        }

        // 场景实例（含窗口池里未激活的）优先于预制体资源，设置窗口名最优先
        private static UIWindow FindWindow()
        {
            UIWindow settingScene = null, anyScene = null, settingPrefab = null;
            foreach (UIWindow w in Resources.FindObjectsOfTypeAll<UIWindow>())
            {
                if (w == null || w.gameObject == null) continue;
                if (w.gameObject.scene.IsValid())
                {
                    if (w is UISettingView) { return w; }
                    if (anyScene == null) anyScene = w;
                }
                else if (w is UISettingView && settingPrefab == null) settingPrefab = w;
            }
            return settingScene ?? anyScene ?? settingPrefab;
        }

        // ===== 页内容（挂在游戏设置窗口的「课堂角色」面板上）=====

        private static int selIndex;

        private static RoleDef Sel =>
            selIndex >= 0 && selIndex < RoleConfig.Roles.Count ? RoleConfig.Roles[selIndex] : null;

        private static bool IsBuiltin(RoleDef role) =>
            role != null && (role.id == "mole" || role.id == "nerd" || role.id == "athlete");

        private sealed class NumField
        {
            public string Label;
            public float Step, Min, Max;
            public bool Int;
            public Func<RoleDef, float> Get;
            public Action<RoleDef, float> Set;
            public Button MinusBtn, PlusBtn;
            public Text ValueText;
        }

        private static readonly NumField[] NumFields =
        {
            new NumField { Label="名额数(0=不出)", Step=1, Min=0, Max=8, Int=true,
                Get=r=>r.count, Set=(r,v)=>r.count=Mathf.RoundToInt(v) },
            new NumField { Label="捣蛋计分倍数", Step=0.25f, Min=0.25f, Max=5f,
                Get=r=>r.trickMultiplier, Set=(r,v)=>r.trickMultiplier=v },
            new NumField { Label="挂柱扣比例", Step=0.05f, Min=0f, Max=1f,
                Get=r=>r.progressPenaltyFraction, Set=(r,v)=>r.progressPenaltyFraction=v },
            new NumField { Label="广播免计分(秒)", Step=0.5f, Min=0f, Max=10f,
                Get=r=>r.scoreGraceSeconds, Set=(r,v)=>r.scoreGraceSeconds=v },
            new NumField { Label="广播冷却(秒)", Step=1f, Min=0f, Max=120f,
                Get=r=>r.musicCooldownSeconds, Set=(r,v)=>r.musicCooldownSeconds=v },
            new NumField { Label="推倒时长(秒)", Step=0.5f, Min=0f, Max=10f,
                Get=r=>r.pushStunSeconds, Set=(r,v)=>r.pushStunSeconds=v },
            new NumField { Label="推倒冷却(秒)", Step=1f, Min=0f, Max=120f,
                Get=r=>r.pushCooldownSeconds, Set=(r,v)=>r.pushCooldownSeconds=v },
            new NumField { Label="推倒距离(米)", Step=0.1f, Min=0.5f, Max=5f,
                Get=r=>r.pushRange, Set=(r,v)=>r.pushRange=v },
            new NumField { Label="背后判定(扇区)", Step=0.05f, Min=-0.99f, Max=0f,
                Get=r=>r.pushBehindDot, Set=(r,v)=>r.pushBehindDot=v },
            new NumField { Label="静心免伤(秒)", Step=1f, Min=0f, Max=120f,
                Get=r=>r.immunityIdleSeconds, Set=(r,v)=>r.immunityIdleSeconds=v },
            new NumField { Label="免伤次数上限", Step=1f, Min=0f, Max=3f, Int=true,
                Get=r=>r.maxImmunityCharges, Set=(r,v)=>r.maxImmunityCharges=Mathf.RoundToInt(v) },
            new NumField { Label="视野外移速×", Step=0.05f, Min=0.5f, Max=3f,
                Get=r=>r.speedBoostUnseen, Set=(r,v)=>r.speedBoostUnseen=v },
            new NumField { Label="视野内移速×", Step=0.05f, Min=0.1f, Max=1f,
                Get=r=>r.speedPenaltySeen, Set=(r,v)=>r.speedPenaltySeen=v },
            new NumField { Label="行动加速×", Step=0.05f, Min=0.5f, Max=3f,
                Get=r=>r.actionSpeedMultiplier, Set=(r,v)=>r.actionSpeedMultiplier=v },
            new NumField { Label="视锥角度(度,0=110)", Step=5f, Min=0f, Max=360f, Int=true,
                Get=r=>r.sightAngleDegrees, Set=(r,v)=>r.sightAngleDegrees=Mathf.RoundToInt(v) },
            new NumField { Label="视锥距离(米,0=10)", Step=0.5f, Min=0f, Max=30f,
                Get=r=>r.sightDistance, Set=(r,v)=>r.sightDistance=v },
        };

        private static Button toggleBroadcast, togglePush;
        private static Text toggleBroadcastLabel, togglePushLabel;

        // 整页构建：角色选择行 + 名称行 + 步进器网格（9 行两列）+ 提示行
        private static void BuildPage(Transform parent, float w, float h)
        {
            float pad = 8f;
            float rowH = 34f;

            // 角色选择行：‹ 角色 1/3：混的入 ›  [添加] [删除] [恢复默认]
            float selBtnW = 34f;
            float smallW = 92f;
            float selW = w - pad * 2f - selBtnW * 2f - smallW * 3f - 18f;
            selectorPrev = NewButton(parent, "上一个", "‹", new Color(0.3f, 0.32f, 0.4f, 1f), new Vector2(selBtnW, rowH), () => ShiftRole(-1));
            Place(selectorPrev.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(selBtnW, rowH), new Vector2(pad, 0f));
            selectorLabel = NewText(parent, "角色指示", "", Mathf.Max(16, baseFontSize - 2), TextAnchor.MiddleCenter, bodyInk);
            Place(selectorLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(selW, rowH), new Vector2(pad + selBtnW + 4f, 0f));
            selectorNext = NewButton(parent, "下一个", "›", new Color(0.3f, 0.32f, 0.4f, 1f), new Vector2(selBtnW, rowH), () => ShiftRole(1));
            Place(selectorNext.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(selBtnW, rowH), new Vector2(pad + selBtnW + selW + 8f, 0f));
            float btnRowX = pad + selBtnW * 2f + selW + 12f;
            addBtn = NewButton(parent, "添加", "添加", new Color(0.25f, 0.42f, 0.28f, 1f), new Vector2(smallW, rowH), AddRole);
            Place(addBtn.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(smallW, rowH), new Vector2(btnRowX, 0f));
            removeBtn = NewButton(parent, "删除", "删除", new Color(0.55f, 0.3f, 0.22f, 1f), new Vector2(smallW, rowH), RemoveRole);
            Place(removeBtn.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(smallW, rowH), new Vector2(btnRowX + smallW + 6f, 0f));
            resetBtn = NewButton(parent, "恢复", "恢复默认", new Color(0.35f, 0.3f, 0.45f, 1f), new Vector2(smallW, rowH), ResetRoles);
            Place(resetBtn.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(smallW, rowH), new Vector2(btnRowX + (smallW + 6f) * 2f, 0f));

            // 名称行
            float nameY = -(rowH + 6f);
            Text nameLabel = NewText(parent, "名称标签", "角色名", Mathf.Max(15, baseFontSize - 4), TextAnchor.MiddleLeft, bodyInk);
            Place(nameLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(72f, rowH - 4f), new Vector2(pad, nameY));
            nameInput = NewInputField(parent, "名称输入", 160f, rowH - 6f, new Vector2(pad + 76f, nameY));
            Text nameHint = NewText(parent, "名称提示", "（联机时以房主配置为准）", Mathf.Max(13, baseFontSize - 7), TextAnchor.MiddleLeft,
                lightBg ? new Color(0.45f, 0.42f, 0.38f, 1f) : new Color(0.75f, 0.75f, 0.75f, 1f));
            Place(nameHint.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(240f, rowH - 4f), new Vector2(pad + 76f + 168f, nameY));

            // 步进器网格：16 项 = 8 行 × 2 列
            float gridTop = (rowH + 6f) * 2f;
            float noteH = 58f;
            float gridH = Mathf.Max(180f, h - gridTop - noteH);
            var gridGo = new GameObject("数值网格", typeof(RectTransform));
            gridGo.transform.SetParent(parent, false);
            Place(gridGo.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(w - pad * 2f, gridH), new Vector2(pad, -gridTop));
            BuildGrid(gridGo.transform, w - pad * 2f, gridH, rowH);

            // 提示行
            noteText = NewText(parent, "提示", "", Mathf.Max(13, baseFontSize - 6), TextAnchor.UpperLeft, bodyInk);
            Place(noteText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(w - pad * 2f, noteH), new Vector2(pad, -gridTop - gridH - 4f));
            noteText.verticalOverflow = VerticalWrapMode.Overflow;
        }

        private static void BuildGrid(Transform parent, float innerW, float bodyH, float rowH)
        {
            rowH = Mathf.Clamp(bodyH / 9f, 26f, 36f);
            float cellW = (innerW - 12f) / 2f;
            int labelSize = Mathf.Max(14, baseFontSize - 5);
            for (int i = 0; i < NumFields.Length; i++)
            {
                NumField f = NumFields[i];
                int col = i / 8, row = i % 8;
                var cell = new GameObject("数值_" + f.Label, typeof(RectTransform));
                cell.transform.SetParent(parent, false);
                float x = -innerW / 2f + col * (cellW + 12f);
                float y = -(row * rowH) - 4f;
                Place(cell.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(cellW, rowH - 6f), new Vector2(x, y));

                Text label = NewText(cell.transform, "标签", f.Label, labelSize, TextAnchor.MiddleLeft, bodyInk);
                Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(Mathf.Max(60f, cellW - 116f), rowH - 6f), Vector2.zero);

                f.MinusBtn = NewButton(cell.transform, "减", "−", new Color(0.45f, 0.3f, 0.2f, 1f), new Vector2(24f, rowH - 8f),
                    () => Adjust(f, -f.Step));
                Place(f.MinusBtn.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, rowH - 8f), new Vector2(cellW - 108f, 0f));

                f.ValueText = NewText(cell.transform, "值", "", labelSize, TextAnchor.MiddleCenter,
                    lightBg ? new Color(0.55f, 0.36f, 0.05f, 1f) : new Color(1f, 0.92f, 0.6f, 1f));
                Place(f.ValueText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(48f, rowH - 6f), new Vector2(cellW - 80f, 0f));

                f.PlusBtn = NewButton(cell.transform, "加", "+", new Color(0.25f, 0.42f, 0.28f, 1f), new Vector2(24f, rowH - 8f),
                    () => Adjust(f, f.Step));
                Place(f.PlusBtn.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, rowH - 8f), new Vector2(cellW - 28f, 0f));
            }

            // 第 9 行：两个技能开关
            float toggleY = -(8 * rowH) - 4f;
            float toggleW = cellW;
            toggleBroadcast = NewButton(parent, "开关_广播", "", new Color(0.2f, 0.38f, 0.62f, 1f), new Vector2(toggleW, rowH - 8f),
                () => FlipToggle(r => r.canBroadcast, (r, v) => r.canBroadcast = v));
            Place(toggleBroadcast.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(toggleW, rowH - 8f), new Vector2(-innerW / 2f, toggleY));
            toggleBroadcastLabel = toggleBroadcast.GetComponentInChildren<Text>();
            togglePush = NewButton(parent, "开关_推倒", "", new Color(0.2f, 0.38f, 0.62f, 1f), new Vector2(toggleW, rowH - 8f),
                () => FlipToggle(r => r.canPush, (r, v) => r.canPush = v));
            Place(togglePush.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(toggleW, rowH - 8f), new Vector2(-innerW / 2f + cellW + 12f, toggleY));
            togglePushLabel = togglePush.GetComponentInChildren<Text>();
        }

        private static void Adjust(NumField f, float delta)
        {
            RoleDef role = Sel;
            if (role == null || !Editable) return;
            float v = f.Get(role) + delta;
            v = Mathf.Round(v * 100f) / 100f;
            f.Set(role, Mathf.Clamp(v, f.Min, f.Max));
            RoleConfig.Save(ModEntry.Log);
        }

        private static void FlipToggle(Func<RoleDef, bool> get, Action<RoleDef, bool> set)
        {
            RoleDef role = Sel;
            if (role == null || !Editable) return;
            set(role, !get(role));
            RoleConfig.Save(ModEntry.Log);
        }

        private static void ShiftRole(int delta)
        {
            int n = RoleConfig.Roles.Count;
            if (n == 0) return;
            selIndex = (selIndex + delta % n + n) % n;
            RefreshPanelValues();
        }

        private static void AddRole()
        {
            if (!Editable) return;
            int n = RoleConfig.Roles.Count + 1;
            string id = "custom" + n;
            while (RoleConfig.Roles.Exists(r => r.id == id))
            {
                n++;
                id = "custom" + n;
            }
            var role = new RoleDef
            {
                id = id,
                name = "自定义角色" + n,
                count = 1,
                trickMultiplier = 1f,
                canBroadcast = false,
                canPush = false,
            };
            RoleConfig.Roles.Add(role);
            selIndex = RoleConfig.Roles.Count - 1;
            RoleConfig.Save(ModEntry.Log);
            ModEntry.Log("[状态] 新增角色「" + role.name + "」（" + role.id + "）");
            RefreshPanelValues();
        }

        private static void RemoveRole()
        {
            RoleDef role = Sel;
            if (role == null || !Editable) return;
            if (IsBuiltin(role))
            {
                ModEntry.Log("[状态] 内置角色不能删除，可把名额数设为 0 禁用");
                return;
            }
            if (RoleConfig.Roles.Count <= 1) return;
            RoleConfig.Roles.RemoveAt(selIndex);
            selIndex = Mathf.Clamp(selIndex, 0, RoleConfig.Roles.Count - 1);
            RoleConfig.Save(ModEntry.Log);
            ModEntry.Log("[状态] 已删除角色（" + role.id + "）");
            RefreshPanelValues();
        }

        private static void ResetRoles()
        {
            if (!Editable) return;
            RoleConfig.ResetToDefaults(ModEntry.Log);
            selIndex = 0;
            RefreshPanelValues();
        }

        // 面板数值/开关/提示刷新。
        // Unity 的 Text.text 同值写入不会触发重排，每帧调用无害
        internal static void RefreshPanelValues()
        {
            int n = RoleConfig.Roles.Count;
            if (selIndex >= n) selIndex = Mathf.Max(0, n - 1);
            RoleDef role = Sel;

            bool editable = Editable;
            if (selectorLabel != null)
                selectorLabel.text = role != null
                    ? "角色 " + (selIndex + 1) + "/" + n + "：" + role.name + (IsBuiltin(role) ? "（内置）" : "")
                    : "暂无角色";
            if (addBtn != null) addBtn.interactable = editable;
            if (removeBtn != null) removeBtn.interactable = editable && role != null && !IsBuiltin(role) && n > 1;
            if (resetBtn != null) resetBtn.interactable = editable;
            if (selectorPrev != null) selectorPrev.interactable = n > 1;
            if (selectorNext != null) selectorNext.interactable = n > 1;

            if (nameInput != null && role != null && !nameInput.isFocused && nameInput.text != role.name)
                nameInput.text = role.name;
            if (nameInput != null) nameInput.interactable = editable && role != null;

            foreach (NumField f in NumFields)
            {
                float v = role != null ? f.Get(role) : 0f;
                if (f.ValueText != null)
                    f.ValueText.text = f.Int ? v.ToString("0") : v.ToString("0.##");
                if (f.MinusBtn != null) f.MinusBtn.interactable = editable && role != null;
                if (f.PlusBtn != null) f.PlusBtn.interactable = editable && role != null;
            }

            if (toggleBroadcastLabel != null)
                toggleBroadcastLabel.text = "广播技能（按 B）：" + (role != null && role.canBroadcast ? "开" : "关");
            if (togglePushLabel != null)
                togglePushLabel.text = "推倒技能（按 E）：" + (role != null && role.canPush ? "开" : "关");
            if (toggleBroadcast != null) toggleBroadcast.interactable = editable && role != null;
            if (togglePush != null) togglePush.interactable = editable && role != null;

            if (noteText != null)
            {
                noteText.text = (editable
                    ? "修改的是本机角色配置，改动下节课生效；联机时角色分配以房主配置为准。"
                    : "课程进行中，角色配置已锁定（课前或下课后可更改）。")
                    + "\n本机配置文件：LocalLow/Configs/trickmole/trickmole.json（内置角色缺失自动补全）";
            }
        }

        // ===== 嵌入游戏设置窗口的页内容 =====

        internal static void BuildEmbeddedPage(Transform parent, float w, float h)
        {
            LocateAssets(); // 首次嵌入时缓存字体/按钮图/墨色
            lightBg = true;               // 嵌入的是原版设置窗（浅色底）
            bodyInk = vanillaInk;
            float pad = 16f;
            float innerW = Mathf.Max(320f, w - pad * 2f);
            var pageGo = new GameObject("角色配置页", typeof(RectTransform));
            pageGo.transform.SetParent(parent, false);
            Place(pageGo.GetComponent<RectTransform>(), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(innerW, Mathf.Max(320f, h - 12f)), new Vector2(pad, 0f));
            BuildPage(pageGo.transform, innerW, Mathf.Max(320f, h - 12f));
            ModEntry.Log("[诊断] 嵌入页样式：字体=" + (font != null ? font.name : "null")
                + " 墨色=" + bodyInk + " 字号=" + baseFontSize);
        }

        // ===== 控件辅助（照搬生化模式面板）=====

        private static Button NewButton(Transform parent, string name, string label, Color fallbackColor, Vector2 size, Action onClick)
        {
            Image img = NewImage(parent, name, btnSprite, fallbackColor);
            Place(img.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), size, Vector2.zero);
            Button btn = img.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.ColorTint;
            Text t = NewText(img.transform, "文字", label, Mathf.Max(15, baseFontSize - 3), TextAnchor.MiddleCenter, bodyInk);
            Stretch(t.rectTransform);
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            return btn;
        }

        private static InputField NewInputField(Transform parent, string name, float w, float h, Vector2 pos)
        {
            Image bg = NewImage(parent, name, btnSprite, new Color(1f, 1f, 1f, 0.85f));
            Place(bg.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(w, h), pos);
            Text value = NewText(bg.transform, "文字", "", Mathf.Max(15, baseFontSize - 3), TextAnchor.MiddleLeft,
                lightBg ? new Color(0.1f, 0.1f, 0.1f, 1f) : Color.white);
            Stretch(value.rectTransform);
            value.rectTransform.offsetMin = new Vector2(8f, 0f);
            value.rectTransform.offsetMax = new Vector2(-8f, 0f);
            InputField input = bg.gameObject.AddComponent<InputField>();
            input.textComponent = value;
            input.targetGraphic = bg;
            input.transition = Selectable.Transition.ColorTint;
            input.lineType = InputField.LineType.SingleLine;
            input.onEndEdit.AddListener(s =>
            {
                RoleDef role = Sel;
                if (role == null) return;
                string t = (s ?? "").Trim();
                if (t.Length == 0) { input.text = role.name; return; }
                if (t == role.name) return;
                role.name = t;
                RoleConfig.Save(ModEntry.Log);
            });
            return input;
        }

        private static Image NewImage(Transform parent, string name, Sprite sprite, Color fallbackColor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            Image img = go.GetComponent<Image>();
            if (sprite != null)
            {
                img.sprite = sprite;
                float border = sprite.border.x + sprite.border.y + sprite.border.z + sprite.border.w;
                img.type = border > 0f ? Image.Type.Sliced : Image.Type.Simple;
            }
            else
            {
                img.color = fallbackColor;
            }
            return img;
        }

        private static Text NewText(Transform parent, string name, string content, int size, TextAnchor anchor, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            Text text = go.GetComponent<Text>();
            text.font = font != null ? font : BuiltinFont();
            text.fontSize = size;
            text.alignment = anchor;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            text.text = content;
            return text;
        }

        private static Font BuiltinFont()
        {
            try { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch { return null; }
        }

        private static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 size, Vector2 pos)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
