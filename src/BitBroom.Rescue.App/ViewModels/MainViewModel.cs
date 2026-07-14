using System.Collections.ObjectModel;
using System.IO;
using BitBroom.Rescue.App.Mvvm;
using BitBroom.Rescue.Core.Health;
using BitBroom.Rescue.Core.Imaging;
using BitBroom.Rescue.Core.Io;
using BitBroom.Rescue.Core.Recovery;
using BitBroom.Rescue.Core.RecycleBin;

namespace BitBroom.Rescue.App.ViewModels;

/// <summary>One recoverable file, adapted for the results grid with a color-coded confidence.</summary>
public sealed class RecoverableRow : ObservableObject
{
    private bool _selected;

    public required RecoverableItem Item { get; init; }

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public string Name => Item.Name;
    public string Path => Item.OriginalPath ?? "(carved — no original path)";
    public string SizeText => FormatSize(Item.SizeBytes);
    public string Modified => Item.ModifiedUtc == DateTime.MinValue ? "" : Item.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string Confidence => Item.Confidence.ToString();
    public string SourceText => Item.Source switch
    {
        RecoverySource.RecycleBin => "Recycle Bin",
        RecoverySource.ShadowCopy => "Previous version",
        RecoverySource.Carved => "Carved",
        _ => "Deleted",
    };

    public string ConfidenceColor => Item.Confidence switch
    {
        RecoveryConfidence.High => "#34D399",
        RecoveryConfidence.Good => "#38BDF8",
        RecoveryConfidence.Fair => "#FBBF24",
        _ => "#F87171",
    };

    internal static string FormatSize(long b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double s = b;
        int i = 0;
        while (s >= 1024 && i < u.Length - 1)
        {
            s /= 1024;
            i++;
        }

        return $"{s:0.#} {u[i]}";
    }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly Func<string, string, bool> _confirm;
    private readonly Func<string?> _pickFolder;
    private readonly Func<string?> _pickImageToOpen;
    private readonly Func<string?> _pickImageToSave;

    private List<RecoverableRow> _allResults = [];
    private IReadOnlyList<RecoverableRow> _results = [];
    private StorageDevice? _selectedDevice;
    private bool _isBusy;
    private string _status = "Select a drive, then Scan. BitBroom Rescue never writes to the drive it's reading.";
    private string _progressText = "";
    private string _filterText = "";
    private CancellationTokenSource? _cts;
    private bool _includeCarving;
    private int _selectedCount;
    private bool _bulkSelecting;

    // Kept alive after a scan: result rows read their content lazily through this
    // session's open (read-only) handle when the user hits Recover.
    private RecoverySession? _session;

    public MainViewModel(
        Func<string, string, bool> confirm,
        Func<string?> pickFolder,
        Func<string?> pickImageToOpen,
        Func<string?> pickImageToSave)
    {
        _confirm = confirm;
        _pickFolder = pickFolder;
        _pickImageToOpen = pickImageToOpen;
        _pickImageToSave = pickImageToSave;

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => SelectedDevice is not null && !_isBusy);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => _isBusy);
        RecoverCommand = new AsyncRelayCommand(RecoverAsync, () => _selectedCount > 0 && !_isBusy);
        CloneCommand = new AsyncRelayCommand(CloneAsync, () => SelectedDevice is not null && SelectedDevice.Kind != StorageKind.Image && !_isBusy);
        RefreshDevicesCommand = new RelayCommand(_ => LoadDevices(), _ => !_isBusy);
        OpenImageCommand = new RelayCommand(_ => OpenImage(), _ => !_isBusy);
        SelectAllCommand = new RelayCommand(_ => SetAll(true));
        SelectNoneCommand = new RelayCommand(_ => SetAll(false));

        LoadDevices();
    }

    public ObservableCollection<StorageDevice> Devices { get; } = [];
    public ObservableCollection<HealthWarning> Warnings { get; } = [];

    /// <summary>Filtered view for the grid. Replaced wholesale so huge scans bind in one shot.</summary>
    public IReadOnlyList<RecoverableRow> Results
    {
        get => _results;
        private set => SetProperty(ref _results, value);
    }

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand RecoverCommand { get; }
    public AsyncRelayCommand CloneCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand OpenImageCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }

    public bool IncludeCarving
    {
        get => _includeCarving;
        set => SetProperty(ref _includeCarving, value);
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                ApplyFilter();
            }
        }
    }

    public StorageDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CloneCommand.RaiseCanExecuteChanged();
                UpdateHealth();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                RecoverCommand.RaiseCanExecuteChanged();
                CloneCommand.RaiseCanExecuteChanged();
                RefreshDevicesCommand.RaiseCanExecuteChanged();
                OpenImageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string ResultSummary => _allResults.Count == 0 ? "" :
        Results.Count == _allResults.Count
            ? $"{_allResults.Count:N0} recoverable · {_selectedCount:N0} selected"
            : $"{Results.Count:N0} of {_allResults.Count:N0} shown · {_selectedCount:N0} selected";

    private void LoadDevices()
    {
        // Keep any image files the user opened; refresh only real hardware.
        List<StorageDevice> images = Devices.Where(d => d.Kind == StorageKind.Image).ToList();
        Devices.Clear();
        foreach (StorageDevice d in DeviceEnumerator.ListVolumes())
        {
            Devices.Add(d);
        }

        try
        {
            foreach (StorageDevice d in DeviceEnumerator.ListPhysicalDisks())
            {
                Devices.Add(d);
            }
        }
        catch
        {
            // best effort
        }

        foreach (StorageDevice img in images)
        {
            Devices.Add(img);
        }

        // Default to the first mounted volume so Scan is immediately usable.
        if (SelectedDevice is null || !Devices.Contains(SelectedDevice))
        {
            SelectedDevice = Devices.FirstOrDefault(d => d.Kind == StorageKind.Volume) ?? Devices.FirstOrDefault();
        }
    }

    private void OpenImage()
    {
        string? path = _pickImageToOpen();
        if (path is null || !File.Exists(path))
        {
            return;
        }

        var device = new StorageDevice
        {
            Kind = StorageKind.Image,
            ImagePath = path,
            Label = Path.GetFileName(path),
            SizeBytes = new FileInfo(path).Length,
            FileSystem = "image",
        };
        Devices.Add(device);
        SelectedDevice = device;
        Status = $"Opened image {device.Label} ({RecoverableRow.FormatSize(device.SizeBytes)}). Scan it like a drive.";
    }

    private void UpdateHealth()
    {
        Warnings.Clear();
        if (SelectedDevice is null || SelectedDevice.Kind == StorageKind.Image)
        {
            return;
        }

        try
        {
            int? disk = SelectedDevice.DiskNumber;
            DriveHealthInfo info = disk is int n
                ? DiskHealthProbe.Probe(n)
                : new DriveHealthInfo();
            foreach (HealthWarning w in HealthAdvisor.Evaluate(info, RecoveryScenario.DeletedFiles))
            {
                Warnings.Add(w);
            }
        }
        catch
        {
            // Health is advisory; never block on it.
        }
    }

    private async Task ScanAsync()
    {
        if (SelectedDevice is null)
        {
            return;
        }

        StorageDevice device = SelectedDevice;
        IsBusy = true;
        SetResults([]);
        Status = "Scanning (read-only)…";
        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            char? letter = device.DriveLetter;
            bool carve = IncludeCarving;
            var found = await Task.Run(() =>
            {
                var list = new List<RecoverableItem>();

                // Recycle Bin first (highest confidence) for mounted volumes.
                if (letter is char c)
                {
                    try
                    {
                        list.AddRange(RecycleBinScanner.ScanVolume(c));
                    }
                    catch
                    {
                        // ignore
                    }
                }

                // The previous session (if any) backs rows that are about to be discarded.
                _session?.Dispose();
                _session = new RecoverySession(device.Open());
                if (_session.FileSystem == DetectedFileSystem.Unknown && device.Kind == StorageKind.PhysicalDisk)
                {
                    throw new InvalidOperationException(
                        "No file system found at the start of this disk (partition tables aren't parsed yet). " +
                        "Scan the drive letter instead, or clone the disk and scan the image.");
                }

                var scanProgress = new Progress<ScanProgress>(p =>
                    ProgressText = $"{p.RecordsSeen:N0} records · {p.Found:N0} deleted found");
                list.AddRange(_session.ScanDeletedFiles(1, scanProgress, cts.Token));

                if (carve)
                {
                    var carveProgress = new Progress<Core.Carving.CarveProgress>(p =>
                        ProgressText = $"Carving {RecoverableRow.FormatSize(p.BytesScanned)} · {p.Found} found");
                    list.AddRange(_session.Carve(progress: carveProgress, cancellationToken: cts.Token));
                }

                list.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
                var rows = new List<RecoverableRow>(list.Count);
                foreach (RecoverableItem it in list)
                {
                    rows.Add(new RecoverableRow { Item = it });
                }

                return rows;
            }, cts.Token);

            foreach (RecoverableRow row in found)
            {
                row.PropertyChanged += OnRowChanged;
            }

            SetResults(found);
            Status = $"Found {_allResults.Count:N0} recoverable items on {device.Label}. Select what you need, then Recover.";
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Scan failed: " + ex.Message;
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
            OnPropertyChanged(nameof(ResultSummary));
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            cts.Dispose();
        }
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_bulkSelecting || e.PropertyName != nameof(RecoverableRow.Selected))
        {
            return;
        }

        _selectedCount += ((RecoverableRow)sender!).Selected ? 1 : -1;
        RecoverCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ResultSummary));
    }

    private void SetResults(List<RecoverableRow> rows)
    {
        _allResults = rows;
        _selectedCount = 0;
        _filterText = "";
        OnPropertyChanged(nameof(FilterText));
        Results = rows;
        OnPropertyChanged(nameof(ResultSummary));
        RecoverCommand.RaiseCanExecuteChanged();
    }

    private void ApplyFilter()
    {
        string needle = _filterText.Trim();
        Results = needle.Length == 0
            ? _allResults
            : _allResults.Where(r =>
                r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                r.Path.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        OnPropertyChanged(nameof(ResultSummary));
    }

    private async Task RecoverAsync()
    {
        List<RecoverableRow> selected = _allResults.Where(r => r.Selected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        string? dest = _pickFolder();
        if (dest is null)
        {
            return;
        }

        string? sourceRoot = SelectedDevice?.DriveLetter is char c ? $"{c}:\\" : null;
        string? refusal = RecoveryDestinationGuard.Validate(sourceRoot, dest);
        if (refusal is not null)
        {
            _confirm("Can't recover there", refusal);
            return;
        }

        if (!_confirm("Recover files",
                $"Recover {selected.Count:N0} item(s) to:\n{dest}\n\nFiles are written only here; the source drive is never modified."))
        {
            return;
        }

        IsBusy = true;
        Status = "Recovering…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            RecoveryWriteResult result = await Task.Run(() =>
            {
                var writer = new RecoveryWriter(dest, sourceRoot);
                var progress = new Progress<(int Done, string Name)>(p => ProgressText = $"Recovered {p.Done:N0}…");
                return writer.WriteAll(selected.Select(r => r.Item), progress, cts.Token);
            }, cts.Token);

            Status = $"Recovered {result.Written:N0} file(s), {RecoverableRow.FormatSize(result.BytesWritten)}. " +
                     (result.Failed > 0 ? $"{result.Failed} failed. " : "") + "Log saved in the output folder.";
        }
        catch (OperationCanceledException)
        {
            Status = "Recovery cancelled.";
        }
        catch (Exception ex)
        {
            Status = "Recovery failed: " + ex.Message;
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            cts.Dispose();
        }
    }

    private async Task CloneAsync()
    {
        if (SelectedDevice is null || SelectedDevice.Kind == StorageKind.Image)
        {
            return;
        }

        StorageDevice device = SelectedDevice;
        string? imagePath = _pickImageToSave();
        if (imagePath is null)
        {
            return;
        }

        string? sourceRoot = device.DriveLetter is char c ? $"{c}:\\" : null;
        string? destDir = Path.GetDirectoryName(Path.GetFullPath(imagePath));
        string? refusal = RecoveryDestinationGuard.Validate(sourceRoot, destDir ?? imagePath);
        if (refusal is not null)
        {
            _confirm("Can't clone there", refusal);
            return;
        }

        if (!_confirm("Clone drive to image",
                $"Read every sector of {device.Label} into:\n{imagePath}\n\n" +
                "The source is opened read-only; unreadable regions are skipped, retried, and logged " +
                "to a map file. For failing drives, do this FIRST, then scan the image."))
        {
            return;
        }

        IsBusy = true;
        Status = $"Cloning {device.Label} (read-only source)…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            ImagingResult result = await Task.Run(() =>
            {
                using ISectorSource source = device.Open();
                var imager = new DiskImager(source);
                var progress = new Progress<ImagingProgress>(p =>
                    ProgressText = $"{p.Phase}: {RecoverableRow.FormatSize(p.BytesCopied)} of {RecoverableRow.FormatSize(p.BytesTotal)} · bad {RecoverableRow.FormatSize(p.BadBytes)} · {p.RateMBps:0.0} MB/s");
                return imager.CreateImage(imagePath, retryPasses: 2, progress: progress, cancellationToken: cts.Token);
            }, cts.Token);

            Status = result.BadBytes == 0
                ? $"Clone complete: {RecoverableRow.FormatSize(result.BytesCopied)} copied, no read errors. Open the image and scan it."
                : $"Clone finished: {RecoverableRow.FormatSize(result.BytesCopied)} copied, {RecoverableRow.FormatSize(result.BadBytes)} unreadable in {result.BadRegions} region(s) — see the .map file. Scan the image, not the failing drive.";
        }
        catch (OperationCanceledException)
        {
            Status = "Clone cancelled (partial image and map kept).";
        }
        catch (Exception ex)
        {
            Status = "Clone failed: " + ex.Message;
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
            }

            cts.Dispose();
        }
    }

    private void SetAll(bool value)
    {
        _bulkSelecting = true;
        try
        {
            // Applies to the filtered view, so "Select all" + a filter selects what you see.
            foreach (RecoverableRow r in Results)
            {
                r.Selected = value;
            }
        }
        finally
        {
            _bulkSelecting = false;
        }

        _selectedCount = _allResults.Count(r => r.Selected);
        RecoverCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ResultSummary));
    }
}
