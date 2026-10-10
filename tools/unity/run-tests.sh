#!/usr/bin/env bash
# Docker の Unity（Linux）で unity/ のプロジェクトをコンパイルし、EditMode テストを実行する。
#
# 必要なもの:
#   環境変数 UNITY_LICENSE_B64 … Unity のライセンスファイル（Unity_lic.ulf）を base64 にしたもの
#     （そのままの内容を UNITY_LICENSE に入れてもよい）
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

if [ -n "${UNITY_LICENSE_B64:-}" ]; then
  LICENSE="$(printf '%s' "$UNITY_LICENSE_B64" | base64 -d)"
elif [ -n "${UNITY_LICENSE:-}" ]; then
  LICENSE="$UNITY_LICENSE"
else
  echo "Unity のライセンスがありません。環境変数 UNITY_LICENSE_B64 を設定してください。" >&2
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
rm -f "$OUT/editmode.xml" "$OUT/unity.log"

status=0
docker run --rm \
  -e GP02_LICENSE="$LICENSE" \
  -v "$REPO:/repo" \
  "$IMAGE" \
  bash -c '
    mkdir -p ~/.local/share/unity3d/Unity
    printf "%s" "$GP02_LICENSE" > ~/.local/share/unity3d/Unity/Unity_lic.ulf
    unity-editor -batchmode -nographics \
      -projectPath /repo/unity \
      -runTests -testPlatform EditMode \
      -testResults /repo/tools/unity/out/editmode.xml \
      -logFile /repo/tools/unity/out/unity.log
  ' || status=$?

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
