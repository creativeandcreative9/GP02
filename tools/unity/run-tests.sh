#!/usr/bin/env bash
# Docker の Unity（Linux）で unity/ のプロジェクトをコンパイルし、EditMode テストを実行する。
#
# 必要なもの（どちらか）:
#   環境変数 UNITY_LICENSES_ZIP_B64 … Windows の %LOCALAPPDATA%\Unity\licenses の中身を zip にして base64 にしたもの
#     （新しい Unity Hub の方式。Linux 版の Unity は ~/.config/unity3d/Unity/licenses から読む）
#   環境変数 UNITY_LICENSE_B64 … 従来のライセンスファイル（Unity_lic.ulf）を base64 にしたもの
#   環境変数が使えない場合は、UNITY_LICENSES_ZIP_B64 と同じ内容をファイル ~/.gp02/unity_licenses_zip.b64 に置いてもよい
#   （クラウド環境のセットアップスクリプトで書き出す）
#
# 出力（Gitには含めない）:
#   tools/unity/out/editmode.xml … テスト結果
#   tools/unity/out/unity.log    … Unity のログ（コンパイルエラーはここに出る）
#
# 2回目以降は unity/Library が残るので、読み込みが速くなる。
set -euo pipefail
cd "$(dirname "$0")/../.."
REPO="$(pwd)"
IMAGE="${GP02_UNITY_IMAGE:-mirror.gcr.io/unityci/editor:ubuntu-2022.3.22f1-base-3}"
OUT="$REPO/tools/unity/out"

LICENSE=""
LICENSES_FILE="$HOME/.gp02/unity_licenses_zip.b64"
if [ -z "${UNITY_LICENSES_ZIP_B64:-}" ] && [ -s "$LICENSES_FILE" ]; then
  UNITY_LICENSES_ZIP_B64="$(tr -d '[:space:]' < "$LICENSES_FILE")"
fi
LICENSES_DIR="$OUT/licenses"
rm -rf "$LICENSES_DIR"
mkdir -p "$LICENSES_DIR"
if [ -n "${UNITY_LICENSES_ZIP_B64:-}" ]; then
  printf '%s' "$UNITY_LICENSES_ZIP_B64" | base64 -d > "$OUT/licenses.zip"
  python3 -I -m zipfile -e "$OUT/licenses.zip" "$LICENSES_DIR"
  rm -f "$OUT/licenses.zip"
elif [ -n "${UNITY_LICENSE_B64:-}" ]; then
  LICENSE="$(printf '%s' "$UNITY_LICENSE_B64" | base64 -d)"
else
  echo "Unity のライセンスがありません。環境変数 UNITY_LICENSES_ZIP_B64 か UNITY_LICENSE_B64、またはファイル $LICENSES_FILE を用意してください。" >&2
  exit 2
fi

# クラウド環境では Docker の本体が止まっていることがあるので、必要なら起動する
if ! docker info >/dev/null 2>&1; then
  (dockerd > /tmp/dockerd.log 2>&1 &)
  for _ in $(seq 1 30); do docker info >/dev/null 2>&1 && break; sleep 1; done
fi

# Docker Hub は匿名だと取得回数の上限にかかりやすいので、既定ではミラー（mirror.gcr.io）から取る
docker image inspect "$IMAGE" >/dev/null 2>&1 || docker pull "$IMAGE"

mkdir -p "$OUT"
rm -f "$OUT/editmode.xml" "$OUT/unity.log" "$OUT/licensing-client.log"

status=0
docker run --rm \
  -e GP02_LICENSE="$LICENSE" \
  -v "$REPO:/repo" \
  "$IMAGE" \
  bash -c '
    mkdir -p ~/.local/share/unity3d/Unity ~/.config/unity3d/Unity/licenses
    if [ -n "$GP02_LICENSE" ]; then
      printf "%s" "$GP02_LICENSE" > ~/.local/share/unity3d/Unity/Unity_lic.ulf
    fi
    cp -r /repo/tools/unity/out/licenses/. ~/.config/unity3d/Unity/licenses/
    unity-editor -batchmode -nographics \
      -projectPath /repo/unity \
      -runTests -testPlatform EditMode \
      -testResults /repo/tools/unity/out/editmode.xml \
      -logFile /repo/tools/unity/out/unity.log
    code=$?
    # ライセンスまわりの調査用に、ライセンス管理のログも残す
    cp ~/.config/unity3d/Unity/Unity.Licensing.Client.log /repo/tools/unity/out/licensing-client.log 2>/dev/null
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
