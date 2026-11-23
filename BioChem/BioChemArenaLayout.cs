using UnityEngine;

namespace BioChem
{
    internal enum BioChemMap { Campus, QuarantineDepot, DistilleryYard }

    internal sealed class BioChemArenaLayout
    {
        internal readonly BioChemMap Map;
        internal readonly Bounds Playable;
        internal readonly Rect TeacherZone;
        internal readonly Rect StudentZone;
        internal readonly Vector3[] Props; // 第一项是黑板，其余是罚站柱
        internal readonly Vector3[] Crates;
        internal readonly Vector3 Vending;
        internal readonly float TeacherYaw;

        private BioChemArenaLayout(BioChemMap map, Bounds playable, Rect teacherZone, Rect studentZone,
            float teacherYaw, Vector3[] props, Vector3[] crates, Vector3 vending)
        {
            Map = map;
            Playable = playable;
            TeacherZone = teacherZone;
            StudentZone = studentZone;
            TeacherYaw = teacherYaw;
            Props = props;
            Crates = crates;
            Vending = vending;
        }

        private static readonly BioChemArenaLayout Depot = new BioChemArenaLayout(BioChemMap.QuarantineDepot,
            new Bounds(new Vector3(0f, 4f, 0f), new Vector3(36f, 12f, 28f)),
            new Rect(-8.25f, -12.9f, 16.5f, 3.3f), new Rect(-8.25f, 9.6f, 16.5f, 3.3f), 0f,
            new[]
            {
                new Vector3(-9f, 0f, -6f),  // 黑板
                new Vector3(-11f, 0f, 1f),  // 西侧货柜
                new Vector3(1f, 0f, 5.5f),  // 中庭
                new Vector3(6.4f, 0f, -6.8f), // 南坡道外侧
                new Vector3(15.5f, 0f, 5.5f), // 东围墙通道
                new Vector3(-15.5f, 0f, -6.5f), // 西南角
                new Vector3(0.65f, 0f, -8.2f),  // 南侧岔口
                new Vector3(-12.5f, 0f, 8.4f),  // 西北通道
                new Vector3(6.3f, 0f, -0.2f),   // 装卸台下方通道
            },
            new[] { new Vector3(-15f, 0f, 3.5f), new Vector3(5.8f, 0f, 5.3f) }, new Vector3(-4.8f, 0f, 7.1f));

        private static readonly BioChemArenaLayout Distillery = new BioChemArenaLayout(BioChemMap.DistilleryYard,
            new Bounds(new Vector3(0f, 5f, 2f), new Vector3(44f, 14f, 32f)),
            new Rect(-20.65f, -13.15f, 19.8f, 3.3f), new Rect(5f, 12f, 4.4f, 5.5f), 34f,
            new[]
            {
                new Vector3(-14.5f, 0f, -6f), // 黑板
                new Vector3(-9f, 0f, 1f),     // 西庭院
                new Vector3(8.8f, 0f, 3.5f),  // 东庭院
                new Vector3(-6f, 0f, -7f),    // 南侧装卸巷
                new Vector3(9f, 0f, 10f),     // 北侧仓库通道
                new Vector3(-19f, 0f, 2f),    // 西侧场边
                new Vector3(0f, 0f, -2f),     // 中央巷口
                new Vector3(2f, 0f, 6.5f),    // 北面的设备间外侧
                new Vector3(7.5f, 0f, -11.5f), // 东南仓库外侧
            },
            new[] { new Vector3(-15f, 0f, 1.7f), new Vector3(3.5f, 0f, 4.4f) }, new Vector3(-2.4f, 0f, 3f));

        internal static BioChemArenaLayout Get(BioChemMap map)
        {
            switch (map)
            {
                case BioChemMap.QuarantineDepot: return Depot;
                case BioChemMap.DistilleryYard: return Distillery;
                default: return null;
            }
        }

        internal static string NameOf(BioChemMap map)
        {
            switch (map)
            {
                case BioChemMap.QuarantineDepot: return "隔离货场";
                case BioChemMap.DistilleryYard: return "蒸馏厂货场";
                default: return "原版校园";
            }
        }
    }
}
