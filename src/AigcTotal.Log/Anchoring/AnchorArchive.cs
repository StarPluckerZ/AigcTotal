using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AigcTotal.Log.Checkpoints;
using AigcTotal.Report;

namespace AigcTotal.Log.Anchoring
{
    /// <summary>归档发布记录（发布流程产物描述；CI 薄壳据此上传 GitHub Release）。</summary>
    public sealed record AnchorPublication(string Date, string TarballPath, string TarballSha256, int FileCount);

    /// <summary>锚定见证：归档日期 + checkpoint canonical 字节指纹。</summary>
    public sealed record AnchorWitness(string Date, string CheckpointFingerprint);

    /// <summary>
    /// 外部锚提供者（开源仓接口，W4）：发布 = 把公开物（segments/ + checkpoints/ + well-known/keys.json）
    /// 打成日期 tarball（含逐文件 SHA-256 manifest）；查询 = 判定某 checkpoint 是否被锚定于日期 T。
    /// 锚定语义：外部见证 checkpoint 字节于日期 T——验证者沿 prev_checkpoint_hash 回溯链，
    /// 碰到任一被锚 checkpoint 即为信任终点（截断链检测见 CheckpointChain）。
    /// </summary>
    public interface IAnchorProvider
    {
        /// <summary>构建并发布日期归档（本地实现落文件；CI 实现可再上传至 GitHub Releases——库核心不引 GitHub API）。</summary>
        AnchorPublication Publish(string publicDir, string anchorDate, string outputDir);

        /// <summary>查询 checkpoint 是否被任一已发布归档见证（按 sha256(完整 canonical JSON) 指纹）。</summary>
        AnchorWitness? Query(string checkpointFingerprint);
    }

    /// <summary>
    /// 第一实现：本地目录归档。Publish 产出 &lt;date&gt;.tar（可复现，同名重打包逐字节一致）；
    /// Query 扫描归档目录。下载与解包留 CI/手动（验证侧用 <see cref="ReadArchive"/> 直接读本地 tarball）。
    /// </summary>
    public sealed class LocalAnchorProvider : IAnchorProvider
    {
        /// <summary>已发布归档所在目录（Query 的扫描范围）。</summary>
        public string ArchiveDirectory { get; }

        public LocalAnchorProvider(string archiveDirectory)
        {
            if (string.IsNullOrWhiteSpace(archiveDirectory))
            {
                throw new ArgumentException("archive directory required", nameof(archiveDirectory));
            }
            ArchiveDirectory = archiveDirectory;
        }

        public AnchorPublication Publish(string publicDir, string anchorDate, string outputDir)
        {
            if (string.IsNullOrWhiteSpace(publicDir)) throw new ArgumentException("public dir required", nameof(publicDir));
            if (!TryParseDate(anchorDate)) throw new ArgumentException("date must be YYYY-MM-DD", nameof(anchorDate));
            if (string.IsNullOrWhiteSpace(outputDir)) throw new ArgumentException("output dir required", nameof(outputDir));

            var entries = new List<(string Path, byte[] Content)>();
            entries.Add(("manifest.json", BuildManifest(publicDir, anchorDate)));

            foreach (string relative in EnumeratePublicFiles(publicDir))
            {
                entries.Add((relative, File.ReadAllBytes(Path.Combine(publicDir, relative))));
            }

            Directory.CreateDirectory(outputDir);
            string tarball = Path.Combine(outputDir, anchorDate + ".tar");
            DeterministicTar.Write(tarball, entries);
            string sha = ToClaim(Rfc6962Sha256(File.ReadAllBytes(tarball)));
            return new AnchorPublication(anchorDate, tarball, sha, entries.Count - 1);
        }

        public AnchorWitness? Query(string checkpointFingerprint)
        {
            foreach (string tarball in EnumerateArchives())
            {
                string date = Path.GetFileNameWithoutExtension(tarball);
                if (!TryParseDate(date)) continue;
                AnchorArchive archive = ReadArchive(tarball);
                foreach (string fingerprint in archive.CheckpointFingerprints())
                {
                    if (fingerprint == checkpointFingerprint)
                    {
                        return new AnchorWitness(date, fingerprint);
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 截断链的信任端点查询：链首 prev_checkpoint_hash 指向的 checkpoint 不在本地——
        /// 若（已整验的）归档内有同指纹 checkpoint，即被锚定，链自该点起可信。
        /// </summary>
        public AnchorWitness? QueryChainEndpoint(string prevCheckpointHash)
        {
            foreach (string tarball in EnumerateArchives())
            {
                string date = Path.GetFileNameWithoutExtension(tarball);
                if (!TryParseDate(date)) continue;
                AnchorArchive archive = ReadArchive(tarball);
                foreach (string fingerprint in archive.CheckpointFingerprints())
                {
                    if (fingerprint == prevCheckpointHash)
                    {
                        return new AnchorWitness(date, fingerprint);
                    }
                }
            }
            return null;
        }

        /// <summary>锚源可以是归档目录（全部 *.tar）或单个 tarball 文件路径。</summary>
        private IEnumerable<string> EnumerateArchives()
        {
            if (File.Exists(ArchiveDirectory))
            {
                yield return ArchiveDirectory;
            }
            else if (Directory.Exists(ArchiveDirectory))
            {
                foreach (string tarball in Directory.GetFiles(ArchiveDirectory, "*.tar"))
                {
                    yield return tarball;
                }
            }
        }

        /// <summary>读取并整验归档：tar 结构 + manifest 逐文件 SHA-256（篡改任一字节即 FormatException）。</summary>
        public static AnchorArchive ReadArchive(string tarballPath)
        {
            if (tarballPath == null) throw new ArgumentNullException(nameof(tarballPath));
            List<(string Path, byte[] Content)> entries = DeterministicTar.Read(tarballPath);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            byte[]? manifestBytes = null;
            foreach (var entry in entries)
            {
                if (files.ContainsKey(entry.Path))
                {
                    throw new FormatException($"duplicate tar entry '{entry.Path}'");
                }
                files[entry.Path] = entry.Content;
                if (entry.Path == "manifest.json") manifestBytes = entry.Content;
            }
            if (manifestBytes == null) throw new FormatException("archive has no manifest.json");

            Dictionary<string, object?> manifest;
            try
            {
                manifest = CanonicalJson.Deserialize(Encoding.UTF8.GetString(manifestBytes));
            }
            catch (FormatException ex)
            {
                throw new FormatException("malformed manifest.json: " + ex.Message, ex);
            }
            if (!manifest.TryGetValue("date", out object? dateObj) || dateObj is not string date || !TryParseDate(date))
            {
                throw new FormatException("manifest.json has no valid 'date'");
            }
            if (!manifest.TryGetValue("files", out object? filesObj) || filesObj is not List<object?> list)
            {
                throw new FormatException("manifest.json must contain a 'files' array");
            }

            foreach (object? item in list)
            {
                if (item is not Dictionary<string, object?> file
                    || !file.TryGetValue("path", out object? pathObj) || pathObj is not string path
                    || !file.TryGetValue("sha256", out object? shaObj) || shaObj is not string expected)
                {
                    throw new FormatException("manifest entry must have path/sha256");
                }
                if (!files.TryGetValue(path, out byte[]? content))
                {
                    throw new FormatException($"manifest references missing file '{path}'");
                }
                string actual = ToClaim(Rfc6962Sha256(content));
                if (actual != expected)
                {
                    throw new FormatException($"file '{path}' does not match manifest (tampered archive?)");
                }
            }
            // 归档内多出的文件（manifest 未记录）同样视为篡改
            foreach (string path in files.Keys)
            {
                if (path == "manifest.json") continue;
                bool listed = false;
                foreach (object? item in list)
                {
                    if (item is Dictionary<string, object?> file
                        && file.TryGetValue("path", out object? pathObj) && pathObj is string p && p == path)
                    {
                        listed = true;
                        break;
                    }
                }
                if (!listed) throw new FormatException($"archive contains unlisted file '{path}'");
            }
            return new AnchorArchive(date, files);
        }

        /// <summary>manifest：date + 逐文件 {path, sha256, bytes}（canonical JSON，进 tarball）。</summary>
        private static byte[] BuildManifest(string publicDir, string date)
        {
            var files = new List<object?>();
            foreach (string relative in EnumeratePublicFiles(publicDir))
            {
                byte[] content = File.ReadAllBytes(Path.Combine(publicDir, relative));
                files.Add(new Dictionary<string, object?>
                {
                    ["path"] = relative,
                    ["sha256"] = ToClaim(Rfc6962Sha256(content)),
                    ["bytes"] = (long)content.Length,
                });
            }
            string manifest = CanonicalJson.Serialize(new Dictionary<string, object?>
            {
                ["date"] = date,
                ["files"] = files,
            });
            return Encoding.UTF8.GetBytes(manifest);
        }

        private static IEnumerable<string> EnumeratePublicFiles(string publicDir)
        {
            foreach (string sub in new[] { "segments", "checkpoints", "well-known" })
            {
                string dir = Path.Combine(publicDir, sub);
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    // 归一化为 '/' 分隔（tar 路径形态与平台无关）
                    yield return sub + "/" + Path.GetFileName(file);
                }
            }
        }

        internal static bool TryParseDate(string date)
        {
            return date != null && date.Length == 10 && date[4] == '-' && date[7] == '-'
                && DateTime.TryParseExact(date, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _);
        }

        internal static byte[] Rfc6962Sha256(byte[] data)
        {
#if NET
            return System.Security.Cryptography.SHA256.HashData(data);
#else
            using var sha = System.Security.Cryptography.SHA256.Create();
            return sha.ComputeHash(data);
#endif
        }

        internal static string ToClaim(byte[] digest)
        {
            var sb = new StringBuilder(7 + 64);
            sb.Append("sha256:");
            foreach (byte b in digest)
            {
                sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }

    /// <summary>整验通过的归档（内存形态）：文件表 + checkpoint 指纹集。</summary>
    public sealed class AnchorArchive
    {
        internal AnchorArchive(string date, IReadOnlyDictionary<string, byte[]> files)
        {
            Date = date;
            _files = files;
        }

        public string Date { get; }

        private readonly IReadOnlyDictionary<string, byte[]> _files;

        public IReadOnlyDictionary<string, byte[]> Files => _files;

        /// <summary>归档内全部 checkpoint 的 canonical 字节指纹（被本归档见证的集合）。</summary>
        public IEnumerable<string> CheckpointFingerprints()
        {
            foreach (var pair in _files)
            {
                if (!pair.Key.StartsWith("checkpoints/", StringComparison.Ordinal)) continue;
                Checkpoint checkpoint = CheckpointCodec.Parse(Encoding.UTF8.GetString(pair.Value));
                yield return CheckpointBuilder.FingerprintOf(checkpoint);
            }
        }
    }
}
