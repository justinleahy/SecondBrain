import ctypes
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


def load(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


inventory = load("inventory")
package = load("package")


class InventoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.artifact = self.root / "artifact"; self.artifact.mkdir()
        self.reports = self.root / "reports"
        self.repository = self.root / "repo"; self.repository.mkdir()
        (self.repository / "Directory.Packages.props").write_text('<Project><PropertyGroup><SecondBrainRuntimeVersion>10.0.12</SecondBrainRuntimeVersion></PropertyGroup></Project>')
        editor_repo = self.repository / "tools/editor"; editor_repo.mkdir(parents=True)
        self.packages = [{"name": "monaco-editor", "version": "0.57.0", "integrity": "sha512-monaco", "license": "MIT", "lockPath": "node_modules/monaco-editor"},
                         {"name": "dompurify", "version": "3.4.17", "integrity": "sha512-dompurify", "license": "MPL-2.0 OR Apache-2.0", "lockPath": "node_modules/dompurify"},
                         {"name": "marked", "version": "14.0.0", "integrity": "sha512-marked", "license": "MIT", "lockPath": "node_modules/marked"}]
        (editor_repo / "package-lock.json").write_text(json.dumps({"packages": {p["lockPath"]: p for p in self.packages}}))
        (self.artifact / "SecondBrain.Server.dll").write_bytes(b"synthetic DLL")
        (self.artifact / "libe_sqlite3.so").write_bytes(b"synthetic native SQLite")
        self.editor = self.artifact / "wwwroot/editor"; self.editor.mkdir(parents=True)
        entries = []
        for name in ("editor.js", "editor.worker.js", "editor.css"):
            value = name.encode(); (self.editor / name).write_bytes(value)
            entries.append({"file": name, "bytes": len(value), "sha256": hashlib.sha256(value).hexdigest()})
        for name in ("MONACO-LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "DOMPURIFY-LICENSE.txt"):
            (self.editor / name).write_text("test notice")
        (self.editor / "manifest.json").write_text(json.dumps({"monaco": "0.57.0", "files": entries, "packages": self.packages, "dependencyPatches": ["monaco-vendored-dompurify"]}))
        self.bom = self.root / "input.json"
        self.bom.write_text(json.dumps({"bomFormat": "CycloneDX", "specVersion": "1.6", "components": [
            {"name": "runtimepack.Microsoft.NETCore.App.Runtime.linux-x64", "version": "10.0.12", "purl": "pkg:nuget/runtimepack.Microsoft.NETCore.App.Runtime.linux-x64@10.0.12"}]}))

    def run_inventory(self):
        with patch.object(inventory.ctypes, "CDLL") as library, patch.object(inventory.subprocess, "check_output", return_value="test"):
            library.return_value.sqlite3_libversion.return_value = b"3.53.4"
            inventory.inventory(self.artifact, self.bom, self.reports, self.repository)

    def test_inventory_preserves_nuget_adds_native_and_browser_and_hashes_all_files(self):
        self.run_inventory()
        bom = json.loads((self.reports / "sbom.cdx.json").read_text())
        self.assertEqual(5, len(bom["components"]))
        self.assertEqual("pkg:nuget/Microsoft.NETCore.App.Runtime.linux-x64@10.0.12", bom["components"][0]["purl"])
        self.assertEqual("pkg:npm/monaco-editor@0.57.0", bom["components"][-3]["purl"])
        self.assertEqual("pkg:npm/dompurify@3.4.17", bom["components"][-2]["purl"])
        self.assertEqual("3.53.4", bom["components"][-4]["version"])
        self.assertEqual(9, len(json.loads((self.reports / "files.json").read_text())["files"]))
        package.package(self.artifact, self.reports, "test-linux-x64")
        self.assertIn("sbom.cdx.json", (self.reports / "SHA256SUMS").read_text())

    def test_tampered_browser_bundle_is_rejected_before_output(self):
        (self.editor / "editor.js").write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "hash mismatch"):
            self.run_inventory()
        self.assertFalse(self.reports.exists())

    def test_missing_runtime_is_rejected(self):
        self.bom.write_text('{"bomFormat":"CycloneDX","components":[]}')
        with self.assertRaisesRegex(ValueError, "NuGet"):
            self.run_inventory()

    def test_symlink_escape_is_rejected(self):
        (self.artifact / "secret").symlink_to(self.bom)
        with self.assertRaisesRegex(ValueError, "symbolic link"):
            self.run_inventory()

    def test_package_refuses_replacement_after_inventory(self):
        self.run_inventory(); (self.editor / "editor.js").write_bytes(b"changed!!")
        with self.assertRaisesRegex(ValueError, "changed after inventory"):
            package.package(self.artifact, self.reports, "test-linux-x64")
        self.assertFalse((self.reports / "test-linux-x64.tar.gz").exists())

    def test_package_refuses_added_files_after_inventory(self):
        self.run_inventory(); (self.artifact / "extra").write_text("not inventoried")
        with self.assertRaisesRegex(ValueError, "changed after inventory"):
            package.package(self.artifact, self.reports, "test-linux-x64")

    def test_unrecorded_transitive_browser_dependency_is_rejected(self):
        manifest = json.loads((self.editor / "manifest.json").read_text())
        manifest["packages"] = manifest["packages"][:1]
        (self.editor / "manifest.json").write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, "dependency inventory is incomplete"):
            self.run_inventory()

    def test_changed_browser_dependency_version_is_rejected(self):
        manifest = json.loads((self.editor / "manifest.json").read_text())
        manifest["packages"][1]["version"] = "3.4.15"
        (self.editor / "manifest.json").write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, "does not match"):
            self.run_inventory()

    def test_runtime_pack_identity_is_canonical_and_keeps_dependency_references(self):
        item = {"name": "runtimepack.Microsoft.AspNetCore.App.Runtime.linux-x64", "version": "10.0.12", "bom-ref": "original-reference"}
        inventory.normalize_runtime_packs([item])
        self.assertEqual("pkg:nuget/Microsoft.AspNetCore.App.Runtime.linux-x64@10.0.12", item["purl"])
        self.assertEqual("original-reference", item["bom-ref"])
        inventory.normalize_runtime_packs([item])
        self.assertEqual(1, len(item["properties"]))


if __name__ == "__main__":
    unittest.main()
