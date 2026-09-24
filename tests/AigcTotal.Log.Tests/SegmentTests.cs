using System;
using System.IO;
using System.Linq;
using AigcTotal.Log.Segments;
using Xunit;

namespace AigcTotal.Log.Tests
{
    /// <summary>段文件：追加/滚动（条数与时长双阈值）、序列单调、读往返、崩溃残行容忍、行形态冻结。</summary>
    public class SegmentTests : IDisposable
    {
        private readonly string _dir;

        public SegmentTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "aigc-log-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
            GC.SuppressFinalize(this);
        }

        private static LogEntry Entry(long seq, int minute) => new LogEntry(
            seq,
            new DateTimeOffset(2026, 9, 21, 8, minute, 0, TimeSpan.Zero),
            "sha256:" + new string('1', 64),   // input_sha256（被检文件指纹）
            "sha256:" + new string('0', 64));  // report_sha256

        [Fact]
        public void CanonicalLine_Form_IsFrozen()
        {
            // 行 = 条目的 canonical JSON（键序：input_sha256 < report_sha256 < seq < timestamp）——
            // 进 Merkle 叶的即这串字节；四字段形态 v1 冻结（D1，2026-09-24）
            string line = Entry(42, 30).ToCanonicalLine();
            Assert.Equal(
                "{\"input_sha256\":\"sha256:1111111111111111111111111111111111111111111111111111111111111111\"," +
                "\"report_sha256\":\"sha256:0000000000000000000000000000000000000000000000000000000000000000\"," +
                "\"seq\":42,\"timestamp\":\"2026-09-21T08:30:00Z\"}",
                line);
        }

        [Fact]
        public void Append_CreatesSegment_Roundtrips()
        {
            using var writer = new SegmentWriter(new SegmentWriterOptions { Directory = _dir });
            writer.Append(Entry(1, 0));
            writer.Append(Entry(2, 1));
            Assert.NotNull(writer.CurrentSegmentPath);
            Assert.Equal(2, writer.CurrentEntryCount);

            var result = SegmentReader.Read(writer.CurrentSegmentPath!);
            Assert.Empty(result.Diagnostics);
            Assert.Equal(2, result.Entries.Count);
            Assert.Equal(1, result.Entries[0].Sequence);
            Assert.Equal(2, result.Entries[1].Sequence);
            Assert.Equal(Entry(2, 1).InputSha256, result.Entries[1].InputSha256);
            Assert.Equal(Entry(2, 1).ReportSha256, result.Entries[1].ReportSha256);
            Assert.Equal(new DateTimeOffset(2026, 9, 21, 8, 1, 0, TimeSpan.Zero), result.Entries[1].TimestampUtc);
        }

        [Fact]
        public void Append_NonMonotonicSequence_Rejected()
        {
            using var writer = new SegmentWriter(new SegmentWriterOptions { Directory = _dir });
            writer.Append(Entry(5, 0));
            Assert.Throws<ArgumentException>(() => writer.Append(Entry(5, 1)));
            Assert.Throws<ArgumentException>(() => writer.Append(Entry(4, 1)));
        }

        [Fact]
        public void Append_BadHashFormat_Rejected()
        {
            using var writer = new SegmentWriter(new SegmentWriterOptions { Directory = _dir });
            Assert.Throws<ArgumentException>(() => writer.Append(new LogEntry(
                1, DateTimeOffset.UtcNow, "not-a-hash", "sha256:" + new string('0', 64))));
            Assert.Throws<ArgumentException>(() => writer.Append(new LogEntry(
                1, DateTimeOffset.UtcNow, "sha256:" + new string('1', 64), "sha256:" + new string('g', 64))));
            // input_sha256 同受形态校验（写侧拒绝）
            Assert.Throws<ArgumentException>(() => writer.Append(new LogEntry(
                1, DateTimeOffset.UtcNow, "sha256:" + new string('G', 64), "sha256:" + new string('0', 64))));
        }

        [Fact]
        public void Roll_ByCountThreshold()
        {
            using var writer = new SegmentWriter(new SegmentWriterOptions { Directory = _dir, MaxEntries = 3 });
            for (long i = 1; i <= 4; i++) writer.Append(Entry(i, (int)i));

            // GetFiles 顺序平台相关（Windows 恰为名序，Linux 任意）——必须显式排序
            string[] segments = Directory.GetFiles(_dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.Equal(2, segments.Length);
            Assert.Equal(3, SegmentReader.Read(segments[0]).Entries.Count);
            Assert.Single(SegmentReader.Read(segments[1]).Entries);
        }

        [Fact]
        public void Roll_ByDurationThreshold()
        {
            using var writer = new SegmentWriter(new SegmentWriterOptions
            {
                Directory = _dir,
                MaxEntries = 1000,
                MaxDuration = TimeSpan.FromMinutes(10),
            });
            writer.Append(Entry(1, 0));  // 08:00
            writer.Append(Entry(2, 5));  // 08:05 同段
            writer.Append(Entry(3, 10)); // 08:10 距首条 10 分钟 → 滚动
            writer.Append(Entry(4, 11)); // 新段

            string[] segments = Directory.GetFiles(_dir, "*.jsonl").OrderBy(f => f).ToArray();
            Assert.Equal(2, segments.Length);
            Assert.Equal(2, SegmentReader.Read(segments[0]).Entries.Count);
            Assert.Equal(2, SegmentReader.Read(segments[1]).Entries.Count);
        }

        [Fact]
        public void Read_TrailingPartialLine_Tolerated_WithDiagnostic()
        {
            // 崩溃场景：最后一行只写了一半（无换行符、JSON 不完整）
            string path = Path.Combine(_dir, "seg.jsonl");
            File.WriteAllText(path,
                Entry(1, 0).ToCanonicalLine() + "\n" +
                Entry(2, 1).ToCanonicalLine() + "\n" +
                "{\"report_sha256\":\"sha256:00");

            var result = SegmentReader.Read(path);

            Assert.Equal(2, result.Entries.Count);
            Assert.Single(result.Diagnostics);
        }

        [Fact]
        public void Read_MalformedLine_Skipped_WithDiagnostic()
        {
            string path = Path.Combine(_dir, "seg.jsonl");
            File.WriteAllText(path,
                Entry(1, 0).ToCanonicalLine() + "\n" +
                "not-json\n" +
                Entry(3, 2).ToCanonicalLine() + "\n");

            var result = SegmentReader.Read(path);

            Assert.Equal(2, result.Entries.Count);
            Assert.Single(result.Diagnostics);
        }

        [Fact]
        public void Read_MissingFile_Throws()
        {
            Assert.Throws<FileNotFoundException>(() => SegmentReader.Read(Path.Combine(_dir, "nope.jsonl")));
        }
    }
}
