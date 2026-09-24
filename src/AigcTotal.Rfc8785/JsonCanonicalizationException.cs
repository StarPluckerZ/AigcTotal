using System;

namespace AigcTotal.Rfc8785
{
    /// <summary>
    /// 输入数据不满足 RFC 8785 / I-JSON 可规范化前提时抛出：
    /// 非 IEEE 754 可表示数字、NaN/Infinity、孤立代理项、重复键、非法 JSON 语法等。
    /// RFC 8785 §2：此类输入 MUST 导致实现以适当错误终止。
    /// </summary>
    public sealed class JsonCanonicalizationException : FormatException
    {
        public JsonCanonicalizationException()
        {
        }

        public JsonCanonicalizationException(string message)
            : base(message)
        {
        }

        public JsonCanonicalizationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
