using System;
using System.Collections.Generic;
using System.Xml;
using AigcTotal.GB45438.Carriers;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Schema
{
    /// <summary>
    /// XMP（RDF/XML）负载解码器：流式 XmlReader（禁 DTD）。
    /// 优先解析 TC260 包装层：&lt;{ns}AIGC&gt; 内嵌附录 E JSON。命名空间 URI 已由指南原文确认
    /// （http://www.tc260.org.cn/ns/AIGC/1.0/，TC260-PG-20259A/202510A/20257A），元素名 AIGC、
    /// 前缀不限（正文 TC260、音频附录F 小写 tc260）；同名多元素时优先取 TC260 URI 者。
    /// 无包装层时退回直连匹配：七字段以属性（&lt;rdf:Description Label="1" …&gt;）或元素
    /// （&lt;Label&gt;1&lt;/Label&gt;）形式出现——按 LocalName 匹配。
    /// 畸形 XML → PayloadMalformed。
    /// </summary>
    public sealed class XmpPayloadDecoder : IPayloadDecoder
    {
        /// <summary>指南确认的 TC260 官方命名空间 URI。</summary>
        public const string Tc260AigcNamespace = "http://www.tc260.org.cn/ns/AIGC/1.0/";

        public PayloadEncoding Encoding => PayloadEncoding.XmpAigc;

        public PayloadDecodeResult Decode(byte[] payload)
        {
            var directFields = new Dictionary<string, string>();
            string? wrappedJson = null;
            bool wrappedIsTc260 = false;
            try
            {
                using (var reader = XmlReader.Create(new System.IO.MemoryStream(payload), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                    IgnoreWhitespace = true,
                    XmlResolver = null,
                }))
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType != XmlNodeType.Element) continue;

                        // TC260 包装层：<TC260:AIGC>{JSON}</TC260:AIGC>——实体由 XmlReader 解码；
                        // 同名多元素时优先取 TC260 官方 URI 者
                        if (reader.LocalName == "AIGC" && !reader.IsEmptyElement)
                        {
                            string ns = reader.NamespaceURI;
                            string json = reader.ReadString();
                            if (wrappedJson == null || (!wrappedIsTc260 && ns == Tc260AigcNamespace))
                            {
                                wrappedJson = json;
                                wrappedIsTc260 = ns == Tc260AigcNamespace;
                            }
                            continue;
                        }

                        // 属性形：<rdf:Description Label="1" ContentProducer="…"/>——不移动读位置
                        if (reader.HasAttributes)
                        {
                            reader.MoveToFirstAttribute();
                            do
                            {
                                if (AnnexEFields.IsKnown(reader.LocalName) && !directFields.ContainsKey(reader.LocalName))
                                {
                                    directFields[reader.LocalName] = reader.Value;
                                }
                            }
                            while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }

                        // 元素形：<Label>2</Label>——仅对已知字段的元素调 ReadString，
                        // 对容器元素（rdf:Description 等）调用会吞掉全部子节点
                        if (!reader.IsEmptyElement && AnnexEFields.IsKnown(reader.LocalName))
                        {
                            string? text = reader.ReadString();
                            if (!string.IsNullOrEmpty(text) && !directFields.ContainsKey(reader.LocalName))
                            {
                                directFields[reader.LocalName] = text;
                            }
                        }
                    }
                }

                // 包装层优先：TC260:AIGC 内的 JSON 是权威负载；解析失败即负载失败（宁降档）
                if (wrappedJson != null)
                {
                    PayloadDecodeResult jsonResult = JsonPayloadDecoder.Decode(wrappedJson);
                    if (jsonResult.Fields != null)
                    {
                        return jsonResult;
                    }
                    return new PayloadDecodeResult(null, jsonResult.ErrorCode ?? CheckCodes.PayloadMalformed);
                }

                return new PayloadDecodeResult(directFields, null);
            }
            catch (XmlException)
            {
                return new PayloadDecodeResult(null, CheckCodes.PayloadMalformed);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 信任边界策略：不可信负载触发的任何下游解析器异常（如 BCL XmlReader 在
                // 编码嗅探边角抛出的 ArgumentOutOfRange）一律视为负载畸形，绝不让其逃出
                return new PayloadDecodeResult(null, CheckCodes.PayloadMalformed);
            }
        }
    }
}
