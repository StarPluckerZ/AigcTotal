using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AigcTotal.GB45438;
using AigcTotal.GB45438.Verdict;
using AigcTotal.Report;
using AigcTotal.TestSupport;

namespace AigcTotal.Fixtures
{
    /// <summary>
    /// 自造语料生成器（deterministic）：
    ///   aigc-fixtures &lt;outDir&gt;            生成语料文件 + manifest.json（含 SHA-256，重跑逐字节一致）
    ///   aigc-fixtures report &lt;corpusFile&gt;  对语料文件产出 golden 报告（pin 了 report_id/时间戳/工具版本）
    /// 语料全部程序化构造，不含任何平台版权文件。
    /// </summary>
    public static class Program
    {
        private const string GoldenReportId = "01J8GZ3X9QF7Y3N4R5T6Z8A9BC";
        private static readonly DateTimeOffset GoldenTimestamp =
            DateTimeOffset.Parse("2026-09-21T08:30:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        public static int Main(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            if (args.Length == 2 && args[0] == "report")
            {
                return EmitGoldenReport(args[1], Console.Out, Console.Error);
            }
            if (args.Length == 1)
            {
                return GenerateCorpus(args[0], Console.Out, Console.Error);
            }
            Console.Error.WriteLine("usage: aigc-fixtures <outDir> | aigc-fixtures report <corpusFile>");
            return 1;
        }

        private static int GenerateCorpus(string outDir, TextWriter stdout, TextWriter stderr)
        {
            try
            {
                Directory.CreateDirectory(outDir);
                var manifest = new List<string>();
                foreach ((string name, byte[] bytes) in Corpus())
                {
                    string path = Path.Combine(outDir, name);
                    File.WriteAllBytes(path, bytes);
                    manifest.Add($"{name}  sha256:{Hash(bytes)}  {bytes.Length} bytes");
                    stdout.WriteLine($"wrote {name} ({bytes.Length} bytes)");
                }
                File.WriteAllLines(Path.Combine(outDir, "manifest.txt"), manifest);
                stdout.WriteLine($"manifest: {Path.Combine(outDir, "manifest.txt")}");
                return 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                stderr.WriteLine($"error: {ex.Message}");
                return 2;
            }
        }

        private static int EmitGoldenReport(string corpusFile, TextWriter stdout, TextWriter stderr)
        {
            if (!File.Exists(corpusFile))
            {
                stderr.WriteLine($"error: file not found: {corpusFile}");
                return 2;
            }
            byte[] bytes = File.ReadAllBytes(corpusFile);
            string inputHash = "sha256:" + Hash(bytes);
            VerificationResult result = AigcLabelVerifier.Verify(bytes);
            ReportEnvelope envelope = AigcReportBuilder.Build(
                result, inputHash, bytes.Length, "aigc-fixtures", "0.1.0",
                reportId: GoldenReportId, timestampUtc: GoldenTimestamp);
            stdout.Write(envelope.CanonicalJson);
            stdout.WriteLine();
            return 0;
        }

        private static IEnumerable<(string Name, byte[] Bytes)> Corpus()
        {
            const string validJson =
                "{\"Label\":\"1\",\"ContentProducer\":\"CorpusStudio\",\"ProduceID\":\"C-001\"," +
                "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}";
            const string missingFieldJson =
                "{\"Label\":\"1\",\"ContentProducer\":\"CorpusStudio\"}";
            const string validXmp =
                "<?xml version=\"1.0\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\" xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                "<rdf:RDF><rdf:Description Label=\"1\" ContentProducer=\"CorpusStudio\" ProduceID=\"C-001\"/></rdf:RDF>" +
                "</x:xmpmeta>";

            yield return ("png-label-valid.png", PngBuilder.Build(
                PngBuilder.Text("AIGC", validJson),
                PngBuilder.Data("IEND", Array.Empty<byte>())));

            // 真实世界形态（豆包实测）：XMP 包装 + TC260 命名空间 + <TC260:AIGC> 内嵌附录 E JSON
            const string tc260Xmp =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
                "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                "<rdf:Description rdf:about=\"\" xmlns:TC260=\"http://www.tc260.org.cn/ns/AIGC/1.0/\">" +
                "<TC260:AIGC>{&quot;Label&quot;:&quot;1&quot;,&quot;ContentProducer&quot;:&quot;CorpusStudio&quot;," +
                "&quot;ProduceID&quot;:&quot;C-002&quot;,&quot;ReservedCode1&quot;:&quot;&quot;," +
                "&quot;ContentPropagator&quot;:&quot;&quot;,&quot;PropagateID&quot;:&quot;&quot;," +
                "&quot;ReservedCode2&quot;:&quot;&quot;}</TC260:AIGC>" +
                "</rdf:Description></rdf:RDF></x:xmpmeta>";
            var itxt = new List<byte>();
            itxt.AddRange(Encoding.ASCII.GetBytes("XML:com.adobe.xmp"));
            itxt.Add(0); itxt.Add(0); itxt.Add(0); itxt.Add(0); itxt.Add(0);
            itxt.AddRange(Encoding.UTF8.GetBytes(tc260Xmp));
            yield return ("png-itxt-xmp.png", PngBuilder.Build(
                PngBuilder.Data("iTXt", itxt.ToArray()),
                PngBuilder.Data("IEND", Array.Empty<byte>())));

            yield return ("png-no-label.png", PngBuilder.Build(
                PngBuilder.Text("Comment", "plain image"),
                PngBuilder.Data("IEND", Array.Empty<byte>())));

            yield return ("png-label-missing-field.png", PngBuilder.Build(
                PngBuilder.Text("AIGC", missingFieldJson),
                PngBuilder.Data("IEND", Array.Empty<byte>())));

            yield return ("png-label-duplicate.png", PngBuilder.Build(
                PngBuilder.Text("AIGC", validJson),
                PngBuilder.Text("AIGC", validJson),
                PngBuilder.Data("IEND", Array.Empty<byte>())));

            byte[] truncated = PngBuilder.Build(
                PngBuilder.Text("AIGC", validJson),
                PngBuilder.Data("IEND", Array.Empty<byte>()));
            yield return ("png-truncated.png", truncated[..^4]);

            yield return ("jpeg-xmp-valid.jpg", JpegBuilder.WithXmp(validXmp));
            yield return ("jpeg-no-xmp.jpg", JpegBuilder.WithoutXmp());

            yield return ("mp4-udta-aigc.mp4", Mp4Builder.Build(
                Mp4Builder.Ftyp("isom"),
                Mp4Builder.Container("moov", Mp4Builder.UdtaAigc(validJson)),
                Mp4Builder.Mdat(32)));

            // TC260-PG-20257A 规定形态（ffmpeg use_metadata_tags）：udta/meta/keys + ilst
            yield return ("mp4-udta-meta-aigc.mp4", Mp4Builder.Build(
                Mp4Builder.Ftyp("isom"),
                Mp4Builder.Container("moov", Mp4Builder.UdtaMetaAigc(validJson)),
                Mp4Builder.Mdat(32)));
            yield return ("flac-aigc.flac", FlacBuilder.Build(validJson));
            yield return ("ogg-aigc.ogg", OggBuilder.Build(validJson));
            yield return ("avi-aigc.avi", AviBuilder.Build(validJson));
            yield return ("webp-xmp.webp", WebpBuilder.Build(validXmp));
            yield return ("tiff-xmp.tiff", TiffBuilder.Build(validXmp));
            yield return ("gif-xmp.gif", GifBuilder.Build(validXmp));
            yield return ("ooxml-custom-aigc.docx", OoxmlBuilder.Build(validJson));
            yield return ("pdf-info-aigc.pdf", PdfBuilder.Build(validJson));
            yield return ("md-front-matter.md", Encoding.UTF8.GetBytes(MdBuilder.FrontMatter("1", "CorpusStudio", "C-002")));

            // TC260-PG-20259A 附录 B 形态：tEXt 负载为 {"AIGC":{七字段}} 包裹
            yield return ("png-text-wrapped.png", PngBuilder.Build(
                PngBuilder.Text("AIGC", "{\"AIGC\":" + validJson + "}"),
                PngBuilder.Data("IEND", Array.Empty<byte>())));

            yield return ("mp4-uuid-xmp.mp4", Mp4Builder.Build(
                Mp4Builder.Ftyp("isom"),
                Mp4Builder.UuidXmp(validXmp)));

            yield return ("wav-aigc.wav", WavBuilder.Build(WavBuilder.Fmt(), WavBuilder.Aigc(validJson)));

            yield return ("mp3-txxx-aigc.mp3", Id3Builder.V24(Id3Builder.TxxxFrame("AIGC", validJson)));

            yield return ("article-ai.txt", Encoding.UTF8.GetBytes("本内容由人工智能生成。\n这是一篇AI生成的正文。"));
            yield return ("article-plain.txt", Encoding.UTF8.GetBytes("这是一篇普通文章，没有任何提示语。"));
        }

        private static string Hash(byte[] bytes)
        {
            var sb = new StringBuilder(64);
            foreach (byte b in SHA256.HashData(bytes))
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
