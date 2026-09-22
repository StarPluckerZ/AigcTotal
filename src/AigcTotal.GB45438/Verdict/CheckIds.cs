namespace AigcTotal.GB45438.Verdict
{
    /// <summary>
    /// check id 注册表：一条原子验证问题的永久机器名（只增不改）。
    /// 逐 id 的适用载体 / outcome 语义 / detail 形状 / 条款映射见 docs/checks.md。
    /// </summary>
    public static class CheckIds
    {
        // —— 探测层 ——
        public const string CarrierDetect = "carrier_detect";
        public const string ContainerStructure = "container_structure";

        // —— 发现层（只有 pass/skip，永不 fail：标识缺失是 verdict 的事）——
        public const string PngTextAigc = "png_text_aigc";
        public const string PngXmpAigc = "png_xmp_aigc";
        public const string JpegApp1Xmp = "jpeg_app1_xmp";
        public const string Mp4XmpAigc = "mp4_xmp_aigc";
        public const string Mp4UdtaAigc = "mp4_udta_aigc";
        public const string WavRiffAigc = "wav_riff_aigc";
        public const string Mp3Id3Txxx = "mp3_id3_txxx";
        public const string TextPromptAffix = "text_prompt_affix";
        public const string FlacVorbisAigc = "flac_vorbis_aigc";
        public const string OggCommentAigc = "ogg_comment_aigc";
        public const string AviRiffAigc = "avi_riff_aigc";
        public const string WebpXmpAigc = "webp_xmp_aigc";
        public const string TiffIfd0Aigc = "tiff_ifd0_aigc";
        public const string GifAppExtAigc = "gif_appext_aigc";
        public const string OoxmlCustomAigc = "ooxml_custom_aigc";
        public const string PdfInfoAigc = "pdf_info_aigc";
        public const string TextFrontMatterAigc = "text_front_matter_aigc";

        // —— Schema 层 ——
        public const string AnnexeFields = "annexe_fields";
        public const string AnnexeCharset = "annexe_charset";
        public const string AnnexeLabelEnum = "annexe_label_enum";
        public const string AnnexeUnknownField = "annexe_unknown_field";
        public const string FieldsAgree = "fields_agree";
        public const string DuplicateLabel = "duplicate_label";

        // —— 取证层 ——
        public const string ForensicWipe = "forensic_wipe";
        public const string ForensicChecksum = "forensic_checksum";
        public const string ForensicPadding = "forensic_padding";

        // —— 显式标识层（文本）——
        public const string TextExplicitPrefix = "text_explicit_prefix";
        public const string TextExplicitSuffix = "text_explicit_suffix";

        // —— M1 阶段临时项：载体/通道尚未实现完整校验时占位（error → inconclusive），补齐后仅对未来载体保留 ——
        public const string CarrierSupport = "carrier_support";
    }

    /// <summary>失败明细码（只增不改）。</summary>
    public static class CheckCodes
    {
        public const string FieldMissing = "field_missing";
        public const string CharsetInvalid = "charset_invalid";
        public const string LabelEnumInvalid = "label_enum_invalid";
        public const string UnknownField = "unknown_field";
        public const string TlvTruncated = "tlv_truncated";
        public const string StructureMalformed = "structure_malformed";
        public const string StructureTruncated = "structure_truncated";
        public const string CrcMismatch = "crc_mismatch";
        public const string UnknownFormat = "unknown_format";
        public const string AmbiguousFormat = "ambiguous_format";
        public const string ResourceLimit = "resource_limit_exceeded";
        public const string CarrierUnsupported = "carrier_unsupported";
        public const string ValueUnsupported = "value_unsupported";
        public const string PayloadMalformed = "payload_malformed";
    }
}
