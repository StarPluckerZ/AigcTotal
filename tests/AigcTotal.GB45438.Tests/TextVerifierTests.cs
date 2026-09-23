using System.Linq;
using System.Text;
using AigcTotal.GB45438.Verdict;
using Xunit;

namespace AigcTotal.GB45438.Tests
{
    public class TextVerifierTests
    {
        [Fact]
        public void Text_WithPrefixPrompt_Compliant()
        {
            byte[] text = Encoding.UTF8.GetBytes("本内容由人工智能生成。\n这是一段正文内容。");

            var result = AigcLabelVerifier.Verify(text);

            Assert.Equal(Carriers.CarrierKind.Text, result.Carrier);
            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.TextExplicitPrefix && c.Outcome == CheckOutcome.Pass);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.TextPromptAffix && c.Outcome == CheckOutcome.Pass);
            Assert.Equal(Carriers.PayloadEncoding.PromptPattern, result.Sites[0].Encoding);
        }

        [Fact]
        public void Text_WithSuffixPrompt_Compliant()
        {
            byte[] text = Encoding.UTF8.GetBytes("正文内容在此。本内容由AI生成");

            var result = AigcLabelVerifier.Verify(text);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.TextExplicitSuffix && c.Outcome == CheckOutcome.Pass);
        }

        [Fact]
        public void Text_CompositionalVariants_Matched_Per5_1()
        {
            // 5.1 b)：要素组合判定，不限于固定文案——"合成"在前、"AI"小写、变体措辞都应命中
            foreach (string body in new[]
                     {
                         "以上内容由人工智能技术合成完成。",
                         "ai合成内容仅供参考",
                         "本作品为AI创作生成物。",
                     })
            {
                var result = AigcLabelVerifier.Verify(Encoding.UTF8.GetBytes(body));
                Assert.Equal(VerdictKind.Compliant, result.Verdict);
            }
        }

        [Fact]
        public void Text_OnlyAiElementWithoutGen_NotMatched()
        {
            // 仅含人工智能要素、无生成合成要素：不构成 5.1 b) 的文字形式显式标识
            byte[] text = Encoding.UTF8.GetBytes("AI 助手为您服务，请问有什么可以帮您？");

            var result = AigcLabelVerifier.Verify(text);

            Assert.Equal(VerdictKind.NotFound, result.Verdict);
        }

        [Fact]
        public void Text_GenElementOutsideWindow_NotMatched()
        {
            // 两要素都在，但生成要素离文本末尾超过 64 字符窗口：后缀窗口不命中
            string body = "这里提到了AI这个词。" + new string('文', 40) + "结尾。";
            var result = AigcLabelVerifier.Verify(Encoding.UTF8.GetBytes(body));

            Assert.Equal(VerdictKind.NotFound, result.Verdict);
        }

        [Fact]
        public void Text_WithoutPrompt_NotFound()
        {
            byte[] text = Encoding.UTF8.GetBytes("一段普通的文字内容。");

            var result = AigcLabelVerifier.Verify(text);

            Assert.Equal(VerdictKind.NotFound, result.Verdict);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.TextPromptAffix && c.Outcome == CheckOutcome.Skip);
        }

        [Fact]
        public void Text_Utf16Bom_DecodedAndMatched()
        {
            byte[] text = Encoding.Unicode.GetBytes("本内容由AI生成。正文。");
            byte[] withBom = new byte[2 + text.Length];
            withBom[0] = 0xFF; withBom[1] = 0xFE;
            text.CopyTo(withBom, 2);

            var result = AigcLabelVerifier.Verify(withBom);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
        }

        [Fact]
        public void Text_BinaryGarbage_Inconclusive_NotPretendingToBeText()
        {
            // 二进制垃圾落到文本兜底：必须报告无法判定，而不是 not_found
            byte[] binary = { 0x89, 0x50, 0xFF, 0xFE, 0x00, 0x01, 0x80, 0xC0, 0xFF, 0x9A };

            var result = AigcLabelVerifier.Verify(binary);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Checks, c =>
                c.Check == CheckIds.CarrierDetect && c.Outcome == CheckOutcome.Error
                && c.Code == CheckCodes.UnknownFormat);
        }

        [Fact]
        public void Text_WhitespaceBeforePrompt_StillMatchedAsPrefix()
        {
            byte[] text = Encoding.UTF8.GetBytes("\n  本内容由AI生成\n正文");

            var result = AigcLabelVerifier.Verify(text);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
        }

        [Fact]
        public void Text_LargeFile_SuffixPrompt_StillCompliant()
        {
            // 头尾窗口改造回归：大文件只需判定首尾窗口，中段不再整体读入
            var sb = new System.Text.StringBuilder();
            sb.Append("本内容由人工智能生成。\n");
            while (sb.Length < 300 * 1024)
            {
                sb.Append("正文填充段落，用于撑大文件体积。\n");
            }
            sb.Append("本文末尾提示：AI生成内容。\n");
            byte[] text = Encoding.UTF8.GetBytes(sb.ToString());
            Assert.True(text.Length > 64 * 1024);

            var result = AigcLabelVerifier.Verify(text);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Equal(2, result.Sites.Count); // 前缀 + 后缀
        }

        [Fact]
        public void Text_LargePlainFile_NotFound_NotInconclusive()
        {
            // 大而无标识的合法文本：可判定（not_found），不再因超过读取预算降为 inconclusive
            var sb = new System.Text.StringBuilder();
            while (sb.Length < 200 * 1024)
            {
                sb.Append("普通正文，没有任何提示语。\n");
            }
            byte[] text = Encoding.UTF8.GetBytes(sb.ToString());

            var result = AigcLabelVerifier.Verify(text);

            Assert.Equal(VerdictKind.NotFound, result.Verdict);
        }

        [Fact]
        public void Text_InvalidUtf8BeyondHeadWindow_Inconclusive()
        {
            // 头窗口之后出现非法 UTF-8：全量流式校验必须仍然抓到（不能只验头尾）
            var sb = new System.Text.StringBuilder();
            sb.Append("这是一篇普通文章。\n");
            while (sb.Length < 100 * 1024)
            {
                sb.Append("正文填充。\n");
            }
            byte[] text = Encoding.UTF8.GetBytes(sb.ToString());
            text[90 * 1024] = 0xFF; // 非法首字节

            var result = AigcLabelVerifier.Verify(text);

            Assert.Equal(VerdictKind.Inconclusive, result.Verdict);
            Assert.Contains(result.Checks, c =>
                c.Check == CheckIds.CarrierDetect && c.Outcome == CheckOutcome.Error
                && c.Code == CheckCodes.UnknownFormat);
        }

        [Fact]
        public void Text_Utf8Bom_FrontMatter_StillDetected()
        {
            // BOM 后紧跟 front matter：解码必须剥掉 BOM，否则 "---" 首行匹配被 BOM 字符挡住
            byte[] body = Encoding.UTF8.GetBytes("---\nAIGC:\n  Label: '1'\n  ContentProducer: 'BomStudio'\n  ProduceID: 'B-1'\n---\n\n正文");
            byte[] text = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray();

            var result = AigcLabelVerifier.Verify(text);

            Assert.Equal(VerdictKind.Compliant, result.Verdict);
            Assert.Contains(result.Checks, c => c.Check == CheckIds.TextFrontMatterAigc && c.Outcome == CheckOutcome.Pass);
        }
    }
}
