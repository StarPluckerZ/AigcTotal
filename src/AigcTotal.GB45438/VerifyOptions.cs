using AigcTotal.GB45438.Carriers;

namespace AigcTotal.GB45438
{
    /// <summary>不可信输入的安全边界（默认值按技术设计 §2.6；进报告回显）。</summary>
    public sealed class SecurityLimits
    {
        /// <summary>整个扫描过程累计读取字节上限（默认 8 MiB：2 GB 视频只读元数据区）。</summary>
        public long MaxTotalRead { get; set; } = 8 * 1024 * 1024;

        /// <summary>单个长度声明（chunk/box size）允许的分配上限（长度炸弹防御，默认 4 MiB）。</summary>
        public long MaxAlloc { get; set; } = 4 * 1024 * 1024;

        /// <summary>容器结构递归深度上限（默认 16 层，MP4 box）。</summary>
        public int MaxDepth { get; set; } = 16;

        /// <summary>单文件容器结构（chunk/box/segment）迭代上限（默认 100 000）。</summary>
        public long MaxStructures { get; set; } = 100_000;

        public SecurityLimits Clone() => new SecurityLimits
        {
            MaxTotalRead = MaxTotalRead,
            MaxAlloc = MaxAlloc,
            MaxDepth = MaxDepth,
            MaxStructures = MaxStructures,
        };
    }

    /// <summary>验证选项。</summary>
    public sealed class VerifyOptions
    {
        public SecurityLimits Security { get; set; } = new SecurityLimits();
    }
}
