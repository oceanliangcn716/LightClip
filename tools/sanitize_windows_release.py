#!/usr/bin/env python3
"""Redact source-location user prefixes in the known PC-supplied release.

This deliberately accepts only one audited, unsigned x64 DLL/archive. It does
not rebuild or patch executable code. Original private path values are never
printed or written into reports. Future source builds use Rust path remapping.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import struct
import zipfile


INPUT_ZIP_SHA256 = "6122fc5a3f68c861fd2cb57402607d294e27773df51212205f18426dbae3d64b"
INPUT_DLL_SHA256 = "bf8b345f27601b875831ad1e74618b248949616ea3f18a8a87290afde0ed0a27"
INPUT_EXE_SHA256 = "d564e1d6d18a1b05d6ed6cfabd05ae5b64ec19a673f859602c8f395ba9bacec8"
DLL_NAME = "lightclip_pairing.dll"
REPORT_NAME = "RELEASE_PRIVACY_VERIFICATION.json"
USER_PREFIX = re.compile(rb"([A-Za-z]:[\\/])Users[\\/]([^\\/\x00\r\n\" ]+)(?=[\\/])", re.I)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def file_digest(path: Path) -> str:
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def pe_layout(data: bytes) -> tuple[int, list[dict], list[tuple[int, int]]]:
    require(len(data) >= 256 and data[:2] == b"MZ", "Not a PE file")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    require(pe + 24 + 240 <= len(data) and data[pe:pe + 4] == b"PE\0\0", "Invalid PE header")
    require(struct.unpack_from("<H", data, pe + 4)[0] == 0x8664, "Expected x64 PE")
    optional = pe + 24
    require(struct.unpack_from("<H", data, optional)[0] == 0x20B, "Expected PE32+")
    require(struct.unpack_from("<I", data, optional + 64)[0] == 0, "Unexpected PE checksum")
    image_base = struct.unpack_from("<Q", data, optional + 24)[0]
    directories = [struct.unpack_from("<II", data, optional + 112 + 8 * i) for i in range(16)]
    require(directories[4] == (0, 0), "Signed binaries are not supported")
    count = struct.unpack_from("<H", data, pe + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    sections = []
    for index in range(count):
        offset = pe + 24 + optional_size + 40 * index
        require(offset + 40 <= len(data), "Invalid section header")
        name = data[offset:offset + 8].split(b"\0")[0].decode("ascii")
        _, rva, size, start = struct.unpack_from("<IIII", data, offset + 8)
        flags = struct.unpack_from("<I", data, offset + 36)[0]
        require(start + size <= len(data), "Invalid section size")
        sections.append({"name": name, "rva": rva, "start": start, "size": size,
                         "executable": bool(flags & 0x20000000)})
    return image_base, sections, directories


def section_for(sections: list[dict], offset: int, size: int = 1) -> dict:
    values = [s for s in sections if s["start"] <= offset and offset + size <= s["start"] + s["size"]]
    require(len(values) == 1, "Range is outside an unambiguous section")
    return values[0]


def directory_bytes(data: bytes, sections: list[dict], entry: tuple[int, int]) -> bytes:
    rva, size = entry
    if not rva or not size:
        return b""
    owners = [s for s in sections if s["rva"] <= rva and rva + size <= s["rva"] + s["size"]]
    require(len(owners) == 1, "Unexpected PE data directory")
    owner = owners[0]
    start = owner["start"] + rva - owner["rva"]
    return data[start:start + size]


def sanitize_dll(original: bytes) -> tuple[bytes, dict]:
    require(digest(original) == INPUT_DLL_SHA256, "DLL differs from the audited original")
    image_base, sections, directories = pe_layout(original)
    matches = list(USER_PREFIX.finditer(original))
    require(len(matches) == 3, "Expected exactly three audited user prefixes")
    output = bytearray(original)
    ranges = []
    location_count = 0
    for match in matches:
        section = section_for(sections, match.start(), len(match.group()))
        require(section["name"] == ".rdata" and not section["executable"], "Path is not readonly metadata")
        require(b"registry" in original[match.end():match.end() + 180], "Expected Cargo registry source location")
        address = image_base + section["rva"] + match.start() - section["start"]
        references = list(re.finditer(re.escape(struct.pack("<Q", address)), original))
        require(bool(references), "No source-location record references the filename")
        for reference in references:
            section_for(sections, reference.start(), 24)
            length, line, column = struct.unpack_from("<QII", original, reference.start() + 8)
            require(0 < length < 512 and 0 < line < 100000 and 0 < column < 10000,
                    "Unexpected Rust source-location record")
            filename = original[match.start():match.start() + length]
            require(filename.endswith(b".rs"), "Filename is not a Rust source location")
            location_count += 1
        # Keep byte lengths, pointers and source-location records unchanged.
        replacement = match.group(1) + b"Build" + match.group()[8:9] + b"_" * len(match.group(2))
        require(len(replacement) == len(match.group()), "Replacement length changed")
        output[match.start():match.end()] = replacement
        ranges.append((match.start(), match.end()))
    result = bytes(output)
    require(len(result) == len(original), "DLL size changed")
    changed = [i for i, (before, after) in enumerate(zip(original, result)) if before != after]
    require(bool(changed) and all(any(start <= i < end for start, end in ranges) for i in changed),
            "A byte outside the approved metadata ranges changed")
    require(not USER_PREFIX.search(result), "Personal prefix remains")
    first_section = min(s["start"] for s in sections)
    require(result[:first_section] == original[:first_section], "PE headers changed")
    section_results = []
    for section in sections:
        start, size = section["start"], section["size"]
        before, after = original[start:start + size], result[start:start + size]
        if section["name"] != ".rdata":
            require(before == after, "A non-metadata section changed")
        section_results.append({"name": section["name"], "executable": section["executable"],
                                "unchanged": before == after, "before_sha256": digest(before),
                                "after_sha256": digest(after)})
    for entry in directories:
        require(directory_bytes(original, sections, entry) == directory_bytes(result, sections, entry),
                "A PE data directory changed")
    return result, {"method": "Equal-length redaction of three Rust source filename user prefixes",
                    "original_dll_sha256": digest(original), "sanitized_dll_sha256": digest(result),
                    "dll_size_unchanged": True, "redacted_prefixes": len(ranges),
                    "changed_byte_count": len(changed), "rust_location_references_verified": location_count,
                    "pe_headers_and_all_data_directories_unchanged": True,
                    "section_hashes": section_results}


def build(input_zip: Path, output_zip: Path, report_path: Path) -> dict:
    require(len({input_zip.resolve(), output_zip.resolve(), report_path.resolve()}) == 3,
            "Input, output and report must use distinct paths")
    require(file_digest(input_zip) == INPUT_ZIP_SHA256, "ZIP differs from the audited PC release")
    with zipfile.ZipFile(input_zip) as source:
        entries = source.infolist()
        require(len(entries) == 275, "Unexpected original archive entry count")
        require(len({entry.filename for entry in entries}) == len(entries), "Duplicate ZIP entries")
        for entry in entries:
            name = PurePosixPath(entry.filename)
            require(not name.is_absolute() and ".." not in name.parts and "\\" not in entry.filename,
                    "Unsafe ZIP path")
            require(not entry.flag_bits & 1 and (entry.external_attr >> 16) & 0o170000 != 0o120000,
                    "Encrypted or symbolic ZIP entry")
        original = source.read(DLL_NAME)
        sanitized, report = sanitize_dll(original)
        require(digest(source.read("LightClip.exe")) == INPUT_EXE_SHA256, "EXE differs from the PC baseline")
        report.update({"date": "2026-10-08", "version": "Windows0.3.0", "architecture": "win-x64",
                       "original_runtime_zip_sha256": INPUT_ZIP_SHA256,
                       "exe_sha256_unchanged": INPUT_EXE_SHA256,
                       "original_files": 275, "original_files_unchanged": 274,
                       "added_file": REPORT_NAME, "code_signing": "Unsigned, unchanged",
                       "windows_execution_after_redaction": False,
                       "validation_boundary": "PC-provided runtime tests remain the original baseline. Mac performed metadata-only redaction and byte/PE/ZIP/privacy checks; no Windows runtime execution is claimed."})
        output_zip.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(output_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as target:
            for entry in entries:
                data = sanitized if entry.filename == DLL_NAME else source.read(entry)
                target.writestr(entry, data, compresslevel=9)
            target.writestr(REPORT_NAME, json.dumps(report, indent=2) + "\n")
    with zipfile.ZipFile(input_zip) as before, zipfile.ZipFile(output_zip) as after:
        require(after.testzip() is None, "Repacked ZIP integrity failed")
        require(set(after.namelist()) == set(before.namelist()) | {REPORT_NAME}, "Repacked file list changed")
        for name in before.namelist():
            require(after.read(name) == (sanitized if name == DLL_NAME else before.read(name)),
                    "An unrelated runtime file changed")
    report["output_runtime_zip_sha256"] = file_digest(output_zip)
    report["output_runtime_zip_bytes"] = output_zip.stat().st_size
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input_zip", type=Path)
    parser.add_argument("output_zip", type=Path)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    report = build(args.input_zip, args.output_zip, args.report)
    print(json.dumps({key: report[key] for key in ("redacted_prefixes", "changed_byte_count",
        "original_files_unchanged", "output_runtime_zip_sha256", "output_runtime_zip_bytes")}))


if __name__ == "__main__":
    main()
