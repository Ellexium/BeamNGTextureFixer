using BeamNGTextureFixer.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace BeamNGTextureFixer.Services
{
    public sealed class GeneratedMaterialDefinitionService
    {
        private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".dds", ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".gif", ".webp"
        };

        private static readonly string[] StripSuffixes =
        {
            "_mat",
            "_material",
            "_materials",
            "material",
            "materials"
        };

        private const string NullNormalBaseName = "null_n.dds";

        public GeneratedMaterialDefinitionResult BuildSuggestions(
            GeneratedMaterialDefinitionRequest request,
            MaterialFinderResult materialFinderResult,
            CancellationToken token = default)
        {
            if (request is null)
                throw new ArgumentNullException(nameof(request));
            if (materialFinderResult is null)
                throw new ArgumentNullException(nameof(materialFinderResult));
            if (string.IsNullOrWhiteSpace(request.ModZipPath))
                throw new ArgumentException("ModZipPath is required.", nameof(request));
            if (!File.Exists(request.ModZipPath))
                throw new FileNotFoundException("Target mod zip was not found.", request.ModZipPath);

            var result = new GeneratedMaterialDefinitionResult
            {
                Request = request
            };

            var assets = ScanAssets(request, result.TraceRows, token);
            var nullNormalFallback = FindNullNormalFallback(assets, result.TraceRows);

            foreach (var issue in materialFinderResult.Issues
                         .Where(x => x.IssueType.Equals("referenced_but_undefined", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();

                var candidate = BuildCandidateForMissingMaterial(issue, assets, nullNormalFallback);
                result.Candidates.Add(candidate);
            }

            result.GeneratedJsonText = BuildGeneratedMaterialsJson(result.Candidates);
            result.Stats = BuildStats(result);
            return result;
        }

        public string ExportCsv(GeneratedMaterialDefinitionResult result, string modZipPath)
        {
            var csvPath = Path.Combine(
                Path.GetDirectoryName(modZipPath) ?? "",
                Path.GetFileNameWithoutExtension(modZipPath) + " - generated material candidates.csv");

            using var writer = new StreamWriter(csvPath, false, new UTF8Encoding(true));

            WriteCsvRow(writer, new[]
            {
                "RowType",
                "MaterialName",
                "ReferenceCount",
                "GenerationStatus",
                "ConfidenceScore",
                "ConfidenceBand",
                "TargetDefinitionFile",
                "ChosenColor",
                "ChosenNormal",
                "ChosenSpecular",
                "ChosenRoughness",
                "ChosenMetallic",
                "ChosenAo",
                "ChosenOpacity",
                "ReferenceSummary",
                "GenerationExplanation",
                "Slot",
                "CandidateAssetPath",
                "CandidateAssetOrigin",
                "CandidateArchivePath",
                "CandidateScore",
                "CandidateReason"
            });

            WriteCsvRow(writer, new[]
            {
                "Summary",
                "",
                result.Stats.ReferencedButUndefinedCount.ToString(),
                "",
                "",
                "",
                "tfgenerated.materials.json",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                "",
                $"Generated material suggestion scan completed. Buildable={result.Stats.BuildableCount}, Partial={result.Stats.PartialCount}, NoSuggestion={result.Stats.NoSuggestionCount}",
                "",
                "",
                "",
                "",
                "",
                ""
            });

            foreach (var trace in result.TraceRows
                         .OrderBy(x => x.ArchivePath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.InternalPath, StringComparer.OrdinalIgnoreCase))
            {
                WriteCsvRow(writer, new[]
                {
                    "Trace",
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
                    trace.Detail,
                    "",
                    trace.InternalPath,
                    trace.Origin,
                    trace.ArchivePath,
                    "",
                    trace.Classification
                });
            }

            foreach (var candidate in result.Candidates.OrderBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase))
            {
                WriteCsvRow(writer, new[]
                {
                    "Candidate",
                    candidate.MaterialName,
                    candidate.ReferenceCount.ToString(),
                    candidate.GenerationStatus,
                    candidate.ConfidenceScore.ToString(),
                    candidate.ConfidenceBand,
                    "tfgenerated.materials.json",
                    candidate.ChosenColor?.InternalPath ?? "",
                    candidate.ChosenNormal?.InternalPath ?? "",
                    candidate.ChosenSpecular?.InternalPath ?? "",
                    candidate.ChosenRoughness?.InternalPath ?? "",
                    candidate.ChosenMetallic?.InternalPath ?? "",
                    candidate.ChosenAo?.InternalPath ?? "",
                    candidate.ChosenOpacity?.InternalPath ?? "",
                    candidate.ReferenceSummary,
                    candidate.GenerationExplanation,
                    "",
                    "",
                    "",
                    "",
                    "",
                    ""
                });

                foreach (var slot in candidate.Slots)
                {
                    if (slot.Value is null)
                        continue;

                    WriteCsvRow(writer, new[]
                    {
                        "ChosenSlot",
                        candidate.MaterialName,
                        candidate.ReferenceCount.ToString(),
                        candidate.GenerationStatus,
                        candidate.ConfidenceScore.ToString(),
                        candidate.ConfidenceBand,
                        "tfgenerated.materials.json",
                        candidate.ChosenColor?.InternalPath ?? "",
                        candidate.ChosenNormal?.InternalPath ?? "",
                        candidate.ChosenSpecular?.InternalPath ?? "",
                        candidate.ChosenRoughness?.InternalPath ?? "",
                        candidate.ChosenMetallic?.InternalPath ?? "",
                        candidate.ChosenAo?.InternalPath ?? "",
                        candidate.ChosenOpacity?.InternalPath ?? "",
                        candidate.ReferenceSummary,
                        candidate.GenerationExplanation,
                        slot.Key,
                        slot.Value.InternalPath,
                        slot.Value.Origin,
                        slot.Value.ArchivePath,
                        slot.Value.Score.ToString(),
                        slot.Value.Reason
                    });
                }
            }

            return csvPath;
        }

        public string ExportGeneratedJsonPreview(GeneratedMaterialDefinitionResult result, string modZipPath)
        {
            var previewPath = Path.Combine(
                Path.GetDirectoryName(modZipPath) ?? "",
                Path.GetFileNameWithoutExtension(modZipPath) + " - tfgenerated.materials.json");

            File.WriteAllText(previewPath, result.GeneratedJsonText, new UTF8Encoding(true));
            return previewPath;
        }

        private List<GeneratedAssetRecord> ScanAssets(
            GeneratedMaterialDefinitionRequest request,
            List<GeneratedMaterialTraceRow> traceRows,
            CancellationToken token)
        {
            var assets = new List<GeneratedAssetRecord>();

            var sourceSpecs = new List<GeneratedSourceSpec>
            {
                new() { Origin = "mod", ArchivePath = request.ModZipPath }
            };

            if (!string.IsNullOrWhiteSpace(request.CurrentFolder) && Directory.Exists(request.CurrentFolder))
            {
                foreach (var zip in Directory.EnumerateFiles(request.CurrentFolder, "*.zip", SearchOption.AllDirectories))
                {
                    sourceSpecs.Add(new GeneratedSourceSpec
                    {
                        Origin = "current_content",
                        ArchivePath = zip
                    });
                }
            }

            if (!string.IsNullOrWhiteSpace(request.OldFolder) && Directory.Exists(request.OldFolder))
            {
                foreach (var zip in Directory.EnumerateFiles(request.OldFolder, "*.zip", SearchOption.AllDirectories))
                {
                    sourceSpecs.Add(new GeneratedSourceSpec
                    {
                        Origin = "old_content",
                        ArchivePath = zip
                    });
                }
            }

            foreach (var source in sourceSpecs)
            {
                token.ThrowIfCancellationRequested();

                traceRows.Add(new GeneratedMaterialTraceRow
                {
                    Origin = source.Origin,
                    ArchivePath = source.ArchivePath,
                    InternalPath = "",
                    Classification = "source_archive",
                    Detail = $"Scanning asset source {Path.GetFileName(source.ArchivePath)}"
                });

                try
                {
                    using var archive = ZipFile.OpenRead(source.ArchivePath);

                    foreach (var entry in archive.Entries)
                    {
                        token.ThrowIfCancellationRequested();

                        if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/"))
                            continue;

                        var internalPath = PathHelpers.NormalizePath(entry.FullName);
                        var ext = Path.GetExtension(internalPath);

                        if (!AssetExtensions.Contains(ext))
                            continue;

                        var stem = Path.ChangeExtension(PathHelpers.Basename(internalPath), null)?.TrimEnd('.') ?? "";
                        var normStem = NormalizeStem(stem);
                        var tokens = Tokenize(normStem);

                        assets.Add(new GeneratedAssetRecord
                        {
                            Origin = source.Origin,
                            ArchivePath = source.ArchivePath,
                            InternalPath = internalPath,
                            FileName = Path.GetFileName(internalPath),
                            BasenameStem = stem,
                            NormalizedStem = normStem,
                            Tokens = tokens
                        });
                    }
                }
                catch (Exception ex)
                {
                    traceRows.Add(new GeneratedMaterialTraceRow
                    {
                        Origin = source.Origin,
                        ArchivePath = source.ArchivePath,
                        InternalPath = "",
                        Classification = "source_error",
                        Detail = ex.Message
                    });
                }
            }

            return assets;
        }

        private GeneratedChosenAsset? FindNullNormalFallback(
            List<GeneratedAssetRecord> assets,
            List<GeneratedMaterialTraceRow> traceRows)
        {
            var candidates = assets
                .Where(x => x.FileName.Equals(NullNormalBaseName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => OriginPriority(x.Origin))
                .ThenBy(x => x.InternalPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var chosen = candidates.FirstOrDefault();
            if (chosen is null)
            {
                traceRows.Add(new GeneratedMaterialTraceRow
                {
                    Origin = "",
                    ArchivePath = "",
                    InternalPath = "",
                    Classification = "null_normal_missing",
                    Detail = "No real null_n.dds fallback was found in scanned current-content or old-content sources."
                });

                return null;
            }

            traceRows.Add(new GeneratedMaterialTraceRow
            {
                Origin = chosen.Origin,
                ArchivePath = chosen.ArchivePath,
                InternalPath = chosen.InternalPath,
                Classification = "null_normal_selected",
                Detail = $"Selected real null_n.dds fallback from {chosen.Origin}."
            });

            return new GeneratedChosenAsset
            {
                Origin = chosen.Origin,
                ArchivePath = chosen.ArchivePath,
                InternalPath = chosen.InternalPath,
                Score = 1000,
                Reason = "Real null_n.dds fallback selected from scanned content."
            };
        }

        private GeneratedMaterialCandidate BuildCandidateForMissingMaterial(
            MaterialIssueRecord issue,
            List<GeneratedAssetRecord> assets,
            GeneratedChosenAsset? nullNormalFallback)
        {
            var candidate = new GeneratedMaterialCandidate
            {
                MaterialName = issue.MaterialName,
                ReferenceCount = issue.ReferenceCount,
                ReferenceSummary = BuildReferenceSummary(issue)
            };

            var seedStems = BuildSeedStems(issue.MaterialName);

            candidate.ChosenColor = PickBestForSlot(seedStems, SlotKind.Color, assets);
            candidate.ChosenNormal = PickBestForSlot(seedStems, SlotKind.Normal, assets);
            candidate.ChosenSpecular = PickBestForSlot(seedStems, SlotKind.Specular, assets);
            candidate.ChosenRoughness = PickBestForSlot(seedStems, SlotKind.Roughness, assets);
            candidate.ChosenMetallic = PickBestForSlot(seedStems, SlotKind.Metallic, assets);
            candidate.ChosenAo = PickBestForSlot(seedStems, SlotKind.Ao, assets);
            candidate.ChosenOpacity = PickBestForSlot(seedStems, SlotKind.Opacity, assets);

            var hasColor = candidate.ChosenColor is not null;
            var hasSupporting = candidate.ChosenNormal is not null || candidate.ChosenSpecular is not null;

            candidate.ConfidenceScore =
                (candidate.ChosenColor?.Score ?? 0) +
                (candidate.ChosenNormal?.Score ?? 0) / 2 +
                (candidate.ChosenSpecular?.Score ?? 0) / 3 +
                (candidate.ChosenRoughness?.Score ?? 0) / 4 +
                (candidate.ChosenAo?.Score ?? 0) / 5;

            if (hasColor && hasSupporting)
            {
                candidate.GenerationStatus = "buildable_generated_candidate";
                candidate.ConfidenceBand = candidate.ConfidenceScore >= 240 ? "high" : "medium";
                candidate.GenerationExplanation =
                    "A credible color texture and at least one supporting lighting texture were found. A generated material definition is reasonable.";
            }
            else if (hasColor)
            {
                candidate.GenerationStatus = "partial_generated_candidate";
                candidate.ConfidenceBand = "low";
                candidate.GenerationExplanation =
                    "Only a credible color texture was found. A minimal generated material definition is possible, but lighting may not match the intended original material.";
            }
            else
            {
                candidate.GenerationStatus = "no_generated_candidate";
                candidate.ConfidenceBand = "none";
                candidate.GenerationExplanation =
                    "No sufficiently strong color texture candidate was found, so no generated material definition is recommended.";
            }

            candidate.Slots["colorMap"] = candidate.ChosenColor;

            if (candidate.ChosenNormal is not null)
            {
                candidate.Slots["normalMap"] = candidate.ChosenNormal;
            }
            else if ((candidate.GenerationStatus == "buildable_generated_candidate" ||
                      candidate.GenerationStatus == "partial_generated_candidate") &&
                     nullNormalFallback is not null)
            {
                candidate.Slots["normalMap"] = new GeneratedChosenAsset
                {
                    Origin = nullNormalFallback.Origin,
                    ArchivePath = nullNormalFallback.ArchivePath,
                    InternalPath = nullNormalFallback.InternalPath,
                    Score = nullNormalFallback.Score,
                    Reason = "No real normal candidate found. Using scanned null_n.dds fallback."
                };

                candidate.ChosenNormal = candidate.Slots["normalMap"];
                candidate.GenerationExplanation += " No real normal candidate was found, so a scanned null_n.dds fallback was used.";
            }
            else if (candidate.GenerationStatus == "buildable_generated_candidate" ||
                     candidate.GenerationStatus == "partial_generated_candidate")
            {
                candidate.GenerationExplanation += " No real normal candidate and no scanned null_n.dds fallback were found, so normalMap was omitted.";
            }

            if (candidate.ChosenSpecular is not null)
                candidate.Slots["specularMap"] = candidate.ChosenSpecular;

            if (candidate.ChosenRoughness is not null)
                candidate.Slots["roughnessMap"] = candidate.ChosenRoughness;

            if (candidate.ChosenMetallic is not null)
                candidate.Slots["metallicMap"] = candidate.ChosenMetallic;

            if (candidate.ChosenAo is not null)
                candidate.Slots["ambientOcclusionMap"] = candidate.ChosenAo;

            if (candidate.ChosenOpacity is not null)
                candidate.Slots["opacityMap"] = candidate.ChosenOpacity;

            return candidate;
        }

        private static int OriginPriority(string origin)
        {
            return origin switch
            {
                "current_content" => 0,
                "old_content" => 1,
                "mod" => 2,
                _ => 9
            };
        }

        private static string BuildReferenceSummary(MaterialIssueRecord issue)
        {
            if (issue.References.Count == 0)
                return "No reference rows were attached.";

            var grouped = issue.References
                .GroupBy(x => x.PropertyName, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"{g.Key} x{g.Count()}")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

            return string.Join("; ", grouped);
        }

        private static List<string> BuildSeedStems(string materialName)
        {
            var seeds = new List<string>();
            var current = materialName.Trim();

            if (!string.IsNullOrWhiteSpace(current))
                seeds.Add(NormalizeStem(current));

            foreach (var suffix in StripSuffixes)
            {
                if (current.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    var stripped = current[..^suffix.Length].Trim('_', '-', ' ');
                    if (!string.IsNullOrWhiteSpace(stripped))
                        seeds.Add(NormalizeStem(stripped));
                }
            }

            return seeds
                .Select(x => x.Trim('_'))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private GeneratedChosenAsset? PickBestForSlot(
            List<string> seedStems,
            SlotKind slotKind,
            List<GeneratedAssetRecord> assets)
        {
            GeneratedChosenAsset? best = null;

            foreach (var asset in assets)
            {
                foreach (var seed in seedStems)
                {
                    var scoreResult = ScoreAssetForSlot(seed, slotKind, asset);
                    if (scoreResult.Score <= 0)
                        continue;

                    if (best is null || scoreResult.Score > best.Score)
                    {
                        best = new GeneratedChosenAsset
                        {
                            Origin = asset.Origin,
                            ArchivePath = asset.ArchivePath,
                            InternalPath = asset.InternalPath,
                            Score = scoreResult.Score,
                            Reason = scoreResult.Reason
                        };
                    }
                }
            }

            return best;
        }

        private static (int Score, string Reason) ScoreAssetForSlot(string seed, SlotKind slotKind, GeneratedAssetRecord asset)
        {
            var score = 0;
            var reasons = new List<string>();

            var suffixes = GetSuffixesForSlot(slotKind);
            var normalizedSeed = NormalizeStem(seed);
            var normalizedAsset = asset.NormalizedStem;

            if (normalizedAsset.Equals(normalizedSeed, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
                reasons.Add("exact stem match");
            }

            if (normalizedAsset.StartsWith(normalizedSeed + "_", StringComparison.OrdinalIgnoreCase))
            {
                score += 35;
                reasons.Add("stem prefix match");
            }

            foreach (var suffix in suffixes)
            {
                var exactWanted = normalizedSeed + suffix;

                if (normalizedAsset.Equals(exactWanted, StringComparison.OrdinalIgnoreCase))
                {
                    score += 220;
                    reasons.Add($"exact slot suffix match {suffix}");
                }
                else if (normalizedAsset.StartsWith(exactWanted + "_", StringComparison.OrdinalIgnoreCase))
                {
                    score += 170;
                    reasons.Add($"slot suffix prefix match {suffix}");
                }
                else if (normalizedAsset.Contains(exactWanted, StringComparison.OrdinalIgnoreCase))
                {
                    score += 120;
                    reasons.Add($"slot suffix contained {suffix}");
                }
            }

            var seedTokens = Tokenize(normalizedSeed);
            var tokenMatches = seedTokens.Intersect(asset.Tokens, StringComparer.OrdinalIgnoreCase).Count();
            if (tokenMatches > 0)
            {
                score += tokenMatches * 10;
                reasons.Add($"token overlap {tokenMatches}");
            }

            if (slotKind == SlotKind.Color && HasGenericColorMarker(normalizedAsset))
            {
                score += 20;
                reasons.Add("generic color marker");
            }

            if (slotKind == SlotKind.Normal && HasGenericNormalMarker(normalizedAsset))
            {
                score += 20;
                reasons.Add("generic normal marker");
            }

            if (slotKind == SlotKind.Specular && HasGenericSpecularMarker(normalizedAsset))
            {
                score += 20;
                reasons.Add("generic specular marker");
            }

            if (slotKind == SlotKind.Roughness && HasGenericRoughnessMarker(normalizedAsset))
            {
                score += 20;
                reasons.Add("generic roughness marker");
            }

            if (slotKind == SlotKind.Metallic && HasGenericMetallicMarker(normalizedAsset))
            {
                score += 20;
                reasons.Add("generic metallic marker");
            }

            if (slotKind == SlotKind.Ao && HasGenericAoMarker(normalizedAsset))
            {
                score += 20;
                reasons.Add("generic AO marker");
            }

            if (slotKind == SlotKind.Opacity && HasGenericOpacityMarker(normalizedAsset))
            {
                score += 20;
                reasons.Add("generic opacity marker");
            }

            if (slotKind != SlotKind.Color && HasGenericColorMarker(normalizedAsset))
            {
                score -= 120;
                reasons.Add("penalty: looks like color texture for non-color slot");
            }

            if (slotKind != SlotKind.Normal && HasGenericNormalMarker(normalizedAsset))
            {
                score -= 120;
                reasons.Add("penalty: looks like normal texture for non-normal slot");
            }

            if (slotKind != SlotKind.Specular && HasGenericSpecularMarker(normalizedAsset))
            {
                score -= 70;
                reasons.Add("penalty: looks like specular texture for non-specular slot");
            }

            if (slotKind != SlotKind.Roughness && HasGenericRoughnessMarker(normalizedAsset))
            {
                score -= 70;
                reasons.Add("penalty: looks like roughness texture for non-roughness slot");
            }

            if (slotKind != SlotKind.Metallic && HasGenericMetallicMarker(normalizedAsset))
            {
                score -= 70;
                reasons.Add("penalty: looks like metallic texture for non-metallic slot");
            }

            if (slotKind != SlotKind.Ao && HasGenericAoMarker(normalizedAsset))
            {
                score -= 60;
                reasons.Add("penalty: looks like AO texture for non-AO slot");
            }

            if (slotKind != SlotKind.Opacity && HasGenericOpacityMarker(normalizedAsset))
            {
                score -= 60;
                reasons.Add("penalty: looks like opacity texture for non-opacity slot");
            }

            var minimum = slotKind switch
            {
                SlotKind.Color => 90,
                SlotKind.Normal => 110,
                SlotKind.Specular => 95,
                SlotKind.Roughness => 95,
                SlotKind.Metallic => 95,
                SlotKind.Ao => 90,
                SlotKind.Opacity => 90,
                _ => 999
            };

            if (score < minimum)
                return (0, "");

            return (score, string.Join("; ", reasons));
        }

        private static string[] GetSuffixesForSlot(SlotKind slotKind)
        {
            return slotKind switch
            {
                SlotKind.Color => new[] { "_d", "_diffuse", "_color", "_albedo", "_basecolor", "_b.color" },
                SlotKind.Normal => new[] { "_n", "_normal", "_nm", "_nm.normal" },
                SlotKind.Specular => new[] { "_s", "_spec", "_specular" },
                SlotKind.Roughness => new[] { "_r", "_rough", "_roughness", "_r.data" },
                SlotKind.Metallic => new[] { "_m", "_metal", "_metallic" },
                SlotKind.Ao => new[] { "_ao", "_occlusion", "_ao.data" },
                SlotKind.Opacity => new[] { "_o", "_opacity", "_alpha", "_o.data" },
                _ => Array.Empty<string>()
            };
        }

        private static bool HasGenericColorMarker(string value)
        {
            return value.Contains("_d", StringComparison.OrdinalIgnoreCase)
                || value.Contains("diffuse", StringComparison.OrdinalIgnoreCase)
                || value.Contains("albedo", StringComparison.OrdinalIgnoreCase)
                || value.Contains("basecolor", StringComparison.OrdinalIgnoreCase)
                || value.Contains("_b.color", StringComparison.OrdinalIgnoreCase)
                || value.Contains("_color", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasGenericNormalMarker(string value)
        {
            return value.Contains("_n", StringComparison.OrdinalIgnoreCase)
                || value.Contains("normal", StringComparison.OrdinalIgnoreCase)
                || value.Contains("_nm", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasGenericSpecularMarker(string value)
        {
            return value.Contains("_s", StringComparison.OrdinalIgnoreCase)
                || value.Contains("spec", StringComparison.OrdinalIgnoreCase)
                || value.Contains("specular", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasGenericRoughnessMarker(string value)
        {
            return value.Contains("_r", StringComparison.OrdinalIgnoreCase)
                || value.Contains("rough", StringComparison.OrdinalIgnoreCase)
                || value.Contains("roughness", StringComparison.OrdinalIgnoreCase)
                || value.Contains("_r.data", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasGenericMetallicMarker(string value)
        {
            return value.Contains("_m", StringComparison.OrdinalIgnoreCase)
                || value.Contains("metal", StringComparison.OrdinalIgnoreCase)
                || value.Contains("metallic", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasGenericAoMarker(string value)
        {
            return value.Contains("_ao", StringComparison.OrdinalIgnoreCase)
                || value.Contains("occlusion", StringComparison.OrdinalIgnoreCase)
                || value.Contains("_ao.data", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasGenericOpacityMarker(string value)
        {
            return value.Contains("_o", StringComparison.OrdinalIgnoreCase)
                || value.Contains("opacity", StringComparison.OrdinalIgnoreCase)
                || value.Contains("alpha", StringComparison.OrdinalIgnoreCase)
                || value.Contains("_o.data", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeStem(string value)
        {
            value = value.Trim();
            value = value.Replace('\\', '/');
            value = Path.GetFileNameWithoutExtension(value);
            value = value.Replace("-", "_").Replace(" ", "_");
            while (value.Contains("__"))
                value = value.Replace("__", "_");
            return value.Trim('_').ToLowerInvariant();
        }

        private static HashSet<string> Tokenize(string value)
        {
            return value
                .Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToLowerInvariant())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static GeneratedMaterialDefinitionStats BuildStats(GeneratedMaterialDefinitionResult result)
        {
            return new GeneratedMaterialDefinitionStats
            {
                ReferencedButUndefinedCount = result.Candidates.Count,
                BuildableCount = result.Candidates.Count(x => x.GenerationStatus == "buildable_generated_candidate"),
                PartialCount = result.Candidates.Count(x => x.GenerationStatus == "partial_generated_candidate"),
                NoSuggestionCount = result.Candidates.Count(x => x.GenerationStatus == "no_generated_candidate")
            };
        }

        private static string BuildGeneratedMaterialsJson(List<GeneratedMaterialCandidate> candidates)
        {
            var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates.Where(x => x.GenerationStatus != "no_generated_candidate"))
            {
                var stage0 = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                if (candidate.Slots.TryGetValue("colorMap", out var color) && color is not null)
                    stage0["colorMap"] = color.InternalPath;

                if (candidate.Slots.TryGetValue("normalMap", out var normal) && normal is not null)
                    stage0["normalMap"] = normal.InternalPath;

                if (candidate.Slots.TryGetValue("specularMap", out var specular) && specular is not null)
                    stage0["specularMap"] = specular.InternalPath;

                if (candidate.Slots.TryGetValue("roughnessMap", out var roughness) && roughness is not null)
                    stage0["roughnessMap"] = roughness.InternalPath;

                if (candidate.Slots.TryGetValue("metallicMap", out var metallic) && metallic is not null)
                    stage0["metallicMap"] = metallic.InternalPath;

                if (candidate.Slots.TryGetValue("ambientOcclusionMap", out var ao) && ao is not null)
                    stage0["ambientOcclusionMap"] = ao.InternalPath;

                if (candidate.Slots.TryGetValue("opacityMap", out var opacity) && opacity is not null)
                    stage0["opacityMap"] = opacity.InternalPath;

                var materialObject = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["name"] = candidate.MaterialName,
                    ["mapTo"] = candidate.MaterialName,
                    ["class"] = "Material",
                    ["Stages"] = new object[] { stage0 }
                };

                root[candidate.MaterialName] = materialObject;
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            return JsonSerializer.Serialize(root, options);
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

        private enum SlotKind
        {
            Color,
            Normal,
            Specular,
            Roughness,
            Metallic,
            Ao,
            Opacity
        }
    }

    public sealed class GeneratedMaterialDefinitionRequest
    {
        public string ModZipPath { get; set; } = string.Empty;
        public string CurrentFolder { get; set; } = string.Empty;
        public string OldFolder { get; set; } = string.Empty;
    }

    public sealed class GeneratedMaterialDefinitionResult
    {
        public GeneratedMaterialDefinitionRequest? Request { get; set; }
        public List<GeneratedMaterialCandidate> Candidates { get; set; } = new();
        public List<GeneratedMaterialTraceRow> TraceRows { get; set; } = new();
        public string GeneratedJsonText { get; set; } = "{}";
        public GeneratedMaterialDefinitionStats Stats { get; set; } = new();
    }

    public sealed class GeneratedMaterialDefinitionStats
    {
        public int ReferencedButUndefinedCount { get; set; }
        public int BuildableCount { get; set; }
        public int PartialCount { get; set; }
        public int NoSuggestionCount { get; set; }
    }

    public sealed class GeneratedMaterialCandidate
    {
        public string MaterialName { get; set; } = string.Empty;
        public int ReferenceCount { get; set; }
        public string GenerationStatus { get; set; } = string.Empty;
        public int ConfidenceScore { get; set; }
        public string ConfidenceBand { get; set; } = string.Empty;
        public string ReferenceSummary { get; set; } = string.Empty;
        public string GenerationExplanation { get; set; } = string.Empty;

        public GeneratedChosenAsset? ChosenColor { get; set; }
        public GeneratedChosenAsset? ChosenNormal { get; set; }
        public GeneratedChosenAsset? ChosenSpecular { get; set; }
        public GeneratedChosenAsset? ChosenRoughness { get; set; }
        public GeneratedChosenAsset? ChosenMetallic { get; set; }
        public GeneratedChosenAsset? ChosenAo { get; set; }
        public GeneratedChosenAsset? ChosenOpacity { get; set; }

        public Dictionary<string, GeneratedChosenAsset?> Slots { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class GeneratedChosenAsset
    {
        public string Origin { get; set; } = string.Empty;
        public string ArchivePath { get; set; } = string.Empty;
        public string InternalPath { get; set; } = string.Empty;
        public int Score { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class GeneratedMaterialTraceRow
    {
        public string Origin { get; set; } = string.Empty;
        public string ArchivePath { get; set; } = string.Empty;
        public string InternalPath { get; set; } = string.Empty;
        public string Classification { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }

    internal sealed class GeneratedAssetRecord
    {
        public string Origin { get; set; } = string.Empty;
        public string ArchivePath { get; set; } = string.Empty;
        public string InternalPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string BasenameStem { get; set; } = string.Empty;
        public string NormalizedStem { get; set; } = string.Empty;
        public HashSet<string> Tokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class GeneratedSourceSpec
    {
        public string Origin { get; set; } = string.Empty;
        public string ArchivePath { get; set; } = string.Empty;
    }
}