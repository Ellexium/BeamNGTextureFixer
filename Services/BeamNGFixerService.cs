using BeamNGTextureFixer.Helpers;
using BeamNGTextureFixer.Models;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeamNGTextureFixer.Services
{
    public class BeamNGFixerService : IDisposable
    {
        private static readonly HashSet<string> TextureKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "baseColorMap",
            "colorMap",
            "normalMap",
            "metallicMap",
            "roughnessMap",
            "opacityMap",
            "ambientOcclusionMap",
            "specularMap",
            "reflectivityMap",
            "clearCoatMap",
            "colorPaletteMap",
            "detailMap",
            "diffuseMap",
            "emissiveMap",
            "instanceColorMap",
            "layerMap",
        };

        // Shared folder-index cache across all service instances
        private static readonly object IndexCacheLock = new();
        private static readonly Dictionary<string, ZipIndexService> IndexCache = new(StringComparer.OrdinalIgnoreCase);

        public List<(TextureRef Ref, SearchHit Hit)> ScanResults { get; private set; } = new();
        public List<TextureRef> TextureRefs { get; private set; } = new();
        public List<string> MaterialFiles { get; private set; } = new();
        public List<MaterialFileInfo> MaterialFileInfos { get; private set; } = new();
        public Dictionary<string, JsonDocument?> MaterialJsonMap { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> MaterialTextMap { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public string ModZipPath { get; private set; } = string.Empty;
        public string OldFolder { get; private set; } = string.Empty;
        public string CurrentFolder { get; private set; } = string.Empty;
        public HashSet<string> ModFileSet { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        private bool _disposed;

        private IEnumerable<string> IterZipFiles(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                yield break;

            foreach (var file in Directory.EnumerateFiles(folder, "*.zip", SearchOption.AllDirectories))
                yield return file;
        }

        private static string NormalizeFolderKey(string folder)
        {
            return Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private ZipIndexService GetOrBuildCachedIndex(string folder)
        {
            var key = NormalizeFolderKey(folder);

            lock (IndexCacheLock)
            {
                if (IndexCache.TryGetValue(key, out var cached))
                    return cached;
            }

            var built = BuildSearchIndexes(folder);

            lock (IndexCacheLock)
            {
                if (!IndexCache.ContainsKey(key))
                    IndexCache[key] = built;

                return IndexCache[key];
            }
        }

        public static void ClearIndexCache()
        {
            lock (IndexCacheLock)
            {
                IndexCache.Clear();
            }
        }

        private void ClearScanState()
        {
            foreach (var doc in MaterialJsonMap.Values)
            {
                doc?.Dispose();
            }

            ScanResults.Clear();
            TextureRefs.Clear();
            MaterialFiles.Clear();
            MaterialFileInfos.Clear();
            MaterialJsonMap.Clear();
            MaterialTextMap.Clear();
            ModFileSet.Clear();

            ModZipPath = string.Empty;
            OldFolder = string.Empty;
            CurrentFolder = string.Empty;
        }

        public void Cleanup()
        {
            ClearScanState();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Cleanup();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private (
            List<string> materialPaths,
            Dictionary<string, JsonDocument?> materialJsonMap,
            Dictionary<string, string> materialTextMap,
            List<MaterialFileInfo> materialInfos,
            HashSet<string> modFileSet
        ) ScanMaterialFiles(string modZipPath)
        {
            var materialPaths = new List<string>();
            var materialJsonMap = new Dictionary<string, JsonDocument?>(StringComparer.OrdinalIgnoreCase);
            var materialTextMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var materialInfos = new List<MaterialFileInfo>();
            var modFileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var archive = ZipFile.OpenRead(modZipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/"))
                    continue;

                var norm = PathHelpers.NormalizePath(entry.FullName);
                modFileSet.Add(norm);

                if (!norm.EndsWith(".materials.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                materialPaths.Add(norm);

                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                var rawText = reader.ReadToEnd();
                materialTextMap[norm] = rawText;

                try
                {
                    materialJsonMap[norm] = JsonDocument.Parse(rawText);
                    materialInfos.Add(new MaterialFileInfo
                    {
                        Path = norm,
                        ParseMode = "pending",
                        RefCount = 0,
                        Error = string.Empty
                    });
                }
                catch (Exception ex)
                {
                    materialJsonMap[norm] = null;
                    materialInfos.Add(new MaterialFileInfo
                    {
                        Path = norm,
                        ParseMode = "pending",
                        RefCount = 0,
                        Error = ex.Message
                    });
                }
            }

            return (materialPaths, materialJsonMap, materialTextMap, materialInfos, modFileSet);
        }

        private List<TextureRef> ExtractJsonTextureRefs(string matFile, JsonDocument data)
        {
            var refs = new List<TextureRef>();
            if (data.RootElement.ValueKind != JsonValueKind.Object)
                return refs;

            foreach (var materialProp in data.RootElement.EnumerateObject())
            {
                var materialName = materialProp.Name;
                var materialBlock = materialProp.Value;

                if (materialBlock.ValueKind != JsonValueKind.Object)
                    continue;

                if (materialBlock.TryGetProperty("Stages", out var stages) && stages.ValueKind == JsonValueKind.Array)
                {
                    var stageIndex = 0;
                    foreach (var stage in stages.EnumerateArray())
                    {
                        if (stage.ValueKind != JsonValueKind.Object)
                        {
                            stageIndex++;
                            continue;
                        }

                        foreach (var prop in stage.EnumerateObject())
                        {
                            if (!TextureKeys.Contains(prop.Name))
                                continue;
                            if (prop.Value.ValueKind != JsonValueKind.String)
                                continue;

                            var value = prop.Value.GetString() ?? string.Empty;
                            var v = value.Trim();
                            if (string.IsNullOrWhiteSpace(v) || v.StartsWith("@"))
                                continue;

                            refs.Add(new TextureRef
                            {
                                MaterialFile = matFile,
                                MaterialName = materialName,
                                StageIndex = stageIndex,
                                Key = prop.Name,
                                OriginalValue = value,
                                NormalizedValue = PathHelpers.NormalizePath(value),
                                ExtractionMode = "json"
                            });
                        }

                        stageIndex++;
                    }
                }
                else
                {
                    foreach (var prop in materialBlock.EnumerateObject())
                    {
                        if (!TextureKeys.Contains(prop.Name))
                            continue;
                        if (prop.Value.ValueKind != JsonValueKind.String)
                            continue;

                        var value = prop.Value.GetString() ?? string.Empty;
                        var v = value.Trim();
                        if (string.IsNullOrWhiteSpace(v) || v.StartsWith("@"))
                            continue;

                        refs.Add(new TextureRef
                        {
                            MaterialFile = matFile,
                            MaterialName = materialName,
                            StageIndex = 0,
                            Key = prop.Name,
                            OriginalValue = value,
                            NormalizedValue = PathHelpers.NormalizePath(value),
                            ExtractionMode = "json"
                        });
                    }
                }
            }

            return refs;
        }

        private List<TextureRef> ExtractTextFallbackRefs(string matFile, string rawText)
        {
            var refs = new List<TextureRef>();
            var keyPattern = string.Join("|", TextureKeys.OrderByDescending(x => x.Length).Select(Regex.Escape));
            var pattern = new Regex($"\"(?<key>{keyPattern})\"\\s*:\\s*\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase);

            var matches = pattern.Matches(rawText);
            for (int i = 0; i < matches.Count; i++)
            {
                var key = matches[i].Groups["key"].Value;
                var path = matches[i].Groups["path"].Value;
                var v = path.Trim();
                if (string.IsNullOrWhiteSpace(v) || v.StartsWith("@"))
                    continue;

                refs.Add(new TextureRef
                {
                    MaterialFile = matFile,
                    MaterialName = "[text-fallback]",
                    StageIndex = i,
                    Key = key,
                    OriginalValue = path,
                    NormalizedValue = PathHelpers.NormalizePath(path),
                    ExtractionMode = "text_fallback"
                });
            }

            if (refs.Count == 0)
            {
                var broad = new Regex("\"(?<path>[^\"]+\\.(?:png|dds|jpg|jpeg|bmp|tga|gif|webp))\"", RegexOptions.IgnoreCase);
                var broadMatches = broad.Matches(rawText);
                for (int i = 0; i < broadMatches.Count; i++)
                {
                    var path = broadMatches[i].Groups["path"].Value;
                    var v = path.Trim();
                    if (string.IsNullOrWhiteSpace(v) || v.StartsWith("@"))
                        continue;
                    if (!v.Contains('/') && !v.Contains('\\'))
                        continue;

                    refs.Add(new TextureRef
                    {
                        MaterialFile = matFile,
                        MaterialName = "[broad-text-fallback]",
                        StageIndex = i,
                        Key = "[path]",
                        OriginalValue = path,
                        NormalizedValue = PathHelpers.NormalizePath(path),
                        ExtractionMode = "text_fallback"
                    });
                }
            }

            return refs;
        }

        private (List<TextureRef> refs, List<MaterialFileInfo> infos) ExtractTextureRefs(
            List<string> materialPaths,
            Dictionary<string, JsonDocument?> materialJsonMap,
            Dictionary<string, string> materialTextMap,
            List<MaterialFileInfo> materialInfos)
        {
            var refs = new List<TextureRef>();
            var infoLookup = materialInfos.ToDictionary(x => x.Path, x => x, StringComparer.OrdinalIgnoreCase);

            foreach (var matFile in materialPaths)
            {
                if (materialJsonMap.TryGetValue(matFile, out var data) && data is not null)
                {
                    var jsonRefs = ExtractJsonTextureRefs(matFile, data);
                    refs.AddRange(jsonRefs);
                    infoLookup[matFile].ParseMode = "json";
                    infoLookup[matFile].RefCount = jsonRefs.Count;
                    continue;
                }

                var rawText = materialTextMap.TryGetValue(matFile, out var text) ? text : string.Empty;
                var fallbackRefs = ExtractTextFallbackRefs(matFile, rawText);
                refs.AddRange(fallbackRefs);

                if (fallbackRefs.Count > 0)
                {
                    infoLookup[matFile].ParseMode = "text_fallback";
                    infoLookup[matFile].RefCount = fallbackRefs.Count;
                }
                else
                {
                    infoLookup[matFile].ParseMode = "unreadable";
                    infoLookup[matFile].RefCount = 0;
                }
            }

            return (refs, infoLookup.Values.ToList());
        }

        private ZipIndexService BuildSearchIndexes(string folder)
        {
            var index = new ZipIndexService();

            foreach (var zipPath in IterZipFiles(folder))
            {
                try
                {
                    using var archive = ZipFile.OpenRead(zipPath);
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/"))
                            continue;

                        var internalPath = PathHelpers.NormalizePath(entry.FullName);
                        Add(index.Exact, internalPath.ToLowerInvariant(), (zipPath, internalPath));
                        Add(index.ByBase, PathHelpers.Basename(internalPath).ToLowerInvariant(), (zipPath, internalPath));
                        Add(index.ByNormName, PathHelpers.NormalizeNameOnly(internalPath), (zipPath, internalPath));
                        Add(index.SamePathOtherExt, Path.ChangeExtension(internalPath, null)?.TrimEnd('.').ToLowerInvariant() ?? string.Empty, (zipPath, internalPath));
                    }
                }
                catch
                {
                }
            }

            return index;
        }

        private void Add(Dictionary<string, List<(string ZipPath, string InternalPath)>> dict, string key, (string ZipPath, string InternalPath) value)
        {
            if (!dict.TryGetValue(key, out var list))
            {
                list = new List<(string ZipPath, string InternalPath)>();
                dict[key] = list;
            }
            list.Add(value);
        }

        private SearchHit? FindInCurrent(TextureRef reference, ZipIndexService? currentIndexes)
        {
            if (currentIndexes is null)
                return null;

            var wantedLower = reference.NormalizedValue.ToLowerInvariant();
            var stem = Path.ChangeExtension(wantedLower, null)?.TrimEnd('.') ?? string.Empty;
            var b = PathHelpers.Basename(wantedLower).ToLowerInvariant();
            var n = PathHelpers.NormalizeNameOnly(wantedLower);

            if (currentIndexes.Exact.TryGetValue(wantedLower, out var exact))
                return new SearchHit { Status = "current_content", MatchType = "current_exact", SourceZipPath = exact[0].ZipPath, InternalPath = exact[0].InternalPath };
            if (currentIndexes.SamePathOtherExt.TryGetValue(stem, out var sameExt))
                return new SearchHit { Status = "current_content", MatchType = "current_same_path_other_ext", SourceZipPath = sameExt[0].ZipPath, InternalPath = sameExt[0].InternalPath };
            if (currentIndexes.ByBase.TryGetValue(b, out var byBase))
                return new SearchHit { Status = "current_content", MatchType = "current_same_basename", SourceZipPath = byBase[0].ZipPath, InternalPath = byBase[0].InternalPath };
            if (currentIndexes.ByNormName.TryGetValue(n, out var byNorm))
                return new SearchHit { Status = "current_content", MatchType = "current_normalized_name", SourceZipPath = byNorm[0].ZipPath, InternalPath = byNorm[0].InternalPath };

            return null;
        }

        private SearchHit FindInOld(TextureRef reference, ZipIndexService oldIndexes)
        {
            var wantedLower = reference.NormalizedValue.ToLowerInvariant();
            var stem = Path.ChangeExtension(wantedLower, null)?.TrimEnd('.') ?? string.Empty;
            var b = PathHelpers.Basename(wantedLower).ToLowerInvariant();
            var n = PathHelpers.NormalizeNameOnly(wantedLower);

            if (oldIndexes.Exact.TryGetValue(wantedLower, out var exact))
                return new SearchHit { Status = "resolved_from_old", MatchType = "exact_path", SourceZipPath = exact[0].ZipPath, InternalPath = exact[0].InternalPath };
            if (oldIndexes.SamePathOtherExt.TryGetValue(stem, out var sameExt))
                return new SearchHit { Status = "resolved_from_old", MatchType = "same_path_other_ext", SourceZipPath = sameExt[0].ZipPath, InternalPath = sameExt[0].InternalPath };
            if (oldIndexes.ByBase.TryGetValue(b, out var byBase))
                return new SearchHit { Status = "resolved_from_old", MatchType = "same_basename", SourceZipPath = byBase[0].ZipPath, InternalPath = byBase[0].InternalPath };
            if (oldIndexes.ByNormName.TryGetValue(n, out var byNorm))
                return new SearchHit { Status = "resolved_from_old", MatchType = "normalized_name", SourceZipPath = byNorm[0].ZipPath, InternalPath = byNorm[0].InternalPath };

            return new SearchHit { Status = "unresolved", MatchType = "unresolved", SourceZipPath = null, InternalPath = null };
        }

        public ScanSummary Scan(string modZipPath, string oldFolder, string? currentFolder = null)
        {
            // Dispose previous scan-state documents before replacing them
            ClearScanState();

            ModZipPath = modZipPath;
            OldFolder = oldFolder;
            CurrentFolder = currentFolder ?? string.Empty;

            var scan = ScanMaterialFiles(modZipPath);
            var extracted = ExtractTextureRefs(scan.materialPaths, scan.materialJsonMap, scan.materialTextMap, scan.materialInfos);

            // Cached indexes shared across service instances
            var oldIndexes = GetOrBuildCachedIndex(oldFolder);
            var currentIndexes = !string.IsNullOrWhiteSpace(currentFolder) ? GetOrBuildCachedIndex(currentFolder!) : null;

            var modLower = new HashSet<string>(scan.modFileSet.Select(x => x.ToLowerInvariant()));

            var results = new List<(TextureRef Ref, SearchHit Hit)>();
            int presentInMod = 0, satisfiedByCurrent = 0, resolvedFromOld = 0, unresolved = 0;

            foreach (var reference in extracted.refs)
            {
                SearchHit hit;
                if (modLower.Contains(reference.NormalizedValue.ToLowerInvariant()))
                {
                    hit = new SearchHit
                    {
                        Status = "present_in_mod",
                        MatchType = "present_in_mod",
                        SourceZipPath = null,
                        InternalPath = reference.NormalizedValue
                    };
                    presentInMod++;
                }
                else
                {
                    var currentHit = FindInCurrent(reference, currentIndexes);
                    if (currentHit is not null)
                    {
                        hit = currentHit;
                        satisfiedByCurrent++;
                    }
                    else
                    {
                        hit = FindInOld(reference, oldIndexes);
                        if (hit.Status == "resolved_from_old")
                            resolvedFromOld++;
                        else
                            unresolved++;
                    }
                }

                results.Add((reference, hit));
            }

            ScanResults = results;
            TextureRefs = extracted.refs;
            MaterialFiles = scan.materialPaths;
            MaterialFileInfos = extracted.infos;
            MaterialJsonMap = scan.materialJsonMap;
            MaterialTextMap = scan.materialTextMap;
            ModFileSet = scan.modFileSet;

            return new ScanSummary
            {
                MaterialFiles = scan.materialPaths.Count,
                TextureRefs = extracted.refs.Count,
                PresentInMod = presentInMod,
                SatisfiedByCurrent = satisfiedByCurrent,
                ResolvedFromOld = resolvedFromOld,
                Unresolved = unresolved,
                JsonOk = extracted.infos.Count(x => x.ParseMode == "json"),
                TextFallback = extracted.infos.Count(x => x.ParseMode == "text_fallback"),
                Unreadable = extracted.infos.Count(x => x.ParseMode == "unreadable"),
                Results = results,
                MaterialInfos = extracted.infos
            };
        }

        public Dictionary<(string SourceZipPath, string BasenameLower), int> BasenameCollisionsWithinSourceZip()
        {
            var counts = new Dictionary<(string SourceZipPath, string BasenameLower), int>();

            foreach (var (_, hit) in ScanResults)
            {
                if (hit.Status != "resolved_from_old" ||
                    string.IsNullOrWhiteSpace(hit.SourceZipPath) ||
                    string.IsNullOrWhiteSpace(hit.InternalPath))
                {
                    continue;
                }

                var key = (hit.SourceZipPath!, PathHelpers.Basename(hit.InternalPath).ToLowerInvariant());
                counts[key] = counts.TryGetValue(key, out var existing) ? existing + 1 : 1;
            }

            return counts;
        }

        public string SourceFolderForMatch(string modStem, SearchHit hit)
        {
            var sourceZipStem = PathHelpers.SanitizeModStem(Path.GetFileName(hit.SourceZipPath ?? "source"));
            return $"missingfilefix_{modStem}_{sourceZipStem}";
        }

        public string MakeMissingfilefixTarget(SearchHit hit, string modStem, Dictionary<(string SourceZipPath, string BasenameLower), int>? collisionCounts = null)
        {
            if (string.IsNullOrWhiteSpace(hit.SourceZipPath) || string.IsNullOrWhiteSpace(hit.InternalPath))
                throw new InvalidOperationException("Resolved hit is missing source zip path or internal path.");

            collisionCounts ??= BasenameCollisionsWithinSourceZip();

            var folder = SourceFolderForMatch(modStem, hit);
            var key = (hit.SourceZipPath!, PathHelpers.Basename(hit.InternalPath).ToLowerInvariant());

            if (collisionCounts.TryGetValue(key, out var count) && count > 1)
                return $"{folder}/{PathHelpers.NormalizePath(hit.InternalPath)}";

            return $"{folder}/{PathHelpers.Basename(hit.InternalPath)}";
        }

        public RewritePlan BuildRewritePlan()
        {
            if (string.IsNullOrWhiteSpace(ModZipPath))
                throw new InvalidOperationException("No mod scanned.");

            var modStem = PathHelpers.SanitizeModStem(Path.GetFileName(ModZipPath));
            var collisionCounts = BasenameCollisionsWithinSourceZip();

            var rewritesByJson = new Dictionary<(string MaterialFile, string MaterialName, int StageIndex, string Key, string OriginalValue), string>();
            var rewritesByText = new Dictionary<string, List<TextRewrite>>(StringComparer.OrdinalIgnoreCase);
            var copyJobs = new Dictionary<(string SourceZipPath, string InternalPath), string>();

            foreach (var (reference, hit) in ScanResults)
            {
                if (hit.Status != "resolved_from_old")
                    continue;

                if (string.IsNullOrWhiteSpace(hit.SourceZipPath) || string.IsNullOrWhiteSpace(hit.InternalPath))
                    continue;

                var newPath = MakeMissingfilefixTarget(hit, modStem, collisionCounts);
                copyJobs[(hit.SourceZipPath!, hit.InternalPath!)] = newPath;

                if (string.Equals(reference.ExtractionMode, "json", StringComparison.OrdinalIgnoreCase))
                {
                    rewritesByJson[(reference.MaterialFile, reference.MaterialName, reference.StageIndex, reference.Key, reference.OriginalValue)] = newPath;
                }
                else
                {
                    if (!rewritesByText.TryGetValue(reference.MaterialFile, out var list))
                    {
                        list = new List<TextRewrite>();
                        rewritesByText[reference.MaterialFile] = list;
                    }

                    list.Add(new TextRewrite
                    {
                        Key = reference.Key,
                        OldValue = reference.OriginalValue,
                        NewPath = newPath
                    });
                }
            }

            return new RewritePlan
            {
                ModStem = modStem,
                CollisionCounts = collisionCounts,
                RewritesByJson = rewritesByJson,
                RewritesByText = rewritesByText,
                CopyJobs = copyJobs
            };
        }

        public BuildFixedResult BuildFixedMod(string outPath, Action<int, int, string>? progressCallback = null)
        {
            if (string.IsNullOrWhiteSpace(ModZipPath))
                throw new InvalidOperationException("No mod scanned.");

            var plan = BuildRewritePlan();
            progressCallback?.Invoke(0, Math.Max(plan.CopyJobs.Count, 1), "Preparing fixed mod...");

            if (plan.CopyJobs.Count == 0)
            {
                return new BuildFixedResult
                {
                    Built = false,
                    OutPath = outPath,
                    Copied = 0,
                    Rewrites = 0
                };
            }

            var reportLines = new List<string>
            {
                $"Mod: {ModZipPath}",
                $"Old content folder: {OldFolder}",
                $"Current content folder: {(string.IsNullOrWhiteSpace(CurrentFolder) ? "(not specified)" : CurrentFolder)}",
                "",
                "Material file modes:"
            };

            foreach (var info in MaterialFileInfos)
                reportLines.Add($"{info.Path} | {info.ParseMode} | refs={info.RefCount} | error={info.Error}");

            var tempPath = outPath + ".tmp";

            if (File.Exists(tempPath))
                File.Delete(tempPath);

            using (var zin = ZipFile.OpenRead(ModZipPath))
            using (var zout = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                foreach (var entry in zin.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/"))
                        continue;

                    var norm = PathHelpers.NormalizePath(entry.FullName);

                    if (MaterialJsonMap.TryGetValue(norm, out var jsonDoc) && jsonDoc is not null)
                    {
                        var updatedJson = RewriteJsonMaterial(norm, jsonDoc, plan.RewritesByJson, reportLines);
                        var outEntry = zout.CreateEntry(entry.FullName, CompressionLevel.Optimal);

                        using var outStream = outEntry.Open();
                        using var writer = new StreamWriter(outStream, new UTF8Encoding(false));
                        writer.Write(updatedJson);
                    }
                    else if (MaterialTextMap.TryGetValue(norm, out var rawText) && plan.RewritesByText.TryGetValue(norm, out var textRewrites))
                    {
                        var updatedText = RewriteTextMaterial(norm, rawText, textRewrites, reportLines);
                        var outEntry = zout.CreateEntry(entry.FullName, CompressionLevel.Optimal);

                        using var outStream = outEntry.Open();
                        using var writer = new StreamWriter(outStream, new UTF8Encoding(false));
                        writer.Write(updatedText);
                    }
                    else
                    {
                        var outEntry = zout.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                        using var inStream = entry.Open();
                        using var outStream = outEntry.Open();
                        inStream.CopyTo(outStream);
                    }
                }

                progressCallback?.Invoke(0, Math.Max(plan.CopyJobs.Count, 1), "Copying resolved files...");

                int copiedSoFar = 0;

                foreach (var job in plan.CopyJobs)
                {
                    var sourceZipPath = job.Key.SourceZipPath;
                    var internalPath = job.Key.InternalPath;
                    var destPath = job.Value;

                    using var sourceArchive = ZipFile.OpenRead(sourceZipPath);
                    var sourceEntry = sourceArchive.GetEntry(internalPath.Replace("\\", "/"))
                                     ?? sourceArchive.Entries.FirstOrDefault(e =>
                                         string.Equals(PathHelpers.NormalizePath(e.FullName), PathHelpers.NormalizePath(internalPath), StringComparison.OrdinalIgnoreCase));

                    if (sourceEntry == null)
                        throw new FileNotFoundException($"Could not find '{internalPath}' inside '{sourceZipPath}'.");

                    var outEntry = zout.CreateEntry(destPath, CompressionLevel.Optimal);
                    using var inStream = sourceEntry.Open();
                    using var outStream = outEntry.Open();
                    inStream.CopyTo(outStream);

                    copiedSoFar++;
                    progressCallback?.Invoke(
                        copiedSoFar,
                        Math.Max(plan.CopyJobs.Count, 1),
                        $"Copying resolved files... {copiedSoFar} / {plan.CopyJobs.Count}");

                    reportLines.Add($"COPY | {sourceZipPath} | {internalPath} -> {destPath}");
                }

                var reportEntry = zout.CreateEntry("missingfilefix_report.txt", CompressionLevel.Optimal);
                using (var reportStream = reportEntry.Open())
                using (var writer = new StreamWriter(reportStream, new UTF8Encoding(false)))
                {
                    writer.Write(string.Join(Environment.NewLine, reportLines));
                }
            }

            if (File.Exists(outPath))
                File.Delete(outPath);

            File.Move(tempPath, outPath);

            return new BuildFixedResult
            {
                Built = true,
                OutPath = outPath,
                Copied = plan.CopyJobs.Count,
                Rewrites = plan.RewritesByJson.Count + plan.RewritesByText.Values.Sum(x => x.Count)
            };
        }

        private string RewriteJsonMaterial(
            string materialFile,
            JsonDocument jsonDoc,
            Dictionary<(string MaterialFile, string MaterialName, int StageIndex, string Key, string OriginalValue), string> rewritesByJson,
            List<string> reportLines)
        {
            using var output = new MemoryStream();
            using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();

            foreach (var materialProp in jsonDoc.RootElement.EnumerateObject())
            {
                writer.WritePropertyName(materialProp.Name);

                if (materialProp.Value.ValueKind != JsonValueKind.Object)
                {
                    materialProp.Value.WriteTo(writer);
                    continue;
                }

                writer.WriteStartObject();

                foreach (var blockProp in materialProp.Value.EnumerateObject())
                {
                    if (blockProp.NameEquals("Stages") && blockProp.Value.ValueKind == JsonValueKind.Array)
                    {
                        writer.WritePropertyName(blockProp.Name);
                        writer.WriteStartArray();

                        int stageIndex = 0;
                        foreach (var stage in blockProp.Value.EnumerateArray())
                        {
                            if (stage.ValueKind != JsonValueKind.Object)
                            {
                                stage.WriteTo(writer);
                                stageIndex++;
                                continue;
                            }

                            writer.WriteStartObject();

                            foreach (var prop in stage.EnumerateObject())
                            {
                                var originalValue = prop.Value.ValueKind == JsonValueKind.String ? (prop.Value.GetString() ?? string.Empty) : string.Empty;
                                var key = (materialFile, materialProp.Name, stageIndex, prop.Name, originalValue);

                                writer.WritePropertyName(prop.Name);

                                if (prop.Value.ValueKind == JsonValueKind.String &&
                                    rewritesByJson.TryGetValue(key, out var newPath))
                                {
                                    writer.WriteStringValue(newPath);
                                    reportLines.Add($"REWRITE JSON | {materialFile} | {materialProp.Name} | stage {stageIndex} | {prop.Name} | {originalValue} -> {newPath}");
                                }
                                else
                                {
                                    prop.Value.WriteTo(writer);
                                }
                            }

                            writer.WriteEndObject();
                            stageIndex++;
                        }

                        writer.WriteEndArray();
                    }
                    else
                    {
                        var originalValue = blockProp.Value.ValueKind == JsonValueKind.String ? (blockProp.Value.GetString() ?? string.Empty) : string.Empty;
                        var key = (materialFile, materialProp.Name, 0, blockProp.Name, originalValue);

                        writer.WritePropertyName(blockProp.Name);

                        if (blockProp.Value.ValueKind == JsonValueKind.String &&
                            rewritesByJson.TryGetValue(key, out var newPath))
                        {
                            writer.WriteStringValue(newPath);
                            reportLines.Add($"REWRITE JSON-FLAT | {materialFile} | {materialProp.Name} | {blockProp.Name} | {originalValue} -> {newPath}");
                        }
                        else
                        {
                            blockProp.Value.WriteTo(writer);
                        }
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.Flush();

            return Encoding.UTF8.GetString(output.ToArray());
        }

        private string RewriteTextMaterial(string materialFile, string rawText, List<TextRewrite> rewrites, List<string> reportLines)
        {
            var updated = rawText;

            foreach (var rewrite in rewrites)
            {
                if (rewrite.Key == "[path]")
                {
                    var oldEscaped = Regex.Escape(rewrite.OldValue);
                    var pattern = new Regex($"\"{oldEscaped}\"", RegexOptions.IgnoreCase);

                    var replaced = false;
                    updated = pattern.Replace(updated, m =>
                    {
                        if (replaced) return m.Value;
                        replaced = true;
                        return $"\"{rewrite.NewPath}\"";
                    });
                }
                else
                {
                    var keyEscaped = Regex.Escape(rewrite.Key);
                    var oldEscaped = Regex.Escape(rewrite.OldValue);
                    var pattern = new Regex($"(\"{keyEscaped}\"\\s*:\\s*\"){oldEscaped}(\")", RegexOptions.IgnoreCase);
                    var replaced = false;

                    updated = pattern.Replace(updated, m =>
                    {
                        if (replaced) return m.Value;
                        replaced = true;
                        return $"{m.Groups[1].Value}{rewrite.NewPath}{m.Groups[2].Value}";
                    });

                    if (!replaced)
                    {
                        var idx = updated.IndexOf(rewrite.OldValue, StringComparison.Ordinal);
                        if (idx >= 0)
                            updated = updated.Remove(idx, rewrite.OldValue.Length).Insert(idx, rewrite.NewPath);
                    }
                }

                reportLines.Add($"REWRITE TEXT | {materialFile} | {rewrite.Key} | {rewrite.OldValue} -> {rewrite.NewPath}");
            }

            return updated;
        }
    }

    public class RewritePlan
    {
        public string ModStem { get; set; } = "";
        public Dictionary<(string SourceZipPath, string BasenameLower), int> CollisionCounts { get; set; } = new();
        public Dictionary<(string MaterialFile, string MaterialName, int StageIndex, string Key, string OriginalValue), string> RewritesByJson { get; set; } = new();
        public Dictionary<string, List<TextRewrite>> RewritesByText { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(string SourceZipPath, string InternalPath), string> CopyJobs { get; set; } = new();
    }

    public class TextRewrite
    {
        public string Key { get; set; } = "";
        public string OldValue { get; set; } = "";
        public string NewPath { get; set; } = "";
    }

    public class BuildFixedResult
    {
        public bool Built { get; set; }
        public string OutPath { get; set; } = "";
        public int Copied { get; set; }
        public int Rewrites { get; set; }
    }

    public class ScanSummary
    {
        public int MaterialFiles { get; set; }
        public int TextureRefs { get; set; }
        public int PresentInMod { get; set; }
        public int SatisfiedByCurrent { get; set; }
        public int ResolvedFromOld { get; set; }
        public int Unresolved { get; set; }
        public int JsonOk { get; set; }
        public int TextFallback { get; set; }
        public int Unreadable { get; set; }
        public List<(TextureRef Ref, SearchHit Hit)> Results { get; set; } = new();
        public List<MaterialFileInfo> MaterialInfos { get; set; } = new();
    }
}
