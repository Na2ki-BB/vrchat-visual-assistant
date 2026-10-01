# VRChat Visual Assistant — 設計の入口

Last reorganized: 2026-10-01 (Etc/UTC)

設計を、共通AI基盤と既存の日本語翻訳機能に分けて管理します。

| 設計書 | 責務 |
| --- | --- |
| [共通AI基盤設計](docs/DESIGN-PLATFORM.md) | 腕メニュー、入力・歩行維持、実行制御、結果表示と配置、機能追加ルール、共通テキストモデル通信、資格情報、privacy、開発・検証方針 |
| [日本語翻訳機能設計](docs/DESIGN-JAPANESE-TRANSLATION.md) | 明示的な画面capture、OCR、英語から日本語への任意翻訳、固有のプロンプトと結果、費用・制約、精度と速度の計測 |

## 読み方と変更先

- 共通UI・入力・FeatureCatalog・通信・privacyを変える場合は基盤設計を読む
- 取得範囲・OCR・翻訳内容・利用費用を変える場合は翻訳機能設計を読み、共通制約も確認する
- capture実装の詳細とoverlay除外順序は翻訳側、`ICaptureSource`の契約とOpenVR/UIの共通ライフサイクルは基盤側を正本とする
- 現在の操作と設定は [README](README.md)、未完了の実装・実機検証は [TASKS](TASKS.md)、OCR改善の採否は [OCR-COVERAGE-PROPOSAL](OCR-COVERAGE-PROPOSAL.md)で確認する
- 開発手順とコード上の正本は [development skill](harness/skills/vrcva-development/SKILL.md) と [source-of-truth map](harness/skills/vrcva-development/references/source-of-truth.md)を参照する

## 今回の分割範囲

実装の変更や新機能の追加はありません。現在の共通基盤はフレーム入力を前提とし、実行時に選べる機能は翻訳のみです。要約は未登録の境界検証用実装です。日本語翻訳の対象は画像中の文字で、音声入力・音声翻訳やYouTube検索の設計追加は含めません。

元の設計書の要求、方式比較、決定理由、Phase履歴、実測値、公式参照は、担当する設計書へ移しました。根拠の日時は2026-08-11〜15のまま保持し、過去のMVP除外事項や旧方式は履歴と区別しています。今回の再整理はWindows実機、API、価格、規約の再検証を意味しません。
