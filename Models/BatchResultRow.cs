using BeamNGTextureFixer.Services;
using System;
using System.Collections.Generic;


namespace BeamNGTextureFixer.Models
{
    public class BatchResultRow
    {
        public string ModZip { get; set; } = "";
        public string ModName { get; set; } = "";
        public int MaterialFiles { get; set; }
        public int TextureRefs { get; set; }
        public int PresentInMod { get; set; }
        public int SatisfiedByCurrent { get; set; }
        public int ResolvedFromOld { get; set; }
        public int Unresolved { get; set; }
        public string BuildStatus { get; set; } = "not built";
        public int FixesMade { get; set; }
        public string FixSummary => $"{ResolvedFromOld} / {FixesMade}";
        public string OutZip { get; set; } = "";
        public List<DetailRow> DetailRows { get; set; } = new();
        public BeamNGFixerService? Service { get; set; }
        public bool IsAborted => string.Equals(BuildStatus, "aborted", StringComparison.OrdinalIgnoreCase);
    }
}