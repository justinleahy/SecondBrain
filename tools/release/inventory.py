"""Augment a Syft CycloneDX SBOM with verified bundled browser and SQLite components."""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
from urllib.parse import quote
import xml.etree.ElementTree as ET


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def normalize_runtime_packs(components):
    for item in components:
        # deps.json prefixes framework pack names with runtimepack.; advisory
        # databases use the actual NuGet package id. Keep bom-ref identities.
        if item.get("name", "").startswith("runtimepack.Microsoft."):
            original = item["name"]
            item["name"] = original.removeprefix("runtimepack.")
            item["purl"] = "pkg:nuget/" + item["name"] + "@" + item["version"]
            item.setdefault("properties", []).append({"name": "secondbrain:deps-runtimepack-name", "value": original})


def inventory(artifact, sbom, output, repository):
    artifact = artifact.resolve(strict=True)
    document = json.loads(sbom.read_text())
    if document.get("bomFormat") != "CycloneDX":
        raise ValueError("A CycloneDX SBOM is required.")
    components = document.setdefault("components", [])
    if not any(item.get("purl", "").startswith("pkg:nuget/") for item in components):
        raise ValueError("The published artifact has no catalogued NuGet packages.")
    runtime = ET.parse(repository / "Directory.Packages.props").findtext(".//SecondBrainRuntimeVersion")
    if not any(item.get("name") == "runtimepack.Microsoft.NETCore.App.Runtime.linux-x64" and item.get("version") == runtime for item in components):
        raise ValueError("The self-contained artifact lacks the pinned .NET runtime.")
    normalize_runtime_packs(components)
    files = []
    for path in sorted(artifact.rglob("*")):
        if path.is_symlink():
            raise ValueError("A published artifact contains a symbolic link.")
        if path.is_file():
            files.append({"path": path.relative_to(artifact).as_posix(), "bytes": path.stat().st_size, "sha256": digest(path)})
    native = [path for path in artifact.rglob("libe_sqlite3.so")]
    is_web = any(path.name == "SecondBrain.Server.dll" for path in artifact.iterdir())
    if is_web and not native:
        raise ValueError("The published artifact lacks its native SQLite library.")
    for path in native:
        # Probe only the already-built, local native library, never a remote path.
        library = ctypes.CDLL(str(path))
        library.sqlite3_libversion.restype = ctypes.c_char_p
        version = library.sqlite3_libversion().decode("ascii")
        if not re.fullmatch(r"\d+\.\d+\.\d+", version):
            raise ValueError("Invalid SQLite version returned by the bundled library.")
        sha = digest(path)
        components.append({"type": "library", "name": "sqlite", "version": version,
            "bom-ref": "secondbrain:sqlite:" + sha, "purl": "pkg:generic/sqlite@" + version,
            "cpe": "cpe:2.3:a:sqlite:sqlite:" + version + ":*:*:*:*:*:*:*",
            "hashes": [{"alg": "SHA-256", "content": sha}],
            "properties": [{"name": "secondbrain:artifact-path", "value": path.relative_to(artifact).as_posix()}]})
    manifests = list(artifact.rglob("wwwroot/editor/manifest.json"))
    if is_web and not manifests:
        raise ValueError("The web artifact lacks its bundled editor manifest.")
    lock = json.loads((repository / "tools/editor/package-lock.json").read_text())
    for manifest_path in manifests:
        manifest = json.loads(manifest_path.read_text())
        if "monaco-vendored-dompurify" not in manifest.get("dependencyPatches", []):
            raise ValueError("The editor lacks its reviewed vendored sanitizer replacement.")
        version = manifest["monaco"]
        if version != lock["packages"]["node_modules/monaco-editor"]["version"]:
            raise ValueError("Bundled Monaco version does not match the reviewed npm lock.")
        editor_root = manifest_path.parent
        seen = set()
        for entry in manifest["files"]:
            name = entry["file"]
            if name in seen or Path(name).name != name:
                raise ValueError("Invalid editor manifest path.")
            seen.add(name)
            path = editor_root / name
            if not path.is_file() or path.is_symlink() or path.stat().st_size != entry["bytes"] or digest(path) != entry["sha256"]:
                raise ValueError("Bundled editor hash mismatch: " + name)
        if not {"editor.js", "editor.worker.js", "editor.css"}.issubset(seen):
            raise ValueError("The editor manifest is incomplete.")
        for notice in ("MONACO-LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "DOMPURIFY-LICENSE.txt"):
            if not (editor_root / notice).is_file():
                raise ValueError("Bundled editor license is absent.")
        bundled = manifest.get("packages", [])
        if not {"monaco-editor", "dompurify", "marked"}.issubset({item["name"] for item in bundled}):
            raise ValueError("The editor's bundled dependency inventory is incomplete.")
        for item in bundled:
            locked = lock["packages"].get(item["lockPath"])
            if not locked or item["version"] != locked["version"] or item["integrity"] != locked["integrity"]:
                raise ValueError("Bundled dependency does not match the reviewed lock: " + item["name"])
            components.append({"type": "library", "name": item["name"], "version": item["version"],
                "bom-ref": "secondbrain:npm:" + item["lockPath"], "purl": "pkg:npm/" + quote(item["name"], safe="/") + "@" + item["version"],
                "licenses": [{"expression": item["license"]}],
                "properties": [{"name": "secondbrain:bundle-manifest", "value": manifest_path.relative_to(artifact).as_posix()},
                               {"name": "secondbrain:npm-integrity", "value": item["integrity"]}]})
    output.mkdir(parents=True, exist_ok=True)
    (output / "sbom.cdx.json").write_text(json.dumps(document, indent=2) + "\n")
    (output / "files.json").write_text(json.dumps({"algorithm": "sha256", "files": files}, indent=2) + "\n")
    metadata = {"gitCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repository, text=True).strip(),
        "workingTreeDirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=repository, text=True)),
        "dotnetSdk": subprocess.check_output(["dotnet", "--version"], cwd=repository, text=True).strip(),
        "repository": os.environ.get("GITHUB_REPOSITORY"), "workflowRef": os.environ.get("GITHUB_WORKFLOW_REF"),
        "workflowRun": os.environ.get("GITHUB_RUN_ID"), "workflowAttempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
        "nativeSqliteVersions": sorted({item["version"] for item in components if item.get("name") == "sqlite"}),
        "fileCount": len(files), "componentCount": len(components), "editorBundles": len(manifests)}
    (output / "build.json").write_text(json.dumps(metadata, indent=2) + "\n")
    print(json.dumps(metadata))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact", type=Path)
    parser.add_argument("sbom", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--repository", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    inventory(args.artifact, args.sbom, args.output, args.repository)
