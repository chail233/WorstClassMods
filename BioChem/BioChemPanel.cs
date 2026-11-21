using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace BioChem
{
    // 管理面板：UGUI 复用游戏设置窗口（UISettingView）的素材——背景框 / 按钮图 / 字体，
    // 关闭过的窗口实例留在窗口池里（SetActive(false) + 回池），运行时随时可从中抽取引用。
    // 单页布局：状态 + 模式开关 + 数值步进器网格（玩法规则长文案已删——超出屏幕）
    internal static class BioChemPanel
    {
        private static GameObject canvasGo;
        private static Text statusText;
        private static Button hostToggle;
        private static Text hostToggleLabel;
        private static GameObject settingsPage;
        private static bool uiRuleActive;

        private static Sprite bgSprite;
        private static GameObject bgNode;
        private static Sprite btnSprite;
        private static Font font;
        private static int baseFontSize = 24;
        private static Color vanillaInk = new Color(0.12f, 0.11f, 0.10f, 1f); // 从设置窗口文字里抠出来的墨色
        private static Color bodyInk = Color.white;                            // 当前面板正文色（换宿主时设置）
        private static bool lightBg = true;                                    // 当前宿主是不是浅色底（决定数值强调色）

        internal static bool IsOpen => canvasGo != null && canvasGo.activeSelf;

        internal static void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        internal static void Open()
        {
            LocateAssets();
            if (canvasGo == null) Build();
            Refresh();
            canvasGo.SetActive(true);
            SetUIInputRule(true);
        }

        internal static void Close()
        {
            if (canvasGo != null)
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es != null && es.currentSelectedGameObject != null
                    && es.currentSelectedGameObject.transform.IsChildOf(canvasGo.transform))
                    es.SetSelectedGameObject(null);
                canvasGo.SetActive(false);
            }
            SetUIInputRule(false);
        }

        internal static void Teardown()
        {
            SetUIInputRule(false);
            if (canvasGo != null) UnityEngine.Object.Destroy(canvasGo);
            canvasGo = null;
        }

        // 嵌入页接管后丢弃自建面板：只藏不拆输入规则——游戏设置窗口自己开着 Rule_UISetting，
        // 我们此时再发 false 会把它的规则关掉。uiRuleActive 归位，下次 Open 时会重新激活
        internal static void HideForEmbedded()
        {
            if (canvasGo != null && canvasGo.activeSelf) canvasGo.SetActive(false);
            uiRuleActive = false;
        }

        // 与 UISettingView.OnActive/OnPassive 同款：面板打开期间冻结玩家输入
        private static void SetUIInputRule(bool on)
        {
            if (uiRuleActive == on) return;
            try
            {
                if (MonoSingletonT<InputManager>.IsSingletonActive())
                {
                    MonoSingletonT<InputManager>.Instance.ChangeRuleSet(EInputRuleSets.RuleSet0, EInputRule.Rule_UISetting, on, false);
                    uiRuleActive = on;
                }
            }
            catch { /* 输入规则失败不影响面板本身 */ }
        }

        // ===== 素材抽取 =====

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
                    // 全屏遮罩/截图底板：占画布 85% 以上，不是窗口框
                    if (w > screen.width * 0.85f && h > screen.height * 0.85f) continue;
                    if (w < 200f || h < 150f) continue;
                    // 9-sliced 图几乎必然是窗框类素材；名字含 bg/frame/panel/window 再加权
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
                    // 设置窗口是浅色底 + 黑字：取正文墨色，我们的内容才不会白字贴白底
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

        // ===== 构建 =====

        private static void Build()
        {
            canvasGo = new GameObject("生化模式面板", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(canvasGo);
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 500; // 与游戏 Menu 层同级，压在 HUD 上
            CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // 遮罩：点空白处关闭
            Image blocker = NewImage(canvasGo.transform, "遮罩", null, new Color(0f, 0f, 0f, 0.55f));
            blocker.rectTransform.anchorMin = Vector2.zero;
            blocker.rectTransform.anchorMax = Vector2.one;
            blocker.rectTransform.offsetMin = Vector2.zero;
            blocker.rectTransform.offsetMax = Vector2.zero;
            Button blockerBtn = blocker.gameObject.AddComponent<Button>();
            blockerBtn.transition = Selectable.Transition.None;
            blockerBtn.onClick.AddListener(Close);

            RectTransform win = null;
            float winH;
            bool cloned = bgNode != null && TryCloneWindowFrame(out win);
            // 克隆到的设置窗框是浅色底：正文改用原版墨色；自建深色底框才用白字
            lightBg = cloned;
            bodyInk = cloned ? vanillaInk : Color.white;
            if (cloned)
            {
                winH = Mathf.Max(600f, win.rect.height);
            }
            else
            {
                Image winImg = NewImage(canvasGo.transform, "窗口", bgSprite, new Color(0.12f, 0.14f, 0.18f, 0.96f));
                win = winImg.rectTransform;
                Place(win, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(640f, 660f), Vector2.zero);
                winH = 660f;

                Text title = NewText(win, "标题", "生化模式 · 管理面板", baseFontSize + 8, TextAnchor.MiddleCenter, bodyInk);
                Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(592f, 52f), new Vector2(0f, -14f));
            }

            float innerW = Mathf.Abs(win.rect.width) - 96f;
            float bodyTopY = -160f; // 玩法规则页签已删：状态行 + 开关下面就是步进器网格
            float bodyH = Mathf.Max(240f, winH - 160f - 62f);

            statusText = NewText(win, "状态", "", baseFontSize, TextAnchor.UpperLeft, bodyInk);
            Place(statusText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(innerW, 56f), new Vector2(-innerW / 2f, -64f));

            hostToggle = NewButton(win, "开关", "", new Color(0.25f, 0.45f, 0.75f, 1f), new Vector2(400f, 44f), () =>
            {
                if (NetRuntime.IsServer) ModEntry.SetEnabled(!ModEntry.enabled);
            });
            Place(hostToggle.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(400f, 44f), new Vector2(0f, -124f));
            hostToggleLabel = hostToggle.GetComponentInChildren<Text>();

            // 步进器网格：单页铺满（玩法规则长文案已删，避免超出屏幕）
            var settingsGo = new GameObject("数值页", typeof(RectTransform));
            settingsGo.transform.SetParent(win, false);
            Place(settingsGo.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(innerW, bodyH), new Vector2(0f, bodyTopY));
            BuildGrid(settingsGo.transform, innerW, bodyH);
            settingsPage = settingsGo;

            Image closeImg = NewImage(win, "关闭", btnSprite, new Color(0.5f, 0.25f, 0.25f, 1f));
            Place(closeImg.rectTransform, new Vector2(0.5f, 0f), new Vector2(1f, 0f), new Vector2(150f, 52f), new Vector2(-24f, 18f));
            Button closeBtn = closeImg.gameObject.AddComponent<Button>();
            Text closeLabel = NewText(closeImg.transform, "文字", "关闭", baseFontSize, TextAnchor.MiddleCenter, bodyInk);
            Stretch(closeLabel.rectTransform);
            closeBtn.onClick.AddListener(Close);
        }

        // ===== 数值步进器 =====

        private sealed class NumField
        {
            public string Label;
            public float Step, Min, Max;
            public bool Int;
            public Func<float> Get;
            public Action<float> Set;
            public Button MinusBtn, PlusBtn;
            public Text ValueText;
        }

        private static readonly NumField[] NumFields =
        {
            new NumField { Label="开局感染人数", Step=1, Min=0, Max=BioChemConfig.MaxInitialInfectedStudents, Int=true,
                Get=()=>ModEntry.Config.initialInfectedStudents, Set=v=>ModEntry.Config.initialInfectedStudents=Mathf.RoundToInt(v) },
            new NumField { Label="挂柱感染(秒)", Step=1, Min=1, Max=600, Int=true,
                Get=()=>ModEntry.Config.pillarInfectSeconds, Set=v=>ModEntry.Config.pillarInfectSeconds=Mathf.RoundToInt(v) },
            new NumField { Label="大老师HP", Step=1, Min=1, Max=999, Int=true,
                Get=()=>ModEntry.Config.teacherMaxHp, Set=v=>ModEntry.Config.teacherMaxHp=Mathf.RoundToInt(v) },
            new NumField { Label="写板书回HP", Step=1, Min=0, Max=99, Int=true,
                Get=()=>ModEntry.Config.teacherHpPerWrite, Set=v=>ModEntry.Config.teacherHpPerWrite=Mathf.RoundToInt(v) },
            new NumField { Label="小老师HP", Step=1, Min=1, Max=999, Int=true,
                Get=()=>ModEntry.Config.miniMaxHp, Set=v=>ModEntry.Config.miniMaxHp=Mathf.RoundToInt(v) },
            new NumField { Label="体力上限", Step=0.5f, Min=1f, Max=999f,
                Get=()=>ModEntry.Config.staminaMax, Set=v=>ModEntry.Config.staminaMax=v },
            new NumField { Label="走路耗体力/秒", Step=0.1f, Min=0f, Max=5f,
                Get=()=>ModEntry.Config.staminaDrainPerSecond, Set=v=>ModEntry.Config.staminaDrainPerSecond=v },
            new NumField { Label="击晕耗体力", Step=0.5f, Min=0f, Max=999f,
                Get=()=>ModEntry.Config.staminaCostPerStun, Set=v=>ModEntry.Config.staminaCostPerStun=v },
            new NumField { Label="写板书回体力", Step=0.5f, Min=0f, Max=999f,
                Get=()=>ModEntry.Config.staminaPerWrite, Set=v=>ModEntry.Config.staminaPerWrite=v },
            new NumField { Label="击晕时长(秒)", Step=0.5f, Min=0.5f, Max=10f,
                Get=()=>ModEntry.Config.miniStunSeconds, Set=v=>ModEntry.Config.miniStunSeconds=v },
            new NumField { Label="击晕冷却(秒)", Step=1f, Min=0.5f, Max=120f,
                Get=()=>ModEntry.Config.miniStunCooldownSeconds, Set=v=>ModEntry.Config.miniStunCooldownSeconds=v },
            new NumField { Label="击晕距离", Step=0.5f, Min=0.5f, Max=10f,
                Get=()=>ModEntry.Config.miniStunRange, Set=v=>ModEntry.Config.miniStunRange=v },
            new NumField { Label="吹箭伤害", Step=1, Min=1, Max=99, Int=true,
                Get=()=>ModEntry.Config.dartDamage, Set=v=>ModEntry.Config.dartDamage=Mathf.RoundToInt(v) },
            new NumField { Label="每感染加伤", Step=0.05f, Min=0f, Max=3f,
                Get=()=>ModEntry.Config.dartDamageBonusPerInfected, Set=v=>ModEntry.Config.dartDamageBonusPerInfected=v },
            new NumField { Label="备弹上限", Step=10, Min=1, Max=999, Int=true,
                Get=()=>ModEntry.Config.dartMaxAmmo, Set=v=>ModEntry.Config.dartMaxAmmo=Mathf.RoundToInt(v) },
            new NumField { Label="食物补弹", Step=10, Min=0, Max=999, Int=true,
                Get=()=>ModEntry.Config.dartAmmoPerFood, Set=v=>ModEntry.Config.dartAmmoPerFood=Mathf.RoundToInt(v) },
            new NumField { Label="连发间隔(秒)", Step=0.1f, Min=0.1f, Max=10f,
                Get=()=>ModEntry.Config.fireInterval, Set=v=>ModEntry.Config.fireInterval=v },
            new NumField { Label="每层减速", Step=0.05f, Min=0.05f, Max=0.8f,
                Get=()=>ModEntry.Config.dartSlowPerStack, Set=v=>ModEntry.Config.dartSlowPerStack=v },
            new NumField { Label="减速层数上限", Step=1, Min=1, Max=10, Int=true,
                Get=()=>ModEntry.Config.dartSlowMaxStacks, Set=v=>ModEntry.Config.dartSlowMaxStacks=Mathf.RoundToInt(v) },
            new NumField { Label="减速持续(秒)", Step=0.1f, Min=0.1f, Max=10f,
                Get=()=>ModEntry.Config.dartSlowSeconds, Set=v=>ModEntry.Config.dartSlowSeconds=v },
            new NumField { Label="倒地复活(秒)", Step=1, Min=1, Max=30, Int=true,
                Get=()=>ModEntry.Config.downSeconds, Set=v=>ModEntry.Config.downSeconds=Mathf.RoundToInt(v) },
            new NumField { Label="猎手解锁人数", Step=1, Min=1, Max=50, Int=true,
                Get=()=>ModEntry.Config.hunterTriggerStudents, Set=v=>ModEntry.Config.hunterTriggerStudents=Mathf.RoundToInt(v) },
            new NumField { Label="猎手HP", Step=1, Min=1, Max=999, Int=true,
                Get=()=>ModEntry.Config.hunterHp, Set=v=>ModEntry.Config.hunterHp=Mathf.RoundToInt(v) },
            new NumField { Label="猎手伤害", Step=1, Min=1, Max=99, Int=true,
                Get=()=>ModEntry.Config.hunterDamage, Set=v=>ModEntry.Config.hunterDamage=Mathf.RoundToInt(v) },
            new NumField { Label="猎手攻速(秒)", Step=0.1f, Min=0.1f, Max=10f,
                Get=()=>ModEntry.Config.hunterAttackInterval, Set=v=>ModEntry.Config.hunterAttackInterval=v },
            new NumField { Label="手枪伤害", Step=1, Min=1, Max=99, Int=true,
                Get=()=>ModEntry.Config.pistolDamage, Set=v=>ModEntry.Config.pistolDamage=Mathf.RoundToInt(v) },
            new NumField { Label="手枪弹匣", Step=1, Min=1, Max=60, Int=true,
                Get=()=>ModEntry.Config.pistolMagSize, Set=v=>ModEntry.Config.pistolMagSize=Mathf.RoundToInt(v) },
            new NumField { Label="手枪备弹上限", Step=5, Min=1, Max=999, Int=true,
                Get=()=>ModEntry.Config.pistolMaxAmmo, Set=v=>ModEntry.Config.pistolMaxAmmo=Mathf.RoundToInt(v) },
            new NumField { Label="食物补手枪弹", Step=5, Min=0, Max=999, Int=true,
                Get=()=>ModEntry.Config.pistolAmmoPerFood, Set=v=>ModEntry.Config.pistolAmmoPerFood=Mathf.RoundToInt(v) },
            new NumField { Label="手枪射速(秒)", Step=0.1f, Min=0.1f, Max=10f,
                Get=()=>ModEntry.Config.pistolFireInterval, Set=v=>ModEntry.Config.pistolFireInterval=v },
            new NumField { Label="军刀轻击伤害", Step=1, Min=1, Max=99, Int=true,
                Get=()=>ModEntry.Config.kukriLightDamage, Set=v=>ModEntry.Config.kukriLightDamage=Mathf.RoundToInt(v) },
            new NumField { Label="军刀重击伤害", Step=1, Min=1, Max=99, Int=true,
                Get=()=>ModEntry.Config.kukriHeavyDamage, Set=v=>ModEntry.Config.kukriHeavyDamage=Mathf.RoundToInt(v) },
            new NumField { Label="轻击间隔(秒)", Step=0.1f, Min=0.15f, Max=10f,
                Get=()=>ModEntry.Config.kukriLightInterval, Set=v=>ModEntry.Config.kukriLightInterval=v },
            new NumField { Label="重击冷却(秒)", Step=0.1f, Min=0.2f, Max=10f,
                Get=()=>ModEntry.Config.kukriHeavyCooldown, Set=v=>ModEntry.Config.kukriHeavyCooldown=v },
            new NumField { Label="轻击距离", Step=0.5f, Min=0.5f, Max=10f,
                Get=()=>ModEntry.Config.kukriLightRange, Set=v=>ModEntry.Config.kukriLightRange=v },
            new NumField { Label="重击距离", Step=0.5f, Min=0.5f, Max=10f,
                Get=()=>ModEntry.Config.kukriHeavyRange, Set=v=>ModEntry.Config.kukriHeavyRange=v },
            new NumField { Label="手雷伤害", Step=5, Min=1, Max=999, Int=true,
                Get=()=>ModEntry.Config.grenadeDamage, Set=v=>ModEntry.Config.grenadeDamage=Mathf.RoundToInt(v) },
            new NumField { Label="手雷半径", Step=0.5f, Min=1f, Max=20f,
                Get=()=>ModEntry.Config.grenadeRadius, Set=v=>ModEntry.Config.grenadeRadius=v },
            new NumField { Label="手雷引信(秒)", Step=0.5f, Min=1f, Max=10f,
                Get=()=>ModEntry.Config.grenadeFuseSeconds, Set=v=>ModEntry.Config.grenadeFuseSeconds=v },
        };

        private static float BuildGrid(Transform parent, float innerW, float bodyH)
        {
            // 超过 30 项自动切三列，防两列布局把面板撑出屏（行高同步压扁）
            int columns = NumFields.Length > 30 ? 3 : 2;
            int rows = (NumFields.Length + columns - 1) / columns;
            float cellW = (innerW - 12f * (columns - 1)) / columns;
            float rowH = Mathf.Clamp(bodyH / rows, 20f, 40f);
            // 字号必须跟着行高缩：39 项把行高压到 20 多像素，还按 baseFontSize-5 取字号会被整行截没
            int labelSize = Mathf.Clamp(Mathf.FloorToInt(rowH * 0.6f), 13, baseFontSize - 5);
            int btnFontSize = Mathf.Clamp(labelSize + 2, 13, Mathf.FloorToInt(rowH - 9f));
            for (int i = 0; i < NumFields.Length; i++)
            {
                NumField f = NumFields[i];
                int col = i / rows, row = i % rows;
                var cell = new GameObject("数值_" + f.Label, typeof(RectTransform));
                cell.transform.SetParent(parent, false);
                float x = -innerW / 2f + col * (cellW + 12f);
                float y = -(row * rowH) - 4f;
                Place(cell.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0f, 1f), new Vector2(cellW, rowH - 6f), new Vector2(x, y));

                Text label = NewText(cell.transform, "标签", f.Label, labelSize, TextAnchor.MiddleLeft, bodyInk);
                Place(label.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(Mathf.Max(60f, cellW - 96f), rowH - 6f), Vector2.zero);

                // 右侧一组：− [数值] +（共占 96px，给六字标签留够宽度）
                f.MinusBtn = NewButton(cell.transform, "减", "−", new Color(0.45f, 0.3f, 0.2f, 1f), new Vector2(22f, rowH - 8f),
                    () => Adjust(f, -f.Step), btnFontSize);
                Place(f.MinusBtn.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, rowH - 8f), new Vector2(cellW - 96f, 0f));

                f.ValueText = NewText(cell.transform, "值", "", labelSize, TextAnchor.MiddleCenter,
                    lightBg ? new Color(0.55f, 0.36f, 0.05f, 1f) : new Color(1f, 0.92f, 0.6f, 1f));
                Place(f.ValueText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(44f, rowH - 6f), new Vector2(cellW - 70f, 0f));

                f.PlusBtn = NewButton(cell.transform, "加", "+", new Color(0.25f, 0.42f, 0.28f, 1f), new Vector2(22f, rowH - 8f),
                    () => Adjust(f, f.Step), btnFontSize);
                Place(f.PlusBtn.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(22f, rowH - 8f), new Vector2(cellW - 26f, 0f));
            }
            return rows * rowH;
        }

        private static void Adjust(NumField f, float delta)
        {
            if (!NetRuntime.IsServer || ModEntry.ClassInProgress()) return;
            float v = f.Get() + delta;
            v = Mathf.Round(v * 100f) / 100f;
            f.Set(Mathf.Clamp(v, f.Min, f.Max));
            BioChemConfig.Instance.Clamp(); // 全量边界兜底
            ModEntry.OnConfigEdited();
        }

        // 克隆设置窗口的窗框节点：剥掉游戏逻辑脚本只留引擎视觉件，页签等残件藏掉，
        // 标题含「设置」的改文案；克隆瞬间游戏脚本的 Awake 无法避免，失败则整体回退自建底框
        private static bool TryCloneWindowFrame(out RectTransform win)
        {
            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(bgNode, canvasGo.transform, false);
                clone.name = "窗口(复用设置界面)";
                foreach (Animator a in clone.GetComponentsInChildren<Animator>(true))
                    UnityEngine.Object.Destroy(a);
                foreach (MonoBehaviour mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    string ns = mb.GetType().Namespace;
                    if (ns != null && ns.StartsWith("UnityEngine")) continue;
                    UnityEngine.Object.Destroy(mb);
                }
                // LayoutGroup 系虽然也是 UnityEngine 命名空间，但会重排我们手动摆放的子控件，剥掉；
                // 滑条/开关/下拉/滚动条同理是设置窗口的残件（只销毁组件不藏节点，防止误伤窗框根节点）
                foreach (Behaviour b in clone.GetComponentsInChildren<Behaviour>(true))
                {
                    if (b is LayoutGroup || b is LayoutElement || b is ContentSizeFitter || b is AspectRatioFitter
                        || b is Slider || b is Toggle || b is Dropdown || b is Scrollbar || b is ScrollRect)
                        UnityEngine.Object.Destroy(b);
                }
                foreach (Button b in clone.GetComponentsInChildren<Button>(true))
                {
                    // RemoveAllListeners 清不掉序列化的持久监听（目标 UISettingView 已被剥掉，点了报错）；
                    // 拆掉重建一个干净的 Button（Destroy 同帧生效不了，必须 DestroyImmediate 才能 AddComponent 同类型）。
                    // gameObject/挂图引用必须在销毁前抓出来——销毁后的组件引用不能再访问
                    GameObject buttonGo = b.gameObject;
                    Graphic targetGraphic = b.targetGraphic;
                    string n = buttonGo.name.ToLowerInvariant();
                    bool isClose = n.Contains("close") || n.Contains("guanbi") || n.Contains("cha");
                    UnityEngine.Object.DestroyImmediate(b);
                    Button fresh = buttonGo.AddComponent<Button>();
                    fresh.targetGraphic = targetGraphic;
                    fresh.transition = Selectable.Transition.ColorTint;
                    if (isClose) fresh.onClick.AddListener(Close);
                    else buttonGo.SetActive(false);
                }
                bool titleDone = false;
                foreach (Text t in clone.GetComponentsInChildren<Text>(true))
                {
                    t.raycastTarget = false;
                    if (!titleDone && t.text.Contains("设置"))
                    {
                        t.text = "生化模式 · 管理面板";
                        titleDone = true;
                    }
                }
                RectTransform rt = (RectTransform)clone.transform;
                rt.localScale = Vector3.one;
                Place(rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(Mathf.Abs(rt.rect.width), Mathf.Abs(rt.rect.height)), Vector2.zero);
                if (!titleDone)
                {
                    Text title = NewText(rt, "标题", "生化模式 · 管理面板", baseFontSize + 8, TextAnchor.MiddleCenter, Color.white);
                    Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                        new Vector2(Mathf.Abs(rt.rect.width) - 48f, 52f), new Vector2(0f, -26f));
                }
                win = rt;
                return true;
            }
            catch (System.Exception e)
            {
                if (clone != null) UnityEngine.Object.Destroy(clone);
                ModEntry.Log("[状态] 复用设置窗口窗框失败，回退自建底框：" + e.Message);
                win = null;
                return false;
            }
        }

        internal static void Refresh()
        {
            if (!IsOpen) return;
            SetUIInputRule(true); // 主菜单打开时 InputManager 可能还没建好，补挂输入冻结
            RefreshPanelValues();
        }

        // 自建面板页与游戏设置窗口嵌入页共用的数值/开关/文案刷新。
        // Unity 的 Text.text 同值写入不会触发重排，每帧调用无害
        internal static void RefreshPanelValues()
        {
            string names = "";
            if (ModEntry.infected.Count > 0)
            {
                var list = new List<string>();
                foreach (ulong uid in ModEntry.infected) list.Add(ModEntry.GetName(uid));
                names = string.Join("、", list);
            }
            if (statusText != null)
            {
                statusText.text = "当前状态：" + (ModEntry.enabled ? "已启用" : "未启用")
                    + (names == "" ? "\n小老师：暂无" : "\n小老师：" + names);
            }

            bool editable = NetRuntime.IsServer && !ModEntry.ClassInProgress();
            if (!NetRuntime.IsServer)
            {
                if (hostToggle != null) hostToggle.interactable = false;
                if (hostToggleLabel != null) hostToggleLabel.text = "模式与数值由房主控制：" + (ModEntry.enabled ? "已启用" : "未启用");
            }
            else if (ModEntry.ClassInProgress())
            {
                if (hostToggle != null) hostToggle.interactable = false;
                if (hostToggleLabel != null) hostToggleLabel.text = "课程进行中，模式与数值已锁定（课前或下课后可更改）";
            }
            else
            {
                if (hostToggle != null) hostToggle.interactable = true;
                if (hostToggleLabel != null) hostToggleLabel.text = ModEntry.enabled ? "启用中 — 点击关闭生化模式" : "已关闭 — 点击开启生化模式";
            }

            // 数值显示随（房主同步的）配置实时刷新；步进器仅房主课前可点
            foreach (NumField f in NumFields)
            {
                if (f.ValueText != null)
                    f.ValueText.text = f.Int ? f.Get().ToString("0") : f.Get().ToString("0.##");
                if (f.MinusBtn != null) f.MinusBtn.interactable = editable;
                if (f.PlusBtn != null) f.PlusBtn.interactable = editable;
            }
        }

        // ===== 嵌入游戏设置窗口的页内容 =====

        // 嵌入成功后调用：在游戏设置面板的 RectTransform 里竖排 状态 + 开关 + 步进器网格。
        // 构建件与自建面板完全一致（NumFields/Adjust 共用，只是宿主换了）
        internal static void BuildEmbeddedPage(Transform parent, float w, float h)
        {
            LocateAssets(); // 玩家可能没按过 F7：字体/按钮图首次在这里缓存
            lightBg = true;               // 嵌入的是原版设置窗（浅色底）
            bodyInk = vanillaInk;
            ModEntry.Log("[诊断] 嵌入页样式：字体=" + (font != null ? font.name : "null")
                + " 墨色=" + bodyInk + " 字号=" + baseFontSize);
            float pad = 24f;
            float innerW = Mathf.Max(320f, w - pad * 2f);
            statusText = NewText(parent, "状态", "", baseFontSize - 2, TextAnchor.UpperLeft, bodyInk);
            Place(statusText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(innerW, 52f), new Vector2(pad, -2f));

            hostToggle = NewButton(parent, "开关", "", new Color(0.25f, 0.45f, 0.75f, 1f), new Vector2(innerW, 38f),
                () => { if (NetRuntime.IsServer) ModEntry.SetEnabled(!ModEntry.enabled); });
            Place(hostToggle.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(innerW, 38f), new Vector2(pad, -58f));
            hostToggleLabel = hostToggle.GetComponentInChildren<Text>();
            hostToggleLabel.fontSize = Mathf.Max(16, baseFontSize - 4); // 状态文案较长，缩一号铺满整行

            // 网格必须有独立容器：BuildGrid 从容器顶部往下排，直接挂面板根会压住状态行和开关。
            // 玩法规则长文案已删（超出屏幕），整个下部高度都给步进器网格
            float gridTop = 108f;
            float gridH = Mathf.Max(240f, h - gridTop - 8f);
            var gridGo = new GameObject("数值网格", typeof(RectTransform));
            gridGo.transform.SetParent(parent, false);
            Place(gridGo.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(innerW, gridH), new Vector2(pad, -gridTop));
            BuildGrid(gridGo.transform, innerW, gridH);
        }

        // ===== 控件辅助 =====

        // 带文字的按钮：尺寸/位置由调用方 Place 覆盖；fontSize 可选（小格子按钮的字必须跟着缩，否则被截没）
        private static Button NewButton(Transform parent, string name, string label, Color fallbackColor, Vector2 size, Action onClick, int? fontSize = null)
        {
            Image img = NewImage(parent, name, btnSprite, fallbackColor);
            Place(img.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), size, Vector2.zero);
            Button btn = img.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.ColorTint;
            Text t = NewText(img.transform, "文字", label, fontSize ?? Mathf.Max(16, baseFontSize - 2), TextAnchor.MiddleCenter, bodyInk);
            Stretch(t.rectTransform);
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            return btn;
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
