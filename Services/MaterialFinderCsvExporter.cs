using BeamNGTextureFixer.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BeamNGTextureFixer.Services
{
    public static class MaterialFinderCsvExporter
    {
        public static string Export(MaterialFinderResult result, string modZipPath)
        {
            var csvPath = Path.Combine(
                Path.GetDirectoryName(modZipPath) ?? "",
                Path.GetFileNameWithoutExtension(modZipPath) + " - material finder report.csv");

            using var writer = new StreamWriter(csvPath, false, new UTF8Encoding(true));

            WriteCsvRow(writer, new[]
            {
        "RowType",
        "MaterialName",
        "IssueType",
        "ReferenceCount",
        "Origin",
        "SourceKind",
        "SourceArchivePath",
        "SourcePath",
        "ReferencedByArchivePath",
        "ReferencedByFile",
        "PropertyName",
        "TextureKey",
        "TextureOriginalValue",
        "TextureNormalizedValue",
        "TextureStatus",
        "TextureMatchType",
        "MatchedAssetOrigin",
        "MatchedAssetArchivePath",
        "MatchedAssetInternalPath",
        "TraceClassification",
        "TraceDetail",
        "TraceDefinitionCount",
        "TraceReferenceCount",
        "DefinitionPrimaryOrigin",
        "DefinitionPrimarySourceKind",
        "DefinitionPrimaryArchivePath",
        "DefinitionPrimaryPath",
        "DefinitionAllLocations",
        "StatusExplanation",
        "TextureExplanation"
    });

            WriteCsvRow(writer, new[]
            {
        "Summary",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        $"Definitions={result.Stats.DefinitionCount}",
        $"References={result.Stats.ReferenceCount}",
        $"Issues={result.Stats.IssueCount}",
        $"ReferencedButUndefined={result.Stats.ReferencedButUndefinedCount}",
        $"DefinedButBroken={result.Stats.DefinedButBrokenCount}",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "",
        "Summary of the material finder scan.",
        ""
    });

            foreach (var trace in result.TraceRows
                .OrderBy(x => x.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.InternalPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.RowType, StringComparer.OrdinalIgnoreCase))
            {
                WriteCsvRow(writer, new[]
                {
            trace.RowType,
            "",
            "",
            "",
            trace.Origin,
            "",
            trace.ArchivePath,
            trace.InternalPath,
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            trace.Classification,
            trace.Detail,
            trace.DefinitionCount.ToString(),
            trace.ReferenceCount.ToString(),
            "",
            "",
            "",
            "",
            "",
            "Trace row showing what the scanner looked at or did.",
            ""
        });
            }

            foreach (var definition in result.Definitions.OrderBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase))
            {
                var primaryDef = definition;
                var allLocations = FormatDefinitionLocations(new List<MaterialDefinitionRecord> { definition });

                if (definition.DependencyChecks.Count == 0)
                {
                    WriteCsvRow(writer, new[]
                    {
                "Definition",
                definition.MaterialName,
                "",
                "",
                definition.Origin,
                definition.SourceKind,
                definition.SourceArchivePath,
                definition.SourcePath,
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                primaryDef.Origin,
                primaryDef.SourceKind,
                primaryDef.SourceArchivePath,
                primaryDef.SourcePath,
                allLocations,
                ExplainIssueStatus("Definition", "", 0),
                ""
            });
                }
                else
                {
                    foreach (var check in definition.DependencyChecks)
                    {
                        WriteCsvRow(writer, new[]
                        {
                    "DefinitionTexture",
                    definition.MaterialName,
                    "",
                    "",
                    definition.Origin,
                    definition.SourceKind,
                    definition.SourceArchivePath,
                    definition.SourcePath,
                    "",
                    "",
                    "",
                    check.Texture.Key,
                    check.Texture.OriginalValue,
                    check.Texture.NormalizedValue,
                    check.Status,
                    check.MatchType,
                    check.Asset?.Origin ?? "",
                    check.Asset?.ArchivePath ?? "",
                    check.Asset?.InternalPath ?? "",
                    "",
                    "",
                    "",
                    "",
                    primaryDef.Origin,
                    primaryDef.SourceKind,
                    primaryDef.SourceArchivePath,
                    primaryDef.SourcePath,
                    allLocations,
                    ExplainIssueStatus("DefinitionTexture", "", 0),
                    ExplainTextureStatus(
                        check.Status,
                        check.MatchType,
                        check.Texture.OriginalValue,
                        check.Asset?.InternalPath ?? "")
                });
                    }
                }
            }

            foreach (var reference in result.References.OrderBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase))
            {
                var matchingDefs = result.Definitions
                    .Where(d => d.MaterialName.Equals(reference.MaterialName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var primaryDef = PickPrimaryDefinition(matchingDefs);
                var allLocations = FormatDefinitionLocations(matchingDefs);

                WriteCsvRow(writer, new[]
                {
            "Reference",
            reference.MaterialName,
            "",
            "",
            reference.Origin,
            "",
            "",
            "",
            reference.ReferencedByArchivePath,
            reference.ReferencedByFile,
            reference.PropertyName,
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            primaryDef?.Origin ?? "",
            primaryDef?.SourceKind ?? "",
            primaryDef?.SourceArchivePath ?? "",
            primaryDef?.SourcePath ?? "",
            allLocations,
            ExplainIssueStatus("Reference", "", 0),
            ""
        });
            }

            foreach (var issue in result.Issues.OrderBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase))
            {
                var primaryDef = PickPrimaryDefinition(issue.Definitions);
                var allLocations = FormatDefinitionLocations(issue.Definitions);

                if (issue.References.Count == 0 && issue.Definitions.Count == 0)
                {
                    WriteCsvRow(writer, new[]
                    {
                "Issue",
                issue.MaterialName,
                issue.IssueType,
                issue.ReferenceCount.ToString(),
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                ExplainIssueStatus("Issue", issue.IssueType, issue.ReferenceCount),
                ""
            });
                }
                else
                {
                    foreach (var reference in issue.References.DefaultIfEmpty())
                    {
                        WriteCsvRow(writer, new[]
                        {
                    "Issue",
                    issue.MaterialName,
                    issue.IssueType,
                    issue.ReferenceCount.ToString(),
                    reference?.Origin ?? "",
                    "",
                    "",
                    "",
                    reference?.ReferencedByArchivePath ?? "",
                    reference?.ReferencedByFile ?? "",
                    reference?.PropertyName ?? "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    primaryDef?.Origin ?? "",
                    primaryDef?.SourceKind ?? "",
                    primaryDef?.SourceArchivePath ?? "",
                    primaryDef?.SourcePath ?? "",
                    allLocations,
                    ExplainIssueStatus("Issue", issue.IssueType, issue.ReferenceCount),
                    ""
                });
                    }
                }
            }

            return csvPath;
        }

        private static MaterialDefinitionRecord? PickPrimaryDefinition(IEnumerable<MaterialDefinitionRecord> definitions)
        {
            return definitions
                .OrderBy(d => DefinitionOriginPriority(d.Origin))
                .ThenBy(d => d.SourceArchivePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.SourcePath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static int DefinitionOriginPriority(string? origin)
        {
            return origin switch
            {
                "mod" => 0,
                "current_content" => 1,
                "old_content" => 2,
                _ => 9
            };
        }

        private static string FormatDefinitionLocations(IEnumerable<MaterialDefinitionRecord> definitions)
        {
            return string.Join(" | ",
                definitions
                    .OrderBy(d => DefinitionOriginPriority(d.Origin))
                    .ThenBy(d => d.SourceArchivePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .Select(d => $"{d.Origin} :: {d.SourceKind} :: {d.SourceArchivePath} :: {d.SourcePath}")
                    .Distinct(StringComparer.OrdinalIgnoreCase));
        }
        private static void WriteCsvRow(StreamWriter writer, IEnumerable<string?> values)
        {
            writer.WriteLine(string.Join(",", values.Select(CsvEscape)));
        }

        private static string CsvEscape(string? value)
        {
            value ??= string.Empty;

            if (value.Contains('"'))
                value = value.Replace("\"", "\"\"");

            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return $"\"{value}\"";

            return value;
        }

        private static string ExplainIssueStatus(string rowType, string issueType, int referenceCount)
        {
            if (rowType.Equals("Summary", StringComparison.OrdinalIgnoreCase))
                return "Summary of the material finder scan.";

            return issueType switch
            {
                "defined_ok" => "Material is referenced and a definition was found. Its requested texture files were resolved.",
                "defined_but_broken" => "Material definition was found, but at least one texture file requested by the material could not be resolved.",
                "referenced_but_undefined" => $"Material is referenced {referenceCount} time(s), but no definition was found in the scanned sources.",
                "defined_but_unreferenced" => "Material definition was found, but no references to this material were found in the scanned sources.",
                "defined_but_unreferenced_and_broken" => "Material definition was found and is not referenced, and at least one requested texture file could not be resolved.",
                _ when rowType.Equals("Reference", StringComparison.OrdinalIgnoreCase) =>
                    "This row shows a place where a material name is being used by another file.",
                _ when rowType.Equals("Definition", StringComparison.OrdinalIgnoreCase) =>
                    "This row shows a material definition file that defines the material.",
                _ when rowType.Equals("DefinitionTexture", StringComparison.OrdinalIgnoreCase) =>
                    "This row shows one texture/file requested by a material definition.",
                _ when rowType.Equals("Issue", StringComparison.OrdinalIgnoreCase) =>
                    "This row summarizes the material-level issue found during correlation.",
                _ when rowType.StartsWith("Reference", StringComparison.OrdinalIgnoreCase) =>
                    "This row comes from a file that references a material name.",
                _ when rowType.Contains("Definition", StringComparison.OrdinalIgnoreCase) =>
                    "This row comes from a file that defines a material.",
                _ => ""
            };
        }

        private static string ExplainTextureStatus(string textureStatus, string textureMatchType, string textureOriginalValue, string matchedAssetInternalPath)
        {
            if (string.IsNullOrWhiteSpace(textureOriginalValue))
                return "";

            if (string.Equals(textureStatus, "missing", StringComparison.OrdinalIgnoreCase))
            {
                return $"The material asks for '{textureOriginalValue}', but no matching file was found in the scanned sources.";
            }

            return textureMatchType switch
            {
                "exact_path" => $"The material asks for '{textureOriginalValue}', and that exact file path was found.",
                "same_path_other_ext" => $"The material asks for '{textureOriginalValue}', and a file with the same path but a different extension was found: '{matchedAssetInternalPath}'.",
                "same_basename" => $"The material asks for '{textureOriginalValue}', and a file with the same basename was found: '{matchedAssetInternalPath}'.",
                "normalized_name" => $"The material asks for '{textureOriginalValue}', and a normalized-name match was found: '{matchedAssetInternalPath}'.",
                _ => $"The material asks for '{textureOriginalValue}', and it resolved using match type '{textureMatchType}'."
            };
        }

        public sealed class MaterialScanTraceRow
        {
            public string RowType { get; set; } = string.Empty;          // SourceArchive, ScannedEntry, DefinitionFound, ReferenceFound, SkippedEntry
            public string Origin { get; set; } = string.Empty;           // mod, current_content, old_content
            public string ArchivePath { get; set; } = string.Empty;      // zip path on disk
            public string InternalPath { get; set; } = string.Empty;     // file path inside zip
            public string Classification { get; set; } = string.Empty;   // definition_file, reference_file, text_candidate, asset, skipped_binary, skipped_nontext, etc
            public string Detail { get; set; } = string.Empty;           // freeform note
            public int DefinitionCount { get; set; }
            public int ReferenceCount { get; set; }
        }
    }
}