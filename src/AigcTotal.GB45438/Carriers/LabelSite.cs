using System.Collections.Generic;

namespace AigcTotal.GB45438.Carriers
{
    /// <summary>隐式标识负载的编码方式。</summary>
    public enum PayloadEncoding
    {
        /// <summary>附录 E 的 JSON 负载。</summary>
        Json = 1,

        /// <summary>XMP（RDF/XML）承载的 aigc 字段。</summary>
        XmpAigc = 2,

        /// <summary>ID3 TXXX 文本帧。</summary>
        Id3Text = 3,

        /// <summary>文本载体首尾提示语模式。</summary>
        PromptPattern = 4,

        /// <summary>Markdown front matter 中的 AIGC YAML 映射（TC260-PG-20258A）。</summary>
        FrontMatterYaml = 5,
    }

    public static class PayloadEncodingTokens
    {
        public static string ToToken(PayloadEncoding encoding)
        {
            switch (encoding)
            {
                case PayloadEncoding.Json: return "json";
                case PayloadEncoding.XmpAigc: return "xmp_aigc";
                case PayloadEncoding.Id3Text: return "id3_text";
                case PayloadEncoding.PromptPattern: return "prompt_pattern";
                case PayloadEncoding.FrontMatterYaml: return "front_matter_yaml";
                default: return encoding.ToString().ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// 结构化定位（canonical 形态：{"carrier":…,"path":[…],"offset":…,"length":…}；
    /// carrier 由报告 input.carrier 继承，故此处不含）。path 元素为 string（格式规范原词）或 int（同类型序号）。
    /// </summary>
    public sealed class SiteLocation
    {
        public SiteLocation(IReadOnlyList<object> path, long offset, long length)
        {
            Path = path;
            Offset = offset;
            Length = length;
        }

        /// <summary>格式规范原词构成的结构路径，如 ["tEXt", 6]。</summary>
        public IReadOnlyList<object> Path { get; }

        /// <summary>负载在文件内的字节偏移。</summary>
        public long Offset { get; }

        /// <summary>负载字节长度。</summary>
        public long Length { get; }
    }

    /// <summary>一个标识出现点（证据现场）。</summary>
    public sealed class LabelSite
    {
        public LabelSite(SiteLocation location, PayloadEncoding encoding, byte[] rawPayload)
        {
            Location = location;
            Encoding = encoding;
            RawPayload = rawPayload;
        }

        public SiteLocation Location { get; }

        public PayloadEncoding Encoding { get; }

        /// <summary>原始负载字节（verify 重跑比对用）。</summary>
        public byte[] RawPayload { get; }

        /// <summary>负载解码出的七字段（附录 E 原词 PascalCase 键 → 原始值文本）；解码失败为 null。</summary>
        public IReadOnlyDictionary<string, string>? Fields { get; internal set; }

        /// <summary>解码/校验诊断码列表（CheckCodes）。</summary>
        public IReadOnlyList<string> DecodeErrors { get; internal set; } = new List<string>();
    }
}
