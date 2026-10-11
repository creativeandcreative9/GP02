#!/usr/bin/env bash
# 画面の画像を日本語で撮るためのフォント（Noto Sans CJK、SIL Open Font License）を tools/unity/out/fonts に用意する。
# Docker の Unity には日本語のフォントが無く、日本語の文字が空白になるため。Unity（Linux）は、入っている Noto Sans CJK を自動で使う。
# フォントは Git に入れない（out/ は除外している）。2回目からは何もしない。
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
FONTS="$REPO/tools/unity/out/fonts"
if [ -s "$FONTS/NotoSansCJK-Regular.ttc" ]; then exit 0; fi
mkdir -p "$FONTS"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
# Ubuntu 22.04 の fonts-noto-cjk。プロキシが http を通さないので https のミラーから取る
URL="https://mirrors.edge.kernel.org/ubuntu/pool/main/f/fonts-noto-cjk/fonts-noto-cjk_20220127%2Brepack1-1_all.deb"
echo "日本語のフォントを取得しています（約60MB）…"
curl -fsSL --max-time 600 -o "$WORK/noto.deb" "$URL"
if command -v dpkg-deb >/dev/null 2>&1; then
  dpkg-deb -x "$WORK/noto.deb" "$WORK/x"
else
  (cd "$WORK" && ar x noto.deb && mkdir -p x && tar -xf data.tar.* -C x)
fi
cp "$WORK/x/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc" "$WORK/x/usr/share/fonts/opentype/noto/NotoSansCJK-Bold.ttc" "$FONTS/"
echo "用意しました: $FONTS"
