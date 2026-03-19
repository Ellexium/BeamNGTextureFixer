using BeamNGTextureFixer.Helpers;
using BeamNGTextureFixer.Models;
using BeamNGTextureFixer.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace BeamNGTextureFixer.ViewModels
{
    public class MainViewModel : ViewModelBase
    {

        public RelayCommand ClearOldCommand { get; }
        public RelayCommand ClearCurrentCommand { get; }
        public RelayCommand ClearModsCommand { get; }

        private bool _replaceOriginalMod;
        public bool ReplaceOriginalMod
        {
            get => _replaceOriginalMod;
            set => SetProperty(ref _replaceOriginalMod, value);
        }
        private static int CountMaterialFilesInZip(string zipPath, CancellationToken token)
        {
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);

                int count = 0;
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();

                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/"))
                        continue;

                    var norm = PathHelpers.NormalizePath(entry.FullName);
                    if (norm.EndsWith(".materials.json", StringComparison.OrdinalIgnoreCase) ||
                        norm.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase) ||
                        norm.EndsWith("materials.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }
                }

                return count;
            }
            catch (InvalidDataException)
            {
                return 0;
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private bool _useNormalizedCurrentContentFixes;

        public bool UseNormalizedCurrentContentFixes
        {
            get => _useNormalizedCurrentContentFixes;
            set => SetProperty(ref _useNormalizedCurrentContentFixes, value);
        }

        private CancellationTokenSource? _cts;
        private bool _closeAfterAbort;

        private bool _canAbort;
        public bool CanAbort
        {
            get => _canAbort;
            set
            {
                if (SetProperty(ref _canAbort, value))
                    AbortCommand.RaiseCanExecuteChanged();
            }
        }

        public event Action? RequestCloseRequested;

        public RelayCommand AbortCommand { get; }

        private int _selectedMainTabIndex;
        public int SelectedMainTabIndex
        {
            get => _selectedMainTabIndex;
            set
            {
                if (SetProperty(ref _selectedMainTabIndex, value))
                    UpdateStatusSummary();
            }
        }

        private readonly BeamNGFixerService _service = new();

        private string _oldContentFolder = string.Empty;
        public string OldContentFolder
        {
            get => _oldContentFolder;
            set
            {
                if (SetProperty(ref _oldContentFolder, value))
                {
                    BeamNGFixerService.ClearIndexCache();
                    RefreshActionButtons();
                }
            }
        }

        private string _currentContentFolder = string.Empty;
        public string CurrentContentFolder
        {
            get => _currentContentFolder;
            set
            {
                if (SetProperty(ref _currentContentFolder, value))
                {
                    BeamNGFixerService.ClearIndexCache();
                    RefreshActionButtons();
                }
            }
        }

        private string _selectedModsDisplay = string.Empty;
        public string SelectedModsDisplay
        {
            get => _selectedModsDisplay;
            set
            {
                if (SetProperty(ref _selectedModsDisplay, value))
                    RefreshActionButtons();
            }
        }

        private string _statusText = "Ready.";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private string _summaryText = "No scan yet.";
        public string SummaryText
        {
            get => _summaryText;
            set => SetProperty(ref _summaryText, value);
        }

        public ObservableCollection<BatchResultRow> BatchResults { get; } = new();
        private List<DetailRow> _detailRows = new();
        public List<DetailRow> DetailRows
        {
            get => _detailRows;
            set => SetProperty(ref _detailRows, value);
        }

        private List<SecondPassRow> _secondPassRows = new();
        public List<SecondPassRow> SecondPassRows
        {
            get => _secondPassRows;
            set => SetProperty(ref _secondPassRows, value);
        }

        private List<ThirdPassRow> _thirdPassRows = new();
        public List<ThirdPassRow> ThirdPassRows
        {
            get => _thirdPassRows;
            set => SetProperty(ref _thirdPassRows, value);
        }

        private string _secondPassSummaryText = "No second pass data yet.";
        public string SecondPassSummaryText
        {
            get => _secondPassSummaryText;
            set => SetProperty(ref _secondPassSummaryText, value);
        }

        private string _thirdPassSummaryText = "No third pass data yet.";
        public string ThirdPassSummaryText
        {
            get => _thirdPassSummaryText;
            set => SetProperty(ref _thirdPassSummaryText, value);
        }

        private BatchResultRow? _selectedBatchResult;
        public BatchResultRow? SelectedBatchResult
        {
            get => _selectedBatchResult;
            set
            {
                if (SetProperty(ref _selectedBatchResult, value))
                {
                    LoadDetailRows(value);
                    IsBusy = false;
                    ExportTextureReportCommand.RaiseCanExecuteChanged();
                    UpdateStatusSummary();
                }
            }
        }

        public List<string> SelectedMods { get; } = new();

        public RelayCommand BrowseOldCommand { get; }
        public RelayCommand BrowseCurrentCommand { get; }
        public RelayCommand BrowseModsCommand { get; }
        public RelayCommand ScanCommand { get; }
        public RelayCommand BuildCommand { get; }
        public RelayCommand AggressivePassCommand { get; }


        public RelayCommand ExportTextureReportCommand { get; }
        public RelayCommand ExportMaterialReportCommand { get; }
        public RelayCommand ExportThirdPassReportCommand { get; }

        private void ClearOldFolder()
        {
            OldContentFolder = string.Empty;
        }

        private void ClearCurrentFolder()
        {
            CurrentContentFolder = string.Empty;
        }

        private void ClearMods()
        {
            SelectedMods.Clear();
            SelectedModsDisplay = string.Empty;
            RefreshActionButtons();
        }

        public MainViewModel()
        {
            BrowseOldCommand = new RelayCommand(BrowseOldFolder);
            BrowseCurrentCommand = new RelayCommand(BrowseCurrentFolder);
            BrowseModsCommand = new RelayCommand(BrowseMods);

            ClearOldCommand = new RelayCommand(ClearOldFolder);
            ClearCurrentCommand = new RelayCommand(ClearCurrentFolder);
            ClearModsCommand = new RelayCommand(ClearMods);

            ScanCommand = new RelayCommand(ScanMods, CanScan);
            BuildCommand = new RelayCommand(BuildModsPlaceholder, CanBuildFirstPass);
            AggressivePassCommand = new RelayCommand(RunAggressivePassPlaceholder, CanRunAggressivePass);

            AbortCommand = new RelayCommand(AbortWork, () => CanAbort);

            ExportTextureReportCommand = new RelayCommand(ExportTextureReport);
            ExportMaterialReportCommand = new RelayCommand(ExportMaterialReport);
            ExportThirdPassReportCommand = new RelayCommand(ExportThirdPassReport);

            // ExportTextureReportCommand = new RelayCommand(ExportTextureReport, () => SelectedBatchResult is not null && DetailRows.Count > 0);
        }


        private bool CanScan()
        {
            if (IsBusy)
                return false;

            return !string.IsNullOrWhiteSpace(OldContentFolder)
                && !string.IsNullOrWhiteSpace(CurrentContentFolder)
                && SelectedMods.Count > 0;
        }

        private bool CanBuildFirstPass()
        {
            if (IsBusy)
                return false;

            return BatchResults.Any(x =>
                !string.Equals(x.BuildStatus, "built", StringComparison.OrdinalIgnoreCase) &&
                x.ResolvedFromOld > 0);
        }

        private bool CanRunAggressivePass()
        {
            if (IsBusy)
                return false;

            return BatchResults.Any(x =>
                string.Equals(x.BuildStatus, "built", StringComparison.OrdinalIgnoreCase) &&
                !x.AggressivePassRan);
        }

        private void RefreshActionButtons()
        {
            ScanCommand.RaiseCanExecuteChanged();
            BuildCommand.RaiseCanExecuteChanged();
            AggressivePassCommand.RaiseCanExecuteChanged();
        }


        private void ExportTextureReport()
        {
            if (SelectedBatchResult is null)
            {
                MessageBox.Show("Select a mod first.", "First Pass");
                return;
            }

            try
            {
                var csvPath = Path.Combine(
                    Path.GetDirectoryName(SelectedBatchResult.ModZip) ?? "",
                    Path.GetFileNameWithoutExtension(SelectedBatchResult.ModZip) + " - first pass report.csv");

                using var writer = new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(true));

                writer.WriteLine("MaterialFile,Mode,Material,Stage,Key,OriginalPath,Status,MatchType,Source,NewPath");

                foreach (var row in SelectedBatchResult.DetailRows)
                {
                    WriteCsvRow(writer, new[]
                    {
                        row.MaterialFile,
                        row.ParseMode,
                        row.MaterialName,
                        row.StageIndex.ToString(),
                        row.Key,
                        row.OriginalPath,
                        row.Status,
                        row.MatchType,
                        row.Source,
                        row.NewPath
                    });
                }

                MessageBox.Show($"First pass report exported:\n\n{csvPath}", "First Pass");

                Process.Start(new ProcessStartInfo
                {
                    FileName = csvPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to export first pass report:\n\n" + ex.Message, "First Pass");
            }
        }

        private void ExportMaterialReport()
        {
            if (SelectedBatchResult is null)
            {
                MessageBox.Show("Select a mod first.", "Second Pass");
                return;
            }

            try
            {
                var csvPath = Path.Combine(
                    Path.GetDirectoryName(SelectedBatchResult.ModZip) ?? "",
                    Path.GetFileNameWithoutExtension(SelectedBatchResult.ModZip) + " - second pass table.csv");

                using var writer = new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(true));

                WriteCsvRow(writer, new[]
                {
                    "Material",
                    "Ref Count",
                    "Status",
                    "Defined In",
                    "Definition Origin",
                    "Color",
                    "Normal",
                    "Specular",
                    "Generated",
                    "Notes"
                });

                foreach (var row in SecondPassRows)
                {
                    WriteCsvRow(writer, new[]
                    {
                        row.MaterialName,
                        row.ReferenceCount.ToString(),
                        row.Status,
                        row.DefinitionPath,
                        row.DefinitionOrigin,
                        row.ColorMap,
                        row.NormalMap,
                        row.SpecularMap,
                        row.GeneratedDefinition,
                        row.Notes
                    });
                }

                MessageBox.Show($"Second pass table exported:\n\n{csvPath}", "Second Pass");

                Process.Start(new ProcessStartInfo
                {
                    FileName = csvPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to export second pass table:\n\n" + ex.Message, "Second Pass");
            }
        }

        private void ExportThirdPassReport()
        {
            if (SelectedBatchResult is null)
            {
                MessageBox.Show("Select a mod first.", "Third Pass");
                return;
            }

            try
            {
                var csvPath = Path.Combine(
                    Path.GetDirectoryName(SelectedBatchResult.ModZip) ?? "",
                    Path.GetFileNameWithoutExtension(SelectedBatchResult.ModZip) + " - third pass table.csv");

                using var writer = new StreamWriter(csvPath, false, new System.Text.UTF8Encoding(true));

                WriteCsvRow(writer, new[]
                {
                    "Material",
                    "Pre-Status",
                    "Should Localize",
                    "Action Taken",
                    "Imported From",
                    "Injected Into",
                    "Textures Copied",
                    "Final Status",
                    "Notes"
                });

                foreach (var row in ThirdPassRows)
                {
                    WriteCsvRow(writer, new[]
                    {
                        row.MaterialName,
                        row.PreStatus,
                        row.ShouldLocalize,
                        row.ActionTaken,
                        row.ImportedFrom,
                        row.InjectedInto,
                        row.TexturesCopied.ToString(),
                        row.FinalStatus,
                        row.Notes
                    });
                }

                MessageBox.Show($"Third pass table exported:\n\n{csvPath}", "Third Pass");

                Process.Start(new ProcessStartInfo
                {
                    FileName = csvPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to export third pass table:\n\n" + ex.Message, "Third Pass");
            }
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
        //private void ExportTextureReport()
        //{
        //    if (SelectedBatchResult is null)
        //    {
        //        MessageBox.Show("No mod selected.", "Export Texture Report");
        //        return;
        //    }

        //    try
        //    {
        //        var modZipPath = SelectedBatchResult.ModZip;
        //        var modFolder = Path.GetDirectoryName(modZipPath);

        //        if (string.IsNullOrWhiteSpace(modFolder) || !Directory.Exists(modFolder))
        //        {
        //            MessageBox.Show("Could not determine the mod folder.", "Export Texture Report");
        //            return;
        //        }

        //        var modNameWithoutExt = Path.GetFileNameWithoutExtension(SelectedBatchResult.ModName);
        //        var reportPath = Path.Combine(modFolder, $"{modNameWithoutExt}- texture report.csv");

        //        var lines = new List<string>
        //{
        //    string.Join(",",
        //        Csv("Material File"),
        //        Csv("Mode"),
        //        Csv("Material"),
        //        Csv("Stage"),
        //        Csv("Key"),
        //        Csv("Original Path"),
        //        Csv("Status"),
        //        Csv("Match Type"),
        //        Csv("Source"),
        //        Csv("New Path"))
        //};

        //        foreach (var row in DetailRows)
        //        {
        //            lines.Add(string.Join(",",
        //                Csv(row.MaterialFile),
        //                Csv(row.ParseMode),
        //                Csv(row.MaterialName),
        //                Csv(row.StageIndex.ToString()),
        //                Csv(row.Key),
        //                Csv(row.OriginalPath),
        //                Csv(row.Status),
        //                Csv(row.MatchType),
        //                Csv(row.Source),
        //                Csv(row.NewPath)));
        //        }

        //        File.WriteAllLines(reportPath, lines);

        //        StatusText = $"Texture report exported: {Path.GetFileName(reportPath)}";
        //        MessageBox.Show($"Texture report saved to:\n{reportPath}", "Export Complete");
        //    }
        //    catch (Exception ex)
        //    {
        //        MessageBox.Show(ex.Message, "Export Texture Report");
        //    }
        //}

        private static string Csv(string? value)
        {
            value ??= string.Empty;
            value = value.Replace("\"", "\"\"");
            return $"\"{value}\"";
        }
        private void BeginBusy()
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            IsBusy = true;
            CanAbort = true;
            IsProgressIndeterminate = true;
            ProgressValue = 0;
            ProgressMaximum = 1;

            RefreshActionButtons();
        }

        private void EndBusy()
        {
            IsBusy = false;
            CanAbort = false;
            IsProgressIndeterminate = true;
            ProgressValue = 0;
            ProgressMaximum = 1;

            _cts?.Dispose();
            _cts = null;

            RefreshActionButtons();

            if (_closeAfterAbort)
            {
                _closeAfterAbort = false;
                RequestCloseRequested?.Invoke();
            }
        }

        private void AbortWork()
        {
            if (_cts is null)
                return;

            StatusText = "Aborting...";
            _cts.Cancel();
        }

        public void AbortAndClose()
        {
            _closeAfterAbort = true;
            AbortWork();
        }
        private void BrowseOldFolder()
        {
            using var dialog = new Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == Forms.DialogResult.OK)
                OldContentFolder = dialog.SelectedPath;
        }

        private void BrowseCurrentFolder()
        {
            using var dialog = new Forms.FolderBrowserDialog();
            if (dialog.ShowDialog() == Forms.DialogResult.OK)
                CurrentContentFolder = dialog.SelectedPath;
        }

        private void BrowseMods()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Target Mod Zip(s)",
                Filter = "Zip files (*.zip)|*.zip|All files (*.*)|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                SelectedMods.Clear();
                SelectedMods.AddRange(dialog.FileNames);
                SelectedModsDisplay = SelectedMods.Count == 1
                    ? SelectedMods[0]
                    : $"{SelectedMods.Count} mod(s) selected";

                RefreshActionButtons();
            }
        }

        private async void ScanMods()
        {
            if (SelectedMods.Count == 0)
            {
                MessageBox.Show("Please choose one or more target mod zips.", "Missing Mod Zips");
                return;
            }

            if (string.IsNullOrWhiteSpace(OldContentFolder) || !Directory.Exists(OldContentFolder))
            {
                MessageBox.Show("Please choose a valid old BeamNG content folder.", "Missing Old Content Folder");
                return;
            }

            BatchResults.Clear();
            RefreshActionButtons();
            DetailRows = new List<DetailRow>();
            SecondPassRows = new List<SecondPassRow>();
            ThirdPassRows = new List<ThirdPassRow>();
            SummaryText = "No scan yet.";
            SecondPassSummaryText = "No second pass data yet.";
            ThirdPassSummaryText = "No third pass data yet.";

            BeginBusy();
            RefreshActionButtons();
            StatusText = "Counting material files...";

            IsProgressIndeterminate = true;

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.Render);

            var token = _cts?.Token ?? CancellationToken.None;

            try
            {
                int totalMaterialFiles = await Task.Run(() =>
                {
                    int total = 0;
                    foreach (var modZip in SelectedMods)
                    {
                        token.ThrowIfCancellationRequested();
                        total += CountMaterialFilesInZip(modZip, token);
                    }
                    return total;
                }, token);

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsProgressIndeterminate = false;
                    ProgressMaximum = Math.Max(totalMaterialFiles, 1);
                    ProgressValue = 0;
                    StatusText = $"Scanning materials... 0 / {totalMaterialFiles}";
                });

                var scannedRows = await Task.Run(() =>
                {
                    var rows = new List<BatchResultRow>();
                    int totalRefs = 0;
                    int totalOld = 0;
                    int totalUnresolved = 0;

                    int globalProcessedMaterials = 0;

                    foreach (var modZip in SelectedMods)
                    {
                        token.ThrowIfCancellationRequested();

                        try
                        {
                            var service = new BeamNGFixerService();
                            var payload = service.Scan(
                                modZip,
                                OldContentFolder,
                                string.IsNullOrWhiteSpace(CurrentContentFolder) ? null : CurrentContentFolder,
                                token,
                                (doneInThisMod, totalInThisMod, message) =>
                                {
                                    int absoluteDone = globalProcessedMaterials + doneInThisMod;

                                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        IsProgressIndeterminate = false;
                                        ProgressMaximum = Math.Max(totalMaterialFiles, 1);
                                        ProgressValue = Math.Min(absoluteDone, totalMaterialFiles);
                                        StatusText = $"{Path.GetFileName(modZip)} - scanning materials... {Math.Min(absoluteDone, totalMaterialFiles)} / {totalMaterialFiles}";
                                    });
                                });

                            

                            var row = new BatchResultRow
                            {
                                ModZip = modZip,
                                ModName = Path.GetFileName(modZip),
                                MaterialFiles = payload.MaterialFiles,
                                TextureRefs = payload.TextureRefs,
                                PresentInMod = payload.PresentInMod,
                                SatisfiedByCurrent = payload.SatisfiedByCurrent,
                                ResolvedFromOld = payload.ResolvedFromOld,
                                Unresolved = payload.Unresolved,
                                BuildStatus = "not built",
                                FixesMade = 0,
                                OutZip = "",
                                AggressivePassRan = false,
                                AggressivePassStatus = "not run",
                                Service = service,
                            };

                            var modStem = PathHelpers.SanitizeModStem(Path.GetFileName(service.ModZipPath));
                            var collisionCounts = service.BasenameCollisionsWithinSourceZip();

                            foreach (var pair in payload.Results)
                            {
                                token.ThrowIfCancellationRequested();

                                var newPath = "";

                                if (pair.Hit.Status == "resolved_from_old")
                                    newPath = service.MakeMissingfilefixTarget(pair.Hit, modStem, collisionCounts);

                                row.DetailRows.Add(new DetailRow
                                {
                                    MaterialFile = pair.Ref.MaterialFile,
                                    ParseMode = pair.Ref.ExtractionMode,
                                    MaterialName = pair.Ref.MaterialName,
                                    StageIndex = pair.Ref.StageIndex,
                                    Key = pair.Ref.Key,
                                    OriginalPath = pair.Ref.OriginalValue,
                                    Status = pair.Hit.Status,
                                    MatchType = pair.Hit.MatchType,
                                    Source = !string.IsNullOrWhiteSpace(pair.Hit.SourceZipPath)
                                        ? $"{Path.GetFileName(pair.Hit.SourceZipPath)} :: {pair.Hit.InternalPath}"
                                        : pair.Hit.InternalPath ?? "",
                                    NewPath = newPath
                                });
                            }

                            rows.Add(row);
                            totalRefs += payload.TextureRefs;
                            totalOld += payload.ResolvedFromOld;
                            totalUnresolved += payload.Unresolved;
                            globalProcessedMaterials += payload.MaterialFiles;
                        }
                        catch (InvalidDataException)
                        {
                            rows.Add(new BatchResultRow
                            {
                                ModZip = modZip,
                                ModName = Path.GetFileName(modZip),
                                BuildStatus = "invalid zip",
                                FixesMade = 0,
                                OutZip = ""
                            });
                        }
                        catch (IOException)
                        {
                            rows.Add(new BatchResultRow
                            {
                                ModZip = modZip,
                                ModName = Path.GetFileName(modZip),
                                BuildStatus = "read error",
                                FixesMade = 0,
                                OutZip = ""
                            });
                        }
                    }

                    return new
                    {
                        Rows = rows,
                        TotalRefs = totalRefs,
                        TotalOld = totalOld,
                        TotalUnresolved = totalUnresolved
                    };
                }, token);

                BatchResults.Clear();
                foreach (var row in scannedRows.Rows)
                    BatchResults.Add(row);
                    OnPropertyChanged(nameof(FixableMods));

                SummaryText =
                    $"Scanned {BatchResults.Count} mod(s)\n" +
                    $"Total referenced texture paths: {scannedRows.TotalRefs}\n" +
                    $"Total fixable references found: {scannedRows.TotalOld}\n" +
                    $"Total unresolved: {scannedRows.TotalUnresolved}\n" +
                    $"Build will only create *_fixed.zip for mods with new paths found.";

                if (FixableMods.Count > 0)
                    SelectedBatchResult = FixableMods[0];
                else
                    SelectedBatchResult = null;
            }
            catch (OperationCanceledException)
            {
                BatchResults.Clear();
                DetailRows = new List<DetailRow>();
                SelectedBatchResult = null;
                SummaryText = "Scan aborted.";
                StatusText = "Scan aborted.";
            }
            catch (Exception ex)
            {
                BatchResults.Clear();
                DetailRows = new List<DetailRow>();
                SelectedBatchResult = null;
                SummaryText = "Scan failed.";
                StatusText = "Error during scan.";
                MessageBox.Show(ex.Message, "Scan Error");
            }
            finally
            {
                EndBusy();

                if (BatchResults.Count > 0)
                    UpdateStatusSummary();
                    RefreshActionButtons();
            }
        }

        private readonly Dictionary<string, MaterialFinderResult> _materialFinderResultsByTargetZip = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GeneratedMaterialDefinitionResult> _generatedResultsByTargetZip = new(StringComparer.OrdinalIgnoreCase);
        private void BindSecondPass(BatchResultRow? batchRow)
        {
            if (batchRow is null)
            {
                SecondPassRows = new List<SecondPassRow>();
                SecondPassSummaryText = "No second pass data yet.";
                return;
            }

            var targetZip = GetTargetZipForRow(batchRow);

            if (string.IsNullOrWhiteSpace(targetZip) ||
                !_materialFinderResultsByTargetZip.TryGetValue(targetZip, out var materialFinderResult))
            {
                SecondPassRows = new List<SecondPassRow>();
                SecondPassSummaryText = "No second pass data yet.";
                return;
            }

            static bool IsUnreferencedIssue(string? issueType) =>
                string.Equals(issueType, "defined_but_unreferenced", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(issueType, "defined_but_unreferenced_and_broken", StringComparison.OrdinalIgnoreCase);

            var visibleIssues = materialFinderResult.Issues
                .Where(issue => !IsUnreferencedIssue(issue.IssueType))
                .OrderBy(issue => issue.MaterialName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = visibleIssues
                .Select(issue =>
                {
                    var primaryDef = issue.Definitions
                        .OrderBy(d => d.Origin == "mod" ? 0 : d.Origin == "current_content" ? 1 : 2)
                        .ThenBy(d => d.SourceArchivePath, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(d => d.SourcePath, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();

                    string colorMap = "";
                    string normalMap = "";
                    string specularMap = "";

                    if (primaryDef is not null)
                    {
                        colorMap = primaryDef.TextureRefs
                            .FirstOrDefault(t => t.Key.Equals("colorMap", StringComparison.OrdinalIgnoreCase)
                                              || t.Key.Equals("baseColorMap", StringComparison.OrdinalIgnoreCase))
                            ?.OriginalValue ?? "";

                        normalMap = primaryDef.TextureRefs
                            .FirstOrDefault(t => t.Key.Equals("normalMap", StringComparison.OrdinalIgnoreCase))
                            ?.OriginalValue ?? "";

                        specularMap = primaryDef.TextureRefs
                            .FirstOrDefault(t => t.Key.Equals("specularMap", StringComparison.OrdinalIgnoreCase))
                            ?.OriginalValue ?? "";
                    }

                    return new SecondPassRow
                    {
                        MaterialName = issue.MaterialName,
                        ReferenceCount = issue.ReferenceCount,
                        Status = issue.IssueType,
                        DefinitionPath = primaryDef?.SourcePath ?? "",
                        DefinitionOrigin = primaryDef?.Origin ?? "",
                        ColorMap = colorMap,
                        NormalMap = normalMap,
                        SpecularMap = specularMap,
                        GeneratedDefinition = issue.IssueType == "referenced_but_undefined" ? "candidate possible" : "",
                        Notes = BuildSecondPassNotes(issue, primaryDef)
                    };
                })
                .ToList();

            SecondPassRows = rows;

            SecondPassSummaryText =
                $"Second Pass materials tracked: {rows.Count}\n" +
                $"Defined OK: {rows.Count(x => x.Status == "defined_ok")}\n" +
                $"Defined but broken: {rows.Count(x => x.Status == "defined_but_broken")}\n" +
                $"Referenced but undefined: {rows.Count(x => x.Status == "referenced_but_undefined")}";
        }

        private string GetTargetZipForRow(BatchResultRow row)
        {
            return ReplaceOriginalMod ? row.ModZip : row.OutZip;
        }

        private void BindThirdPass(BatchResultRow? batchRow)
        {
            if (batchRow is null)
            {
                ThirdPassRows = new List<ThirdPassRow>();
                ThirdPassSummaryText = "No third pass data yet.";
                return;
            }

            var targetZip = GetTargetZipForRow(batchRow);

            if (string.IsNullOrWhiteSpace(targetZip) ||
                !_materialFinderResultsByTargetZip.TryGetValue(targetZip, out var materialFinderResult))
            {
                ThirdPassRows = new List<ThirdPassRow>();
                ThirdPassSummaryText = "No third pass data yet.";
                return;
            }


            _generatedResultsByTargetZip.TryGetValue(targetZip, out var generatedResult);

            var issueRows = materialFinderResult.Issues
                .Where(x =>
                    !string.Equals(x.IssueType, "defined_but_unreferenced", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(x.IssueType, "defined_but_unreferenced_and_broken", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase)
                .Select(issue =>
                {
                    var primaryDef = PickPrimaryDefinition(issue.Definitions);

                    var candidate = generatedResult?.Candidates
                        .FirstOrDefault(c => string.Equals(c.MaterialName, issue.MaterialName, StringComparison.OrdinalIgnoreCase));

                    bool isReferenced =
                        !string.Equals(issue.IssueType, "defined_but_unreferenced", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(issue.IssueType, "defined_but_unreferenced_and_broken", StringComparison.OrdinalIgnoreCase);

                    bool candidateBuildable =
                        candidate is not null &&
                        !string.Equals(candidate.GenerationStatus, "no_generated_candidate", StringComparison.OrdinalIgnoreCase);

                    bool shouldLocalize =
                        isReferenced &&
                        (
                            (primaryDef is not null && !string.Equals(primaryDef.Origin, "mod", StringComparison.OrdinalIgnoreCase))
                            || candidateBuildable
                        );

                    string importedFrom = primaryDef is null
                        ? ""
                        : $"{primaryDef.Origin} :: {primaryDef.SourceArchivePath} :: {primaryDef.SourcePath}";

                    string injectedInto = shouldLocalize
                        ? "tfgenerated.materials.json"
                        : "";

                    string actionTaken = shouldLocalize
                        ? "recreate definition in tfgenerated.materials.json"
                        : "leave as-is";

                    string finalStatus = shouldLocalize
                        ? "ready_for_aggressive_localize"
                        : "no_aggressive_action_needed";

                    string notes;
                    if (primaryDef is null && !candidateBuildable)
                    {
                        notes = issue.IssueType == "referenced_but_undefined"
                            ? "No definition was found anywhere, and no viable generated candidate exists."
                            : "No primary definition found.";
                    }
                    else if (candidateBuildable && primaryDef is null)
                    {
                        notes = "No definition exists, but a generated material candidate will be used.";
                    }
                    else if (shouldLocalize)
                    {
                        notes = "Definition exists outside the mod. Aggressive pass will recreate this material locally.";
                    }
                    else
                    {
                        notes = "Definition is already inside the mod, so aggressive localization is not needed.";
                    }

                    int textureFilesReferenced;
                    int texturesCopied;

                    // CASE 1: External definition
                    if (primaryDef is not null)
                    {
                        var referencedPaths = primaryDef.TextureRefs
                            .Where(t => !string.IsNullOrWhiteSpace(t.OriginalValue))
                            .Select(t => t.OriginalValue.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        textureFilesReferenced = referencedPaths.Count;

                        texturesCopied = shouldLocalize
                            ? referencedPaths.Count
                            : 0;
                    }
                    // CASE 2: Generated candidate
                    else if (candidateBuildable && candidate is not null)
                    {
                        var generatedPaths = candidate.Slots
                            .Select(s => s.Value)
                            .Where(v => v != null && !string.IsNullOrWhiteSpace(v.InternalPath))
                            .Select(v => v!.InternalPath.Trim())
                            .ToList();

                        textureFilesReferenced = generatedPaths.Count;
                        texturesCopied = generatedPaths.Count;
                    }
                    // CASE 3: Nothing usable
                    else
                    {
                        textureFilesReferenced = 0;
                        texturesCopied = 0;
                    }

                    return new ThirdPassRow
                    {
                        MaterialName = issue.MaterialName,
                        PreStatus = issue.IssueType,
                        ShouldLocalize = shouldLocalize ? "Yes" : "No",
                        ActionTaken = actionTaken,
                        ImportedFrom = importedFrom,
                        InjectedInto = injectedInto,
                        TextureFilesReferenced = textureFilesReferenced,
                        TexturesCopied = texturesCopied,
                        FinalStatus = finalStatus,
                        Notes = notes
                    };
                })
                .ToList();

            ThirdPassRows = issueRows;

            ThirdPassSummaryText =
                $"Third Pass materials tracked: {issueRows.Count}\n" +
                $"Should localize: {issueRows.Count(x => x.ShouldLocalize == "Yes")}\n" +
                $"Already local to mod: {issueRows.Count(x => x.ShouldLocalize == "No")}\n" +
                $"Total texture files referenced: {issueRows.Sum(x => x.TextureFilesReferenced)}\n" +
                $"Total textures copied: {issueRows.Sum(x => x.TexturesCopied)}";
        }

        private static bool IsUnreferencedIssue(string? issueType)
        {
            return string.Equals(issueType, "defined_but_unreferenced", StringComparison.OrdinalIgnoreCase)
                || string.Equals(issueType, "defined_but_unreferenced_and_broken", StringComparison.OrdinalIgnoreCase);
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
        public List<BatchResultRow> FixableMods =>
            BatchResults.Where(x => x.ResolvedFromOld > 0).ToList();

        private void LoadDetailRows(BatchResultRow? row)
        {
            if (row is null)
            {
                DetailRows = new List<DetailRow>();
                SecondPassRows = new List<SecondPassRow>();
                ThirdPassRows = new List<ThirdPassRow>();
                SecondPassSummaryText = "No second pass data yet.";
                ThirdPassSummaryText = "No third pass data yet.";
                UpdateStatusSummary();
                return;
            }

            DetailRows = row.DetailRows.ToList();

            BindSecondPass(row);
            BindThirdPass(row);

            UpdateStatusSummary();
        }



        private static string BuildSecondPassNotes(MaterialIssueRecord issue, MaterialDefinitionRecord? primaryDef)
        {
            if (issue.IssueType == "referenced_but_undefined")
                return "Material is referenced by the mod, but no definition was found in scanned sources.";

            if (primaryDef is null)
                return "No primary definition selected.";

            if (issue.IssueType == "defined_but_broken")
            {
                int missingCount = primaryDef.DependencyChecks.Count(x => x.Status == "missing");
                return $"Definition found, but {missingCount} texture dependency/dependencies are unresolved.";
            }

            if (issue.IssueType == "defined_ok")
                return "Definition found and all known texture dependencies resolved.";

            if (issue.IssueType == "defined_but_unreferenced")
                return "Definition exists, but no material references were found in scanned text files.";

            if (issue.IssueType == "defined_but_unreferenced_and_broken")
                return "Definition exists and is unreferenced, and at least one texture dependency is unresolved.";

            return "";
        }

       

        private async void BuildModsPlaceholder()
        {
            if (BatchResults.Count == 0)
            {
                MessageBox.Show("Scan one or more mods first.", "No Scan");
                return;
            }

            var remainingRows = BatchResults
                .Where(x => !string.Equals(x.BuildStatus, "built", StringComparison.OrdinalIgnoreCase)
                         && x.ResolvedFromOld > 0)
                .ToList();

            if (remainingRows.Count == 0)
            {
                StatusText = "All fixable mods are already built.";
                return;
            }

            if (ReplaceOriginalMod)
            {
                var result = MessageBox.Show(
                    "This will replace the original selected mod zip file(s).\n\nContinue?",
                    "Replace Original Mod",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            var allRows = BatchResults.ToList();
            var rowsToBuild = remainingRows;

            BeginBusy();
            StatusText = "Building fixed mods...";

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.Render);

            var token = _cts?.Token ?? CancellationToken.None;

            try
            {
                var selectedPathBeforeRefresh = SelectedBatchResult?.ModZip;




                var buildOutcome = await Task.Run(() =>
                {
                    int builtCount = 0;
                    int skippedCount = 0;
                    int abortedCount = 0;

                    foreach (var row in rowsToBuild)
                    {
                        if (row.Service is null)
                            continue;

                        try
                        {
                            token.ThrowIfCancellationRequested();

                            var outPath = ReplaceOriginalMod
                                ? row.ModZip
                                : Path.Combine(
                                    Path.GetDirectoryName(row.ModZip) ?? "",
                                    Path.GetFileNameWithoutExtension(row.ModZip) + "_fixed.zip");

                            var result = row.Service.BuildFixedMod(
                                outPath,
                                (done, total, message) =>
                                {
                                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        IsProgressIndeterminate = false;
                                        ProgressMaximum = total;
                                        ProgressValue = done;
                                        StatusText = $"{Path.GetFileName(row.ModZip)} - {message}";
                                    });
                                },
                                token);

                            if (result.Built)
                            {
                                row.BuildStatus = "built";
                                row.OutZip = result.OutPath;
                                row.FixesMade = result.Copied;
                                builtCount++;
                            }
                            else
                            {
                                row.BuildStatus = "skipped";
                                row.OutZip = "";
                                row.FixesMade = 0;
                                skippedCount++;
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            row.BuildStatus = "not built";
                            row.OutZip = "";
                            row.FixesMade = 0;
                            abortedCount++;
                            break;
                        }
                    }

                    return new
                    {
                        Rows = allRows,
                        BuiltCount = builtCount,
                        SkippedCount = skippedCount,
                        AbortedCount = abortedCount,
                        SelectedPathBeforeRefresh = selectedPathBeforeRefresh,
                        WasCancelled = token.IsCancellationRequested
                    };
                });

                BatchResults.Clear();
                foreach (var row in buildOutcome.Rows)
                    BatchResults.Add(row);
                    OnPropertyChanged(nameof(FixableMods));

                if (!string.IsNullOrWhiteSpace(buildOutcome.SelectedPathBeforeRefresh))
                {
                    SelectedBatchResult = FixableMods.FirstOrDefault(x => x.ModZip == buildOutcome.SelectedPathBeforeRefresh);
                }

                if (SelectedBatchResult is null && FixableMods.Count > 0)
                    SelectedBatchResult = FixableMods[0];

                if (SelectedBatchResult is null)
                    SelectedBatchResult = null;

                if (buildOutcome.WasCancelled)
                    StatusText = "Build aborted.";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Build aborted.";
            }
            catch (Exception ex)
            {
                StatusText = "Error during build.";
                MessageBox.Show(ex.Message, "Build Error");
            }
            finally
            {
                EndBusy();
                UpdateStatusSummary();
                RefreshActionButtons();
            }
        }

        private async void RunAggressivePassPlaceholder()
        {
            if (BatchResults.Count == 0)
            {
                MessageBox.Show("Build one or more mods first.", "Aggressive Pass");
                return;
            }

            var rowsToProcess = BatchResults
                .Where(x =>
                    string.Equals(x.BuildStatus, "built", StringComparison.OrdinalIgnoreCase) &&
                    !x.AggressivePassRan)
                .ToList();

            if (rowsToProcess.Count == 0)
            {
                StatusText = "No built mods are waiting for aggressive pass.";
                RefreshActionButtons();
                return;
            }

            BeginBusy();
            RefreshActionButtons();
            StatusText = "Running aggressive pass...";

            var token = _cts?.Token ?? CancellationToken.None;

            try
            {
                await Task.Run(() =>
                {
                    foreach (var row in rowsToProcess)
                    {
                        token.ThrowIfCancellationRequested();

                        var targetZip = ReplaceOriginalMod ? row.ModZip : row.OutZip;

                        if (string.IsNullOrWhiteSpace(targetZip) || !File.Exists(targetZip))
                        {
                            row.AggressivePassRan = false;
                            row.AggressivePassStatus = "missing built zip";
                            continue;
                        }

                        try
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                StatusText = $"{Path.GetFileName(targetZip)} - second pass material scan...";
                            });

                            var materialFinder = new MaterialFinderService();
                            var materialFinderResult = materialFinder.Scan(
                                new MaterialFinderRequest
                                {
                                    ModZipPath = targetZip,
                                    CurrentFolder = string.IsNullOrWhiteSpace(CurrentContentFolder) ? string.Empty : CurrentContentFolder,
                                    OldFolder = string.IsNullOrWhiteSpace(OldContentFolder) ? string.Empty : OldContentFolder,
                                    ScanReferencesInContentFolders = false
                                },
                                token);

                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                StatusText = $"{Path.GetFileName(targetZip)} - third pass generation scan...";
                            });

                            var generatedService = new GeneratedMaterialDefinitionService();
                            var generatedMaterialResult = generatedService.BuildSuggestions(
                                new GeneratedMaterialDefinitionRequest
                                {
                                    ModZipPath = targetZip,
                                    CurrentFolder = string.IsNullOrWhiteSpace(CurrentContentFolder) ? string.Empty : CurrentContentFolder,
                                    OldFolder = string.IsNullOrWhiteSpace(OldContentFolder) ? string.Empty : OldContentFolder
                                },
                                materialFinderResult,
                                token);

                            _materialFinderResultsByTargetZip[targetZip] = materialFinderResult;
                            _generatedResultsByTargetZip[targetZip] = generatedMaterialResult;

                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                StatusText = $"{Path.GetFileName(targetZip)} - injecting generated materials...";
                            });

                            var aggressiveResult = ApplyAggressiveLocalizationToZip(
                                targetZip,
                                materialFinderResult,
                                generatedMaterialResult,
                                token);

                            row.AggressivePassRan = true;
                            row.AggressivePassStatus = aggressiveResult.StatusText;

                            if (!ReplaceOriginalMod)
                                row.OutZip = targetZip;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            row.AggressivePassRan = false;
                            row.AggressivePassStatus = "error";
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                MessageBox.Show(
                                    $"Aggressive pass failed for:\n\n{row.ModName}\n\n{ex.Message}",
                                    "Aggressive Pass Error");
                            });
                        }
                    }
                }, token);

                if (SelectedBatchResult is not null)
                    LoadDetailRows(SelectedBatchResult);

                StatusText = "Aggressive pass complete.";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Aggressive pass aborted.";
            }
            catch (Exception ex)
            {
                StatusText = "Error during aggressive pass.";
                MessageBox.Show(ex.Message, "Aggressive Pass Error");
            }
            finally
            {
                EndBusy();
                RefreshActionButtons();
                UpdateStatusSummary();
            }
        }

        private sealed class AggressiveApplyResult
        {
            public string StatusText { get; set; } = "done";
            public int MaterialsWritten { get; set; }
            public int TextureFilesCopied { get; set; }
        }

        private AggressiveApplyResult ApplyAggressiveLocalizationToZip(
            string targetZip,
            MaterialFinderResult materialFinderResult,
            GeneratedMaterialDefinitionResult generatedMaterialResult,
            CancellationToken token)
        {
            var result = new AggressiveApplyResult();

            var modName = Path.GetFileNameWithoutExtension(targetZip);
            var localizationFolderName = $"missingfilefix_{modName}_tfgeneratedlocalization";

            var tempRoot = Path.Combine(
                Path.GetTempPath(),
                "beamng_texture_fixer_aggressive_" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(tempRoot);

            try
            {
                ZipFile.ExtractToDirectory(targetZip, tempRoot);

                var localizationFolderPath = Path.Combine(tempRoot, localizationFolderName);
                Directory.CreateDirectory(localizationFolderPath);

                var copiedDestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var materialsJsonRoot = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                foreach (var issue in materialFinderResult.Issues
                             .Where(x => !IsUnreferencedIssue(x.IssueType))
                             .OrderBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();

                    var primaryDef = PickPrimaryDefinition(issue.Definitions);

                    var candidate = generatedMaterialResult.Candidates
                        .FirstOrDefault(c => string.Equals(c.MaterialName, issue.MaterialName, StringComparison.OrdinalIgnoreCase));

                    bool candidateBuildable =
                        candidate is not null &&
                        !string.Equals(candidate.GenerationStatus, "no_generated_candidate", StringComparison.OrdinalIgnoreCase);

                    bool shouldLocalize =
                        (primaryDef is not null && !string.Equals(primaryDef.Origin, "mod", StringComparison.OrdinalIgnoreCase))
                        || candidateBuildable;

                    if (!shouldLocalize)
                        continue;

                    var stage = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                    if (primaryDef is not null && !string.Equals(primaryDef.Origin, "mod", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var dependency in primaryDef.DependencyChecks)
                        {
                            token.ThrowIfCancellationRequested();

                            if (dependency.Asset is null)
                                continue;

                            if (!string.Equals(dependency.Status, "resolved", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (string.IsNullOrWhiteSpace(dependency.Texture?.Key))
                                continue;

                            var copiedFileName = CopyAssetIntoLocalizationFolder(
                                dependency.Asset.ArchivePath,
                                dependency.Asset.InternalPath,
                                localizationFolderPath,
                                copiedDestNames);

                            if (string.IsNullOrWhiteSpace(copiedFileName))
                                continue;

                            stage[dependency.Texture.Key] = $"{localizationFolderName}/{copiedFileName}".Replace("\\", "/");
                            result.TextureFilesCopied++;
                        }
                    }
                    else if (candidateBuildable && candidate is not null)
                    {
                        foreach (var slot in candidate.Slots.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            token.ThrowIfCancellationRequested();

                            var asset = slot.Value;
                            if (asset is null)
                                continue;

                            if (string.IsNullOrWhiteSpace(slot.Key))
                                continue;

                            var copiedFileName = CopyAssetIntoLocalizationFolder(
                                asset.ArchivePath,
                                asset.InternalPath,
                                localizationFolderPath,
                                copiedDestNames);

                            if (string.IsNullOrWhiteSpace(copiedFileName))
                                continue;

                            stage[slot.Key] = $"{localizationFolderName}/{copiedFileName}".Replace("\\", "/");
                            result.TextureFilesCopied++;
                        }
                    }

                    if (stage.Count == 0)
                        continue;

                    var materialNode = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["name"] = issue.MaterialName,
                        ["class"] = "Material",
                        ["Stages"] = new List<Dictionary<string, object>> { stage }
                    };

                    materialsJsonRoot[issue.MaterialName] = materialNode;
                    result.MaterialsWritten++;
                }

                if (materialsJsonRoot.Count == 0)
                {
                    result.StatusText = "done - nothing to inject";
                    return result;
                }

                var jsonText = JsonSerializer.Serialize(
                    materialsJsonRoot,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });

                var wroteAny = false;

                var domainFolders = new[] { "vehicles", "levels" };

                foreach (var domain in domainFolders)
                {
                    var domainPath = Path.Combine(tempRoot, domain);

                    if (!Directory.Exists(domainPath))
                        continue;

                    foreach (var subDir in Directory.GetDirectories(domainPath))
                    {
                        var materialsJsonPath = Path.Combine(subDir, "tfgenerated.materials.json");
                        File.WriteAllText(materialsJsonPath, jsonText);
                        wroteAny = true;
                    }
                }

                // fallback: if no vehicles/levels folders exist, write to root
                if (!wroteAny)
                {
                    var fallbackPath = Path.Combine(tempRoot, "tfgenerated.materials.json");
                    File.WriteAllText(fallbackPath, jsonText);
                }

                if (File.Exists(targetZip))
                    File.Delete(targetZip);

                ZipFile.CreateFromDirectory(tempRoot, targetZip, CompressionLevel.Optimal, false);

                result.StatusText = $"done - {result.MaterialsWritten} material(s), {result.TextureFilesCopied} texture path(s)";
                return result;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempRoot))
                        Directory.Delete(tempRoot, true);
                }
                catch
                {
                }
            }
        }

        private static string? CopyAssetIntoLocalizationFolder(
            string sourceArchivePath,
            string internalPath,
            string localizationFolderPath,
            HashSet<string> copiedDestNames)
        {
            if (string.IsNullOrWhiteSpace(sourceArchivePath) ||
                string.IsNullOrWhiteSpace(internalPath) ||
                !File.Exists(sourceArchivePath))
            {
                return null;
            }

            using var zip = ZipFile.OpenRead(sourceArchivePath);

            var normalizedInternalPath = internalPath.Replace("\\", "/").TrimStart('/');

            var entry = zip.Entries.FirstOrDefault(e =>
                string.Equals(
                    e.FullName.Replace("\\", "/").TrimStart('/'),
                    normalizedInternalPath,
                    StringComparison.OrdinalIgnoreCase));

            if (entry is null)
                return null;

            var originalFileName = Path.GetFileName(normalizedInternalPath);
            if (string.IsNullOrWhiteSpace(originalFileName))
                return null;

            var finalFileName = MakeUniqueFileName(originalFileName, copiedDestNames);
            var destPath = Path.Combine(localizationFolderPath, finalFileName);

            entry.ExtractToFile(destPath, true);

            return finalFileName;
        }

        private static string MakeUniqueFileName(string originalFileName, HashSet<string> usedNames)
        {
            var baseName = Path.GetFileNameWithoutExtension(originalFileName);
            var ext = Path.GetExtension(originalFileName);

            var candidate = originalFileName;
            int counter = 2;

            while (!usedNames.Add(candidate))
            {
                candidate = $"{baseName}_{counter}{ext}";
                counter++;
            }

            return candidate;
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        private bool _isProgressIndeterminate = true;
        public bool IsProgressIndeterminate
        {
            get => _isProgressIndeterminate;
            set => SetProperty(ref _isProgressIndeterminate, value);
        }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set => SetProperty(ref _progressValue, value);
        }

        private double _progressMaximum = 100;
        public double ProgressMaximum
        {
            get => _progressMaximum;
            set => SetProperty(ref _progressMaximum, value);
        }

        private void UpdateStatusSummary()
        {
            if (IsBusy)
                return;

            if (SelectedMainTabIndex == 0)
            {
                int totalMods = BatchResults.Count;

                if (totalMods == 0)
                {
                    StatusText = "Ready.";
                    return;
                }

                int totalFixableRefs = BatchResults.Sum(x => x.ResolvedFromOld);
                int modsWithFixableRefs = BatchResults.Count(x => x.ResolvedFromOld > 0);
                int modsWithNoRefsToFix = BatchResults.Count(x => x.ResolvedFromOld == 0);

                int builtMods = BatchResults.Count(x =>
                    string.Equals(x.BuildStatus, "built", StringComparison.OrdinalIgnoreCase));

                int totalTexturesCopied = BatchResults
                    .Where(x => string.Equals(x.BuildStatus, "built", StringComparison.OrdinalIgnoreCase))
                    .Sum(x => x.FixesMade);

                // Before any build has actually happened, keep the "no references found to fix" wording
                bool anyBuildHasHappened = BatchResults.Any(x =>
                    string.Equals(x.BuildStatus, "built", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.BuildStatus, "skipped", StringComparison.OrdinalIgnoreCase));

                if (!anyBuildHasHappened)
                {
                    StatusText = $"{totalFixableRefs} fixable references across {modsWithFixableRefs} out of {totalMods} mods ({modsWithNoRefsToFix} with no references found to fix)";
                }
                else
                {
                    int abortedMods = totalMods - builtMods;
                    StatusText = $"{totalTexturesCopied} textures copied satisfying {totalFixableRefs} fixable references across {builtMods} out of {totalMods} mods ({abortedMods} aborted)";
                }
            }
            else if (SelectedMainTabIndex == 1)
            {
                if (SelectedBatchResult is null)
                {
                    StatusText = "No mod selected.";
                    return;
                }

                int fixableRefs = SelectedBatchResult.ResolvedFromOld;
                int texturesOverall = SelectedBatchResult.TextureRefs;
                string modName = SelectedBatchResult.ModName;

                bool thisModBuilt = string.Equals(SelectedBatchResult.BuildStatus, "built", StringComparison.OrdinalIgnoreCase);

                if (thisModBuilt)
                {
                    StatusText = $"{SelectedBatchResult.FixesMade} textures copied satisfying {fixableRefs} fixable references, {texturesOverall} textures overall for {modName}";
                }
                else
                {
                    StatusText = $"{fixableRefs} fixable references found, {texturesOverall} textures overall for {modName}";
                }
            }
            else if (SelectedMainTabIndex == 2)
            {
                if (SelectedBatchResult is null)
                {
                    StatusText = "No mod selected.";
                    return;
                }

                StatusText = $"{SecondPassRows.Count} second-pass material rows for {SelectedBatchResult.ModName}";
            }
            else if (SelectedMainTabIndex == 3)
            {
                if (SelectedBatchResult is null)
                {
                    StatusText = "No mod selected.";
                    return;
                }

                StatusText = $"{ThirdPassRows.Count} third-pass material rows for {SelectedBatchResult.ModName}";
            }
        }
    }
}