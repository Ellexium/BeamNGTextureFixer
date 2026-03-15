namespace BeamNGTextureFixer.Models
{
    public class MaterialFileInfo
    {
        public string Path { get; set; } = "";
        public string ParseMode { get; set; } = "";
        public int RefCount { get; set; }
        public string Error { get; set; } = "";
    }
}