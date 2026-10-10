#!/usr/bin/env bash
# 見た目の確認用の動画を作る。出力は tools/preview/out/（Gitには含めない）
#   out/compare.mp4 : 紹介動画の下書き
#   out/compare.gif : チャットなどで見る用
#   out/frames/     : 1コマずつのPNG
set -euo pipefail
cd "$(dirname "$0")"
export PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

rm -rf out/frames
dotnet run --project Preview -c Release -- out/frames.json
node render.js out/frames.json out/frames
ffmpeg -y -loglevel error -framerate 30 -i out/frames/%04d.png -c:v libx264 -pix_fmt yuv420p out/compare.mp4
ffmpeg -y -loglevel error -framerate 30 -i out/frames/%04d.png \
  -vf "scale=800:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse" out/compare.gif
echo "完了: $(pwd)/out/compare.mp4, $(pwd)/out/compare.gif"
