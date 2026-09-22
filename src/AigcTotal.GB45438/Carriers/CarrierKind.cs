namespace AigcTotal.GB45438.Carriers
{
    /// <summary>载体格式。MOV/QuickTime 并入 <see cref="Mp4"/>（M4A 同为 keys/ilst 机制），细分形态记入 CarrierDetail。</summary>
    public enum CarrierKind
    {
        Png = 1,
        Jpeg = 2,
        Mp4 = 3,
        Wav = 4,
        Mp3 = 5,
        Text = 6,
        Flac = 7,
        Ogg = 8,
        Avi = 9,
        Webp = 10,
        Tiff = 11,
        Gif = 12,
        Ooxml = 13,
        Pdf = 14,
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
                case CarrierKind.Flac: return "flac";
                case CarrierKind.Ogg: return "ogg";
                case CarrierKind.Avi: return "avi";
                case CarrierKind.Webp: return "webp";
                case CarrierKind.Tiff: return "tiff";
                case CarrierKind.Gif: return "gif";
                case CarrierKind.Ooxml: return "ooxml";
                case CarrierKind.Pdf: return "pdf";
                default: return kind.ToString().ToLowerInvariant();
            }
        }
    }
}
