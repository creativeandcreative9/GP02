# GP02

VRChatアバター改変向けの、Unity上で動くメッシュ編集ツールを開発する。
コンセプトや方針の根拠は `20261010_企画概要.md` を参照。

## 開発の分担
- **計算の中核**（粒子の計算、ピン留め、硬さ、衝突、シェイプキーの差分計算、貫通の検出）
  - Unityに依存しないC#（netstandard2.1）で書き、クラウド環境で自動テストする
- **見た目の確認**
  - 計算結果をJSONに書き出し、ブラウザ（Chromium）で描画して画像・動画にする。クラウド環境で行う
- **Unityとのつなぎ**（ブラシ操作の画面、メッシュの読み書き、BlendShapeの追加、NDMF対応）
  - ローカルのUnity 2022.3.22f1で確認する

## フォルダ構成
```
GP02/
├── src/Core/          計算の中核（Unity非依存のC#）
├── tests/Core.Tests/  計算の中核の自動テスト
├── tools/preview/     見た目の確認（ブラウザ描画・動画化）
└── unity/             Unityとのつなぎ（ローカルで確認。未着手）
```

## よく使うコマンド
- テスト: `dotnet test tests/Core.Tests`
- 見た目の確認（従来方式と粒子方式を並べた動画）: `tools/preview/run.sh`
  - 出力は `tools/preview/out/`（Gitには含めない）

## クラウド環境
- .NET 8 SDK を使う。新しいセッションで入っていない場合は、環境設定の Setup script に次を追加する
  - `curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir $HOME/.dotnet`

## 注意
- リポジトリはpublic。購入したアバター・衣装のデータや、APIキーなどの秘密情報は入れない
- テスト用の形状は、プログラムで生成したもの（布、スカート状の形など）を使う

## 後回しにしていること（忘れないこと）
- **Unity部分の自動チェック（GitHub Actions）**
  - 導入時は、**Unityのライセンス情報をGitHubのシークレットに登録する必要がある**

## ルール
@rule/01_コーディング規約.md
