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
using System.Threading;

namespace BeamNGTextureFixer.Services
{
    public sealed class MaterialFinderService
    {
        private static readonly HashSet<string> MaterialDefinitionFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "materials.cs",
            "materials.json"
        };

        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".cs", ".jbeam", ".lua", ".txt", ".cfg", ".material", ".materials",
            ".xml", ".ini", ".log", ".shader", ".hlsl", ".mis", ".ter", ".forest",
            ".html", ".htm", ".js", ".css", ".yml", ".yaml"
        };

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
            "detailNormalMap",
            "diffuseMap",
            "emissiveMap",
            "instanceColorMap",
            "layerMap",
            "overlayMap",
            "macroMap"
        };

        private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".dds", ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".gif", ".webp"
        };

        public MaterialFinderResult Scan(
            MaterialFinderRequest request,
            CancellationToken token = default,
            Action<int, int, string>? progress = null)
        {
            if (request is null)
                throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.ModZipPath))
                throw new ArgumentException("ModZipPath is required.", nameof(request));
            if (!File.Exists(request.ModZipPath))
                throw new FileNotFoundException("Target mod zip was not found.", request.ModZipPath);

            var result = new MaterialFinderResult
            {
                Request = request
            };

            var assetIndex = new MaterialAssetIndex();
            var sourceSpecs = BuildSourceSpecs(request);

            int sourceCounter = 0;
            foreach (var spec in sourceSpecs)
            {
                token.ThrowIfCancellationRequested();
                sourceCounter++;

                progress?.Invoke(sourceCounter, sourceSpecs.Count, $"Scanning {spec.Label}...");

                result.TraceRows.Add(new MaterialScanTraceRow
                {
                    RowType = "SourceArchive",
                    Origin = spec.Origin,
                    ArchivePath = spec.ArchivePath,
                    InternalPath = "",
                    Classification = "archive",
                    Detail = spec.Label,
                    DefinitionCount = 0,
                    ReferenceCount = 0
                });

                ScanSource(spec, request, result, assetIndex, token);
            }

            Correlate(result, assetIndex);
            result.Stats = BuildStats(result);
            return result;
        }

        private List<MaterialScanSource> BuildSourceSpecs(MaterialFinderRequest request)
        {
            var list = new List<MaterialScanSource>
            {
                new()
                {
                    Origin = "mod",
                    Label = Path.GetFileName(request.ModZipPath),
                    ArchivePath = request.ModZipPath,
                    ScanReferences = true,
                    ScanDefinitions = true
                }
            };

            if (!string.IsNullOrWhiteSpace(request.CurrentFolder) && Directory.Exists(request.CurrentFolder))
            {
                foreach (var zip in Directory.EnumerateFiles(request.CurrentFolder, "*.zip", SearchOption.AllDirectories))
                {
                    list.Add(new MaterialScanSource
                    {
                        Origin = "current_content",
                        Label = Path.GetFileName(zip),
                        ArchivePath = zip,
                        ScanReferences = request.ScanReferencesInContentFolders,
                        ScanDefinitions = true
                    });
                }
            }

            if (!string.IsNullOrWhiteSpace(request.OldFolder) && Directory.Exists(request.OldFolder))
            {
                foreach (var zip in Directory.EnumerateFiles(request.OldFolder, "*.zip", SearchOption.AllDirectories))
                {
                    list.Add(new MaterialScanSource
                    {
                        Origin = "old_content",
                        Label = Path.GetFileName(zip),
                        ArchivePath = zip,
                        ScanReferences = request.ScanReferencesInContentFolders,
                        ScanDefinitions = true
                    });
                }
            }

            return list;
        }

        private void ScanSource(
            MaterialScanSource spec,
            MaterialFinderRequest request,
            MaterialFinderResult result,
            MaterialAssetIndex assetIndex,
            CancellationToken token)
        {
            try
            {
                using var archive = ZipFile.OpenRead(spec.ArchivePath);

                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();

                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/"))
                        continue;

                    var internalPath = PathHelpers.NormalizePath(entry.FullName);

                    result.TraceRows.Add(new MaterialScanTraceRow
                    {
                        RowType = "ScannedEntry",
                        Origin = spec.Origin,
                        ArchivePath = spec.ArchivePath,
                        InternalPath = internalPath,
                        Classification = "entry_seen",
                        Detail = "",
                        DefinitionCount = 0,
                        ReferenceCount = 0
                    });

                    IndexAssetEntry(assetIndex, spec, internalPath);

                    var isDefinitionFile = IsDefinitionFile(internalPath);
                    var isReferenceFile = ShouldScanReferences(internalPath, isDefinitionFile);
                    var isTextCandidate = isDefinitionFile || isReferenceFile || IsTextCandidate(internalPath);

                    if (AssetExtensions.Contains(Path.GetExtension(internalPath)))
                    {
                        result.TraceRows.Add(new MaterialScanTraceRow
                        {
                            RowType = "ScannedEntry",
                            Origin = spec.Origin,
                            ArchivePath = spec.ArchivePath,
                            InternalPath = internalPath,
                            Classification = "asset",
                            Detail = "Indexed as texture asset",
                            DefinitionCount = 0,
                            ReferenceCount = 0
                        });
                    }

                    if (!isTextCandidate)
                    {
                        result.TraceRows.Add(new MaterialScanTraceRow
                        {
                            RowType = "SkippedEntry",
                            Origin = spec.Origin,
                            ArchivePath = spec.ArchivePath,
                            InternalPath = internalPath,
                            Classification = "skipped_non_text_candidate",
                            Detail = "Not a definition file, reference file, or text candidate",
                            DefinitionCount = 0,
                            ReferenceCount = 0
                        });
                        continue;
                    }

                    var readResult = ReadEntryText(entry);
                    if (readResult.Text is null)
                    {
                        result.TraceRows.Add(new MaterialScanTraceRow
                        {
                            RowType = "SkippedEntry",
                            Origin = spec.Origin,
                            ArchivePath = spec.ArchivePath,
                            InternalPath = internalPath,
                            Classification = "skipped_unreadable_text",
                            Detail = string.IsNullOrWhiteSpace(readResult.Detail)
                                ? "Text read/decode failed"
                                : readResult.Detail,
                            DefinitionCount = 0,
                            ReferenceCount = 0
                        });
                        continue;
                    }

                    var rawText = readResult.Text;

                    result.TraceRows.Add(new MaterialScanTraceRow
                    {
                        RowType = "ReadText",
                        Origin = spec.Origin,
                        ArchivePath = spec.ArchivePath,
                        InternalPath = internalPath,
                        Classification = "text_read",
                        Detail = $"Decoded as {readResult.EncodingName}",
                        DefinitionCount = 0,
                        ReferenceCount = 0
                    });

                    if (spec.ScanDefinitions && isDefinitionFile)
                    {
                        var beforeDefs = result.Definitions.Count;
                        ScanDefinitionText(spec, internalPath, rawText, result, token);
                        var addedDefs = result.Definitions.Count - beforeDefs;

                        result.TraceRows.Add(new MaterialScanTraceRow
                        {
                            RowType = "DefinitionFile",
                            Origin = spec.Origin,
                            ArchivePath = spec.ArchivePath,
                            InternalPath = internalPath,
                            Classification = "definition_file",
                            Detail = $"Scanned definition file, found {addedDefs} definitions",
                            DefinitionCount = addedDefs,
                            ReferenceCount = 0
                        });
                    }

                    if (spec.ScanReferences && isReferenceFile)
                    {
                        var beforeRefs = result.References.Count;
                        ScanReferenceText(spec, internalPath, rawText, result, token);
                        var addedRefs = result.References.Count - beforeRefs;

                        result.TraceRows.Add(new MaterialScanTraceRow
                        {
                            RowType = "ReferenceFile",
                            Origin = spec.Origin,
                            ArchivePath = spec.ArchivePath,
                            InternalPath = internalPath,
                            Classification = "reference_file",
                            Detail = $"Scanned reference file, found {addedRefs} references",
                            DefinitionCount = 0,
                            ReferenceCount = addedRefs
                        });
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                result.TraceRows.Add(new MaterialScanTraceRow
                {
                    RowType = "SourceError",
                    Origin = spec.Origin,
                    ArchivePath = spec.ArchivePath,
                    InternalPath = "",
                    Classification = "invalid_zip",
                    Detail = ex.Message,
                    DefinitionCount = 0,
                    ReferenceCount = 0
                });
            }
            catch (IOException ex)
            {
                result.TraceRows.Add(new MaterialScanTraceRow
                {
                    RowType = "SourceError",
                    Origin = spec.Origin,
                    ArchivePath = spec.ArchivePath,
                    InternalPath = "",
                    Classification = "io_error",
                    Detail = ex.Message,
                    DefinitionCount = 0,
                    ReferenceCount = 0
                });
            }
            catch (UnauthorizedAccessException ex)
            {
                result.TraceRows.Add(new MaterialScanTraceRow
                {
                    RowType = "SourceError",
                    Origin = spec.Origin,
                    ArchivePath = spec.ArchivePath,
                    InternalPath = "",
                    Classification = "access_error",
                    Detail = ex.Message,
                    DefinitionCount = 0,
                    ReferenceCount = 0
                });
            }
        }

        private void IndexAssetEntry(MaterialAssetIndex assetIndex, MaterialScanSource spec, string internalPath)
        {
            var ext = Path.GetExtension(internalPath);
            if (!AssetExtensions.Contains(ext))
                return;

            var entry = new MaterialAssetRecord
            {
                Origin = spec.Origin,
                ArchivePath = spec.ArchivePath,
                InternalPath = internalPath
            };

            Add(assetIndex.Exact, internalPath.ToLowerInvariant(), entry);
            Add(assetIndex.ByBase, PathHelpers.Basename(internalPath).ToLowerInvariant(), entry);
            Add(assetIndex.ByNormName, PathHelpers.NormalizeNameOnly(internalPath), entry);

            var stem = Path.ChangeExtension(internalPath, null)?.TrimEnd('.').ToLowerInvariant() ?? string.Empty;
            Add(assetIndex.SamePathOtherExt, stem, entry);
        }

        private static bool IsDefinitionFile(string path)
        {
            var norm = PathHelpers.NormalizePath(path);
            var fileName = Path.GetFileName(norm);

            if (MaterialDefinitionFileNames.Contains(fileName))
                return true;

            return norm.EndsWith(".materials.json", StringComparison.OrdinalIgnoreCase)
                || norm.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTextCandidate(string path)
        {
            var name = Path.GetFileName(path);
            if (!name.Contains('.'))
                return true;

            var ext = Path.GetExtension(path);
            return TextExtensions.Contains(ext);
        }

        private static bool ShouldScanReferences(string path, bool isDefinitionFile)
        {
            if (isDefinitionFile)
                return false;

            var fileName = Path.GetFileName(path);

            if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return true;
            if (fileName.EndsWith(".jbeam", StringComparison.OrdinalIgnoreCase))
                return true;
            if (fileName.EndsWith(".mis", StringComparison.OrdinalIgnoreCase))
                return true;
            if (fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private static ReadTextResult ReadEntryText(ZipArchiveEntry entry)
        {
            try
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                var text = reader.ReadToEnd();

                return new ReadTextResult
                {
                    Text = text,
                    EncodingName = reader.CurrentEncoding?.WebName ?? "unknown",
                    Detail = ""
                };
            }
            catch (Exception ex)
            {
                return new ReadTextResult
                {
                    Text = null,
                    EncodingName = "",
                    Detail = ex.Message
                };
            }
        }

        private void ScanDefinitionText(
            MaterialScanSource spec,
            string internalPath,
            string rawText,
            MaterialFinderResult result,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (internalPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var def in ExtractJsonDefinitions(spec, internalPath, rawText))
                    AddDefinition(result, def);

                return;
            }

            foreach (var def in ExtractCsDefinitions(spec, internalPath, rawText))
                AddDefinition(result, def);
        }

        private void ScanReferenceText(
            MaterialScanSource spec,
            string internalPath,
            string rawText,
            MaterialFinderResult result,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (internalPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                internalPath.EndsWith(".jbeam", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var reference in ExtractJsonLikeReferences(spec, internalPath, rawText))
                {
                    result.References.Add(reference);

                    result.TraceRows.Add(new MaterialScanTraceRow
                    {
                        RowType = "ReferenceFound",
                        Origin = reference.Origin,
                        ArchivePath = reference.ReferencedByArchivePath,
                        InternalPath = reference.ReferencedByFile,
                        Classification = reference.PropertyName,
                        Detail = reference.MaterialName,
                        DefinitionCount = 0,
                        ReferenceCount = 1
                    });
                }

                return;
            }

            foreach (var reference in ExtractLooseTextReferences(spec, internalPath, rawText))
            {
                result.References.Add(reference);

                result.TraceRows.Add(new MaterialScanTraceRow
                {
                    RowType = "ReferenceFound",
                    Origin = reference.Origin,
                    ArchivePath = reference.ReferencedByArchivePath,
                    InternalPath = reference.ReferencedByFile,
                    Classification = reference.PropertyName,
                    Detail = reference.MaterialName,
                    DefinitionCount = 0,
                    ReferenceCount = 1
                });
            }
        }

        private IEnumerable<MaterialDefinitionRecord> ExtractJsonDefinitions(
            MaterialScanSource spec,
            string internalPath,
            string rawText)
        {
            var results = new List<MaterialDefinitionRecord>();
            JsonDocument? doc = null;

            try
            {
                doc = JsonDocument.Parse(rawText);

                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return results;

                foreach (var materialProp in doc.RootElement.EnumerateObject())
                {
                    if (materialProp.Value.ValueKind != JsonValueKind.Object)
                        continue;

                    var def = new MaterialDefinitionRecord
                    {
                        MaterialName = materialProp.Name,
                        SourcePath = internalPath,
                        SourceArchivePath = spec.ArchivePath,
                        SourceKind = "json",
                        Origin = spec.Origin
                    };

                    foreach (var textureRef in ExtractJsonTextureRefs(internalPath, materialProp.Name, materialProp.Value))
                        def.TextureRefs.Add(textureRef);

                    results.Add(def);
                }
            }
            catch (JsonException)
            {
                return results;
            }
            finally
            {
                doc?.Dispose();
            }

            return results;
        }

        private IEnumerable<TextureRef> ExtractJsonTextureRefs(
            string materialFile,
            string materialName,
            JsonElement materialBlock)
        {
            if (materialBlock.TryGetProperty("Stages", out var stages) && stages.ValueKind == JsonValueKind.Array)
            {
                var stageIndex = 0;

                foreach (var stage in stages.EnumerateArray())
                {
                    if (stage.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in stage.EnumerateObject())
                        {
                            if (!TextureKeys.Contains(prop.Name))
                                continue;
                            if (prop.Value.ValueKind != JsonValueKind.String)
                                continue;

                            var value = prop.Value.GetString() ?? string.Empty;
                            if (ShouldKeepTextureValue(value))
                            {
                                var normalizedValue = PathHelpers.NormalizePath(value);

                                bool hasPath =
                                    normalizedValue.Contains('/') ||
                                    normalizedValue.Contains('\\');

                                if (!hasPath)
                                {
                                    var materialFolder = Path.GetDirectoryName(PathHelpers.NormalizePath(materialFile)) ?? string.Empty;
                                    materialFolder = PathHelpers.NormalizePath(materialFolder);

                                    if (!string.IsNullOrWhiteSpace(materialFolder))
                                        normalizedValue = PathHelpers.NormalizePath($"{materialFolder}/{normalizedValue}");
                                }

                                yield return new TextureRef
                                {
                                    MaterialFile = materialFile,
                                    MaterialName = materialName,
                                    StageIndex = stageIndex,
                                    Key = prop.Name,
                                    OriginalValue = value,
                                    NormalizedValue = normalizedValue,
                                    ExtractionMode = "json"
                                };
                            }
                        }
                    }

                    stageIndex++;
                }
            }

            foreach (var prop in materialBlock.EnumerateObject())
            {
                if (!TextureKeys.Contains(prop.Name))
                    continue;
                if (prop.Value.ValueKind != JsonValueKind.String)
                    continue;

                var value = prop.Value.GetString() ?? string.Empty;
                if (!ShouldKeepTextureValue(value))
                    continue;

                var normalizedValue = PathHelpers.NormalizePath(value);

                bool hasPath =
                    normalizedValue.Contains('/') ||
                    normalizedValue.Contains('\\');

                if (!hasPath)
                {
                    var materialFolder = Path.GetDirectoryName(PathHelpers.NormalizePath(materialFile)) ?? string.Empty;
                    materialFolder = PathHelpers.NormalizePath(materialFolder);

                    if (!string.IsNullOrWhiteSpace(materialFolder))
                        normalizedValue = PathHelpers.NormalizePath($"{materialFolder}/{normalizedValue}");
                }

                yield return new TextureRef
                {
                    MaterialFile = materialFile,
                    MaterialName = materialName,
                    StageIndex = 0,
                    Key = prop.Name,
                    OriginalValue = value,
                    NormalizedValue = normalizedValue,
                    ExtractionMode = "json"
                };
            }
        }

        private IEnumerable<MaterialDefinitionRecord> ExtractCsDefinitions(
            MaterialScanSource spec,
            string internalPath,
            string rawText)
        {
            var text = StripLineComments(rawText);

            foreach (var def in ExtractSingletonMaterialDefinitions(spec, internalPath, text))
                yield return def;

            foreach (var def in ExtractTerrainMaterialDefinitions(spec, internalPath, text))
                yield return def;
        }

        private IEnumerable<MaterialDefinitionRecord> ExtractSingletonMaterialDefinitions(
            MaterialScanSource spec,
            string internalPath,
            string text)
        {
            var blockPattern = new Regex(
                "singleton\\s+Material\\s*\\(\\s*(?<name>(\"[^\"]+\"|[^)\\r\\n]+))\\s*\\)\\s*\\{(?<body>.*?)\\};",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var assignmentPattern = new Regex(
                "(?<key>[A-Za-z_][A-Za-z0-9_]*)(?:\\[(?<stage>\\d+)\\])?\\s*=\\s*\"(?<value>[^\"]*)\"\\s*;",
                RegexOptions.IgnoreCase);

            foreach (Match blockMatch in blockPattern.Matches(text))
            {
                var materialName = Unquote(blockMatch.Groups["name"].Value);
                var body = blockMatch.Groups["body"].Value;

                var def = new MaterialDefinitionRecord
                {
                    MaterialName = materialName,
                    SourcePath = internalPath,
                    SourceArchivePath = spec.ArchivePath,
                    SourceKind = "cs",
                    Origin = spec.Origin
                };

                foreach (Match assign in assignmentPattern.Matches(body))
                {
                    var key = assign.Groups["key"].Value;
                    if (!TextureKeys.Contains(key))
                        continue;

                    var value = assign.Groups["value"].Value;
                    if (!ShouldKeepTextureValue(value))
                        continue;

                    int stageIndex = 0;
                    if (assign.Groups["stage"].Success)
                        int.TryParse(assign.Groups["stage"].Value, out stageIndex);

                    def.TextureRefs.Add(new TextureRef
                    {
                        MaterialFile = internalPath,
                        MaterialName = materialName,
                        StageIndex = stageIndex,
                        Key = key,
                        OriginalValue = value,
                        NormalizedValue = PathHelpers.NormalizePath(value),
                        ExtractionMode = "cs"
                    });
                }

                yield return def;
            }
        }

        private IEnumerable<MaterialDefinitionRecord> ExtractTerrainMaterialDefinitions(
            MaterialScanSource spec,
            string internalPath,
            string text)
        {
            var blockPattern = new Regex(
                "new\\s+TerrainMaterial\\s*\\(\\s*\\)\\s*\\{(?<body>.*?)\\};",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var assignmentPattern = new Regex(
                "(?<key>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*\"(?<value>[^\"]*)\"\\s*;",
                RegexOptions.IgnoreCase);

            foreach (Match blockMatch in blockPattern.Matches(text))
            {
                var body = blockMatch.Groups["body"].Value;
                string? internalName = null;
                var refs = new List<TextureRef>();

                foreach (Match assign in assignmentPattern.Matches(body))
                {
                    var key = assign.Groups["key"].Value;
                    var value = assign.Groups["value"].Value;

                    if (key.Equals("internalName", StringComparison.OrdinalIgnoreCase))
                    {
                        internalName = value.Trim();
                        continue;
                    }

                    if (!TextureKeys.Contains(key))
                        continue;
                    if (!ShouldKeepTextureValue(value))
                        continue;

                    refs.Add(new TextureRef
                    {
                        MaterialFile = internalPath,
                        MaterialName = string.Empty,
                        StageIndex = 0,
                        Key = key,
                        OriginalValue = value,
                        NormalizedValue = PathHelpers.NormalizePath(value),
                        ExtractionMode = "terrain_cs"
                    });
                }

                if (string.IsNullOrWhiteSpace(internalName))
                    continue;

                foreach (var textureRef in refs)
                    textureRef.MaterialName = internalName;

                yield return new MaterialDefinitionRecord
                {
                    MaterialName = internalName,
                    SourcePath = internalPath,
                    SourceArchivePath = spec.ArchivePath,
                    SourceKind = "terrain_cs",
                    Origin = spec.Origin,
                    TextureRefs = refs
                };
            }
        }

        private IEnumerable<MaterialReferenceRecord> ExtractJsonLikeReferences(
            MaterialScanSource spec,
            string internalPath,
            string rawText)
        {
            JsonDocument doc;

            try
            {
                doc = JsonDocument.Parse(rawText);
            }
            catch (JsonException)
            {
                yield break;
            }

            using (doc)
            {
                foreach (var reference in ExtractJsonLikeReferencesFromElement(
                             spec,
                             internalPath,
                             doc.RootElement))
                {
                    yield return reference;
                }
            }
        }

        private IEnumerable<MaterialReferenceRecord> ExtractJsonLikeReferencesFromElement(
            MaterialScanSource spec,
            string internalPath,
            JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    var key = prop.Name.Trim();

                    bool isMaterialKey =
                        key.Equals("material", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("materials", StringComparison.OrdinalIgnoreCase) ||
                        key.EndsWith("material", StringComparison.OrdinalIgnoreCase) ||
                        key.EndsWith("materials", StringComparison.OrdinalIgnoreCase);

                    if (isMaterialKey)
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var materialName = prop.Value.GetString()?.Trim() ?? string.Empty;

                            if (LooksLikeMaterialName(materialName))
                            {
                                yield return new MaterialReferenceRecord
                                {
                                    MaterialName = materialName,
                                    ReferencedByFile = internalPath,
                                    ReferencedByArchivePath = spec.ArchivePath,
                                    PropertyName = key,
                                    Origin = spec.Origin,
                                    Evidence = materialName
                                };
                            }
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in prop.Value.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.String)
                                    continue;

                                var materialName = item.GetString()?.Trim() ?? string.Empty;

                                if (!LooksLikeMaterialName(materialName))
                                    continue;

                                yield return new MaterialReferenceRecord
                                {
                                    MaterialName = materialName,
                                    ReferencedByFile = internalPath,
                                    ReferencedByArchivePath = spec.ArchivePath,
                                    PropertyName = key,
                                    Origin = spec.Origin,
                                    Evidence = materialName
                                };
                            }
                        }
                    }

                    foreach (var nested in ExtractJsonLikeReferencesFromElement(spec, internalPath, prop.Value))
                        yield return nested;
                }

                yield break;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in ExtractJsonLikeReferencesFromElement(spec, internalPath, item))
                        yield return nested;
                }
            }
        }

        private IEnumerable<MaterialReferenceRecord> ExtractLooseTextReferences(
            MaterialScanSource spec,
            string internalPath,
            string rawText)
        {
            var pattern = new Regex(
                "(?<key>[A-Za-z_][A-Za-z0-9_]*material)\\s*=\\s*\"(?<value>[^\"]+)\"",
                RegexOptions.IgnoreCase);

            foreach (Match match in pattern.Matches(rawText))
            {
                var key = match.Groups["key"].Value.Trim();
                var materialName = match.Groups["value"].Value.Trim();

                if (!key.EndsWith("material", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!LooksLikeMaterialName(materialName))
                    continue;

                yield return new MaterialReferenceRecord
                {
                    MaterialName = materialName,
                    ReferencedByFile = internalPath,
                    ReferencedByArchivePath = spec.ArchivePath,
                    PropertyName = key,
                    Origin = spec.Origin,
                    Evidence = match.Value
                };
            }
        }

        private void AddDefinition(MaterialFinderResult result, MaterialDefinitionRecord definition)
        {
            result.Definitions.Add(definition);
            Add(result.DefinitionsByMaterialName, definition.MaterialName.ToLowerInvariant(), definition);

            result.TraceRows.Add(new MaterialScanTraceRow
            {
                RowType = "DefinitionFound",
                Origin = definition.Origin,
                ArchivePath = definition.SourceArchivePath,
                InternalPath = definition.SourcePath,
                Classification = definition.SourceKind,
                Detail = definition.MaterialName,
                DefinitionCount = 1,
                ReferenceCount = 0
            });
        }

        private void Correlate(MaterialFinderResult result, MaterialAssetIndex assetIndex)
        {
            foreach (var definition in result.Definitions)
                definition.DependencyChecks = EvaluateTextureRefs(definition.TextureRefs, assetIndex);

            var groupedRefs = result.References
                .GroupBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groupedRefs)
            {
                var issue = new MaterialIssueRecord
                {
                    MaterialName = group.Key,
                    ReferenceCount = group.Count(),
                    References = group.ToList()
                };

                if (result.DefinitionsByMaterialName.TryGetValue(group.Key.ToLowerInvariant(), out var defs) && defs.Count > 0)
                {
                    issue.Definitions = defs;
                    issue.IssueType = defs.Any(x => x.DependencyChecks.Any(y => y.Status == "missing"))
                        ? "defined_but_broken"
                        : "defined_ok";
                }
                else
                {
                    issue.IssueType = "referenced_but_undefined";
                }

                result.Issues.Add(issue);
            }

            foreach (var definition in result.Definitions)
            {
                if (!result.References.Any(x => x.MaterialName.Equals(definition.MaterialName, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Issues.Add(new MaterialIssueRecord
                    {
                        MaterialName = definition.MaterialName,
                        IssueType = definition.DependencyChecks.Any(x => x.Status == "missing")
                            ? "defined_but_unreferenced_and_broken"
                            : "defined_but_unreferenced",
                        Definitions = new List<MaterialDefinitionRecord> { definition },
                        ReferenceCount = 0
                    });
                }
            }
        }

        private List<TextureDependencyCheck> EvaluateTextureRefs(List<TextureRef> refs, MaterialAssetIndex assetIndex)
        {
            var checks = new List<TextureDependencyCheck>();

            foreach (var textureRef in refs)
                checks.Add(EvaluateTextureRef(textureRef, assetIndex));

            return checks;
        }

        private TextureDependencyCheck EvaluateTextureRef(TextureRef textureRef, MaterialAssetIndex assetIndex)
        {
            var wantedLower = textureRef.NormalizedValue.ToLowerInvariant();
            var stem = Path.ChangeExtension(wantedLower, null)?.TrimEnd('.') ?? string.Empty;
            var basename = PathHelpers.Basename(wantedLower).ToLowerInvariant();
            var normName = PathHelpers.NormalizeNameOnly(wantedLower);

            if (assetIndex.Exact.TryGetValue(wantedLower, out var exact) && exact.Count > 0)
            {
                return new TextureDependencyCheck
                {
                    Texture = textureRef,
                    Status = "resolved",
                    MatchType = "exact_path",
                    Asset = exact[0]
                };
            }

            if (assetIndex.SamePathOtherExt.TryGetValue(stem, out var sameExt) && sameExt.Count > 0)
            {
                return new TextureDependencyCheck
                {
                    Texture = textureRef,
                    Status = "resolved",
                    MatchType = "same_path_other_ext",
                    Asset = sameExt[0]
                };
            }

            if (assetIndex.ByBase.TryGetValue(basename, out var byBase) && byBase.Count > 0)
            {
                return new TextureDependencyCheck
                {
                    Texture = textureRef,
                    Status = "resolved",
                    MatchType = "same_basename",
                    Asset = byBase[0]
                };
            }

            if (assetIndex.ByNormName.TryGetValue(normName, out var byNorm) && byNorm.Count > 0)
            {
                return new TextureDependencyCheck
                {
                    Texture = textureRef,
                    Status = "resolved",
                    MatchType = "normalized_name",
                    Asset = byNorm[0]
                };
            }

            return new TextureDependencyCheck
            {
                Texture = textureRef,
                Status = "missing",
                MatchType = "missing",
                Asset = null
            };
        }

        private static MaterialFinderStats BuildStats(MaterialFinderResult result)
        {
            return new MaterialFinderStats
            {
                DefinitionCount = result.Definitions.Count,
                ReferenceCount = result.References.Count,
                IssueCount = result.Issues.Count,
                ReferencedButUndefinedCount = result.Issues.Count(x => x.IssueType == "referenced_but_undefined"),
                DefinedButBrokenCount = result.Issues.Count(x =>
                    x.IssueType == "defined_but_broken" ||
                    x.IssueType == "defined_but_unreferenced_and_broken")
            };
        }

        private static bool ShouldKeepTextureValue(string value)
        {
            var v = value.Trim();
            if (string.IsNullOrWhiteSpace(v))
                return false;
            if (v.StartsWith("@", StringComparison.Ordinal))
                return false;
            return true;
        }

        private static bool LooksLikeMaterialName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            if (value.Contains('/') || value.Contains('\\'))
                return false;
            if (value.Contains('.'))
                return false;
            return true;
        }

        private static string StripLineComments(string text)
        {
            return Regex.Replace(text, @"//.*?$", string.Empty, RegexOptions.Multiline);
        }

        private static string Unquote(string value)
        {
            value = value.Trim();
            if (value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\""))
                return value.Substring(1, value.Length - 2);
            return value;
        }

        private static void Add<T>(Dictionary<string, List<T>> dict, string key, T value)
        {
            if (!dict.TryGetValue(key, out var list))
            {
                list = new List<T>();
                dict[key] = list;
            }

            list.Add(value);
        }
    }

    public sealed class MaterialFinderRequest
    {
        public string ModZipPath { get; set; } = string.Empty;
        public string CurrentFolder { get; set; } = string.Empty;
        public string OldFolder { get; set; } = string.Empty;
        public bool ScanReferencesInContentFolders { get; set; }
    }

    public sealed class MaterialFinderResult
    {
        public MaterialFinderRequest? Request { get; set; }
        public List<MaterialDefinitionRecord> Definitions { get; set; } = new();
        public List<MaterialReferenceRecord> References { get; set; } = new();
        public List<MaterialIssueRecord> Issues { get; set; } = new();
        public List<MaterialScanTraceRow> TraceRows { get; set; } = new();

        public Dictionary<string, List<MaterialDefinitionRecord>> DefinitionsByMaterialName { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public MaterialFinderStats Stats { get; set; } = new();
    }

    public sealed class MaterialDefinitionRecord
    {
        public string MaterialName { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string SourceArchivePath { get; set; } = string.Empty;
        public string SourceKind { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public List<TextureRef> TextureRefs { get; set; } = new();
        public List<TextureDependencyCheck> DependencyChecks { get; set; } = new();
    }

    public sealed class MaterialReferenceRecord
    {
        public string MaterialName { get; set; } = string.Empty;
        public string ReferencedByFile { get; set; } = string.Empty;
        public string ReferencedByArchivePath { get; set; } = string.Empty;
        public string PropertyName { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public string Evidence { get; set; } = string.Empty;
    }

    public sealed class MaterialIssueRecord
    {
        public string MaterialName { get; set; } = string.Empty;
        public string IssueType { get; set; } = string.Empty;
        public int ReferenceCount { get; set; }
        public List<MaterialReferenceRecord> References { get; set; } = new();
        public List<MaterialDefinitionRecord> Definitions { get; set; } = new();
    }

    public sealed class TextureDependencyCheck
    {
        public TextureRef Texture { get; set; } = new();
        public string Status { get; set; } = string.Empty;
        public string MatchType { get; set; } = string.Empty;
        public MaterialAssetRecord? Asset { get; set; }
    }

    public sealed class MaterialAssetRecord
    {
        public string Origin { get; set; } = string.Empty;
        public string ArchivePath { get; set; } = string.Empty;
        public string InternalPath { get; set; } = string.Empty;
    }

    public sealed class MaterialFinderStats
    {
        public int DefinitionCount { get; set; }
        public int ReferenceCount { get; set; }
        public int IssueCount { get; set; }
        public int ReferencedButUndefinedCount { get; set; }
        public int DefinedButBrokenCount { get; set; }
    }

    public sealed class MaterialScanTraceRow
    {
        public string RowType { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public string ArchivePath { get; set; } = string.Empty;
        public string InternalPath { get; set; } = string.Empty;
        public string Classification { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
        public int DefinitionCount { get; set; }
        public int ReferenceCount { get; set; }
    }

    internal sealed class MaterialAssetIndex
    {
        public Dictionary<string, List<MaterialAssetRecord>> Exact { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<MaterialAssetRecord>> ByBase { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<MaterialAssetRecord>> ByNormName { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, List<MaterialAssetRecord>> SamePathOtherExt { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class MaterialScanSource
    {
        public string Origin { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string ArchivePath { get; set; } = string.Empty;
        public bool ScanDefinitions { get; set; }
        public bool ScanReferences { get; set; }
    }

    internal sealed class ReadTextResult
    {
        public string? Text { get; set; }
        public string EncodingName { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }
}