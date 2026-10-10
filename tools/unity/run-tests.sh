#!/usr/bin/env bash
# Docker の Unity（Linux）で unity/ のプロジェクトをコンパイルし、EditMode テストを実行する。
#
# 必要なもの（どれか）:
#   環境変数 UNITY_EMAIL と UNITY_PASSWORD … CI専用の Unity アカウント（2段階認証なし）。Docker の中でサインインしてライセンスを取る
#     （PCで作ったライセンスは PC に結びついていて Docker では使えないため、これを優先する）
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
if [ -n "${UNITY_EMAIL:-}" ] && [ -n "${UNITY_PASSWORD:-}" ]; then
  : # Docker の中でサインインする（下の docker run で渡す）
elif [ -n "${UNITY_LICENSES_ZIP_B64:-}" ]; then
  printf '%s' "$UNITY_LICENSES_ZIP_B64" | base64 -d > "$OUT/licenses.zip"
  python3 -I -m zipfile -e "$OUT/licenses.zip" "$LICENSES_DIR"
  rm -f "$OUT/licenses.zip"
elif [ -n "${UNITY_LICENSE_B64:-}" ]; then
  LICENSE="$(printf '%s' "$UNITY_LICENSE_B64" | base64 -d)"
else
  echo "Unity のライセンスがありません。環境変数 UNITY_EMAIL と UNITY_PASSWORD（CI専用アカウント）、UNITY_LICENSES_ZIP_B64、UNITY_LICENSE_B64、またはファイル $LICENSES_FILE を用意してください。" >&2
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

# クラウド環境の通信は中継（プロキシ）の証明書で暗号化されているので、Docker の中でも信頼させる。
# これが無いと Unity のライセンス管理がサーバーにつながらない（UntrustedRoot）
CA_ARGS=()
CA_FILE="${GP02_EXTRA_CA:-/root/.ccr/ca-bundle.crt}"
if [ -s "$CA_FILE" ]; then
  CA_ARGS=(-v "$CA_FILE:/usr/local/share/ca-certificates/gp02-extra.crt:ro")
fi

status=0
# パスワードはコマンドラインに値を書かず、環境変数の名前だけで渡す
UNITY_EMAIL="${UNITY_EMAIL:-}" UNITY_PASSWORD="${UNITY_PASSWORD:-}" \
docker run --rm \
  -e GP02_LICENSE="$LICENSE" \
  -e UNITY_EMAIL -e UNITY_PASSWORD \
  "${CA_ARGS[@]}" \
  -v "$REPO:/repo" \
  "$IMAGE" \
  bash -c '
    update-ca-certificates >/dev/null 2>&1 || true
    mkdir -p ~/.local/share/unity3d/Unity ~/.config/unity3d/Unity/licenses
    if [ -n "$GP02_LICENSE" ]; then
      printf "%s" "$GP02_LICENSE" > ~/.local/share/unity3d/Unity/Unity_lic.ulf
    fi
    cp -r /repo/tools/unity/out/licenses/. ~/.config/unity3d/Unity/licenses/
    login=()
    if [ -n "$UNITY_EMAIL" ] && [ -n "$UNITY_PASSWORD" ]; then
      login=(-username "$UNITY_EMAIL" -password "$UNITY_PASSWORD")
    fi
    unity-editor -batchmode -nographics "${login[@]}" \
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

# Unity はコマンドラインの引数をログに書くので、パスワードを伏せる
if [ -n "${UNITY_PASSWORD:-}" ]; then
  for f in "$OUT/unity.log" "$OUT/licensing-client.log"; do
    [ -f "$f" ] || continue
    UNITY_PASSWORD="$UNITY_PASSWORD" python3 -I -c '
import os, sys
p = sys.argv[1]
s = open(p, encoding="utf-8", errors="replace").read()
open(p, "w", encoding="utf-8").write(s.replace(os.environ["UNITY_PASSWORD"], "********"))
' "$f"
  done
fi

echo "---- ライセンス ----"
grep -hE "License is not active|No valid Unity Editor license|Successfully (activated|resolved)|HasEntitlements|Failed to login|LoginRequest" "$OUT/unity.log" 2>/dev/null | sort -u | head -5 || true

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
