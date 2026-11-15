using System.Collections.Generic;
using Rewired;
using Rewired.Interfaces;
using UnityEngine;

namespace TrickMole
{
    /// <summary>
    /// 游戏原生风格按键提示浮层（模式借鉴 Workshop 玩法整合包的 KeyHint）：
    /// 优先取 Rewired 键盘元素标识上的原生按键图标，取不到时画文字键帽；
    /// 位置在屏幕中央偏右下（原生交互提示区域附近），仅 Repaint 阶段绘制。
    /// </summary>
    internal static class KeyHint
    {
        private static readonly Dictionary<KeyCode, Sprite> glyphs = new Dictionary<KeyCode, Sprite>();
        private static readonly HashSet<KeyCode> missing = new HashSet<KeyCode>();
        private static Texture2D capTex;

        private static float S => Mathf.Clamp((float)Screen.height / 1080f, 0.55f, 2f);

        internal static void Prompt(KeyCode key, string text)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            float s = S;
            float capH = 42f * s;
            float x = Screen.width / 2f + 80f * s;
            float y = Screen.height / 2f - 4f * s;
            float capW = Cap(x, y, capH, key);
            GUIStyle style = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(30f * s),
                alignment = TextAnchor.MiddleLeft,
                fontStyle = FontStyle.Bold
            };
            GUI.Label(new Rect(x + capW + 20f * s, y - 4f * s, 900f * s, capH + 8f * s), text, style);
        }

        // Rewired 键盘元素标识 → 原生按键图标；取不到的键记入 missing 后改画文字键帽
        private static Sprite Glyph(KeyCode k)
        {
            if (glyphs.TryGetValue(k, out Sprite sprite)) return sprite;
            if (missing.Contains(k)) return null;
            try
            {
                if (!ReInput.isReady) { missing.Add(k); return null; }
                Keyboard keyboard = ReInput.controllers.Keyboard;
                if (keyboard == null) { missing.Add(k); return null; }
                string name = Norm(RewiredName(k));
                foreach (ControllerElementIdentifier identifier in keyboard.ButtonElementIdentifiers)
                {
                    if (identifier == null || Norm(identifier.name) != name) continue;
                    object glyph = null;
                    try { glyph = identifier.GetGlyph((AxisRange)0); } catch { }
                    if (!(glyph is Sprite))
                    {
                        try
                        {
                            IGlyphProvider provider = ReInput.glyphs.glyphProvider;
                            object boxed = null;
                            if (provider != null && provider.TryGetGlyph(identifier.GetFinalGlyphKey((AxisRange)0), out boxed))
                                glyph = boxed;
                        }
                        catch { }
                    }
                    sprite = glyph as Sprite;
                    break;
                }
            }
            catch { }
            if (sprite != null) glyphs[k] = sprite;
            else missing.Add(k);
            return sprite;
        }

        private static string Norm(string s)
        {
            return s?.Replace(" ", "").Replace("_", "").ToLowerInvariant() ?? "";
        }

        private static string RewiredName(KeyCode k)
        {
            int i = (int)k;
            if (i >= 48 && i <= 57) return (i - 48).ToString(); // 数字键
            return k.ToString(); // 字母键等直接用键名
        }

        // 键帽背景：中心实、边缘渐隐的白纹理，配合 16px border 拉出圆角
        private static Texture2D CapTex()
        {
            if (capTex != null) return capTex;
            capTex = new Texture2D(64, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int x = 0; x < 64; x++)
            {
                for (int y = 0; y < 64; y++)
                {
                    float cx = Mathf.Clamp(x + 0.5f, 14f, 50f);
                    float cy = Mathf.Clamp(y + 0.5f, 14f, 50f);
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                    capTex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(14f - dist + 0.5f)));
                }
            }
            capTex.Apply();
            return capTex;
        }

        private static float Cap(float x, float y, float h, KeyCode k)
        {
            return Cap(x, y, h, Glyph(k), k.ToString());
        }

        private static float Cap(float x, float y, float h, Sprite sprite, string label)
        {
            Color color = GUI.color;
            if (sprite != null && sprite.texture != null)
            {
                Rect tr = sprite.textureRect;
                float w = h * tr.width / Mathf.Max(1f, tr.height);
                Texture tex = sprite.texture;
                GUI.color = new Color(1f, 1f, 1f, 1f);
                GUI.DrawTextureWithTexCoords(new Rect(x, y, w, h), tex,
                    new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height));
                GUI.color = color;
                return w;
            }
            GUIStyle textStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(h / S * ((label.Length > 2) ? 0.42f : 0.58f)),
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold
            };
            float w2 = Mathf.Max(h, textStyle.CalcSize(new GUIContent(label)).x + h * 0.55f);
            GUIStyle boxStyle = new GUIStyle { border = new RectOffset(16, 16, 16, 16) };
            boxStyle.normal.background = CapTex();
            GUI.color = new Color(0f, 0f, 0f, 0.25f);
            GUI.Box(new Rect(x + 1f, y + 2f, w2, h), GUIContent.none, boxStyle);
            GUI.color = new Color(0.98f, 0.98f, 0.96f, 1f);
            GUI.Label(new Rect(x + 1f, y + 2f, w2, h), label, textStyle);
            GUI.color = color;
            return w2;
        }
    }
}
