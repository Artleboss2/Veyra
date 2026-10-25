#!/usr/bin/env python3
"""Generates synthetic but structurally valid APK fixtures for Veyra's parser tests.

Builds real binary AndroidManifest.xml (AXML), a resources.arsc table with label and
icon entries, minimal DEX containers, and a ZIP package around them. No external deps.
"""
import os
import struct
import zipfile
import zlib

ANDROID_NS = "http://schemas.android.com/apk/res/android"
OUT_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "build", "fixtures")

ATTR_IDS = {
    "label": 0x01010001,
    "icon": 0x01010002,
    "name": 0x01010003,
    "debuggable": 0x0101000f,
    "versionCode": 0x0101021b,
    "versionName": 0x0101021c,
    "minSdkVersion": 0x0101020c,
    "targetSdkVersion": 0x01010270,
}


def u16(v):
    return struct.pack("<H", v & 0xFFFF)


def u32(v):
    return struct.pack("<I", v & 0xFFFFFFFF)


def uleb(v):
    out = bytearray()
    while True:
        b = v & 0x7F
        v >>= 7
        if v:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


class Pool:
    def __init__(self):
        self.items = []
        self.index = {}

    def add(self, s):
        if s in self.index:
            return self.index[s]
        self.index[s] = len(self.items)
        self.items.append(s)
        return self.index[s]

    def idx(self, s):
        if s not in self.index:
            raise KeyError("string not registered: %r" % s)
        return self.index[s]


def build_pool(items, utf8=True):
    count = len(items)
    offsets = []
    data = bytearray()
    for s in items:
        offsets.append(len(data))
        raw = s.encode("utf-8")
        utf16len = len(s.encode("utf-16-le")) // 2
        data += uleb(utf16len) + uleb(len(raw)) + raw + b"\x00"

    header_size = 28
    strings_start = header_size + 4 * count
    body = bytearray()
    body += u32(count) + u32(0) + u32(0x100 if utf8 else 0) + u32(strings_start) + u32(0)
    for off in offsets:
        body += u32(off)
    body += data
    while len(body) % 4:
        body += b"\x00"

    return u16(0x0001) + u16(header_size) + u32(8 + len(body)) + bytes(body)


class El:
    def __init__(self, name):
        self.name = name
        self.attrs = []
        self.children = []

    def attr(self, name, raw=None, data=None, vtype=0x03, ns=True):
        self.attrs.append({"name": name, "raw": raw, "data": data, "type": vtype, "ns": ns})
        return self

    def add(self, child):
        self.children.append(child)
        return child


def build_axml(root, pool):
    res_map_len = len(pool.items)
    res_map = [0] * res_map_len
    for attr, rid in ATTR_IDS.items():
        if attr in pool.index:
            res_map[pool.index[attr]] = rid

    out = bytearray()
    out += build_pool(pool.items)

    map_body = b"".join(u32(v) for v in res_map)
    out += u16(0x0180) + u16(8) + u32(8 + len(map_body)) + map_body

    ns_uri_idx = pool.idx(ANDROID_NS)
    prefix_idx = pool.idx("android")

    def start_ns():
        return u16(0x0100) + u16(16) + u32(24) + u32(1) + u32(0xFFFFFFFF) + u32(prefix_idx) + u32(ns_uri_idx)

    def end_ns():
        return u16(0x0101) + u16(16) + u32(24) + u32(1) + u32(0xFFFFFFFF) + u32(prefix_idx) + u32(ns_uri_idx)

    def start_el(el):
        body = bytearray()
        name_idx = pool.idx(el.name)
        for a in el.attrs:
            ns_idx = ns_uri_idx if a["ns"] else 0xFFFFFFFF
            raw = a["raw"]
            raw_field = 0xFFFFFFFF if raw is None else pool.idx(raw)
            body += u32(ns_idx) + u32(pool.idx(a["name"])) + u32(raw_field)
            body += u16(8) + bytes([0, a["type"]]) + u32(a["data"] if a["data"] is not None else 0)
        size = 16 + 20 + len(body)
        chunk = bytearray()
        chunk += u16(0x0102) + u16(16) + u32(size) + u32(1) + u32(0xFFFFFFFF)
        chunk += u32(0xFFFFFFFF) + u32(name_idx)
        chunk += u16(20) + u16(20) + u16(len(el.attrs)) + u16(0) + u16(0) + u16(0)
        chunk += body
        assert len(chunk) == size
        return bytes(chunk)

    def end_el(el):
        return (u16(0x0103) + u16(16) + u32(24) + u32(1) + u32(0xFFFFFFFF)
                + u32(0xFFFFFFFF) + u32(pool.idx(el.name)))

    out += start_ns()

    stack = [(root, False)]
    while stack:
        el, closing = stack.pop()
        if closing:
            out += end_el(el)
        else:
            out += start_el(el)
            stack.append((el, True))
            for child in reversed(el.children):
                stack.append((child, False))

    out += end_ns()

    doc = bytearray()
    doc += u16(0x0003) + u16(8) + u32(8 + len(out))
    doc += out
    return bytes(doc)


def build_png(w, h):
    rows = bytearray()
    for y in range(h):
        rows.append(0)
        for x in range(w):
            t = (x + y) / float(w + h)
            r = int(120 + 110 * t)
            g = int(70 + 40 * t)
            b = int(220 - 40 * t)
            d = ((x - w / 2) ** 2 + (y - h / 2) ** 2) ** 0.5
            a = 255 if d < w * 0.42 else 0
            rows += bytes([r, g, b, a])

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(rows), 9))
            + chunk(b"IEND", b""))


def build_arsc(label, icon_path):
    global_strings = [icon_path, label, "app_name", "ic_launcher"]
    type_names = ["string", "mipmap"]
    key_names = ["app_name", "ic_launcher"]

    global_pool = build_pool(global_strings)
    type_pool = build_pool(type_names)
    key_pool = build_pool(key_names)

    def type_chunk(type_id, key_idx, value, density):
        entry_count = 1
        header_size = 8 + 1 + 1 + 2 + 4 + 4 + 64
        entries_start = header_size + 4 * entry_count
        entry = u16(8) + u16(0) + u32(key_idx) + u16(8) + bytes([0, 0x03]) + u32(value)
        size = entries_start + len(entry)
        out = bytearray()
        out += u16(0x0201) + u16(header_size) + u32(size)
        out += bytes([type_id, 0]) + u16(0)
        out += u32(entry_count) + u32(entries_start)
        config = bytearray(64)
        struct.pack_into("<I", config, 0, 64)
        struct.pack_into("<H", config, 14, density)
        out += config
        out += u32(0)
        out += entry
        assert len(out) == size
        return bytes(out)

    type_string = type_chunk(1, 0, 1, 0)
    type_icon = type_chunk(2, 1, 0, 480)

    package_body = bytearray()
    package_body += u32(0x7F)
    name = "com.veyra.fixture".encode("utf-16-le")
    package_body += name + b"\x00" * (256 - len(name))
    package_body += u32(288)
    package_body += u32(len(type_names))
    package_body += u32(288 + len(type_pool))
    package_body += u32(len(key_names))
    package_body += u32(0)
    # chunk header is 8 bytes; the package header itself is 288 bytes total
    assert len(package_body) == 280

    package_body += type_pool + key_pool + type_string + type_icon

    package = u16(0x0200) + u16(288) + u32(8 + len(package_body)) + bytes(package_body)

    table_body = global_pool + package
    table = u16(0x0002) + u16(12) + u32(12 + len(table_body)) + u32(1) + table_body
    return table


def build_dex(class_names):
    strings = list(class_names) + ["Ljava/lang/Object;"]
    n = len(strings)
    string_ids_off = 0x70
    type_ids_off = string_ids_off + 4 * n
    class_defs_off = type_ids_off + 4 * n
    data_off = class_defs_off + 32 * len(class_names)

    data = bytearray()
    string_data_offsets = []
    for s in strings:
        string_data_offsets.append(data_off + len(data))
        raw = s.encode("utf-8")
        data += uleb(len(s.encode("utf-16-le")) // 2) + raw + b"\x00"
    while len(data) % 4:
        data += b"\x00"

    total = data_off + len(data)
    buf = bytearray(total)
    buf[0:8] = b"dex\n035\x00"
    struct.pack_into("<I", buf, 32, total)
    struct.pack_into("<I", buf, 36, 0x70)
    struct.pack_into("<I", buf, 40, 0x12345678)
    struct.pack_into("<I", buf, 56, n)
    struct.pack_into("<I", buf, 60, string_ids_off)
    struct.pack_into("<I", buf, 64, n)
    struct.pack_into("<I", buf, 68, type_ids_off)
    struct.pack_into("<I", buf, 96, len(class_names))
    struct.pack_into("<I", buf, 100, class_defs_off)
    struct.pack_into("<I", buf, 104, len(data))
    struct.pack_into("<I", buf, 108, data_off)

    for i in range(n):
        struct.pack_into("<I", buf, string_ids_off + i * 4, string_data_offsets[i])
        struct.pack_into("<I", buf, type_ids_off + i * 4, i)

    for i in range(len(class_names)):
        base = class_defs_off + i * 32
        struct.pack_into("<I", buf, base, i)
        struct.pack_into("<I", buf, base + 4, 1)
        struct.pack_into("<I", buf, base + 8, n - 1)
        struct.pack_into("<I", buf, base + 12, 0)
        struct.pack_into("<I", buf, base + 16, 0xFFFFFFFF)
        struct.pack_into("<I", buf, base + 20, 0)
        struct.pack_into("<I", buf, base + 24, 0)
        struct.pack_into("<I", buf, base + 28, 0)

    buf[data_off:data_off + len(data)] = data
    return bytes(buf)


def make_manifest(package, label_ref, icon_ref, min_sdk, target_sdk, activity=".MainActivity"):
    pool = Pool()
    for s in (ANDROID_NS, "android", "manifest", "package", "uses-sdk", "uses-permission",
              "application", "activity", "intent-filter", "action", "category",
              "android.permission.INTERNET", "android.permission.ACCESS_NETWORK_STATE",
              "android.intent.action.MAIN", "android.intent.category.LAUNCHER"):
        pool.add(s)
    for attr in ATTR_IDS:
        pool.add(attr)
    for v in (package, "1", "0.9.4", str(min_sdk), str(target_sdk), activity):
        pool.add(v)

    manifest = El("manifest")
    manifest.attr("package", raw=package, data=pool.idx(package), ns=False)
    manifest.attr("versionCode", data=1, vtype=0x10)
    manifest.attr("versionName", raw="0.9.4", data=pool.idx("0.9.4"))

    sdk = manifest.add(El("uses-sdk"))
    sdk.attr("minSdkVersion", data=min_sdk, vtype=0x10)
    sdk.attr("targetSdkVersion", data=target_sdk, vtype=0x10)

    for perm in ("android.permission.INTERNET", "android.permission.ACCESS_NETWORK_STATE"):
        p = manifest.add(El("uses-permission"))
        p.attr("name", raw=perm, data=pool.idx(perm))

    app = manifest.add(El("application"))
    app.attr("label", data=label_ref, vtype=0x01)
    app.attr("icon", data=icon_ref, vtype=0x01)
    app.attr("debuggable", data=0xFFFFFFFF, vtype=0x12)

    act = app.add(El("activity"))
    act.attr("name", raw=activity, data=pool.idx(activity))
    intent = act.add(El("intent-filter"))
    a1 = intent.add(El("action"))
    a1.attr("name", raw="android.intent.action.MAIN", data=pool.idx("android.intent.action.MAIN"))
    c1 = intent.add(El("category"))
    c1.attr("name", raw="android.intent.category.LAUNCHER", data=pool.idx("android.intent.category.LAUNCHER"))

    return build_axml(manifest, pool)


def write_apk(path, manifest, arsc, dexes, extra_files=None):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("AndroidManifest.xml", manifest)
        z.writestr("resources.arsc", arsc)
        for name, blob in dexes.items():
            z.writestr(name, blob)
        if extra_files:
            for name, blob in extra_files.items():
                z.writestr(name, blob)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    icon_path = "res/mipmap-xxhdpi/ic_launcher.png"
    png = build_png(96, 96)

    manifest = make_manifest("com.veyra.fixture", 0x7F010000, 0x7F020000, 24, 34)
    arsc = build_arsc("Veyra Fixture", icon_path)
    dex1 = build_dex(["Lcom/veyra/fixture/MainActivity;", "Lcom/veyra/fixture/Net;"])

    write_apk(os.path.join(OUT_DIR, "veyra-fixture.apk"), manifest, arsc,
              {"classes.dex": dex1}, {icon_path: png})

    dex2 = build_dex(["Lcom/veyra/fixture/Helper;", "Lcom/veyra/fixture/Store;"])
    native_extra = {
        "lib/arm64-v8a/libunity.so": b"\x7fELF" + b"\x00" * 256,
        "lib/armeabi-v7a/libunity.so": b"\x7fELF" + b"\x00" * 256,
        "assets/bin/Data/globalgamemanagers": b"\x00" * 64,
        icon_path: png,
    }
    write_apk(os.path.join(OUT_DIR, "veyra-fixture-native.apk"), manifest, arsc,
              {"classes.dex": dex1, "classes2.dex": dex2}, native_extra)

    with zipfile.ZipFile(os.path.join(OUT_DIR, "veyra-fixture-broken.apk"), "w") as z:
        z.writestr("readme.txt", "Not an APK")

    for f in sorted(os.listdir(OUT_DIR)):
        p = os.path.join(OUT_DIR, f)
        print("%-30s %8d bytes" % (f, os.path.getsize(p)))


if __name__ == "__main__":
    main()
