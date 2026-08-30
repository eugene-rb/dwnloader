# Dwnldr

高速でシンプルなダウンローダー。複数のサイトから画像・動画・音声を一括ダウンロードできます。

## 特徴

- **マルチサイト対応**: Pixiv、Twitter、その他多くのサイトに対応
- **高速ダウンロード**: 並列処理で複数ファイルを同時ダウンロード
- **クリップボード監視**: URL をコピーするだけで自動的に取り込み
- **自動更新**: Velopack による自動アップデート機能
- **モダン UI**: WinUI3 による Windows 11 / 10 標準デザイン

## インストール

### Windows 環境

[Releases ページ](https://github.com/eugene-rb/dwnloader/releases)から最新版をダウンロード：

1. **インストーラー** (\Dwnldr-win-Setup.exe\) - 推奨
   - 自動インストール、アンインストール対応

2. **ポータブル版** (\Dwnldr-win-Portable.zip\)
   - インストール不要、どこでも実行可能

## 使用方法

### 基本操作

1. **URL を追加**
   - URL をテキストボックスにペースト → 「追加」ボタン
   - 複数行の URL もまとめて処理可能

2. **ダウンロード開始**
   - 「待機中を開始」で全タスク開始
   - 「未完了を再試行」でリトライ

3. **クリップボード監視**
   - チェックボックス有効時、コピーした URL を自動取り込み

### 設定

- **出力フォルダ**: ダウンロード先（デフォルト：ユーザーフォルダ）
- **ワーカー数**: 並列ダウンロード数を調整
- **再試行回数**: 失敗時のリトライ回数
- **アカウント**: Pixiv・Twitter ログイン情報管理

## 技術スタック

- **フレームワーク**: WinUI3（Windows App SDK 1.7）
- **ランタイム**: .NET 8.0（Self-Contained）
- **UI**: XAML + C#
- **配布**: Velopack（GitHub Releases）
- **アーキテクチャ**: 
  - \Dwnloader\ (UI層)
  - \Dwnloader.Core\ (ロジック層 - UI非依存)

## 必要な環境

- Windows 10 / 11（x64）
- .NET 8.0 ランタイム（アプリに同梱）
- yt-dlp（動画・音声ダウンロード時、別途インストール推奨）

## ライセンス

詳細はプロジェクトを参照してください。

## 開発

### ビルド

\\\ash
# Debug ビルド
dotnet build Dwnloader/Dwnloader.csproj

# Release ビルド
dotnet publish Dwnloader/Dwnloader.csproj -c Release -o publish
\\\

### 最新リリース

新しいバージョンは自動更新機能により配信されます。

---

問題報告や機能リクエストは [Issues ページ](https://github.com/eugene-rb/dwnloader/issues) へお願いします。
