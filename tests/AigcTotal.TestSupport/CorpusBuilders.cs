using System;
using System.Collections.Generic;
using System.IO;
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
}
