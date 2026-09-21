using System.IO;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Verdict;
using Xunit;

namespace AigcTotal.GB45438.Tests
{
    public class BoundedReaderTests
    {
        private static MemoryStream StreamOf(byte count)
        {
            var data = new byte[count];
            for (int i = 0; i < count; i++) data[i] = (byte)(i & 0xFF);
            return new MemoryStream(data);
        }

        [Fact]
        public void TotalReadLimit_TriggersLimitException()
        {
            var limits = new SecurityLimits { MaxTotalRead = 10 };
            using (var stream = StreamOf(64))
            {
                var reader = new BoundedReader(stream, limits);
                Assert.Throws<CarrierLimitException>(() => reader.ReadAtMost(11));
            }
        }

        [Fact]
        public void AllocLimit_RejectsDeclaredLength()
        {
            var limits = new SecurityLimits { MaxAlloc = 16 };
            using (var stream = StreamOf(64))
            {
                var reader = new BoundedReader(stream, limits);
                var ex = Assert.Throws<CarrierLimitException>(() => reader.ReadAtMost(17));
                Assert.Equal(LimitKind.Alloc, ex.Kind);
            }
        }

        [Fact]
        public void ReadExactly_OnEof_ThrowsStructureTruncated()
        {
            using (var stream = StreamOf(4))
            {
                var reader = new BoundedReader(stream, new SecurityLimits());
                var ex = Assert.Throws<CarrierStructureException>(() => reader.ReadExactly(8, "test"));
                Assert.Equal(CheckCodes.StructureTruncated, ex.Code);
            }
        }

        [Fact]
        public void StructureCount_LimitTriggers()
        {
            var limits = new SecurityLimits { MaxStructures = 3 };
            using (var stream = StreamOf(64))
            {
                var reader = new BoundedReader(stream, limits);
                reader.CountStructure();
                reader.CountStructure();
                reader.CountStructure();
                Assert.Throws<CarrierLimitException>(reader.CountStructure);
            }
        }

        [Fact]
        public void Depth_LimitTriggersAndExitRecovers()
        {
            var limits = new SecurityLimits { MaxDepth = 2 };
            using (var stream = StreamOf(8))
            {
                var reader = new BoundedReader(stream, limits);
                reader.EnterScope();
                reader.EnterScope();
                Assert.Throws<CarrierLimitException>(reader.EnterScope);
                reader.ExitScope();
                reader.EnterScope(); // 回落后再进一层合法
            }
        }

        [Fact]
        public void BigEndian_ReadsCorrectly()
        {
            using (var stream = new MemoryStream(new byte[] { 0x12, 0x34, 0x56, 0x78 }))
            {
                var reader = new BoundedReader(stream, new SecurityLimits());
                Assert.Equal(0x12345678u, reader.ReadUInt32BE("u32"));
            }
        }
    }
}
