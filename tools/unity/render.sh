#!/usr/bin/env bash
# Docker の Unity を画面あり（仮想画面 Xvfb ＋ ソフトウェア描画）で起動し、見た目を画像にする。
# 編集中のプレビューや、保存したシェイプキーの見た目を、クラウドで確かめるため。
#
# 使い方: tools/unity/render.sh [メソッド名]（既定は RenderScenes.All。EditAndSave・Tools・Layers・Select・Symmetry・Penetration を続けて描く）
#   描画の中身は tools/unity/render/RenderScenes.cs
#
# 開発用の unity/ とは別の作業用プロジェクト（tools/unity/out/render_project）を使うので、
# run-tests.sh と同時に動かしてもぶつからない。Burst は入れない（描画の確認には不要）。
# GP02_RENDER_BURST=1 なら Burst を入れる（Burst のある環境での仕上がりを見るとき）。
# ライセンスは run-tests.sh と同じ（UNITY_EMAIL と UNITY_PASSWORD、または UNITY_LICENSE_B64）。
#
# 出力（Gitには含めない）:
#   tools/unity/out/render/*.png … 画像（edit_and_save.png ＝ 編集と保存、tools.png ＝ 対称・回す・拡大縮小・元に戻すブラシ、
#                                   layers.png ＝ 重ね着（上: スカートだけ、下: スカートとペチコートをまとめて）で回す・縮める・両側を外へ引く、
#                                   select.png ＝ 点を選んで動かす（前の裾の点を矩形で選び、移動・軸ごとの拡大・法線まわりの回転・内へ押す）。select_marked.png は選んだ点の印つき、
#                                   penetration.png ＝ 貫通を直す（体の Hip_Big を 100 にして後ろの腰が突き抜けたスカートを、調べて（赤い点）押し出し、保存する。3段目はペチコートを別のレンダラーにしてまとめて直す））
#   tools/unity/out/render/unity.log
set -euo pipefail
cd "$(dirname "$0")/../.."
REPO="$(pwd)"
IMAGE="${GP02_UNITY_IMAGE:-mirror.gcr.io/unityci/editor:ubuntu-2022.3.22f1-base-3}"
METHOD="${1:-RenderScenes.All}"
OUT="$REPO/tools/unity/out/render"
PROJECT="$REPO/tools/unity/out/render_project"

LICENSE=""
USE_LOGIN=0
if [ -n "${UNITY_LICENSE_B64:-}" ]; then
  LICENSE="$(printf '%s' "$UNITY_LICENSE_B64" | base64 -d)"
elif [ -n "${UNITY_EMAIL:-}" ] && [ -n "${UNITY_PASSWORD:-}" ]; then
  USE_LOGIN=1
else
  echo "Unity のライセンスがありません。UNITY_EMAIL と UNITY_PASSWORD、または UNITY_LICENSE_B64 を用意してください。" >&2
  exit 2
fi

# 作業用プロジェクト: 設定は開発用からコピーし、ツール本体は /repo から読む
mkdir -p "$OUT" "$PROJECT/Assets/RenderScenes" "$PROJECT/Packages"
rm -f "$OUT"/*.png "$OUT/unity.log"
rm -rf "$PROJECT/ProjectSettings"
cp -r "$REPO/unity/ProjectSettings" "$PROJECT/"
cp "$REPO/tools/unity/render/RenderScenes.cs" "$REPO/tools/unity/render/RenderScenes.asmdef" "$PROJECT/Assets/RenderScenes/"
python3 -I - "$REPO/unity/Packages/manifest.json" "$PROJECT/Packages/manifest.json" "${GP02_RENDER_BURST:-0}" <<'PY'
import json, sys
manifest = json.load(open(sys.argv[1]))
deps = manifest["dependencies"]
deps["com.gp02.clothedit"] = "file:/repo/packages/com.gp02.clothedit"
if sys.argv[3] != "1":
    deps.pop("com.unity.burst", None)
json.dump(manifest, open(sys.argv[2], "w"), indent=2)
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
  -e GP02_METHOD="$METHOD" \
  -e UNITY_EMAIL -e UNITY_PASSWORD \
  -v "$REPO:/repo:ro" \
  -v "$PROJECT:/proj" \
  -v "$OUT:/out" \
  "$IMAGE" \
  bash -c '
    mkdir -p ~/.local/share/unity3d/Unity
    if [ -n "$GP02_LICENSE" ]; then
      printf "%s" "$GP02_LICENSE" > ~/.local/share/unity3d/Unity/Unity_lic.ulf
    fi
    if [ -f /gp02-ca.crt ]; then
      cp /gp02-ca.crt /usr/local/share/ca-certificates/gp02-proxy.crt
      update-ca-certificates >/dev/null 2>&1
    fi
    LC=/opt/unity/Editor/Data/Resources/Licensing/Client/Unity.Licensing.Client
    if [ "$GP02_USE_LOGIN" = 1 ]; then
      $LC --activate-ulf --username "$UNITY_EMAIL" --password "$UNITY_PASSWORD" | grep -v "$UNITY_PASSWORD"
    fi
    # -nographics を付けず、仮想画面の上で OpenGL（Mesa のソフトウェア描画）を使う
    xvfb-run -a -s "-screen 0 1280x720x24" unity-editor -batchmode -force-glcore \
      -projectPath /proj -executeMethod "$GP02_METHOD" -logFile /out/unity.log
    code=$?
    if [ "$GP02_USE_LOGIN" = 1 ]; then
      $LC --return-ulf --username "$UNITY_EMAIL" --password "$UNITY_PASSWORD" | grep -v "$UNITY_PASSWORD"
    fi
    exit $code
  ' || status=$?

echo "---- 描画 ----"
grep -h "\[GP02描画\]" "$OUT/unity.log" 2>/dev/null | sort -u || true
grep -hE "^Renderer: " "$OUT/unity.log" 2>/dev/null | head -1 || true
ls "$OUT"/*.png 2>/dev/null || echo "画像がありません（ログを確認: $OUT/unity.log）"
exit "$status"
