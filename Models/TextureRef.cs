namespace BeamNGTextureFixer.Models
{
    public class TextureRef
    {
        public string MaterialFile { get; set; } = "";
        public string MaterialName { get; set; } = "";
        public int StageIndex { get; set; }
        public string Key { get; set; } = "";
        public string OriginalValue { get; set; } = "";
        public string NormalizedValue { get; set; } = "";
        public string ExtractionMode { get; set; } = "";
    }
}