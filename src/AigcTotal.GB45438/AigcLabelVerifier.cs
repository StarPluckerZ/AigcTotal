using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AigcTotal.GB45438.Carriers;
using AigcTotal.GB45438.Carriers.Detection;
using AigcTotal.GB45438.Carriers.Parsing;
using AigcTotal.GB45438.Carriers.Parsing.Jpeg;
using AigcTotal.GB45438.Carriers.Parsing.Avi;
using AigcTotal.GB45438.Carriers.Parsing.Flac;
using AigcTotal.GB45438.Carriers.Parsing.Gif;
using AigcTotal.GB45438.Carriers.Parsing.Mp3;
using AigcTotal.GB45438.Carriers.Parsing.Mp4;
using AigcTotal.GB45438.Carriers.Parsing.Ogg;
using AigcTotal.GB45438.Carriers.Parsing.Ooxml;
using AigcTotal.GB45438.Carriers.Parsing.Pdf;
using AigcTotal.GB45438.Carriers.Parsing.Png;
using AigcTotal.GB45438.Carriers.Parsing.Text;
using AigcTotal.GB45438.Carriers.Parsing.Tiff;
using AigcTotal.GB45438.Carriers.Parsing.Wav;
using AigcTotal.GB45438.Carriers.Parsing.Webp;
using AigcTotal.GB45438.IO;
using AigcTotal.GB45438.Schema;
using AigcTotal.GB45438.Verdict;

namespace AigcTotal.GB45438
{
    /// <summary>
    /// 验证门面：探测 → 容器扫描 → 负载解码 → 附录 E 校验 → 聚合判定。
    /// 同版本 + 同输入字节 → 逐字段一致。同步为核心；VerifyAsync 为 Web 宿主门面（线程池 + 取消贯穿）。
    /// </summary>
    public static class AigcLabelVerifier
    {
        private static readonly Dictionary<CarrierKind, ICarrierParser> Parsers = CreateParsers();
        private static readonly Dictionary<PayloadEncoding, IPayloadDecoder> Decoders = CreateDecoders();

        // —— 同步 API：CLI / WASM / 控制台 ——

        public static VerificationResult Verify(Stream input, VerifyOptions? options = null)
            => VerifyCore(input, options, CancellationToken.None);

        public static VerificationResult Verify(byte[] input, VerifyOptions? options = null)
            => VerifyCore(new MemoryStream(input), options, CancellationToken.None);

        public static VerificationResult Verify(ReadOnlySpan<byte> input, VerifyOptions? options = null)
            => Verify(input.ToArray(), options);

        // —— 异步 API：Web 宿主 ——

        public static Task<VerificationResult> VerifyAsync(Stream input, VerifyOptions? options = null,
            CancellationToken ct = default)
            => Task.Run(() => VerifyCore(input, options, ct), CancellationToken.None);

        public static Task<VerificationResult> VerifyAsync(byte[] input, VerifyOptions? options = null,
            CancellationToken ct = default)
            => Task.Run(() => VerifyCore(new MemoryStream(input), options, ct), CancellationToken.None);

        private static Dictionary<CarrierKind, ICarrierParser> CreateParsers()
        {
            return new Dictionary<CarrierKind, ICarrierParser>
            {
                [CarrierKind.Png] = new PngParser(),
                [CarrierKind.Jpeg] = new JpegParser(),
                [CarrierKind.Mp4] = new Mp4Parser(),
                [CarrierKind.Wav] = new WavParser(),
                [CarrierKind.Mp3] = new Mp3Parser(),
                [CarrierKind.Text] = new TextParser(),
                [CarrierKind.Flac] = new FlacParser(),
                [CarrierKind.Ogg] = new OggParser(),
                [CarrierKind.Avi] = new AviParser(),
                [CarrierKind.Webp] = new WebpParser(),
                [CarrierKind.Tiff] = new TiffParser(),
                [CarrierKind.Gif] = new GifParser(),
                [CarrierKind.Ooxml] = new OoxmlParser(),
                [CarrierKind.Pdf] = new PdfParser(),
            };
        }

        private static Dictionary<PayloadEncoding, IPayloadDecoder> CreateDecoders()
        {
            return new Dictionary<PayloadEncoding, IPayloadDecoder>
            {
                [PayloadEncoding.Json] = new JsonPayloadDecoder(),
                [PayloadEncoding.XmpAigc] = new XmpPayloadDecoder(),
                [PayloadEncoding.FrontMatterYaml] = new FrontMatterDecoder(),
            };
        }

        private static VerificationResult VerifyCore(Stream stream, VerifyOptions? options, CancellationToken ct)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            var limits = options?.Security.Clone() ?? new SecurityLimits();
            var reader = new BoundedReader(stream, limits);
            reader.Seek(0);

            var checks = new List<CheckResult>();
            var signals = new List<ForensicSignal>();
            List<LabelSite> sites = new List<LabelSite>();

            // ① 探测
            CarrierKind kind;
            bool ambiguous;
            try
            {
                kind = CarrierDetectorRegistry.Detect(reader, out ambiguous);
            }
            catch (CarrierStructureException ex)
            {
                checks.Add(new CheckResult(CheckIds.CarrierDetect, CheckOutcome.Error, code: ex.Code));
                return Build(kind: null, limits, sites, signals, checks);
            }
            catch (CarrierLimitException ex)
            {
                checks.Add(new CheckResult(CheckIds.CarrierDetect, CheckOutcome.Error,
                    code: CheckCodes.ResourceLimit, detail: ex.Kind.ToString()));
                return Build(null, limits, sites, signals, checks);
            }

            if (ambiguous)
            {
                signals.Add(new ForensicSignal(SignalKind.CarrierAmbiguous, null, "multiple detectors matched"));
                checks.Add(new CheckResult(CheckIds.CarrierDetect, CheckOutcome.Error,
                    code: CheckCodes.AmbiguousFormat, detail: "polyglot input"));
                return Build(kind, limits, sites, signals, checks);
            }
            // 文本为兜底载体：carrier_detect 由 TextParser 自报（含二进制误探测的 error 路径）
            if (kind != CarrierKind.Text)
            {
                checks.Add(new CheckResult(CheckIds.CarrierDetect, CheckOutcome.Pass));
            }

            // ② 容器扫描
            if (!Parsers.TryGetValue(kind, out ICarrierParser? parser))
            {
                checks.Add(new CheckResult(CheckIds.CarrierSupport, CheckOutcome.Error,
                    code: CheckCodes.CarrierUnsupported,
                    detail: $"carrier '{CarrierNames.ToToken(kind)}' not implemented yet"));
                return Build(kind, limits, sites, signals, checks);
            }

            CarrierScan scan;
            try
            {
                scan = parser.Scan(reader, options ?? new VerifyOptions(), ct);
            }
            catch (CarrierStructureException ex)
            {
                checks.Add(new CheckResult(CheckIds.ContainerStructure, CheckOutcome.Error, code: ex.Code));
                return Build(kind, limits, sites, signals, checks);
            }
            catch (CarrierLimitException ex)
            {
                signals.Add(new ForensicSignal(SignalKind.ResourceLimitExceeded, null, ex.Kind.ToString()));
                checks.Add(new CheckResult(CheckIds.ContainerStructure, CheckOutcome.Error,
                    code: CheckCodes.ResourceLimit, detail: ex.Kind.ToString()));
                return Build(kind, limits, sites, signals, checks);
            }

            sites = new List<LabelSite>(scan.Sites);
            signals.AddRange(scan.Signals);
            checks.AddRange(scan.Checks);

            // ③ 负载解码（PromptPattern 为模式匹配站点，无七字段负载，跳过解码与 schema 校验）
            for (int i = 0; i < sites.Count; i++)
            {
                LabelSite site = sites[i];
                if (site.Encoding == PayloadEncoding.PromptPattern)
                {
                    continue;
                }
                if (Decoders.TryGetValue(site.Encoding, out IPayloadDecoder? decoder))
                {
                    PayloadDecodeResult decoded = decoder.Decode(site.RawPayload);
                    site.Fields = decoded.Fields;
                    var errors = new List<string>();
                    if (decoded.ErrorCode != null) errors.Add(decoded.ErrorCode);
                    site.DecodeErrors = errors;
                }
                else
                {
                    site.Fields = null;
                    site.DecodeErrors = new List<string> { CheckCodes.CarrierUnsupported };
                }
            }

            // ④ 附录 E 逐站点校验
            for (int i = 0; i < sites.Count; i++)
            {
                if (sites[i].Encoding == PayloadEncoding.PromptPattern)
                {
                    continue;
                }
                checks.AddRange(AnnexEValidator.Validate(sites[i], i));
            }

            // ⑤ 文件级 + 取证 + 总判定
            checks.AddRange(VerdictComposer.FileLevelChecks(sites));
            checks.AddRange(VerdictComposer.ChecksFromSignals(signals));

            return Build(kind, limits, sites, signals, checks, scan.CarrierDetail);
        }

        private static VerificationResult Build(CarrierKind? kind, SecurityLimits limits,
            List<LabelSite> sites, List<ForensicSignal> signals, List<CheckResult> checks,
            string? carrierDetail = null)
        {
            VerdictKind verdict = VerdictComposer.Compose(checks, sites);
            return new VerificationResult(kind, carrierDetail, limits, sites, signals, checks, verdict);
        }
    }
}
