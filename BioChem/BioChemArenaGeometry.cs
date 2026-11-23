using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BioChem
{
    internal sealed class BioChemArenaGeometry : IDisposable
    {
        private readonly List<Object> assets = new List<Object>();
        internal GameObject Root { get; }

        // 使用校园地板相同的 Default 层，原生移动探地和放人检查均接受此层。
        internal static int GroundLayer => 0;
        internal static int WallLayer => Mathf.Max(0, LayerMask.NameToLayer("Wall"));
        internal static int SolidMask => (1 << GroundLayer) | (1 << WallLayer);

        internal BioChemArenaGeometry(BioChemArenaLayout layout, Vector3 origin, ClassroomConfig school)
        {
            Root = new GameObject(BioChemArenaLayout.NameOf(layout.Map));
            Root.SetActive(false);
            try
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(Root, school.gameObject.scene);
                Root.transform.position = origin;
                var concrete = Paint(new Color(0.28f, 0.32f, 0.34f));
                var steel = Paint(new Color(0.15f, 0.20f, 0.23f));
                var teal = Paint(new Color(0.12f, 0.44f, 0.47f));
                var rust = Paint(new Color(0.59f, 0.27f, 0.14f));
                var yellow = Paint(new Color(0.9f, 0.63f, 0.18f));
                BuildBoundary(layout.Playable, concrete, steel, rust, teal, layout.Map != BioChemMap.DistilleryYard);
                if (layout.Map == BioChemMap.QuarantineDepot) BuildDepot(steel, teal, rust);
                else LoadDistillery();
                for (int i = 0; i < layout.Props.Length; i++)
                    Box("互动区标记", layout.Props[i] + Vector3.up * 0.022f, new Vector3(1.7f, 0.012f, 1.7f), yellow, -1);
                BuildSupplies(layout, steel, teal, yellow);
                AddLight(-10f, -6f, new Color(1f, 0.85f, 0.67f));
                AddLight(10f, 5f, new Color(0.69f, 0.91f, 1f));
                Root.SetActive(true);
                Physics.SyncTransforms();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void BuildBoundary(Bounds bounds, Material concrete, Material steel, Material south, Material north, bool showFloor)
        {
            Vector3 center = bounds.center;
            Vector3 size = bounds.size;
            if (showFloor)
                Box("混凝土地面", new Vector3(center.x, -0.3f, center.z), new Vector3(size.x, 0.6f, size.z), concrete, GroundLayer);
            else
                ColliderBox(new Vector3(center.x, -0.3f, center.z), new Vector3(size.x, 0.6f, size.z), 0f, GroundLayer);

            Box("西围墙", new Vector3(bounds.min.x - 0.18f, 3.1f, center.z), new Vector3(0.5f, 6.2f, size.z + 1f), steel, WallLayer);
            Box("东围墙", new Vector3(bounds.max.x + 0.18f, 3.1f, center.z), new Vector3(0.5f, 6.2f, size.z + 1f), steel, WallLayer);
            Box("南围墙", new Vector3(center.x, 3.1f, bounds.min.z - 0.18f), new Vector3(size.x + 1f, 6.2f, 0.5f), south, WallLayer);
            Box("北围墙", new Vector3(center.x, 3.1f, bounds.max.z + 0.18f), new Vector3(size.x + 1f, 6.2f, 0.5f), north, WallLayer);
        }

        private void BuildDepot(Material steel, Material teal, Material rust)
        {
            Container("西侧货柜", -11f, -2.8f, 5.6f, 2.7f, teal, steel);
            Box("西侧短箱", new Vector3(-8.5f, 0.65f, 5f), new Vector3(2.8f, 1.3f, 2.3f), rust, WallLayer);
            Box("南侧挡板", new Vector3(-2.5f, 1.1f, -4.5f), new Vector3(5.2f, 2.2f, 0.55f), steel, WallLayer);
            Container("中转货柜", 2.8f, -0.9f, 3f, 5.2f, rust, steel);
            Box("北侧挡板", new Vector3(-3.8f, 1f, 3f), new Vector3(3.5f, 2f, 0.55f), steel, WallLayer);
            Box("东侧短箱", new Vector3(15.5f, 0.75f, -2.4f), new Vector3(2.2f, 1.5f, 3.1f), teal, WallLayer);
            Box("装卸高台", new Vector3(10.6f, 1.2f, 0f), new Vector3(5.2f, 2.4f, 6f), steel, GroundLayer);
            Ramp(-9f, -3f, steel);
            Ramp(9f, 3f, steel);
            Box("高台挡板", new Vector3(12.94f, 2.87f, 0f), new Vector3(0.38f, 0.94f, 4.4f), teal, WallLayer);
            for (int x = -14; x <= 14; x += 4)
            {
                Box("教师入口地标", new Vector3(x, 0.007f, -9.1f), new Vector3(2.2f, 0.012f, 0.11f), rust, -1);
                Box("学生入口地标", new Vector3(x, 0.007f, 9.1f), new Vector3(2.2f, 0.012f, 0.11f), teal, -1);
            }
        }

        private void Ramp(float lowZ, float highZ, Material material)
        {
            Vector3 low = new Vector3(10.6f, 0f, lowZ);
            Vector3 high = new Vector3(10.6f, 2.4f, highZ);
            Quaternion rotation = Quaternion.LookRotation(high - low, Vector3.up);
            Vector3 center = (high + low) * 0.5f - rotation * Vector3.up * 0.18f;
            Box("高台坡道", center, new Vector3(5.2f, 0.36f, Vector3.Distance(high, low) + 0.08f),
                material, GroundLayer).transform.localRotation = rotation;
        }

        private void Container(string label, float x, float z, float width, float depth, Material panel, Material frame)
        {
            Box(label, new Vector3(x, 1.5f, z), new Vector3(width, 3f, depth), panel, WallLayer);
            for (int side = -1; side <= 1; side += 2)
            {
                float face = z + side * depth * 0.5f;
                Box("货柜上框", new Vector3(x, 2.88f, face), new Vector3(width, 0.22f, 0.13f), frame, -1);
                Box("货柜下框", new Vector3(x, 0.15f, face), new Vector3(width, 0.25f, 0.13f), frame, -1);
                for (float offset = 0.28f; offset < width; offset += 0.46f)
                    Box("货柜竖肋", new Vector3(x - width / 2 + offset, 1.5f, face), new Vector3(0.065f, 2.55f, 0.12f), frame, -1);
            }
        }

        private void BuildSupplies(BioChemArenaLayout layout, Material steel, Material teal, Material yellow)
        {
            foreach (Vector3 point in layout.Crates)
            {
                Box("补给箱", point + new Vector3(0f, 0.32f, 0f), new Vector3(1.6f, 0.64f, 1.02f), teal, WallLayer);
                Box("补给箱黄边", point + new Vector3(0f, 0.59f, 0f), new Vector3(1.62f, 0.07f, 1.04f), yellow, -1);
                Box("补给箱台面", point + new Vector3(0f, 0.64f, 0f), new Vector3(1.45f, 0.04f, 0.88f), steel, -1);
            }
            Vector3 machine = layout.Vending;
            Box("贩卖机", machine + new Vector3(0f, 0.9f, 0f), new Vector3(0.96f, 1.8f, 0.68f), steel, WallLayer);
            Box("贩卖机玻璃窗", machine + new Vector3(-0.09f, 1.19f, 0.35f), new Vector3(0.64f, 0.78f, 0.05f), teal, -1);
            Box("贩卖机按钮", machine + new Vector3(0.34f, 1.16f, 0.365f), new Vector3(0.11f, 0.26f, 0.07f), yellow, -1);
            Box("贩卖机出货口", machine + new Vector3(0f, 0.47f, 0.355f), new Vector3(0.71f, 0.23f, 0.07f), teal, -1);
        }

        private void LoadDistillery()
        {
            using var resource = typeof(BioChemArenaGeometry).Assembly.GetManifestResourceStream("BioChem.DistilleryYard");
            if (resource == null) throw new InvalidDataException("DLL 缺少蒸馏厂货场资源");
            using var reader = new BinaryReader(new GZipStream(resource, CompressionMode.Decompress));
            if (reader.ReadUInt32() != 0x314D4342) throw new InvalidDataException("蒸馏厂货场资源格式错误");
            var textures = new Texture2D[reader.ReadInt32()];
            for (int i = 0; i < textures.Length; i++)
            {
                int width = reader.ReadInt32();
                int height = reader.ReadInt32();
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, true);
                assets.Add(texture);
                // 资源只存原图像素；由 Unity 生成后续 mip 层。
                var pixels = new Color32[checked(width * height)];
                for (int pixel = 0; pixel < pixels.Length; pixel++)
                    pixels[pixel] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                texture.SetPixels32(pixels);
                texture.Apply(true, true);
                textures[i] = texture;
            }
            int batches = reader.ReadInt32();
            for (int i = 0; i < batches; i++)
            {
                var tint = new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                float smoothness = reader.ReadSingle();
                float metallic = reader.ReadSingle();
                int textureIndex = reader.ReadInt32();
                int vertexCount = reader.ReadInt32();
                var indices = new int[reader.ReadInt32()];
                var vertices = new Vector3[vertexCount];
                var normals = new Vector3[vertexCount];
                var uvs = new Vector2[vertexCount];
                for (int j = 0; j < vertexCount; j++)
                {
                    vertices[j] = ReadVector(reader);
                    normals[j] = ReadVector(reader);
                    uvs[j] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                }
                for (int j = 0; j < indices.Length; j++) indices[j] = reader.ReadInt32();
                if (vertexCount == 0) continue;
                var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                assets.Add(mesh);
                mesh.vertices = vertices;
                mesh.normals = normals;
                mesh.uv = uvs;
                mesh.triangles = indices;
                mesh.UploadMeshData(true);
                var batch = new GameObject("蒸馏厂场景网格");
                batch.transform.SetParent(Root.transform, false);
                batch.transform.localPosition = Vector3.down * 0.14f;
                batch.AddComponent<MeshFilter>().sharedMesh = mesh;
                Material material = Paint(tint, smoothness);
                if (material != null)
                {
                    if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic * 0.4f);
                    if (textureIndex >= 0) material.mainTexture = textures[textureIndex];
                }
                batch.AddComponent<MeshRenderer>().sharedMaterial = material;
            }
            int collisionBoxes = reader.ReadInt32();
            for (int i = 0; i < collisionBoxes; i++)
                ColliderBox(ReadVector(reader) + Vector3.down * 0.14f, ReadVector(reader), reader.ReadSingle(), WallLayer);
        }

        private static Vector3 ReadVector(BinaryReader reader) => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        private Material Paint(Color color, float smoothness = 0.28f)
        {
            Material material = WeaponModels.Tint(color, smoothness);
            if (material == null) return null;
            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * 0.26f);
            }
            assets.Add(material);
            return material;
        }

        private GameObject Box(string label, Vector3 position, Vector3 size, Material material, int layer)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = label;
            box.transform.SetParent(Root.transform, false);
            box.transform.localPosition = position;
            box.transform.localScale = size;
            if (layer >= 0) box.layer = layer;
            else box.GetComponent<Collider>().enabled = false;
            if (material != null) box.GetComponent<MeshRenderer>().sharedMaterial = material;
            return box;
        }

        private void ColliderBox(Vector3 center, Vector3 size, float yaw, int layer)
        {
            var box = new GameObject("场地碰撞") { layer = layer };
            box.transform.SetParent(Root.transform, false);
            box.transform.localPosition = center;
            box.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            box.AddComponent<BoxCollider>().size = size;
        }

        private void AddLight(float x, float z, Color color)
        {
            var lamp = new GameObject("货场照明");
            lamp.transform.SetParent(Root.transform, false);
            lamp.transform.localPosition = new Vector3(x, 6f, z);
            Light light = lamp.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 24f;
            light.intensity = 2f;
            light.color = color;
        }

        public void Dispose()
        {
            if (Root != null)
            {
                Root.SetActive(false);
                Object.Destroy(Root);
            }
            foreach (Object asset in assets) Object.Destroy(asset);
            assets.Clear();
        }
    }
}
