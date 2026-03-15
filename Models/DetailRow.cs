namespace BeamNGTextureFixer.Models
{
    public class DetailRow
    {
        public string MaterialFile { get; set; } = "";
        public string ParseMode { get; set; } = "";
        public string MaterialName { get; set; } = "";
        public int StageIndex { get; set; }
        public string Key { get; set; } = "";
        public string OriginalPath { get; set; } = "";
        public int ResolvedFromOld { get; set; }
        public int FixesMade { get; set; }
        public string Status { get; set; } = "";
        public string MatchType { get; set; } = "";
        public string Source { get; set; } = "";
        public string NewPath { get; set; } = "";
    }
}