using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace BeamNGTextureFixer.Helpers
{
    public static class PathHelpers
    {
        public static string NormalizePath(string? p)
        {
            p ??= string.Empty;
            p = p.Trim().Replace("\\", "/");

            if (p.StartsWith("game:", StringComparison.OrdinalIgnoreCase))
                p = p.Substring(5);

            while (p.StartsWith("/"))
                p = p[1..];

            return p;
        }

        public static string Basename(string? p)
        {
            return Path.GetFileName(NormalizePath(p));
        }

        public static string StripZipExt(string name)
        {
            return Regex.Replace(name ?? string.Empty, @"\.zip$", "", RegexOptions.IgnoreCase);
        }

        public static string SanitizeModStem(string name)
        {
            name = StripZipExt(Path.GetFileName(name)).ToLowerInvariant();
            name = Regex.Replace(name, @"[^a-z0-9]+", "_");
            name = name.Trim('_');
            return string.IsNullOrWhiteSpace(name) ? "mod" : name;
        }

        public static string NormalizeNameOnly(string? p)
        {
            var name = Path.GetFileName(NormalizePath(p));
            var stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
            var ext = Path.GetExtension(name).ToLowerInvariant();

            stem = stem.Replace(".normal", "");
            stem = stem.Replace(".data", "");
            stem = Regex.Replace(stem, @"__+", "_");
            stem = Regex.Replace(stem, @"[^a-z0-9_]+", "_");

            var aliasMap = new Dictionary<string, string>
            {
                ["_nm"] = "_n",
                ["_normal"] = "_n",
                ["_metallic"] = "_m",
                ["_roughness"] = "_r",
            };

            foreach (var kv in aliasMap)
            {
                if (stem.EndsWith(kv.Key))
                {
                    stem = stem[..^kv.Key.Length] + kv.Value;
                    break;
                }
            }

            return $"{stem}{ext}";
        }
    }
}