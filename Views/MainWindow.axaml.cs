using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using HandBrakeTreemap.Models;
using HandBrakeTreemap.Services;
using HandBrakeTreemap.ViewModels;

namespace HandBrakeTreemap.Views;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Double-click on a file: hook with handledEventsToo so it always arrives.
        this.FindControl<ListBox>("FileList")?.AddHandler(
            InputElement.DoubleTappedEvent, OnFileDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(MainViewModel.LogText))
                        // After layout, so the new line is measured before we scroll.
                        Avalonia.Threading.Dispatcher.UIThread.Post(ScrollLogToEnd,
                            Avalonia.Threading.DispatcherPriority.Background);
                };
        };
    }

    // --- Scan / HandBrake ---------------------------------------------------------------
    private async void OnScan(object? sender, RoutedEventArgs e) => await Vm.ScanAsync();

    private async void OnRefreshHandBrake(object? sender, RoutedEventArgs e)
    {
        Log.Info("Knop Test: HandBrakeCLI controleren…");
        await Vm.RefreshHandBrakeAsync();
    }

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Kies je mediamap",
            AllowMultiple = false
        });

        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        Vm.FolderPath = path;
        await Vm.ScanAsync();
    }

    private async void OnBrowseHandBrake(object? sender, RoutedEventArgs e)
    {
        IStorageFolder? start = null;
        try
        {
            var current = Vm.HandBrakePath;
            var dir = Path.IsPathRooted(current) ? Path.GetDirectoryName(current) : null;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                start = await StorageProvider.TryGetFolderFromPathAsync(new Uri(dir));
        }
        catch { /* no start folder, fine */ }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Zoek HandBrakeCLI",
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = OperatingSystem.IsWindows()
                ? new[]
                {
                    new FilePickerFileType("HandBrakeCLI.exe") { Patterns = new[] { "HandBrakeCLI.exe" } },
                    new FilePickerFileType("Programma's (*.exe)") { Patterns = new[] { "*.exe" } }
                }
                : new[] { FilePickerFileTypes.All }
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        Vm.HandBrakePath = path;
        await Vm.RefreshHandBrakeAsync();
    }

    private async void OnReloadPresets(object? sender, RoutedEventArgs e) => await Vm.ReloadPresetsAsync();

    private async void OnBrowsePreset(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Kies een geëxporteerd HandBrake preset-bestand",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("HandBrake preset (*.json)") { Patterns = new[] { "*.json" } },
                FilePickerFileTypes.All
            }
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            Vm.PresetImportFile = path;
            await Vm.ReloadPresetsAsync();
            // Select the first preset from the chosen file.
            var first = Vm.Presets.FirstOrDefault(p => string.Equals(p.ImportFile, path, System.StringComparison.OrdinalIgnoreCase));
            if (first != null) Vm.SelectedPreset = first;
        }
    }

    // --- Convert / verify / delete ------------------------------------------------------
    private async void OnConvertFolder(object? sender, RoutedEventArgs e)
    {
        var node = Vm.SelectedNode ?? Vm.Root;
        if (node == null)
        {
            Vm.Status = "Scan eerst een map.";
            return;
        }
        if (node.PendingCount == 0)
        {
            Vm.Status = "Niets meer om te zetten in deze map.";
            return;
        }

        var ok = await ConfirmAsync(
            "Map omzetten",
            $"{node.PendingCount} bestand(en) ({Format.Bytes(node.PendingBytes)}) in\n{node.FullPath}\n" +
            $"omzetten met preset \"{Vm.SelectedPreset?.Name}\"?\n\nDe originele bestanden blijven staan.",
            "Omzetten");
        if (ok)
            await Vm.ConvertFolderAsync();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Vm.Cancel();

    private async void OnVerify(object? sender, RoutedEventArgs e) => await Vm.VerifyFolderAsync();

    private async void OnDeleteOriginals(object? sender, RoutedEventArgs e)
    {
        var plan = Vm.PlanDelete();
        if (plan == null) return;

        var missingNote = plan.MissingMkv > 0
            ? $"{plan.MissingMkv} bestand(en) hebben geen .mkv meer — die originelen blijven staan en zijn terug 'te doen'. "
            : "";

        if (plan.Files.Count == 0 && plan.LowSaving.Count == 0)
        {
            Vm.Status = missingNote + (plan.SkippedUnverified > 0
                ? $"Geen originelen te verwijderen: {plan.SkippedUnverified} omgezet(te) bestand(en) zijn nog niet (succesvol) gecontroleerd. " +
                  "Klik eerst 'Controleer omzettingen', of zet 'Alleen gecontroleerde' uit."
                : "Geen omgezette originelen in deze map.");
            return;
        }

        var where = Vm.UseRecycleBin ? "naar de prullenbak verplaatsen" : "DEFINITIEF verwijderen";
        var msg = "";
        if (plan.Files.Count > 0)
            msg +=
                $"{plan.Files.Count} originele bestand(en) {where}?\n\n" +
                $"Origineel: {Format.Bytes(plan.OriginalBytes)}\n" +
                $".mkv:        {Format.Bytes(plan.MkvBytes)}\n" +
                $"Bespaard:  {Format.Bytes(plan.OriginalBytes - plan.MkvBytes)} ({Format.Change(plan.OriginalBytes, plan.MkvBytes)})";
        if (plan.LowSaving.Count > 0)
            msg += (msg.Length > 0 ? "\n\n" : "") +
                   $"{plan.LowSaving.Count} bestand(en) besparen minder dan {Vm.MinSavingPercent}%: " +
                   "daarvan wordt de .mkv verwijderd en blijft het origineel behouden (hernoemd naar *.nomkv.*).";
        if (plan.SkippedUnverified > 0)
            msg += $"\n\n{plan.SkippedUnverified} niet-gecontroleerd(e) bestand(en) worden overgeslagen.";
        if (plan.MissingMkv > 0)
            msg += $"\n\n{plan.MissingMkv} bestand(en) hebben geen .mkv meer: die originelen blijven staan.";
        msg += "\n\nVlak voor elk origineel wordt nog eens gecontroleerd of de .mkv er is.";

        if (await ConfirmAsync("Originelen verwijderen", msg, "Verwijderen"))
            await Vm.DeleteOriginalsAsync(plan);
    }

    private async void OnDeleteConverted(object? sender, RoutedEventArgs e)
    {
        var plan = Vm.PlanDeleteConverted();
        if (plan == null) return;

        if (plan.Files.Count == 0)
        {
            Vm.Status = "Geen omgezette .mkv-bestanden in deze map." +
                        (plan.MissingMkv > 0 ? $" ({plan.MissingMkv} waren al verdwenen; die staan terug op 'te doen'.)" : "");
            return;
        }

        var where = Vm.UseRecycleBin ? "naar de prullenbak verplaatsen" : "DEFINITIEF verwijderen";
        var msg =
            $"{plan.Files.Count} omgezet(te) .mkv-bestand(en) ({Format.Bytes(plan.MkvBytes)}) {where}?\n\n" +
            "De originelen blijven staan en komen terug op 'te doen', zodat je ze opnieuw kunt omzetten " +
            "(bv. met een andere preset).\n\nEen .mkv waarvan het origineel ontbreekt, wordt niet verwijderd.";

        if (await ConfirmAsync("Omgezette bestanden verwijderen", msg, "Verwijder .mkv"))
            await Vm.DeleteConvertedAsync(plan);
    }

    // --- Open / play --------------------------------------------------------------------
    /// <summary>The file selected in the list (read straight from the ListBox, with the VM as fallback).</summary>
    private VideoFile? CurrentFile =>
        this.FindControl<ListBox>("FileList")?.SelectedItem as VideoFile ?? Vm.SelectedFile;

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var node = Vm.SelectedNode ?? Vm.Root;
        if (node == null) { Log.Info("Open map: geen map geselecteerd."); return; }
        FileOps.Open(node.FullPath);
    }

    private async void OnConvertFile(object? sender, RoutedEventArgs e)
    {
        var f = CurrentFile;
        if (f == null) { NoFileSelected(); return; }
        if (f.IsPending)
        {
            var ok = await ConfirmAsync(
                "Bestand omzetten",
                $"{f.Name} ({Format.Bytes(f.SizeBytes)})\nomzetten met preset \"{Vm.SelectedPreset?.Name}\"?\n\nHet origineel blijft staan.",
                "Omzetten");
            if (!ok) return;
        }
        await Vm.ConvertFileAsync(f);
    }

    private async void OnAnalyseFile(object? sender, RoutedEventArgs e)
    {
        var f = CurrentFile;
        if (f == null) { NoFileSelected(); return; }
        await Vm.AnalyseFileAsync(f);
    }

    private void OnPlayOriginal(object? sender, RoutedEventArgs e)
    {
        var f = CurrentFile;
        if (f == null) { NoFileSelected(); return; }
        Play(f.FullPath);
    }

    private void OnPlayMkv(object? sender, RoutedEventArgs e)
    {
        var f = CurrentFile;
        if (f == null) { NoFileSelected(); return; }
        if (!File.Exists(f.TargetPath))
        {
            Vm.Status = "Er is (nog) geen .mkv voor dit bestand.";
            Log.Info($"Speel .mkv: bestaat (nog) niet: {f.TargetPath}");
            return;
        }
        Play(f.TargetPath);
    }

    private void OnReveal(object? sender, RoutedEventArgs e)
    {
        var f = CurrentFile;
        if (f == null) { NoFileSelected(); return; }
        FileOps.Reveal(File.Exists(f.TargetPath) ? f.TargetPath : f.FullPath);
    }

    /// <summary>
    /// Double-click a file in the list to play it in the default player:
    /// the .mkv when it has been converted (to check the result), otherwise the original.
    /// Hooked with handledEventsToo so the ListBoxItem can't swallow it.
    /// </summary>
    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        var f = (e.Source as StyledElement)?.DataContext as VideoFile ?? CurrentFile;
        if (f == null) { Log.Info("Dubbelklik: geen bestand onder de muis."); return; }

        Play(f.IsConverted && File.Exists(f.TargetPath) ? f.TargetPath : f.FullPath);
        e.Handled = true;
    }

    private void Play(string path)
    {
        if (FileOps.Open(path))
            Vm.Status = "Afspelen: " + path;
        else
            Vm.Status = "Kon niet afspelen (zie log): " + path;
    }

    private void NoFileSelected()
    {
        Vm.Status = "Selecteer eerst een bestand in de lijst rechts.";
        Log.Info(Vm.Status);
    }

    // --- Log panel ----------------------------------------------------------------------
    private void OnClearLog(object? sender, RoutedEventArgs e) => Vm.ClearLog();

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        if (Clipboard != null)
            await Clipboard.SetTextAsync(Vm.LogText);
    }

    private void OnOpenLogFile(object? sender, RoutedEventArgs e) => FileOps.Open(Log.FilePath);

    /// <summary>Keep the log scrolled to the newest line.</summary>
    private void ScrollLogToEnd()
    {
        this.FindControl<ScrollViewer>("LogScroll")?.ScrollToEnd();
    }

    // --- Simple confirm dialog ----------------------------------------------------------
    private async Task<bool> ConfirmAsync(string title, string message, string okText)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var ok = new Button { Content = okText, IsDefault = true };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Annuleren", IsCancel = true };
        ok.Click += (_, _) => dlg.Close(true);
        cancel.Click += (_, _) => dlg.Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(18), Spacing = 16 };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(buttons);
        dlg.Content = root;

        var result = await dlg.ShowDialog<bool?>(this);
        return result == true;
    }
}
