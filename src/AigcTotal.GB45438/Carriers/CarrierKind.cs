namespace AigcTotal.GB45438.Carriers
{
    /// <summary>载体格式。MOV/QuickTime 并入 <see cref="Mp4"/>，细分形态记入 CarrierDetail。</summary>
    public enum CarrierKind
    {
        Png = 1,
        Jpeg = 2,
        Mp4 = 3,
        Wav = 4,
        Mp3 = 5,
        Text = 6,
    }

    public static class CarrierNames
    {
        /// <summary>canonical 小写令牌（进报告字节形态，只增不改）。</summary>
        public static string ToToken(CarrierKind kind)
        {
            switch (kind)
            {
                case CarrierKind.Png: return "png";
                case CarrierKind.Jpeg: return "jpeg";
                case CarrierKind.Mp4: return "mp4";
                case CarrierKind.Wav: return "wav";
                case CarrierKind.Mp3: return "mp3";
                case CarrierKind.Text: return "text";
                default: return kind.ToString().ToLowerInvariant();
            }
        }
    }
}
