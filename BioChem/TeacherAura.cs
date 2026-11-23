using System.Collections.Generic;
using UnityEngine;

namespace BioChem
{
    internal static class TeacherAura
    {
        // 浮在头顶的双锥轮廓，从侧面和高处都可辨认。
        private static readonly Vector3 Tip = Vector3.up * 0.24f;
        private static readonly Vector3[] Outline =
        {
            Tip, Vector3.right * 0.16f, -Tip, Vector3.forward * 0.16f, Tip,
            Vector3.left * 0.16f, -Tip, Vector3.back * 0.16f, Tip,
        };
        private static readonly Dictionary<ulong, LineRenderer> markers = new Dictionary<ulong, LineRenderer>();
        private static readonly HashSet<ulong> visible = new HashSet<ulong>();
        private static readonly List<ulong> expired = new List<ulong>();
        private static Material material;

        internal static void Tick()
        {
            if (!ModEntry.enabled || !ModEntry.ServerClassActive() || !NetRuntime.IsConnected || !UnitManager.IsSingletonActive())
            {
                Clear();
                return;
            }

            visible.Clear();
            float phase = Time.unscaledTime;
            Color color = Color.Lerp(new Color32(255, 46, 38, 255), new Color32(255, 159, 74, 255),
                0.5f + 0.5f * Mathf.Sin(phase * 5f));
            foreach (Unit unit in UnitManager.Instance.playerList)
            {
                if (!ModEntry.IsLivingTeacherOpponent(unit) || !unit.isPlayer || unit.isLocalPlayer || unit.transform == null
                    || unit.GetComponent<PlayerInvisibilityComponent>()?.IsInvisible == true) continue;

                Transform model = unit.transform;
                if (!markers.TryGetValue(unit.UID, out LineRenderer marker) || marker == null || marker.transform.parent != model)
                {
                    Remove(unit.UID);
                    marker = CreateMarker(model);
                    if (marker == null) continue;
                    markers[unit.UID] = marker;
                }
                visible.Add(unit.UID);
                Transform head = unit.GetPartRoot(EUnitPartType.HeadPosition);
                Vector3 headPosition = head != null ? head.position : unit.Position + Vector3.up * 1.65f;
                marker.transform.position = headPosition + Vector3.up * (0.5f + 0.06f * Mathf.Sin(phase * 3f));
                marker.transform.rotation = Quaternion.Euler(0f, phase * 95f, 0f);
                marker.startColor = marker.endColor = color;
            }

            expired.Clear();
            foreach (ulong uid in markers.Keys)
                if (!visible.Contains(uid)) expired.Add(uid);
            foreach (ulong uid in expired) Remove(uid);
        }

        private static LineRenderer CreateMarker(Transform model)
        {
            if (material == null)
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null) return null;
                material = new Material(shader);
            }
            var marker = new GameObject("生化教师标记");
            marker.layer = model.gameObject.layer;
            marker.transform.SetParent(model, false);
            var line = marker.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.useWorldSpace = false;
            line.startWidth = line.endWidth = 0.055f;
            line.positionCount = Outline.Length;
            line.SetPositions(Outline);
            return line;
        }

        private static void Remove(ulong uid)
        {
            if (markers.TryGetValue(uid, out LineRenderer marker) && marker != null) Object.Destroy(marker.gameObject);
            markers.Remove(uid);
        }

        internal static void Clear()
        {
            foreach (LineRenderer marker in markers.Values)
                if (marker != null) Object.Destroy(marker.gameObject);
            markers.Clear();
        }

        internal static void Dispose()
        {
            Clear();
            if (material != null) Object.Destroy(material);
            material = null;
        }
    }
}
