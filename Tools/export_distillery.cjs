// Pack the CC0 GLB into the BioChem runtime format. Run from the repository root with Node and FFmpeg.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const { spawnSync } = require('node:child_process');
const { gzipSync } = require('node:zlib');

const source = process.argv[2] ?? 'MapModels/DistilleryYard/DistilleryYard-PNG.glb';
const destination = process.argv[3] ?? 'BioChem/Maps/DistilleryYard.bin.gz';
const glb = fs.readFileSync(source);
assert.equal(glb.toString('ascii', 0, 4), 'glTF');
assert.equal(glb.readUInt32LE(4), 2);
assert.equal(glb.readUInt32LE(8), glb.length);
const jsonEnd = 20 + glb.readUInt32LE(12);
const doc = JSON.parse(glb.toString('utf8', 20, jsonEnd));
const bin = glb.subarray(jsonEnd + 8);

function accessor(index, components) {
    const acc = doc.accessors[index];
    const view = doc.bufferViews[acc.bufferView];
    assert(!acc.sparse && (acc.componentType === 5122 || acc.componentType === 5123));
    const offset = (view.byteOffset ?? 0) + (acc.byteOffset ?? 0);
    const stride = view.byteStride ?? components * 2;
    return Array.from({ length: acc.count }, (_, row) => Array.from({ length: components }, (_, column) => {
        const byte = offset + row * stride + column * 2;
        if (acc.componentType === 5123) return bin.readUInt16LE(byte) / (acc.normalized ? 65535 : 1);
        const value = bin.readInt16LE(byte);
        return acc.normalized ? Math.max(-1, value / 32767) : value;
    }));
}

function rotate(v, q = [0, 0, 0, 1]) {
    const [x, y, z, w] = q;
    const [vx, vy, vz] = v;
    const a = 2 * (y * vz - z * vy);
    const b = 2 * (z * vx - x * vz);
    const c = 2 * (x * vy - y * vx);
    return [vx + w * a + y * c - z * b, vy + w * b + z * a - x * c, vz + w * c + x * b - y * a];
}

function transform(v, chain, normal = false) {
    for (let i = chain.length - 1; i >= 0; i--) {
        const node = chain[i];
        assert(!node.matrix && (!node.scale || node.scale.every(x => x > 0)));
        const scale = node.scale ?? [1, 1, 1];
        v = rotate(v.map((x, axis) => normal ? x / scale[axis] : x * scale[axis]), node.rotation);
        if (!normal && node.translation) v = v.map((x, axis) => x + node.translation[axis]);
    }
    if (normal) {
        const length = Math.hypot(...v);
        if (length > 0) v = v.map(x => x / length);
    }
    return v;
}

const batches = doc.materials.map(() => ({ vertices: [], indices: [] }));
const boxes = [];
const root = doc.nodes[doc.scenes[doc.scene ?? 0].nodes[0]];
assert.deepEqual(root.children.length, 337);
const primitives = new Map();

for (const rootIndex of root.children) {
    const placement = doc.nodes[rootIndex];
    const kind = placement.name.replace(/^\d+-/, '');
    const doorway = kind === 'warehouse-door-pair';
    const low = [Infinity, Infinity, Infinity];
    const high = [-Infinity, -Infinity, -Infinity];
    readNode(placement, []);

    function readNode(node, chain) {
        // The starter GLB packs closed door leaves as static meshes. Leave the two passageways open.
        if (doorway && /\/door-(?:left|right)\/|door-pair-iron$/.test(node.name)) return;
        const current = [...chain, node];
        if (node.mesh !== undefined) {
            for (const primitive of doc.meshes[node.mesh].primitives) {
                assert.equal(primitive.mode ?? 4, 4);
                if (!primitives.has(primitive)) {
                    const a = primitive.attributes;
                    primitives.set(primitive, {
                        positions: accessor(a.POSITION, 3), normals: accessor(a.NORMAL, 3),
                        uvs: a.TEXCOORD_0 === undefined ? null : accessor(a.TEXCOORD_0, 2),
                        indices: accessor(primitive.indices, 1),
                    });
                }
                const data = primitives.get(primitive);
                const batch = batches[primitive.material];
                const start = batch.vertices.length / 8;
                for (let i = 0; i < data.positions.length; i++) {
                    const local = transform(data.positions[i], current.slice(1));
                    local.forEach((x, axis) => { low[axis] = Math.min(low[axis], x); high[axis] = Math.max(high[axis], x); });
                    const [x, y, z] = transform(local, [placement]);
                    const [nx, ny, nz] = transform(data.normals[i], current, true);
                    const [u, v] = data.uvs?.[i] ?? [0, 0];
                    batch.vertices.push(x, y, -z, nx, ny, -nz, u, 1 - v);
                }
                for (let i = 0; i < data.indices.length; i += 3) {
                    // Reflecting Z to Unity coordinates reverses winding.
                    batch.indices.push(start + data.indices[i][0], start + data.indices[i + 2][0], start + data.indices[i + 1][0]);
                }
            }
        }
        for (const child of node.children ?? []) readNode(doc.nodes[child], current);
    }

    const size = high.map((x, axis) => x - low[axis]);
    if (!size.every(x => x > 0.05) || size[1] < 0.5 || (placement.translation?.[1] ?? 0) > 0.5
        || /^(?:cask-hoist-rail|steam-line|pipework-run|hose-point|hose-reel)$/.test(kind)) continue;
    const center = high.map((x, axis) => (x + low[axis]) / 2);
    if (doorway) {
        const sideWidth = (size[0] - 2.56) * 0.5;
        for (const x of [low[0] + sideWidth / 2, high[0] - sideWidth / 2])
            addBox([x, center[1], center[2]], [sideWidth, size[1], size[2]]);
        addBox([center[0], (3.35 + high[1]) / 2, center[2]], [2.56, high[1] - 3.35, size[2]]);
    } else {
        addBox(center, size);
    }

    function addBox(localCenter, dimensions) {
        const [x, y, z] = transform(localCenter, [placement]);
        const q = placement.rotation ?? [0, 0, 0, 1];
        assert(q[0] === 0 && q[2] === 0 && !placement.scale);
        boxes.push([x, y, -z, ...dimensions, -2 * Math.atan2(q[1], q[3]) * 180 / Math.PI]);
    }
}

const chunks = [Buffer.from('BCM1')];
const ints = values => {
    const chunk = Buffer.alloc(values.length * 4);
    values.forEach((value, i) => chunk.writeInt32LE(value, i * 4));
    chunks.push(chunk);
};
const floats = values => {
    const chunk = Buffer.alloc(values.length * 4);
    values.forEach((value, i) => { assert(Number.isFinite(value)); chunk.writeFloatLE(value, i * 4); });
    chunks.push(chunk);
};

ints([doc.images.length]);
for (const image of doc.images) {
    const view = doc.bufferViews[image.bufferView];
    const png = bin.subarray(view.byteOffset, view.byteOffset + view.byteLength);
    assert.equal(png.toString('hex', 0, 8), '89504e470d0a1a0a');
    const width = png.readUInt32BE(16);
    const height = png.readUInt32BE(20);
    const decode = spawnSync('ffmpeg', ['-v', 'error', '-i', 'pipe:0', '-vf', 'vflip', '-frames:v', '1', '-f', 'rawvideo', '-pix_fmt', 'rgba', 'pipe:1'],
        { input: png, maxBuffer: 8 * 1024 * 1024 });
    assert.equal(decode.status, 0, decode.error?.message ?? decode.stderr?.toString());
    assert.equal(decode.stdout.length, width * height * 4);
    ints([width, height]);
    chunks.push(decode.stdout);
}

ints([batches.length]);
for (const [index, batch] of batches.entries()) {
    const material = doc.materials[index].pbrMetallicRoughness;
    const texture = material.baseColorTexture?.index;
    floats([...(material.baseColorFactor ?? [1, 1, 1, 1]), 1 - (material.roughnessFactor ?? 1), material.metallicFactor ?? 1]);
    ints([texture === undefined ? -1 : doc.textures[texture].source, batch.vertices.length / 8, batch.indices.length]);
    floats(batch.vertices);
    ints(batch.indices);
}
ints([boxes.length]);
for (const box of boxes) floats(box);

fs.mkdirSync(require('node:path').dirname(destination), { recursive: true });
const compressed = gzipSync(Buffer.concat(chunks), { level: 9 });
fs.writeFileSync(destination, compressed);
console.log(`${destination}: ${compressed.length} bytes, ${batches.reduce((sum, b) => sum + b.indices.length / 3, 0)} triangles, ${boxes.length} collision boxes`);
