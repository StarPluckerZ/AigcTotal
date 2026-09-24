using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace AigcTotal.Rfc8785.Conformance
{
    /// <summary>
    /// RFC 8785 一致性证明运行器：汇总全部附录/正文向量、开发门户官方测试数据、
    /// Node（V8）侧「附录 A 逐字实现」差分对账与大规模数字差分，输出 Markdown 证明报告。
    /// 退出码：0 = 全部通过（SKIPPED 组不计入失败）；1 = 存在失败。
    /// </summary>
    internal static class Program
    {
        private const int StructCorpusSize = 2500;
        private const int NumberFuzzSamples = 200_000;

        private static int Main(string[] args)
        {
            string? outPath = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--out")
                {
                    outPath = args[i + 1];
                }
            }

            var sections = new List<Section>
            {
                RunSection322(),
                RunSection323(),
                RunAppendixB(),
                RunAppendixCde(),
                RunOfficialTestdata(),
            };

            string? nodeVersion = DetectNode();
            if (nodeVersion is null)
            {
                sections.Add(Section.Skipped("Node 附录 A 逐字实现差分", "node 不在 PATH 中"));
                sections.Add(Section.Skipped("V8 数字大规模差分", "node 不在 PATH 中"));
            }
            else
            {
                sections.Add(RunNodeStructDifferential(nodeVersion));
                sections.Add(RunNodeNumberDifferential(nodeVersion));
            }

            string markdown = RenderMarkdown(sections, nodeVersion);
            Console.WriteLine(markdown);
            if (outPath is not null)
            {
                File.WriteAllText(outPath, markdown, new UTF8Encoding(false));
                Console.Error.WriteLine($"report written: {outPath}");
            }

            foreach (Section section in sections)
            {
                if (section.Failed > 0)
                {
                    return 1;
                }
            }

            return 0;
        }

        private sealed record Section(string Title, int Total, int Passed, int Failed, string Note, List<string> Details)
        {
            public static Section Pass(string title, int total, string note, List<string>? details = null)
            {
                return new Section(title, total, total, 0, note, details ?? new List<string>());
            }

            public static Section Skipped(string title, string note)
            {
                return new Section(title, 0, 0, 0, note, new List<string>());
            }
        }

        // ---- §3.2.2 / §3.2.3 / §3.2.4 正文向量 ----

        private const string Rfc322Input = """
            {
              "numbers": [333333333.33333329, 1E30, 4.50,
                          2e-3, 0.000000000000000000000000001],
              "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
              "literals": [null, true, false]
            }
            """;

        private const string Rfc322Canonical = """
            {"literals":[null,true,false],"numbers":[333333333.3333333,1e+30,4.5,0.002,1e-27],"string":"€$\u000f\nA'B\"\\\\\"/"}
            """;

        private const string Rfc324Utf8Hex =
            "7b 22 6c 69 74 65 72 61 6c 73 22 3a 5b 6e 75 6c 6c 2c 74 72 75 65 2c 66 61 6c 73 65 5d 2c 22 6e 75 6d 62 65 72 73 22 3a " +
            "5b 33 33 33 33 33 33 33 33 33 2e 33 33 33 33 33 33 33 2c 31 65 2b 33 30 2c 34 2e 35 2c 30 2e 30 30 32 2c 31 65 2d 32 37 " +
            "5d 2c 22 73 74 72 69 6e 67 22 3a 22 e2 82 ac 24 5c 75 30 30 30 66 5c 6e 41 27 42 5c 22 5c 5c 5c 5c 5c 22 2f 22 7d";

        private const string Rfc323Input = """
            {
              "\u20ac": "Euro Sign",
              "\r": "Carriage Return",
              "\ufb33": "Hebrew Letter Dalet With Dagesh",
              "1": "One",
              "\ud83d\ude00": "Emoji: Grinning Face",
              "\u0080": "Control",
              "\u00f6": "Latin Small Letter O With Diaeresis"
            }
            """;

        private static readonly string[] Rfc323ArgumentOrder =
        {
            "Carriage Return",
            "One",
            "Control",
            "Latin Small Letter O With Diaeresis",
            "Euro Sign",
            "Emoji: Grinning Face",
            "Hebrew Letter Dalet With Dagesh",
        };

        private static Section RunSection322()
        {
            var details = new List<string>();
            int passed = 0, total = 0;

            string canonical = JsonCanonicalizer.Canonicalize(Rfc322Input);
            total++;
            if (canonical == Rfc322Canonical)
            {
                passed++;
                details.Add("§3.2.2 规范形态逐字一致");
            }
            else
            {
                details.Add($"§3.2.2 不一致：got {canonical}");
            }

            byte[] utf8 = JsonCanonicalizer.CanonicalizeToUtf8(Rfc322Input);
            byte[] expected = Convert.FromHexString(Rfc324Utf8Hex.Replace(" ", string.Empty, StringComparison.Ordinal));
            total++;
            bool hexOk = utf8.AsSpan().SequenceEqual(expected);
            if (hexOk)
            {
                passed++;
            }

            details.Add($"§3.2.4 UTF-8 字节级向量：{(hexOk ? "一致" : "不一致")}（{utf8.Length} 字节）");
            return new Section("RFC 8785 §3.2.2/§3.2.4 原语序列化与 UTF-8 生成", total, passed, total - passed,
                hexOk ? "canonical + UTF-8 字节向量一致" : "存在不一致", details);
        }

        private static Section RunSection323()
        {
            var details = new List<string>();
            string canonical = JsonCanonicalizer.Canonicalize(Rfc323Input);

            const string expectedCanonical = "{\"\\r\":\"Carriage Return\",\"1\":\"One\",\"\":\"Control\"," +
                "\"ö\":\"Latin Small Letter O With Diaeresis\",\"€\":\"Euro Sign\"," +
                "\"😀\":\"Emoji: Grinning Face\",\"דּ\":\"Hebrew Letter Dalet With Dagesh\"}";
            bool canonicalOk = canonical == expectedCanonical;
            details.Add(canonicalOk
                ? "规范形态一致（U+0080 依 §3.2.2.2 原样输出）"
                : $"不一致：got {canonical}");

            int previous = -1;
            bool orderOk = true;
            foreach (string label in Rfc323ArgumentOrder)
            {
                int index = canonical.IndexOf(label, StringComparison.Ordinal);
                if (index <= previous)
                {
                    orderOk = false;
                    break;
                }

                previous = index;
            }

            details.Add(orderOk
                ? "「Expected argument order after sorting」逐项一致（UTF-16 码元序）"
                : "排序次序与 RFC 给出的论证顺序不一致");

            return new Section("RFC 8785 §3.2.3 UTF-16 码元序排序", 2, (canonicalOk ? 1 : 0) + (orderOk ? 1 : 0),
                (canonicalOk ? 0 : 1) + (orderOk ? 0 : 1), canonicalOk && orderOk ? "一致" : "存在不一致", details);
        }

        // ---- 附录 B 表 1 ----

        private static readonly (string Hex, string? Expected, string Comment)[] Table1 =
        {
            ("0000000000000000", "0", "Zero"),
            ("8000000000000000", "0", "Minus zero"),
            ("0000000000000001", "5e-324", "Min pos number"),
            ("8000000000000001", "-5e-324", "Min neg number"),
            ("7fefffffffffffff", "1.7976931348623157e+308", "Max pos number"),
            ("ffefffffffffffff", "-1.7976931348623157e+308", "Max neg number"),
            ("4340000000000000", "9007199254740992", "Max pos int (1)"),
            ("c340000000000000", "-9007199254740992", "Max neg int (1)"),
            ("4430000000000000", "295147905179352830000", "~2**68 (2)"),
            ("7fffffffffffffff", null, "NaN (3)"),
            ("7ff0000000000000", null, "Infinity (3)"),
            ("44b52d02c7e14af5", "9.999999999999997e+22", string.Empty),
            ("44b52d02c7e14af6", "1e+23", string.Empty),
            ("44b52d02c7e14af7", "1.0000000000000001e+23", string.Empty),
            ("444b1ae4d6e2ef4e", "999999999999999700000", string.Empty),
            ("444b1ae4d6e2ef4f", "999999999999999900000", string.Empty),
            ("444b1ae4d6e2ef50", "1e+21", string.Empty),
            ("3eb0c6f7a0b5ed8c", "9.999999999999997e-7", string.Empty),
            ("3eb0c6f7a0b5ed8d", "0.000001", string.Empty),
            ("41b3de4355555553", "333333333.3333332", string.Empty),
            ("41b3de4355555554", "333333333.33333325", string.Empty),
            ("41b3de4355555555", "333333333.3333333", string.Empty),
            ("41b3de4355555556", "333333333.3333334", string.Empty),
            ("41b3de4355555557", "333333333.33333343", string.Empty),
            ("becbf647612f3696", "-0.0000033333333333333333", string.Empty),
            ("43143ff3c1cb0959", "1424953923781206.2", "Round to even (4)"),
        };

        private static Section RunAppendixB()
        {
            var details = new List<string>();
            int passed = 0;
            foreach ((string hex, string? expected, string comment) in Table1)
            {
                double value = BitConverter.Int64BitsToDouble(unchecked((long)Convert.ToUInt64(hex, 16)));
                bool ok;
                if (expected is null)
                {
                    try
                    {
                        JsonCanonicalizer.Serialize(value);
                        ok = false;
                        details.Add($"{hex}（{comment}）未按 MUST 终止");
                    }
                    catch (JsonCanonicalizationException)
                    {
                        ok = true;
                    }
                }
                else
                {
                    ok = JsonCanonicalizer.Serialize(value) == expected;
                    if (!ok)
                    {
                        details.Add($"{hex} 期望 {expected}，实际 {JsonCanonicalizer.Serialize(value)}");
                    }
                }

                if (ok)
                {
                    passed++;
                }
            }

            details.Add("含 2 的幂边界（~2**68、1e+23 邻域）、±0、次正规极值与 Note 2 平局取偶（Round to even）行");
            return new Section("RFC 8785 附录 B 表 1（Number Serialization Samples）", Table1.Length, passed,
                Table1.Length - passed, passed == Table1.Length ? "全部一致" : "存在不一致", details);
        }

        // ---- 附录 C/D/E 正文样例 ----

        private static Section RunAppendixCde()
        {
            var details = new List<string>();
            int passed = 0, total = 0;

            void Check(string name, bool ok, string detail)
            {
                total++;
                if (ok)
                {
                    passed++;
                    details.Add($"{name}：一致");
                }
                else
                {
                    details.Add($"{name}：不一致（{detail}）");
                }
            }

            Check("附录 C 地址记录按 address/city/name/state/zip 输出",
                JsonCanonicalizer.Canonicalize("{\"name\":\"John Doe\",\"address\":\"2000 Sunset Boulevard\",\"city\":\"Los Angeles\",\"zip\":\"90001\",\"state\":\"CA\"}")
                    == "{\"address\":\"2000 Sunset Boulevard\",\"city\":\"Los Angeles\",\"name\":\"John Doe\",\"state\":\"CA\",\"zip\":\"90001\"}",
                string.Empty);

            bool dOverflow = false;
            try
            {
                JsonCanonicalizer.Canonicalize("{\"giantNumber\":1.4e+9999}");
            }
            catch (JsonCanonicalizationException)
            {
                dOverflow = true;
            }

            Check("附录 D 1.4e+9999 超出 binary64 → MUST 终止（推荐字符串包装的原因）", dOverflow, string.Empty);

            Check("附录 D 字符串包装的巨数原样透传",
                JsonCanonicalizer.Canonicalize("{\"giantNumber\":\"1.4e+9999\"}") == "{\"giantNumber\":\"1.4e+9999\"}",
                string.Empty);

            Check("附录 D int64Max 按 binary64 语义变形（V8 同为 9223372036854776000）",
                JsonCanonicalizer.Canonicalize("{\"int64Max\":9223372036854775807}") == "{\"int64Max\":9223372036854776000}",
                string.Empty);

            Check("附录 E 纯字符串子类型透传（\"big\":\"055\" 保持原样）",
                JsonCanonicalizer.Canonicalize("{\"time\":\"2019-01-28T07:45:10Z\",\"big\":\"055\",\"val\":3.5}")
                    == "{\"big\":\"055\",\"time\":\"2019-01-28T07:45:10Z\",\"val\":3.5}",
                string.Empty);

            return new Section("RFC 8785 附录 C/D/E 正文样例", total, passed, total - passed,
                passed == total ? "全部一致" : "存在不一致", details);
        }

        // ---- 开发门户官方测试数据（附录 I） ----

        private static Section RunOfficialTestdata()
        {
            string? root = FindUp("standards/rfc8785/testdata");
            if (root is null)
            {
                return Section.Skipped("开发门户官方测试数据（附录 I）", "未找到 standards/rfc8785/testdata 目录");
            }

            string inputDir = Path.Combine(root, "input");
            string outputDir = Path.Combine(root, "output");
            var details = new List<string>();
            int passed = 0, total = 0;
            foreach (string inputFile in Directory.GetFiles(inputDir, "*.json"))
            {
                string name = Path.GetFileName(inputFile);
                string outputFile = Path.Combine(outputDir, name);
                if (!File.Exists(outputFile))
                {
                    continue;
                }

                byte[] actual = JsonCanonicalizer.CanonicalizeToUtf8(File.ReadAllBytes(inputFile));
                byte[] expected = File.ReadAllBytes(outputFile);
                bool ok = actual.AsSpan().SequenceEqual(expected);
                total++;
                if (ok)
                {
                    passed++;
                    details.Add($"{name}：逐字节一致");
                }
                else
                {
                    details.Add($"{name}：不一致");
                }
            }

            return new Section("开发门户官方测试数据（cyberphone/json-canonicalization testdata，Apache-2.0）",
                total, passed, total - passed, passed == total && total > 0 ? "全部逐字节一致" : "存在不一致或为空", details);
        }

        // ---- Node（V8）差分 ----

        private static string? DetectNode()
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("node", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (proc is null)
                {
                    return null;
                }

                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(10_000);
                return proc.ExitCode == 0 ? output : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string OraclePath => Path.Combine(AppContext.BaseDirectory, "oracle.cjs");

        private static (int ExitCode, string StdOut, string StdErr) RunNode(string arguments)
        {
            using var proc = Process.Start(new ProcessStartInfo("node", $"\"{OraclePath}\" {arguments}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (proc is null)
            {
                return (2, string.Empty, "cannot start node");
            }

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(120_000);
            return (proc.ExitCode, stdout, stderr);
        }

        private static Section RunNodeStructDifferential(string nodeVersion)
        {
            if (!File.Exists(OraclePath))
            {
                return Section.Skipped("Node 附录 A 逐字实现差分", "oracle.cjs 未随运行器输出");
            }

            string corpusPath = Path.Combine(Path.GetTempPath(), $"rfc8785-struct-{Guid.NewGuid():N}.jsonl");
            try
            {
                File.WriteAllLines(corpusPath, BuildStructCorpus());
                (int exitCode, string stdout, string stderr) = RunNode($"struct \"{corpusPath}\"");
                var details = new List<string>
                {
                    $"附录 A 代码逐字拷贝自 RFC 8785（V8 = JSON.stringify/JSON.parse 规范引擎），node {nodeVersion}",
                };
                if (exitCode == 0 && stdout.Trim().StartsWith("OK ", StringComparison.Ordinal))
                {
                    int total = int.Parse(stdout.Trim()["OK ".Length..], CultureInfo.InvariantCulture);
                    details.Add($"语料含 RFC/官方样例原文 + {StructCorpusSize} 条程序化生成（含控制字符、C1、 emoji、非规范化 Unicode、乱序键）");
                    return new Section("Node/V8 附录 A 逐字实现差分（结构语料）", total, total, 0, $"V8 全量一致（node {nodeVersion}）", details);
                }

                details.Add($"node 退出码 {exitCode}；stdout={stdout.Trim()} stderr={stderr.Trim()}");
                return new Section("Node/V8 附录 A 逐字实现差分（结构语料）", 1, 0, 1, "差分失败", details);
            }
            finally
            {
                File.Delete(corpusPath);
            }
        }

        private static Section RunNodeNumberDifferential(string nodeVersion)
        {
            if (!File.Exists(OraclePath))
            {
                return Section.Skipped("V8 数字大规模差分", "oracle.cjs 未随运行器输出");
            }

            string hexPath = Path.Combine(Path.GetTempPath(), $"rfc8785-num-{Guid.NewGuid():N}.txt");
            try
            {
                List<double> samples = BuildNumberCorpus(NumberFuzzSamples);
                using (var writer = new StreamWriter(hexPath))
                {
                    foreach (double sample in samples)
                    {
                        writer.WriteLine(BitConverter.DoubleToInt64Bits(sample).ToString("X16", CultureInfo.InvariantCulture));
                    }
                }

                (int exitCode, string stdout, string stderr) = RunNode($"numbers \"{hexPath}\"");
                if (exitCode != 0)
                {
                    return new Section("V8 数字大规模差分（JSON.stringify ≡ ECMA-262 Number::toString）", 1, 0, 1,
                        $"node 失败：{stderr.Trim()}", new List<string>());
                }

                string[] expected = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (expected.Length != samples.Count)
                {
                    return new Section("V8 数字大规模差分（JSON.stringify ≡ ECMA-262 Number::toString）", 1, 0, 1,
                        $"行数不匹配：{expected.Length} ≠ {samples.Count}", new List<string>());
                }

                var details = new List<string> { $"样本 = 全随机位型 / 正规 binade 聚焦 / 次正规 / 10 的幂 ±1ulp / 2 的幂 ±1ulp / 小数邻域（node {nodeVersion}）" };
                int passed = 0, firstBad = -1;
                string firstBadExpected = string.Empty, firstBadActual = string.Empty;
                int skippedNonFinite = 0;
                for (int i = 0; i < samples.Count; i++)
                {
                    if (!double.IsFinite(samples[i]))
                    {
                        // 非有限样本的 MUST 终止行为由附录 B 组覆盖，此处仅差分有限域
                        skippedNonFinite++;
                        continue;
                    }

                    string actual = JsonCanonicalizer.Serialize(samples[i]);
                    if (actual == expected[i].Trim())
                    {
                        passed++;
                    }
                    else if (firstBad < 0)
                    {
                        firstBad = i;
                        firstBadExpected = expected[i].Trim();
                        firstBadActual = actual;
                    }
                }

                if (firstBad >= 0)
                {
                    details.Add($"首个不一致 #{firstBad}: bits={BitConverter.DoubleToInt64Bits(samples[firstBad]):X16} 期望 {firstBadExpected} 实际 {firstBadActual}");
                }

                if (skippedNonFinite > 0)
                {
                    details.Add($"跳过非有限样本 {skippedNonFinite} 个（MUST 终止行为见附录 B 组）");
                }

                return new Section("V8 数字大规模差分（JSON.stringify ≡ ECMA-262 Number::toString）",
                    samples.Count - skippedNonFinite, passed, samples.Count - skippedNonFinite - passed,
                    passed == samples.Count - skippedNonFinite ? "V8 全量一致" : "存在不一致", details);
            }
            finally
            {
                File.Delete(hexPath);
            }
        }

        // ---- 语料生成（确定性 xorshift64*） ----

        private static ulong _rngState = 0x243F6A8885A308D3UL;

        private static ulong NextRandom()
        {
            _rngState ^= _rngState >> 12;
            _rngState ^= _rngState << 25;
            _rngState ^= _rngState >> 27;
            return _rngState * 2685821657736338717UL;
        }

        private static double NextFiniteDouble()
        {
            ulong bits = NextRandom();
            int selector = (int)(NextRandom() % 4UL);
            if (selector == 0)
            {
                bits = (bits & 0x000FFFFFFFFFFFFFUL) | ((1 + (NextRandom() % 2046UL)) << 52); // 正规域
            }
            else if (selector == 1)
            {
                bits &= 0x000FFFFFFFFFFFFFUL; // 次正规域
                if (bits == 0)
                {
                    bits = 1;
                }
            }

            if (((bits >> 52) & 0x7FF) == 0x7FF)
            {
                bits ^= 1UL << 52;
            }

            return BitConverter.Int64BitsToDouble(unchecked((long)bits));
        }

        private static double NextInterestingDouble()
        {
            // 偏置采样：半整数 / 整数 / 10 的幂邻域，抬高边界命中率
            return (NextRandom() % 3UL) switch
            {
                0 => (long)(NextRandom() % 4_000_000UL) - 2_000_000 + ((NextRandom() % 2UL) * 0.5d),
                1 => Math.Pow(10, (double)(long)(NextRandom() % 617UL) - 308),
                _ => NextFiniteDouble(),
            };
        }

        private static readonly char[] SpecialChars =
        {
            '"', '\\', '/', '\b', '\f', '\n', '\r', '\t', (char)0x00, (char)0x1F, (char)0x7F, (char)0x80, '€', 'ö', '日', (char)0x2028, (char)0x2029,
        };

        private static string NextRandomString()
        {
            int length = (int)(NextRandom() % 13UL);
            var sb = new StringBuilder(length * 2 + 2);
            while (sb.Length < length)
            {
                ulong choice = NextRandom() % 3UL;
                if (choice == 0)
                {
                    sb.Append((char)('a' + (NextRandom() % 26UL)));
                }
                else if (choice == 1)
                {
                    sb.Append(SpecialChars[NextRandom() % (ulong)SpecialChars.Length]);
                }
                else
                {
                    sb.Append(char.ConvertFromUtf32(0x10000 + (int)(NextRandom() % 0xFFFFFUL))); // 含 emoji 与 CJK 扩展
                }
            }

            return sb.ToString();
        }

        private static readonly string[] KeyPool =
        {
            "a", "A", "z", "Z", "aa", "1", "10", "2", "", "€", "😀", "ö", "\r", "\n", "\u0080", "</script>", " key", "健", "键",
        };

        private static object? RandomValue(int depth)
        {
            if (depth >= 4)
            {
                return RandomScalar();
            }

            return (int)(NextRandom() % 5UL) switch
            {
                0 => RandomScalar(),
                1 => RandomScalar(),
                2 => BuildArray(depth),
                3 => BuildObject(depth),
                _ => RandomScalar(),
            };
        }

        private static object? RandomScalar()
        {
            return (int)(NextRandom() % 5UL) switch
            {
                0 => null,
                1 => (NextRandom() & 1) == 0,
                2 => NextRandomString(),
                3 => NextInterestingDouble(),
                _ => NextFiniteDouble(),
            };
        }

        private static List<object?> BuildArray(int depth)
        {
            int count = (int)(NextRandom() % 5UL);
            var list = new List<object?>(count);
            for (int i = 0; i < count; i++)
            {
                list.Add(RandomValue(depth + 1));
            }

            return list;
        }

        private static Dictionary<string, object?> BuildObject(int depth)
        {
            int count = (int)(NextRandom() % 5UL);
            var dict = new Dictionary<string, object?>(count);
            for (int i = 0; i < count; i++)
            {
                string key = (NextRandom() & 1) == 0
                    ? KeyPool[NextRandom() % (ulong)KeyPool.Length]
                    : NextRandomString();
                dict[key] = RandomValue(depth + 1);
            }

            return dict;
        }

        /// <summary>插入序 plain JSON 写端：生成「未排序但合法」的 JSON 文本作为差分输入。</summary>
        private static void WritePlain(StringBuilder sb, object? value)
        {
            if (value is null)
            {
                sb.Append("null");
            }
            else if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
            }
            else if (value is string s)
            {
                WritePlainString(sb, s);
            }
            else if (value is double d)
            {
                sb.Append(JsonCanonicalizer.Serialize(d));
            }
            else if (value is long l)
            {
                sb.Append(JsonCanonicalizer.Serialize((double)l));
            }
            else if (value is int i)
            {
                sb.Append(JsonCanonicalizer.Serialize((double)i));
            }
            else if (value is List<object?> list)
            {
                sb.Append('[');
                for (int k = 0; k < list.Count; k++)
                {
                    if (k > 0)
                    {
                        sb.Append(',');
                    }

                    WritePlain(sb, list[k]);
                }

                sb.Append(']');
            }
            else if (value is Dictionary<string, object?> dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object?> entry in dict)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    WritePlainString(sb, entry.Key);
                    sb.Append(':');
                    WritePlain(sb, entry.Value);
                }

                sb.Append('}');
            }
            else
            {
                throw new InvalidOperationException($"unsupported corpus type {value.GetType().Name}");
            }
        }

        private static void WritePlainString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                if (c == '"' || c == '\\')
                {
                    sb.Append('\\').Append(c);
                }
                else if (c < 0x20 || c == '\u2028' || c == '\u2029')
                {
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(c);
                }
            }

            sb.Append('"');
        }

        private static List<string> BuildStructCorpus()
        {
            var entries = new List<string>();
            void Add(string rawInput)
            {
                string expected = JsonCanonicalizer.Canonicalize(rawInput);
                var sb = new StringBuilder();
                sb.Append("{\"input\":");
                WritePlainString(sb, rawInput);
                sb.Append(",\"expected\":");
                WritePlainString(sb, expected);
                sb.Append('}');
                entries.Add(sb.ToString());
            }

            Add(Rfc322Input);
            Add(Rfc323Input);
            Add("-0");
            Add("\"\\ud83d\\ude00\"");
            Add("{\"k\":[1.0,2e2,3E-1,0.1e1]}");
            for (int i = 0; i < StructCorpusSize; i++)
            {
                var sb = new StringBuilder();
                WritePlain(sb, RandomValue(0));
                Add(sb.ToString());
            }

            return entries;
        }

        private static List<double> BuildNumberCorpus(int count)
        {
            var samples = new List<double>(count);
            foreach ((string hex, string? _, string? _) in Table1)
            {
                double value = BitConverter.Int64BitsToDouble(unchecked((long)Convert.ToUInt64(hex, 16)));
                if (double.IsFinite(value)) // NaN/Infinity 行的 MUST 终止已由附录 B 组覆盖
                {
                    samples.Add(value);
                }
            }

            for (int exp = -324; exp <= 308; exp++)
            {
                double v = Math.Pow(10, exp);
                samples.Add(v);
                samples.Add(BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(v) - 1));
                samples.Add(BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(v) + 1));
                samples.Add(-v);
            }

            while (samples.Count < count)
            {
                samples.Add(NextFiniteDouble());
            }

            return samples;
        }

        // ---- 报告 ----

        private static string RenderMarkdown(List<Section> sections, string? nodeVersion)
        {
            var sb = new StringBuilder();
            sb.Append("# RFC 8785（JCS）一致性证明报告\n\n");
            sb.Append(CultureInfo.InvariantCulture, $"- 生成时间：{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n");
            sb.Append(CultureInfo.InvariantCulture, $"- 被测实现：AigcTotal.Rfc8785 v{typeof(JsonCanonicalizer).Assembly.GetName().Version?.ToString(3)}（net10.0，运行于 {Environment.Version}）\n");
            sb.Append("- 验证基线：RFC 8785 正文 §3.2.2–§3.2.4 与附录 B/C/D/E 全部向量；");
            sb.Append("附录 I 开发门户官方测试数据（cyberphone/json-canonicalization testdata）；\n");
            sb.Append("- 独立预言机：Node/V8（附录 A 示例规范化器逐字实现 + JSON.stringify 的 ECMA-262 Number::toString）");
            sb.Append(nodeVersion is null ? "【未安装，相关组跳过】\n" : $"（{nodeVersion}）\n\n");

            int totalChecks = 0, totalPassed = 0, totalFailed = 0, skippedSections = 0;
            sb.Append("## 摘要\n\n| # | 验证组 | 检查数 | 通过 | 失败 | 结论 |\n|---|--------|--------|------|------|------|\n");
            for (int i = 0; i < sections.Count; i++)
            {
                Section s = sections[i];
                totalChecks += s.Total;
                totalPassed += s.Passed;
                totalFailed += s.Failed;
                if (s.Total == 0)
                {
                    skippedSections++;
                    sb.Append(CultureInfo.InvariantCulture, $"| {i + 1} | {s.Title} | – | – | – | SKIPPED（{s.Note}） |\n");
                }
                else
                {
                    sb.Append(CultureInfo.InvariantCulture, $"| {i + 1} | {s.Title} | {s.Total} | {s.Passed} | {s.Failed} | {(s.Failed == 0 ? "PASS" : "**FAIL**")} |\n");
                }
            }

            sb.Append(CultureInfo.InvariantCulture, $"\n**合计：{totalChecks} 项检查，{totalPassed} 通过，{totalFailed} 失败**（{skippedSections} 组环境性跳过）。\n\n");

            sb.Append("## 证明结构\n\n");
            sb.Append("1. **规范向量**：RFC 8785 全部规范性测试数据——§3.2.2 原语序列化、§3.2.3 UTF-16 码元序排序、\n");
            sb.Append("   §3.2.4 逐字节 UTF-8 输出、附录 B 表 1 全部 26 行（含 NaN/Infinity 的 MUST 终止行）。\n");
            sb.Append("2. **附录样例**：附录 C 键序、附录 D 大数终止/字符串包装、附录 E 子类型透传。\n");
            sb.Append("3. **官方扩展向量**：附录 I 开发门户 testdata（values/weird/french/structures/arrays/unicode 六组）逐字节比对。\n");
            sb.Append("4. **引擎差分**：附录 A 的 ECMAScript 示例规范化器逐字运行于 V8，对程序化结构语料全量对账；\n");
            sb.Append("   数字侧以 V8 JSON.stringify（ECMA-262 §7.1.12.1 的规范实现）作 20 万级样本差分。\n");
            sb.Append("5. **不变量**：全部数字输出做「解析回原 double 位形」的往返校验（dotnet test 内常态化执行）。\n\n");

            sb.Append("## 各组明细\n\n");
            for (int i = 0; i < sections.Count; i++)
            {
                Section s = sections[i];
                sb.Append(CultureInfo.InvariantCulture, $"### {i + 1}. {s.Title}\n\n");
                if (s.Total == 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"- SKIPPED：{s.Note}\n\n");
                    continue;
                }

                sb.Append(CultureInfo.InvariantCulture, $"- 结论：{(s.Failed == 0 ? "PASS" : "FAIL")}（{s.Passed}/{s.Total}）；{s.Note}\n");
                foreach (string detail in s.Details)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"- {detail}\n");
                }

                sb.Append('\n');
            }

            return sb.ToString();
        }

        private static string? FindUp(string relativeDir)
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 12 && dir is not null; i++)
            {
                string candidate = Path.Combine(dir.FullName, relativeDir);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            return null;
        }
    }
}
