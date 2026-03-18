namespace BeamNGTextureFixer.Models
{
    public sealed class SecondPassRow
    {
        public string MaterialName { get; set; } = string.Empty;
        public int ReferenceCount { get; set; }
        public string Status { get; set; } = string.Empty;
        public string DefinitionPath { get; set; } = string.Empty;
        public string DefinitionOrigin { get; set; } = string.Empty;
        public string ColorMap { get; set; } = string.Empty;
        public string NormalMap { get; set; } = string.Empty;
        public string SpecularMap { get; set; } = string.Empty;
        public string GeneratedDefinition { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
    }
}