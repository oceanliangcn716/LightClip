#!/usr/bin/env python3
"""Package only checked-in source candidates and public synthetic test fixtures.

The whitelist intentionally excludes preferences, credentials, build outputs,
local SDKs, app backups and real clipboard data.
"""
from pathlib import Path
import hashlib
import zipfile

root = Path(__file__).resolve().parent.parent
paths = [root / name for name in (
    "README.md", "RELEASE_NOTES.md", "WINDOWS_IMPORT_REVIEW.md", "PROTOCOL.md", "PAIRING_V2.md", "STREAM_V2.md", "THIRD_PARTY_NOTICES.txt",
    "给PC上Codex的群组升级提示词.md", "给PC上Codex的文件升级提示词.md", "build-mac.sh", ".gitignore",
    "pairing/Cargo.toml", "pairing/Cargo.lock", "pairing/build-windows.ps1",
    "pairing/src/lib.rs", "pairing/include/LightClipPairing.h",
    "tests/verification.json", "tests/双机隔离验收.md",
    "tools/render_icon.swift", "tools/package_handoff.py", "tools/sanitize_windows_release.py",
)]
for directory, patterns in (
    ("mac", ("*.swift",)),
    ("tests", ("*.cs", "*.csproj")),
    ("assets", ("*.svg", "*.ico", "*.icns", "*.png")),
):
    for pattern in patterns:
        paths.extend((root / directory).glob(pattern))
windows_suffixes = {".cs", ".csproj", ".ps1", ".md", ".manifest", ".ico", ".txt", ".rs", ".toml", ".lock", ".h"}
for path in (root / "windows").rglob("*"):
    relative = path.relative_to(root / "windows")
    if any(part in {"bin", "obj", "target", "artifacts", ".vs"} for part in relative.parts):
        continue
    if path.is_file() and (path.suffix in windows_suffixes or path.name == ".gitignore"):
        paths.append(path)
paths = sorted(set(paths))
for path in paths:
    if not path.is_file() or path.is_symlink():
        raise RuntimeError("A required regular source file is missing")
destination = root / "dist/LightClip-source-Mac0.3.1-Windows0.3.0.zip"
destination.parent.mkdir(exist_ok=True)
with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for path in paths:
        archive.write(path, str(Path("LightClip-source") / path.relative_to(root)))
with zipfile.ZipFile(destination) as archive:
    if archive.testzip() is not None:
        raise RuntimeError("Archive integrity check failed")
    assert len(archive.namelist()) == len(paths)
print({"archive": destination.name, "files": len(paths),
       "bytes": destination.stat().st_size,
       "sha256": hashlib.sha256(destination.read_bytes()).hexdigest()})
