namespace BeamNGTextureFixer.Models
{
    public class SearchHit
    {
        public string Status { get; set; } = "";
        public string MatchType { get; set; } = "";
        public string? SourceZipPath { get; set; }
        public string? InternalPath { get; set; }
    }
}