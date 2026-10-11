#!/usr/bin/env bash
# NDMF 入りの作業用プロジェクトで、Docker の Unity の EditMode テストを実行する。
# NDMF 連携（packages/com.gp02.clothedit/Editor/NDMF/）と、そのテスト（Tests/Editor/NDMF/）は、NDMF があるときだけ有効になるため、
# NDMF の入っていない開発用の unity/（run-tests.sh）では動かない。ここで確かめる。
#
# 使い方: tools/unity/ndmf-tests.sh
#
# 作業用プロジェクト（tools/unity/out/ndmf_project）は、開発用の unity/ とは別なので、run-tests.sh と同時に動かしてもぶつからない。
#   - NDMF は VPM（https://vpm.nadena.dev）から取得して、そのプロジェクトの Packages/ に置く（リポジトリには入れない。MIT ライセンス）
#   - NDMF は本来 VRChat SDK の中で使う前提なので、VRChat SDK の無い素の Unity で読み込めるよう、作業用の写しだけ書き換える:
#     asmdef から VRC.* の参照を外し、VRChat SDK が同梱する 0Harmony.dll などの DLL を NDMF 付属のものから足す
#   - com.unity.collections 2.1.4 と com.unity.mathematics 1.2.6 を manifest に足す（NDMF の依存）
#   - GP02 のテストは通常のものも含めてすべて動く（NDMF のテストは Tests/Editor/NDMF/）
#
# ライセンスは run-tests.sh と同じ（UNITY_LICENSES_ZIP_B64、UNITY_LICENSE_B64、または UNITY_EMAIL と UNITY_PASSWORD）。
# 追加の引数: 環境変数 GP02_UNITY_ARGS に入れると Unity に渡す（例: GP02_UNITY_ARGS="-testFilter GP02.Tests.NdmfPluginTests"）
#
# 出力（Gitには含めない）:
#   tools/unity/out/ndmf/editmode.xml … テスト結果
#   tools/unity/out/ndmf/unity.log    … Unity のログ（コンパイルエラーはここに出る）
set -euo pipefail
cd "$(dirname "$0")/../.."
REPO="$(pwd)"
IMAGE="${GP02_UNITY_IMAGE:-mirror.gcr.io/unityci/editor:ubuntu-2022.3.22f1-base-3}"
OUT="$REPO/tools/unity/out/ndmf"
PROJECT="$REPO/tools/unity/out/ndmf_project"
DOWNLOAD="$REPO/tools/unity/out/ndmf_download"
NDMF_VERSION="1.14.8"
NDMF_URL="https://packages.vpm.nadena.dev/repositories/nadena.dev/packages/nadena.dev.ndmf/$NDMF_VERSION/e1103ea150b9f1d49d415d632ed4da4e46cd8121a1f1f7ea917b155193a1ab83.zip"
NDMF_SHA256="e1103ea150b9f1d49d415d632ed4da4e46cd8121a1f1f7ea917b155193a1ab83"

LICENSE=""
USE_LOGIN=0
LICENSES_FILE="$HOME/.gp02/unity_licenses_zip.b64"
if [ -z "${UNITY_LICENSES_ZIP_B64:-}" ] && [ -s "$LICENSES_FILE" ]; then
  UNITY_LICENSES_ZIP_B64="$(tr -d '[:space:]' < "$LICENSES_FILE")"
fi
LICENSES_DIR="$REPO/tools/unity/out/ndmf_licenses"
rm -rf "$LICENSES_DIR"
mkdir -p "$LICENSES_DIR"
if [ -n "${UNITY_LICENSES_ZIP_B64:-}" ]; then
  printf '%s' "$UNITY_LICENSES_ZIP_B64" | base64 -d > "$LICENSES_DIR.zip"
  python3 -I -m zipfile -e "$LICENSES_DIR.zip" "$LICENSES_DIR"
  rm -f "$LICENSES_DIR.zip"
elif [ -n "${UNITY_LICENSE_B64:-}" ]; then
  LICENSE="$(printf '%s' "$UNITY_LICENSE_B64" | base64 -d)"
elif [ -n "${UNITY_EMAIL:-}" ] && [ -n "${UNITY_PASSWORD:-}" ]; then
  USE_LOGIN=1
else
  echo "Unity のライセンスがありません。UNITY_LICENSES_ZIP_B64 か UNITY_LICENSE_B64、UNITY_EMAIL と UNITY_PASSWORD を用意してください。" >&2
  exit 2
fi

# NDMF の取得（取得済みなら使い回す）。中身を確かめてから展開する
mkdir -p "$DOWNLOAD"
if [ ! -f "$DOWNLOAD/ndmf-$NDMF_VERSION.zip" ] || [ "$(sha256sum "$DOWNLOAD/ndmf-$NDMF_VERSION.zip" | cut -d' ' -f1)" != "$NDMF_SHA256" ]; then
  curl -fsSL -o "$DOWNLOAD/ndmf-$NDMF_VERSION.zip" "$NDMF_URL"
fi
if [ "$(sha256sum "$DOWNLOAD/ndmf-$NDMF_VERSION.zip" | cut -d' ' -f1)" != "$NDMF_SHA256" ]; then
  echo "NDMF のハッシュが一致しません。" >&2
  exit 1
fi

# 作業用プロジェクト: 設定は開発用からコピーし、ツール本体は /repo から読む
mkdir -p "$OUT" "$PROJECT/Assets" "$PROJECT/Packages"
rm -f "$OUT/editmode.xml" "$OUT/unity.log"
rm -rf "$PROJECT/ProjectSettings"
cp -r "$REPO/unity/ProjectSettings" "$PROJECT/"
python3 -I - "$REPO/unity/Packages/manifest.json" "$PROJECT/Packages/manifest.json" "$DOWNLOAD/ndmf-$NDMF_VERSION.zip" "$PROJECT/Packages/nadena.dev.ndmf" "$NDMF_VERSION" <<'PY'
import json, os, shutil, sys, zipfile

manifest_in, manifest_out, zip_path, package_dir, version = sys.argv[1:6]

# manifest: 開発用に NDMF の依存を足す。NDMF 自体は Packages/ に置く（埋め込みパッケージ）
manifest = json.load(open(manifest_in))
deps = manifest["dependencies"]
deps["com.gp02.clothedit"] = "file:/repo/packages/com.gp02.clothedit"
deps["com.unity.collections"] = "2.1.4"
deps["com.unity.mathematics"] = "1.2.6"
json.dump(manifest, open(manifest_out, "w"), indent=2)

# NDMF の展開と書き換え（版が同じなら、前回のものを使い回して Unity の再取り込みを避ける）
stamp = os.path.join(package_dir, ".gp02-prepared")
version = version + "+prepared2"   # 書き換えの内容を変えたら、ここを上げて作り直す
if os.path.exists(stamp) and open(stamp).read().strip() == version:
    sys.exit(0)
shutil.rmtree(package_dir, ignore_errors=True)
os.makedirs(package_dir)
with zipfile.ZipFile(zip_path) as z:
    for name in z.namelist():
        target = os.path.realpath(os.path.join(package_dir, name))
        if not target.startswith(os.path.realpath(package_dir) + os.sep):
            raise SystemExit("不正なパス: " + name)
    z.extractall(package_dir)

# VRChat SDK の無い素の Unity で読み込めるようにする
path = os.path.join(package_dir, "Editor", "nadena.dev.ndmf.asmdef")
asmdef = json.load(open(path))
asmdef["references"] = [r for r in asmdef["references"] if not r.startswith("VRC.")]
asmdef["precompiledReferences"] = [r for r in asmdef["precompiledReferences"] if not r.startswith("VRCSDK")]
json.dump(asmdef, open(path, "w"), indent=4)

# VRChat SDK が同梱するはずの DLL は、NDMF 付属のもの（Dependencies~）を使う（Immutable は Unsafe を必要とする）
deps_dir = os.path.join(package_dir, "Dependencies~")
for dll in ("0Harmony.dll", "System.Collections.Immutable.dll", "System.Runtime.CompilerServices.Unsafe.dll"):
    shutil.copy(os.path.join(deps_dir, dll), os.path.join(package_dir, "Editor", dll))
open(stamp, "w").write(version)
PY

if ! docker info >/dev/null 2>&1; then
  # 前回の Docker の記録が残っていると起動できないので、動いていなければ消す
  if ! pgrep -x dockerd >/dev/null; then rm -f /var/run/docker.pid; fi
  (dockerd > /tmp/dockerd.log 2>&1 &)
  for _ in $(seq 1 30); do docker info >/dev/null 2>&1 && break; sleep 1; done
fi
docker image inspect "$IMAGE" >/dev/null 2>&1 || docker pull "$IMAGE"

DOCKER_ARGS=()
if [ -n "${HTTPS_PROXY:-}" ]; then
  DOCKER_ARGS+=(--network host -e "HTTPS_PROXY=$HTTPS_PROXY" -e "https_proxy=$HTTPS_PROXY")
fi
CA_BUNDLE="${GP02_CA_BUNDLE:-/root/.ccr/ca-bundle.crt}"
if [ -s "$CA_BUNDLE" ]; then
  DOCKER_ARGS+=(-v "$CA_BUNDLE:/gp02-ca.crt:ro")
fi

status=0
docker run --rm \
  "${DOCKER_ARGS[@]}" \
  -e GP02_LICENSE="$LICENSE" \
  -e GP02_USE_LOGIN="$USE_LOGIN" \
  -e GP02_UNITY_ARGS="${GP02_UNITY_ARGS:-}" \
  -e UNITY_EMAIL -e UNITY_PASSWORD \
  -v "$REPO:/repo:ro" \
  -v "$PROJECT:/proj" \
  -v "$OUT:/out" \
  -v "$LICENSES_DIR:/gp02-licenses:ro" \
  "$IMAGE" \
  bash -c '
    mkdir -p ~/.local/share/unity3d/Unity ~/.config/unity3d/Unity/licenses
    if [ -n "$GP02_LICENSE" ]; then
      printf "%s" "$GP02_LICENSE" > ~/.local/share/unity3d/Unity/Unity_lic.ulf
    fi
    cp -r /gp02-licenses/. ~/.config/unity3d/Unity/licenses/
    if [ -f /gp02-ca.crt ]; then
      cp /gp02-ca.crt /usr/local/share/ca-certificates/gp02-proxy.crt
      update-ca-certificates >/dev/null 2>&1
    fi
    LC=/opt/unity/Editor/Data/Resources/Licensing/Client/Unity.Licensing.Client
    if [ "$GP02_USE_LOGIN" = 1 ]; then
      $LC --activate-ulf --username "$UNITY_EMAIL" --password "$UNITY_PASSWORD" | grep -v "$UNITY_PASSWORD"
    fi
    unity-editor -batchmode -nographics \
      -projectPath /proj \
      -runTests -testPlatform EditMode \
      -testResults /out/editmode.xml \
      -logFile /out/unity.log $GP02_UNITY_ARGS
    code=$?
    # 使い捨てのコンテナで有効化したライセンスは、毎回返却して有効化の数を溜めない
    if [ "$GP02_USE_LOGIN" = 1 ]; then
      $LC --return-ulf --username "$UNITY_EMAIL" --password "$UNITY_PASSWORD" | grep -v "$UNITY_PASSWORD"
    fi
    exit $code
  ' || status=$?

# ライセンスの写しは残さない
rm -rf "$LICENSES_DIR"

echo "---- ライセンス ----"
grep -hE "License is not active|No valid Unity Editor license|Successfully (activated|resolved)|HasEntitlements" "$OUT/unity.log" 2>/dev/null | sort -u | head -5 || true

echo "---- コンパイルエラー ----"
grep -E "error CS[0-9]+" "$OUT/unity.log" 2>/dev/null | sort -u || echo "なし"

echo "---- テスト結果 ----"
if [ -f "$OUT/editmode.xml" ]; then
  python3 -I - "$OUT/editmode.xml" <<'PY'
import sys
import xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
print(f"合計 {root.get('total')} / 成功 {root.get('passed')} / 失敗 {root.get('failed')} / スキップ {root.get('skipped')}")
ndmf = [c for c in root.iter("test-case") if "NdmfPluginTests" in (c.get("fullname") or "")]
print(f"  うち NDMF のテスト {len(ndmf)} 件（成功 {sum(1 for c in ndmf if c.get('result') == 'Passed')}）")
for case in root.iter("test-case"):
    if case.get("result") == "Failed":
        message = case.findtext("failure/message", default="").strip()
        print(f"  失敗: {case.get('fullname')}\n    {message}")
PY
else
  echo "テスト結果がありません（ログを確認: $OUT/unity.log）"
fi

# Unity の終了コード: 0 = 成功、2 = 失敗したテストあり、3 = コンパイルエラーなどで実行できず
exit "$status"
