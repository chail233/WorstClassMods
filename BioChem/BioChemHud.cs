using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace BioChem
{
    internal sealed class BioChemHud
    {
        private const float Width = 420f;
        private const float Inset = 17f;
        private const float Margin = 20f;
        private static readonly Color Ink = new Color32(250, 251, 222, 255);
        private static readonly Color Muted = new Color32(207, 201, 177, 255);
        private static readonly Color Gold = new Color32(241, 195, 122, 255);
        private static readonly Color Health = new Color32(228, 117, 100, 255);
        private static readonly Color Stamina = new Color32(165, 235, 179, 255);

        private readonly List<string> names = new List<string>(3);
        private readonly Vector3[] corners = new Vector3[4];
        private readonly Image[] slotFrames = new Image[4];
        private RectTransform root, viewport, loadout;
        private Canvas canvas;
        private Font font;
        private Sprite idleSlot, selectedSlot, keyFrame, meterTrack;
        private Image accent;
        private Text identity, evolution, weapon, ammo, hint, damage, roster;
        private Meter health, stamina;

        internal void Tick()
        {
            if (!ModEntry.enabled || !ClassroomManager.IsSingletonActive() || !UnitManager.IsSingletonActive()
                || BioChemPanel.IsOpen)
            {
                Hide();
                return;
            }

            var state = ClassroomManager.Instance.curState;
            Unit self = UnitManager.Instance.localPlayer;
            if ((state != EClassroomState.Prepare && state != EClassroomState.Classing) || self == null || !self.isActived)
            {
                Hide();
                return;
            }

            bool hunter = ModEntry.LocalIsHunter;
            bool mini = ModEntry.LocalIsInfected;
            bool teacher = ModEntry.LocalIsBigTeacher;
            bool student = StudentWeapons.CanSelect(self);
            var view = SingletonT<UIManager>.Instance.GetOpenUI(EUIWindowType.BattleMain) as UIBattleMainView;
            if (view == null || !view.isActiveAndEnabled || !(hunter || mini || teacher || student))
            {
                Hide();
                return;
            }

            if (root == null || viewport != view.transform) Build(view);
            if (root == null) return;

            SetText(identity, hunter ? "老师猎手" : mini ? "小老师" : teacher ? "老师" : "学生");
            accent.color = identity.color = hunter ? Gold : mini || teacher ? Health : Ink;
            SetText(evolution, !hunter && (teacher || mini)
                ? $"进化 {ModEntry.GetEvolutionStage(self.UID)}/{ModEntry.MaxEvolutionStage}"
                : "");

            float y = 57f;
            if (hunter || mini || teacher)
            {
                float hp = hunter ? ModEntry.GetHunterHp(self.UID) : ModEntry.GetHp(self.UID, teacher);
                float max = hunter ? ModEntry.Config.hunterHp : ModEntry.GetTeacherMaxHp(self.UID, teacher);
                health.Show(ref y, hp, max);
            }
            else health.Hide();
            if (mini) stamina.Show(ref y, ModEntry.SelfStamina(), ModEntry.Config.staminaMax);
            else stamina.Hide();

            bool classing = state == EClassroomState.Classing;
            loadout.gameObject.SetActive(student);
            if (student) UpdateLoadout(ref y);
            SetText(hint, Status(self, hunter, mini, teacher, classing));
            SetText(damage, classing ? DamageText() : "");
            SetText(roster, RosterText());
            Stack(hint, ref y, 2);
            Stack(damage, ref y, 1);
            Stack(roster, ref y, 2);

            root.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, y + 8f);
            FitToView(view);
        }

        private void Build(UIBattleMainView view)
        {
            Dispose();
            viewport = view.transform as RectTransform;
            canvas = view.GetComponent<Canvas>();
            font = view.countDownText?.font ?? view.EnergyCountText?.font;
            if (viewport == null || canvas == null || font == null) return;

            idleSlot = FindSprite(view.skillContainer, "UnSelectBg");
            selectedSlot = FindSprite(view.skillContainer, "SelectBg");
            keyFrame = FindSprite(view.skillContainer, "KeyBg");
            meterTrack = FindSprite(view.trickSlider?.transform.parent, "bg");

            root = NewRect(viewport, "生化HUD");
            root.anchorMin = root.anchorMax = Vector2.zero;
            root.pivot = new Vector2(1f, 0f);
            root.sizeDelta = new Vector2(Width, 0f);

            Stretch(ImageAt(root, "描边", new Color32(148, 116, 72, 224)).rectTransform, 0f);
            Stretch(ImageAt(root, "底板", new Color32(31, 27, 22, 249)).rectTransform, 1f);
            accent = ImageAt(root, "身份强调色", Gold);
            Place(accent.rectTransform, 1f, 1f, 118f, 3f);
            identity = Label(root, "身份", 28, Ink, bold: true);
            Place(identity.rectTransform, Inset, 9f, 174f, 39f);
            evolution = Label(root, "进化阶数", 17, Muted, TextAnchor.MiddleRight);
            Place(evolution.rectTransform, 167f, 10f, 133f, 36f);
            var separator = ImageAt(root, "分隔线", new Color32(111, 91, 64, 220));
            Place(separator.rectTransform, Inset, 49f, Width - Inset * 2f, 1f);

            var settings = ImageAt(root, "设置", keyFrame == null ? new Color32(70, 54, 33, 255) : Color.white, keyFrame);
            Place(settings.rectTransform, Width - Inset - 86f, 12f, 86f, 30f);
            settings.raycastTarget = true;
            var button = settings.gameObject.AddComponent<Button>();
            button.targetGraphic = settings;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(ModEntry.TogglePanel);
            var settingsLabel = Label(settings.transform, "F7 设置", 16, Gold, TextAnchor.MiddleCenter);
            Stretch(settingsLabel.rectTransform, 0f);
            settingsLabel.text = "F7 设置";

            health = new Meter(this, "生命", Health);
            stamina = new Meter(this, "体力", Stamina);
            BuildLoadout();
            hint = Label(root, "操作与状态", 18, Ink);
            damage = Label(root, "感染伤害加成", 16, Gold);
            roster = Label(root, "感染名单", 16, Muted);
        }

        private void BuildLoadout()
        {
            loadout = NewRect(root, "武器栏");
            weapon = Label(loadout, "已选武器", 22, Ink, bold: true);
            Place(weapon.rectTransform, 0f, 0f, 160f, 31f);
            ammo = Label(loadout, "弹药", 20, Gold, TextAnchor.MiddleRight);
            Place(ammo.rectTransform, 160f, 0f, Width - Inset * 2f - 160f, 31f);

            const float gap = 8f;
            float cellWidth = (Width - Inset * 2f - gap * (slotFrames.Length - 1)) / slotFrames.Length;
            for (int i = 0; i < slotFrames.Length; i++)
            {
                var slot = (StudentWeaponSlot)(i + 1);
                string slotWeapon = StudentWeapons.WeaponName(slot);
                var frame = slotFrames[i] = ImageAt(loadout, StudentWeapons.SlotName(slot), Color.white, idleSlot);
                Place(frame.rectTransform, i * (cellWidth + gap), 36f, cellWidth, 78f);
                var key = ImageAt(frame.transform, "数字键", keyFrame == null ? new Color32(62, 48, 30, 255) : Color.white, keyFrame);
                Place(key.rectTransform, 7f, 6f, 23f, 20f);
                var number = Label(key.transform, "键值", 13, Gold, TextAnchor.MiddleCenter);
                Stretch(number.rectTransform, 0f);
                number.text = (i + 1).ToString();

                var item = Label(frame.transform, "武器", 20, slotWeapon == null ? Muted : Ink, TextAnchor.MiddleCenter, true);
                Place(item.rectTransform, 0f, 26f, cellWidth, 27f);
                item.text = slotWeapon ?? "—";
                var label = Label(frame.transform, "栏位", 14, Muted, TextAnchor.MiddleCenter);
                Place(label.rectTransform, 0f, 54f, cellWidth, 17f);
                label.text = StudentWeapons.SlotName(slot);
            }
        }

        private void UpdateLoadout(ref float y)
        {
            Place(loadout, Inset, y, Width - Inset * 2f, 114f);
            SetText(weapon, StudentWeapons.SelectedWeapon ?? (StudentWeapons.Selected == StudentWeaponSlot.None ? "武器" : "空栏位"));
            SetText(ammo, Ak47Weapon.Equipped
                ? $"{Ak47Weapon.Mag}/{Ak47Weapon.MagSize} · 备弹 {ModEntry.SelfDartAmmo}/{ModEntry.Config.dartMaxAmmo}"
                : "");
            for (int i = 0; i < slotFrames.Length; i++)
            {
                bool selected = (int)StudentWeapons.Selected == i + 1;
                Sprite sprite = selected ? selectedSlot : idleSlot;
                if (slotFrames[i].sprite != sprite) slotFrames[i].sprite = sprite;
                slotFrames[i].color = sprite != null ? Color.white : new Color32(70, 54, 35, selected ? (byte)255 : (byte)160);
            }
            y += 120f;
        }

        private static string Status(Unit self, bool hunter, bool mini, bool teacher, bool classing)
        {
            var config = ModEntry.Config;
            if (hunter) return $"猎刀 · 左键挥砍   {config.hunterDamage} 伤害 / {config.hunterAttackInterval:0.#} 秒";
            if (mini)
            {
                string stun = ModEntry.StunCooldownRemain > 0f ? $"击晕冷却 {Mathf.CeilToInt(ModEntry.StunCooldownRemain)} 秒"
                    : ModEntry.SelfStamina() < config.staminaCostPerStun ? $"体力不足 · 击晕需 {config.staminaCostPerStun:0.#}"
                    : $"F 击晕 · 消耗 {config.staminaCostPerStun:0.#} 体力";
                return stun + "\n靠近眩晕的学生抓取，再带到罚站柱";
            }
            if (teacher) return ModEntry.SelfSlowStacks > 0
                ? $"被减速 ×{ModEntry.SelfSlowStacks}   ·   移速 -{ModEntry.SelfSlowStacks * config.dartSlowPerStack:P0}" : "";

            string action;
            if (Ak47Weapon.Equipped)
                action = ModEntry.SelfDartAmmo <= 0 ? "备弹耗尽 · 进食补充"
                    : Ak47Weapon.Reloading ? $"换弹中 · {Ak47Weapon.ReloadRemain:0.0} 秒"
                    : "左键连发   右键瞄准   R 换弹";
            else if (StudentWeapons.Selected == StudentWeaponSlot.Primary)
            {
                Unit mouthItem = self.GetComponent<MouthItemUseComponent>()?.currentMouthItem;
                action = mouthItem != null && !StudentWeapons.IsLocalAk(mouthItem)
                    ? "嘴部被占用 · 腾空后按 1 装备"
                    : "AK47 未就绪 · 按 1 重试";
            }
            else action = "按 1 装备 AK47";
            return classing && ModEntry.HunterUnlocked ? action + "\nG 变身老师猎手" : action;
        }

        private static string DamageText()
        {
            float bonus = ModEntry.DartDamageBonus();
            return bonus > 0f
                ? $"AK47 伤害 {ModEntry.EffectiveDartDamage()}   ·   基础 {ModEntry.Config.dartDamage}   ·   感染 +{bonus:P0}"
                : $"AK47 伤害 {ModEntry.EffectiveDartDamage()}";
        }

        private string RosterText()
        {
            int total = ModEntry.infected.Count;
            if (total == 0) return "";
            names.Clear();
            foreach (ulong uid in ModEntry.infected)
            {
                if (names.Count == 3) break;
                string name = (ModEntry.GetName(uid) ?? "玩家").Replace('\r', ' ').Replace('\n', ' ');
                names.Add(name.Length > 10 ? name.Substring(0, 9) + "…" : name);
            }
            return "小老师 · " + string.Join("、", names) + (total > names.Count ? $" 等 {total} 人" : "");
        }

        private void Stack(Text label, ref float y, int maxLines)
        {
            bool visible = label.text.Length > 0;
            label.gameObject.SetActive(visible);
            if (!visible) return;

            float width = Width - Inset * 2f;
            var settings = label.GetGenerationSettings(new Vector2(width, 0f));
            float preferred = label.cachedTextGeneratorForLayout.GetPreferredHeight(label.text, settings) / label.pixelsPerUnit;
            float lineHeight = label.fontSize + 6f;
            float height = Mathf.Clamp(Mathf.Ceil(preferred) + 2f, lineHeight, lineHeight * maxLines);
            Place(label.rectTransform, Inset, y, width, height);
            y += height + 3f;
        }

        private void FitToView(UIBattleMainView view)
        {
            Rect frame = viewport.rect;
            Rect available = frame;
            Camera eventCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Rect safe = Screen.safeArea;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, safe.min, eventCamera, out var min)
                && RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, safe.max, eventCamera, out var max))
            {
                available = Rect.MinMaxRect(Mathf.Max(frame.xMin, min.x), Mathf.Max(frame.yMin, min.y),
                    Mathf.Min(frame.xMax, max.x), Mathf.Min(frame.yMax, max.y));
            }

            float guideLeft = view.mGuideKeyItemPanel != null && view.mGuideKeyItemPanel.transform is RectTransform guide
                ? BoundsOf(guide).xMin : available.xMax;
            Rect? skills = view.skillContainer is RectTransform skillRect && skillRect.gameObject.activeInHierarchy
                ? BoundsOf(skillRect) : (Rect?)null;
            float scale = Fit(available, guideLeft, skills, canvas.scaleFactor, root.rect.height, out var corner);
            if (scale <= 0f)
            {
                Hide();
                return;
            }
            root.localScale = Vector3.one * scale;
            root.anchoredPosition = corner - frame.min;
            root.gameObject.SetActive(true);
        }

        private static float Fit(Rect available, float guideLeft, Rect? skills, float canvasScale, float cardHeight,
            out Vector2 corner)
        {
            float right = Mathf.Min(available.xMax, guideLeft) - Margin;
            float bottom = available.yMin + Margin;
            float scale = Mathf.Clamp(0.72f / Mathf.Max(0.01f, canvasScale), 1f, 1.4f);
            scale = Mathf.Min(scale, (right - available.xMin - Margin) / Width);
            if (skills is Rect occupied && occupied.xMin < right && occupied.xMax > right - Width * scale)
                bottom = Mathf.Max(bottom, occupied.yMax + Margin);
            float maxHeight = Mathf.Min(available.height * 0.39f, available.yMax - bottom - Margin);
            corner = new Vector2(right, bottom);
            return Mathf.Min(scale, maxHeight / cardHeight);
        }

        private Rect BoundsOf(RectTransform child)
        {
            child.GetWorldCorners(corners);
            Vector3 first = viewport.InverseTransformPoint(corners[0]);
            Vector3 last = viewport.InverseTransformPoint(corners[2]);
            return Rect.MinMaxRect(first.x, first.y, last.x, last.y);
        }

        private void Hide()
        {
            if (root != null && root.gameObject.activeSelf) root.gameObject.SetActive(false);
        }

        internal void Dispose()
        {
            if (root != null) Object.Destroy(root.gameObject);
            root = null;
            viewport = null;
        }

        private static Sprite FindSprite(Transform parent, string childName)
        {
            if (parent == null) return null;
            foreach (var image in parent.GetComponentsInChildren<Image>(true))
                if (image.name == childName && image.sprite != null) return image.sprite;
            return null;
        }

        private Text Label(Transform parent, string name, int size, Color color, TextAnchor align = TextAnchor.MiddleLeft, bool bold = false)
        {
            var label = NewRect(parent, name).gameObject.AddComponent<Text>();
            label.font = font;
            label.fontSize = size;
            label.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            label.color = color;
            label.alignment = align;
            label.supportRichText = false;
            label.raycastTarget = false;
            return label;
        }

        private static RectTransform NewRect(Transform parent, string name)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false);
            return rect;
        }

        private static Image ImageAt(Transform parent, string name, Color color, Sprite sprite = null)
        {
            var image = NewRect(parent, name).gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        private static void Stretch(RectTransform rect, float padding)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.one * padding;
            rect.offsetMax = -Vector2.one * padding;
        }

        private static void SetText(Text label, string text)
        {
            if (label.text != text) label.text = text;
        }

        private sealed class Meter
        {
            private readonly RectTransform row;
            private readonly RectTransform fill;
            private readonly Text count;

            internal Meter(BioChemHud hud, string name, Color color)
            {
                const float width = Width - Inset * 2f;
                row = NewRect(hud.root, name);
                Text label = hud.Label(row, name, 17, Muted);
                label.text = name;
                Place(label.rectTransform, 0, 0, 130f, 23f);
                count = hud.Label(row, "数值", 20, Ink, TextAnchor.MiddleRight, true);
                Place(count.rectTransform, 130f, 0, width - 130f, 23f);
                var track = ImageAt(row, "槽", new Color32(111, 94, 68, 255), hud.meterTrack);
                Place(track.rectTransform, 0, 25f, width, 8f);
                fill = ImageAt(track.transform, "进度", color).rectTransform;
                Stretch(fill, 2f);
            }

            internal void Show(ref float y, float value, float max)
            {
                row.gameObject.SetActive(true);
                Place(row, Inset, y, Width - Inset * 2f, 34f);
                SetText(count, $"{value:0.#} / {max:0.#}");
                float fraction = max > 0f ? Mathf.Clamp01(value / max) : 0f;
                fill.gameObject.SetActive(fraction > 0f);
                fill.anchorMax = new Vector2(fraction, 1f);
                y += 41f;
            }

            internal void Hide() => row.gameObject.SetActive(false);
        }
    }
}
