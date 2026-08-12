# OCR カバレッジ改善 意見書

**状態:** 提案（未実装）
**対象:** `AdaptiveOcrEngine` を中心とした OCR 精度・レイテンシ・コストの改善
**作成日:** 2026-08-12

この文書は設計レビューの結論をまとめたものです。実装を担当する者は、まず「2. 現状の実装」で事実関係を把握し、「5. 採用する改善案」を優先順位順に実施してください。「6. 採用しなかった案」も必ず読んでください。一見良さそうに見えて棄却した案が含まれており、再提案を防ぐためです。

---

## 1. 背景と問題提起

出発点は次の問いでした。

> 画面いっぱいの英語があった場合も、見切れて処理されないことがあるのではないか？

**結論: ある。しかも画面いっぱいの英語こそが最も見切れやすい。**

現在の適応型 OCR は「全画面を 1 回 OCR し、結果が弱ければ画面を 3 本の横帯に分けて再認識する」という構造ですが、この「弱い」の判定方法に構造的な欠陥があります。

---

## 2. 現状の実装（事実）

### 2.1 判定ロジック — [`src/VrcVa.Core/AdaptiveOcrEngine.cs`](src/VrcVa.Core/AdaptiveOcrEngine.cs)

```
EnhancementThreshold = 80   // 行 7
MinimumImprovement   = 4    // 行 8
```

1. 全画面を OCR（`primary`）
2. `Score(primary.Text) >= 80` なら即 return（行 20）— 帯パスは走らない
3. 未満なら帯 3 本を OCR し、`MergeUniqueLines` で統合（行 39）
4. `Score(統合) < primaryScore + 4` なら `primary` を返す（行 40-43）
5. 採用時は警告文を付与し、帯フレームを `finally` で確実に Dispose（行 54-60）

`Score()`（行 81-100）は **ASCII 英数字 `[A-Za-z0-9]` の個数**です。空白・記号・非 ASCII は 0 点。Windows OCR が返す信頼度（confidence）は**一切使っていません**。既存テストが仕様を固定しています: `Score("Ab c-12_日本語") == 5`。

### 2.2 帯の生成 — [`src/VrcVa.Windows/Ocr/WindowsOcrRegionSource.cs`](src/VrcVa.Windows/Ocr/WindowsOcrRegionSource.cs)

- `RegionCount = 3`、全幅 × 高さ `(H+1)/2`
- `startY = 0, round(maxStart/2), maxStart`（`maxStart = H - regionHeight`）
- 1920×1080 なら `[0,540] / [270,810] / [540,1080]`
- `scale = Math.Min(2d, min(MaxImageDimension/W, MaxImageDimension/h))` → **通常のミラー解像度では常に 2 倍拡大**

**帯の幾何自体は健全です。** 垂直方向は全域をカバーし、各帯の切断面は必ず隣接帯の内側に入るため、行が帯境界で失われることはありません。ここは変更不要です。

### 2.3 全画面パスは拡大しない — [`src/VrcVa.Windows/Ocr/WindowsOcrEngine.cs:17-23`](src/VrcVa.Windows/Ocr/WindowsOcrEngine.cs#L17-L23)

`MaxImageDimension` 超過時に例外を投げるだけで、拡大も縮小もしません。**拡大処理が入るのは帯パスだけ**です。

### 2.4 その他の関連する契約

| 箇所 | 事実 | 影響 |
|---|---|---|
| [`Models.cs:42`](src/VrcVa.Core/Models.cs#L42) | `CapturedFrame` が `byte[] encodedImage` を保持 | 帯を PNG 経由で渡すしかない（後述の往復コスト） |
| [`Models.cs:93-96`](src/VrcVa.Core/Models.cs#L93-L96) | `OcrOutput` は `Text` / 言語 / 警告のみ | レイアウト情報を上位に渡せない |
| [`Contracts.cs:8-11`](src/VrcVa.Core/Contracts.cs#L8-L11) | `IOcrEngine` に進捗チャネルがない | 帯パス中に進捗を出せない |
| [`OcrAnalyzer.cs:17-21`](src/VrcVa.Core/OcrAnalyzer.cs#L17-L21) | 進捗報告は OCR 開始前の 1 回のみ | 数秒間メッセージが無変化 |
| [`ScanPipeline.cs:81-85`](src/VrcVa.Core/ScanPipeline.cs#L81-L85) | `InlineProgress` が `.GetAwaiter().GetResult()` で同期ブロック | 進捗報告を増やす際の注意点 |
| [`OpenAiTranslatorOptions.cs:16`](src/VrcVa.Infrastructure/OpenAiTranslatorOptions.cs#L16) | `MaxOutputTokens = 1_200` | 出力に天井あり、入力は青天井 |
| [`OpenAiTextTranslator.cs:166-201`](src/VrcVa.Infrastructure/OpenAiTextTranslator.cs#L166-L201) | `ExtractOutputText` が `status` / `incomplete_details` を見ない | 切れた翻訳が成功として表示される |

---

## 3. 特定した問題

### 問題 1（最重要）: 閾値ゲートの向きが逆

`Score` は**絶対量**であって**達成率**ではありません。「読めるはずの総量」を知らないまま絶対量で判断しているため、情報量の多い画面で必ず誤ります。

| 画面 | primaryScore | 帯パス | 実際に受ける処理 |
|---|---|---|---|
| 文字が少ない看板 | 20 | 走る | 2 倍拡大あり（手厚い） |
| 掲示板いっぱいの英語 | 800 | **走らない** | 素の解像度のみ（手薄） |

1000 文字あるうち 120 文字しか拾えなくても `120 >= 80` で「strong」判定となり、残り 880 文字を取りに行く経路が存在しません。**これが「見切れ」の本体です。**

### 問題 2: 帯採用時に primary 固有の行が消える

[`AdaptiveOcrEngine.cs:39`](src/VrcVa.Core/AdaptiveOcrEngine.cs#L39) の `MergeUniqueLines` に渡るのは `regionOutputs` **だけ**で、`primary` が含まれていません。帯が採用されると全画面パスだけが拾っていた行は捨てられます。`+4` のゲートは合計スコアで見るため、帯が +20 稼いで primary 固有の行を -10 失っても net +10 で採用され、その 10 文字は消えます。

### 問題 3: 重複除去の甘さがゲートを骨抜きにする

帯は約 50% 重なるため同じ行が 2 本の帯に現れます。dedup が**行単位の完全一致（大小無視）**なので、`Emergency exit` と `Emergency exlt` のような 1 文字違いは両方残ります。結果、実質の情報量が増えていないのにスコアだけ約 2 倍になり、`+4` のゲートは自明に通過します。**加えて、この重複は翻訳の入力トークンをそのまま増やすため、現状すでに余計な料金を払っている可能性があります。**

### 問題 4: 翻訳が黙って切れる

`max_output_tokens = 1200` は、画面いっぱいの英語を日本語訳する際に現実的に到達し得ます。`ExtractOutputText` は `status` を見ないため、**途中で切れた翻訳が何の警告もなく成功として表示されます**。

---

## 4. コスト分析

### 4.1 処理コスト（1920×1080 想定・見積もり）

| パス | 解像度 | ピクセル数 |
|---|---|---|
| primary（拡大なし） | 1920×1080 | 2.07 M |
| 帯 ×3（2 倍拡大） | 3840×1080 × 3 | 12.44 M |
| **帯実行時の合計** | — | **14.5 M（約 7 倍）** |

推定時間の内訳:

| 処理 | 推定 |
|---|---|
| primary の OCR | 150–350 ms |
| 帯 3 本の OCR | 1.0–2.0 s |
| **PNG エンコード ×3** | 0.3–0.9 s |
| **PNG デコード ×3** | 0.2–0.45 s |
| 元画像からの変換デコード ×3 | 0.15–0.4 s |
| **帯パス合計** | **約 1.7–3.7 s** |

**このうち 3〜4 割（0.7–1.7 s）は PNG の往復であり、OCR ではありません。** `CapturedFrame` が encoded bytes を持つ契約（`Core` のプラットフォーム非依存性を守るための判断）の代償です。

### 4.2 体感レイテンシはモードで大きく変わる

| モード | 帯なし | 帯あり | 体感 |
|---|---|---|---|
| **OCR のみ（既定）** | 約 0.5–1.0 s | **約 2.5–5 s** | **カテゴリが変わる**（「押したら出る」→「待つ」） |
| 翻訳あり | 約 3–7 s | 約 5–10 s | 既に「待つ」体験に埋没する |

`VRCVA_TRANSLATION_PROVIDER` の既定は `none` で `OcrAnalyzer` が使われるため、**既定構成では OCR が体感レイテンシのほぼ 100%** です。同じ変更の体感インパクトが設定によって数倍違う点に注意してください。

### 4.3 料金コスト

**OCR はすべてローカル実行のため、帯パスを何回増やしても金銭コストは 0 です。** 増えるのは時間と電力のみ。金銭が動くのは翻訳の入力トークン（OCR 文字数にほぼ比例）だけです。

翻訳入力トークンの相対比（概算）:

| 状態 | 相対量 |
|---|---|
| 現状・帯なし | ×1.0 |
| 現状・帯採用（重複残りあり） | ×1.5 – 2.0 |
| union 化のみ | ×1.6 – 2.2 |
| **union 化 + 近似重複除去** | **×1.0 – 1.2** |

なお `OpenAiTranslatorOptions` は `BudgetModel` / `QualityModel` を持ち既定は Quality 側です。**モデル選択のほうが、本提案のどの変更よりも桁違いに大きい料金レバーです。** 料金最適化が目的ならそちらを先に検討してください。

---

## 5. 採用する改善案

### 優先度 1: union 化 + 近似重複除去（セットで実施）

処理時間の増加ゼロ、料金はむしろ減少方向、リスク最小。**ここから着手すること。**

- `MergeUniqueLines` に `primary.Text` を**先頭に**含める
- 結果が primary の上位集合になることが保証されるため、`MinimumImprovement` による比較ゲート（行 40-43）を**削除**する
- dedup を近似一致に変更する（正規化キー、または編集距離ベース）

**必ずセットで行う理由:** 現状の「勝った方を選ぶ」構造は、primary が誤認識したゴミ行を丸ごと捨てるフィルタとしても機能していました。union 化はその安全弁を外すため、近似重複除去による品質担保が前提になります。片方だけの実装は品質を悪化させ得ます。

近似一致の閾値は緩めすぎないこと。メニュー項目のような**正当な繰り返し行**を消してはいけません。

### 優先度 2: 帯パス中の進捗表示

**実時間の短縮より、知覚時間の改善のほうが費用対効果が高いと判断しました。**

現状、帯パスが数秒動く間、ユーザーが見ているのは「画像から文字を認識しています…」という変化しない 1 行だけです。3 秒の無変化は「処理中」ではなく「**固まった**」と読まれます。

- `IOcrEngine.RecognizeAsync` に進捗チャネルを追加し、`AdaptiveOcrEngine` が「強化処理に切り替えた」旨を報告できるようにする
- **注意:** [`ScanPipeline.cs:81-85`](src/VrcVa.Core/ScanPipeline.cs#L81-L85) の `InlineProgress` は `.GetAwaiter().GetResult()` で同期ブロックします。報告を増やすと、そのたびに OCR スレッドが WPF ディスパッチと XSOverlay の UDP 送信を待ちます。**報告を増やすなら `IProgress` の受け方も同時に見直してください。**

### 優先度 3: カバレッジ判定への置き換え（本丸）

問題 1 に直接効く唯一の案です。**`Score` と `EnhancementThreshold` を廃止します。**

- 判定基準を「何文字読めたか」から「**読み残しがありそうか**」へ変更する
- `OcrResult.Lines` の bounding box を用い、(a) テキストが画面の一部に偏っている、(b) 行の文字高が小さく拡大の効果が見込める、のいずれかで帯パスを起動する
- **`IOcrEngine` / `OcrOutput` の契約拡張が必要**です。ただし `Core` のプラットフォーム非依存性を守るため、**WinRT の型をそのまま `Core` に持ち込まないこと。** Core 側の中立な型（矩形と行の集合）に変換して渡してください

実装量が最大で影響範囲も広いため、優先度 1・2 の完了後に着手してください。

### 優先度 4: 翻訳の incomplete 検出（独立）

- `ExtractOutputText` 付近で `status == "incomplete"` / `incomplete_details.reason` を検査する
- **まずは警告を出すのみ**（`OcrOutput.Warning` と同じ表示経路に載せる）とし、料金 ±0 に留める
- 分割再送は料金がほぼ 2 倍（instructions が毎回課金される）になるため、**本提案では採用しない**
- 他の項目と独立しているため、いつ着手しても構いません

### 優先度 5: PNG 往復の削減（保留）

帯パスの 3〜4 割を占めますが、`CapturedFrame` の契約変更を伴い、`Core` の設計原則に触れます。**優先度 3 まで完了し、実測で必要性が確認できてから判断してください。**

---

## 6. 採用しなかった案（再提案しないこと）

### 却下: 常に帯を走らせる

閾値を廃止でき最もコードが単純になりますが、**既定の OCR のみモードでは全スキャンが常時 3–5 秒**になります。帯パスが本来必要なのは「primary が弱かった場面」だけであり、その代替（ユーザーによる手動リトライ = 位置変更・狙い直し・再押下で 10 秒以上）と比べれば条件付き実行は圧倒的に有利です。

**条件分岐という設計自体は正しく、間違っているのは条件のほうです。** よって「条件を撤廃する」のではなく「条件を正す」（優先度 3）を選択しました。

### 却下: 密度閾値（スコア ÷ 画素数など）

実装は最小ですが、`80` という魔法の数字を別の魔法の数字に置き換えるだけです。「大きな看板 1 枚が画面中央にある」という正当なケースで密度が低く出て誤発火します。

### 対象外: キャプチャ段の見切れ

HWND 指定の VRChat ウィンドウのみをキャプチャするため、VR 内の実 FOV とデスクトップミラーの画は一致しません。ミラーに写っていない文字は最初から画素として存在せず、**OCR 側では原理的に救えません。** 本提案の対象外です。

---

## 7. 受け入れ基準

1. 画面いっぱいの英語（primaryScore が 80 を大きく超える場面）で、従来より認識行数が増えること
2. 帯パス採用時に、primary のみが拾っていた行が結果から消えないこと
3. 帯の重なりに由来する近似重複行が結果に残らないこと
4. 正当な繰り返し行（同一文言のメニュー項目など）が誤って除去されないこと
5. 帯パス中に進捗表示が変化すること
6. 翻訳が `max_output_tokens` で打ち切られた場合に警告が表示されること

---

## 8. 既存テストへの影響

[`tests/VrcVa.Core.Tests/AdaptiveOcrEngineTests.cs`](tests/VrcVa.Core.Tests/AdaptiveOcrEngineTests.cs) は**全面的な見直しが必要**です。実装前に必ず確認してください。

| テスト | 影響 |
|---|---|
| `RecognizeAsync_SkipsEnhancementWhenPrimaryTextIsStrong` | 優先度 3 で判定基準が変わるため前提が変化 |
| `RecognizeAsync_UsesBetterOverlappingRegionTextAndRemovesDuplicates` | union 化で primary の `"EXIT"` が結果先頭に加わるため**期待値が変わる** |
| `RecognizeAsync_KeepsPrimaryTextWhenRegionsAreNotBetter` | union は常に primary の上位集合になるため**テストの前提そのものが消滅**。「上位集合であること」を検証する内容に書き換えること |
| `Score_CountsOnlyAsciiLettersAndDigits` | 優先度 3 で `Score` を廃止するため**削除**対象 |

帯フレームが `Dispose` されることを検証している箇所（行 44-45）は**維持してください**。プライバシー要件です。

---

## 9. 制約事項

`CONTRIBUTING.md` と `DESIGN.md` の既存ルールに従ってください。特に:

- **WSL / Linux では検証不可。** WinRT OCR・Windows Graphics Capture・WPF・グローバルホットキーは Windows 実機での実行が必須です（`DESIGN.md:33-36`）。`scripts\build.ps1` および `scripts\test-capture.ps1` を Windows で実行してください
- **`VrcVa.Core` はプラットフォーム非依存を維持すること。** WinRT の型を `Core` に持ち込まないでください
- **画像・OCR テキスト・翻訳結果・シークレットをログに出力しないこと**（`DESIGN.md:226`）。テストを通すために入出力内容をログに出すことは禁止されています
- **`provider = none` のとき OCR テキストが PC 外に出ない**というプライバシー境界を壊さないこと
- 変更は `Trigger → Capture → Analyzer → Result → Renderer` のフローを保つ小さな垂直変更に留めること
- 実 API キーやネットワーク実接続のテストを追加しないこと。`HttpMessageHandler` のフェイクを使用すること

---

## 10. 推奨する実施順序

```
優先度 1（union 化 + 近似重複除去）  ← ここから。低リスク・即効・料金減
        ↓
優先度 2（進捗表示）                 ← 知覚レイテンシの改善
        ↓
優先度 3（カバレッジ判定）           ← 本丸。契約変更を伴う
        ↓
優先度 4（翻訳 incomplete 検出）     ← 独立。任意のタイミングで可
        ↓
優先度 5（PNG 往復削減）             ← 実測で必要性を確認してから
```

各優先度ごとに独立した PR とし、まとめて 1 つの PR にしないでください。
