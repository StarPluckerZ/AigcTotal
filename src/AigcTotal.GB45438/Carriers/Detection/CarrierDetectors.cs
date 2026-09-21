using System;
using System.Collections.Generic;
using System.Text;
using AigcTotal.GB45438.Carriers;

namespace AigcTotal.GB45438.Carriers.Detection
{
    /// <summary>魔数探测器：无状态纯函数，按声明的前缀字节判定。</summary>
    public interface ICarrierDetector
    {
        CarrierKind Kind { get; }

        /// <summary>判定所需的前缀字节数。</summary>
        int HeaderLength { get; }

        bool Matches(byte[] header, int length);
    }

    public sealed class PngDetector : ICarrierDetector
    {
        private static readonly byte[] Signature =
            { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        public CarrierKind Kind => CarrierKind.Png;
        public int HeaderLength => 8;

        public bool Matches(byte[] header, int length)
        {
            if (length < HeaderLength) return false;
            for (int i = 0; i < Signature.Length; i++)
            {
                if (header[i] != Signature[i]) return false;
            }
            return true;
        }
    }

    public sealed class JpegDetector : ICarrierDetector
    {
        public CarrierKind Kind => CarrierKind.Jpeg;
        public int HeaderLength => 3;

        public bool Matches(byte[] header, int length) =>
            length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;
    }

    public sealed class Mp4Detector : ICarrierDetector
    {
        // 'ftyp' box：偏移 4 起为 box 类型（覆盖 MP4 与 QuickTime/MOV）
        public CarrierKind Kind => CarrierKind.Mp4;
        public int HeaderLength => 8;

        public bool Matches(byte[] header, int length) =>
            length >= 8
            && header[4] == (byte)'f' && header[5] == (byte)'t'
            && header[6] == (byte)'y' && header[7] == (byte)'p';
    }

    public sealed class WavDetector : ICarrierDetector
    {
        public CarrierKind Kind => CarrierKind.Wav;
        public int HeaderLength => 12;

        public bool Matches(byte[] header, int length) =>
            length >= 12
            && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'A' && header[10] == (byte)'V' && header[11] == (byte)'E';
    }

    public sealed class Mp3Detector : ICarrierDetector
    {
        public CarrierKind Kind => CarrierKind.Mp3;
        public int HeaderLength => 3;

        public bool Matches(byte[] header, int length) =>
            length >= 3 && header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3';
    }

    /// <summary>探测注册表（固定顺序）。文本为兜底：所有二进制探测器未命中时判定 Text。</summary>
    public static class CarrierDetectorRegistry
    {
        public static readonly IReadOnlyList<ICarrierDetector> All = new ICarrierDetector[]
        {
            new PngDetector(),
            new JpegDetector(),
            new Mp4Detector(),
            new WavDetector(),
            new Mp3Detector(),
        };

        private const int MaxHeader = 12;

        /// <summary>读取头部并对全部探测器求值：无命中 → Text（兜底）；多命中 → ambiguous。</summary>
        public static CarrierKind Detect(AigcTotal.GB45438.IO.BoundedReader reader, out bool ambiguous)
        {
            byte[] header = reader.ReadAtMost(Math.Min(MaxHeader, reader.Length));
            var hits = new List<CarrierKind>();
            foreach (var detector in All)
            {
                if (detector.HeaderLength <= header.Length && detector.Matches(header, header.Length))
                {
                    hits.Add(detector.Kind);
                }
            }
            ambiguous = hits.Count > 1;
            if (hits.Count == 1) return hits[0];
            if (hits.Count == 0) return CarrierKind.Text;
            return hits[0]; // 歧义：调用方按 ambiguous 信号降级 inconclusive
        }
    }
}
