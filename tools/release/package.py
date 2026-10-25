"""Package an inventoried release and bind its archive and evidence with SHA-256."""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import tarfile


class HashedReader:
    def __init__(self, stream):
        self.stream = stream
        self.sha = hashlib.sha256()

    def read(self, length=-1):
        data = self.stream.read(length)
        self.sha.update(data)
        return data


def package(artifact, reports, name):
    if not name or any(c not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_." for c in name):
        raise ValueError("Invalid release package name.")
    manifest = json.loads((reports / "files.json").read_text())
    expected = {item["path"]: item for item in manifest["files"]}
    actual = {p.relative_to(artifact).as_posix() for p in artifact.rglob("*") if p.is_file()}
    if actual != set(expected):
        raise ValueError("Release files changed after inventory.")
    for relative, item in expected.items():
        path = artifact / relative
        if path.is_symlink() or not path.resolve().is_relative_to(artifact.resolve()) or path.stat().st_size != item["bytes"]:
            raise ValueError("Release path changed after inventory.")
        with path.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != item["sha256"]:
                raise ValueError("Release bytes changed after inventory.")
    archive = reports / (name + ".tar.gz")
    with archive.open("xb") as output, gzip.GzipFile(filename="", mode="wb", fileobj=output, mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode="w") as tar:
            for relative in sorted(expected):
                path = artifact / relative
                info = tar.gettarinfo(str(path), arcname=relative)
                info.uid = info.gid = info.mtime = 0
                info.uname = info.gname = ""
                info.mode = 0o755 if path.stat().st_mode & 0o111 else 0o644
                with path.open("rb") as stream:
                    reader = HashedReader(stream)
                    tar.addfile(info, reader)
                    if reader.sha.hexdigest() != expected[relative]["sha256"] or stream.read(1):
                        raise ValueError("Release bytes changed while packaging.")
    lines = []
    for path in sorted(reports.iterdir()):
        if path.is_file() and path.name != "SHA256SUMS":
            with path.open("rb") as stream:
                lines.append(hashlib.file_digest(stream, "sha256").hexdigest() + "  " + path.name)
    (reports / "SHA256SUMS").write_text("\n".join(lines) + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact", type=Path)
    parser.add_argument("reports", type=Path)
    parser.add_argument("name")
    args = parser.parse_args()
    package(args.artifact.resolve(strict=True), args.reports.resolve(strict=True), args.name)
