using System.Threading.Tasks;
using AigcTotal.GB45438.Verdict;
using AigcTotal.TestSupport;
using Xunit;

namespace AigcTotal.GB45438.Tests
{
    public class JpegAndAsyncTests
    {
        private const string ValidXmp =
            "<?xml version=\"1.0\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\" xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:RDF><rdf:Description Label=\"1\" ContentProducer=\"TestStudio\" ProduceID=\"P-0001\"/></rdf:RDF>" +
            "</x:xmpmeta>";

        [Fact]
        public void XmpAttributes_FoundAndValidated()
        {
            byte[] jpeg = JpegBuilder.WithXmp(ValidXmp);

            var result = AigcLabelVerifier.Verify(jpeg);

            Assert.Equal(Carriers.CarrierKind.Jpeg, result.Carrier);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            var site = Assert.Single(result.Sites);
            Assert.Equal(Carriers.PayloadEncoding.XmpAigc, site.Encoding);
            Assert.Equal("1", site.Fields!["Label"]);
            Assert.Equal("P-0001", site.Fields["ProduceID"]);
        }

        [Fact]
        public void XmpElements_FoundAndValidated()
        {
            string xmp =
                "<?xml version=\"1.0\"?><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                "<rdf:Description><Label>2</Label><ContentProducer>MetaStudio</ContentProducer>" +
                "<ProduceID>J-42</ProduceID></rdf:Description></rdf:RDF>";
            byte[] jpeg = JpegBuilder.WithXmp(xmp);

            var result = AigcLabelVerifier.Verify(jpeg);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal("MetaStudio", result.Sites[0].Fields!["ContentProducer"]);
            Assert.Equal("2", result.Sites[0].Fields!["Label"]);
        }

        [Fact]
        public void NoXmp_IsNotFound()
        {
            byte[] jpeg = JpegBuilder.WithoutXmp();

            var result = AigcLabelVerifier.Verify(jpeg);

            Assert.Equal(VerdictKind.NotFound, result.Verdict);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.JpegApp1Xmp && c.Outcome == CheckOutcome.Skip);
        }

        [Fact]
        public void MalformedXmp_PayloadError_Inconclusive()
        {
            byte[] jpeg = JpegBuilder.WithXmp("<not-xml");

            var result = AigcLabelVerifier.Verify(jpeg);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Checks, c =>
                c.Check == CheckIds.AnnexeFields && c.Outcome == CheckOutcome.Error
                && c.Code == CheckCodes.PayloadMalformed);
        }

        [Fact]
        public async Task VerifyAsync_Stream_MatchesSync()
        {
            byte[] jpeg = JpegBuilder.WithXmp(ValidXmp);
            using (var stream = new System.IO.MemoryStream(jpeg))
            {
                var syncResult = AigcLabelVerifier.Verify(jpeg);
                var asyncResult = await AigcLabelVerifier.VerifyAsync(stream);

                Assert.Equal(syncResult.Verdict, asyncResult.Verdict);
                Assert.Equal(syncResult.Sites.Count, asyncResult.Sites.Count);
            }
        }

        [Fact]
        public async Task VerifyAsync_CancellationPropagates()
        {
            byte[] png = PngBuilder.Build(
                PngBuilder.Text("AIGC", "{\"Label\":\"1\",\"ContentProducer\":\"a\",\"ProduceID\":\"b\"}"),
                PngBuilder.Data("IEND", System.Array.Empty<byte>()));
            using (var stream = new System.IO.MemoryStream(png))
            using (var cts = new System.Threading.CancellationTokenSource())
            {
                cts.Cancel();
                await Assert.ThrowsAsync<System.OperationCanceledException>(
                    () => AigcLabelVerifier.VerifyAsync(stream, null, cts.Token));
            }
        }

        // —— EXIF UserComment 通道（TC260-PG-20259A 附录 B 包裹形态）——

        [Fact]
        public void Jpeg_ExifUserComment_Compliant()
        {
            const string payload =
                "{\"AIGC\":{\"Label\":\"1\",\"ContentProducer\":\"ExifStudio\",\"ProduceID\":\"E-1\"," +
                "\"ReservedCode1\":\"\",\"ContentPropagator\":\"\",\"PropagateID\":\"\",\"ReservedCode2\":\"\"}}";
            byte[] jpeg = JpegBuilder.WithExifUserComment(payload);

            var result = AigcLabelVerifier.Verify(jpeg);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            var site = Assert.Single(result.Sites);
            Assert.Equal(Carriers.PayloadEncoding.Json, site.Encoding);
            Assert.Equal("ExifStudio", site.Fields!["ContentProducer"]);
            Assert.Equal("Exif", Assert.IsType<string>(site.Location.Path[2]));
            Assert.Equal("UserComment", Assert.IsType<string>(site.Location.Path[3]));
            Assert.Contains(result.Checks, c => c.Check == CheckIds.JpegExifUserComment && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Jpeg_ExifUserComment_PlainTextComment_Ignored_NotFound()
        {
            // 普通照片的 UserComment 备注满地都是：非 AIGC 形态必须静默忽略（不出站点、不降档）
            byte[] jpeg = JpegBuilder.WithExifUserComment(" 我的生活照 ");

            var result = AigcLabelVerifier.Verify(jpeg);

            Assert.Equal(VerdictKind.NotFound, result.Verdict);
            Assert.Empty(result.Sites);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.JpegExifUserComment && c.Outcome == CheckOutcome.Skip);
        }

        [Fact]
        public void Jpeg_ExifUserComment_MalformedJson_Inconclusive()
        {
            // 看起来是标识负载（{"AIGC"… 开头）但 JSON 畸形：通道在、读不出 → 无法判定（防假性 not_found）
            byte[] jpeg = JpegBuilder.WithExifUserComment("{\"AIGC\":{\"Label\":");

            var result = AigcLabelVerifier.Verify(jpeg);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.JpegExifUserComment && c.Outcome == CheckOutcome.Pass);
            Assert.Contains(result.Checks, c =>
                c.Check == CheckIds.AnnexeFields && c.Outcome == CheckOutcome.Error);
        }
    }
}
