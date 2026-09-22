using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AigcTotal.TestSupport
{
    /// <summary>
    /// 自造测试语料构造器（golden fixtures 的种子）：不使用任何平台版权文件。
    /// CRC-32 与库内实现独立（规避同源错误：此表多项式生成采用逐位算法）。
    /// </summary>
    public static class PngBuilder
    {
        private static readonly byte[] Signature =
            { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public static byte[] Build(params PngChunk[] chunks)
        {
            var ms = new MemoryStream();
            ms.Write(Signature, 0, Signature.Length);
            foreach (var chunk in chunks)
            {
                ms.Write(chunk.ToBytes(), 0, chunk.ToBytes().Length);
            }
            return ms.ToArray();
        }

        public static PngChunk Text(string keyword, string text)
        {
            byte[] keywordBytes = Encoding.ASCII.GetBytes(keyword);
            byte[] textBytes = Encoding.UTF8.GetBytes(text);
            var data = new byte[keywordBytes.Length + 1 + textBytes.Length];
            keywordBytes.CopyTo(data, 0);
            data[keywordBytes.Length] = 0;
            textBytes.CopyTo(data, keywordBytes.Length + 1);
            return Data("tEXt", data);
        }

        public static PngChunk TextNoSeparator(byte[] data) => Data("tEXt", data);

        public static PngChunk Data(string type, byte[] data, bool corruptCrc = false)
        {
            var crcInput = new byte[4 + data.Length];
            Encoding.ASCII.GetBytes(type).CopyTo(crcInput, 0);
            data.CopyTo(crcInput, 4);
            uint crc = Crc32(crcInput);
            if (corruptCrc) crc ^= 0xDEADBEEFu;
            return new PngChunk(type, data, crc);
        }

        public static uint Crc32(byte[] bytes)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
            }
            return ~crc;
        }
    }

    public sealed class PngChunk
    {
        private readonly string _type;
        private readonly byte[] _data;
        private readonly uint _crc;

        public PngChunk(string type, byte[] data, uint crc)
        {
            _type = type;
            _data = data;
            _crc = crc;
        }

        public byte[] ToBytes()
        {
            var ms = new MemoryStream();
            uint length = (uint)_data.Length;
            ms.WriteByte((byte)(length >> 24));
            ms.WriteByte((byte)(length >> 16));
            ms.WriteByte((byte)(length >> 8));
            ms.WriteByte((byte)length);
            byte[] typeBytes = Encoding.ASCII.GetBytes(_type);
            ms.Write(typeBytes, 0, 4);
            ms.Write(_data, 0, _data.Length);
            ms.WriteByte((byte)(_crc >> 24));
            ms.WriteByte((byte)(_crc >> 16));
            ms.WriteByte((byte)(_crc >> 8));
            ms.WriteByte((byte)_crc);
            return ms.ToArray();
        }
    }

    public static class JpegBuilder
    {
        private static readonly byte[] XmpHeader =
            Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

        public static byte[] WithXmp(string xmp)
        {
            var ms = new MemoryStream();
            ms.WriteByte(0xFF); ms.WriteByte(0xD8);                 // SOI
            byte[] payload = new byte[XmpHeader.Length + Encoding.UTF8.GetByteCount(xmp)];
            XmpHeader.CopyTo(payload, 0);
            Encoding.UTF8.GetBytes(xmp).CopyTo(payload, XmpHeader.Length);
            WriteSegment(ms, 0xE1, payload);                       // APP1 XMP
            ms.WriteByte(0xFF); ms.WriteByte(0xDA);                // SOS（元数据结束）
            ms.WriteByte(0xFF); ms.WriteByte(0xD9);                // EOI
            return ms.ToArray();
        }

        public static byte[] WithoutXmp()
        {
            var ms = new MemoryStream();
            ms.WriteByte(0xFF); ms.WriteByte(0xD8);
            ms.WriteByte(0xFF); ms.WriteByte(0xD9);
            return ms.ToArray();
        }

        private static void WriteSegment(MemoryStream ms, byte marker, byte[] payload)
        {
            ms.WriteByte(0xFF); ms.WriteByte(marker);
            uint length = (uint)(payload.Length + 2);
            ms.WriteByte((byte)(length >> 8));
            ms.WriteByte((byte)length);
            ms.Write(payload, 0, payload.Length);
        }
    }

    public static class Mp4Builder
    {
        // Adobe XMP uuid box：BE7ACFCB-97A9-42E8-9C71-999491E3AFAC
        private static readonly byte[] XmpUuid =
        {
            0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8,
            0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC,
        };

        public static byte[] Ftyp(string brand)
        {
            byte[] data = new byte[8];
            Encoding.ASCII.GetBytes(brand).CopyTo(data, 0);
            data[4] = 0; data[5] = 0; data[6] = 0; data[7] = 1; // minor version
            return Box("ftyp", data);
        }

        public static byte[] Box(string type, byte[] data)
        {
            var ms = new MemoryStream();
            WriteU32BE(ms, (uint)(8 + data.Length));
            byte[] t = Encoding.ASCII.GetBytes(type);
            ms.Write(t, 0, 4);
            ms.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        public static byte[] Container(string type, params byte[][] children)
        {
            using (var inner = new MemoryStream())
            {
                foreach (byte[] child in children)
                {
                    inner.Write(child, 0, child.Length);
                }
                return Box(type, inner.ToArray());
            }
        }

        public static byte[] UdtaAigc(string json) => Container("udta", Box("aigc", Encoding.UTF8.GetBytes(json)));

        /// <summary>
        /// TC260-PG-20257A 规定形态：udta/meta（full box）/keys（mdta 命名空间 key=AIGC）
        /// + ilst（序号条目 → data box → 裸 JSON 负载）。ffmpeg -movflags use_metadata_tags 实测一致。
        /// </summary>
        public static byte[] UdtaMetaAigc(string json)
        {
            byte[] keyName = Encoding.ASCII.GetBytes("AIGC");
            uint keySize = (uint)(4 + 4 + keyName.Length + 1); // key_size字段自身 + 'mdta' + name + NUL
            byte[] keyEntry = new[]
            {
                U32BE(keySize),
                Encoding.ASCII.GetBytes("mdta"),
                keyName,
                new byte[] { 0 },
            }.SelectMany(x => x).ToArray();
            byte[] keys = Box("keys", new[]
            {
                new byte[] { 0, 0, 0, 0 },   // version/flags
                new byte[] { 0, 0, 0, 1 },   // entry count = 1
                keyEntry,
            }.SelectMany(x => x).ToArray());

            byte[] dataBox = Box("data", new[]
            {
                new byte[] { 0, 0, 0, 1 },   // 数据类型 1 = UTF-8
                new byte[] { 0, 0, 0, 0 },   // locale
                Encoding.UTF8.GetBytes(json),
            }.SelectMany(x => x).ToArray());
            byte[] ilst = Box("ilst", BoxRaw(new byte[] { 0, 0, 0, 1 }, dataBox)); // 条目序号 1

            byte[] metaPayload = new byte[4].Concat(keys).Concat(ilst).ToArray(); // full box version/flags
            return Container("udta", Box("meta", metaPayload));
        }

        /// <summary>类型字段为原始字节的 box（如 ilst 的序号条目）。</summary>
        public static byte[] BoxRaw(byte[] typeBytes, byte[] data)
        {
            var ms = new MemoryStream();
            WriteU32BE(ms, (uint)(8 + data.Length));
            ms.Write(typeBytes, 0, 4);
            ms.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        private static byte[] U32BE(uint value) => new[]
        {
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value,
        };

        public static byte[] UuidXmp(string xmp)
        {
            byte[] data = new byte[XmpUuid.Length + Encoding.UTF8.GetByteCount(xmp)];
            XmpUuid.CopyTo(data, 0);
            Encoding.UTF8.GetBytes(xmp).CopyTo(data, XmpUuid.Length);
            return Box("uuid", data);
        }

        public static byte[] Mdat(int declaredSize)
        {
            var ms = new MemoryStream();
            WriteU32BE(ms, (uint)(8 + declaredSize));
            ms.Write(Encoding.ASCII.GetBytes("mdat"), 0, 4);
            ms.Write(new byte[declaredSize], 0, declaredSize);
            return ms.ToArray();
        }

        public static byte[] Build(params byte[][] boxes)
        {
            using (var ms = new MemoryStream())
            {
                foreach (byte[] box in boxes)
                {
                    ms.Write(box, 0, box.Length);
                }
                return ms.ToArray();
            }
        }

        private static void WriteU32BE(MemoryStream ms, uint value)
        {
            ms.WriteByte((byte)(value >> 24));
            ms.WriteByte((byte)(value >> 16));
            ms.WriteByte((byte)(value >> 8));
            ms.WriteByte((byte)value);
        }
    }

    public static class WavBuilder
    {
        public static byte[] Build(params (string Id, byte[] Data)[] chunks)
        {
            uint dataSize = 4; // "WAVE"
            foreach (var chunk in chunks)
            {
                dataSize += (uint)(8 + chunk.Data.Length + chunk.Data.Length % 2);
            }
            var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("RIFF"), 0, 4);
            ms.WriteByte((byte)dataSize);
            ms.WriteByte((byte)(dataSize >> 8));
            ms.WriteByte((byte)(dataSize >> 16));
            ms.WriteByte((byte)(dataSize >> 24));
            ms.Write(Encoding.ASCII.GetBytes("WAVE"), 0, 4);
            foreach (var chunk in chunks)
            {
                ms.Write(Encoding.ASCII.GetBytes(chunk.Id), 0, 4);
                uint len = (uint)chunk.Data.Length;
                ms.WriteByte((byte)len);
                ms.WriteByte((byte)(len >> 8));
                ms.WriteByte((byte)(len >> 16));
                ms.WriteByte((byte)(len >> 24));
                ms.Write(chunk.Data, 0, chunk.Data.Length);
                if (len % 2 == 1) ms.WriteByte(0); // 奇数填充
            }
            return ms.ToArray();
        }

        public static (string Id, byte[] Data) Aigc(string json) => ("AIGC", Encoding.UTF8.GetBytes(json));

        public static (string Id, byte[] Data) Fmt() => ("fmt ", new byte[16]);
    }

    public static class Id3Builder
    {
        public static byte[] V24(params (string Id, byte[] Data)[] frames)
        {
            return Build(versionMajor: 4, frames);
        }

        public static (string Id, byte[] Data) TxxxFrame(string description, string text)
        {
            byte[] desc = Encoding.UTF8.GetBytes(description);
            byte[] body = Encoding.UTF8.GetBytes(text);
            var data = new byte[1 + desc.Length + 1 + body.Length];
            data[0] = 3; // UTF-8 编码字节
            desc.CopyTo(data, 1);
            data[1 + desc.Length] = 0;
            body.CopyTo(data, 2 + desc.Length);
            return ("TXXX", data);
        }

        private static byte[] FrameBytes(string id, byte[] data)
        {
            var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes(id), 0, 4);
            // v2.4 帧长 syncsafe
            uint size = (uint)data.Length;
            ms.WriteByte((byte)((size >> 21) & 0x7F));
            ms.WriteByte((byte)((size >> 14) & 0x7F));
            ms.WriteByte((byte)((size >> 7) & 0x7F));
            ms.WriteByte((byte)(size & 0x7F));
            ms.WriteByte(0); ms.WriteByte(0); // flags
            ms.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        private static byte[] Build(byte versionMajor, (string Id, byte[] Data)[] frames)
        {
            uint bodySize = 0;
            foreach (var frame in frames)
            {
                byte[] bytes = FrameBytes(frame.Id, frame.Data);
                bodySize += (uint)bytes.Length;
            }
            var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes("ID3"), 0, 3);
            ms.WriteByte(versionMajor);
            ms.WriteByte(0); // revision
            ms.WriteByte(0); // flags
            ms.WriteByte((byte)((bodySize >> 21) & 0x7F));
            ms.WriteByte((byte)((bodySize >> 14) & 0x7F));
            ms.WriteByte((byte)((bodySize >> 7) & 0x7F));
            ms.WriteByte((byte)(bodySize & 0x7F));
            foreach (var frame in frames)
            {
                byte[] bytes = FrameBytes(frame.Id, frame.Data);
                ms.Write(bytes, 0, bytes.Length);
            }
            return ms.ToArray();
        }
    }

    /// <summary>小端 u32 助手（LE 系语料构造用）。</summary>
    public static class Bytes
    {
        public static byte[] U32LE(uint v) => new[]
        {
            (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24),
        };
    /// <summary>小端 RIFF chunk：fourcc(4) + len(4) + data。</summary>
    public static byte[] BoxLE(string fourcc, byte[] data)
    {
        var ms = new MemoryStream();
        byte[] t = Encoding.ASCII.GetBytes(fourcc);
        ms.Write(t, 0, t.Length);
        ms.Write(Bytes.U32LE((uint)data.Length), 0, 4);
        ms.Write(data, 0, data.Length);
        return ms.ToArray();
    }
    }

public static class FlacBuilder
{
    public static byte[] Build(string aigcJson, bool withAigcPrefix = true)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("fLaC"), 0, 4);
        WriteBlock(ms, 0, new byte[34], last: false);          // STREAMINFO 占位
        var comment = new List<byte>();
        comment.AddRange(Bytes.U32LE(4));
        comment.AddRange(Encoding.ASCII.GetBytes("test"));
        byte[] entry = Encoding.UTF8.GetBytes((withAigcPrefix ? "AIGC=" : "OTHER=") + aigcJson);
        comment.AddRange(Bytes.U32LE(1));                      // 条目数
        comment.AddRange(Bytes.U32LE((uint)entry.Length));
        comment.AddRange(entry);
        WriteBlock(ms, 4, comment.ToArray(), last: true);      // VORBIS_COMMENT
        return ms.ToArray();
    }

    private static void WriteBlock(MemoryStream ms, byte type, byte[] data, bool last)
    {
        ms.WriteByte((byte)((last ? 0x80 : 0) | type));
        ms.WriteByte((byte)(data.Length >> 16));
        ms.WriteByte((byte)(data.Length >> 8));
        ms.WriteByte((byte)data.Length);
        ms.Write(data, 0, data.Length);
    }
}

public static class OggBuilder
{
    public static byte[] Build(string aigcJson)
    {
        byte[] idPacket = new byte[33];
        idPacket[0] = 0x01;
        Encoding.ASCII.GetBytes("vorbis").CopyTo(idPacket, 1);
        byte[] commentPkt = new List<byte>()
        {
            0x03,
        }
        .Concat(Encoding.ASCII.GetBytes("vorbis"))
        .Concat(Bytes.U32LE(4))
        .Concat(Encoding.ASCII.GetBytes("test"))
        .Concat(Bytes.U32LE(1))
        .Concat(Bytes.U32LE((uint)Encoding.UTF8.GetByteCount("AIGC=" + aigcJson)))
        .Concat(Encoding.UTF8.GetBytes("AIGC=" + aigcJson))
        .ToArray();

        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("OggS"), 0, 4);
        ms.WriteByte(0); ms.WriteByte(0x02);                    // version, BOS
        ms.Write(new byte[8], 0, 8);                            // granule
        ms.Write(new byte[] { 1, 0, 0, 0 }, 0, 4);              // serial
        ms.Write(new byte[] { 0, 0, 0, 0 }, 0, 4);              // sequence
        ms.Write(new byte[] { 0, 0, 0, 0 }, 0, 4);              // crc（解析器不校验）
        ms.WriteByte(2);                                        // 2 段
        ms.WriteByte((byte)idPacket.Length);                    // <255 → 包结束
        ms.WriteByte((byte)commentPkt.Length);
        ms.Write(idPacket, 0, idPacket.Length);
        ms.Write(commentPkt, 0, commentPkt.Length);
        return ms.ToArray();
    }
}

public static class AviBuilder
{
    /// <summary>aigcJson 为 null 时不写入 AIGC 子块（用于 not_found 场景）。</summary>
    public static byte[] Build(string? aigcJson)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("RIFF"), 0, 4);
        ms.Write(new byte[] { 0, 0, 0, 0 }, 0, 4);              // RIFF size 占位
        ms.Write(Encoding.ASCII.GetBytes("AVI "), 0, 4);
        void Chunk(string id, byte[] data)
        {
            ms.Write(Encoding.ASCII.GetBytes(id), 0, 4);
            ms.Write(Bytes.U32LE((uint)data.Length), 0, 4);
            ms.Write(data, 0, data.Length);
            if (data.Length % 2 == 1) ms.WriteByte(0);          // RIFF 奇数补齐
        }
        // LIST('INFO') 包体
        using (var info = new MemoryStream())
        {
            info.Write(Encoding.ASCII.GetBytes("INFO"), 0, 4);
            if (aigcJson != null)
            {
                byte[] aigcData = Encoding.UTF8.GetBytes(aigcJson + " ");
                ChunkInto(info, "AIGC", aigcData);
            }
            byte[] sub = { 1, 2, 3, 4 };
            ChunkInto(info, "JUNK", sub);
            Chunk("LIST", info.ToArray());
        }
        // 回填 RIFF size
        uint size = (uint)(ms.Length - 8);
        long pos = ms.Position;
        ms.Position = 4;
        ms.Write(Bytes.U32LE(size), 0, 4);
        ms.Position = pos;
        return ms.ToArray();
    }

    private static void ChunkInto(MemoryStream ms, string id, byte[] data)
    {
        ms.Write(Encoding.ASCII.GetBytes(id), 0, 4);
        ms.Write(Bytes.U32LE((uint)data.Length), 0, 4);
        ms.Write(data, 0, data.Length);
        if (data.Length % 2 == 1) ms.WriteByte(0);
    }
}

public static class WebpBuilder
{
    public static byte[] Build(string xmp)
    {
        byte[] xmpData = Encoding.UTF8.GetBytes(xmp);
        byte[] vp8x = Bytes.BoxLE("VP8X", new byte[10]);
        byte[] xmpChunk = Bytes.BoxLE("XMP ", xmpData);
        byte[] payload = vp8x.Concat(xmpChunk).ToArray();
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("RIFF"), 0, 4);
        ms.Write(Bytes.U32LE((uint)(4 + payload.Length)), 0, 4);
        ms.Write(Encoding.ASCII.GetBytes("WEBP"), 0, 4);
        ms.Write(payload, 0, payload.Length);
        return ms.ToArray();
    }
}

public static class TiffBuilder
{
    public static byte[] Build(string xmp)
    {
        byte[] xmpData = Encoding.UTF8.GetBytes(xmp);
        using (var ms = new MemoryStream())
        {
            ms.Write(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, 0, 4); // II + 42
            ms.Write(Bytes.U32LE(8), 0, 4);                              // IFD0 偏移
            ms.Write(new byte[] { 1, 0 }, 0, 2);                   // 条目数 = 1
            ms.Write(new byte[] { 0xBC, 0x02 }, 0, 2);             // tag 0x2BC
            ms.Write(new byte[] { 7, 0 }, 0, 2);                   // type 7 UNDEFINED
            ms.Write(Bytes.U32LE((uint)xmpData.Length), 0, 4);           // count
            ms.Write(Bytes.U32LE(26), 0, 4);                             // value 偏移 = 8+2+12+4
            ms.Write(Bytes.U32LE(0), 0, 4);                              // 下一 IFD = 0
            ms.Write(xmpData, 0, xmpData.Length);
            return ms.ToArray();
        }
    }
}

public static class GifBuilder
{
    public static byte[] Build(string xmp)
    {
        byte[] xmpData = Encoding.UTF8.GetBytes(xmp);
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("GIF89a"), 0, 6);
        ms.Write(new byte[] { 1, 0, 1, 0, 0x00, 0, 0 }, 0, 7);     // 屏幕描述符，无全局色表
        ms.WriteByte(0x21); ms.WriteByte(0xFF); ms.WriteByte(0x0B); // 应用扩展
        ms.Write(Encoding.ASCII.GetBytes("XMP Data"), 0, 8);
        ms.Write(new byte[] { 0xEC, 0x1B, 0xDF }, 0, 3);           // 鉴别码（不校验）
        for (int i = 0; i < xmpData.Length; i += 255)
        {
            int len = Math.Min(255, xmpData.Length - i);
            ms.WriteByte((byte)len);
            ms.Write(xmpData, i, len);
        }
        ms.WriteByte(0);                                            // 子块终止
        ms.WriteByte(0x3B);                                         // trailer
        return ms.ToArray();
    }
}

public static class OoxmlBuilder
{
    public static byte[] Build(string aigcJson)
    {
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/custom-properties\" " +
            "xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">" +
            "<property fmtid=\"{D5CDD505-2E9C-101B-9397-08002B2CF9AE}\" pid=\"2\" name=\"AIGC\">" +
            "<vt:lpwstr>" + aigcJson.Replace("&", "&amp;").Replace("\"", "&quot;") + "</vt:lpwstr>" +
            "</property></Properties>";
        byte[] name = Encoding.ASCII.GetBytes("docProps/custom.xml");
        byte[] data = Encoding.UTF8.GetBytes(xml);
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, 0, 4);      // local header
        ms.Write(new byte[] { 20, 0 }, 0, 2);                       // version
        ms.Write(new byte[] { 0, 0 }, 0, 2);                        // flags
        ms.Write(new byte[] { 0, 0 }, 0, 2);                        // method 0 = stored
        ms.Write(new byte[4], 0, 4);                                // 时间/日期
        ms.Write(new byte[4], 0, 4);                                // crc（解析器不校验）
        ms.Write(Bytes.U32LE((uint)data.Length), 0, 4);                   // csize
        ms.Write(Bytes.U32LE((uint)data.Length), 0, 4);                   // usize
        ms.Write(new byte[] { (byte)name.Length, 0 }, 0, 2);        // fnlen
        ms.Write(new byte[] { 0, 0 }, 0, 2);                        // extra len
        ms.Write(name, 0, name.Length);
        ms.Write(data, 0, data.Length);
        return ms.ToArray();
    }
}

public static class PdfBuilder
{
    public static byte[] Build(string aigcJson)
    {
        string body =
            "%PDF-1.7\n" +
            "1 0 obj<</Producer(CorpusStudio)>>endobj\n" +
            "trailer<</Root 1 0 R/Info 1 0 R>>\n" +
            "/AIGC (" + aigcJson + ")\n" +
            "%%EOF";
        return Encoding.ASCII.GetBytes(body);
    }
}

public static class MdBuilder
{
    public static string FrontMatter(string label, string producer, string produceId)
    {
        return "---\nAIGC:\n  Label: '" + label + "'\n  ContentProducer: '" + producer +
               "'\n  ProduceID: '" + produceId + "'\n---\n\n\u6b63\u6587\u5185\u5bb9\u3002\n";
    }
}
}
