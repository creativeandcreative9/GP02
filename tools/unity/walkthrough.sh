#!/usr/bin/env bash
# Unity 初心者の操作を、最初から最後まで自動で再現して、各段階の画面（日本語）と記録を撮る。
# （メニューで窓を開く → 衣装を選ぶ → 編集を開始 → ピン留め → つまむ → 戻す → 選んで動かす → 動作チェック → 貫通を直す →
#   保存 → 一覧 → 終了 → 閉じて開き直す → 失敗しやすい例）
# Docker の Unity を仮想画面（Xvfb）の上で動かして、BeginnerWalkthroughTests だけを実行する。ライセンスの用意は run-tests.sh と同じ。
#
# 使い方:
#   tools/unity/walkthrough.sh           … いまの unity/ のプロジェクトで動かす（Burst が入っている）
#   tools/unity/walkthrough.sh noburst   … Burst を外して動かす。BOOTH で配る .unitypackage を入れたばかりの、Burst の無いプロジェクトに近い。
#                                          manifest.json と packages-lock.json は、終わったら元に戻す
#
# 出力（Git には含めない）:
#   tools/unity/out/walkthrough/walkthrough.md … 記録（段階ごとの、初心者がしようとしたこと・実際にした操作・画面に出た文字・かかった時間・画像）
#   tools/unity/out/walkthrough/NN_名前.png    … 各段階の画面（左上から ヒエラルキー・インスペクター ／ GP02 の窓 ／ シーンビュー・コンソール）
#   tools/unity/out/walkthrough_burst/ または walkthrough_noburst/ … 同じ内容の控え（Burst あり・なしを両方動かして見比べるため）
#   tools/unity/out/editmode.xml ・ unity.log … run-tests.sh と同じ
set -euo pipefail
cd "$(dirname "$0")/../.."
REPO="$(pwd)"
OUT="$REPO/tools/unity/out"
MODE="${1:-burst}"

case "$MODE" in
  burst) ;;
  noburst) ;;
  *) echo "使い方: tools/unity/walkthrough.sh [burst|noburst]" >&2; exit 2 ;;
esac

MANIFEST="$REPO/unity/Packages/manifest.json"
LOCK="$REPO/unity/Packages/packages-lock.json"
BACKUP="$(mktemp -d)"
restore() {
  if [ -f "$BACKUP/manifest.json" ]; then cp "$BACKUP/manifest.json" "$MANIFEST"; fi
  if [ -f "$BACKUP/packages-lock.json" ]; then cp "$BACKUP/packages-lock.json" "$LOCK"; fi
  rm -rf "$BACKUP"
}
trap restore EXIT

if [ "$MODE" = noburst ]; then
  cp "$MANIFEST" "$BACKUP/manifest.json"
  cp "$LOCK" "$BACKUP/packages-lock.json"
  python3 -I - "$MANIFEST" <<'PY'
import json, sys
path = sys.argv[1]
manifest = json.load(open(path))
manifest["dependencies"].pop("com.unity.burst", None)
json.dump(manifest, open(path, "w"), indent=2)
PY
  echo "Burst を外して動かします（終わったら manifest.json と packages-lock.json を元に戻します）"
fi

status=0
GP02_GUI=1 GP02_GUI_LANG=ja GP02_WALKTHROUGH=1 GP02_UNITY_ARGS="-testFilter GP02.Tests.BeginnerWalkthroughTests" "$REPO/tools/unity/run-tests.sh" || status=$?

echo
echo "---- 初心者の通しの結果 ----"
if [ -f "$OUT/walkthrough/walkthrough.md" ]; then
  rm -rf "$OUT/walkthrough_$MODE"
  cp -r "$OUT/walkthrough" "$OUT/walkthrough_$MODE"
  echo "記録: tools/unity/out/walkthrough/walkthrough.md（控え: tools/unity/out/walkthrough_$MODE/）"
  echo "画像: $(ls "$OUT/walkthrough"/*.png 2>/dev/null | wc -l) 枚（tools/unity/out/walkthrough/）"
  echo "途中で止まった段階:"
  grep -E '^\| [0-9]+ \|.*（問題あり）' "$OUT/walkthrough/walkthrough.md" || echo "  なし"
  grep -E '^## 全体の時間' -A 3 "$OUT/walkthrough/walkthrough.md" | sed 's/^/  /' || true
else
  echo "記録ができませんでした（テストが動かなかった可能性があります。$OUT/unity.log を確認してください）"
  [ "$status" -eq 0 ] && status=1
fi
exit "$status"
