# Check registry（check id 注册表）

> 只增不改（additive-only）。每个 id 回答一个原子验证问题；outcome ∈ pass / fail / warn / skip / error。
> 发现层检查只有 pass/skip，永不 fail：标识缺失是 verdict 的事，不是检查失败。

| id | 层 | 回答的问题 | outcome 语义 |
|---|---|---|---|
| `carrier_detect` | 探测 | 载体能否唯一确定 | pass=唯一命中；error+`unknown_format`/`ambiguous_format` → inconclusive |
| `container_structure` | 探测 | 容器是否完整可遍历 | pass；error+`structure_truncated`/`structure_malformed`/`resource_limit_exceeded` → inconclusive |
| `png_text_aigc` | 发现 | PNG tEXt / zTXt（关键字 AIGC）通道是否发现标识站点 | pass=发现 ≥1 站点；skip=无；负载支持裸 JSON 与 `{"AIGC":{…}}` 包裹两种形态（后者见 TC260-PG-20259A 附录 B）；zTXt 为 zlib 压缩负载（解压后校验，膨胀以 MaxAlloc 封顶，站点坐标指向文件内压缩字节区），畸形 zlib 流 → 畸形信号 → inconclusive |
| `png_xmp_aigc` | 发现 | PNG iTXt（关键字 XML:com.adobe.xmp）XMP 通道是否发现标识 | pass/skip；TC260 包装层（`<TC260:AIGC>` 内嵌附录 E JSON，ns=http://www.tc260.org.cn/ns/AIGC/1.0/）已支持；压缩 iTXt（flag≠0）→ error → inconclusive |
| `jpeg_app1_xmp` | 发现 | JPEG APP1 XMP 是否发现标识 | pass/skip |
| `jpeg_exif_usercomment` | 发现 | JPEG EXIF UserComment（APP1 "Exif\0\0" → IFD0 tag 0x9286，type 7）是否发现标识（TC260-PG-20259A 附录 B 包裹形态） | pass/skip；负载仅识别 `{"AIGC"…`/`{"Label"…` 形态（UserComment 为通用备注字段，普通文本静默忽略，防误判）；tag 命中但值不可读 → error → inconclusive |
| `mp4_xmp_aigc` | 发现 | MP4 XMP-aigc 通道（uuid box，Adobe XMP UUID）是否发现标识 | pass/skip |
| `mp4_udta_aigc` | 发现 | MP4 udta 元数据通道是否发现标识 | pass/skip；**主通道** = moov.udta.meta.keys(key=AIGC) + ilst（TC260-PG-20257A 规定，ffmpeg use_metadata_tags 形态）；字面 aigc box 为兼容探测 |
| `wav_riff_aigc` | 发现 | WAV RIFF AIGC chunk 是否发现标识 | pass/skip；空壳 → 信号 `metadata_shell_empty` → inconclusive |
| `mp3_id3_txxx` | 发现 | MP3 ID3 TXXX（描述=AIGC）帧是否发现标识 | pass/skip（v2.3/v2.4，UTF-8/Latin-1 描述；v2.4 扩展头按 syncsafe 读取）；ID3v2.2（3 字节帧 ID）→ error+`value_unsupported` → inconclusive（显式不支持，不出假 not_found） |
| `text_prompt_affix` | 发现 | 文本首尾标识区是否发现提示语模式（要素组合匹配：人工智能/AI 要素 + 生成/合成要素，5.1 b)） | pass/skip；二进制误落到文本兜底 → carrier_detect error `unknown_format`；大文件只取首尾窗口判定（头 64KB/尾 4KB，中段流式校验编码） |
| `flac_vorbis_aigc` | 发现 | FLAC VORBIS_COMMENT 块（key=AIGC）是否发现标识（TC260-PG-202510A） | pass/skip |
| `ogg_comment_aigc` | 发现 | OGG（Vorbis/OPUS）注释头（key=AIGC）是否发现标识（TC260-PG-202510A） | pass/skip |
| `avi_riff_aigc` | 发现 | AVI LIST/INFO 内 AIGC 子块是否发现标识（TC260-PG-20257A） | pass/skip |
| `webp_xmp_aigc` | 发现 | WebP "XMP " chunk 是否发现标识（TC260-PG-20259A） | pass/skip |
| `tiff_ifd0_aigc` | 发现 | TIFF IFD0 tag 0x2BC（type 7）是否发现标识（TC260-PG-20259A） | pass/skip |
| `gif_appext_aigc` | 发现 | GIF Application Extension（XMP Data）是否发现标识（TC260-PG-20259A） | pass/skip |
| `ooxml_custom_aigc` | 发现 | OOXML docProps/custom.xml property[@name=AIGC] 是否发现标识（TC260-PG-20258A，docx/pptx/xlsx 族） | pass/skip；条目在但不可读 → error → inconclusive |
| `pdf_info_aigc` | 发现 | PDF Info Dictionary /AIGC 键是否发现标识（TC260-PG-20258A）；有界裸扫描（头/尾窗口，预算对半），对象流压缩形态与大文件中部 Info 不可见（已知局限） | pass/skip |
| `text_front_matter_aigc` | 发现 | Markdown front matter AIGC 映射是否发现标识（TC260-PG-20258A，YAML 形态，字段照常校验） | pass/skip |
| `annexe_fields` | Schema | 七字段是否齐备（Label/ContentProducer/ProduceID 必填非空，基线） | pass；fail+`field_missing`；error+`payload_malformed` |
| `annexe_charset` | Schema | 字段值是否符合附录 E 字符集（j 条：GB18030 码位 0x21~0x7E 除 `\"` 转义；空格/控制/DEL/多字节均违规——中文名称应使用编码） | pass；fail+`charset_invalid` |
| `annexe_label_enum` | Schema | Label 枚举取值是否合法（附录 E c)：1=属于/2=可能/3=疑似，类型为字符串；数字形态宽容通过） | pass；fail+`label_enum_invalid` |
| `annexe_unknown_field` | Schema | 是否出现未知字段（严格策略） | pass；fail+`unknown_field` |
| `fields_agree` | Schema | 多站点字段是否互相一致（FIELDS_DISAGREE） | pass/fail；<2 解码站点时 skip |
| `duplicate_label` | Schema | 是否重复打标 | 一致重复 → **fail**（GB 45438-2025 第 6.1 c)：应仅保留一份）→ 不合规；其他 skip |
| `forensic_wipe` | 取证 | 擦除证据综合评估 | `metadata_shell_empty` 信号 → error → inconclusive |
| `forensic_checksum` | 取证 | CRC/校验信号评估 | M1 基线：任何 CRC 失配 → warn（字段仍可读时判定随字段） |
| `forensic_padding` | 取证 | 异常填充信号评估 | warn |
| `text_explicit_prefix` / `text_explicit_suffix` | 显式 | 文本首/尾显式提示语格式 | 已实现：首/尾 64 字符窗口内"人工智能要素（人工智能/AI）+ 生成合成要素（生成/合成）"同现 → pass（GB 45438-2025 第 5.1 条要素组合判定；角标形式不单独判定，宁 skip）；窗口取自头/尾窗口去空白后的文本 |
| `carrier_support` | M1 临时 | 该载体/通道是否已实现完整校验 | 未实现 → error+`carrier_unsupported` → inconclusive（宁降档不猜测） |

## 聚合规则（四档判定，冻结）

任何 `error` → `inconclusive`；任何 `fail` → `noncompliant`；有解码站点 → `compliant`；否则 `not_found`。

## Signal registry（取证信号，证据不评价）

`metadata_shell_empty` / `anomalous_padding` / `checksum_mismatch` / `structure_truncated` / `structure_malformed` / `resource_limit_exceeded` / `carrier_ambiguous`
