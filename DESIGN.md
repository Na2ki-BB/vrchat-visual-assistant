# VRChat Visual Assistant — 設計の入口

Last reorganized: 2026-10-01 (Etc/UTC)

設計を、共通AI基盤と機能別の設計に分けて管理します。実装済みの機能と、これから詰める概要案は区別します。

| 設計書 | 責務 |
| --- | --- |
| [共通AI基盤設計](docs/DESIGN-PLATFORM.md) | 腕メニュー、入力・歩行維持、実行制御、結果表示と配置、機能追加ルール、共通通信、資格情報、privacy。共通音声入力はWPF/VR接続済み・実機/API受入待ち |
| [日本語翻訳機能設計](docs/DESIGN-JAPANESE-TRANSLATION.md) | 明示的な画面capture、OCR、英語から日本語への任意翻訳、固有のプロンプトと結果、費用・制約、精度と速度の計測 |
| [動画検索機能設計](docs/DESIGN-VIDEO-SEARCH.md) | **WPF/VR接続済み・実機/実サービス受入待ち**。共通認識文からの直接/解釈検索、yt-dlpで10件取得・5件ずつ表示、候補選択とURLコピー、残る受入確認 |

## 読み方と変更先

- 共通UI・入力・FeatureCatalog・通信・privacyを変える場合は基盤設計を読む
- 取得範囲・OCR・翻訳内容・利用費用を変える場合は翻訳機能設計を読み、共通制約も確認する
- 録音・文字起こし・認識文の保持は基盤設計、文章の直接/解釈検索・候補選択・URLコピーは動画検索機能設計を読み、実装済み仕様と未完了の受入確認を区別する
- capture実装の詳細とoverlay除外順序は翻訳側、`ICaptureSource`の契約とOpenVR/UIの共通ライフサイクルは基盤側を正本とする
- 現在の操作と設定は [README](README.md)、未完了の実装・実機検証は [TASKS](TASKS.md)、OCR改善の採否は [OCR-COVERAGE-PROPOSAL](OCR-COVERAGE-PROPOSAL.md)で確認する
- 開発手順とコード上の正本は [development skill](harness/skills/vrcva-development/SKILL.md) と [source-of-truth map](harness/skills/vrcva-development/references/source-of-truth.md)を参照する

## 文書再整理と設計追加の範囲

文書再整理そのものでは実装を変更しませんでした。その後のI1で純粋な設定/音声形式契約を追加し、I2で画像とテキストのhandlerを分けました。K5で実行時catalogに翻訳・直接動画検索・解釈動画検索を登録しました。要約は未登録の境界検証用実装です。日本語翻訳の対象は画像中の文字で、音声入力・音声翻訳ではありません。

元の設計書の要求、方式比較、決定理由、Phase履歴、実測値、公式参照は、担当する設計書へ移しました。根拠の日時は2026-08-11〜15のまま保持し、過去のMVP除外事項や旧方式は履歴と区別しています。今回の再整理はWindows実機、API、価格、規約の再検証を意味しません。

2026-10-01の設計合意に沿い、I1〜I4で入力/設定/共通single-flightと独立quota、J1〜J3でWinMM録音・GPT Transcribe・同意/専用資格情報・WPF音声フロー、K1〜K5で直接/解釈検索・固定yt-dlp・thumbnail/clipboard・WPF候補操作、L1/L2でVRの進捗/失敗操作・腕マイク・認識文・候補を接続しました。認識文の直下に2つの検索ボタンを置き、最大10件を5件ずつ表示し、本人が選んだURLをコピーします。ワールドへの貼り付け・再生は手動です。

検索解釈は `gpt-6-luna` / `reasoning.effort=none`、音声は `gpt-transcribe`。音声の初期300秒/30送信、翻訳10送信、検索解釈10送信を独立して数えます。共通gateとWPF/VRの同一sessionは維持し、本文・音声を設定やログへ保存しません。

[最終自動/静的検証の証拠](TASKS.md#l3-verification-evidence--2026-10-01)と[実機・実サービスの未完了ゲート](TASKS.md#post-mvp-milestone-l--vr-integration-and-separate-acceptance-gates)を区別します。fakeの成功は実マイク、固定yt-dlpのYouTube互換性、API精度/費用、新しいVR画面のQuest 3S受入を意味しません。通常版の録音停止は実API送信へ進み、公開fakeデモモードはありません。
