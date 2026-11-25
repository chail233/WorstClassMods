using UnityEngine;

namespace BioChem
{
    // 模型的刀尖或棍梢朝本地 +Z。挥击位置分别取自相机和人物，待机才跟随原挂点。
    internal sealed class MeleeSwing
    {
        // 等比缩短视模型和相机的距离，保持画面尺寸并减少贴墙时的遮挡。
        private const float ViewScale = 0.65f;
        private static readonly Vector3 ViewIdlePosition = new Vector3(0.34f, -0.30f, 0.69f) * ViewScale;
        private static readonly Quaternion ViewIdleRotation = Quaternion.LookRotation(new Vector3(-0.24f, 0.87f, 0.43f), Vector3.back);

        private readonly Unit owner;
        private Vector3 handPosition;
        private Quaternion handRotation;
        private TrailRenderer worldTrail;
        private TrailRenderer viewTrail;
        private Pose handStart;
        private Pose viewStart;
        private float startedAt;
        private float windupTime;
        private float contactTime;
        private float recoveryTime;
        private bool overhead;
        private bool active;
        private bool trailing;

        internal Transform World { get; private set; }
        internal Transform View { get; private set; }
        internal float NextBuildTryAt { get; set; }

        internal MeleeSwing(Unit owner)
        {
            this.owner = owner;
        }

        internal void BindWorld(Transform world)
        {
            if (world == null) return;
            World = world;
            handPosition = world.localPosition;
            handRotation = world.localRotation;
            handStart = new Pose(handPosition, handRotation);
            worldTrail = world.GetComponentInChildren<TrailRenderer>();
            RefreshWorldVisibility();
        }

        internal void BindView(Transform view)
        {
            if (view == null) return;
            View = view;
            View.localScale *= ViewScale;
            View.SetLocalPositionAndRotation(ViewIdlePosition, ViewIdleRotation);
            viewStart = new Pose(ViewIdlePosition, ViewIdleRotation);
            viewTrail = view.GetComponentInChildren<TrailRenderer>();
            if (viewTrail != null) viewTrail.widthMultiplier *= ViewScale;
            RefreshWorldVisibility();
        }

        internal void Play(float contactTime, float recoveryTime, bool overhead = false)
        {
            this.overhead = overhead;
            this.contactTime = contactTime;
            this.recoveryTime = recoveryTime;
            windupTime = contactTime * (overhead ? 0.58f : 0.42f);
            // 连击打断收招时，从当前位姿接上下一次起手。
            if (World != null) handStart = new Pose(World.localPosition, World.localRotation);
            if (View != null) viewStart = new Pose(View.localPosition, View.localRotation);
            startedAt = Time.unscaledTime;
            active = true;
            StopTrail();
            if (worldTrail != null) worldTrail.Clear();
            if (viewTrail != null) viewTrail.Clear();
        }

        internal void Tick(float now)
        {
            RefreshWorldVisibility();
            if (!active) return;
            float elapsed = now - startedAt;
            if (elapsed >= contactTime + recoveryTime)
            {
                active = false;
                StopTrail();
                if (World != null) World.SetLocalPositionAndRotation(handPosition, handRotation);
                if (View != null) View.SetLocalPositionAndRotation(ViewIdlePosition, ViewIdleRotation);
                return;
            }

            // 起手、挥过身前、收回，三个阶段在连接点共用同一位姿。
            bool windingUp = elapsed < windupTime;
            float arc = Mathf.SmoothStep(0f, 1f, (elapsed - windupTime) / (contactTime - windupTime));
            float reach = elapsed < contactTime
                ? Mathf.SmoothStep(0f, 1f, elapsed / windupTime)
                : Mathf.SmoothStep(1f, 0f, (elapsed - contactTime) / recoveryTime);
            Vector3 direction = overhead
                ? Arc(new Vector3(0.31f, 0.91f, 0.28f), new Vector3(-0.73f, 0.53f, 0.43f), new Vector3(-0.43f, -0.86f, 0.28f), arc)
                : Arc(new Vector3(0.82f, 0.45f, 0.35f), new Vector3(-0.56f, 0.74f, 0.20f), new Vector3(-0.89f, -0.23f, 0.39f), arc);

            if (View != null)
            {
                Vector3 front = overhead
                    ? Arc(new Vector3(0.20f, 0.19f, 0.96f), new Vector3(0.06f, 0.13f, 1.21f), new Vector3(-0.18f, -0.20f, 0.93f), arc)
                    : Arc(new Vector3(0.30f, 0.10f, 0.89f), new Vector3(0.03f, 0.08f, 1.18f), new Vector3(-0.30f, -0.09f, 0.88f), arc);
                View.SetLocalPositionAndRotation(Vector3.Lerp(windingUp ? viewStart.position : ViewIdlePosition, front * ViewScale, reach),
                    Quaternion.Slerp(windingUp ? viewStart.rotation : ViewIdleRotation, Quaternion.LookRotation(direction, Vector3.back), reach));
            }
            if (World != null && owner.transform != null)
            {
                Transform hand = World.parent;
                Quaternion facing = Quaternion.Euler(0f, owner.transform.eulerAngles.y, 0f);
                Vector3 front = overhead
                    ? Arc(new Vector3(0.33f, 1.27f, 0.54f), new Vector3(0.05f, 1.10f, 1.04f), new Vector3(-0.28f, 0.72f, 0.64f), arc)
                    : Arc(new Vector3(0.45f, 1.05f, 0.52f), new Vector3(0.04f, 1.02f, 1.05f), new Vector3(-0.44f, 0.86f, 0.62f), arc);
                // 保留手骨父级的缩放和显隐，让刀的姿态以人物朝向为基准。
                Vector3 origin = hand.TransformPoint(windingUp ? handStart.position : handPosition);
                Quaternion rotation = hand.rotation * (windingUp ? handStart.rotation : handRotation);
                World.SetPositionAndRotation(Vector3.Lerp(origin, owner.Position + facing * front, reach),
                    Quaternion.Slerp(rotation, facing * Quaternion.LookRotation(direction), reach));
            }

            if (elapsed >= contactTime) StopTrail();
            else if (!trailing && elapsed >= windupTime)
            {
                trailing = true;
                float trailTime = Mathf.Clamp(contactTime - windupTime, 0.12f, 0.20f);
                WeaponModels.EmitSlashTrail(worldTrail, trailTime);
                WeaponModels.EmitSlashTrail(viewTrail, trailTime);
            }
        }

        internal void Destroy()
        {
            if (World != null) Object.Destroy(World.gameObject);
            if (View != null) Object.Destroy(View.gameObject);
        }

        private void RefreshWorldVisibility()
        {
            // 相机的 PlayerSelf 层遮罩会随姿态切换。本机有视模型时，只画这一把武器。
            if (!owner.isLocalPlayer || World == null) return;
            bool visible = View == null || !View.gameObject.activeInHierarchy;
            if (World.gameObject.activeSelf != visible) World.gameObject.SetActive(visible);
        }

        private void StopTrail()
        {
            if (!trailing) return;
            trailing = false;
            WeaponModels.StopSlashTrail(worldTrail);
            WeaponModels.StopSlashTrail(viewTrail);
        }

        private static Vector3 Arc(Vector3 start, Vector3 control, Vector3 end, float t)
        {
            return Vector3.Lerp(Vector3.Lerp(start, control, t), Vector3.Lerp(control, end, t), t);
        }
    }
}
