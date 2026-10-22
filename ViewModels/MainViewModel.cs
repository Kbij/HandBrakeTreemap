using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using HandBrakeTreemap.Models;
using HandBrakeTreemap.Services;

namespace HandBrakeTreemap.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly HandBrakeRunner _runner = new();
    private CancellationTokenSource? _cts;

    private bool _loadingSettings;
    private bool _reloadingPresets;
    private string? _savedPresetName;

    public MainViewModel()
    {
        Log.Line += OnLogLine;
        string built = "?";
        try { built = File.GetLastWriteTime(typeof(MainViewModel).Assembly.Location).ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
        Log.Info($"HandBrake Treemap gestart (build {built}). Logbestand: {Log.FilePath}");

        // --- Restore settings (registry) ---
        _loadingSettings = true;
        var savedCli = AppSettings.GetString(AppSettings.HandBrakePath);
        HandBrakePath = !string.IsNullOrWhiteSpace(savedCli) && File.Exists(savedCli) ? savedCli! : _runner.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(savedCli) && !File.Exists(savedCli))
            Log.Warn($"Bewaard HandBrakeCLI-pad bestaat niet meer: {savedCli}");
        FolderPath = AppSettings.GetString(AppSettings.LastFolder) ?? string.Empty;
        PresetImportFile = AppSettings.GetString(AppSettings.PresetFile) ?? string.Empty;
        MinSavingPercent = AppSettings.GetDecimal(AppSettings.MinSavingPercent) ?? 5;
        OnlyVerified = AppSettings.GetBool(AppSettings.OnlyVerified) ?? true;
        UseRecycleBin = FileOps.RecycleBinSupported && (AppSettings.GetBool(AppSettings.UseRecycleBin) ?? true);
        _savedPresetName = AppSettings.GetString(AppSettings.Preset);

        foreach (var n in PresetCatalog.FallbackBuiltIn)
            Presets.Add(new PresetItem { Name = n });
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == (_savedPresetName ?? PresetCatalog.Default))
                         ?? Presets.FirstOrDefault(p => p.Name == PresetCatalog.Default);
        _loadingSettings = false;

        _ = ProbeHandBrakeAsync();
    }

    // --- Log panel ----------------------------------------------------------------------
    private const int MaxLogLines = 2000;
    private readonly LinkedList<string> _logLines = new();

    private string _logText = string.Empty;
    public string LogText { get => _logText; private set => SetField(ref _logText, value); }

    private void OnLogLine(string line) => Post(() =>
    {
        _logLines.AddLast(line);
        while (_logLines.Count > MaxLogLines) _logLines.RemoveFirst();
        LogText = string.Join(Environment.NewLine, _logLines);
    });

    public void ClearLog()
    {
        _logLines.Clear();
        LogText = string.Empty;
    }

    // --- Inputs -------------------------------------------------------------------------
    private string _folderPath = string.Empty;
    public string FolderPath
    {
        get => _folderPath;
        set => SetField(ref _folderPath, value);
    }

    private void Save(string name, string? value)
    {
        if (!_loadingSettings) AppSettings.SetString(name, value);
    }

    private string _handBrakePath = string.Empty;
    public string HandBrakePath
    {
        get => _handBrakePath;
        set
        {
            if (SetField(ref _handBrakePath, value))
            {
                _runner.ExecutablePath = value;
                // Only remember a path that actually points at an executable.
                if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
                    Save(AppSettings.HandBrakePath, value);
            }
        }
    }

    private string _presetImportFile = string.Empty;
    public string PresetImportFile
    {
        get => _presetImportFile;
        set { if (SetField(ref _presetImportFile, value)) Save(AppSettings.PresetFile, value); }
    }

    public ObservableCollection<PresetItem> Presets { get; } = new();

    private PresetItem? _selectedPreset;
    public PresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetField(ref _selectedPreset, value) && value != null && !_reloadingPresets)
            {
                _savedPresetName = value.Name;
                Save(AppSettings.Preset, value.Name);
            }
        }
    }

    private string _presetInfo = string.Empty;
    public string PresetInfo { get => _presetInfo; private set => SetField(ref _presetInfo, value); }

    /// <summary>
    /// (Re)load presets: custom ones from the HandBrake GUI store (+ the chosen preset file),
    /// built-ins from HandBrakeCLI --preset-list. Keeps the current selection when possible.
    /// </summary>
    public async Task ReloadPresetsAsync()
    {
        var keep = _savedPresetName ?? SelectedPreset?.Name;
        try
        {
            var (presets, sources) = await PresetCatalog.LoadAllAsync(_runner, PresetImportFile, CancellationToken.None);
            _reloadingPresets = true;
            Presets.Clear();
            foreach (var p in presets) Presets.Add(p);

            SelectedPreset =
                Presets.FirstOrDefault(p => p.Name == keep) ??
                Presets.FirstOrDefault(p => p.Name == PresetCatalog.Default) ??
                Presets.FirstOrDefault();
            _reloadingPresets = false;
            if (keep != null && SelectedPreset?.Name != keep)
                Log.Warn($"Bewaarde preset '{keep}' niet gevonden; '{SelectedPreset?.Name}' gekozen.");

            int custom = presets.Count(p => p.IsCustom);
            PresetInfo = custom > 0
                ? $"{custom} eigen preset(s) gevonden in: {string.Join(", ", sources)}"
                : "⚠ Geen eigen presets gevonden of leesbaar (zie log) — de ingebouwde presets worden getoond. " +
                  "Kies het presets.json-bestand met '…'.";
            Log.Info($"Presets geladen: {presets.Count - custom} ingebouwd, {custom} eigen" +
                     (sources.Count > 0 ? $" ({string.Join(", ", sources)})" : ""));
            foreach (var c in PresetCatalog.GuiPresetFileCandidates(_runner.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var state = PresetCatalog.Describe(c);
                if (state != "niet gevonden" || c.Contains(@"\Backup\", StringComparison.OrdinalIgnoreCase)
                    || c.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase))
                    Log.Write(state == "geen toegang" ? LogLevel.Warn : LogLevel.Info,
                              $"  presets gezocht: {c} — {state}" +
                              (state == "geen toegang" ? " (dit Windows-account mag die map niet lezen; kopieer het bestand of exporteer de presets)" : ""));
            }
        }
        catch (Exception ex)
        {
            _reloadingPresets = false;
            PresetInfo = "Presets laden mislukt: " + ex.Message;
            Log.Error("Presets laden mislukt", ex);
        }
    }

    public bool RecycleBinAvailable => FileOps.RecycleBinSupported;

    private bool _useRecycleBin;
    public bool UseRecycleBin
    {
        get => _useRecycleBin;
        set { if (SetField(ref _useRecycleBin, value)) Save(AppSettings.UseRecycleBin, value.ToString()); }
    }

    private decimal _minSavingPercent = 5;
    /// <summary>
    /// Minimum saving (in %) for a conversion to be worth it. Below this the new .mkv is
    /// thrown away and the original is renamed to "*.nomkv.*" so it is skipped next time.
    /// </summary>
    public decimal MinSavingPercent
    {
        get => _minSavingPercent;
        set
        {
            if (SetField(ref _minSavingPercent, Math.Clamp(value, 0, 100)) && !_loadingSettings)
                AppSettings.SetDecimal(AppSettings.MinSavingPercent, _minSavingPercent);
        }
    }

    private bool IsWorthIt(long original, long mkv)
        => original > 0 && (original - mkv) * 100.0 / original >= (double)MinSavingPercent;

    private bool _onlyVerified = true;
    /// <summary>Only delete originals whose .mkv passed the duration check.</summary>
    public bool OnlyVerified
    {
        get => _onlyVerified;
        set { if (SetField(ref _onlyVerified, value)) Save(AppSettings.OnlyVerified, value.ToString()); }
    }

    // --- Tree ---------------------------------------------------------------------------
    public ObservableCollection<FolderNode> Roots { get; } = new();
    public FolderNode? Root => Roots.FirstOrDefault();

    private FolderNode? _selectedNode;
    public FolderNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (SetField(ref _selectedNode, value))
            {
                RefreshSelectedFiles();
                UpdateTexts();
            }
        }
    }

    /// <summary>Folder to act on: the selected one, or the scanned root.</summary>
    private FolderNode? Target => SelectedNode ?? Root;

    public ObservableCollection<VideoFile> SelectedFiles { get; } = new();

    private VideoFile? _selectedFile;
    public VideoFile? SelectedFile
    {
        get => _selectedFile;
        set => SetField(ref _selectedFile, value);
    }

    // --- Texts / status -----------------------------------------------------------------
    private string _selectedTitle = "Geen map gescand";
    public string SelectedTitle { get => _selectedTitle; private set => SetField(ref _selectedTitle, value); }

    private string _selectedPendingText = string.Empty;
    public string SelectedPendingText { get => _selectedPendingText; private set => SetField(ref _selectedPendingText, value); }

    private string _selectedConvertedText = string.Empty;
    public string SelectedConvertedText { get => _selectedConvertedText; private set => SetField(ref _selectedConvertedText, value); }

    private string _totalText = string.Empty;
    public string TotalText { get => _totalText; private set => SetField(ref _totalText, value); }

    private string _sessionText = string.Empty;
    public string SessionText
    {
        get => _sessionText;
        private set { if (SetField(ref _sessionText, value)) OnPropertyChanged(nameof(HasSessionText)); }
    }
    public bool HasSessionText => !string.IsNullOrEmpty(SessionText);

    private string _status = "Kies een map en klik Scan.";
    public string Status { get => _status; set => SetField(ref _status, value); }

    private string _handBrakeStatus = "HandBrakeCLI: zoeken…";
    public string HandBrakeStatus { get => _handBrakeStatus; set => SetField(ref _handBrakeStatus, value); }

    private string _currentFileText = string.Empty;
    public string CurrentFileText { get => _currentFileText; set => SetField(ref _currentFileText, value); }

    private double _currentProgress;
    public double CurrentProgress { get => _currentProgress; set => SetField(ref _currentProgress, value); }

    private string _currentDetail = string.Empty;
    /// <summary>"42,1 % • 85 fps • nog 0:12:30" for the file being converted.</summary>
    public string CurrentDetail { get => _currentDetail; set => SetField(ref _currentDetail, value); }

    private string _overallDetail = "Totaal";
    /// <summary>"Totaal: 3/10 • 37 % • nog ~1:20:00" for the whole run.</summary>
    public string OverallDetail { get => _overallDetail; set => SetField(ref _overallDetail, value); }

    private double _overallProgress;
    public double OverallProgress { get => _overallProgress; set => SetField(ref _overallProgress, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (SetField(ref _isBusy, value)) OnPropertyChanged(nameof(IsNotBusy)); }
    }
    public bool IsNotBusy => !IsBusy;

    // --- HandBrake probe ----------------------------------------------------------------
    private async Task ProbeHandBrakeAsync()
    {
        HandBrakeStatus = $"HandBrakeCLI testen… ({_runner.ExecutablePath})";
        Log.Info($"HandBrakeCLI testen: {_runner.ExecutablePath}");

        // A bare name / missing file: try to find it again (e.g. installed in the meantime).
        if (!File.Exists(_runner.ExecutablePath) && HandBrakeRunner.LocateExecutable() is { } found)
        {
            Log.Info($"HandBrakeCLI gevonden op: {found}");
            HandBrakePath = found;
        }

        var version = await _runner.TryGetVersionAsync(CancellationToken.None);
        if (version != null)
        {
            HandBrakeStatus = $"✓ {version}  ({_runner.ExecutablePath})";
            Log.Info($"HandBrakeCLI OK: {version}");
            if (File.Exists(_runner.ExecutablePath))
                AppSettings.SetString(AppSettings.HandBrakePath, _runner.ExecutablePath);
        }
        else
        {
            var hint = HandBrakeRunner.GuiInstalledWithoutCli()
                ? " De HandBrake-GUI is geïnstalleerd, maar HandBrakeCLI is een aparte download: " +
                  "'winget install HandBrake.HandBrake.CLI' of https://handbrake.fr/downloads2.php (Command Line Version), " +
                  "en kies daarna HandBrakeCLI.exe met 'Zoeken…'."
                : " Kies HandBrakeCLI.exe met 'Zoeken…'.";
            HandBrakeStatus = $"✗ HandBrakeCLI niet gevonden of start niet ({_runner.ExecutablePath}).{hint}";
            Log.Error($"HandBrakeCLI werkt niet: {_runner.ExecutablePath}.{hint}");
        }
        await ReloadPresetsAsync();
    }

    public Task RefreshHandBrakeAsync() => ProbeHandBrakeAsync();

    // --- Scan ---------------------------------------------------------------------------
    public async Task ScanAsync()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath))
        {
            Status = "Map bestaat niet.";
            return;
        }

        IsBusy = true;
        Status = "Scannen…";
        Log.Info($"Scannen: {FolderPath}");
        var previouslySelected = SelectedNode?.FullPath;
        _cts = new CancellationTokenSource();
        var folder = FolderPath;

        try
        {
            var result = await Task.Run(() => FolderScanner.Scan(folder, _cts.Token), _cts.Token);

            Roots.Clear();
            Roots.Add(result.Root);
            OnPropertyChanged(nameof(Root));
            SelectedNode = (previouslySelected != null ? result.Root.Find(previouslySelected) : null) ?? result.Root;
            RefreshSelectedFiles();
            UpdateTexts();

            var r = result.Root;
            AppSettings.SetString(AppSettings.LastFolder, folder);
            Log.Info($"Scan klaar: {folder} — {r.PendingCount} te doen ({Format.Bytes(r.PendingBytes)}), " +
                     $"{r.ConvertedCount} omgezet, {r.NotWorthCount} .nomkv");
            foreach (var t in result.TempFiles)
                Log.Warn($"Onvolledig bestand van een onderbroken omzetting: {t}");
            Status = $"Scan klaar: {r.PendingCount} bestanden om te zetten, {r.ConvertedCount} al omgezet." +
                     (result.TempFiles.Count > 0
                         ? $"  Let op: {result.TempFiles.Count} onvolledig(e) *.hbtmp.mkv bestand(en) van een onderbroken omzetting gevonden."
                         : "");
        }
        catch (OperationCanceledException)
        {
            Status = "Scan geannuleerd.";
        }
        catch (Exception ex)
        {
            Status = "Scan-fout: " + ex.Message;
            Log.Error("Scan-fout", ex);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void RefreshSelectedFiles()
    {
        SelectedFiles.Clear();
        var node = Target;
        if (node == null) return;

        var ordered = node.AllFiles()
            .OrderBy(f => f.IsConverting ? 0 : f.IsPending ? 1 : f.IsConverted ? 2 : 3)
            .ThenByDescending(f => f.SizeBytes);
        foreach (var f in ordered)
            SelectedFiles.Add(f);
    }

    private void UpdateTexts()
    {
        var node = Target;
        if (node == null)
        {
            SelectedTitle = "Geen map gescand";
            SelectedPendingText = SelectedConvertedText = TotalText = string.Empty;
            return;
        }

        SelectedTitle = node.FullPath;
        SelectedPendingText = node.PendingCount == 0
            ? "Niets meer om te zetten in deze map."
            : $"Om te zetten: {node.PendingCount} bestand(en), {Format.Bytes(node.PendingBytes)}" +
              (node.FailedCount > 0 ? $"   ({node.FailedCount} eerder mislukt)" : "");
        if (node.NotWorthCount > 0)
            SelectedPendingText += $"\nNiet de moeite (.nomkv, origineel behouden): {node.NotWorthCount} bestand(en), {Format.Bytes(node.NotWorthBytes)}";
        SelectedConvertedText = node.ConvertedCount == 0
            ? string.Empty
            : $"Omgezet (origineel nog aanwezig): {node.ConvertedCount} — " +
              $"{Format.Bytes(node.ConvertedOriginalBytes)} → {Format.Bytes(node.ConvertedMkvBytes)}, " +
              $"bespaard {Format.Bytes(node.SavedBytes)} ({Format.Change(node.ConvertedOriginalBytes, node.ConvertedMkvBytes)})";

        var r = Root;
        TotalText = r == null
            ? string.Empty
            : $"Hele scan: {Format.Bytes(r.PendingBytes)} te doen • {Format.Bytes(r.SavedBytes)} bespaard";
    }

    private void FileChanged(VideoFile file)
    {
        file.Folder?.RecalculateUpwards();
        UpdateTexts();
    }

    // --- Convert ------------------------------------------------------------------------
    /// <summary>Convert every not-yet-converted video in the selected folder (and below).</summary>
    public async Task ConvertFolderAsync()
    {
        if (IsBusy) return;
        var node = Target;
        if (node == null)
        {
            Status = "Scan eerst een map.";
            return;
        }

        var queue = node.AllFiles()
            .Where(f => f.IsPending)
            .OrderBy(f => f.FullPath, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (queue.Count == 0)
        {
            Status = "Niets meer om te zetten in deze map.";
            return;
        }

        await ConvertQueueAsync(queue, node.Name, $"Map omzetten: {node.FullPath}");
    }

    /// <summary>Convert one single file (e.g. as a test of the chosen preset).</summary>
    public async Task ConvertFileAsync(VideoFile file)
    {
        if (IsBusy) return;
        if (file.IsNotWorth)
        {
            Status = "Dit bestand is gemarkeerd als .nomkv (niet de moeite) en wordt niet omgezet.";
            return;
        }
        if (file.IsConverted)
        {
            Status = "Dit bestand is al omgezet. Verwijder eerst de .mkv (\"Verwijder omgezette\") om het opnieuw te doen.";
            return;
        }
        await ConvertQueueAsync(new List<VideoFile> { file }, file.Name, $"Eén bestand omzetten: {file.FullPath}");
    }

    private async Task ConvertQueueAsync(List<VideoFile> queue, string label, string logTitle)
    {
        var version = await _runner.TryGetVersionAsync(CancellationToken.None);
        if (version == null)
        {
            Status = "HandBrakeCLI niet gevonden — controleer het pad bovenaan.";
            return;
        }

        IsBusy = true;
        OverallProgress = 0;
        CurrentProgress = 0;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var presetItem = SelectedPreset;
        if (presetItem == null)
        {
            Status = "Kies eerst een preset.";
            return;
        }
        var preset = presetItem.Name;
        var importFile = presetItem.ImportFile ?? (string.IsNullOrWhiteSpace(PresetImportFile) ? null : PresetImportFile);

        long totalBytes = Math.Max(1, queue.Sum(f => f.SizeBytes));
        long doneBytes = 0;
        int converted = 0, failed = 0, notWorth = 0;
        var minPct = MinSavingPercent;
        long origBytes = 0, mkvBytes = 0;
        bool cancelled = false;
        SessionText = string.Empty;
        Log.Info($"{logTitle} — {queue.Count} bestand(en), {Format.Bytes(totalBytes)}, " +
                 $"preset '{preset}'{(importFile != null ? $" uit {importFile}" : "")}, min. besparing {minPct}%");

        var runClock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < queue.Count; i++)
            {
                if (token.IsCancellationRequested) { cancelled = true; break; }

                var file = queue[i];
                var baseBytes = doneBytes;
                file.Status = FileStatus.Converting;
                file.Progress = 0;
                file.Error = string.Empty;
                CurrentFileText = $"[{i + 1}/{queue.Count}] {file.RelativePath}";
                Status = $"Bezig met {label}: {i + 1} van {queue.Count}";

                int fileNo = i + 1;
                int lastLoggedDecile = 0;
                CurrentDetail = "starten…";
                var result = await _runner.ConvertAsync(
                    file.FullPath, file.TargetPath, preset, importFile,
                    p => Post(() =>
                    {
                        if (!file.IsConverting) return;
                        var pct = p.Percent;
                        file.Progress = pct;
                        CurrentProgress = pct;
                        double processed = baseBytes + file.SizeBytes * pct / 100.0;
                        OverallProgress = processed * 100.0 / totalBytes;

                        var parts = new List<string> { $"{pct:0.0} %" };
                        if (p.Fps is double fps) parts.Add($"{fps:0} fps");
                        if (p.Eta is TimeSpan eta) parts.Add("nog " + Format.Duration(eta));
                        CurrentDetail = string.Join("  •  ", parts);

                        // Whole run: extrapolate from the bytes processed so far.
                        var elapsed = runClock.Elapsed;
                        string rest = "";
                        if (elapsed.TotalSeconds > 20 && processed > 0)
                        {
                            var remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * (totalBytes - processed) / processed);
                            rest = "  •  nog ~" + Format.Duration(remaining);
                        }
                        OverallDetail = $"Totaal: bestand {fileNo}/{queue.Count}  •  {OverallProgress:0.0} %{rest}";

                        int decile = (int)(pct / 10);
                        if (decile > lastLoggedDecile && decile < 10)
                        {
                            lastLoggedDecile = decile;
                            Log.Info($"  {file.Name}: {CurrentDetail}");
                        }
                    }),
                    token);

                if (result.Cancelled)
                {
                    file.Status = FileStatus.Pending;
                    file.Progress = 0;
                    FileChanged(file);
                    cancelled = true;
                    break;
                }

                if (result.Success && !IsWorthIt(file.SizeBytes, result.MkvBytes))
                {
                    // Not worth it: drop the new .mkv, keep the original and mark it "*.nomkv.*".
                    var pct = Format.Change(file.SizeBytes, result.MkvBytes);
                    Log.Info($"Te weinig besparing ({Format.Bytes(file.SizeBytes)} → {Format.Bytes(result.MkvBytes)}, {pct}): {file.FullPath}");
                    // Find out why while the .mkv still exists.
                    CurrentDetail = "analyseren waarom…";
                    var why = await AnalyseAsync(file.FullPath, file.TargetPath, preset, importFile);
                    MarkNotWorth(file, $"besparing {pct} < {minPct}%" + (why != null ? $" — {why}" : ""));
                    notWorth++;
                }
                else if (result.Success || result.Skipped)
                {
                    file.MkvBytes = result.MkvBytes;
                    file.Verify = VerifyStatus.NotChecked;
                    file.VerifyInfo = string.Empty;
                    file.Status = FileStatus.Converted;
                    file.Progress = 100;
                    converted++;
                    Log.Info(result.Skipped
                        ? $".mkv bestond al: {file.TargetPath}"
                        : $"Klaar: {file.Name}  {Format.Bytes(file.SizeBytes)} → {Format.Bytes(result.MkvBytes)} ({Format.Change(file.SizeBytes, result.MkvBytes)})");
                    origBytes += file.SizeBytes;
                    mkvBytes += result.MkvBytes;
                }
                else
                {
                    file.Error = result.Error ?? "onbekende fout";
                    file.Status = FileStatus.Failed;
                    file.Progress = 0;
                    failed++;
                    Log.Error($"Omzetten mislukt: {file.FullPath} — {file.Error}");
                }

                FileChanged(file);
                doneBytes += file.SizeBytes;
                OverallProgress = doneBytes * 100.0 / totalBytes;
                SessionText = SessionSummary(converted, failed, notWorth, origBytes, mkvBytes, running: true);
            }

            SessionText = SessionSummary(converted, failed, notWorth, origBytes, mkvBytes, running: false);
            Log.Info((cancelled ? "Gestopt. " : "Klaar. ") + SessionText);
            Status = cancelled
                ? $"Gestopt — {converted} bestand(en) omgezet. Originelen staan er nog."
                : $"Klaar met {label}. Originelen staan er nog — controleer en verwijder ze daarna.";
        }
        catch (Exception ex)
        {
            Status = "Fout: " + ex.Message;
            Log.Error("Fout tijdens omzetten", ex);
        }
        finally
        {
            CurrentFileText = string.Empty;
            CurrentDetail = string.Empty;
            OverallDetail = $"Totaal: klaar in {Format.Duration(runClock.Elapsed)}";
            CurrentProgress = 0;
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSelectedFiles();
        }
    }

    /// <summary>
    /// Probe original (and .mkv if given) with HandBrakeCLI and log why the conversion
    /// does not save space. Returns a short reason, or null when the source can't be read.
    /// </summary>
    private async Task<string?> AnalyseAsync(string originalPath, string? mkvPath, string presetName, string? importFile)
    {
        try
        {
            Log.Info($"Analyse: {Path.GetFileName(originalPath)}");
            var src = await MediaAnalysis.ProbeAsync(_runner, originalPath, CancellationToken.None);
            if (src == null)
            {
                Log.Warn("  Analyse: bron kon niet gelezen worden.");
                return null;
            }
            MediaInfo? outp = null;
            if (!string.IsNullOrEmpty(mkvPath) && File.Exists(mkvPath))
                outp = await MediaAnalysis.ProbeAsync(_runner, mkvPath, CancellationToken.None);

            var settings = PresetCatalog.GetSettings(importFile, presetName);
            var (reason, lines) = MediaAnalysis.Explain(src, outp, settings);
            foreach (var l in lines) Log.Info("  " + l);
            return reason;
        }
        catch (Exception ex)
        {
            Log.Error("Analyse mislukt", ex);
            return null;
        }
    }

    /// <summary>"Analyseer" button: explain what converting this file does / would do.</summary>
    public async Task AnalyseFileAsync(VideoFile file)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = $"Analyseren: {file.Name}…";
        try
        {
            var p = SelectedPreset;
            var importFile = p?.ImportFile ?? (string.IsNullOrWhiteSpace(PresetImportFile) ? null : PresetImportFile);
            var why = await AnalyseAsync(file.FullPath, File.Exists(file.TargetPath) ? file.TargetPath : null,
                                         p?.Name ?? "", importFile);
            Status = why != null ? $"Analyse {file.Name}: {why} (details in de log)" : "Analyse mislukt (zie log).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Delete the freshly made .mkv and rename the original to "name.nomkv.ext".
    /// The original itself is never deleted here.
    /// </summary>
    private void MarkNotWorth(VideoFile file, string reason)
    {
        try
        {
            if (File.Exists(file.TargetPath))
            {
                File.Delete(file.TargetPath);
                Log.Info($".mkv verwijderd (te weinig besparing): {file.TargetPath}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($".mkv verwijderen mislukt: {file.TargetPath}", ex);
            file.Error = "kon .mkv niet verwijderen: " + ex.Message;
            file.Status = FileStatus.Failed;
            return;
        }

        file.MkvBytes = null;
        var renamed = VideoFile.NoMkvPathFor(file.FullPath);
        try
        {
            if (!File.Exists(renamed))
            {
                File.Move(file.FullPath, renamed);
                Log.Info($"Hernoemd naar .nomkv: {renamed}");
                file.SetPath(renamed);
                file.Error = reason;
            }
            else
            {
                file.Error = reason + $" (kon niet hernoemen: {Path.GetFileName(renamed)} bestaat al)";
            }
        }
        catch (Exception ex)
        {
            file.Error = reason + " (hernoemen mislukt: " + ex.Message + ")";
        }
        file.Progress = 0;
        file.Status = FileStatus.NotWorth;
    }

    private static string SessionSummary(int converted, int failed, int notWorth, long orig, long mkv, bool running)
    {
        var s = $"{(running ? "Tot nu toe" : "Deze sessie")}: {converted} omgezet — " +
                $"{Format.Bytes(orig)} → {Format.Bytes(mkv)}, bespaard {Format.Bytes(orig - mkv)}" +
                (orig > 0 ? $" ({Format.Change(orig, mkv)})" : "");
        if (notWorth > 0) s += $" • {notWorth} niet de moeite (origineel behouden als .nomkv)";
        if (failed > 0) s += $" • {failed} mislukt";
        return s;
    }

    // --- Verify -------------------------------------------------------------------------
    /// <summary>
    /// Compare the duration of each original with its .mkv (via HandBrakeCLI --scan).
    /// </summary>
    public async Task VerifyFolderAsync()
    {
        if (IsBusy) return;
        var node = Target;
        if (node == null) return;

        var list = node.AllFiles().Where(f => f.IsConverted && f.Verify != VerifyStatus.Ok).ToList();
        if (list.Count == 0)
        {
            Status = "Niets te controleren (geen ongecontroleerde omgezette bestanden).";
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        int ok = 0, bad = 0;
        try
        {
            for (int i = 0; i < list.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var f = list[i];
                CurrentFileText = $"Controleren [{i + 1}/{list.Count}] {f.RelativePath}";
                OverallProgress = i * 100.0 / list.Count;
                f.Verify = VerifyStatus.Checking;

                if (!File.Exists(f.TargetPath) || new FileInfo(f.TargetPath).Length == 0)
                {
                    f.VerifyInfo = ".mkv ontbreekt of is leeg";
                    f.Verify = VerifyStatus.Mismatch;
                    bad++;
                    continue;
                }

                var a = await _runner.GetDurationAsync(f.FullPath, token);
                var b = await _runner.GetDurationAsync(f.TargetPath, token);

                if (a == null || b == null)
                {
                    f.VerifyInfo = "duur niet leesbaar" + (b == null ? " (mkv)" : " (origineel)");
                    f.Verify = VerifyStatus.Error;
                    bad++;
                }
                else if (Math.Abs((a.Value - b.Value).TotalSeconds) <= 2)
                {
                    f.VerifyInfo = $"duur gelijk ({Format.Duration(b.Value)})";
                    f.Verify = VerifyStatus.Ok;
                    ok++;
                }
                else
                {
                    f.VerifyInfo = $"duur verschilt: {Format.Duration(a.Value)} vs {Format.Duration(b.Value)}";
                    f.Verify = VerifyStatus.Mismatch;
                    bad++;
                    Log.Warn($"Duur verschilt ({f.VerifyInfo}): {f.FullPath}");
                }
            }
            Status = $"Controle klaar: {ok} in orde, {bad} met een probleem.";
            Log.Info(Status);
        }
        catch (OperationCanceledException)
        {
            foreach (var f in list.Where(f => f.Verify == VerifyStatus.Checking))
                f.Verify = VerifyStatus.NotChecked;
            Status = "Controle gestopt.";
        }
        finally
        {
            CurrentFileText = string.Empty;
            OverallProgress = 0;
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    // --- Delete originals ---------------------------------------------------------------
    /// <param name="Files">Originals to delete (their .mkv is kept).</param>
    /// <param name="LowSaving">Saving below the threshold: delete the .mkv, keep the original as *.nomkv.*.</param>
    /// <param name="MissingMkv">Marked converted, but the .mkv is gone/empty: reset to "te doen", original kept.</param>
    public sealed record DeletePlan(List<VideoFile> Files, long OriginalBytes, long MkvBytes, int SkippedUnverified,
                                    List<VideoFile> LowSaving, int MissingMkv);

    /// <summary>
    /// Re-check every "converted" file in the folder against the disk: the .mkv must still exist
    /// and not be empty. Files whose .mkv disappeared go back to "te doen". Returns how many were reset.
    /// </summary>
    private int RefreshConvertedFromDisk(FolderNode node)
    {
        int missing = 0;
        foreach (var f in node.AllFiles().Where(f => f.IsConverted).ToList())
        {
            long len = -1;
            try { if (File.Exists(f.TargetPath)) len = new FileInfo(f.TargetPath).Length; } catch { }

            if (len <= 0)
            {
                Log.Warn($".mkv niet (meer) gevonden, terug op 'te doen': {f.TargetPath}");
                f.MkvBytes = null;
                f.Verify = VerifyStatus.NotChecked;
                f.VerifyInfo = string.Empty;
                f.Status = FileStatus.Pending;
                f.Error = string.Empty;
                f.Folder?.RecalculateUpwards();
                missing++;
            }
            else if (f.MkvBytes != len)
            {
                f.MkvBytes = len;              // .mkv changed on disk: re-verify
                f.Verify = VerifyStatus.NotChecked;
                f.Folder?.RecalculateUpwards();
            }
        }
        if (missing > 0) { RefreshSelectedFiles(); UpdateTexts(); }
        return missing;
    }

    /// <summary>Which originals in the selected folder may be deleted right now.</summary>
    public DeletePlan? PlanDelete()
    {
        var node = Target;
        if (node == null) return null;

        int missing = RefreshConvertedFromDisk(node);
        var converted = node.AllFiles().Where(f => f.IsConverted).ToList();
        var lowSaving = converted.Where(f => !IsWorthIt(f.SizeBytes, f.MkvBytes ?? f.SizeBytes)).ToList();
        var worth = converted.Except(lowSaving).ToList();
        var allowed = worth.Where(f => !OnlyVerified || f.Verify == VerifyStatus.Ok).ToList();
        return new DeletePlan(
            allowed,
            allowed.Sum(f => f.SizeBytes),
            allowed.Sum(f => f.MkvBytes ?? 0),
            worth.Count - allowed.Count,
            lowSaving,
            missing);
    }

    public async Task DeleteOriginalsAsync(DeletePlan plan)
    {
        if (IsBusy || (plan.Files.Count == 0 && plan.LowSaving.Count == 0)) return;
        IsBusy = true;
        int deleted = 0, failed = 0;
        long freed = 0, saved = 0;
        var recycle = UseRecycleBin;

        int kept = 0;
        try
        {
            // Too little saving: keep the original, throw away the .mkv.
            foreach (var f in plan.LowSaving)
            {
                MarkNotWorth(f, $"besparing {Format.Change(f.SizeBytes, f.MkvBytes ?? f.SizeBytes)} < {MinSavingPercent}%");
                if (f.IsNotWorth) kept++; else failed++;
                f.Folder?.RecalculateUpwards();
            }

            for (int i = 0; i < plan.Files.Count; i++)
            {
                var f = plan.Files[i];
                CurrentFileText = $"Verwijderen [{i + 1}/{plan.Files.Count}] {f.RelativePath}";
                OverallProgress = i * 100.0 / plan.Files.Count;
                await Task.Delay(1); // keep the UI responsive

                // Final safety net: only delete when the .mkv is really there and non-empty.
                if (!File.Exists(f.TargetPath) || new FileInfo(f.TargetPath).Length == 0)
                {
                    Log.Warn($"Origineel NIET verwijderd, .mkv ontbreekt of is leeg: {f.TargetPath}");
                    failed++;
                    continue;
                }

                try
                {
                    FileOps.Delete(f.FullPath, recycle);
                }
                catch (Exception ex)
                {
                    Log.Error($"Verwijderen mislukt: {f.FullPath}", ex);
                    failed++;
                    continue;
                }

                if (File.Exists(f.FullPath)) // e.g. user cancelled a recycle-bin dialog
                {
                    failed++;
                    continue;
                }

                deleted++;
                freed += f.SizeBytes;
                saved += f.SavedBytes;
                f.Folder?.Files.Remove(f);
                f.Folder?.RecalculateUpwards();
            }

            Root?.PruneEmpty();
            if (SelectedNode != null && Root != null && Root.Find(SelectedNode.FullPath) == null)
                SelectedNode = Root;

            SessionText = $"{deleted} origine(e)l(en) verwijderd{(recycle ? " (naar prullenbak)" : "")} — " +
                          $"{Format.Bytes(freed)} vrijgemaakt, netto bespaard {Format.Bytes(saved)}." +
                          (kept > 0 ? $"  {kept} origine(e)l(en) behouden als .nomkv (te weinig besparing), hun .mkv is verwijderd." : "");
            Status = failed > 0 ? $"{failed} bestand(en) konden niet verwijderd worden." : "Originelen verwijderd.";
            Log.Info(SessionText);
        }
        finally
        {
            CurrentFileText = string.Empty;
            OverallProgress = 0;
            IsBusy = false;
            RefreshSelectedFiles();
            UpdateTexts();
        }
    }

    // --- Delete converted (.mkv) --------------------------------------------------------
    public sealed record DeleteConvertedPlan(List<VideoFile> Files, long MkvBytes, int MissingMkv);

    /// <summary>The .mkv files in the selected folder that can be removed (originals stay).</summary>
    public DeleteConvertedPlan? PlanDeleteConverted()
    {
        var node = Target;
        if (node == null) return null;
        int missing = RefreshConvertedFromDisk(node);
        var files = node.AllFiles().Where(f => f.IsConverted).ToList();
        return new DeleteConvertedPlan(files, files.Sum(f => f.MkvBytes ?? 0), missing);
    }

    /// <summary>
    /// Delete the converted .mkv files; the originals are kept and go back to "te doen".
    /// Only deletes when the original is still there, so a video never disappears completely.
    /// </summary>
    public async Task DeleteConvertedAsync(DeleteConvertedPlan plan)
    {
        if (IsBusy || plan.Files.Count == 0) return;
        IsBusy = true;
        int deleted = 0, failed = 0, noOriginal = 0;
        long freed = 0;
        var recycle = UseRecycleBin;

        try
        {
            for (int i = 0; i < plan.Files.Count; i++)
            {
                var f = plan.Files[i];
                CurrentFileText = $".mkv verwijderen [{i + 1}/{plan.Files.Count}] {Path.GetFileName(f.TargetPath)}";
                OverallProgress = i * 100.0 / plan.Files.Count;
                await Task.Delay(1);

                if (!File.Exists(f.FullPath))
                {
                    Log.Warn($".mkv NIET verwijderd, origineel ontbreekt: {f.TargetPath}");
                    noOriginal++; // the .mkv would be the only copy left: leave it alone
                    continue;
                }
                if (!File.Exists(f.TargetPath))
                {
                    f.MkvBytes = null;
                    f.Status = FileStatus.Pending;
                    f.Folder?.RecalculateUpwards();
                    continue;
                }

                try
                {
                    FileOps.Delete(f.TargetPath, recycle);
                }
                catch (Exception ex)
                {
                    Log.Error($"Verwijderen mislukt: {f.TargetPath}", ex);
                    failed++;
                    continue;
                }
                if (File.Exists(f.TargetPath))
                {
                    failed++;
                    continue;
                }

                deleted++;
                freed += f.MkvBytes ?? 0;
                f.MkvBytes = null;
                f.Verify = VerifyStatus.NotChecked;
                f.VerifyInfo = string.Empty;
                f.Progress = 0;
                f.Status = FileStatus.Pending;
                f.Folder?.RecalculateUpwards();
            }

            SessionText = $"{deleted} omgezet(te) .mkv-bestand(en) verwijderd{(recycle ? " (naar prullenbak)" : "")} — " +
                          $"{Format.Bytes(freed)} vrijgemaakt. De originelen staan weer op 'te doen'.";
            var problems = new List<string>();
            if (failed > 0) problems.Add($"{failed} konden niet verwijderd worden");
            if (noOriginal > 0) problems.Add($"{noOriginal} overgeslagen omdat het origineel ontbreekt");
            Status = problems.Count > 0 ? string.Join(", ", problems) + "." : "Omgezette bestanden verwijderd.";
            Log.Info(SessionText + (problems.Count > 0 ? "  " + Status : ""));
        }
        finally
        {
            CurrentFileText = string.Empty;
            OverallProgress = 0;
            IsBusy = false;
            RefreshSelectedFiles();
            UpdateTexts();
        }
    }

    public void Cancel() => _cts?.Cancel();

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
