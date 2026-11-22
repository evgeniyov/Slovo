#!/usr/bin/env python3
"""Restores the symlinks of the onnxruntime static framework that sherpa-onnx pulls in.

The onnxruntime-libs release zip stores `Versions/Current`, the top-level binary,
`Headers` and `Resources` as plain copies instead of symlinks. Executables link it
fine, but Swift Build embeds the framework into test bundles, cannot resolve
`Versions/Current`, leaves it unsigned, and signing `SlovoCoreTests.xctest` then
fails with "code object is not signed at all".

The extracted copy lives in `.build/artifacts`, so this has to run again after
`swift package reset` or a fresh clone. It is idempotent; build.py runs it every time.

Usage: python3 Scripts/repair_onnxruntime_framework.py
"""

import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ARTIFACTS = ROOT / ".build" / "artifacts" / "onnxruntime-libs" / "OnnxruntimeMacOS"
TOP_LEVEL = ["onnxruntime", "Headers", "Resources", "Modules"]


def remove(path: Path) -> None:
    if path.is_symlink() or path.is_file():
        path.unlink()
    elif path.is_dir():
        shutil.rmtree(path)


def repair(framework: Path) -> bool:
    """Returns True when something was changed."""
    versions = framework / "Versions"
    version_a = versions / "A"
    current = versions / "Current"
    if not version_a.is_dir():
        print(f"skipped {framework}: no Versions/A", file=sys.stderr)
        return False

    changed = False
    if not current.is_symlink():
        remove(current)
        current.symlink_to("A")
        changed = True

    for name in TOP_LEVEL:
        top = framework / name
        if top.is_symlink() or not top.exists():
            continue
        inside = version_a / name
        if inside.exists():
            remove(top)
        else:
            # Modules exists only at the top level; it moves into the version folder.
            shutil.move(str(top), str(inside))
        top.symlink_to(f"Versions/Current/{name}")
        changed = True
    return changed


def main() -> None:
    if not ARTIFACTS.is_dir():
        return
    for framework in sorted(ARTIFACTS.glob("*.xcframework/macos-*/onnxruntime.framework")):
        if repair(framework):
            print(f"repaired framework symlinks in {framework.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
