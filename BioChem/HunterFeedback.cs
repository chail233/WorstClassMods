using System.Collections.Generic;
using UnityEngine;

namespace BioChem
{
    /// <summary>
    /// 猎刀命中反馈（仅攻击者本机，客户端预测，CF 生化手感——点击瞬间出反馈，不等服务器）：
    /// - 命中标记：准星处四斜块 ✕ 闪现（击杀=红色加大）
    /// - 伤害浮字：服务端确认扣血后在目标位置上浮淡出
    /// - 震屏：复用老师剑 TeacherSwordFeedback 命中震屏（HorizontalHit 同款曲线），击杀加大振幅
    /// 音效不在这里：攻击者命中音走挥刀预测（ModEntry）、其他端走 Hit 广播，靠 AttackerUID 去重防双响
    /// </summary>
    internal static class HunterFeedback
    {
        private const float HitMarkerTime = 0.16f;   // 普通命中标记停留
        private const float KillMarkerTime = 0.35f;  // 击杀标记停留
        private const float NumberLife = 0.7f;       // 伤害浮字寿命
        private const float NumberRiseWorld = 0.45f; // 浮字上浮的世界高度
        private const float KillShakeScale = 1.7f;   // 击杀震屏振幅倍数

        private sealed class FloatNumber
        {
            public Vector3 worldPos;
            public string text;
            public Color color;
            public int fontSize;
            public float screenOffset;
            public float spawnAt;
        }

        private static readonly List<FloatNumber> numbers = new List<FloatNumber>();
        private static float hitMarkerUntil;
        private static float killMarkerUntil;
        private static TeacherSwordFeedback shake;               // 老师剑同款震屏组件（懒建；随场景卸载销毁后自动重建）
        private static TeacherSwordShakeSettings shakeSettings;  // 优先抄老师剑 hitShake 曲线，懒解析一次
        private static GUIStyle numberStyle;                     // IMGUI 样式（OnGUI 内懒建）
        private static int nextNumberOffset;

        internal static void OnPredictedHit(bool killed)
        {
            float now = Time.unscaledTime;
            if (killed) killMarkerUntil = now + KillMarkerTime;
            hitMarkerUntil = Mathf.Max(hitMarkerUntil, now + HitMarkerTime);
            PlayShake(killed ? KillShakeScale : 1f);
        }

        internal static void ShowDamage(Vector3 position, int amount)
        {
            numbers.Add(new FloatNumber
            {
                worldPos = position,
                text = "-" + amount,
                color = new Color(1f, 0.44f, 0.30f),
                fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.028f), 20, 42),
                screenOffset = ((nextNumberOffset++ % 3) - 1) * 20f,
                spawnAt = Time.unscaledTime,
            });
            if (numbers.Count > 24) numbers.RemoveRange(0, numbers.Count - 24);
        }

        // 复用老师剑命中震屏（与老师挥剑命中同一手感来源；曲线上优先抄场上老师模型的 hitShake 设置）。
        // 手雷爆炸近距离震屏也走这里（BioGrenade.PlayExplosion）
        internal static void PlayShake(float amplitudeScale)
        {
            if (!MonoSingletonT<SceneManager>.IsSingletonActive()) return;
            Camera camera = MonoSingletonT<SceneManager>.Instance.MainCamera;
            if (camera == null) return;
            if (shake == null)
                shake = new GameObject("HunterKnifeFeedback").AddComponent<TeacherSwordFeedback>();
            if (shakeSettings == null)
            {
                shakeSettings = FindTeacherHitShake() ?? new TeacherSwordShakeSettings(); // 场上没老师就用默认衰减曲线
            }
            shake.Play(camera, shakeSettings, SwordShakeKind.HorizontalHit, amplitudeScale);
        }

        private static TeacherSwordShakeSettings FindTeacherHitShake()
        {
            if (!UnitManager.IsSingletonActive()) return null;
            foreach (Unit unit in UnitManager.Instance.playerList)
            {
                if (unit == null || !unit.isActived) continue;
                // TeacherMeleeConfig 是模型上的 MonoBehaviour（非 IUnitComponent），走 Unity 侧 GetComponent
                var config = unit.gameObject.GetComponent<TeacherMeleeConfig>();
                var settings = config != null ? config.settings : null;
                if (settings != null && settings.horizontalFeedback != null && settings.horizontalFeedback.hitShake != null)
                    return settings.horizontalFeedback.hitShake;
            }
            return null;
        }

        // ===== IMGUI 绘制（Pump.OnGUI 每帧调用） =====

        internal static void DrawOverlay()
        {
            float now = Time.unscaledTime;
            bool noMarker = now >= killMarkerUntil && now >= hitMarkerUntil;
            if (noMarker && numbers.Count == 0) return;

            if (!noMarker)
            {
                bool killMark = now < killMarkerUntil;
                float remain = (killMark ? killMarkerUntil : hitMarkerUntil) - now;
                float life = killMark ? KillMarkerTime : HitMarkerTime;
                float alpha = Mathf.Clamp01(remain / life * 2f); // 前半程全亮，后半程淡出
                Color color = killMark
                    ? new Color(1f, 0.25f, 0.2f, alpha)
                    : new Color(1f, 1f, 1f, alpha * 0.85f);
                DrawHitMarker(new Vector2(Screen.width / 2f, Screen.height / 2f), killMark ? 30f : 22f, color);
            }

            // 清过期浮字
            for (int i = numbers.Count - 1; i >= 0; i--)
                if (now - numbers[i].spawnAt > NumberLife) numbers.RemoveAt(i);
            if (numbers.Count == 0) return;
            Camera camera = MonoSingletonT<SceneManager>.IsSingletonActive()
                ? MonoSingletonT<SceneManager>.Instance.MainCamera : null;
            if (camera == null) return;
            if (numberStyle == null)
                numberStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            foreach (FloatNumber number in numbers)
            {
                float t = Mathf.Clamp01((now - number.spawnAt) / NumberLife);
                Vector3 screen = camera.WorldToScreenPoint(number.worldPos + Vector3.up * (t * NumberRiseWorld));
                if (screen.z <= 0f) continue; // 相机背后不画
                float alpha = 1f - t * t;     // 二次曲线淡出
                float pop = Mathf.Lerp(1.35f, 1f, Mathf.Clamp01(t * 6f));
                numberStyle.fontSize = Mathf.RoundToInt(number.fontSize * pop);
                Vector2 size = numberStyle.CalcSize(new GUIContent(number.text));
                // WorldToScreenPoint 是左下原点，GUI 是左上原点
                Rect rect = new Rect(screen.x + number.screenOffset - size.x / 2f, Screen.height - screen.y - size.y / 2f, size.x, size.y);
                numberStyle.normal.textColor = new Color(0f, 0f, 0f, alpha * 0.85f);
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), number.text, numberStyle);
                numberStyle.normal.textColor = new Color(number.color.r, number.color.g, number.color.b, alpha);
                GUI.Label(rect, number.text, numberStyle);
            }
        }

        // 经典 FPS 命中标记：四枚 45° 斜块绕准星一圈（Texture2D.whiteTexture + 矩阵旋转，不依赖字体字形）
        private static void DrawHitMarker(Vector2 center, float size, Color color)
        {
            float gap = size * 0.32f;               // 中心留空（不遮准星）
            float tickLen = size * 0.42f;
            float thickness = Mathf.Max(2f, size * 0.14f);
            for (int i = 0; i < 4; i++)
            {
                float angle = 45f + i * 90f;
                // GUI 坐标系 y 向下：+角度=x 轴转向 +y，径向方向与矩形长轴同一角度参数
                Vector2 dir = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
                Vector2 tickCenter = center + dir * (gap + tickLen * 0.5f);
                DrawRotatedRect(tickCenter, angle, new Vector2(tickLen, thickness), color);
            }
        }

        private static void DrawRotatedRect(Vector2 center, float angleDeg, Vector2 size, Color color)
        {
            Matrix4x4 saved = GUI.matrix;
            Vector3 pivot = new Vector3(center.x, center.y, 0f); // GUI.matrix 在 GUI 坐标系（左上原点）工作
            Quaternion rotation = Quaternion.Euler(0f, 0f, angleDeg);
            GUI.matrix = Matrix4x4.TRS(pivot, rotation, Vector3.one) * Matrix4x4.TRS(-pivot, Quaternion.identity, Vector3.one);
            var content = new Rect(center.x - size.x / 2f, center.y - size.y / 2f, size.x, size.y);
            GUI.DrawTexture(content, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, color, 0f, 0f);
            GUI.matrix = saved;
        }
    }
}
