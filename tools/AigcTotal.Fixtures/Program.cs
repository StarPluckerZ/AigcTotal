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
            if (args.Length == 3 && args[0] == "golden")
            {
                return GenerateGoldenReports(args[1], args[2], Console.Out, Console.Error);
            }
            if (args.Length == 1)
            {
                return GenerateCorpus(args[0], Console.Out, Console.Error);
            }
            Console.Error.WriteLine("usage: aigc-fixtures <outDir> | aigc-fixtures report <corpusFile> | aigc-fixtures golden <corpusDir> <outDir>");
            return 1;
        }

        /// <summary>
        /// 对语料目录内全部文件（manifest.txt 除外）批量产出 golden 报告 → outDir/&lt;name&gt;.report.json。
        /// golden 与实时生成逐字节一致是 schema 冻结的物理执行器；schema 变更必须走讨论（见 CONTRIBUTING）。
        /// </summary>
        private static int GenerateGoldenReports(string corpusDir, string outDir, TextWriter stdout, TextWriter stderr)
        {
            if (!Directory.Exists(corpusDir))
            {
                stderr.WriteLine($"error: corpus dir not found: {corpusDir}");
                return 2;
            }
            Directory.CreateDirectory(outDir);
            int written = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(corpusDir))
                {
                    string name = Path.GetFileName(file);
                    if (name == "manifest.txt") continue;

                    byte[] bytes = File.ReadAllBytes(file);
                    string inputHash = "sha256:" + Hash(bytes);
                    VerificationResult result = AigcLabelVerifier.Verify(bytes);
                    ReportEnvelope envelope = AigcReportBuilder.Build(
                        result, inputHash, bytes.Length, "aigc-fixtures", "0.1.0",
                        reportId: GoldenReportId, timestampUtc: GoldenTimestamp);

                    string destination = Path.Combine(outDir, name + ".report.json");
                    File.WriteAllText(destination, envelope.CanonicalJson,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    stdout.WriteLine($"golden {name} -> {VerdictTokens.ToToken(result.Verdict)}");
                    written++;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                stderr.WriteLine($"error: {ex.Message}");
                return 2;
            }
            stdout.WriteLine($"golden reports: {written} files -> {outDir}");
            return 0;
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
            // method=8（deflate）形态：真实 docx 常态，fuzz 种子触达 ZIP 解压路径
            yield return ("ooxml-custom-aigc-deflate.docx", OoxmlBuilder.BuildDeflate(validJson));
            // 流式写出形态（bit3 描述符 + 完整中央目录）：fuzz 种子触达中央目录主路径
            yield return ("ooxml-custom-aigc-streaming.docx", OoxmlBuilder.BuildStreaming(validJson));
            yield return ("pdf-info-aigc.pdf", PdfBuilder.Build(validJson));
            yield return ("md-front-matter.md", Encoding.UTF8.GetBytes(MdBuilder.FrontMatter("1", "CorpusStudio", "C-002")));

            // TC260-PG-20259A 附录 B 形态：tEXt 负载为 {"AIGC":{七字段}} 包裹
            yield return ("png-text-wrapped.png", PngBuilder.Build(
                PngBuilder.Text("AIGC", "{\"AIGC\":" + validJson + "}"),
                PngBuilder.Data("IEND", Array.Empty<byte>())));

            // zTXt 通道（zlib 压缩负载，Zlib.Stored 确定性构造）
            yield return ("png-ztxt-aigc.png", PngBuilder.Build(
                PngBuilder.Ztxt("AIGC", validJson),
                PngBuilder.Data("IEND", Array.Empty<byte>())));

            // JPEG EXIF UserComment 通道（TC260-PG-20259A 附录 B 包裹形态）
            yield return ("jpeg-exif-usercomment.jpg", JpegBuilder.WithExifUserComment(
                "{\"AIGC\":" + validJson + "}"));

            yield return ("mp4-uuid-xmp.mp4", Mp4Builder.Build(
                Mp4Builder.Ftyp("isom"),
                Mp4Builder.UuidXmp(validXmp)));

            yield return ("wav-aigc.wav", WavBuilder.Build(WavBuilder.Fmt(), WavBuilder.Aigc(validJson)));

            yield return ("mp3-txxx-aigc.mp3", Id3Builder.V24(Id3Builder.TxxxFrame("AIGC", validJson)));

            yield return ("article-ai.txt", Encoding.UTF8.GetBytes("本内容由人工智能生成。\n这是一篇AI生成的正文。"));
            yield return ("article-plain.txt", Encoding.UTF8.GetBytes("这是一篇普通文章，没有任何提示语。"));

            // —— golden 扩面（2026-09-23）：schema 失败码、not_found 载体变体、取证信号、显式后缀、探测歧义 ——
            const string enumInvalidJson =
                "{\"Label\":\"5\",\"ContentProducer\":\"CorpusStudio\",\"ProduceID\":\"C-101\"," +
                "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}";
            const string unknownFieldJson =
                "{\"Label\":\"1\",\"ContentProducer\":\"CorpusStudio\",\"ProduceID\":\"C-102\",\"Extra\":\"x\"," +
                "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}";
            const string disagreeJsonA =
                "{\"Label\":\"1\",\"ContentProducer\":\"StudioA\",\"ProduceID\":\"C-103\"," +
                "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}";
            const string disagreeJsonB =
                "{\"Label\":\"2\",\"ContentProducer\":\"StudioB\",\"ProduceID\":\"C-103\"," +
                "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}";

            // noncompliant：附录 E 逐项失败码各有独立语料
            yield return ("png-label-charset-violation.png", PngBuilder.Build(
                PngBuilder.Text("AIGC",
                    "{\"Label\":\"1\",\"ContentProducer\":\"中文平台\",\"ProduceID\":\"C-104\"," +
                    "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}"),
                PngBuilder.Data("IEND", Array.Empty<byte>())));
            yield return ("png-label-invalid-enum.png", PngBuilder.Build(
                PngBuilder.Text("AIGC", enumInvalidJson),
                PngBuilder.Data("IEND", Array.Empty<byte>())));
            yield return ("png-label-unknown-field.png", PngBuilder.Build(
                PngBuilder.Text("AIGC", unknownFieldJson),
                PngBuilder.Data("IEND", Array.Empty<byte>())));
            yield return ("png-label-disagree.png", PngBuilder.Build(
                PngBuilder.Text("AIGC", disagreeJsonA),
                PngBuilder.Text("AIGC", disagreeJsonB),
                PngBuilder.Data("IEND", Array.Empty<byte>())));

            // inconclusive：空壳（metadata_shell_empty）与 CRC 失配（warn，判定随字段）
            yield return ("png-text-empty-shell.png", PngBuilder.Build(
                PngBuilder.Text("AIGC", ""),
                PngBuilder.Data("IEND", Array.Empty<byte>())));
            yield return ("png-crc-corrupt.png", PngBuilder.Build(
                PngBuilder.Data("tEXt", Encoding.ASCII.GetBytes("AIGC\0" + validJson), corruptCrc: true),
                PngBuilder.Data("IEND", Array.Empty<byte>())));

            // not_found：各载体的无标识变体
            yield return ("wav-no-label.wav", WavBuilder.Build(WavBuilder.Fmt()));
            yield return ("mp4-no-label.mp4", Mp4Builder.Build(Mp4Builder.Ftyp("isom"), Mp4Builder.Mdat(32)));
            yield return ("mp3-no-label.mp3", Id3Builder.V24(Id3Builder.TxxxFrame("Comment", "nothing")));
            yield return ("flac-no-label.flac", FlacBuilder.Build(validJson, withAigcPrefix: false));
            yield return ("pdf-no-label.pdf", Encoding.ASCII.GetBytes(
                "%PDF-1.7\n1 0 obj<</Producer(CorpusStudio)>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF"));
            yield return ("ooxml-no-label.docx", OoxmlBuilder.BuildWithoutCustom());

            // 显式标识：仅后缀站点（前缀窗口无 AI 要素）
            yield return ("article-ai-suffix.txt", Encoding.UTF8.GetBytes(
                "这是一篇普通的文章，讲述一个漫长的故事，内容平实，没有使用任何特殊技术，也没有任何特殊的标记或者提示语，只是用来撑长开头窗口的一段正文。\n文末提示：本内容由AI生成。"));

            // 探测歧义（polyglot：JPEG 魔数 + ftyp 双命中）→ inconclusive
            yield return ("polyglot-ambiguous.bin", new byte[]
            {
                0xFF, 0xD8, 0xFF, 0xE0, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 0x00, 0x00, 0x01, 0x02,
            });

            // —— fuzz 回归种子（SharpFuzz 崩溃输入原字节内嵌，确定性重放）——
            // 基于自造语料 gif-xmp.gif 的覆盖率引导变异产物，均为 GIF/GIF 双结构交错输入：
            // gif-xmp-eof-truncated.gif：XMP 子块链在 EOF 截断 → GifParser 子块循环越界（已修，转回归）
            yield return ("gif-xmp-eof-truncated.gif", Convert.FromBase64String(
                "R0lGODlhAQABAAAAACH/C1hNUCBEYXRh7Bvf3Tw/eG1sIHZlcnNpb249IjEuMCI/Pjx4OnhtcG1l" +
                "dGEgeG1sbnM6eD0iYWRvYmU6bnM6bWV0YS8iIHhtbG5zOnJkZj0iaHR0cDovL3d3dy53My5vcmcv" +
                "MTk5OS8wMi8yMi1yZGYtc3ludGF4LW5zIyI+PHJkZjpSREY+PHJkZjpEZXNjcmlwdGlvbiBvbnRl" +
                "bnRQcm9kdWNlcj0iQ29ycHVzU3R1ZGlv//////8PAAAiIFByb2R1Y2VJRD0iQy0wMDEiLz48L3Jk" +
                "ZjpSREY+PC94OnhtcDBtZXRhPgA7"));
            // fuzz-found-input.bin：GIF 变异体（结构交错 + 声明长度炸弹），验证 GIF 子块/描述符跳过路径
            yield return ("fuzz-found-input.bin", Convert.FromBase64String(
                "R0lGODlhAQABAAAAACH/C1hNUCBEYXRh7Bvf3Tw/eG1sIHZlcnNpb249IjEuMCI/Pjxh7Bvf3Tw/" +
                "eG1sIHZlcnNpb249IjEuMCI/Pjx4OnhtcG1ldGEgeG1sbnM6eD0iYWRvYmU6bnM6bWV0YS8iIHht" +
                "bG5zOnJkZj0iaHR0cDovL3d3dy53My5vcmcvMTk5Of//////DwAALzAyLzIyLXJkZi1zeW50YXgt" +
                "bnMjIj48cmRmOlJERj48cmRmOkRlc/////9jcmlwdGlHSUY5MGEBAAEAAAAAIf8LWE1QIER0AAAA" +
                "qAAAAAEAAACgZGF0YQAAAAEAAAAAeyJMYWJlbCI6IjEiLCJDb250ZW50UHJvZHVjZXIiOiJDcG1l" +
                "dGEgeG1sbm9uIExhYmVsPSIxIiBDb250ZW50UHJvZHVjZXI9IkNvcnB1c1N0dWRpbyIgUHJvczp4" +
                "PSJhZG9iZTpuczptZXRhLyIgeG1sbnM6ciRkdWNlSUQ9IkMtMDAxIi8+PC9yZj0iaHR0cDovL3d3" +
                "dy53My5vcmcvMTk5OS8wMi8yMi1yb2R1Y2VJRERERERERERERERERERERERERERERERERERERERE" +
                "RERERHJnLzE5OTkvMDIvMjItcmRmLXN5bnRheH5ucyMiPjxyZEMtMDAxIi8+PC9yZj0iaHR0cDov" +
                "L3d3dy53My5vcmcvMTk5OS8wMi8yMi1yb2R1Y2VJRERERERERERERERERERERERERERERERERERE" +
                "RERERERERHJnLzE5OTkvMDIvMjItcmRmLXN5bnRheH5ucyMiPjxyZGY6UkRGPjxyZGY6RGVzY3Jp" +
                "cHRpb24gTGFiZWw9IjEiIENvbnRlbnRQcm9kZjpSREY+PC94OnhtcG1ldGE+ADtkdWNlcj0iQ29y" +
                "cHVzU3R1ZGlvIiBQcm9kdWNlSUQ9IkMtMDAxIi8+PC9yZGY6eDp4bXBtZXRhIHhtbG5zOng9ImFk" +
                "b2JlOm5z5zptZXRhLyIgeG1sbnM6cmRmPSJodHRwOi8vd3d3LnczLm9yZy8xOTlkZi1zeW50YXh+" +
                "bnMjIj48cmRmOkRlc2NyaXB0aW9uIExhYmVsPSIxIiBDb250ZW50UHJvZGY6UkRGPjwveDp4bXBt" +
                "ZXRhPgA7ZHVjZXI9IkNvcnB1c1N0dWRpbyIgUHJvZHVjZUlEPSJDLTAwMSIvPjwvcmRmOng6eG1w" +
                "bWV0YSB4bWxuczp4PSJhZG9iZTpuczptZXRhLyIgeG1sbnM6cmRmPSJodHRwOi8vd3d3LnczLm9y" +
                "Zy8xOTk5//////8PAAAvMDIvMjItcmRmLXN5bnRheC1ucyMiPjxyZGY6UkRGPjxyZGY6RGVz////" +
                "/2NyaXB0aUdJRjkwZjpSREY+PHJkZjpEZXNjcmlwdGlvbiBMYWJlbD0iMSIgQ29udGVudFByb2Rm" +
                "OlJERj48L3g6eG1wbWV0YT4AO2R1Y2VyPSJDb3JwdXNTdHVkaW8iIFByb2R1Y2VJRD0iQy0wMDEi" +
                "Lz48L3JkZjp4OnhtcG1ldGEgeG1sbnM6eD0iYWRvYmU6bnPnOm1ldGEvIiB4bWxuczpyZGY9Imh0" +
                "dHA6Ly93d3cudzMub3JnLzE5OWRmLXN5bnRheH5ucyMiPjxyZGY6RGVzY3JpcHRpb24gTGFiZWw9" +
                "IjEiIENvbnRlbnRQcm9kZjpSREY+PC94OnhtcG1ldGE+ADtkdWNlcj0iQ29ycHVzU3R1ZGlvIiBQ" +
                "cm9kdWNlSUQ9IkMtMDAxIi8+PC9yZGY6eDp4bXBtZXRhIHhtbG5zOng9ImFkb2JlOm5zOm1ldGEv" +
                "IiB4bWxuczpyZGY9Imh0dHA6Ly93d3cudzMub3JnLzE5OTn//////w8AAC8wMi8yMi1yZGYtc3lu" +
                "dGF4LW5zIyI+PHJkZjpSREY+PHJkZjpEZXP/////Y3JpcHRpR0lGOTBhAQABAAAAACH/C1hNUCBE" +
                "dAAAAKgAAAABAAAAoGRhdGEAAAABAAAAAHsiTGFiZWwiOiIxIiwiQ29udGVudFByb2R1Y2VyIjo9" +
                "IkNvcnB1c1N0dWRpbyIsIlByb2R1Y2VJRERERERERERERERERERERERERERERERERERERERERERE" +
                "RHJnLzE5OTkvMDIvMjItcmRmLXN5bnRheH5ucyMiPjxyZGY6UkRGPjxyZGY6RGVzY3JpcHRpb24g" +
                "TGFiZWw9IjEiIENvbnRlbnRQcm9kZjpSREY+PC94OnhtcG1ldGE+ADtkdWNlcj0iQ29ycHVzU3R1" +
                "ZGlvIiBQcm9kdWNlSUQ9IkMtMDAxIi8+PC9yZGY6UkRGPjwveDp4bXBtZXRhPgA7"));
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
