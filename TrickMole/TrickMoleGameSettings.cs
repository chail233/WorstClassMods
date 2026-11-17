using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace TrickMole
{
    /// <summary>真嵌入游戏设置窗口：玩家手动打开游戏设置时，克隆 UISettingView 的页签、新建 UI_SettingPanel 子类页，
    /// 追加进 tabList/panelList——「课堂角色」成为游戏设置里的一页，与原版页签同款切换/导航。
    /// 窗口实例可能被回收重建：每秒扫描，正开着的实例优先、窗口池里的闲置实例兜底。</summary>
    internal static class TrickMoleGameSettings
    {
        public static UISettingView View;      // 已嵌入的窗口实例
        public static int PageIndex = -1;      // 我们的页在 panelList 里的下标
        private static float scanAt;           // 场景实例扫描节流（FindObjectsOfTypeAll 很贵）
        private static int lastOpenLogId;      // 诊断：设置窗口打开状态变化时打一条

        public static bool Embedded => View != null && PageIndex >= 0;

        // ===== 每帧驱动 =====

        public static void Tick()
        {
            // 窗口实例可能被回收重建：优先跟随真正打开的那个实例挂页，池里闲置实例兜底
            if (Time.unscaledTime >= scanAt)
            {
                scanAt = Time.unscaledTime + 1f;
                UISettingView open = GetOpenView();
                if (open != null && open.GetInstanceID() != lastOpenLogId)
                {
                    lastOpenLogId = open.GetInstanceID();
                    ModEntry.Log("[诊断] 设置窗口已打开：实例=" + lastOpenLogId
                        + " 嵌入实例=" + (View != null ? View.GetInstanceID() : 0)
                        + " 页=" + PageIndex);
                }
                else if (open == null)
                {
                    lastOpenLogId = 0;
                }
                UISettingView target = open != null ? open : (Embedded ? null : FindSceneView());
                if (target != null && (!Embedded || View != target))
                {
                    try { Embed(target); }
                    catch (Exception e) { ModEntry.Log("[状态] 嵌入游戏设置窗口失败：" + e.Message); }
                }
            }

            // 设置窗口开着时保持数值/开关文案刷新
            if (Embedded && IsSettingOpen()) TrickMolePanel.RefreshPanelValues();
        }

        // ===== 嵌入 =====

        private static UISettingView GetOpenView()
        {
            try
            {
                var ui = SingletonT<UIManager>.Instance;
                return ui != null ? ui.GetOpenUI(EUIWindowType.Setting) as UISettingView : null;
            }
            catch { return null; }
        }

        private static bool IsSettingOpen()
        {
            try
            {
                var ui = SingletonT<UIManager>.Instance;
                return ui != null && ui.IsUIOpen(EUIWindowType.Setting);
            }
            catch { return false; }
        }

        private static UISettingView FindSceneView()
        {
            UISettingView idle = null;
            foreach (UIWindow w in Resources.FindObjectsOfTypeAll<UIWindow>())
            {
                if (!(w is UISettingView) || !w.gameObject.scene.IsValid()) continue;
                if (w.gameObject.activeInHierarchy) return (UISettingView)w; // 正开着的窗口优先
                if (idle == null) idle = (UISettingView)w;                    // 窗口池里的实例兜底
            }
            return idle;
        }

        private static void Embed(UISettingView view)
        {
            List<UI_SettingTab> tabs = view.tabList;
            List<UI_SettingPanel> panels = view.panelList;
            if (tabs == null || panels == null || tabs.Count == 0 || panels.Count == 0)
                throw new InvalidOperationException("设置窗口页签/面板列表为空");
            for (int i = 0; i < panels.Count; i++)
            {
                if (panels[i] != null && panels[i].GetComponent<TrickMoleSettingPanel>() != null)
                {
                    View = view; PageIndex = i; return; // 已经挂过（窗口复用）
                }
            }

            UI_SettingPanel samplePanel = panels[0];
            UI_SettingTab lastTab = tabs[tabs.Count - 1];
            int newIndex = panels.Count;

            // 1) 页签：克隆最后一枚，按钮拆掉重建接我们的下标
            GameObject tabGo = UnityEngine.Object.Instantiate(lastTab.gameObject, lastTab.transform.parent);
            tabGo.name = "页签_课堂角色";
            // 最后一枚是官方的「模组设置」页签：没有 ModSettings 注册时游戏会把它隐藏（inactive），
            // 而 SwitchTab 遇到 inactive 页签直接 return —— 不激活的话切页永远静默失败
            tabGo.SetActive(true);
            RectTransform tabRt = (RectTransform)tabGo.transform;
            RectTransform lastRt = (RectTransform)lastTab.transform;
            // 兜底位置：沿用相邻页签间距外推（LayoutTabs 能对齐时会整体重排覆盖）
            tabRt.anchoredPosition = tabs.Count >= 2
                ? lastRt.anchoredPosition + (lastRt.anchoredPosition - ((RectTransform)tabs[tabs.Count - 2].transform).anchoredPosition)
                : lastRt.anchoredPosition + new Vector2(lastRt.rect.width + 8f, 0f);
            tabRt.localScale = Vector3.one;
            foreach (MonoBehaviour mb in tabGo.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb is UI_SettingTab) continue;      // 页签状态件保留
                if (mb is Button) continue;             // 稍后重建
                string ns = mb.GetType().Namespace;
                if (ns == null || !ns.StartsWith("UnityEngine")) UnityEngine.Object.Destroy(mb);
            }
            // 克隆的是官方的「模组设置」页签，文字走本地化键（UI_ModSettings_Tab）。不清键的话
            // OnEnable/SetText 会把它刷回「模组设置」，玩家看到两个同名页签，自然以为没加成功
            foreach (Text t in tabGo.GetComponentsInChildren<Text>(true))
            {
                if (t is TextEx te) te.SetKey(null);
                t.text = "课堂角色";
            }
            Button oldBtn = tabGo.GetComponent<Button>();
            if (oldBtn != null) UnityEngine.Object.DestroyImmediate(oldBtn); // 持久监听（原下标）随组件一起没
            Button tabBtn = tabGo.AddComponent<Button>();
            tabBtn.transition = Selectable.Transition.None; // 选中态由 UI_SettingTab 的 selected 图管理
            int capturedIndex = newIndex;
            tabBtn.onClick.AddListener(() => { try { view.OnClickTab(capturedIndex); } catch { } });

            // 2) 面板页：RectTransform 抄现有面板（同容器、同尺寸），内容复用我们的构建件
            var panelGo = new GameObject("设置页_课堂角色", typeof(RectTransform));
            panelGo.transform.SetParent(samplePanel.transform.parent, false);
            RectTransform src = (RectTransform)samplePanel.transform;
            RectTransform rt = panelGo.GetComponent<RectTransform>();
            rt.anchorMin = src.anchorMin; rt.anchorMax = src.anchorMax; rt.pivot = src.pivot;
            rt.sizeDelta = src.sizeDelta; rt.anchoredPosition = src.anchoredPosition;
            rt.offsetMin = src.offsetMin; rt.offsetMax = src.offsetMax;
            rt.localScale = src.localScale;
            var panel = panelGo.AddComponent<TrickMoleSettingPanel>();
            float w = Mathf.Abs(src.rect.width);
            float h = Mathf.Abs(src.rect.height);
            TrickMolePanel.BuildEmbeddedPage(panelGo.transform, w, h);
            // 设置窗口是 ScreenSpaceCamera 画布，UI 全在 Layer 5；运行时 new 的对象默认 Layer 0，
            // 会被 UI 相机按 cullingMask 裁掉——整页内容"看不见"就是它
            SetLayerRecursive(panelGo, samplePanel.gameObject.layer);
            SetLayerRecursive(tabGo, lastTab.gameObject.layer);

            // 3) 追加进列表并保持当前页不变
            UI_SettingTab ourTab = tabGo.GetComponent<UI_SettingTab>();
            tabs.Add(ourTab);
            panels.Add(panel);
            panel.SetVisible(false); // 当前页保持不变，玩家点到我们页签时 SwitchTab 会点亮并调 SetInfo
            LayoutTabs(tabs);
            View = view;
            PageIndex = panels.Count - 1;
            try { ourTab.SetState(false); } catch { } // 页签可视件（选中/未选中图）先摆成未选中态
            ModEntry.Log("[状态] 「课堂角色」已挂进游戏设置窗口（第 " + (PageIndex + 1) + " 页，共 " + panels.Count + " 页）");
        }

        // 页签排布：只排「可见」页签——没有 ModSettings 注册时官方会把最后一枚页签隐藏（inactive），
        // 全量排会留下空槽。优先保持原生尺寸/间距；放不下时等比收窄并避开右上角关闭按钮
        private static void LayoutTabs(List<UI_SettingTab> tabs)
        {
            var vis = new List<UI_SettingTab>();
            foreach (UI_SettingTab t in tabs)
                if (t != null && t.gameObject.activeSelf) vis.Add(t);
            if (vis.Count < 2) return;

            Transform parent = vis[0].transform.parent;
            float y = vis[0].transform.GetComponent<RectTransform>().anchoredPosition.y;
            float left = float.MaxValue, nativeW = 0f, firstX = 0f, lastX = 0f;
            int originals = vis.Count - 1; // 最后一枚是我们克隆的
            for (int i = 0; i < originals; i++)
            {
                RectTransform rt = (RectTransform)vis[i].transform;
                // 布局与预期不符（不同容器/不同行）就保持原样，不硬挪
                if (rt.parent != parent || Mathf.Abs(rt.anchoredPosition.y - y) > 1f) return;
                if (i == 0) { firstX = rt.anchoredPosition.x; nativeW = Mathf.Abs(rt.rect.width); }
                lastX = rt.anchoredPosition.x;
                left = Mathf.Min(left, rt.anchoredPosition.x - Mathf.Abs(rt.rect.width) * 0.5f);
            }
            float step = originals > 1 ? (lastX - firstX) / (originals - 1) : nativeW + 8f;
            float gap = Mathf.Max(4f, step - nativeW);

            float limit = CloseButtonLeft(parent);
            float width = nativeW;
            float need = vis.Count * nativeW + (vis.Count - 1) * gap;
            float available = limit - left;
            if (need > available) width = Mathf.Max(90f, (available - (vis.Count - 1) * gap) / vis.Count);

            for (int i = 0; i < vis.Count; i++)
            {
                RectTransform rt = (RectTransform)vis[i].transform;
                rt.sizeDelta = new Vector2(width, rt.sizeDelta.y);
                rt.anchoredPosition = new Vector2(left + width * 0.5f + i * (width + gap), rt.anchoredPosition.y);
                Image sel = vis[i].selected, unsel = vis[i].unselected;
                if (sel != null) sel.rectTransform.sizeDelta = new Vector2(Mathf.Max(60f, width - 10f), sel.rectTransform.sizeDelta.y);
                if (unsel != null) unsel.rectTransform.sizeDelta = new Vector2(Mathf.Max(60f, width - 10f), unsel.rectTransform.sizeDelta.y);
            }
        }

        // 页签右侧可用边界：右上角关闭按钮的左缘（找不到就用画布右缘留白）
        private static float CloseButtonLeft(Transform tabParent)
        {
            if (tabParent != null)
                foreach (Transform child in tabParent)
                    if (child.name == "CloseBtn")
                    {
                        RectTransform rt = (RectTransform)child;
                        return rt.anchoredPosition.x - Mathf.Abs(rt.rect.width) * 0.5f - 6f;
                    }
            return 900f;
        }

        // 游戏 UI 相机只渲染 UI 层；运行时 new 出来的对象默认 Layer 0，必须整棵子树改成父面板的层
        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }
    }

    /// <summary>游戏设置窗口里的课堂角色页：SwitchTab 切到本页时游戏会调 SetInfo()，正好刷新数值显示</summary>
    public class TrickMoleSettingPanel : UI_SettingPanel
    {
        public override void SetInfo()
        {
            TrickMolePanel.RefreshPanelValues();
        }
    }
}
