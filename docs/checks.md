# Check registry（check id 注册表）

> 只增不改（additive-only）。每个 id 回答一个原子验证问题；outcome ∈ pass / fail / warn / skip / error。
> 发现层检查只有 pass/skip，永不 fail：标识缺失是 verdict 的事，不是检查失败。

| id | 层 | 回答的问题 | outcome 语义 |
|---|---|---|---|
| `carrier_detect` | 探测 | 载体能否唯一确定 | pass=唯一命中；error+`unknown_format`/`ambiguous_format` → inconclusive |
| `container_structure` | 探测 | 容器是否完整可遍历 | pass；error+`structure_truncated`/`structure_malformed`/`resource_limit_exceeded` → inconclusive |
| `png_text_aigc` | 发现 | PNG tEXt（关键字 AIGC）通道是否发现标识站点 | pass=发现 ≥1 站点；skip=无；负载支持裸 JSON 与 `{"AIGC":{…}}` 包裹两种形态（后者见 TC260-PG-20259A 附录 B） |
| `png_xmp_aigc` | 发现 | PNG iTXt（关键字 XML:com.adobe.xmp）XMP 通道是否发现标识 | pass/skip；TC260 包装层（`<TC260:AIGC>` 内嵌附录 E JSON，ns=http://www.tc260.org.cn/ns/AIGC/1.0/）已支持；压缩 iTXt（flag≠0）→ error → inconclusive |
| `jpeg_app1_xmp` | 发现 | JPEG APP1 XMP 是否发现标识 | pass/skip |
| `mp4_xmp_aigc` | 发现 | MP4 XMP-aigc 通道（uuid box，Adobe XMP UUID）是否发现标识 | pass/skip |
| `mp4_udta_aigc` | 发现 | MP4 udta 元数据通道是否发现标识 | pass/skip；**主通道** = moov.udta.meta.keys(key=AIGC) + ilst（TC260-PG-20257A 规定，ffmpeg use_metadata_tags 形态）；字面 aigc box 为兼容探测 |
| `wav_riff_aigc` | 发现 | WAV RIFF AIGC chunk 是否发现标识 | pass/skip；空壳 → 信号 `metadata_shell_empty` → inconclusive |
| `mp3_id3_txxx` | 发现 | MP3 ID3 TXXX（描述=AIGC）帧是否发现标识 | pass/skip（v2.3/v2.4，UTF-8/Latin-1 描述） |
| `text_prompt_affix` | 发现 | 文本首尾标识区是否发现提示语模式（基线模式表，待 TC260 文本指南校准） | pass/skip；二进制误落到文本兜底 → carrier_detect error `unknown_format` |
| `annexe_fields` | Schema | 七字段是否齐备（Label/ContentProducer/ProduceID 必填非空，基线） | pass；fail+`field_missing`；error+`payload_malformed` |
| `annexe_charset` | Schema | 字段值是否符合附录 E 字符集（M1 基线：禁控制字符，待原文收紧） | pass；fail+`charset_invalid` |
| `annexe_label_enum` | Schema | Label 枚举取值是否合法（1=是/2=可能是/3=疑似） | pass；fail+`label_enum_invalid` |
| `annexe_unknown_field` | Schema | 是否出现未知字段（严格策略） | pass；fail+`unknown_field` |
| `fields_agree` | Schema | 多站点字段是否互相一致（FIELDS_DISAGREE） | pass/fail；<2 解码站点时 skip |
| `duplicate_label` | Schema | 是否重复打标 | 一致重复 → **warn**（不影响判定）；其他 skip |
| `forensic_wipe` | 取证 | 擦除证据综合评估 | `metadata_shell_empty` 信号 → error → inconclusive |
| `forensic_checksum` | 取证 | CRC/校验信号评估 | M1 基线：任何 CRC 失配 → warn（字段仍可读时判定随字段） |
| `forensic_padding` | 取证 | 异常填充信号评估 | warn |
| `text_explicit_prefix` / `text_explicit_suffix` | 显式 | 文本首/尾显式提示语格式 | 待实现 |
| `carrier_support` | M1 临时 | 该载体/通道是否已实现完整校验 | 未实现 → error+`carrier_unsupported` → inconclusive（宁降档不猜测） |

## 聚合规则（四档判定，冻结）

任何 `error` → `inconclusive`；任何 `fail` → `noncompliant`；有解码站点 → `compliant`；否则 `not_found`。

## Signal registry（取证信号，证据不评价）

`metadata_shell_empty` / `anomalous_padding` / `checksum_mismatch` / `structure_truncated` / `structure_malformed` / `resource_limit_exceeded` / `carrier_ambiguous`
