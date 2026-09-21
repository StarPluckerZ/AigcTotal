# AigcTotal

Deterministic verification of AIGC implicit content labels — the C#/.NET implementation of **GB 45438-2025** (*Cybersecurity technology — Labeling method for AI-generated synthetic content*), the mandatory Chinese national standard in force since 2025-09-01.

> GB 45438-2025《网络安全技术 人工智能生成合成内容标识方法》元数据隐式标识的确定性验证库：解析、校验、取证、四档判定。

## What it does

Parses the implicit label metadata embedded by AI generation services from file carriers (PNG / JPEG now; MP4, WAV, MP3, text on the M1 roadmap), validates the seven Annex-E fields, and produces a four-tier verdict:

| Verdict | Meaning |
|---|---|
| `compliant` | Label present and conforming to Annex E |
| `noncompliant` | Label present but violating Annex E (missing/invalid fields, disagreeing duplicates) |
| `not_found` | No label found, no signs of tampering |
| `inconclusive` | Cannot judge: damaged container, ambiguous detection, wipe evidence, resource limits |

Fully deterministic: same library version + same input bytes → identical results, suitable for compliance self-checks and reproducible attestation reports.

## Status

M1 in progress (2026-09):

- [x] Bounded, untrusted-input-safe parsing (`BoundedReader`: total-read / alloc / depth / structure caps)
- [x] Carrier detection with ambiguity signaling
- [x] PNG: `tEXt` (keyword `AIGC`) sites, CRC verification, truncation forensics; `iTXt` XMP channel with the TC260 wrapper (`<TC260:AIGC>` embedded Annex-E JSON, verified against a real Doubao-generated image); non-metadata chunks (IDAT) stream-skipped — multi-MB files verify under a tiny read budget
- [x] JPEG: APP1 XMP sites
- [x] MP4/MOV: recursive box walk (`uuid` XMP + `udta/aigc` channels), `mdat` skipped without reading, brand detail
- [x] WAV: RIFF chunk walk with odd-size padding, `AIGC` chunk sites, empty-shell forensics
- [x] MP3: ID3v2.3/v2.4 frames, `TXXX` (description `AIGC`) sites, syncsafe sizes
- [x] Text: BOM/strict-UTF-8 detection, explicit prompt prefix/suffix matching (baseline patterns)
- [x] Annex E JSON payload decoding, per-site schema validation, multi-site aggregation
- [x] Four-tier verdict composition, sync + async API
- [x] `aigc-report` CLI: canonical report envelope (schema v1) as the official reference implementation; JCS canonical JSON writer, ULID, CI-friendly exit codes
- [x] Golden corpus (`samples/corpus/`, deterministic, self-made — no copyrighted platform files) + golden report regression test (schema-freeze enforcement)
- [x] Robustness suite in CI: seeded mutations, truncation, length/depth bombs — no exception may escape `Verify`
- [ ] SharpFuzz coverage-guided fuzzing (`tools/AigcTotal.Fuzz`, Linux) — harness ready, periodic runs
- [ ] XMP namespace-precise matching, MP4 `udta` atom name, text prompt patterns — pending TC260 guide verification

## Usage

### CLI (`aigc-report`) — 官方校验报告参考实现

```console
$ aigc-report image.png video.mp4          # 在输入旁生成 <file>.report.json
$ aigc-report --stdout article.txt         # 单文件 canonical JSON 到 stdout
$ aigc-report --pretty image.png           # 人类可读（非 canonical）
```

默认输出是 **canonical JSON（RFC 8785 受限子集）**——被哈希、签名、入透明日志的正是这串字节。退出码可直接用于发布前 CI 门禁：`0` 合规 / `1` 不合规 / `2` 未检出 / `3` 无法判定 / `10` 处理错误（多文件取最严重）。

### Library

```csharp
using AigcTotal.GB45438;

using var stream = File.OpenRead("aigc-image.png");
VerificationResult result = AigcLabelVerifier.Verify(stream);

Console.WriteLine(result.Verdict);          // Compliant / Noncompliant / NotFound / Inconclusive
foreach (var site in result.Sites)
{
    Console.WriteLine($"{site.Encoding} at {site.Location.Offset}: {site.Fields?["ContentProducer"]}");
}
```

## Design principles

- **Zero third-party dependencies** on `net10.0` (`netstandard2.0` adds only `System.Memory`).
- **Untrusted input safety**: every read goes through a bounded reader; limits are echoed in results for reproducibility.
- **Evidence vs. verdict**: `sites`/`signals` are physical facts; `checks` are rule outcomes; the verdict is composed — never guessed.
- **Stable vocabulary**: check ids, verdicts, and field names are frozen tokens (see `docs/checks.md`); human-readable presentation belongs to the display layer, not to this library.

## Vocabulary & schema

- `docs/checks.md` — check id registry (additive-only)
- `docs/canonicalization.md` — canonical JSON rules for attestation reports

## License

[Apache-2.0](LICENSE)
