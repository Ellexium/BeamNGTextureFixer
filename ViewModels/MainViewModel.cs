using BeamNGTextureFixer.Helpers;
using BeamNGTextureFixer.Models;
using BeamNGTextureFixer.Services;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace BeamNGTextureFixer.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
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

        public MainViewModel()
        {
            BrowseOldCommand = new RelayCommand(BrowseOldFolder);
            BrowseCurrentCommand = new RelayCommand(BrowseCurrentFolder);
            BrowseModsCommand = new RelayCommand(BrowseMods);
            ScanCommand = new RelayCommand(ScanMods);
            BuildCommand = new RelayCommand(BuildModsPlaceholder);
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
            DetailRows = new List<DetailRow>();

            IsBusy = true;
            IsProgressIndeterminate = true;
            StatusText = "Scanning mods...";

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.Render);

            try
            {
                var scannedRows = await Task.Run(() =>
                {
                    var rows = new List<BatchResultRow>();
                    int totalRefs = 0;
                    int totalOld = 0;
                    int totalUnresolved = 0;

                    foreach (var modZip in SelectedMods)
                    {
                        var service = new BeamNGFixerService();
                        var payload = service.Scan(
                            modZip,
                            OldContentFolder,
                            string.IsNullOrWhiteSpace(CurrentContentFolder) ? null : CurrentContentFolder);

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
                            Service = service
                        };

                        var modStem = PathHelpers.SanitizeModStem(Path.GetFileName(service.ModZipPath));
                        var collisionCounts = service.BasenameCollisionsWithinSourceZip();

                        foreach (var pair in payload.Results)
                        {
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
                    }

                    return new
                    {
                        Rows = rows,
                        TotalRefs = totalRefs,
                        TotalOld = totalOld,
                        TotalUnresolved = totalUnresolved
                    };
                });

                foreach (var row in scannedRows.Rows)
                    BatchResults.Add(row);

                SummaryText =
                    $"Scanned {BatchResults.Count} mod(s)\n" +
                    $"Total referenced texture paths: {scannedRows.TotalRefs}\n" +
                    $"Total resolved from old content: {scannedRows.TotalOld}\n" +
                    $"Total unresolved: {scannedRows.TotalUnresolved}\n" +
                    $"Build will only create *_fixed.zip for mods with new paths found.";

                if (BatchResults.Count > 0)
                    SelectedBatchResult = BatchResults[0];

                UpdateStatusSummary();
            }
            catch (Exception ex)
            {
                StatusText = "Error during scan.";
                MessageBox.Show(ex.Message, "Scan Error");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void LoadDetailRows(BatchResultRow? row)
        {
            if (row is null)
            {
                DetailRows = new List<DetailRow>();
                UpdateStatusSummary();
                return;
            }

            DetailRows = row.DetailRows.ToList();
            UpdateStatusSummary();
        }
        private async void BuildModsPlaceholder()
        {
            if (BatchResults.Count == 0)
            {
                MessageBox.Show("Scan one or more mods first.", "No Scan");
                return;
            }

            IsBusy = true;
            IsProgressIndeterminate = true;
            ProgressValue = 0;
            ProgressMaximum = 1;
            StatusText = "Building fixed mods...";

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.Render);

            try
            {
                var selectedPathBeforeRefresh = SelectedBatchResult?.ModZip;

                var rowsToBuild = BatchResults.ToList();

                var buildOutcome = await Task.Run(() =>
                {
                    int builtCount = 0;
                    int skippedCount = 0;

                    foreach (var row in rowsToBuild)
                    {
                        if (row.Service is null)
                            continue;

                        var outPath = Path.Combine(
                            Path.GetDirectoryName(row.ModZip) ?? "",
                            Path.GetFileNameWithoutExtension(row.ModZip) + "_fixed.zip");

                        var result = row.Service.BuildFixedMod(outPath, (done, total, message) =>
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                IsProgressIndeterminate = false;
                                ProgressMaximum = total;
                                ProgressValue = done;
                                StatusText = $"{Path.GetFileName(row.ModZip)} - {message}";
                            });
                        });

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

                    return new
                    {
                        Rows = rowsToBuild,
                        BuiltCount = builtCount,
                        SkippedCount = skippedCount,
                        SelectedPathBeforeRefresh = selectedPathBeforeRefresh
                    };
                });

                BatchResults.Clear();
                foreach (var row in buildOutcome.Rows)
                    BatchResults.Add(row);

                if (!string.IsNullOrWhiteSpace(buildOutcome.SelectedPathBeforeRefresh))
                {
                    SelectedBatchResult = BatchResults.FirstOrDefault(x => x.ModZip == buildOutcome.SelectedPathBeforeRefresh);
                }
                if (BatchResults.Count > 0)
                    SelectedBatchResult = BatchResults[0];

                IsBusy = false;
                UpdateStatusSummary();

                MessageBox.Show(
                    $"Built: {buildOutcome.BuiltCount}\nSkipped (no new paths found): {buildOutcome.SkippedCount}",
                    "Batch Build Complete");
            }
            catch (Exception ex)
            {
                StatusText = "Error during build.";
                MessageBox.Show(ex.Message, "Build Error");
            }
            finally
            {
                IsBusy = false;
                UpdateStatusSummary();
                IsProgressIndeterminate = true;
                ProgressValue = 0;
                ProgressMaximum = 1;
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
                int modsWithFixableRefs = BatchResults.Count(x => x.ResolvedFromOld > 0);
                int totalFixableRefs = BatchResults.Sum(x => x.ResolvedFromOld);
                int totalTexturesCopied = BatchResults.Sum(x => x.FixesMade);

                if (totalMods == 0)
                {
                    StatusText = "Ready.";
                    return;
                }

                StatusText = $"{totalTexturesCopied} textures copied satisfying {totalFixableRefs} fixable references across {modsWithFixableRefs} out of {totalMods} mods";
            }
            else
            {
                if (SelectedBatchResult is null)
                {
                    StatusText = "No mod selected.";
                    return;
                }

                int fixableRefs = SelectedBatchResult.ResolvedFromOld;
                int texturesCopied = SelectedBatchResult.FixesMade;
                int textureCount = SelectedBatchResult.TextureRefs;
                string modName = SelectedBatchResult.ModName;

                StatusText = $"{texturesCopied} textures copied satisfying {fixableRefs} fixable references, {textureCount} textures overall for {modName}";
            }
        }
    }
}