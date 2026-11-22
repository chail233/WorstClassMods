using UnityEngine;
using UnityEngine.UI;

namespace TeacherReward
{
    // 奖励选择面板：UGUI 复用游戏设置窗口（UISettingView）的素材——背景框/按钮图/字体
    // （关闭过的窗口实例留在窗口池里 SetActive(false)，运行时随时可从中抽取引用）。
    // 老师专属：按 H 打开（ModEntry.HandlePanelKey 门槛：老师+上课中+有剩余点数），
    // 点卡 → ModEntry.SelectCard → 服务端扣 1 点并广播快照，本端确认后自动关面板。
    internal static class RewardPanel
    {
        private static GameObject canvasGo;
        private static Text pointsText;
        private static bool uiRuleActive;

        private static Sprite bgSprite;
        private static GameObject bgNode;
        private static Sprite btnSprite;
        private static Font font;
        private static int baseFontSize = 24;
        private static Color vanillaInk = new Color(0.12f, 0.11f, 0.10f, 1f); // 从设置窗口文字里抠出来的墨色
        private static Color bodyInk = Color.white;                            // 当前面板正文色（换宿主时设置）
        private static bool lightBg = true;                                    // 当前宿主是不是浅色底

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
            if (canvasGo != null && canvasGo.activeSelf)
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
            canvasGo = new GameObject("老师奖励面板", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
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
            bool cloned = bgNode != null && TryCloneWindowFrame(out win);
            // 克隆到的设置窗框是浅色底：正文改用原版墨色；自建深色底框才用白字
            lightBg = cloned;
            bodyInk = cloned ? vanillaInk : Color.white;
            if (!cloned)
            {
                Image winImg = NewImage(canvasGo.transform, "窗口", bgSprite, new Color(0.14f, 0.16f, 0.22f, 0.97f));
                win = winImg.rectTransform;
                Place(win, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(780f, 620f), Vector2.zero);
                Text title = NewText(win, "标题", "老师奖励", baseFontSize + 8, TextAnchor.MiddleCenter, bodyInk);
                Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(680f, 48f), new Vector2(0f, -12f));
            }

            float winW = Mathf.Abs(win.rect.width);
            float winH = Mathf.Abs(win.rect.height);
            float innerW = winW - 96f;

            pointsText = NewText(win, "点数", "", baseFontSize, TextAnchor.MiddleCenter, bodyInk);
            Place(pointsText.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(innerW, 40f), new Vector2(0f, -66f));

            // 卡片网格：2 列（占位卡数量少，两列够用且格子大）
            int columns = 2;
            int rows = (ModEntry.Cards.Length + columns - 1) / columns;
            float cellW = (innerW - 16f * (columns - 1)) / columns;
            float gridTop = 116f;
            float gridH = Mathf.Max(rows * 96f, winH - gridTop - 90f);
            float rowH = Mathf.Clamp(gridH / rows, 96f, 150f);
            for (int i = 0; i < ModEntry.Cards.Length; i++)
            {
                RewardCardDef card = ModEntry.Cards[i];
                int col = i % columns, row = i / columns;
                float x = -innerW / 2f + col * (cellW + 16f) + cellW / 2f;
                float y = -gridTop - rowH / 2f - row * (rowH + 12f);
                Button btn = NewButton(win, "卡_" + card.Id, "", new Color(0.22f, 0.36f, 0.52f, 1f), new Vector2(cellW, rowH), null);
                Place(btn.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(cellW, rowH), new Vector2(x, y));
                Text name = NewText(btn.transform, "卡名", card.Name, baseFontSize, TextAnchor.MiddleCenter, bodyInk);
                Place(name.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(cellW - 24f, 34f), new Vector2(0f, -10f));
                Text desc = NewText(btn.transform, "描述", card.Desc, Mathf.Max(14, baseFontSize - 4), TextAnchor.MiddleCenter,
                    lightBg ? new Color(0.45f, 0.40f, 0.34f, 1f) : new Color(0.82f, 0.82f, 0.82f, 1f));
                Place(desc.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(cellW - 24f, 40f), new Vector2(0f, -50f));
                RewardCardDef captured = card;
                btn.onClick.AddListener(() => ModEntry.SelectCard(captured.Id));
            }

            Image closeImg = NewImage(win, "关闭", btnSprite, new Color(0.5f, 0.25f, 0.25f, 1f));
            Place(closeImg.rectTransform, new Vector2(0.5f, 0f), new Vector2(1f, 0f), new Vector2(150f, 52f), new Vector2(-24f, 18f));
            Button closeBtn = closeImg.gameObject.AddComponent<Button>();
            Text closeLabel = NewText(closeImg.transform, "文字", "关闭", baseFontSize, TextAnchor.MiddleCenter, bodyInk);
            Stretch(closeLabel.rectTransform);
            closeBtn.onClick.AddListener(Close);
        }

        // 克隆设置窗口的窗框节点：剥掉游戏逻辑脚本只留引擎视觉件，残件藏掉、标题改文案；
        // 克隆瞬间游戏脚本的 Awake 无法避免，失败则整体回退自建底框
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
                        t.text = "老师奖励";
                        titleDone = true;
                    }
                }
                RectTransform rt = (RectTransform)clone.transform;
                rt.localScale = Vector3.one;
                Place(rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(Mathf.Abs(rt.rect.width), Mathf.Abs(rt.rect.height)), Vector2.zero);
                if (!titleDone)
                {
                    Text title = NewText(rt, "标题", "老师奖励", baseFontSize + 8, TextAnchor.MiddleCenter, Color.white);
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

        // ===== 每帧刷新 =====

        internal static void Refresh()
        {
            if (!IsOpen) return;
            SetUIInputRule(true); // 主菜单等场景打开时 InputManager 可能还没建好，补挂输入冻结
            int points = ModEntry.MyPointsLeft;
            bool usable = points > 0;
            if (pointsText != null)
                pointsText.text = "剩余奖励点：" + points + " / " + ModEntry.MaxPoints
                    + (usable ? "　（点一张卡消耗 1 点）" : "　（点数已用完）");
            foreach (Button btn in canvasGo.GetComponentsInChildren<Button>(true))
            {
                // 卡片按钮按剩余点数启用/禁用；已持有的常驻卡/护盾灰掉防重复购买；关闭按钮与遮罩永远可点
                string n = btn.gameObject.name;
                if (!n.StartsWith("卡_")) continue;
                btn.interactable = usable && !ModEntry.IsCardOwned(n.Substring(2));
            }
        }

        // ===== 控件辅助 =====

        // 带文字的按钮：尺寸/位置由调用方 Place 覆盖；label 为空则不放文字（卡片按钮自己叠两层文本）
        private static Button NewButton(Transform parent, string name, string label, Color fallbackColor, Vector2 size, System.Action onClick)
        {
            Image img = NewImage(parent, name, btnSprite, fallbackColor);
            Place(img.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), size, Vector2.zero);
            Button btn = img.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.ColorTint;
            if (!string.IsNullOrEmpty(label))
            {
                Text t = NewText(img.transform, "文字", label, Mathf.Max(16, baseFontSize - 2), TextAnchor.MiddleCenter, bodyInk);
                Stretch(t.rectTransform);
            }
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
