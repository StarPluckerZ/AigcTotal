using System;

namespace AigcTotal.Log.Signing
{
    /// <summary>
    /// 报告/checkpoint 签名原语（ES256 = ECDSA P-256 + SHA-256）。实现留在闭源仓（KMS 托管）；
    /// 本接口即闭源实现的全部契约：对输入字节签名，返回 64 字节 P1363（r‖s）。
    /// 实现必须确定性可复现或由 KMS 保证——签名不入 canonical 字节，仅入 tree_head_signature 字段。
    /// </summary>
    public interface IReportSigner
    {
        byte[] Sign(byte[] data);
    }
}
