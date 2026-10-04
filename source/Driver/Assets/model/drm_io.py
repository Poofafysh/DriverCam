"""Driver runtime formats (.drm model, .dra clips): writer + reader. Plain Python (no bpy), so it also runs as a checker:

    python drm_io.py driver.drm driver_anims.dra      -> prints a summary and validates every chunk

All values little-endian, floats are float32, strings are u8 length + ASCII. Space = Unity (x right, y up, z forward,
metres), already converted from Blender by the build script (C = [[-1,0,0],[0,0,1],[0,-1,0]]; triangles are written in
Unity winding, i.e. Blender's (0,2,1) order, the same flip DriverCam's cockpit export does).

driver.drm
  "DRM1" u16 version=1 u16 flags=0, then chunks [4CC][u32 len][payload]; readers skip unknown tags.
  SKEL  u16 n; per bone (parents before children): str name, i16 parent(-1), f3 restLocalPos, f4 restLocalRot(xyzw),
        f3 restLocalScale
  SOCK  u16 n; per: str name, u16 bone, f3 pos, f4 rot (bone-local)
  MATL  u8 n; per slot: str name, str texture, f4 baseColor, f smoothness, f metallic, f3 emission, str emissionTex,
        u8 flags (1 = outline, 2 = alphaClip)
  MESH  one chunk per LOD: u8 lod, u32 nVerts (<= 65535), u32 nIdx, u8 nSub, (u32 start, u32 count, u8 matSlot) * nSub,
        f3 pos[], f3 nrm[], f2 uv[], u8x4 boneIdx[], u8x4 weight[] (sum 255), u16 idx[]   (mesh space = Driver_Root)
  MASK  one chunk per LOD: u8 lod, u32 nVerts, u8 flags[nVerts]  bit0 = head/neck/collar (a triangle with ANY bit0
        vertex is left out of the driver-view variant), bit1 = finger
  RIGD  u8 n; per: str name, u16 bone, u8 matSlot, u16 nVerts, f3 pos[], f3 nrm[], f2 uv[], u32 nIdx, u16 idx[]
        (bone-local; names ending in "_lod1" belong to LOD1)
  META  UTF-8 "key=value" lines

driver_anims.dra
  "DRA1" u16 version=1; u16 nClips; per clip: str name, f fps, u16 nFrames, u8 flags (1 loop, 2 additive,
  4 pelvisTranslation), u16 nTracks, per track: str boneName, u8 channels (1 rot, 2 pos);
  then frame-major data: for each frame, for each track: f4 rot (xyzw) if channels&1, f3 pos if channels&2.
  Non-additive rot/pos are full parent-relative local values. Additive rot is a bone-local delta: local = base * delta.
"""
import struct, sys

MAGIC_DRM, MAGIC_DRA = b"DRM1", b"DRA1"


class W:
    def __init__(s): s.b = bytearray()
    def u8(s, v): s.b += struct.pack("<B", v)
    def u16(s, v): s.b += struct.pack("<H", v)
    def i16(s, v): s.b += struct.pack("<h", v)
    def u32(s, v): s.b += struct.pack("<I", v)
    def f(s, *v): s.b += struct.pack("<%df" % len(v), *v)
    def s(s_, t):
        e = t.encode("ascii"); assert len(e) < 256, t
        s_.u8(len(e)); s_.b += e


class R:
    def __init__(s, b, o=0): s.b = b; s.o = o
    def _(s, fmt):
        v = struct.unpack_from("<" + fmt, s.b, s.o); s.o += struct.calcsize("<" + fmt); return v
    def u8(s): return s._("B")[0]
    def u16(s): return s._("H")[0]
    def i16(s): return s._("h")[0]
    def u32(s): return s._("I")[0]
    def f(s, n=1): v = s._("%df" % n); return v if n > 1 else v[0]
    def s(s_): n = s_.u8(); t = s_.b[s_.o:s_.o + n].decode("ascii"); s_.o += n; return t


def _chunk(out, tag, w):
    out += tag.encode("ascii") + struct.pack("<I", len(w.b)) + w.b


def write_drm(path, skel, sockets, mats, meshes, masks, rigids, meta):
    """skel [(name, parent, pos3, rot4, scl3)], sockets [(name, bone, pos3, rot4)], mats [dict], meshes [dict(lod, pos,
    nrm, uv, bi, bw, idx, subs[(start,count,slot)])], masks [(lod, flags)], rigids [dict(name,bone,slot,pos,nrm,uv,idx)]"""
    out = bytearray(MAGIC_DRM + struct.pack("<HH", 1, 0))
    w = W(); w.u16(len(skel))
    for name, parent, p, q, sc in skel:
        w.s(name); w.i16(parent); w.f(*p); w.f(*q); w.f(*sc)
    _chunk(out, "SKEL", w)
    w = W(); w.u16(len(sockets))
    for name, bone, p, q in sockets:
        w.s(name); w.u16(bone); w.f(*p); w.f(*q)
    _chunk(out, "SOCK", w)
    w = W(); w.u8(len(mats))
    for m in mats:
        w.s(m["name"]); w.s(m["tex"]); w.f(*m["color"]); w.f(m["smooth"]); w.f(m["metal"]); w.f(*m["emission"])
        w.s(m["emit_tex"]); w.u8(m["flags"])
    _chunk(out, "MATL", w)
    for m in meshes:
        n = len(m["pos"]); assert n <= 65535
        w = W(); w.u8(m["lod"]); w.u32(n); w.u32(len(m["idx"])); w.u8(len(m["subs"]))
        for st, cnt, slot in m["subs"]: w.u32(st); w.u32(cnt); w.u8(slot)
        for v in m["pos"]: w.f(*v)
        for v in m["nrm"]: w.f(*v)
        for v in m["uv"]: w.f(*v)
        for v in m["bi"]: w.b += bytes(v)
        for v in m["bw"]: w.b += bytes(v)
        w.b += struct.pack("<%dH" % len(m["idx"]), *m["idx"])
        _chunk(out, "MESH", w)
    for lod, flags in masks:
        w = W(); w.u8(lod); w.u32(len(flags)); w.b += bytes(flags); _chunk(out, "MASK", w)
    w = W(); w.u8(len(rigids))
    for r in rigids:
        w.s(r["name"]); w.u16(r["bone"]); w.u8(r["slot"]); w.u16(len(r["pos"]))
        for v in r["pos"]: w.f(*v)
        for v in r["nrm"]: w.f(*v)
        for v in r["uv"]: w.f(*v)
        w.u32(len(r["idx"])); w.b += struct.pack("<%dH" % len(r["idx"]), *r["idx"])
    _chunk(out, "RIGD", w)
    w = W(); w.b += "\n".join("%s=%s" % kv for kv in meta).encode("utf-8"); _chunk(out, "META", w)
    open(path, "wb").write(out)
    return len(out)


def read_drm(path):
    b = open(path, "rb").read()
    assert b[:4] == MAGIC_DRM, "bad magic"
    ver, flags = struct.unpack_from("<HH", b, 4)
    o = 8; d = {"version": ver, "meshes": [], "masks": [], "unknown": []}
    while o < len(b):
        tag = b[o:o + 4].decode("ascii"); ln = struct.unpack_from("<I", b, o + 4)[0]; r = R(b[o + 8:o + 8 + ln]); o += 8 + ln
        if tag == "SKEL":
            d["skel"] = [(r.s(), r.i16(), r.f(3), r.f(4), r.f(3)) for _ in range(r.u16())]
        elif tag == "SOCK":
            d["sockets"] = [(r.s(), r.u16(), r.f(3), r.f(4)) for _ in range(r.u16())]
        elif tag == "MATL":
            d["mats"] = [dict(name=r.s(), tex=r.s(), color=r.f(4), smooth=r.f(), metal=r.f(), emission=r.f(3),
                              emit_tex=r.s(), flags=r.u8()) for _ in range(r.u8())]
        elif tag == "MESH":
            lod, n, ni, ns = r.u8(), r.u32(), r.u32(), r.u8()
            subs = [(r.u32(), r.u32(), r.u8()) for _ in range(ns)]
            pos = [r.f(3) for _ in range(n)]; nrm = [r.f(3) for _ in range(n)]; uv = [r.f(2) for _ in range(n)]
            bi = [tuple(r._("4B")) for _ in range(n)]; bw = [tuple(r._("4B")) for _ in range(n)]
            idx = list(r._("%dH" % ni))
            d["meshes"].append(dict(lod=lod, pos=pos, nrm=nrm, uv=uv, bi=bi, bw=bw, idx=idx, subs=subs))
        elif tag == "MASK":
            lod, n = r.u8(), r.u32(); d["masks"].append((lod, list(r._("%dB" % n))))
        elif tag == "RIGD":
            rs = []
            for _ in range(r.u8()):
                name, bone, slot, n = r.s(), r.u16(), r.u8(), r.u16()
                pos = [r.f(3) for _ in range(n)]; nrm = [r.f(3) for _ in range(n)]; uv = [r.f(2) for _ in range(n)]
                ni = r.u32(); idx = list(r._("%dH" % ni))
                rs.append(dict(name=name, bone=bone, slot=slot, pos=pos, nrm=nrm, uv=uv, idx=idx))
            d["rigids"] = rs
        elif tag == "META":
            d["meta"] = dict(l.split("=", 1) for l in r.b.decode("utf-8").splitlines() if "=" in l)
        else:
            d["unknown"].append(tag)
    return d


def write_dra(path, clips):
    """clips [dict(name, fps, flags, tracks[(bone, channels)], frames[[per track (rot4 or None, pos3 or None)]])]"""
    w = W(); w.b += MAGIC_DRA; w.u16(1); w.u16(len(clips))
    for c in clips:
        w.s(c["name"]); w.f(c["fps"]); w.u16(len(c["frames"])); w.u8(c["flags"]); w.u16(len(c["tracks"]))
        for bone, ch in c["tracks"]: w.s(bone); w.u8(ch)
        for fr in c["frames"]:
            for (bone, ch), (q, p) in zip(c["tracks"], fr):
                if ch & 1: w.f(*q)
                if ch & 2: w.f(*p)
    open(path, "wb").write(w.b)
    return len(w.b)


def read_dra(path):
    r = R(open(path, "rb").read())
    assert r.b[:4] == MAGIC_DRA; r.o = 4
    ver, n = r.u16(), r.u16(); clips = []
    for _ in range(n):
        name, fps, nf, flags, nt = r.s(), r.f(), r.u16(), r.u8(), r.u16()
        tracks = [(r.s(), r.u8()) for _ in range(nt)]
        frames = []
        for _f in range(nf):
            fr = []
            for bone, ch in tracks:
                q = r.f(4) if ch & 1 else None; p = r.f(3) if ch & 2 else None; fr.append((q, p))
            frames.append(fr)
        clips.append(dict(name=name, fps=fps, flags=flags, tracks=tracks, frames=frames))
    assert r.o == len(r.b), "trailing bytes"
    return clips


# ---------------------------------------------------------------------------------------------- checker (plain Python)
def _qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz)


def _qrot(q, v):
    x, y, z, w_ = q; vx, vy, vz = v
    tx, ty, tz = 2 * (y * vz - z * vy), 2 * (z * vx - x * vz), 2 * (x * vy - y * vx)
    return (vx + w_ * tx + y * tz - z * ty, vy + w_ * ty + z * tx - x * tz, vz + w_ * tz + x * ty - y * tx)


def check(drm_path, dra_path=None):
    d = read_drm(drm_path); errs = []
    skel = d["skel"]; nb = len(skel)
    for i, (name, p, pos, q, sc) in enumerate(skel):
        if p >= i: errs.append("bone %s parent order" % name)
        if abs(sum(c * c for c in q) - 1) > 1e-3: errs.append("bone %s rot not unit" % name)
    # world rest positions (for a quick height check)
    wp, wq = [], []
    for name, p, pos, q, sc in skel:
        if p < 0: wp.append(pos); wq.append(q)
        else:
            r = _qrot(wq[p], pos); wp.append(tuple(a + b for a, b in zip(wp[p], r))); wq.append(_qmul(wq[p], q))
    names = [s[0] for s in skel]
    for m in d["meshes"]:
        n = len(m["pos"])
        if max(m["idx"]) >= n: errs.append("LOD%d index out of range" % m["lod"])
        if len(m["idx"]) % 3: errs.append("LOD%d idx not tris" % m["lod"])
        for k, (bi, bw) in enumerate(zip(m["bi"], m["bw"])):
            if sum(bw) != 255: errs.append("LOD%d vert %d weights sum %d" % (m["lod"], k, sum(bw))); break
            if any(b >= nb for b in bi): errs.append("LOD%d vert %d bone idx" % (m["lod"], k)); break
        tot = sum(c for _, c, _ in m["subs"])
        if tot != len(m["idx"]): errs.append("LOD%d submesh counts" % m["lod"])
        for v in m["nrm"]:
            if abs(sum(c * c for c in v) - 1) > 1e-2: errs.append("LOD%d normal not unit" % m["lod"]); break
        ys = [v[1] for v in m["pos"]]
        used = sorted({names[b] for bi, bw in zip(m["bi"], m["bw"]) for b, x in zip(bi, bw) if x})
        print("  MESH lod%d: %5d verts %5d tris, %d submesh, y %.3f..%.3f, %d bones used" %
              (m["lod"], n, len(m["idx"]) // 3, len(m["subs"]), min(ys), max(ys), len(used)))
    for lod, fl in d["masks"]:
        m = [x for x in d["meshes"] if x["lod"] == lod][0]
        if len(fl) != len(m["pos"]): errs.append("MASK lod%d length" % lod)
        print("  MASK lod%d: head %d, finger %d" % (lod, sum(1 for f in fl if f & 1), sum(1 for f in fl if f & 2)))
    for r in d.get("rigids", []):
        if max(r["idx"]) >= len(r["pos"]): errs.append("RIGD %s idx" % r["name"])
        print("  RIGD %-12s bone %-6s slot %d: %4d verts %4d tris" % (r["name"], names[r["bone"]], r["slot"],
                                                                     len(r["pos"]), len(r["idx"]) // 3))
    print("  SKEL %d bones; head joint y %.3f; sockets %s" % (nb, wp[names.index("head")][1],
                                                            ", ".join(s[0] for s in d["sockets"])))
    print("  MATL " + ", ".join("%s(%s)" % (m["name"], m["tex"]) for m in d["mats"]))
    print("  META " + ", ".join("%s=%s" % kv for kv in d.get("meta", {}).items()))
    if dra_path:
        for c in read_dra(dra_path):
            miss = [b for b, _ in c["tracks"] if b not in names]
            if miss: errs.append("clip %s unknown bones %s" % (c["name"], miss))
            for fr in c["frames"]:
                for q, p in fr:
                    if q is not None and abs(sum(x * x for x in q) - 1) > 1e-3: errs.append("clip %s rot" % c["name"]); break
            print("  CLIP %-12s %5.1f fps %3d frames flags %d, %d tracks" % (c["name"], c["fps"], len(c["frames"]),
                                                                            c["flags"], len(c["tracks"])))
    print("RESULT: " + ("OK" if not errs else "FAIL\n  " + "\n  ".join(errs[:20])))
    return not errs


if __name__ == "__main__":
    a = [x for x in sys.argv[1:]]
    sys.exit(0 if check(a[0], a[1] if len(a) > 1 else None) else 1)
