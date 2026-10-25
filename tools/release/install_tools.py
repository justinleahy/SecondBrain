"""Install reviewed Linux x64 tools after verifying their pinned release digests."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import platform
import tarfile
import urllib.request


def install(destination, names):
    if platform.system() != "Linux" or platform.machine() not in ("x86_64", "AMD64"):
        raise ValueError("The reviewed release toolchain targets Linux x64.")
    destination.mkdir(parents=True, exist_ok=True)
    pins = json.loads(Path(__file__).with_name("toolchain.json").read_text())
    for name in names:
        pin = pins[name]
        request = urllib.request.Request(pin["url"], headers={"User-Agent": "SecondBrain-release-tools"})
        with urllib.request.urlopen(request, timeout=120) as response:
            data = response.read(256 * 1024 * 1024 + 1)
        if len(data) > 256 * 1024 * 1024 or hashlib.sha256(data).hexdigest() != pin["sha256"]:
            raise ValueError("Release tool digest mismatch: " + name)
        if "member" in pin:
            with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as archive:
                member = archive.getmember(pin["member"])
                if not member.isfile() or member.size > 256 * 1024 * 1024:
                    raise ValueError("Invalid tool archive member: " + name)
                data = archive.extractfile(member).read()
        pending = destination / (name + ".pending")
        with pending.open("xb") as output:
            output.write(data)
        pending.chmod(0o755)
        os.replace(pending, destination / name)
        print(name + " " + pin["version"] + " verified")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("destination", type=Path)
    parser.add_argument("names", nargs="+", choices=("syft", "grype", "cosign"))
    args = parser.parse_args()
    install(args.destination.resolve(), args.names)
