using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public static string NormalizeNameOnly2(string? p)
        {
            var name = Path.GetFileName(NormalizePath(p));
            var stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();

            // Normalize separators
            stem = stem.Replace('\\', '_')
                       .Replace('/', '_')
                       .Replace('-', '_')
                       .Replace(' ', '_');

            // Remove inserted descriptors
            stem = stem.Replace(".color", "")
                       .Replace(".normal", "")
                       .Replace(".data", "")
                       .Replace(".specular", "")
                       .Replace(".roughness", "")
                       .Replace(".metallic", "")
                       .Replace(".opacity", "")
                       .Replace(".alpha", "")
                       .Replace(".ao", "")
                       .Replace(".occlusion", "");

            stem = Regex.Replace(stem, @"[^a-z0-9_]+", "_");
            stem = Regex.Replace(stem, @"__+", "_").Trim('_');

            // Canonical suffix mapping
            var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["_nm"] = "_n",
                ["_normal"] = "_n",
                ["_normals"] = "_n",

                ["_diffuse"] = "_d",
                ["_albedo"] = "_d",
                ["_basecolor"] = "_d",
                ["_base_color"] = "_d",
                ["_color"] = "_d",
                ["_col"] = "_d",

                ["_spec"] = "_s",
                ["_specular"] = "_s",

                ["_rough"] = "_r",
                ["_roughness"] = "_r",

                ["_metal"] = "_m",
                ["_metallic"] = "_m",

                ["_occlusion"] = "_ao",
                ["_ambientocclusion"] = "_ao",
                ["_ambient_occlusion"] = "_ao",

                ["_opacity"] = "_o",
                ["_alpha"] = "_o",
            };

            bool changed;
            do
            {
                changed = false;

                foreach (var kv in aliasMap.OrderByDescending(x => x.Key.Length))
                {
                    if (stem.EndsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        stem = stem[..^kv.Key.Length] + kv.Value;
                        changed = true;
                        break;
                    }
                }

                stem = Regex.Replace(stem, @"__+", "_").Trim('_');
            }
            while (changed);

            // --- FUZZY LAYER STARTS HERE ---

            // Extract semantic role (_d, _n, etc.)
            string role = "";
            if (stem.EndsWith("_ao"))
            {
                role = "_ao";
                stem = stem[..^3];
            }
            else if (stem.Length > 2 && stem[^2] == '_')
            {
                role = stem[^2..];
                stem = stem[..^2];
            }

            // Remove digits (ibishurx3 -> ibishurx)
            stem = Regex.Replace(stem, @"\d+", "");

            // Remove single-letter segments like _a_ or _b_
            stem = Regex.Replace(stem, @"(?<=_)[a-z](?=_)", "");

            // Remove underscores entirely for broader matching
            stem = stem.Replace("_", "");

            // Final cleanup
            stem = Regex.Replace(stem, @"[^a-z0-9]+", "");

            return $"{stem}{role}";
        }

    }
}