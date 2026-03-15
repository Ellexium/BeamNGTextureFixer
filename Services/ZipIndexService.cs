using System.Collections.Generic;

namespace BeamNGTextureFixer.Services
{
    public class ZipIndexService
    {
        public Dictionary<string, List<(string ZipPath, string InternalPath)>> Exact { get; } = new();
        public Dictionary<string, List<(string ZipPath, string InternalPath)>> ByBase { get; } = new();
        public Dictionary<string, List<(string ZipPath, string InternalPath)>> ByNormName { get; } = new();
        public Dictionary<string, List<(string ZipPath, string InternalPath)>> SamePathOtherExt { get; } = new();
    }
}