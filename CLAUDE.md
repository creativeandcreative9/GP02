# GP02

VRChatアバター改変向けの、Unity上で動くメッシュ編集ツールを開発する。
コンセプトや方針の根拠は `20261010_企画概要.md` を参照。
開発目標と進捗は `20261010_開発目標と進捗.md` にまとめる（作業が進んだら更新する）。

## 開発の分担
- **計算の中核**（粒子の計算、ピン留め、硬さ、衝突、シェイプキーの差分計算、貫通の検出）
  - Unityに依存しないC#（netstandard2.1）で書き、クラウド環境で自動テストする
  - ソースは Unity用パッケージの中（`packages/com.gp02.clothedit/Editor/Core/`）に置き、`src/Core/Core.csproj` はそれを参照してビルドするだけ
- **見た目の確認**
  - 計算結果をJSONに書き出し、ブラウザ（Chromium）で描画して画像・動画にする。クラウド環境で行う
- **Unityとのつなぎ**（ブラシ操作の画面、メッシュの読み書き、BlendShapeの追加）
  - ローカルのUnity 2022.3.22f1（VRChat SDK なし）で確認する

## VRChat との関係
- VRChat 向けに作るが、**VRChat SDK も NDMF も無い素の Unity で、すべての機能が動くこと**
- ツール本体（`GP02.Core`、`GP02.Editor`）から VRChat SDK・NDMF を参照しない
- NDMF 連携など VRChat 向けの機能は、別の asmdef に分け、対象のパッケージが入っているときだけ有効にする（`versionDefines` と `defineConstraints` を使う）
- 開発用の `unity/` には VRChat SDK を入れない。**VRChat 環境でのテストは最後の段階だけ**行う

## フォルダ構成
```
GP02/
├── packages/com.gp02.clothedit/   ツール本体（Unity用パッケージ）
│   └── Editor/
│       ├── Core/                  計算の中核（Unity非依存のC#）
│       └── *.cs                   Unityとのつなぎ（ローカルで確認）
├── src/Core/                      計算の中核をクラウドでビルドするための csproj（ソースは置かない）
├── tests/Core.Tests/              計算の中核の自動テスト
├── tools/preview/                 見た目の確認（ブラウザ描画・動画化）
├── tools/bench/                   速度と布の伸びの測定
├── tools/unity/                   Unity用の補助（.meta の作成）
└── unity/                         開発用のUnityプロジェクト（VRChat SDK なし。開き方は 20261010_Unity環境の導入手順.md）
```

## よく使うコマンド
- テスト: `dotnet test tests/Core.Tests`
- 見た目の確認（従来方式と粒子方式を並べた動画）: `tools/preview/run.sh`
  - 出力は `tools/preview/out/`（Gitには含めない）
- 速度の測定: `dotnet run --project tools/bench/Bench -c Release`
- Unity でのコンパイルと EditMode テスト（Docker）: `tools/unity/run-tests.sh`
  - Unity のライセンスが必要。クラウド環境では環境変数 `UNITY_EMAIL` と `UNITY_PASSWORD`（Unity ID）を使う。コンテナの中で Personal ライセンスを有効化し、終わったら返却する（動作確認済み）
    - 代わりに `UNITY_LICENSES_ZIP_B64`（Windows の `%LOCALAPPDATA%\Unity\licenses` の中身を zip→base64）や `UNITY_LICENSE_B64`（従来の Unity_lic.ulf を base64）も使える
  - クラウド環境のプロキシと証明書（`/root/.ccr/ca-bundle.crt`）は、スクリプトがコンテナに渡す
  - イメージは `mirror.gcr.io/unityci/editor:ubuntu-2022.3.22f1-base-3`（Docker Hub は匿名の取得上限にかかるためミラーを使う）
  - 結果は `tools/unity/out/`。2回目以降は `unity/Library` が残るので速い
  - 画面（ウィンドウ・インスペクター）の描画を実際に動かすテスト（`ShapeKeyGuiSmokeTests`・`ClothEditWindowGuiSmokeTests`）は、通常の実行（`-nographics`）では飛ばす。`GP02_GUI=1 tools/unity/run-tests.sh` で仮想画面（Xvfb）の上で、ほかのテストと一緒に動かすと、画面の画像が `tools/unity/out/gui/` に出る（`GP02_UNITY_ARGS="-testFilter GP02.Tests.ShapeKeyGuiSmokeTests"` のように絞れる）
    - `GP02_GUI_LANG=ja` を付けると、日本語の画面で撮る（日本語のフォント Noto Sans CJK は `tools/unity/fetch-jp-font.sh` で `tools/unity/out/fonts` に取得してコンテナに入れる。Git には入れない）。付けなければ英語の画面
- Unity での見た目の確認（Docker、画面あり）: `tools/unity/render.sh`
  - 仮想画面（Xvfb）とソフトウェア描画（Mesa の llvmpipe）で Unity を起動し、テスト用の衣装の編集前・編集中のプレビュー・保存したシェイプキーを画像にする
  - 描画の中身は `tools/unity/render/RenderScenes.cs`。出力は `tools/unity/out/render/`（`edit_and_save.png` に編集と保存を、`tools.png` に対称でつまむ・回す・拡大縮小・元に戻すブラシの結果を、それぞれ1枚にまとめる。動作チェックは `pose_check.png`・`pose_escape.png`。`tools/unity/render.sh RenderScenes.PoseCheck` でそれだけ描ける）
  - 開発用の `unity/` とは別の作業用プロジェクトを使うので、`run-tests.sh` と同時に動かせる
  - `-executeMethod` では毎フレームの更新が回らないので、SkinnedMeshRenderer は `BakeMesh` で取り出して描く
- 初心者の通しの再現（Docker、画面あり）: `tools/unity/walkthrough.sh`（窓を開く → 衣装を選ぶ → 編集 → 動作チェック → 保存 → 失敗しやすい例までを自動で再現し、各段階の日本語の画面 `NN_名前.png` と記録 `walkthrough.md` を `tools/unity/out/walkthrough/` に出す。`noburst` を付けると Burst なし。中身は `Tests/Editor/BeginnerWalkthroughTests*.cs`で、`GP02_GUI=1` かつ `GP02_WALKTHROUGH=1` のときだけ動く）
- NDMF 連携のテスト（Docker、NDMF 入りの作業用プロジェクト）: `tools/unity/ndmf-tests.sh`
  - NDMF 1.14.8 を VPM から取得して `tools/unity/out/` の作業用プロジェクトに入れ、`Editor/NDMF/` の Plugin と `Tests/Editor/NDMF/` のテスト（NDMF があるときだけ有効）を、通常のテストと一緒に動かす。開発用の `unity/` には NDMF を入れない
- `.meta` の作成: `python3 tools/unity/generate_meta.py`
  - `packages/` にファイルやフォルダを追加したら、必ず実行して `.meta` もコミットする（Unityに自動で作らせるとGUIDがPCごとに変わるため）

## クラウド環境
- .NET 8 SDK を使う。新しいセッションで入っていない場合は、環境設定の Setup script に次を追加する
  - `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir $HOME/.dotnet`

## 注意
- リポジトリはpublic。購入したアバター・衣装のデータや、APIキーなどの秘密情報は入れない
- テスト用の形状は、プログラムで生成したもの（布、スカート状の形など）を使う

## 決めたこと
- 紹介動画は、完成間近になってから作る（`tools/preview` の比較動画は検証用）
- 実アバターでの確認には、著作権フリーのアバターを用意する（VRoidで自作する予定）
  - リポジトリに入れる場合は、ライセンスが再配布を認めているか（CC0など）を確認する
- 布のシワの細かい再現は重視しない（既存ツールでも表現しきれていないため）
- 粒子はメッシュの頂点と切り離し、表面に1cm間隔（アバターの実寸）で配置する。頂点は近くの粒子についていく
- 計算は、つまんだ部品（島）全体で行う（周りだけに絞ると、外側を固定するため境目が伸びる）
- 配布は BOOTH の .unitypackage を想定する（依存パッケージは自動では入らないので、増やすときは慎重に）
- 速さは Unity（Mono）で測る。クラウドの .NET より10倍以上遅いことがある
  - Mono では System.Numerics.Vector3 の演算子が1つずつ関数の呼び出しになるので、毎フレーム回る処理では成分ごとに計算する
  - Unity での測定は、一時的な EditMode テストを `unity/Assets/` に置き、`GP02_UNITY_ARGS=-debugCodeOptimization tools/unity/run-tests.sh` で行う（利用者のエディタの既定はデバッグモードで、それと同じ遅い条件で測るため。バッチモードの既定のままだと速く出る）
  - Burst は任意の高速化として使う（`Editor/Burst/` の `GP02.Editor.Burst`。com.unity.burst があるときだけ `versionDefines` と `defineConstraints` で有効になり、`ClothSolver.DefaultKernel` を差し替える）。Burst がなくても、すべての機能が C# で動く
  - 開発用の `unity/` には Burst を入れてある。Burst なしの確認は、manifest から一時的に外して `tools/unity/run-tests.sh` を回す

## 未解決の課題
- 粒子化した布で、ドラッグ直後に局所的な伸びが残る（スカート相当・10cm引っ張り・反復40回で、粒子間の伸びが最大約27%）
  - 制約どうしの矛盾ではなく、収束の遅さが原因。粗い段（2cm・4cm）から解く仕組みの効果は小さい
  - 対策として「ドラッグ中は粗く、手を止めたら仕上げる」（`ClothSolver.Settle`）を入れた
    - 10cm引っ張り: 10%以下まで約0.3秒、5%以下まで約0.9秒。3cm引っ張り: 5%以下まで約0.07秒
  - Unityで実際に触って品質が足りなければ、計算方法を Projective Dynamics（全体を連立方程式で解く）に変えることを検討する

- 布の長さで届かない位置まで引いたときの伸び: つまんだ範囲を柔らかい制約にし（`ClothSolver.BrushCompliance`）、体や内側の布までの隙間は内側へ入れるようにして、大きく減らした（スカートを外へ10cmで約54%→約4%）
  - ただし隙間が無い場合（ぴったりの服）は伸びが残りやすい。詳しくは開発目標と進捗.md の「課題とリスク」
  - 重ね着の外側は、層どうしの接触（`LayerContacts`、`ClothSolver.SetLayerContacts`）で内側の層を押して動けるようにした（同じメッシュの別の島も、まとめて編集する別のレンダラーも同じ）。外の布が内の布を突き抜けなくなった
    - 残る課題: 内側の層も一緒に計算するので粒子が約2倍になり、Burst の無い C# では1コマの反復が減って、仕上げの時間切れまでに伸びが下がりきらないことがある。大きく回すと、頂点の補間で 1〜2mm 内側の層に入ることがある

## 後回しにしていること（忘れないこと）
- **Unity部分の自動チェック（GitHub Actions）**
  - 導入時は、**Unityのライセンス情報をGitHubのシークレットに登録する必要がある**

## ルール
@rule/01_コーディング規約.md
