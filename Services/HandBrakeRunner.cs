using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HandBrakeTreemap.Services;

/// <summary>One progress update from HandBrakeCLI.</summary>
public readonly record struct EncodeProgress(double Percent, double? Fps, double? AvgFps, TimeSpan? Eta);

public sealed class ConversionResult
{
    public bool Success { get; init; }
    public bool Skipped { get; init; }
    public bool Cancelled { get; init; }
    public long MkvBytes { get; init; }
    public string? Error { get; init; }
}

/// <summary>Wraps the HandBrakeCLI executable: discovery, transcoding and duration probing.</summary>
public sealed class HandBrakeRunner
{
    // Matches "Encoding: task 1 of 1, 42.08 %".
    private static readonly Regex ProgressRegex =
        new(@"(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);

    private static readonly Regex DetailRegex =
        new(@"\(([\d.]+)\s*fps,\s*avg\s*([\d.]+)\s*fps,\s*ETA\s*(\d+)h(\d+)m(\d+)s", RegexOptions.Compiled);

    private static double? ParseD(string s)
        => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    // Matches the title duration in a --scan log: "  + duration: 01:42:13".
    private static readonly Regex DurationRegex =
        new(@"\+ duration:\s*(\d+):(\d{2}):(\d{2})", RegexOptions.Compiled);

    public string ExecutablePath { get; set; }

    public HandBrakeRunner(string? executablePath = null)
    {
        ExecutablePath = string.IsNullOrWhiteSpace(executablePath)
            ? (LocateExecutable() ?? DefaultName)
            : executablePath!;
    }

    private static string DefaultName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "HandBrakeCLI.exe" : "HandBrakeCLI";

    /// <summary>Best-effort discovery of HandBrakeCLI on PATH and common install locations.</summary>
    public static string? LocateExecutable()
    {
        var exeName = DefaultName;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }

        var guesses = new List<string>
        {
            // Next to this program (e.g. HandBrakeCLI.exe copied into the app folder).
            Path.Combine(AppContext.BaseDirectory, exeName)
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            guesses.Add(Path.Combine(pf, "HandBrake", "HandBrakeCLI.exe"));
            guesses.Add(Path.Combine(pf, "HandBrakeCLI", "HandBrakeCLI.exe"));
            guesses.Add(Path.Combine(pfx86, "HandBrake", "HandBrakeCLI.exe"));
            guesses.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "HandBrakeCLI.exe"));

            // winget installs the CLI under ...\WinGet\Packages\HandBrake.HandBrake.CLI_<id>\
            try
            {
                var pkgs = Path.Combine(local, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(pkgs))
                    foreach (var d in Directory.EnumerateDirectories(pkgs, "HandBrake.HandBrake.CLI*"))
                        foreach (var exe in Directory.EnumerateFiles(d, "HandBrakeCLI.exe", SearchOption.AllDirectories))
                            guesses.Add(exe);
            }
            catch { /* ignore */ }
        }
        else
        {
            guesses.Add("/usr/bin/HandBrakeCLI");
            guesses.Add("/usr/local/bin/HandBrakeCLI");
            guesses.Add("/opt/homebrew/bin/HandBrakeCLI");
            guesses.Add("/snap/bin/HandBrakeCLI");
        }

        foreach (var g in guesses)
            if (File.Exists(g)) return g;

        return null;
    }

    /// <summary>True when the HandBrake GUI is installed but the separate CLI isn't (common pitfall).</summary>
    public static bool GuiInstalledWithoutCli()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var dir = Path.Combine(pf, "HandBrake");
        return File.Exists(Path.Combine(dir, "HandBrake.exe")) && !File.Exists(Path.Combine(dir, "HandBrakeCLI.exe"));
    }

    /// <summary>Query HandBrake for its version, used as an availability probe.</summary>
    public async Task<string?> TryGetVersionAsync(CancellationToken token)
    {
        try
        {
            var (output, _) = await RunToEndAsync(new[] { "--version" }, token);
            var text = output.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            foreach (var line in text.Split('\n'))
                if (line.TrimStart().StartsWith("HandBrake", StringComparison.OrdinalIgnoreCase))
                    return line.Trim();
            return text.Split('\n')[0].Trim();
        }
        catch (Exception ex)
        {
            Log.Warn($"HandBrakeCLI starten mislukt ('{ExecutablePath}'): {ex.Message}");
            return null;
        }
    }

    /// <summary>Raw output of <c>HandBrakeCLI --preset-list</c> (null when HandBrakeCLI can't run).</summary>
    public async Task<string?> GetPresetListAsync(CancellationToken token)
    {
        try
        {
            var (output, _) = await RunToEndAsync(new[] { "--preset-list" }, token);
            return output;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Presetlijst ophalen bij HandBrakeCLI mislukt: {ex.Message}");
            return null;
        }
    }

    /// <summary>Duration of the main title, via <c>HandBrakeCLI --scan</c>. Null if unreadable.</summary>
    public async Task<TimeSpan?> GetDurationAsync(string path, CancellationToken token)
    {
        try
        {
            var (output, _) = await RunToEndAsync(
                new[] { "--scan", "--previews", "1:0", "--input", path }, token);
            var m = DurationRegex.Match(output);
            if (!m.Success)
            {
                Log.Warn($"Geen speelduur gevonden voor {path}");
                return null;
            }
            return new TimeSpan(int.Parse(m.Groups[1].Value),
                                int.Parse(m.Groups[2].Value),
                                int.Parse(m.Groups[3].Value));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Speelduur uitlezen mislukt voor {path}: {ex.Message}");
            return null;
        }
    }

    private async Task<(string output, int exitCode)> RunToEndAsync(IEnumerable<string> args, CancellationToken token)
    {
        var psi = BaseStartInfo();
        foreach (var a in args) psi.ArgumentList.Add(a);
        Log.Info($"Start: {Quote(psi.FileName)} {string.Join(" ", psi.ArgumentList.Select(Quote))}");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Kon HandBrakeCLI niet starten");
        using var reg = token.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });

        // Read both streams concurrently so neither pipe can fill up and block the process.
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(outTask, errTask);
        await proc.WaitForExitAsync(token);
        Log.Info($"  → exitcode {proc.ExitCode}");
        return (outTask.Result + "\n" + errTask.Result, proc.ExitCode);
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    /// <summary>
    /// Transcode <paramref name="inputPath"/> into <paramref name="outputPath"/> (.mkv).
    /// HandBrake writes to a temporary "*.hbtmp.mkv" which is only renamed to the final name
    /// once it finished successfully, so a half-written file never looks "converted".
    /// The original is never touched.
    /// </summary>
    public async Task<ConversionResult> ConvertAsync(
        string inputPath,
        string outputPath,
        string presetName,
        string? presetImportFile,
        Action<EncodeProgress>? onProgress,
        CancellationToken token)
    {
        if (File.Exists(outputPath))
            return new ConversionResult { Skipped = true, MkvBytes = new FileInfo(outputPath).Length, Error = "Doelbestand bestaat al" };

        var tempPath = FolderScanner.TempFor(outputPath);
        TryDelete(tempPath);

        var psi = BaseStartInfo();
        if (!string.IsNullOrWhiteSpace(presetImportFile) && File.Exists(presetImportFile))
        {
            psi.ArgumentList.Add("--preset-import-file");
            psi.ArgumentList.Add(presetImportFile!);
        }
        psi.ArgumentList.Add("--preset");
        psi.ArgumentList.Add(presetName);
        psi.ArgumentList.Add("--input");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(tempPath);
        psi.ArgumentList.Add("--format");
        psi.ArgumentList.Add("av_mkv"); // force Matroska whatever the preset says
        Log.Info($"Omzetten: {Quote(psi.FileName)} {string.Join(" ", psi.ArgumentList.Select(Quote))}");

        var stderr = new StringBuilder();          // rolling tail
        var head = new List<string>();              // first lines: init errors appear early
        try
        {
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            double lastReported = -1;
            void Handle(string? line)
            {
                if (string.IsNullOrEmpty(line)) return;
                var m = ProgressRegex.Match(line);
                if (m.Success && double.TryParse(m.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                {
                    if (pct >= 0 && pct <= 100 && Math.Abs(pct - lastReported) >= 0.1)
                    {
                        lastReported = pct;
                        // "Encoding: task 1 of 1, 42.08 % (85.31 fps, avg 80.12 fps, ETA 00h12m30s)"
                        double? fps = null, avg = null;
                        TimeSpan? eta = null;
                        var d = DetailRegex.Match(line);
                        if (d.Success)
                        {
                            fps = ParseD(d.Groups[1].Value);
                            avg = ParseD(d.Groups[2].Value);
                            eta = new TimeSpan(int.Parse(d.Groups[3].Value), int.Parse(d.Groups[4].Value), int.Parse(d.Groups[5].Value));
                        }
                        onProgress?.Invoke(new EncodeProgress(pct, fps, avg, eta));
                    }
                }
            }

            proc.OutputDataReceived += (_, e) => Handle(e.Data);
            proc.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                lock (stderr)
                {
                    if (head.Count < 3000) head.Add(e.Data);
                    stderr.AppendLine(e.Data);
                    if (stderr.Length > 64_000) stderr.Remove(0, stderr.Length - 32_000);
                }
            };

            if (!proc.Start())
                return new ConversionResult { Error = "Kon HandBrakeCLI niet starten" };

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using (token.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } }))
            {
                try
                {
                    await proc.WaitForExitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    // fall through: handled below
                }
            }

            if (token.IsCancellationRequested)
            {
                try { proc.WaitForExit(5000); } catch { }
                TryDelete(tempPath);
                return new ConversionResult { Cancelled = true, Error = "Geannuleerd" };
            }

            if (proc.ExitCode == 0 && File.Exists(tempPath) && new FileInfo(tempPath).Length > 0)
            {
                File.Move(tempPath, outputPath);
                onProgress?.Invoke(new EncodeProgress(100, null, null, TimeSpan.Zero));
                return new ConversionResult { Success = true, MkvBytes = new FileInfo(outputPath).Length };
            }

            TryDelete(tempPath);
            string tail, full;
            List<string> headCopy;
            lock (stderr)
            {
                tail = Tail(stderr.ToString(), 300);
                headCopy = new List<string>(head);
                full = string.Join(Environment.NewLine, head) + Environment.NewLine +
                       "----- (laatste regels) -----" + Environment.NewLine + stderr;
            }
            ReportFailure(proc.ExitCode, headCopy, full);
            return new ConversionResult { Error = $"exit {proc.ExitCode}. {tail}" };
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            return new ConversionResult { Error = ex.Message };
        }
    }

    // Lines in HandBrake's log that explain why an encode failed.
    private static readonly Regex ErrorLineRegex = new(
        @"error|fail|cannot|can't|unable|invalid|not supported|unsupported|nvenc|nvEncode|cuda|driver|qsv|vce|amf|no such|denied",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// On a failed encode: log the telling lines and keep HandBrake's full log in
    /// %LOCALAPPDATA%\HandBrakeTreemap\handbrake-laatste-fout.log.
    /// </summary>
    private static void ReportFailure(int exitCode, List<string> lines, string fullLog)
    {
        var meaning = exitCode switch
        {
            1 => "geannuleerd",
            2 => "ongeldige invoer/argumenten",
            3 => "initialisatie mislukt (vaak: video-encoder niet beschikbaar, bv. NVENC/QSV of driver te oud)",
            4 => "onbekende fout",
            5 => "fout bij lezen van de bron",
            _ => "onbekend"
        };
        Log.Error($"HandBrake exitcode {exitCode}: {meaning}");

        int shown = 0;
        foreach (var l in lines)
        {
            if (shown >= 25) break;
            if (!ErrorLineRegex.IsMatch(l)) continue;
            if (l.Contains("decoder errors", StringComparison.OrdinalIgnoreCase)) continue; // "0 decoder errors" noise
            Log.Error("  HB: " + l.Trim());
            shown++;
        }

        // Known, fixable causes → a plain-language hint.
        if (fullLog.Contains("minimum required Nvidia driver", StringComparison.OrdinalIgnoreCase) ||
            fullLog.Contains("required nvenc API version", StringComparison.OrdinalIgnoreCase))
            Log.Error("OORZAAK: de NVIDIA-driver is te oud voor deze HandBrakeCLI (NVENC). " +
                      "Oplossing: werk de NVIDIA-driver bij (570 of nieuwer), of gebruik een HandBrakeCLI met " +
                      "dezelfde versie als je HandBrake-GUI (bv. 1.8.x) en kies die met 'Zoeken…'.");
        else if (fullLog.Contains("OpenEncodeSessionEx", StringComparison.OrdinalIgnoreCase) ||
                 fullLog.Contains("out of memory", StringComparison.OrdinalIgnoreCase) && fullLog.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
            Log.Error("OORZAAK: de videokaart kon geen NVENC-sessie openen — waarschijnlijk bezet door een andere encode.");

        try
        {
            var path = Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "handbrake-laatste-fout.log");
            File.WriteAllText(path, fullLog);
            Log.Info($"  Volledig HandBrake-log: {path}");
        }
        catch { /* best effort */ }
    }

    private ProcessStartInfo BaseStartInfo() => new()
    {
        FileName = ExecutablePath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static string Tail(string text, int max)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : "…" + text[^max..];
    }
}
