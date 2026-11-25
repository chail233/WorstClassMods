using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace BioChem
{
    public struct BioChemDamageScoreMessage : NetworkMessage
    {
        public ulong Attacker;
        public int Damage;
        public long Total;
        public Vector3 Position;
    }

    internal static class BioChemDamageScore
    {
        private const float PopTime = 0.13f;
        private const float FlightTime = 0.50f;

        private struct Flight
        {
            internal int Points;
            internal Vector2 Origin;
            internal float StartedAt;
        }

        private static readonly Dictionary<ulong, long> scores = new Dictionary<ulong, long>();
        private static readonly List<Unit> rankedPlayers = new List<Unit>();
        private static readonly List<Flight> flights = new List<Flight>();
        private static long flyingPoints;
        private static bool dirty;
        private static UIBattleMainView lastView;
        private static bool lastEnabled;
        private static EClassroomState lastState;
        private static RectTransform localScoreText;
        private static GUIStyle flyStyle;

        internal static void Install(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(UIBattleMainView), nameof(UIBattleMainView.RefreshRank)),
                prefix: new HarmonyMethod(typeof(BioChemDamageScore), nameof(BeforeRefreshRank)));
        }

        internal static void Award(ulong attacker, ulong target, int damage)
        {
            if (damage <= 0 || attacker == target || !NetRuntime.IsServer || !UnitManager.IsSingletonActive()) return;
            Unit shooter = UnitManager.Instance.GetUnitByUID(attacker);
            Unit victim = UnitManager.Instance.GetUnitByUID(target);
            if (shooter == null || !shooter.isPlayer || victim == null) return;

            var message = new BioChemDamageScoreMessage
            {
                Attacker = attacker, Damage = damage, Total = scores.GetValueOrDefault(attacker) + damage,
                Position = victim.Position + Vector3.up * 1.45f
            };
            Apply(message);
            RoomManager.Instance.BroadcastToAllExceptHost(message);
        }

        internal static void OnClientScore(BioChemDamageScoreMessage message)
        {
            if (!NetworkServer.active && ModEntry.enabled) Apply(message);
        }

        private static void Apply(BioChemDamageScoreMessage message)
        {
            if (message.Total <= scores.GetValueOrDefault(message.Attacker)) return;
            scores[message.Attacker] = message.Total;
            dirty = true;
            if (message.Attacker != LocalUID || message.Damage <= 0) return;

            HunterFeedback.ShowDamage(message.Position, message.Damage);
            Camera camera = SceneManager.IsSingletonActive() ? SceneManager.Instance.MainCamera : null;
            Vector3 screen = camera != null ? camera.WorldToScreenPoint(message.Position) : Vector3.zero;
            Vector2 origin = screen.z > 0
                ? new Vector2(Mathf.Clamp(screen.x, 24f, Screen.width - 24f),
                    Mathf.Clamp(Screen.height - screen.y, 24f, Screen.height - 24f))
                : new Vector2(Screen.width * 0.50f, Screen.height * 0.58f);
            flights.Add(new Flight { Points = message.Damage, Origin = origin, StartedAt = Time.unscaledTime });
            flyingPoints += message.Damage;
        }

        internal static void WriteState(BioChemStateDto dto)
        {
            if (scores.Count == 0) return;
            dto.damageScoreUids = new long[scores.Count];
            dto.damageScoreTotals = new long[scores.Count];
            int index = 0;
            foreach (var pair in scores)
            {
                dto.damageScoreUids[index] = (long)pair.Key;
                dto.damageScoreTotals[index++] = pair.Value;
            }
        }

        internal static void ReadState(BioChemStateDto dto)
        {
            if (!dto.enabled || dto.damageScoreUids == null || dto.damageScoreTotals == null
                || dto.damageScoreUids.Length != dto.damageScoreTotals.Length)
            {
                Reset();
                return;
            }
            scores.Clear();
            for (int i = 0; i < dto.damageScoreUids.Length; i++)
                scores[(ulong)dto.damageScoreUids[i]] = dto.damageScoreTotals[i];
            dirty = true;
        }

        internal static void Reset()
        {
            scores.Clear();
            flights.Clear();
            flyingPoints = 0;
            localScoreText = null;
            dirty = true;
        }

        internal static void RemovePlayer(ulong uid)
        {
            if (scores.Remove(uid)) dirty = true;
        }

        private static ulong LocalUID => UnitManager.IsSingletonActive() ? UnitManager.Instance.localPlayerUID : 0;

        internal static void Tick()
        {
            if (!ModEntry.enabled && !lastEnabled) return;
            float now = Time.unscaledTime;
            for (int i = flights.Count - 1; i >= 0; i--)
            {
                Flight flight = flights[i];
                if (now - flight.StartedAt < PopTime + FlightTime) continue;
                flyingPoints -= flight.Points;
                flights.RemoveAt(i);
                dirty = true;
            }

            var view = SingletonT<UIManager>.Instance?.GetOpenUI(EUIWindowType.BattleMain) as UIBattleMainView;
            if (view == null || !view.isActiveAndEnabled)
            {
                lastView = null;
                localScoreText = null;
                return;
            }
            var state = ClassroomManager.IsSingletonActive() ? ClassroomManager.Instance.curState : EClassroomState.BeforeClass;
            if (lastView == view && lastEnabled == ModEntry.enabled && lastState == state && !dirty) return;
            if (ModEntry.enabled || (lastView == view && lastEnabled))
                view.RefreshRank();
            lastView = view;
            lastEnabled = ModEntry.enabled;
            lastState = state;
            dirty = false;
        }

        private static bool BeforeRefreshRank(UIBattleMainView __instance)
        {
            if (!ModEntry.enabled) return true;
            var list = __instance.rankListCtrl;
            if (list == null) return false;
            localScoreText = null;
            rankedPlayers.Clear();
            var state = ClassroomManager.IsSingletonActive() ? ClassroomManager.Instance.curState : EClassroomState.BeforeClass;
            if ((state == EClassroomState.Classing || state == EClassroomState.ClassFinish) && UnitManager.IsSingletonActive())
            {
                foreach (Unit unit in UnitManager.Instance.playerList)
                    if (unit != null && unit.isPlayer && scores.ContainsKey(unit.UID)) rankedPlayers.Add(unit);
            }

            ulong self = LocalUID;
            long DisplayedScore(ulong uid) => System.Math.Max(0, scores[uid] - (uid == self ? flyingPoints : 0));
            rankedPlayers.Sort((a, b) =>
            {
                int byScore = DisplayedScore(b.UID).CompareTo(DisplayedScore(a.UID));
                return byScore != 0 ? byScore : a.UID.CompareTo(b.UID);
            });
            list.SafeSetActive(rankedPlayers.Count > 0);
            for (int i = 0; i < rankedPlayers.Count; i++)
            {
                if (!(list.GetCtrlByIdx(i) is UIRankItem row)) continue;
                Unit unit = rankedPlayers[i];
                row.SafeSetActive(true);
                row.SetInfo(i, unit.GetPlayerUnitName(), 0f);
                row.scoreText.text = DisplayedScore(unit.UID) + " 分";
                if (unit.UID == self) localScoreText = row.scoreText.rectTransform;
            }
            for (int i = rankedPlayers.Count; i < list.m_Lables.Count; i++)
                list.m_Lables[i].SafeSetActive(false);
            return false;
        }

        internal static void DrawFlights()
        {
            if (Event.current.type != EventType.Repaint || flights.Count == 0
                || localScoreText == null || !localScoreText.gameObject.activeInHierarchy) return;
            Canvas canvas = localScoreText.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            Camera uiCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 dest = RectTransformUtility.WorldToScreenPoint(uiCamera, localScoreText.TransformPoint(localScoreText.rect.center));
            dest.y = Screen.height - dest.y;
            if (flyStyle == null)
                flyStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            foreach (Flight flight in flights)
            {
                float age = Time.unscaledTime - flight.StartedAt;
                float t = Mathf.Clamp01((age - PopTime) / FlightTime);
                Vector2 pos = Vector2.Lerp(flight.Origin + Vector2.down * 19f, dest, Mathf.SmoothStep(0f, 1f, t));
                pos.y -= Mathf.Sin(t * Mathf.PI) * Screen.height * 0.055f;
                flyStyle.fontSize = Mathf.RoundToInt(Mathf.Lerp(24f, 15f, t) * Mathf.Clamp(Screen.height / 1080f, 0.75f, 1.5f));
                string label = "+" + flight.Points;
                flyStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(pos.x - 79f, pos.y - 19f, 160f, 40f), label, flyStyle);
                flyStyle.normal.textColor = new Color(1f, 0.78f, 0.32f);
                GUI.Label(new Rect(pos.x - 80f, pos.y - 20f, 160f, 40f), label, flyStyle);
            }
        }
    }
}
