#!/usr/bin/env python3
"""Builds bench/VeyraBench.apk: a pure-Dalvik, no-native-code benchmark.

Pipeline, all local after the one-time R8 download:
  javac  ->  tiny android stubs (compile only, never packaged)
  javac  ->  bench/src
  D8     ->  classes.dex
  python ->  binary AndroidManifest.xml + ZIP
  jarsigner -> v1 signature (accepted by Android 7-14 for targetSdk 28)

The stubs only teach javac the framework signatures. They are passed to D8 as a
library, never as program input, so the APK contains no fake android.* classes.
"""
import os
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BENCH = os.path.join(ROOT, "bench")
BUILD = os.path.join(BENCH, "build")
R8 = os.path.join(ROOT, "tools", "r8", "r8.jar")
KEYSTORE = os.path.join(BUILD, "veyra-bench.keystore")
APK = os.path.join(BENCH, "VeyraBench.apk")
JAVA = "javac"
JAVA_RUN = "java"
JARSIGNER = "jarsigner"
KEYTOOL = "keytool"

sys.path.insert(0, os.path.join(ROOT, "tools"))
import make_fixtures as mf  # noqa: E402  (reuse the AXML writer)

mf.ATTR_IDS["exported"] = 0x01010010
mf.ATTR_IDS["hasCode"] = 0x0101000C

PACKAGE = "com.veyra.bench"
ACTIVITY = ".BenchActivity"
LABEL = "Veyra Bench"
VERSION_CODE = 1
VERSION_NAME = "1.0"


def run(cmd, **kw):
    print("  $", " ".join(cmd))
    r = subprocess.call(cmd, **kw)
    if r != 0:
        raise SystemExit("command failed (%d): %s" % (r, " ".join(cmd)))


def javac(srcs, out, cp=None):
    cmd = [JAVA, "--release", "8", "-encoding", "UTF-8", "-nowarn", "-d", out]
    if cp:
        cmd += ["-classpath", cp]
    cmd += srcs
    run(cmd)


def manifest():
    pool = mf.Pool()
    for s in (mf.ANDROID_NS, "android", "manifest", "package", "uses-sdk",
              "application", "activity", "intent-filter", "action", "category",
              "android.intent.action.MAIN", "android.intent.category.LAUNCHER"):
        pool.add(s)
    for attr in mf.ATTR_IDS:
        pool.add(attr)
    for v in (PACKAGE, str(VERSION_CODE), VERSION_NAME, "24", "28", LABEL, ACTIVITY):
        pool.add(v)

    root = mf.El("manifest")
    root.attr("package", raw=PACKAGE, data=pool.idx(PACKAGE), ns=False)
    root.attr("versionCode", data=VERSION_CODE, vtype=0x10)
    root.attr("versionName", raw=VERSION_NAME, data=pool.idx(VERSION_NAME))

    sdk = root.add(mf.El("uses-sdk"))
    sdk.attr("minSdkVersion", data=24, vtype=0x10)
    sdk.attr("targetSdkVersion", data=28, vtype=0x10)

    app = root.add(mf.El("application"))
    app.attr("label", raw=LABEL, data=pool.idx(LABEL))
    app.attr("hasCode", data=1, vtype=0x12)

    act = app.add(mf.El("activity"))
    act.attr("name", raw=ACTIVITY, data=pool.idx(ACTIVITY))
    act.attr("exported", data=1, vtype=0x12)
    intent = act.add(mf.El("intent-filter"))
    a = intent.add(mf.El("action"))
    a.attr("name", raw="android.intent.action.MAIN", data=pool.idx("android.intent.action.MAIN"))
    c = intent.add(mf.El("category"))
    c.attr("name", raw="android.intent.category.LAUNCHER", data=pool.idx("android.intent.category.LAUNCHER"))
    return mf.build_axml(root, pool)


def main():
    if not os.path.isfile(R8):
        raise SystemExit("missing tools/r8/r8.jar")

    if os.path.isdir(BUILD):
        shutil.rmtree(BUILD)
    stub_classes = os.path.join(BUILD, "stubs")
    app_classes = os.path.join(BUILD, "classes")
    dex_dir = os.path.join(BUILD, "dex")
    for d in (stub_classes, app_classes, dex_dir):
        os.makedirs(d)

    print("==> compiling android stubs (compile-time only)")
    javac([p for p in _walk(os.path.join(BENCH, "stubs")) if p.endswith(".java")], stub_classes)

    print("==> compiling the benchmark")
    javac([p for p in _walk(os.path.join(BENCH, "src")) if p.endswith(".java")], app_classes, stub_classes)

    stub_jar = os.path.join(BUILD, "stubs.jar")
    with zipfile.ZipFile(stub_jar, "w") as z:
        for base, _, files in os.walk(stub_classes):
            for f in files:
                if f.endswith(".class"):
                    p = os.path.join(base, f)
                    z.write(p, os.path.relpath(p, stub_classes).replace("\\", "/"))

    app_jar = os.path.join(BUILD, "bench.jar")
    with zipfile.ZipFile(app_jar, "w") as z:
        for base, _, files in os.walk(app_classes):
            for f in files:
                if f.endswith(".class"):
                    p = os.path.join(base, f)
                    z.write(p, os.path.relpath(p, app_classes).replace("\\", "/"))

    print("==> dexing with D8")
    run([JAVA_RUN, "-cp", R8, "com.android.tools.r8.D8", "--release", "--min-api", "24",
         "--lib", stub_jar, "--output", dex_dir, app_jar])

    dex = os.path.join(dex_dir, "classes.dex")
    if not os.path.isfile(dex):
        raise SystemExit("D8 produced no classes.dex")

    print("==> packaging")
    staged = os.path.join(BUILD, "unsigned.apk")
    with zipfile.ZipFile(staged, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("AndroidManifest.xml", manifest())
        z.write(dex, "classes.dex")

    print("==> signing (v1 / JAR)")
    if not os.path.isfile(KEYSTORE):
        run([KEYTOOL, "-genkeypair", "-keystore", KEYSTORE, "-alias", "veyra",
             "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
             "-storepass", "veyrabench", "-keypass", "veyrabench",
             "-dname", "CN=Veyra Bench, O=Veyra, C=BE"])
    if os.path.isfile(APK):
        os.remove(APK)
    shutil.copyfile(staged, APK)
    run([JARSIGNER, "-keystore", KEYSTORE, "-storepass", "veyrabench",
         "-keypass", "veyrabench", "-sigalg", "SHA256withRSA",
         "-digestalg", "SHA-256", APK, "veyra"])

    size = os.path.getsize(APK)
    dex_size = os.path.getsize(dex)
    print("==> %s  %d bytes  (classes.dex %d bytes)" % (APK, size, dex_size))


def _walk(root):
    for base, _, files in os.walk(root):
        for f in sorted(files):
            yield os.path.join(base, f)


if __name__ == "__main__":
    main()
