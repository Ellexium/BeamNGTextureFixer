namespace BeamNGTextureFixer.Models
{
    public sealed class ThirdPassRow
    {
        public string MaterialName { get; set; } = string.Empty;
        public string PreStatus { get; set; } = string.Empty;
        public string ShouldLocalize { get; set; } = string.Empty;
        public string ActionTaken { get; set; } = string.Empty;
        public string ImportedFrom { get; set; } = string.Empty;
        public string InjectedInto { get; set; } = string.Empty;

        public int TextureFilesReferenced { get; set; }
        public int TexturesCopied { get; set; }

        public string FinalStatus { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
    }
}