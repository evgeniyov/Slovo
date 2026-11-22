#!/usr/bin/env python3
"""Create a portable ZIP and a per-user Setup.exe from a self-contained x64 publish."""

import argparse
import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path, PureWindowsPath


PACKAGE = Path(__file__).resolve().parent
ROOT = PACKAGE.parent.parent


def write_uninstall_files(payload: Path, instructions: Path) -> None:
    """Keep files that the user added in the installation directory."""
    files = sorted(path.relative_to(payload) for path in payload.rglob("*") if path.is_file())
    folders = {parent for file in files for parent in file.parents if parent != Path(".")}
    commands = [f'Delete "$INSTDIR\\{PureWindowsPath(file)}"' for file in files]
    commands += [f'RMDir "$INSTDIR\\{PureWindowsPath(folder)}"'
                 for folder in sorted(folders, key=lambda path: (-len(path.parts), path))]
    instructions.write_text("\n".join(commands) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("publish", type=Path, help="self-contained Windows x64 publish folder")
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=Path, default=Path("dist/release"))
    args = parser.parse_args()
    if re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", args.version) is None:
        parser.error("the version must be three numbers separated by dots")
    executable = shutil.which("makensis")
    if not executable:
        parser.error("makensis (NSIS) is required")
    if not (args.publish / "Slovo.exe").is_file() or not (args.publish / "coreclr.dll").is_file():
        parser.error("the publish folder must contain Slovo.exe and a self-contained runtime")
    args.output.mkdir(parents=True, exist_ok=True)
    output = args.output.resolve()
    name = f"Slovo-{args.version}-win-x64"

    with tempfile.TemporaryDirectory(prefix="slovo-windows-release-") as folder:
        staging = Path(folder)
        payload = staging / f"{name}-portable"
        shutil.copytree(args.publish, payload, ignore=shutil.ignore_patterns("install.ps1", "uninstall.ps1", "README.txt"))

        for license_name in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
            shutil.copy2(ROOT / license_name, payload / license_name)

        instructions = staging / "uninstall-files.nsh"
        write_uninstall_files(payload, instructions)
        define = "/D" if os.name == "nt" else "-D"
        installer = output / f"{name}-setup.exe"
        subprocess.run([
            executable, f"{define}VERSION={args.version}", f"{define}INSTALLER={installer}",
            f"{define}PAYLOAD={payload}", f"{define}UNINSTALL_FILES={instructions}",
            str(PACKAGE / "slovo.nsi"),
        ], check=True)

        launcher = (PACKAGE / "Start Slovo.cmd").read_text(encoding="ascii")
        (payload / "Start Slovo.cmd").write_bytes(launcher.replace("\n", "\r\n").encode("ascii"))
        shutil.copyfile(PACKAGE / "README-portable.txt", payload / "README.txt")
        archive = shutil.make_archive(str(output / payload.name), "zip", root_dir=staging, base_dir=payload.name)

    print(installer)
    print(archive)


if __name__ == "__main__":
    main()
