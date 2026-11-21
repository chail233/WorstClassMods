# -*- coding: utf-8 -*-
"""GoldSrc (CS1.6) MDL v10 -> OBJ + 贴图 PNG（几何/UV/法线/骨骼动画还原/多贴图合并）。
用法: python -I mdl2obj.py <input.mdl> <bodypart序号> <output.obj> [tex_prefix] [bone过滤|分隔] [orient]
布局依据 GoldSrc studio.h（HLSDK cl_dll StudioModelRenderer 交叉验证）：
- studiohdr_t: name[64] 后无 checksum，length@72；numbones@140 boneindex@144；
  numtextures@180 textureindex@184 texturedataindex@188；
  numskinref@192 numskinfamilies@196 skinindex@200；numbodyparts@204 bodypartindex@208
- mstudiobone_t(112B): name[32],parent@32,flags@36,bonecontroller[6]@40,value[6]@64,scale[6]@88
- mstudiomodel_t: name[64],type,boundrad,nummesh,meshindex,numverts,vertinfoindex,vertindex,
  numnorms,norminfoindex,normindex,...（vertinfo/norminfo=每顶点 1 字节骨骼号）
- mstudiomesh_t(20B): numtris,triindex,skinref,numnorms,unused
- trivert(8B): vertindex,normindex,s,t（4 short）
- glcmd 流: short 计数（>0 条带/<0 扇形），每顶点 1 个 trivert
- mstudiotexture_t(80B): name[64],flags,w,h,dataoff；像素索引紧跟 dataoff，768 调色板跟像素后
- 动画: seqdesc.animindex 起 numbones × mstudioanim_t{offset[6]}（通道 X,Y,Z,RX,RY,RZ）；
  通道流=分块 [short: 合法数|总数<<8] + 合法数个 short 值；帧值=骨骼默认值+流值×scale；
  角度=弧度；骨骼矩阵=父链 ∘ T(pos) ∘ R(quat)（GoldSrc 行向量约定，按 HLSDK 原样实现）"""
import struct
import sys
import zlib
import math

def clean_name(b):
    return "".join(chr(c) if 32 <= c < 127 else "?" for c in b.split(b"\0")[0]).strip("? ")

def fail(msg):
    print("[失败] " + msg)
    sys.exit(1)

def write_png(path, w, h, rgb):
    def chunk(tag, d):
        return struct.pack(">I", len(d)) + tag + d + struct.pack(">I", zlib.crc32(tag + d) & 0xFFFFFFFF)
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
    stride = w * 3
    raw = b"".join(b"\x00" + rgb[y * stride:(y + 1) * stride] for y in range(h))
    open(path, "wb").write(PNG_HEAD + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))

PNG_HEAD = b"\x89PNG\r\n\x1a\n"

def write_raw(path, w, h, rgb):
    """Unity LoadRawTextureData 用：int32 w + int32 h + RGB（行序翻转为自下而上）"""
    stride = w * 3
    rows = [rgb[y * stride:(y + 1) * stride] for y in range(h)]
    body = b"".join(reversed(rows))
    open(path, "wb").write(struct.pack("<ii", w, h) + body)

def quat_to_matrix(q):
    """HLSDK QuaternionMatrix（行向量约定）：q=(x,y,z,w)"""
    x, y, z, w = q
    m = [[0.0] * 4 for _ in range(3)]
    m[0][0] = 1.0 - 2.0 * y * y - 2.0 * z * z
    m[1][0] = 2.0 * x * y + 2.0 * w * z
    m[2][0] = 2.0 * x * z - 2.0 * w * y
    m[0][1] = 2.0 * x * y - 2.0 * w * z
    m[1][1] = 1.0 - 2.0 * x * x - 2.0 * z * z
    m[2][1] = 2.0 * y * z + 2.0 * w * x
    m[0][2] = 2.0 * x * z + 2.0 * w * y
    m[1][2] = 2.0 * y * z - 2.0 * w * x
    m[2][2] = 1.0 - 2.0 * x * x - 2.0 * y * y
    return m

def concat(a, b):
    """HLSDK ConcatTransforms（行主序 3x4）：out = a*b"""
    out = [[0.0] * 4 for _ in range(3)]
    for i in range(3):
        for j in range(3):
            out[i][j] = sum(a[i][k] * b[k][j] for k in range(3))
        out[i][3] = sum(a[i][k] * b[k][3] for k in range(3)) + a[i][3]
    return out

def vector_transform(v, m):
    """HLSDK VectorTransform：out = v*m（含平移列）"""
    return [sum(v[k] * m[k][j] for k in range(3)) + m[j][3] for j in range(3)]

def vector_rotate(v, m):
    """法线变换：只乘旋转列（法线不能吃平移，混入平移会得到长度数十的垃圾向量），并归一化"""
    x = sum(v[k] * m[k][0] for k in range(3))
    y = sum(v[k] * m[k][1] for k in range(3))
    z = sum(v[k] * m[k][2] for k in range(3))
    L = math.sqrt(x * x + y * y + z * z)
    return [x / L, y / L, z / L] if L > 1e-9 else [0.0, 1.0, 0.0]

def angle_quaternion(angles):
    """HLSDK AngleQuaternion"""
    sy, cy = math.sin(angles[2] / 2), math.cos(angles[2] / 2)
    sp, cp = math.sin(angles[1] / 2), math.cos(angles[1] / 2)
    sr, cr = math.sin(angles[0] / 2), math.cos(angles[0] / 2)
    return (sr * cp * cy - cr * sp * sy,
            cr * sp * cy + sr * cp * sy,
            cr * cp * sy - sr * sp * cy,
            cr * cp * cy + sr * sp * sy)

def main():
    if len(sys.argv) < 4:
        fail("用法: python -I mdl2obj.py <input.mdl> <bodypart序号> <output.obj> [tex_prefix] [bone过滤|分隔] [orient]")
    src, bp_arg, dst = sys.argv[1], sys.argv[2], sys.argv[3]
    tex_prefix = sys.argv[4] if len(sys.argv) > 4 else dst.rsplit(".", 1)[0]
    bone_filter_arg = sys.argv[5].split("|") if len(sys.argv) > 5 and sys.argv[5] else None
    do_orient = len(sys.argv) > 6 and sys.argv[6] == "orient"
    data = open(src, "rb").read()
    n = len(data)
    if struct.unpack_from("<i", data, 0)[0] != 0x54534449:
        fail("不是 HL1 MDL（IDST）")

    # ===== 贴图表（80B：name64 flags w h dataoff） =====
    texs = []
    for base in range(76, 260, 4):
        nt, ti = struct.unpack_from("<ii", data, base)
        if not (1 <= nt <= 64 and 4 <= ti < n - 80 * nt):
            continue
        cand = []
        for i in range(nt):
            off = ti + i * 80
            nm = clean_name(data[off:off + 64])
            flags, w, h, dataoff = struct.unpack_from("<4i", data, off + 64)
            if not (16 <= w <= 4096 and 16 <= h <= 4096 and 0 < dataoff < n):
                cand = []
                break
            cand.append(dict(name=nm, w=w, h=h, dataoff=dataoff))
        if cand:
            texs = cand
            print("贴图表 @%d：%s" % (base, [t["name"] for t in texs]))
            break

    skin0 = []
    for base in range(76, 260, 4):
        a, b, c = struct.unpack_from("<3i", data, base)
        if a < 1 or not (1 <= b <= 32) or not (4 <= c < n) or a * b * 2 >= n:
            continue
        vals = struct.unpack_from("<%dh" % (a * b), data, c)
        if all(0 <= v < max(1, len(texs)) for v in vals[:min(a, 8)]):
            skin0 = list(vals[:a])
            break
    def tex_idx_of(skinref):
        return skin0[skinref] if 0 <= skinref < len(skin0) else 0

    # ===== 贴图导出：单张直出；多张横向合并为一张图集（UV 重映射到各自区间） =====
    if texs:
        if len(texs) == 1:
            t = texs[0]
            w, h = t["w"], t["h"]
            idx = data[t["dataoff"]:t["dataoff"] + w * h]
            pal = data[t["dataoff"] + w * h:t["dataoff"] + w * h + 768]
            rgb = bytearray(w * h * 3)
            for i, c in enumerate(idx):
                rgb[i * 3:i * 3 + 3] = pal[c * 3:c * 3 + 3]
            write_png("%s.png" % tex_prefix, w, h, bytes(rgb))
            write_raw("%s.raw" % tex_prefix, w, h, bytes(rgb))
            print("贴图 %dx%d → %s.png / %s.raw" % (w, h, tex_prefix, tex_prefix))
        else:
            total_w = sum(t["w"] for t in texs)
            max_h = max(t["h"] for t in texs)
            canvas = bytearray(total_w * max_h * 3)
            for i, t in enumerate(texs):
                w, h = t["w"], t["h"]
                idx = data[t["dataoff"]:t["dataoff"] + w * h]
                pal = data[t["dataoff"] + w * h:t["dataoff"] + w * h + 768]
                xoff = sum(tt["w"] for tt in texs[:i])
                for pi, c in enumerate(idx):
                    px = xoff + pi % w
                    py = pi // w
                    if py < max_h:
                        o = (py * total_w + px) * 3
                        canvas[o:o + 3] = pal[c * 3:c * 3 + 3]
                t["_xoff"] = xoff
                t["_total_w"] = float(total_w)
            atlas_path = "%s_atlas.png" % tex_prefix
            write_png(atlas_path, total_w, max_h, bytes(canvas))
            write_raw("%s_atlas.raw" % tex_prefix, total_w, max_h, bytes(canvas))
            print("合并贴图 %dx%d → %s / %s_atlas.raw" % (total_w, max_h, atlas_path, tex_prefix))

    # ===== 骨骼（112B：name32 parent flags controller[6] value[6] scale[6]） =====
    numbones, boneindex = struct.unpack_from("<2i", data, 140)
    bones = []
    bone_names = []
    if 0 < numbones <= 256 and 0 < boneindex < n:
        for i in range(numbones):
            boff = boneindex + i * 112
            nm = clean_name(data[boff:boff + 32])
            parent = struct.unpack_from("<i", data, boff + 32)[0]
            value = struct.unpack_from("<6f", data, boff + 64)
            scale = struct.unpack_from("<6f", data, boff + 88)
            bones.append(dict(name=nm, parent=parent, value=value, scale=scale))
            bone_names.append(nm)
        print("骨骼(%d)：%s" % (numbones, bone_names))

    # ===== 动画帧 0：解出每骨骼 pos/quat =====
    seqindex = struct.unpack_from("<i", data, 168)[0]
    label = clean_name(data[seqindex:seqindex + 32])
    animindex = None
    if numbones > 0:
        candidates = []
        for off in range(seqindex + 96, seqindex + 148, 4):
            V = struct.unpack_from("<i", data, off)[0]
            if not (0 < V < n - numbones * 12):
                continue
            o = struct.unpack_from("<%dH" % numbones, data, V)
            if not all(0 <= x < n - V for x in o):
                continue
            if sum(1 for x in o if x) < numbones // 4:
                continue
            candidates.append(V)
        if candidates:
            # 真表的 offset 都是小值（块紧跟表后）；取 max(offset) 最小者
            candidates.sort(key=lambda V: max(struct.unpack_from("<%dH" % numbones, data, V)))
            animindex = candidates[0]
        print("序列 %r animindex=%s（候选 %s）" % (label, animindex, candidates))

    def stream_frame0(base):
        """通道流帧 0 值：分块 [short: 合法数|总数<<8] + 合法数个 short；帧 0 取块内第 1 个"""
        hdr = struct.unpack_from("<H", data, base)[0]
        total = (hdr >> 8) & 0xFF
        if total <= 0:
            return None
        return struct.unpack_from("<h", data, base + 2)[0]

    bone_pose = {}
    for i in range(numbones):
        pos = [bones[i]["value"][j] for j in range(3)]
        ang = [bones[i]["value"][j + 3] for j in range(3)]
        if animindex:
            offs = struct.unpack_from("<6H", data, animindex + i * 12)
            for ch in range(6):
                if offs[ch] == 0:
                    continue
                v = stream_frame0(animindex + i * 12 + offs[ch])
                if v is None:
                    continue
                if ch < 3:
                    pos[ch] = bones[i]["value"][ch] + v * bones[i]["scale"][ch]
                else:
                    ang[ch - 3] = bones[i]["value"][ch] + v * bones[i]["scale"][ch]
        bone_pose[i] = (pos, ang)

    # ===== 骨骼世界矩阵（父链合成，HLSDK 行向量约定） =====
    world = {}
    for i in range(numbones):
        pos, ang = bone_pose[i]
        local = quat_to_matrix(angle_quaternion(ang))
        local[0][3], local[1][3], local[2][3] = pos
        parent = bones[i]["parent"]
        world[i] = local if parent == -1 else concat(world[parent], local)

    def pca_orient(verts, norms, muzzle_verts):
        """PCA：主轴指向 +Z（枪口侧为正），次轴指向 +Y"""
        cx = sum(v[0] for v in verts) / len(verts)
        cy = sum(v[1] for v in verts) / len(verts)
        cz = sum(v[2] for v in verts) / len(verts)
        cov = [[0.0] * 3 for _ in range(3)]
        for v in verts:
            d = (v[0] - cx, v[1] - cy, v[2] - cz)
            for i in range(3):
                for j in range(3):
                    cov[i][j] += d[i] * d[j]
        def matvec(m, v): return [sum(m[i][j] * v[j] for j in range(3)) for i in range(3)]
        def rayleigh(m, e): return sum(e[i] * matvec(m, e)[i] for i in range(3))
        e = [1.0, 0.0, 0.0]
        for _ in range(300):
            e2 = matvec(cov, e)
            L = math.sqrt(sum(x * x for x in e2))
            e = [x / L for x in e2]
        C = (cx, cy, cz)
        if muzzle_verts:
            proj = sum(sum((v[i] - C[i]) * e[i] for i in range(3)) for v in muzzle_verts)
            if proj < 0: e = [-x for x in e]
        cov2 = [row[:] for row in cov]
        lam = rayleigh(cov, e)
        for i in range(3):
            for j in range(3):
                cov2[i][j] -= e[i] * e[j] * lam
        f = [0.0, 1.0, 0.0]
        for _ in range(300):
            f2 = matvec(cov2, f)
            L = math.sqrt(sum(x * x for x in f2))
            f = [x / L for x in f2]
        f = [f[i] - sum(f[i] * e[i] for i in range(3)) * e[i] for i in range(3)]
        L = math.sqrt(sum(x * x for x in f)); f = [x / L for x in f]
        g = [e[1] * f[2] - e[2] * f[1], e[2] * f[0] - e[0] * f[2], e[0] * f[1] - e[1] * f[0]]
        def tf(v):
            d = (v[0] - C[0], v[1] - C[1], v[2] - C[2])
            return (sum(d[i] * g[i] for i in range(3)), sum(d[i] * f[i] for i in range(3)), sum(d[i] * e[i] for i in range(3)))
        return [tf(v) for v in verts], [tf(v) for v in norms]

    # ===== bodypart/模型/网格 =====
    bodypart_base = None
    for base in range(76, 260, 4):
        nb, bpi = struct.unpack_from("<ii", data, base)
        if not (1 <= nb <= 16 and 4 <= bpi < n - 200):
            continue
        try:
            nummodels, basei, mi0 = struct.unpack_from("<iii", data, bpi + 64)
            if not (1 <= nummodels <= 8 and 0 <= basei <= 4096 and 0 < mi0 < n - 104):
                continue
            mtype, _, nummesh, meshindex, numverts, vertinfo, vertindex = struct.unpack_from("<i f i i i i i", data, mi0 + 64)
            if not (0 < nummesh <= 32 and 0 < numverts < 20000 and 0 < vertindex < n):
                continue
            bodypart_base = (base, nb, bpi)
            break
        except struct.error:
            continue
    if not bodypart_base:
        fail("定位 bodypart 表失败")
    _, numbodyparts, bodypartindex = bodypart_base
    bp_names = [clean_name(data[bodypartindex + i * 76:bodypartindex + i * 76 + 64]) for i in range(numbodyparts)]
    print("bodypart 表 @%d：%s" % (bodypartindex, bp_names))
    bp_list = list(range(numbodyparts)) if bp_arg == "all" else [int(bp_arg)]
    for bp in bp_list:
        if not (0 <= bp < numbodyparts):
            fail("bodypart 序号越界")

    for bp in bp_list:
        boff = bodypartindex + bp * 76
        nummodels, _, modelindex0 = struct.unpack_from("<iii", data, boff + 64)
        bp_verts, bp_norms, bp_uvs, bp_tris = [], [], [], []
        for k in range(nummodels):
            moff = modelindex0 + k * 104
            mname = clean_name(data[moff:moff + 64])
            _, _, nummesh, meshindex, numverts, vertinfo, vertindex, numnorms, norminfo, normindex = \
                struct.unpack_from("<i f i i i i i i i i", data, moff + 64)
            if not (0 < nummesh <= 32 and 0 < numverts < 20000 and 0 < vertindex + numverts * 12 <= n):
                print("[跳过] 模型 '%s' 非法" % mname)
                continue
            vbone = list(data[vertinfo:vertinfo + numverts]) if 0 < vertinfo < n else [0] * numverts
            if any(b >= max(1, numbones) for b in vbone):
                vbone = list(struct.unpack_from("<%dh" % numverts, data, vertinfo))
            verts = [vector_transform(struct.unpack_from("<3f", data, vertindex + i * 12), world[vbone[i]])
                     for i in range(numverts)]
            norms = []
            if 0 < normindex < n and numnorms > 0:
                nbone = list(data[norminfo:norminfo + numnorms]) if 0 < norminfo < n else [0] * numnorms
                if any(b >= max(1, numbones) for b in nbone):
                    nbone = list(struct.unpack_from("<%dh" % numnorms, data, norminfo))
                norms = [vector_rotate(struct.unpack_from("<3f", data, normindex + i * 12), world[nbone[i]])
                         for i in range(min(numnorms, len(nbone)))]
            print("模型 '%s': nummesh=%d numverts=%d（已按骨骼世界变换）" % (mname, nummesh, numverts))
            keep = None
            muzzle_verts = None
            if bone_names and bone_filter_arg:
                keep = set(i for i, bn in enumerate(bone_names)
                           if any(sub.lower() in bn.lower() for sub in bone_filter_arg))
                print("  骨骼过滤保留：%s → %d/%d 顶点" %
                      ([bone_names[i] for i in sorted(keep)], sum(1 for b in vbone if b in keep), numverts))
                muzzle_verts = [verts[i] for i in range(numverts)
                                if i < len(vbone) and any(sub.lower() in bone_names[vbone[i]].lower()
                                                          for sub in ("slide", "bolt", "front", "muzzle"))]
            if do_orient and muzzle_verts:
                verts, norms = pca_orient(verts, norms, muzzle_verts)
            for i in range(nummesh):
                numtris, triindex, skinref = struct.unpack_from("<3i", data, meshindex + i * 20)
                if not (0 < numtris < 20000 and 4 <= triindex < n):
                    print("  [警告] mesh[%d] 非法" % i)
                    continue
                tex_i = tex_idx_of(skinref)
                tex = texs[tex_i] if tex_i < len(texs) else None
                pos = triindex
                got = 0
                guard = 0
                mesh_tris = []
                while got < numtris and guard < 3000:
                    guard += 1
                    cnt = struct.unpack_from("<h", data, pos)[0]; pos += 2
                    if cnt == 0: continue
                    run = []
                    for _ in range(abs(cnt)):
                        vi, ni, s, t = struct.unpack_from("<4h", data, pos); pos += 8
                        run.append((vi, ni, s, t))
                    add = abs(cnt) - 2
                    if add < 0: break
                    if cnt > 0:
                        for k in range(add):
                            mesh_tris.append((run[k], run[k + 2], run[k + 1]) if k % 2 == 0 else (run[k], run[k + 1], run[k + 2]))
                    else:
                        for k in range(add):
                            mesh_tris.append((run[0], run[k + 2], run[k + 1]))
                    got += add
                if abs(got - numtris) > 2:
                    print("  [警告] mesh[%d] 三角数 %d != %d" % (i, got, numtris))
                if keep is not None:
                    mesh_tris = [t for t in mesh_tris if all(vbone[e[0]] in keep for e in t)]
                print("  mesh[%d]: tris=%d skinref=%d → 贴图 %d" % (i, len(mesh_tris), skinref, tex_i))
                base_v = len(bp_verts)
                base_n = len(bp_norms)
                bp_verts.extend(verts)
                bp_norms.extend(norms)
                for (e0, e1, e2) in mesh_tris:
                    ids = []
                    for (vi, ni, s, t) in (e0, e1, e2):
                        ids.append(len(bp_uvs))
                        if tex:
                            u = (s + tex.get("_xoff", 0)) / tex.get("_total_w", float(tex["w"]))
                            bp_uvs.append((u, 1.0 - t / max(1, tex["h"])))
                        else:
                            bp_uvs.append((0.0, 0.0))
                    bp_tris.append((base_v + e0[0], ids[0], base_n + e0[1],
                                    base_v + e1[0], ids[1], base_n + e1[1],
                                    base_v + e2[0], ids[2], base_n + e2[1]))
        if not bp_tris:
            print("[跳过] bodypart %d 无几何" % bp)
            continue
        xs = [v[0] for v in bp_verts]; ys = [v[1] for v in bp_verts]; zs = [v[2] for v in bp_verts]
        print("bodypart %d 导出：%d 顶点 / %d 三角 / bbox=(%.2f, %.2f, %.2f)" %
              (bp, len(bp_verts), len(bp_tris), max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs)))
        obj_path = dst if len(bp_list) == 1 else dst.replace(".obj", "_bp%d.obj" % bp)
        with open(obj_path, "w") as f:
            for v in bp_verts:
                f.write("v %.6f %.6f %.6f\n" % tuple(v))
            for nv in bp_norms:
                f.write("vn %.4f %.4f %.4f\n" % tuple(nv))
            for uv in bp_uvs:
                f.write("vt %.6f %.6f\n" % tuple(uv))
            for t in bp_tris:
                f.write("f %d/%d/%d %d/%d/%d %d/%d/%d\n" % (t[0] + 1, t[1] + 1, t[2] + 1, t[3] + 1, t[4] + 1, t[5] + 1, t[6] + 1, t[7] + 1, t[8] + 1))
        print("已写出 " + obj_path)

if __name__ == "__main__":
    main()
