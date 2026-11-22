#!/usr/bin/env python3
"""Builds Slovo.app in the repository root.

Runs from VS Code's Run button with no arguments. Standard library only.

    python3 build.py              release build, signed, in ./Slovo.app
    python3 build.py --debug      debug configuration (faster to compile)
    python3 build.py --install    also copy the app to /Applications/Slovo.app
    python3 build.py --dmg        also package the app as Slovo-<version>.dmg
    python3 build.py --windows    build only the Windows app (Windows/), as
                                  Slovo-<version>-win-x64.zip; needs the .NET 10 SDK

Signing identity: SIGN_IDENTITY (or --sign-identity), else "Slovo Local" when the
keychain has it (Scripts/make_signing_identity.sh creates it once), else ad-hoc. A
stable identity keeps macOS permission grants (Microphone, Accessibility, Input
Monitoring, Screen & System Audio Recording) across rebuilds and reinstalls; ad-hoc
signatures lose them every time.
"""

import argparse
import os
import plistlib
import shutil
import subprocess
import sys
from pathlib import Path

APP_NAME = "Slovo"
BUNDLE_ID = "local.slovo.Slovo"
MIN_OS = "14.4"
LOCAL_IDENTITY = "Slovo Local"

ROOT = Path(__file__).resolve().parent
APP = ROOT / f"{APP_NAME}.app"
ENTITLEMENTS = ROOT / "entitlements.plist"
ICON_SCRIPT = ROOT / "Scripts" / "make_icon.swift"
ICON_CACHE = ROOT / ".build" / "slovo-icon"
APP_RESOURCES = ROOT / "Sources" / "Slovo" / "Resources"

# Resource bundles the app needs. The generated Bundle.module accessor looks in
# Contents/Resources of the app (Swift Build) or next to the executable (the older
# native build), and Slovo's own locator checks both, so bundles go to Resources.
REQUIRED_BUNDLES = ["Slovo_Slovo.bundle", "Slovo_SlovoCore.bundle"]
# FluidAudio's bundle holds data for code paths Slovo does not call today; copying it
# keeps its Bundle.module from trapping if one is reached later.
OPTIONAL_BUNDLES = ["FluidAudio_FluidAudio.bundle"]
# Dynamic frameworks from SwiftPM binary targets (whisper.cpp's prebuilt whisper.xcframework).
# SwiftPM leaves them next to the executable, which finds them through @loader_path; in the
# app they go to Contents/Frameworks, found through an rpath added to the executable.
FRAMEWORKS = ["whisper.framework"]
FRAMEWORKS_RPATH = "@executable_path/../Frameworks"

ENTITLEMENT_VALUES = {
    "com.apple.security.device.audio-input": True,
    # Hardened runtime's library validation loads a framework only when it is Apple's or
    # carries the app's Team ID. Local signatures ("Slovo Local", ad-hoc) have no
    # Team ID, so dyld refuses whisper.framework even though build.py signed it with the
    # app's own identity ("different Team IDs"). Everything else hardened runtime enforces
    # (no DYLD_* variables, no unsigned executable memory) stays. A Developer ID build
    # could drop this entitlement.
    "com.apple.security.cs.disable-library-validation": True,
}

INFO_STRINGS = {
    "NSMicrophoneUsageDescription":
        "Slovo uses the microphone to turn your speech into text on this Mac.",
    "NSAudioCaptureUsageDescription":
        "Slovo records system audio while you record a meeting, to transcribe the other participants.",
}


def step(title: str) -> None:
    print(f"==> {title}", flush=True)


def fail(message: str) -> "NoReturn":
    print(f"error: {message}", file=sys.stderr, flush=True)
    sys.exit(1)


def run(*argv: str, capture: bool = False) -> str:
    """Runs a command from the repository root; any failure stops the build."""
    try:
        result = subprocess.run(argv, cwd=ROOT, check=True, text=True, capture_output=capture)
    except FileNotFoundError:
        fail(f"{argv[0]} not found")
    except subprocess.CalledProcessError as error:
        if capture:
            sys.stderr.write(error.stdout or "")
            sys.stderr.write(error.stderr or "")
        fail(f"{' '.join(argv)} exited with {error.returncode}")
    return result.stdout if capture else ""


def build_number() -> str:
    """The HEAD commit time: stable for a commit and increasing across commits."""
    try:
        result = subprocess.run(["git", "show", "-s", "--format=%ct", "HEAD"], cwd=ROOT,
                                check=True, text=True, capture_output=True)
        value = result.stdout.strip()
        if value:
            return value
    except (subprocess.CalledProcessError, FileNotFoundError):
        pass
    return "1"


def write_entitlements() -> None:
    data = plistlib.dumps(ENTITLEMENT_VALUES)
    if not ENTITLEMENTS.exists() or ENTITLEMENTS.read_bytes() != data:
        ENTITLEMENTS.write_bytes(data)


def make_icon() -> Path:
    """Draws the icon with Scripts/make_icon.swift, reusing it until the script changes."""
    icns = ICON_CACHE / "AppIcon.icns"
    if icns.exists() and icns.stat().st_mtime >= ICON_SCRIPT.stat().st_mtime:
        return icns
    step("Drawing the app icon")
    iconset = ICON_CACHE / "AppIcon.iconset"
    shutil.rmtree(ICON_CACHE, ignore_errors=True)
    ICON_CACHE.mkdir(parents=True)
    run("swift", str(ICON_SCRIPT), str(iconset))
    run("iconutil", "-c", "icns", str(iconset), "-o", str(icns))
    return icns


def make_info_plist(version: str, build: str) -> None:
    info = {
        "CFBundleDevelopmentRegion": "en",
        "CFBundleDisplayName": APP_NAME,
        "CFBundleExecutable": APP_NAME,
        "CFBundleIconFile": "AppIcon",
        "CFBundleIdentifier": BUNDLE_ID,
        "CFBundleInfoDictionaryVersion": "6.0",
        "CFBundleLocalizations": ["en", "ru"],
        "CFBundleName": APP_NAME,
        "CFBundlePackageType": "APPL",
        "CFBundleShortVersionString": version,
        "CFBundleVersion": build,
        "LSApplicationCategoryType": "public.app-category.productivity",
        "LSMinimumSystemVersion": MIN_OS,
        "LSUIElement": True,
        "NSHighResolutionCapable": True,
        **INFO_STRINGS,
    }
    with open(APP / "Contents" / "Info.plist", "wb") as file:
        plistlib.dump(info, file)


def signing_identity(requested: str) -> str:
    if requested:
        return requested
    try:
        result = subprocess.run(["security", "find-identity", "-v", "-p", "codesigning"],
                                check=True, text=True, capture_output=True)
        if f'"{LOCAL_IDENTITY}"' in result.stdout:
            return LOCAL_IDENTITY
    except (subprocess.CalledProcessError, FileNotFoundError):
        pass
    return "-"


def sign(identity: str) -> None:
    # Sign nested frameworks first. SwiftPM resource bundles contain data only and
    # are sealed by the outer app signature, including bundles without an Info.plist.
    for framework in sorted((APP / "Contents" / "Frameworks").glob("*.framework")):
        run("codesign", "--force", "--options", "runtime", "--sign", identity, str(framework))
    run("codesign", "--force", "--options", "runtime", "--entitlements", str(ENTITLEMENTS),
        "--sign", identity, str(APP))
    run("codesign", "--verify", "--deep", "--strict", "--verbose=2", str(APP))


def install() -> None:
    target = Path("/Applications") / APP.name
    step(f"Installing to {target}")
    running = subprocess.run(["pgrep", "-x", APP_NAME], capture_output=True).returncode == 0
    if running:
        print("   Slovo is running; quit it so the new version starts next time.")
    if target.exists():
        shutil.rmtree(target)
    run("ditto", str(APP), str(target))


def make_dmg(version: str) -> Path:
    """Packages the signed app as a compressed disk image with a drop target."""
    dmg = ROOT / f"{APP_NAME}-{version}.dmg"
    staging = ROOT / ".build" / "dmg"
    step(f"Packaging {dmg.name}")
    shutil.rmtree(staging, ignore_errors=True)
    staging.mkdir(parents=True)
    run("ditto", str(APP), str(staging / APP.name))
    (staging / "Applications").symlink_to("/Applications")
    dmg.unlink(missing_ok=True)
    run("hdiutil", "create", "-volname", f"{APP_NAME} {version}", "-srcfolder", str(staging),
        "-format", "UDZO", "-quiet", str(dmg))
    shutil.rmtree(staging, ignore_errors=True)
    return dmg


def dotnet_executable() -> str:
    """The .NET SDK: on PATH, or where dotnet-install.sh puts it (~/.dotnet)."""
    found = shutil.which("dotnet")
    if found:
        return found
    local = Path.home() / ".dotnet" / "dotnet"
    if local.exists():
        return str(local)
    fail("the .NET 10 SDK was not found; install it from https://dot.net or with dotnet-install.sh")


def build_windows(version: str, runtime: str) -> Path:
    """Publishes the Windows app self-contained for `runtime` and zips the folder.

    Everything Windows needs is in the zip: Slovo.exe, the .NET runtime, Avalonia and the
    sherpa-onnx/ONNX Runtime DLLs. Unzip anywhere and run Slovo.exe; nothing is installed.
    """
    dotnet = dotnet_executable()
    # No usage reports from the .NET SDK or from the Avalonia build tasks.
    env = dict(os.environ, DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1", AVALONIA_TELEMETRY_OPTOUT="1")
    project = ROOT / "Windows" / "src" / "Slovo.App" / "Slovo.App.csproj"
    output = ROOT / "dist" / f"Slovo-{version}-{runtime}"
    step(f"Publishing the Windows app ({runtime})")
    shutil.rmtree(output, ignore_errors=True)
    try:
        subprocess.run([dotnet, "publish", str(project), "-c", "Release", "-r", runtime, "--self-contained",
                        f"-p:Version={version}", "-o", str(output)], cwd=ROOT, check=True, env=env)
    except subprocess.CalledProcessError as error:
        fail(f"dotnet publish exited with {error.returncode}")
    if not (output / "Slovo.exe").exists():
        fail(f"{output / 'Slovo.exe'} was not produced")
    for name in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
        shutil.copy2(ROOT / name, output / name)
    archive = ROOT / f"Slovo-{version}-{runtime}"
    step(f"Packaging {archive.name}.zip")
    Path(str(archive) + ".zip").unlink(missing_ok=True)
    shutil.make_archive(str(archive), "zip", root_dir=output.parent, base_dir=output.name)
    return Path(str(archive) + ".zip")


def main() -> None:
    parser = argparse.ArgumentParser(description="Build Slovo.app")
    parser.add_argument("--install", action="store_true", help="copy the app to /Applications")
    parser.add_argument("--dmg", action="store_true", help="package the app as a disk image")
    parser.add_argument("--debug", action="store_true", help="use the debug configuration")
    parser.add_argument("--windows", action="store_true", help="build only the Windows app as a zip")
    parser.add_argument("--windows-runtime", default="win-x64", choices=["win-x64", "win-arm64"])
    parser.add_argument("--version", default=os.environ.get("VERSION", "0.5.1"))
    parser.add_argument("--build-number", default=os.environ.get("BUILD_NUMBER", ""))
    parser.add_argument("--sign-identity", default=os.environ.get("SIGN_IDENTITY", ""))
    args = parser.parse_args()

    os.chdir(ROOT)
    if args.windows:
        print(f"Done: {build_windows(args.version, args.windows_runtime)}")
        return
    configuration = "debug" if args.debug else "release"

    # Without this, test bundles fail to sign; see the script for why.
    run(sys.executable, str(ROOT / "Scripts" / "repair_onnxruntime_framework.py"))

    step(f"Building {APP_NAME} ({configuration})")
    run("swift", "build", "-c", configuration, "--product", APP_NAME)
    products = Path(run("swift", "build", "-c", configuration, "--show-bin-path", capture=True).strip())
    executable = products / APP_NAME
    if not executable.exists():
        fail(f"{executable} was not produced")

    icon = make_icon()
    write_entitlements()

    step(f"Assembling {APP.name}")
    shutil.rmtree(APP, ignore_errors=True)
    macos = APP / "Contents" / "MacOS"
    resources = APP / "Contents" / "Resources"
    macos.mkdir(parents=True)
    resources.mkdir(parents=True)
    shutil.copy2(executable, macos / APP_NAME)
    shutil.copy2(icon, resources / "AppIcon.icns")
    for name in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
        shutil.copy2(ROOT / name, resources / name)

    for name in REQUIRED_BUNDLES + OPTIONAL_BUNDLES:
        source = products / name
        if source.exists():
            shutil.copytree(source, resources / name, symlinks=True)
        elif name in REQUIRED_BUNDLES:
            fail(f"{source} is missing; without it the interface shows raw string keys")

    # Without its frameworks the executable does not start at all.
    frameworks = APP / "Contents" / "Frameworks"
    frameworks.mkdir()
    for name in FRAMEWORKS:
        source = products / name
        if not source.exists():
            fail(f"{source} is missing; the app cannot launch without it")
        # Symlinks keep the framework's Versions/Current layout, which codesign requires.
        shutil.copytree(source, frameworks / name, symlinks=True)
    run("install_name_tool", "-add_rpath", FRAMEWORKS_RPATH, str(macos / APP_NAME))

    # macOS reads permission prompts and the localized name from the app's own lproj
    # folders, not from SwiftPM bundles.
    for lproj in sorted(APP_RESOURCES.glob("*.lproj")):
        strings = lproj / "InfoPlist.strings"
        if strings.exists():
            (resources / lproj.name).mkdir(exist_ok=True)
            shutil.copy2(strings, resources / lproj.name / "InfoPlist.strings")

    make_info_plist(args.version, args.build_number or build_number())

    identity = signing_identity(args.sign_identity)
    step("Signing " + ("ad-hoc" if identity == "-" else f"with '{identity}'"))
    sign(identity)
    if identity == "-":
        print("   Ad-hoc signature: macOS permissions reset on every rebuild. Run "
              "Scripts/make_signing_identity.sh once to keep them.")

    if args.install:
        install()

    if args.dmg:
        print(f"Done: {make_dmg(args.version)}")

    print(f"Done: {APP}")


if __name__ == "__main__":
    main()
