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
    }
}
