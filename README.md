# ClipCompose

[English](README.en.md) | 日本語

ClipCompose は、Windows 11 向けの常駐アプリです。登録しておいたテンプレート（プロンプト・定型文）を呼び出し、必要な場合だけクリップボードの文章を `{{clipboard}}` の位置へ差し込んで、結果をコピーまたは自動で貼り付けます。Copilot などの生成 AI に渡すプロンプトや、メール・チャットの定型文に使えます。

このリポジトリは、一般公開している ClipCompose のソースコードです。

## 主な機能

- カテゴリとテンプレートの 2 階層で登録・編集・複製・並べ替え・検索（設定画面の［テンプレート］タブ）
- テンプレートの本文に `{{clipboard}}` を書くと、実行時にクリップボードの文章へ置き換え。`{{clipboard}}` の無いテンプレートは本文をそのままコピー（クリップボードを読まない）
- 選択画面: カテゴリ見出し付きの一覧から選んで Enter。カテゴリの折りたたみ、テンプレート名・カテゴリ名での検索、本文の表示、ピンでの固定、キー操作・ドラッグでの並べ替え
- 固定テンプレート: 1 件を固定しておき、固定モードのホットキーで選択画面を出さずに実行
- ホットキー（通常モード・固定モード。既定は未設定）と、ホットキーを押したときのウィンドウへの自動貼り付け（任意）
- コピー元が「クリップボードの履歴に含めない」などと指定した文章を差し込んだ場合は、結果にも同じ指定を付ける
- カテゴリを選んだエクスポート、ファイルから選んだカテゴリの追加インポート、全件置換での復元（JSON）
- Windows へのサインイン時の自動起動（Microsoft Store 版。既定はオフ）
- 表示言語は英語と日本語（Windows の表示言語に合わせるか、設定で固定）

クリップボードの常時監視や履歴の保存はしません。テンプレートと設定はこの PC の中だけに保存し、ClipCompose がインターネットへ送信することはありません（詳しくは下記のプライバシーポリシー）。AI への送信や回答の取得はしません。

## 対応 OS と実行環境

- Windows 11（64 ビット、x64）
- Microsoft Store 版は .NET を内包しているため、追加のインストールは不要です。
- このリポジトリのソースからビルドして実行する場合は、[.NET 10 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/10.0) が必要です。

## インストール

正式な配布先は Microsoft Store です（無料）。

- Microsoft Store: https://apps.microsoft.com/detail/9MWJ11BSD5PG

この GitHub リポジトリでは実行ファイル（バイナリ）を配布していません。

## 基本的な使い方

1. 起動するとタスクトレイに常駐します（ウィンドウは表示されません）。
2. トレイアイコンを右クリック →［設定…］の［テンプレート］タブで、カテゴリとテンプレートを作成します。コピーした文章を入れたい位置に `{{clipboard}}` を書きます。
3. 差し込みたい文章をコピーし、トレイアイコンをクリックして選択画面を開き、テンプレートを選んで Enter を押します。結果がコピーされるので、貼り付け先で `Ctrl+V` します。
4. 設定画面の［ホットキーと動作］タブでホットキーを設定すると、どのアプリからでも選択画面を呼び出し、自動で貼り付けることもできます。
5. 終了はトレイのメニューの［終了］です。

詳しい使い方は取扱説明書をご覧ください。

- 取扱説明書: https://www.ja1bus.com/clipcompose/manual/

## ビルド方法

### 必要な環境

- Windows 11 x64
- [.NET SDK 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)（確認済み: 10.0.402。ルートの `global.json` で 10.0 系の SDK を選びます）
- `dotnet` CLI だけでビルドできます（Visual Studio は不要）。
- 本体は NuGet パッケージに依存しません。テストだけが NuGet の `Microsoft.NET.Test.Sdk`・`xunit`・`xunit.runner.visualstudio` を使います（初回のビルド時に nuget.org から取得します）。

### コマンド

リポジトリのルートで実行します。

```powershell
dotnet build ClipCompose.csproj
dotnet test Tests/ClipCompose.Tests.csproj
dotnet run --project ClipCompose.csproj
```

`dotnet run` で起動するとタスクトレイに常駐します。終了はトレイのメニューの［終了］です。テンプレートと設定は `%LOCALAPPDATA%\ClipCompose\` に保存されます。

Framework-dependent（win-x64）の出力を作るには次を実行します。出力先は `publish\FrameworkDependent\` で、実行には .NET 10 Desktop Runtime（x64）が必要です。

```powershell
dotnet publish ClipCompose.csproj -c Release -p:PublishProfile=FrameworkDependent
```

出力には `LICENSE` と `THIRD-PARTY-NOTICES.md` も入ります（設定画面の［バージョン情報］から表示します）。

### 構成

| パス | 内容 |
|---|---|
| `ClipCompose.csproj` | 本体（WPF、`net10.0-windows`）。タスクトレイだけ WinForms の `NotifyIcon` を使用 |
| `Core/` | 画面・Win32 に依存しないロジック（合成・テンプレート管理・保存・設定）と、画面の文字列（英語 `Strings.resx`・日本語 `Strings.ja.resx`） |
| `Tests/` | `Core` の単体テスト（xUnit） |
| `Services/`・`Views/`・`Interop/` | クリップボード・ホットキー・貼り付け・トレイなどの処理・画面・Win32 API の宣言 |
| `icon.png`・`Resources/icon.ico` | アプリアイコン（`pwsh -File tools/make-icon.ps1` で `icon.png` から `icon.ico` を作成） |

Microsoft Store 版のパッケージ（MSIX）を作る仕組みは、このリポジトリに含めていません。`ClipCompose.csproj` にある `ClipComposeMsix` の条件と `Services/PackageInfo.cs` は Store 版のためのものです。

ソースコード中のコメントは主に日本語です。コメント中の「仕様書 §N」は、このリポジトリには含めていない開発用の仕様書の節番号です。

## ライセンス

[MIT License](LICENSE) で公開しています。

Copyright (c) 2026 ja1bus（© ja1bus）

第三者のソフトウェアについては [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) を参照してください。

## 無保証

本ソフトウェアは「現状のまま」提供され、明示・黙示を問わず、いかなる保証もありません。本ソフトウェアの使用によって生じたいかなる損害についても、作者は責任を負いません（詳細は [LICENSE](LICENSE)）。

## 関連リンク

- Web ページ: https://www.ja1bus.com/clipcompose/
- 取扱説明書: https://www.ja1bus.com/clipcompose/manual/
- 更新履歴: https://www.ja1bus.com/clipcompose/changelog/
- プライバシーポリシー: https://www.ja1bus.com/clipcompose/privacy/
- Microsoft Store: https://apps.microsoft.com/detail/9MWJ11BSD5PG
- お問い合わせ: contact@ja1bus.com
