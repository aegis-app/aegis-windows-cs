# 🛡️ AEGIS Windows版 (C# クライアント)

| 項目 | 内容 |
|---|---|
| バージョン | 1.0.0 |
| 状態 | 再実装 (旧 aegis-windows-cs v1.9.1 の機能を全て再現し、統一開発フローへ移行) |
| 稼働先 | Windows (ユーザー PC) |
| 実装 | C# / .NET 8 / WinForms (`net8.0-windows`)、外部依存なし (標準ライブラリのみ) |

地震・津波・緊急地震速報 (EEW)・気象警報・台風・天気・レーダーを
1つのウィンドウに集約する Windows デスクトップクライアントです。

## 📥 ビルド

- 必要環境: .NET SDK 8
- 一括ビルド (テスト → 単一ファイル exe): `build.bat` (Windows) / `./build.sh` (Linux クロスビルド)
- 成果物: `out/AEGIS.exe` (自己完結・圧縮つき単一ファイル、約69MB)
- テストのみ: `dotnet run --project src/AegisTest/AegisTest.csproj -c Release` (256件)

## 🖥️ 画面構成

- **ブリーフィング**: 「📌 現在のまとめ」カード + 地点別天気カード +
  時間別・週間予報の表 (DataGridView)
- **地震**: 最新地震の大型カード + 一覧 (30秒自動更新)、地図ボタンで
  OpenStreetMap を開く
- **レーダー**: 気象庁ナウキャスト (主) / RainViewer (代替) +
  地理院タイル、地点切り替え、5分自動更新
- **ニュース**: NHK RSS (cat0 + cat1)、災害関連を優先表示、クリックで
  ブラウザを開く
- **設定**: 取得方式 (直接/サーバー)、サーバー接続テスト、地点追加
  (都道府県 → 予報区域候補)、接続診断、EEW デモ表示、トレイ・通知、
  A.A.S 連携

EEW 発表時はカウントダウン付きのピン留めカードが全タブ共通で
最上部に出ます (1秒更新)。

## 📡 データソース

- 地震・津波・EEW・気象警報・台風: **A.A.S (Aegis Alert System) のみ**
  (SSE `/v1/stream` が主、REST `/v1/events` が初期同期・履歴回収。
  初期同期ぶんは通知しません)。A.A.S のデータは再配布しません
- 天気予報: 気象庁 予報 JSON + Open-Meteo (気温・天気は気象庁モデル、
  降水確率は ECMWF IFS)
- レーダー: 気象庁ナウキャスト + RainViewer + 地理院タイル
- ニュース: NHK RSS

## ⚙️ 設定・保存先

- 設定: `%AppData%\AEGIS\settings.json`
  (取得方式、サーバー URL、追加地点、トレイ、通知、A.A.S 連携)
- 前回ブリーフィングのキャッシュ: `%AppData%\AEGIS\briefing-cache.json`
  (起動直後に即表示するため。取得失敗を含む内容は保存しません)
- 書き込みは一時ファイル経由のアトミック置換です

## 🔔 通知

- トレイ常駐 + バルーン通知 (地震は震度下限つき、EEW は震度が
  上がった続報のみ再通知、注意報のみの発表は設定で切替)
- 初回チェック時は既存イベントをすべて既読化し、起動直後の
  一斉通知を防ぎます

## ⌨️ CLI

- `AEGIS.exe --version` : バージョン表示
- `AEGIS.exe --cli` : ブリーフィングを標準出力へ
- `AEGIS.exe --diag` : 接続診断を標準出力へ
- 二重起動時は既存ウィンドウを前面に出します

## 📚 ドキュメント

- [DESIGN.md](DESIGN.md) — 旧→新の対応表と判断記録
- [CHANGELOG.md](CHANGELOG.md) — 変更履歴
- [CONTRIBUTING.md](CONTRIBUTING.md) — 開発フロー (統一開発フロー準拠)
