#!/usr/bin/env python3
"""
packages/ の中で .meta ファイルがないファイル・フォルダに、.meta を作る。

Unity は .meta が無いと開いたときに自動で作るが、その場合は GUID がPCごとに変わり、
クラウドとローカルで食い違う。クラウドでファイルを追加したら、これを実行して .meta もコミットする。

使い方: python3 tools/unity/generate_meta.py
"""
import pathlib
import uuid

ROOT = pathlib.Path(__file__).resolve().parents[2] / "packages"

FOLDER = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

SCRIPT = """fileFormatVersion: 2
guid: {guid}
MonoImporter:
  externalObjects: {{}}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {{instanceID: 0}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

ASSEMBLY_DEFINITION = """fileFormatVersion: 2
guid: {guid}
AssemblyDefinitionImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

PACKAGE_MANIFEST = """fileFormatVersion: 2
guid: {guid}
PackageManifestImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

TEXT = """fileFormatVersion: 2
guid: {guid}
TextScriptImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

DEFAULT = """fileFormatVersion: 2
guid: {guid}
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def template_for(path: pathlib.Path) -> str:
    if path.is_dir():
        return FOLDER
    if path.name == "package.json":
        return PACKAGE_MANIFEST
    return {
        ".cs": SCRIPT,
        ".asmdef": ASSEMBLY_DEFINITION,
        ".md": TEXT,
        ".txt": TEXT,
        ".json": TEXT,
    }.get(path.suffix, DEFAULT)


def ignored(path: pathlib.Path) -> bool:
    # Unity が読み込まないもの（隠しファイル、~ で終わるフォルダ）と .meta 自身
    return path.suffix == ".meta" or any(part.startswith(".") or part.endswith("~") for part in path.parts)


def main() -> None:
    created = 0
    for package in sorted(p for p in ROOT.iterdir() if p.is_dir()):
        for path in sorted(package.rglob("*")):
            relative = path.relative_to(package)
            if ignored(relative):
                continue
            meta = path.with_name(path.name + ".meta")
            if meta.exists():
                continue
            meta.write_text(template_for(path).format(guid=uuid.uuid4().hex), encoding="utf-8")
            created += 1
            print(f"作成: {meta.relative_to(ROOT.parent)}")
    print(f"{created} 個の .meta を作成しました")


if __name__ == "__main__":
    main()
