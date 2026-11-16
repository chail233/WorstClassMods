using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace BioChem
{
    /// <summary>真嵌入游戏设置窗口：克隆 UISettingView 的页签、新建 UI_SettingPanel 子类页，
    /// 追加进 tabList/panelList——「生化模式」成为游戏设置里的一页，与原版页签同款切换/导航。
    /// 任何一步失败都走 BioChemPanel 自建面板兜底。页内容复用 BioChemPanel 的构建件。</summary>
    internal static class BioChemGameSettings
    {
        public static UISettingView View;      // 已嵌入的窗口实例
        public static int PageIndex = -1;      // 我们的页在 panelList 里的下标
        private static readonly HashSet<int> embeddedIds = new HashSet<int>();
        private static bool pendingSwitch;     // ShowUI 异步开窗后轮询到实例再跳页
        private static float pendingSwitchUntil;
        private static float scanAt;           // 场景实例扫描节流（FindObjectsOfTypeAll 很贵）

        public static bool Embedded => View != null && PageIndex >= 0;

        // ===== 每帧驱动 =====

        public static void Tick()
        {
            // ShowUI 异步加载：窗口实例就绪后补挂页并跳转
            if (pendingSwitch)
            {
                if (Time.unscaledTime > pendingSwitchUntil)
                {
                    pendingSwitch = false;
                }
                else
                {
                    UISettingView view = GetOpenView();
                    if (view != null)
                    {
                        pendingSwitch = false;
                        if (!Embedded || View != view)
                        {
                            try { Embed(view); }
                            catch (Exception e)
                            {
                                ModEntry.Log("[状态] 嵌入游戏设置窗口失败，改用自建面板：" + e.Message);
                                return;
                            }
                        }
                        if (Embedded && View == view)
                        {
                            BioChemPanel.HideForEmbedded(); // 自建面板退场（不动输入规则，游戏设置窗口的规则还开着）
                            view.SwitchTab(PageIndex);
                        }
                    }
                }
            }

            // 玩家手动开过设置窗口（或主菜单挂着）→ 自动挂页
            if (!Embedded && Time.unscaledTime >= scanAt)
            {
                scanAt = Time.unscaledTime + 1f;
                UISettingView view = FindSceneView();
                if (view != null && !embeddedIds.Contains(view.GetInstanceID()))
                {
                    try
                    {
                        Embed(view);
                        BioChemPanel.HideForEmbedded(); // 玩家手动开的设置窗口挂上了页：自建面板退场
                    }
                    catch (Exception e) { ModEntry.Log("[状态] 嵌入游戏设置窗口失败（不影响自建面板）：" + e.Message); }
                }
            }

            // 设置窗口开着时保持数值/开关文案刷新
            if (Embedded && IsSettingOpen()) BioChemPanel.RefreshPanelValues();
        }

        // ===== F7：打开游戏设置窗口并跳到生化页；嵌不进去返回 false 走自建面板 =====

        public static bool TryOpen()
        {
            try
            {
                var ui = SingletonT<UIManager>.Instance;
                if (ui == null) return false;

                UISettingView open = GetOpenView();
                if (open != null)
                {
                    // 已开着：确保页挂上（失败返回 false 走自建面板），然后跳到我们页
                    if (!Embedded || View != open)
                    {
                        try { Embed(open); }
                        catch (Exception e)
                        {
                            ModEntry.Log("[状态] 嵌入游戏设置窗口失败，改用自建面板：" + e.Message);
                            return false;
                        }
                    }
                    if (!Embedded || View != open) return false;
                    BioChemPanel.HideForEmbedded();
                    open.SwitchTab(PageIndex);
                    return true;
                }
                if (ui.IsUIOpenOrLoading(EUIWindowType.Setting)) return true; // 加载中：Tick 轮询完成后自动跳页
                ui.ShowUI(EUIWindowType.Setting);
                pendingSwitch = true;
                pendingSwitchUntil = Time.unscaledTime + 8f;
                return true;
            }
            catch (Exception e)
            {
                ModEntry.Log("[状态] 拉起游戏设置窗口失败：" + e.Message);
                return false;
            }
        }

        private static void SwitchToOurs(UISettingView view)
        {
            if (!Embedded || View != view) return;
            try { view.SwitchTab(PageIndex); } catch (Exception e) { ModEntry.Log("[状态] 跳到生化页失败：" + e.Message); }
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
            foreach (UIWindow w in Resources.FindObjectsOfTypeAll<UIWindow>())
            {
                if (w is UISettingView && w.gameObject.scene.IsValid()) return (UISettingView)w;
            }
            return null;
        }

        private static void Embed(UISettingView view)
        {
            List<UI_SettingTab> tabs = view.tabList;
            List<UI_SettingPanel> panels = view.panelList;
            if (tabs == null || panels == null || tabs.Count == 0 || panels.Count == 0)
                throw new InvalidOperationException("设置窗口页签/面板列表为空");
            for (int i = 0; i < panels.Count; i++)
            {
                if (panels[i] != null && panels[i].GetComponent<BioChemSettingPanel>() != null)
                {
                    View = view; PageIndex = i; return; // 已经挂过（窗口复用）
                }
            }

            UI_SettingPanel samplePanel = panels[0];
            UI_SettingTab lastTab = tabs[tabs.Count - 1];
            int newIndex = panels.Count;

            // 1) 页签：克隆最后一枚，位置沿相邻页签的间距外推；按钮拆掉重建接我们的下标
            GameObject tabGo = UnityEngine.Object.Instantiate(lastTab.gameObject, lastTab.transform.parent);
            tabGo.name = "页签_生化模式";
            RectTransform tabRt = (RectTransform)tabGo.transform;
            RectTransform lastRt = (RectTransform)lastTab.transform;
            if (tabs.Count >= 2)
            {
                RectTransform prevRt = (RectTransform)tabs[tabs.Count - 2].transform;
                tabRt.anchoredPosition = lastRt.anchoredPosition + (lastRt.anchoredPosition - prevRt.anchoredPosition);
            }
            else
            {
                tabRt.anchoredPosition = lastRt.anchoredPosition + new Vector2(lastRt.rect.width + 8f, 0f);
            }
            tabRt.localScale = Vector3.one;
            foreach (MonoBehaviour mb in tabGo.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb is UI_SettingTab) continue;      // 页签状态件保留
                if (mb is Button) continue;             // 稍后重建
                string ns = mb.GetType().Namespace;
                if (ns == null || !ns.StartsWith("UnityEngine")) UnityEngine.Object.Destroy(mb);
            }
            Button oldBtn = tabGo.GetComponent<Button>();
            if (oldBtn != null) UnityEngine.Object.DestroyImmediate(oldBtn); // 持久监听（原下标）随组件一起没
            Button tabBtn = tabGo.AddComponent<Button>();
            tabBtn.transition = Selectable.Transition.None; // 选中态由 UI_SettingTab 的 selected 图管理
            int capturedIndex = newIndex;
            tabBtn.onClick.AddListener(() => { try { view.OnClickTab(capturedIndex); } catch { } });

            // 2) 面板页：RectTransform 抄现有面板（同容器、同尺寸），内容复用我们的构建件
            var panelGo = new GameObject("设置页_生化模式", typeof(RectTransform));
            panelGo.transform.SetParent(samplePanel.transform.parent, false);
            RectTransform src = (RectTransform)samplePanel.transform;
            RectTransform rt = panelGo.GetComponent<RectTransform>();
            rt.anchorMin = src.anchorMin; rt.anchorMax = src.anchorMax; rt.pivot = src.pivot;
            rt.sizeDelta = src.sizeDelta; rt.anchoredPosition = src.anchoredPosition;
            rt.offsetMin = src.offsetMin; rt.offsetMax = src.offsetMax;
            rt.localScale = src.localScale;
            var panel = panelGo.AddComponent<BioChemSettingPanel>();
            float w = Mathf.Abs(src.rect.width);
            float h = Mathf.Abs(src.rect.height);
            BioChemPanel.BuildEmbeddedPage(panelGo.transform, w, h);

            // 3) 追加进列表并保持当前页不变
            tabs.Add(tabGo.GetComponent<UI_SettingTab>());
            panels.Add(panel);
            panel.SetVisible(false); // 当前页保持不变，玩家点到我们页签时 SwitchTab 会点亮并调 SetInfo
            View = view;
            PageIndex = panels.Count - 1;
            embeddedIds.Add(view.GetInstanceID());
            ModEntry.Log("[状态] 「生化模式」已挂进游戏设置窗口（第 " + (PageIndex + 1) + " 页，共 " + panels.Count + " 页）");
        }
    }

    /// <summary>游戏设置窗口里的生化页：SwitchTab 切到本页时游戏会调 SetInfo()，正好刷新数值显示</summary>
    public class BioChemSettingPanel : UI_SettingPanel
    {
        public override void SetInfo()
        {
            BioChemPanel.RefreshPanelValues();
        }
    }
}
