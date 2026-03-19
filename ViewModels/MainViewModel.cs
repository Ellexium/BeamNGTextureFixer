using BeamNGTextureFixer.Helpers;
using BeamNGTextureFixer.Models;
using BeamNGTextureFixer.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using System.IO.Compression;

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
                }
            }
        }

        private string _selectedModsDisplay = string.Empty;
        public string SelectedModsDisplay
        {
            get => _selectedModsDisplay;
            set => SetProperty(ref _selectedModsDisplay, value);
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
        }

        public RelayCommand AggressivePassCommand { get; }
        public MainViewModel()
        {
            BrowseOldCommand = new RelayCommand(BrowseOldFolder);
            BrowseCurrentCommand = new RelayCommand(BrowseCurrentFolder);
            BrowseModsCommand = new RelayCommand(BrowseMods);

            ClearOldCommand = new RelayCommand(ClearOldFolder);
            ClearCurrentCommand = new RelayCommand(ClearCurrentFolder);
            ClearModsCommand = new RelayCommand(ClearMods);

            ScanCommand = new RelayCommand(ScanMods);
            BuildCommand = new RelayCommand(BuildModsPlaceholder, CanBuildFirstPass);
            AggressivePassCommand = new RelayCommand(RunAggressivePassPlaceholder, CanRunAggressivePass);

            AbortCommand = new RelayCommand(AbortWork, () => CanAbort);

            ExportTextureReportCommand = new RelayCommand(ExportTextureReport);
            ExportMaterialReportCommand = new RelayCommand(ExportMaterialReport);
            ExportThirdPassReportCommand = new RelayCommand(ExportThirdPassReport);

            // ExportTextureReportCommand = new RelayCommand(ExportTextureReport, () => SelectedBatchResult is not null && DetailRows.Count > 0);
        }

        private bool CanBuildFirstPass()
        {
            if (IsBusy)
                return false;

            return BatchResults.Any(x =>
                !string.Equals(x.BuildStatus, "built", StringComparison.OrdinalIgnoreCase) &&
                x.ResolvedFromOld > 0);
        }

        private void RefreshActionButtons()
        {
            BuildCommand.RaiseCanExecuteChanged();
            AggressivePassCommand.RaiseCanExecuteChanged();
        }
        private bool CanRunAggressivePass()
        {
            if (IsBusy)
                return false;

            return BatchResults.Any(x =>
                string.Equals(x.BuildStatus, "built", StringComparison.OrdinalIgnoreCase) &&
                !x.AggressivePassRan);
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

                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                StatusText = $"{Path.GetFileName(modZip)} - second pass material scan...";
                            });

                            var materialFinder = new MaterialFinderService();
                            var materialFinderResult = materialFinder.Scan(
                                new MaterialFinderRequest
                                {
                                    ModZipPath = modZip,
                                    CurrentFolder = string.IsNullOrWhiteSpace(CurrentContentFolder) ? string.Empty : CurrentContentFolder,
                                    OldFolder = string.IsNullOrWhiteSpace(OldContentFolder) ? string.Empty : OldContentFolder,
                                    ScanReferencesInContentFolders = false
                                },
                                token);

                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                StatusText = $"{Path.GetFileName(modZip)} - third pass generation scan...";
                            });

                            var generatedService = new GeneratedMaterialDefinitionService();
                            var generatedMaterialResult = generatedService.BuildSuggestions(
                                new GeneratedMaterialDefinitionRequest
                                {
                                    ModZipPath = modZip,
                                    CurrentFolder = string.IsNullOrWhiteSpace(CurrentContentFolder) ? string.Empty : CurrentContentFolder,
                                    OldFolder = string.IsNullOrWhiteSpace(OldContentFolder) ? string.Empty : OldContentFolder
                                },
                                materialFinderResult,
                                token);

                            //var finder = new MaterialFinderService();
                            //var finderResult = finder.Scan(
                            //    new MaterialFinderRequest
                            //    {
                            //        ModZipPath = modZip,
                            //        CurrentFolder = string.IsNullOrWhiteSpace(CurrentContentFolder) ? string.Empty : CurrentContentFolder,
                            //        OldFolder = string.IsNullOrWhiteSpace(OldContentFolder) ? string.Empty : OldContentFolder,
                            //        ScanReferencesInContentFolders = false
                            //    },
                            //    token);

                            //MaterialFinderCsvExporter.Export(finderResult, modZip);

                            //var generatedService = new GeneratedMaterialDefinitionService();

                            //var generatedResult = generatedService.BuildSuggestions(
                            //    new GeneratedMaterialDefinitionRequest
                            //    {
                            //        ModZipPath = modZip,
                            //        CurrentFolder = string.IsNullOrWhiteSpace(CurrentContentFolder) ? string.Empty : CurrentContentFolder,
                            //        OldFolder = string.IsNullOrWhiteSpace(OldContentFolder) ? string.Empty : OldContentFolder
                            //    },
                            //    finderResult,
                            //    token);

                            //generatedService.ExportCsv(generatedResult, modZip);
                            //generatedService.ExportGeneratedJsonPreview(generatedResult, modZip);

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
                                MaterialFinderResult = materialFinderResult,
                                GeneratedMaterialDefinitionResult = generatedMaterialResult
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

            BindSecondPass(row.MaterialFinderResult);
            BindThirdPass(row);

            UpdateStatusSummary();
        }

        private void BindSecondPass(MaterialFinderResult? result)
        {
            if (result is null)
            {
                SecondPassRows = new List<SecondPassRow>();
                SecondPassSummaryText = "No second pass data yet.";
                return;
            }

            var rows = result.Issues
                .Where(x =>
                    !string.Equals(x.IssueType, "defined_but_unreferenced", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(x.IssueType, "defined_but_unreferenced_and_broken", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase)
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
                $"Referenced but undefined: {rows.Count(x => x.Status == "referenced_but_undefined")}\n" +
                $"Defined but unreferenced: {rows.Count(x => x.Status == "defined_but_unreferenced" || x.Status == "defined_but_unreferenced_and_broken")}";
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

        private void BindThirdPass(BatchResultRow? batchRow)
        {
            if (batchRow?.MaterialFinderResult is null)
            {
                ThirdPassRows = new List<ThirdPassRow>();
                ThirdPassSummaryText = "No third pass data yet.";
                return;
            }

            var issueRows = batchRow.MaterialFinderResult.Issues
                    .Where(x =>
                        !string.Equals(x.IssueType, "defined_but_unreferenced", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(x.IssueType, "defined_but_unreferenced_and_broken", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase)
                    .Select(issue =>
                    {
                    var primaryDef = PickPrimaryDefinition(issue.Definitions);

                        bool isReferenced =
                            !string.Equals(issue.IssueType, "defined_but_unreferenced", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(issue.IssueType, "defined_but_unreferenced_and_broken", StringComparison.OrdinalIgnoreCase);

                        bool shouldLocalize =
                            isReferenced &&
                            primaryDef is not null &&
                            !string.Equals(primaryDef.Origin, "mod", StringComparison.OrdinalIgnoreCase);

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
                    if (primaryDef is null)
                    {
                        notes = issue.IssueType == "referenced_but_undefined"
                            ? "No definition was found anywhere, so aggressive localization cannot copy a real external definition yet."
                            : "No primary definition found.";
                    }
                    else if (shouldLocalize)
                    {
                        notes = "Definition exists outside the mod. Aggressive pass should recreate this material locally in tfgenerated.materials.json.";
                    }
                    else
                    {
                        notes = "Definition is already inside the mod, so aggressive localization is not needed.";
                    }

                    int textureCount = primaryDef?.TextureRefs.Count ?? 0;

                    return new ThirdPassRow
                    {
                        MaterialName = issue.MaterialName,
                        PreStatus = issue.IssueType,
                        ShouldLocalize = shouldLocalize ? "Yes" : "No",
                        ActionTaken = actionTaken,
                        ImportedFrom = importedFrom,
                        InjectedInto = injectedInto,
                        TexturesCopied = textureCount,
                        FinalStatus = finalStatus,
                        Notes = notes
                    };
                })
                .ToList();

            ThirdPassRows = issueRows;

            ThirdPassSummaryText =
                $"Third Pass materials tracked: {issueRows.Count}\n" +
                $"Should localize: {issueRows.Count(x => x.ShouldLocalize == "Yes")}\n" +
                $"Already local to mod: {issueRows.Count(x => x.ShouldLocalize == "No")}";
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
        private static string BuildThirdPassImportedFrom(GeneratedMaterialCandidate candidate)
        {
            var sources = new List<string>();

            if (candidate.ChosenColor is not null)
                sources.Add($"{candidate.ChosenColor.Origin} :: {candidate.ChosenColor.InternalPath}");

            if (candidate.ChosenNormal is not null)
                sources.Add($"{candidate.ChosenNormal.Origin} :: {candidate.ChosenNormal.InternalPath}");

            if (candidate.ChosenSpecular is not null)
                sources.Add($"{candidate.ChosenSpecular.Origin} :: {candidate.ChosenSpecular.InternalPath}");

            return string.Join(" | ", sources.Distinct(StringComparer.OrdinalIgnoreCase));
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
                MessageBox.Show("Scan and build one or more mods first.", "No Built Mods");
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

                        var targetZip = ReplaceOriginalMod
                            ? row.ModZip
                            : row.OutZip;

                        if (string.IsNullOrWhiteSpace(targetZip) || !File.Exists(targetZip))
                        {
                            row.AggressivePassRan = false;
                            row.AggressivePassStatus = "missing built zip";
                            continue;
                        }

                        // placeholder for:
                        // 1. MaterialFinder on targetZip
                        // 2. GeneratedMaterialDefinitionService on targetZip
                        // 3. copy localized textures into missingfilefix_<mod>_tfgeneratedlocalization/
                        // 4. inject tfgenerated.materials.json into targetZip

                        row.AggressivePassRan = true;
                        row.AggressivePassStatus = "done";
                    }
                }, token);

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