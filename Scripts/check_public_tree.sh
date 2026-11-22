#!/usr/bin/env bash
# Check HEAD by default. Pass --worktree to check unpublished local edits.
# SLOVO_PRIVATE_PATTERNS may point to a local file of case-insensitive EREs.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"
exec python3 - "$@" <<'PY'
import argparse
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import zipfile


HOME_PATH = re.compile(
    r"(?:/(?:Users|home)/([^/\s\"']+)/|[A-Z]:\\+Users\\+([^\\/\r\n\"']+)\\+)",
    re.IGNORECASE,
)
PLACEHOLDER_USERS = {"me", "you", "<вы>", "<id>", "anna", "alice", "demo", "user", "test", "тест", "example"}
SECRET = re.compile(
    r"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----|"
    r"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,}|"
    r"xox[baprs]-[A-Za-z0-9-]{16,}|(?:AKIA|ASIA)[A-Z0-9]{16}|"
    r"sk-(?:[A-Za-z0-9_-]{32,}))\b"
)
SECRET_PATH = re.compile(r"(?:^|/)(?:\.env[^/]*|[^/]+\.(?:pem|p12|mobileprovision))(?:/|$)", re.IGNORECASE)
ZIP_HEADERS = (b"PK\x03\x04", b"PK\x05\x06")
ASSET_HEADERS = {
    ".png": (b"\x89PNG\r\n\x1a\n",),
    ".ico": (b"\x00\x00\x01\x00",),
    ".wav": (b"RIFF",),
    ".h264": (b"\x00\x00\x00\x01", b"\x00\x00\x01"),
    ".jar": ZIP_HEADERS,
    ".zip": ZIP_HEADERS,
}
COPYRIGHT = re.compile(r"^Copyright \(c\) [0-9]{4} ([A-Za-z0-9-]+)$", re.MULTILINE)


def run(*args, data=None):
    return subprocess.run(args, input=data, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=True).stdout


def git_paths(*args):
    return [os.fsdecode(path) for path in run("git", *args).split(b"\0") if path]


def paths_for_scan(worktree):
    if worktree:
        return sorted(set(git_paths("ls-files", "-z", "--cached", "--others", "--exclude-standard")))
    # The index is the fast path. A staged change can make it differ from HEAD.
    if not run("git", "diff", "--cached", "--name-only", "-z", "HEAD", "--"):
        return git_paths("ls-files", "-z", "--cached")
    return git_paths("ls-tree", "-r", "-z", "--name-only", "HEAD")


def read_private_rules(rules):
    if "SLOVO_PRIVATE_PATTERNS" not in os.environ:
        return False
    try:
        path = Path(os.environ["SLOVO_PRIVATE_PATTERNS"])
        patterns = [line for line in path.read_bytes().splitlines()
                    if line.strip() and not line.lstrip().startswith(b"#")]
    except OSError:
        raise ValueError("SLOVO_PRIVATE_PATTERNS must name a readable local file.") from None
    rules.write(b"".join(line + b"\n" for line in patterns))
    private_match(rules, "")
    return bool(patterns)


def private_match(rules, value):
    rules.seek(0)
    result = subprocess.run(
        ["grep", "-Eiq", "-f", f"/dev/fd/{rules.fileno()}"],
        input=value.encode("utf-8", errors="replace"),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        pass_fds=(rules.fileno(),),
    )
    if result.returncode not in (0, 1):
        raise ValueError("Could not apply the private ERE rules. Check the local pattern file.")
    return result.returncode == 0


class Scanner:
    def __init__(self, license_text, rules, use_private_rules):
        credits = list(COPYRIGHT.finditer(license_text))
        if len(credits) != 1:
            raise ValueError("LICENSE must contain a single copyright line naming the public GitHub account.")
        self.credit = credits[0][0]
        # The MIT credit names the public GitHub account without embedding it here.
        account = re.escape(credits[0][1])
        self.public_repo = re.compile(
            rf"(?<![\w.-])github\.com/{account}/slovo(?:\.git)?(?![\w.-])",
            re.IGNORECASE,
        )
        self.rules = rules
        self.use_private_rules = use_private_rules
        self.failed = False

    def report(self, path, problem):
        print(f"{path}: {problem}", file=sys.stderr)
        self.failed = True

    def findings(self, value):
        value = self.public_repo.sub("", value)
        if any((match[1] or match[2]).casefold() not in PLACEHOLDER_USERS for match in HOME_PATH.finditer(value)):
            yield "local home path"
        if SECRET.search(value):
            yield "possible private key or token"
        if self.use_private_rules and private_match(self.rules, value):
            yield "private pattern"

    def check_path(self, path, label):
        problems = list(self.findings(path))
        if SECRET_PATH.search(path):
            problems.append("secret file type")
        for problem in problems:
            self.report(label, f"path name contains {problem}")
        return not problems

    def scan_content(self, name, data, label, *, root_file=False, archive=True):
        text = data.decode("utf-8", errors="replace")
        suffix = Path(name).suffix.lower()
        is_archive = suffix in {".jar", ".zip"} or data.startswith(ZIP_HEADERS)
        is_binary = "\x00" in text or "\ufffd" in text or is_archive
        if is_binary:
            allowed = data.startswith(ASSET_HEADERS.get(suffix, ())) and (suffix != ".wav" or data[8:12] == b"WAVE")
            if root_file and not allowed:
                self.report(label, "binary file type is outside the asset allowlist")
            # strings handles binary records. Keep UTF-8 runs as well for non-ASCII metadata.
            strings = run("strings", "-a", "-n", "3", data=data).decode("utf-8", errors="replace")
            text = re.sub(r"[\x00-\x08\x0b-\x1f\x7f\ufffd]+", "\n", text) + "\n" + strings
        elif root_file and name == "LICENSE":
            text = "\n".join(line for line in text.splitlines() if line != self.credit)

        for problem in self.findings(text):
            self.report(label, f"content contains {problem}")
        if archive and is_archive:
            self.scan_archive(data, label)

    def scan_archive(self, data, label):
        with tempfile.NamedTemporaryFile(suffix=".zip") as snapshot:
            snapshot.write(data)
            snapshot.flush()
            try:
                listing = run("unzip", "-l", snapshot.name).decode("utf-8", errors="replace")
                # The first line names the scanner's temporary file.
                for problem in self.findings(listing.partition("\n")[2]):
                    self.report(label, f"archive listing contains {problem}")
                with zipfile.ZipFile(snapshot.name) as archive:
                    for member in archive.infolist():
                        self.check_path(member.filename, f"{label} (archive entry)")
                        if not member.is_dir():
                            self.scan_content(member.filename, archive.read(member), label, archive=False)
            except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError, zipfile.BadZipFile):
                self.report(label, "could not inspect archive")


def main():
    parser = argparse.ArgumentParser(description="Scan the public repository for private data.")
    parser.add_argument("--worktree", action="store_true", help="scan checked-out files and non-ignored additions")
    options = parser.parse_args()

    def read_blob(name):
        if not options.worktree:
            return run("git", "show", f"HEAD:{name}")
        path = Path(name)
        if path.is_symlink() or not path.is_file():
            raise ValueError("expected a regular file")
        return path.read_bytes()

    with tempfile.TemporaryFile() as rules:
        use_private_rules = read_private_rules(rules)
        scanner = Scanner(read_blob("LICENSE").decode("utf-8"), rules, use_private_rules)
        count = 0
        for name in paths_for_scan(options.worktree):
            path = Path(name)
            if options.worktree and not path.exists() and not path.is_symlink():
                continue  # An unpublished deletion is absent from the proposed tree.
            label = repr(name) if scanner.check_path(name, "(repository path)") else "(repository path)"
            try:
                blob = read_blob(name)
                scanner.scan_content(name, blob, label, root_file=True)
                count += 1
            except (OSError, subprocess.CalledProcessError):
                scanner.report(label, "could not read or inspect file")
            except ValueError as error:
                scanner.report(label, str(error))
        if scanner.failed:
            return 1
        source = "working tree" if options.worktree else "HEAD"
        print(f"Public tree check passed ({count} files from {source}).")
        return 0


try:
    sys.exit(main())
except (OSError, ValueError, subprocess.CalledProcessError) as error:
    sys.exit(f"Public tree check failed: {error}")
PY
