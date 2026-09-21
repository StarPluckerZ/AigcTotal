using System;
using System.Collections.Generic;
using System.Xml;
using AigcTotal.GB45438.Carriers;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438.Schema
{
    /// <summary>
    /// XMP（RDF/XML）负载解码器：流式 XmlReader（禁 DTD）。
    /// 优先解析 TC260 包装层：&lt;{ns}AIGC&gt; 内嵌附录 E JSON（ns = http://www.tc260.org.cn/ns/AIGC/1.0/，
    /// 2026-09 豆包真实样本实测；按 LocalName "AIGC" 匹配、命名空间不限，前向兼容）。
    /// 无包装层时退回直连匹配：七字段以属性（&lt;rdf:Description Label="1" …&gt;）或元素
    /// （&lt;Label&gt;1&lt;/Label&gt;）形式出现——按 LocalName 匹配，命名空间待指南原文确认后收紧。
    /// 畸形 XML → PayloadMalformed。
    /// </summary>
    public sealed class XmpPayloadDecoder : IPayloadDecoder
    {
        public PayloadEncoding Encoding => PayloadEncoding.XmpAigc;

        public PayloadDecodeResult Decode(byte[] payload)
        {
            var directFields = new Dictionary<string, string>();
            string? wrappedJson = null;
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

                        // TC260 包装层：<TC260:AIGC>{JSON}</TC260:AIGC>——实体由 XmlReader 解码
                        if (reader.LocalName == "AIGC" && !reader.IsEmptyElement)
                        {
                            wrappedJson ??= reader.ReadString();
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
        }
    }
}
