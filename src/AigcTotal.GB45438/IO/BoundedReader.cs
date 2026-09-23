using System;
using System.IO;

namespace AigcTotal.GB45438.IO
{
    /// <summary>资源上限触发的类别。</summary>
    public enum LimitKind
    {
        TotalRead = 1,
        Alloc = 2,
        Depth = 3,
        Structures = 4,
    }

    /// <summary>BoundedReader 安全边界触发（→ 诊断 resource_limit_exceeded → 无法判定，不崩溃）。</summary>
    public sealed class CarrierLimitException : Exception
    {
        public CarrierLimitException(LimitKind kind, string message)
            : base(message)
        {
            Kind = kind;
        }

        public LimitKind Kind { get; }
    }

    /// <summary>容器结构异常（截断/畸形），由门面转为 container_structure 诊断。</summary>
    public sealed class CarrierStructureException : Exception
    {
        public CarrierStructureException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        /// <summary>CheckCodes.StructureTruncated / CheckCodes.StructureMalformed。</summary>
        public string Code { get; }
    }

    /// <summary>
    /// 唯一合法的读取原语：所有容器解析经由它执行累计读取、单次分配、深度与结构数四类上限。
    /// 非线程安全；每个 Verify 调用一个实例。
    /// </summary>
    public sealed class BoundedReader
    {
        private readonly Stream _stream;
        private readonly SecurityLimits _limits;
        private long _totalRead;
        private int _depth;
        private long _structures;

        public BoundedReader(Stream stream, SecurityLimits limits)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
            if (!stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));
            _stream = stream;
            _limits = limits ?? new SecurityLimits();
        }

        public long Length => _stream.Length;

        public long Position => _stream.Position;

        public long TotalRead => _totalRead;

        /// <summary>移动到文件内绝对偏移。越界（负数或越过文件尾）即结构损坏 → CarrierStructureException，
        /// 由门面消化——所有解析器的 Seek 都经由此处，杜绝越界定位逃出契约。</summary>
        public void Seek(long offset)
        {
            if (offset < 0 || offset > Length)
            {
                throw new CarrierStructureException(Verdict.CheckCodes.StructureTruncated,
                    $"seek to {offset} outside file bounds [0, {Length}]");
            }
            _stream.Seek(offset, SeekOrigin.Begin);
        }

        /// <summary>登记一个容器结构（chunk/box/segment）；超出 MaxStructures 抛 CarrierLimitException。</summary>
        public void CountStructure()
        {
            _structures++;
            if (_structures > _limits.MaxStructures)
            {
                throw new CarrierLimitException(LimitKind.Structures,
                    $"structure count exceeded {_limits.MaxStructures}");
            }
        }

        /// <summary>进入一层嵌套结构；超出 MaxDepth 抛 CarrierLimitException（失败不计入深度）。必须配对调用 ExitScope。</summary>
        public void EnterScope()
        {
            if (_depth + 1 > _limits.MaxDepth)
            {
                throw new CarrierLimitException(LimitKind.Depth, $"nesting depth exceeded {_limits.MaxDepth}");
            }
            _depth++;
        }

        public void ExitScope()
        {
            if (_depth > 0) _depth--;
        }

        /// <summary>读取恰好 count 字节；提前 EOF → CarrierStructureException(truncated)。</summary>
        public byte[] ReadExactly(long count, string context)
        {
            var buffer = ReadAtMost(count);
            if (buffer.Length != count)
            {
                throw new CarrierStructureException(Verdict.CheckCodes.StructureTruncated,
                    $"unexpected EOF in {context}: expected {count} bytes, got {buffer.Length}");
            }
            return buffer;
        }

        /// <summary>读取至多 count 字节（允许 EOF 短读）。</summary>
        public byte[] ReadAtMost(long count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return EmptyBuffer;
            if (count > _limits.MaxAlloc)
            {
                throw new CarrierLimitException(LimitKind.Alloc, $"declared length {count} exceeds MaxAlloc {_limits.MaxAlloc}");
            }
            if (_totalRead + count > _limits.MaxTotalRead)
            {
                throw new CarrierLimitException(LimitKind.TotalRead,
                    $"total read would reach {_totalRead + count} exceeding MaxTotalRead {_limits.MaxTotalRead}");
            }

            var buffer = new byte[count];
            int filled = 0;
            while (filled < buffer.Length)
            {
                int n = _stream.Read(buffer, filled, buffer.Length - filled);
                if (n <= 0) break;
                filled += n;
            }
            _totalRead += filled;
            if (filled == buffer.Length) return buffer;
            var trimmed = new byte[filled];
            Array.Copy(buffer, trimmed, filled);
            return trimmed;
        }

        public ushort ReadUInt16BE(string context)
        {
            var b = ReadExactly(2, context);
            return (ushort)((b[0] << 8) | b[1]);
        }

        public uint ReadUInt32BE(string context)
        {
            var b = ReadExactly(4, context);
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        public ulong ReadUInt64BE(string context)
        {
            var b = ReadExactly(8, context);
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                value = (value << 8) | b[i];
            }
            return value;
        }

        /// <summary>流式块的回调（ReadOnlySpan 是 ref struct，不能用 Action&lt;T&gt; 承载）。</summary>
        public delegate void StreamChunkHandler(ReadOnlySpan<byte> chunk);

        /// <summary>
        /// 流式推进 dataLength 字节并按块回调 chunk（零保留、不计入 MaxTotalRead——该上限约束的是
        /// 解析器保留/分配的字节量，本路径不保留任何字节，由文件实际长度兜底）。
        /// 用于跳过/校验大块不相关数据；声明越过 EOF → StructureTruncated。
        /// </summary>
        public void StreamScan(long dataLength, StreamChunkHandler chunk, string context)
        {
            long fileRemaining = Length - Position;
            if (dataLength > fileRemaining)
            {
                throw new CarrierStructureException(Verdict.CheckCodes.StructureTruncated,
                    $"{context}: declared {dataLength} bytes but only {fileRemaining} remain");
            }
            if (dataLength <= 0) return;
            int bufferSize = (int)Math.Min(64 * 1024, dataLength);
            var buffer = new byte[bufferSize];
            long remaining = dataLength;
            while (remaining > 0)
            {
                int take = (int)Math.Min(buffer.Length, remaining);
                int read = _stream.Read(buffer, 0, take);
                if (read <= 0)
                {
                    throw new CarrierStructureException(Verdict.CheckCodes.StructureTruncated,
                        $"unexpected EOF in {context}");
                }
                chunk(buffer.AsSpan(0, read));
                remaining -= read;
            }
        }

        /// <summary>流式推进 dataLength 字节并返回运行中的 CRC-32 状态（调用方以 Crc32.Finalize 收尾）。</summary>
        public uint StreamCrc(uint runningCrc, long dataLength, string context)
        {
            uint crc = runningCrc;
            StreamScan(dataLength, span => crc = Crc32.Update(crc, span), context);
            return crc;
        }

        public uint ReadUInt32LE(string context)
        {
            var b = ReadExactly(4, context);
            return b[0] | ((uint)b[1] << 8) | ((uint)b[2] << 16) | ((uint)b[3] << 24);
        }

        private static readonly byte[] EmptyBuffer = System.Array.Empty<byte>();
    }

    /// <summary>PNG chunk CRC-32（IEEE 802.3，查表法，纯 BCL 实现；流式接口供大 chunk 跳读校验）。</summary>
    internal static class Crc32
    {
        public const uint Initial = 0xFFFFFFFFu;

        private static readonly uint[] Table = BuildTable();

        public static uint Compute(byte[] bytes, int offset, int count)
        {
            return Finalize(Update(Initial, bytes.AsSpan(offset, count)));
        }

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            return crc;
        }

        public static uint Finalize(uint crc) => ~crc;

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[i] = c;
            }
            return table;
        }
    }
}
