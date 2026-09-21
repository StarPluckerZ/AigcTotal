using System.Collections.Generic;
using AigcTotal.GB45438.Carriers;

namespace AigcTotal.GB45438.Schema
{
    /// <summary>
    /// GB 45438-2025 附录 E 七字段模型。键为标准原词（PascalCase，透传进报告）；
    /// 值为负载里的原始文本（不做归一化——Label 的数字/字符串形式以原文为准，待标准原文终确）。
    /// </summary>
    public static class AnnexEFields
    {
        public const string Label = "Label";
        public const string ContentProducer = "ContentProducer";
        public const string ProduceID = "ProduceID";
        public const string ReservedCode1 = "ReservedCode1";
        public const string ContentPropagator = "ContentPropagator";
        public const string PropagateID = "PropagateID";
        public const string ReservedCode2 = "ReservedCode2";

        /// <summary>标准定义的字段名集合。</summary>
        public static readonly IReadOnlyList<string> Known =
            new[] { Label, ContentProducer, ProduceID, ReservedCode1, ContentPropagator, PropagateID, ReservedCode2 };

        /// <summary>必填且非空（基线判定，待附录 E 原文终确：ReservedCode 与传播字段的空值语义）。</summary>
        public static readonly IReadOnlyList<string> RequiredNonEmpty =
            new[] { Label, ContentProducer, ProduceID };

        public static bool IsKnown(string fieldName)
        {
            foreach (var known in Known)
            {
                if (known == fieldName) return true;
            }
            return false;
        }
    }

    /// <summary>负载解码结果：字段表或失败码。</summary>
    public sealed class PayloadDecodeResult
    {
        public PayloadDecodeResult(IReadOnlyDictionary<string, string>? fields, string? errorCode)
        {
            Fields = fields;
            ErrorCode = errorCode;
        }

        /// <summary>七字段（含未知键，交校验器执行严格策略）；失败为 null。</summary>
        public IReadOnlyDictionary<string, string>? Fields { get; }

        /// <summary>失败码（CheckCodes）；成功为 null。</summary>
        public string? ErrorCode { get; }
    }

    /// <summary>负载解码器：原始负载字节 → 七字段表。</summary>
    public interface IPayloadDecoder
    {
        PayloadEncoding Encoding { get; }

        PayloadDecodeResult Decode(byte[] payload);
    }
}
