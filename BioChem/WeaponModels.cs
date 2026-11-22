using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BioChem
{
    /// <summary>
    /// 武器模型共享工具（AK47/手枪/军刀/手雷共用）：运行时 OBJ 解析 + 归一化 + 材质与方块拼装小工具。
    /// OBJ 约定：文件坐标里武器口/刀尖沿 -Z（AK 打包文件的朝向），加载后按 modelYawDeg 绕 Y 旋转把正面转到 +Z，
    /// 总长（包围盒 Z 边）缩放到 normalizeLength，包围盒中心移到原点；objMuzzle 是口部在文件坐标里的位置，
    /// 传 null 则取旋转后包围盒最前端中心（通用兜底）。
    /// </summary>
    internal static class WeaponModels
    {
        internal static Transform LoadObj(string path, string rootName, Color tint, float smoothness,
            float normalizeLength, float modelYawDeg, Vector3? objMuzzle, out Vector3 muzzleLocal,
            string textureFile = null, float modelRollDeg = 0f)
        {
            var positions = new List<Vector3>();
            var uvs = new List<Vector2>();
            var normals = new List<Vector3>();
            var outVerts = new List<Vector3>();
            var outUvs = new List<Vector2>();
            var outNormals = new List<Vector3>();
            var triangles = new List<int>();
            bool hasUv = false, hasNormal = false;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                // RemoveEmptyEntries：双空格/尾空格会产生空 token（解析成零顶点角=退化三角形）
                string[] parts = line.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts[0] == "v" && parts.Length >= 4)
                {
                    positions.Add(new Vector3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])));
                }
                else if (parts[0] == "vt" && parts.Length >= 3)
                {
                    uvs.Add(new Vector2(Parse(parts[1]), Parse(parts[2])));
                }
                else if (parts[0] == "vn" && parts.Length >= 4)
                {
                    normals.Add(new Vector3(Parse(parts[1]), Parse(parts[2]), Parse(parts[3])));
                }
                else if (parts[0] == "f" && parts.Length >= 4)
                {
                    var corners = new List<int[]>(); // 每角 [v, vt, vn]，0 = 无
                    foreach (string token in parts)
                    {
                        if (token == "f") continue;
                        string[] idx = token.Split('/');
                        int v = Idx(idx[0], positions.Count);
                        int vt = idx.Length > 1 && idx[1].Length > 0 ? Idx(idx[1], uvs.Count) : 0;
                        int vn = idx.Length > 2 && idx[2].Length > 0 ? Idx(idx[2], normals.Count) : 0;
                        corners.Add(new[] { v, vt, vn });
                    }
                    // fan 三角化：每个角生成独立顶点（保留逐角 uv/normal，不做共享索引）
                    int baseIndex = outVerts.Count;
                    foreach (var c in corners)
                    {
                        outVerts.Add(c[0] > 0 ? positions[c[0] - 1] : Vector3.zero);
                        hasUv |= c[1] > 0;
                        outUvs.Add(c[1] > 0 ? uvs[c[1] - 1] : Vector2.zero);
                        hasNormal |= c[2] > 0;
                        outNormals.Add(c[2] > 0 ? normals[c[2] - 1] : Vector3.up);
                    }
                    for (int i = 1; i < corners.Count - 1; i++)
                    {
                        triangles.Add(baseIndex);
                        triangles.Add(baseIndex + i);
                        triangles.Add(baseIndex + i + 1);
                    }
                }
            }
            if (positions.Count == 0 || triangles.Count == 0)
                throw new IOException("OBJ 无有效顶点/面");

            var mesh = new Mesh { vertices = outVerts.ToArray(), triangles = triangles.ToArray(), normals = outNormals.ToArray(), uv = outUvs.ToArray() };
            if (!hasNormal || HasBrokenNormals(outNormals)) mesh.RecalculateNormals();
            if (!hasUv) mesh.uv = new Vector2[mesh.vertexCount];
            // 先旋转到统一朝向（口沿 +Z）再按 Z 长度归一化；roll=绕枪管轴正过来（转换器 PCA 次轴符号随机）
            Quaternion rotation = Quaternion.Euler(0f, modelYawDeg, 0f) * Quaternion.Euler(0f, 0f, modelRollDeg);
            var verts = mesh.vertices;
            var norms = mesh.normals;
            for (int i = 0; i < verts.Length; i++)
            {
                verts[i] = rotation * verts[i];
                norms[i] = rotation * norms[i];
            }
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.RecalculateBounds();
            Vector3 center = mesh.bounds.center;
            float scale = normalizeLength / Mathf.Max(0.01f, mesh.bounds.size.z);
            for (int i = 0; i < verts.Length; i++)
                verts[i] = (verts[i] - center) * scale; // 均匀缩放不改变已归一的法线方向
            mesh.vertices = verts;
            mesh.RecalculateBounds();
            muzzleLocal = objMuzzle.HasValue
                ? (rotation * objMuzzle.Value - center) * scale
                : new Vector3(0f, mesh.bounds.extents.y * 0.6f, mesh.bounds.max.z); // 兜底：包围盒最前端中心（y 取上半段≈枪管高度）

            var root = new GameObject(rootName);
            var child = new GameObject("mesh");
            child.transform.SetParent(root.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            // 贴图优先（raw 像素文件：int32 w + int32 h + RGB 自下而上），缺失回退单色 Tint
            Material material = null;
            string rawPath = null;
            if (!string.IsNullOrEmpty(textureFile))
            {
                rawPath = Path.ChangeExtension(textureFile, ".raw");
                if (!File.Exists(rawPath)) rawPath = null;
            }
            if (rawPath != null)
            {
                try
                {
                    var bytes = File.ReadAllBytes(rawPath);
                    int tw = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
                    int th = bytes[4] | (bytes[5] << 8) | (bytes[6] << 16) | (bytes[7] << 24);
                    var colors = new Color32[tw * th];
                    for (int p = 0; p < tw * th; p++)
                        colors[p] = new Color32(bytes[8 + p * 3], bytes[8 + p * 3 + 1], bytes[8 + p * 3 + 2], 255);
                    var texture = new Texture2D(tw, th, TextureFormat.RGBA32, false);
                    texture.SetPixels32(colors);
                    texture.Apply();
                    material = Tint(Color.white, smoothness);
                    if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
                    else if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
                }
                catch (System.Exception e)
                {
                    ModEntry.Log?.Invoke("[状态] " + rootName + " 贴图加载失败，回退单色：" + e.Message);
                }
            }
            child.AddComponent<MeshRenderer>().sharedMaterial = material ?? Tint(tint, smoothness);
            var muzzle = new GameObject("muzzle");
            muzzle.transform.SetParent(root.transform, false);
            muzzle.transform.localPosition = muzzleLocal;
            return root.transform;
        }

        // 侧视轮廓挤出成薄网格（军刀弯刃用）：outline 在 XY 平面（沿轮廓顺序），沿 Z 挤出 thickness。
        // 端面从质心 fan 三角化（军刀侧影近似星形凸），侧面逐边补 quad。
        internal static Mesh BuildExtrudedMesh(Vector2[] outline, float thickness)
        {
            Vector2 centroid = Vector2.zero;
            foreach (Vector2 p in outline) centroid += p;
            centroid /= outline.Length;
            float half = thickness * 0.5f;

            var verts = new List<Vector3>();
            var tris = new List<int>();
            // 两端面：质心 fan（前 +half / 后 -half）
            int frontBase = verts.Count;
            verts.Add(new Vector3(centroid.x, centroid.y, half));
            foreach (Vector2 p in outline) verts.Add(new Vector3(p.x, p.y, half));
            for (int i = 1; i <= outline.Length; i++)
            {
                int a = frontBase + i, b = frontBase + i % outline.Length + 1;
                tris.Add(frontBase); tris.Add(b); tris.Add(a);
            }
            int backBase = verts.Count;
            verts.Add(new Vector3(centroid.x, centroid.y, -half));
            foreach (Vector2 p in outline) verts.Add(new Vector3(p.x, p.y, -half));
            for (int i = 1; i <= outline.Length; i++)
            {
                int a = backBase + i, b = backBase + i % outline.Length + 1;
                tris.Add(backBase); tris.Add(a); tris.Add(b);
            }
            // 侧面：逐边 quad
            for (int i = 0; i < outline.Length; i++)
            {
                Vector2 a = outline[i], b = outline[(i + 1) % outline.Length];
                int v = verts.Count;
                verts.Add(new Vector3(a.x, a.y, half));
                verts.Add(new Vector3(b.x, b.y, half));
                verts.Add(new Vector3(b.x, b.y, -half));
                verts.Add(new Vector3(a.x, a.y, -half));
                tris.Add(v); tris.Add(v + 1); tris.Add(v + 2);
                tris.Add(v); tris.Add(v + 2); tris.Add(v + 3);
            }
            var mesh = new Mesh { vertices = verts.ToArray(), triangles = tris.ToArray() };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        internal static void CreatePart(Transform parent, string name, Vector3 localPos, Vector3 size, Material material, Vector3? euler = null)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(go.GetComponent<Collider>()); // 纯视觉：Collider 会挡交互/射线，必须剥掉
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(euler ?? Vector3.zero);
            go.transform.localScale = size;
            if (material != null)
                go.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        internal static Material Tint(Color color, float smoothness)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit"); // 游戏 URP 管线
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) return null; // 全找不到就留默认材质
            var material = new Material(shader);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            return material;
        }

        internal static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                SetLayerRecursive(child.gameObject, layer);
        }

        // ===== 挥刀轨迹（TrailRenderer 拖尾，近战武器共用） =====

        private static Material slashTrailMaterial;

        // 在 parent 的 localPos（刀尖）创建挥刀拖尾，默认不发射（Sprites/Default 顶点色渐隐）
        internal static TrailRenderer AttachSlashTrail(Transform parent, Vector3 localPos, Color color, float width)
        {
            if (slashTrailMaterial == null)
            {
                Shader shader = Shader.Find("Sprites/Default"); // URP 下可用（口焰 quad 同款）
                if (shader == null) return null;
                slashTrailMaterial = new Material(shader);
            }
            var go = new GameObject("slashTrail");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            TrailRenderer trail = go.AddComponent<TrailRenderer>();
            trail.material = slashTrailMaterial;
            trail.time = 0.3f;               // 每次挥刀按表现时长覆写：弧线全程可见，停手后淡出
            trail.minVertexDistance = 0.025f;
            trail.widthMultiplier = width;
            trail.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.2f)); // 头粗尾细
            trail.numCapVertices = 2;
            trail.alignment = LineAlignment.View; // 面向相机成带，任意角度都能看清
            trail.autodestruct = false;
            trail.emitting = false;
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            trail.receiveShadows = false;
            // c1 变体 facade 无 TrailRenderer.color(Gradient)：用 start/endColor 做头亮尾透明的线性渐隐
            trail.startColor = new Color(color.r, color.g, color.b, 0.9f);
            trail.endColor = new Color(color.r, color.g, color.b, 0f);
            return trail;
        }

        // 开挥：按本次挥刀时长设拖尾寿命（弧线全程可见），清掉上一段残影再开发射
        internal static void EmitSlashTrail(TrailRenderer trail, float swingDuration)
        {
            if (trail == null) return;
            trail.time = swingDuration;
            trail.Clear();
            trail.emitting = true;
        }

        internal static void StopSlashTrail(TrailRenderer trail)
        {
            if (trail == null) return;
            trail.emitting = false; // 弧线按 time 自然淡出，不瞬消
        }

        // ===== 挥刀位姿（近战武器共用） =====

        // 整刀绕腕前支点（沿刀背退 pivotDistance）刚体旋转，位置与朝向同轴同步转，刀刃贴弧线。
        // swingAxisLocal 须无 Z 分量（保持支点⊥转轴）；pivotDistance=0 退化为原地自转
        internal static void ApplySwingPose(Transform knife, Vector3 restPos, Quaternion restRot,
            float angle, float zTilt, float pivotDistance, Vector3 swingAxisLocal)
        {
            Quaternion rot = restRot * Quaternion.AngleAxis(angle, swingAxisLocal) * Quaternion.Euler(0f, 0f, zTilt);
            Quaternion delta = rot * Quaternion.Inverse(restRot);            // 相对静止姿态的刚体旋转
            Vector3 pivot = restPos + (restRot * -Vector3.forward) * pivotDistance; // 支点在刀背延长线上（垂直于转轴）
            knife.localPosition = pivot + delta * (restPos - pivot);
            knife.localRotation = rot;
        }

        // GoldSrc（CS1.6）转换的法线曾混入骨骼平移（长度可达数十而非 1，着色整体发蓝/花掉）：
        // 长度异常就放弃 OBJ 法线，改由几何重建（逐角展开的网格重建后即平面着色，符合低模观感）
        private static bool HasBrokenNormals(List<Vector3> normals)
        {
            foreach (Vector3 n in normals)
            {
                float len = n.magnitude;
                if (len < 0.5f || len > 1.5f) return true;
            }
            return false;
        }

        private static float Parse(string s)
        {
            float v;
            float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);
            return v;
        }

        private static int Idx(string s, int count) // OBJ 索引 1 基 + 负数（相对当前）
        {
            if (!int.TryParse(s, out int v)) return 0;
            return v > 0 ? (v <= count ? v : 0) : (count + v + 1 > 0 ? count + v + 1 : 0);
        }
    }
}
