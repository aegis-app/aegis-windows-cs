# DESIGN — AEGIS Windows版 再実装の設計記録

旧実装 `aegis-app/aegis-windows-cs` (最終 v1.9.1) を、統一開発フロー
(2026-11-28 採用) に沿ってゼロから再実装した際の対応表と判断記録。

## 🔁 旧→新 対応表

| 旧 (v1.9.1) | 新 (v1.0.0) | 備考 |
|---|---|---|
| `src/AegisWin/Data.cs` (基盤) | `src/AegisWin/Data.cs` | 時刻 (JST)・地点・HTTP (リトライ/30秒キャッシュ/同時取得共有)・JSON ヘルパーを再実装 |
| `Data.Quake.cs` | 同名 | 電文マージ、詳細文、観測点要約、地点別震度 (実測のみ) を再現 |
| `Data.Eew.cs` | 同名 | 行動ガイド、同一視キー、地域選択 (2段スコア)、到達予想 (発表値 → S波推定) を再現 |
| `Data.Weather.cs` | 同名 | WMO 表、気象庁予報/降水確率/気温、Open-Meteo (サフィックス付きキーのみ) を再現。GUI の表用に構造化エントリ (HourlyEntry/DailyEntry) を追加 |
| `Data.Briefing.cs` | 同名 | まとめ導出、サーバーモード (severity→level、行動ガイド付加) を再現 |
| `Data.Features.cs` | 同名 | 都道府県47、レーダー (JMA/RainViewer/地理院、z7→z8 配置計算)、NHK RSS を再現 |
| `Data.DiagNotify.cs` | 同名 | 接続診断、通知候補、EEW 再通知規則、警報 ID (Java ハッシュ互換) を再現 |
| `Data.A2s.cs` | 同名 | 種別ごとの検証、key+updatedAt 重複判定、再生抑制、警報/台風のスナップショット間引き、変換群を再現 |
| `A2sStream.cs` | 同名 | SSE 行状態機械、ready 前後での replay 切替、再接続 5/10/30/60 秒を再現 |
| `Settings.cs` | 同名 | settings.json / briefing-cache.json (アトミック書き込み、失敗内容はキャッシュしない) を再現 |
| `Program.cs` | 同名 | 単一インスタンス (Mutex + 既存窓の前面化)、`--version` / `--cli` / `--diag` を再現 |
| `MainForm.cs` / `MainForm.Helpers.cs` | 同名 | 5タブ、EEW ピン留めカード、トレイ、タイマー群 (5分/60秒/1秒/30秒) を再現 |
| `src/AegisTest` (550件) | 同名 (256件) | 同じ振る舞い範囲をフィクスチャで検証。件数はアサーションの粒度差 (複合条件を1件にまとめた箇所があるため) |
| `build.sh` / `build.bat` | 同名 | テスト → publish (out/AEGIS.exe) の一括ビルドを再現 |
| GitHub Actions | `.github/workflows/ci.yml` | 下記「CI の判断」参照 |

## 📝 判断記録

### 2026-11-28: 統一開発フローの採用
- dev-workflow-kit の土台 (Issue/PR テンプレート、CONTRIBUTING) を採用。
  CONTRIBUTING は本リポジトリ向けに調整 (設定は settings.json であり
  .env ではない旨、ビルド/テスト手順、ロジック層の GUI 非依存ルール)。
- バージョンは再実装に伴い 1.0.0 に振り直した (Data.Version、
  csproj の Version とも)。

### 2026-11-28: CI の判断 (Group C)
- キットの分類では C# クライアントは Group C (土台+手順のみ、CI は
  リポジトリごとに判断)。旧リポジトリは GitHub Actions
  (windows-latest) でテストと publish を確認し exe を artifact 保存
  していたため、新実装でも同じ形の CI を置いた:
  `windows-latest` で AegisTest 実行 → publish → artifact 保存。
- ローカル (Linux VM) でも EnableWindowsTargeting によりビルドと
  publish が可能なことは確認済み。ただし exe の実行確認は Windows が
  必要なため未実施 (CI の artifact でユーザーが実機確認する運用)。

### 2026-11-28: 警報の深刻度 (Grade) の解釈
- 通知可否は「特別警報・警報は常時、注意報のみは設定で切替」が
  製品仕様 (設定コメントに明記)。新実装では A.A.S の severity を
  4=特別警報級 (special) / 3=警報級 (warning) / 2以下=注意報級
  (advisory) と解釈し、ShouldNotify のゲートに使った。
  旧テストでは severity 2 の注意報候補の Grade が "warning" と
  されていたが、これは表示上のラベル付けの差と判断し、通知の
  可否 (ユーザーに見える振る舞い) が仕様通りになる方を優先した。

### 2026-11-28: NumOf の堅牢化
- テスト中に、コードから代入した int の JsonValue では
  `TryGetValue<double>` が失敗し数値が null になる事象を確認。
  NumOf は double → int → long → decimal → 文字列の順に試すよう
  堅牢化した (JSON テキスト由来の値では発生しないが、将来の
  プログラム的構築に備える)。

## ⚠ 未再現・未確認

- **exe の実行確認**: この VM (Linux) では Windows 用 exe を実行
  できないため、起動・画面表示・トレイ・バルーン通知の実挙動は
  未確認。ビルド (コンパイル) と publish、ロジック層テスト
  256件の合格までは確認済み。実機確認はユーザー環境での
  リリースビルド時に行う。
- 上記以外で再現できなかった旧機能はない。
