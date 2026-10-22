using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HandBrakeTreemap.Models;

namespace HandBrakeTreemap.Services;

public sealed record AudioTrackInfo(string Codec, int Channels, long BitRate, string Description);

/// <summary>What HandBrakeCLI --scan --json reports about a file.</summary>
public sealed record MediaInfo(
    string Path,
    long FileBytes,
    string Container,
    string VideoCodec,
    int Width,
    int Height,
    double Fps,
    TimeSpan Duration,
    List<AudioTrackInfo> Audio,
    int SubtitleCount)
{
    /// <summary>Overall bitrate from file size / duration (bits per second).</summary>
    public double TotalBitRate => Duration.TotalSeconds > 0 ? FileBytes * 8.0 / Duration.TotalSeconds : 0;

    public double AudioBitRate => Audio.Sum(a => (double)a.BitRate);

    /// <summary>Estimated video bitrate = total − audio (container overhead ignored).</summary>
    public double VideoBitRate => Math.Max(0, TotalBitRate - AudioBitRate);

    /// <summary>Bits per pixel per frame — how "heavily" the video is compressed.</summary>
    public double BitsPerPixel => Width > 0 && Height > 0 && Fps > 0 ? VideoBitRate / (Width * (double)Height * Fps) : 0;

    public string Describe()
    {
        var audio = Audio.Count == 0
            ? "geen audio"
            : string.Join(" + ", Audio.Select(a => $"{a.Codec} {a.Channels}ch{(a.BitRate > 0 ? $" {Kbps(a.BitRate)}" : "")}"));
        return $"{VideoCodec} {Width}x{Height} {Fps:0.##} fps, {Format.Duration(Duration)}, " +
               $"video ≈ {Kbps(VideoBitRate)}, totaal {Kbps(TotalBitRate)} ({Format.Bytes(FileBytes)}), audio: {audio}" +
               (SubtitleCount > 0 ? $", {SubtitleCount} ondertitel(s)" : "");
    }

    public static string Kbps(double bps) => bps >= 1_000_000 ? $"{bps / 1_000_000:0.0} Mbit/s" : $"{bps / 1000:0} kbit/s";
}

/// <summary>The settings of a preset that matter for "why did the file grow?".</summary>
public sealed record PresetSettings(
    string Name,
    string VideoEncoder,
    int QualityType,          // 2 = constant quality, 1 = average bitrate
    double Quality,
    int AvgBitrate,           // kbit/s when QualityType == 1
    string Framerate,         // "30", "auto", …
    string FramerateMode,     // "pfr" = peak (cap), "cfr", "vfr"
    int MaxWidth,
    int MaxHeight,
    List<string> AudioEncoders,
    List<int> AudioBitrates);

public static class MediaAnalysis
{
    /// <summary>Probe a file with HandBrakeCLI. Null when it can't be read.</summary>
    public static async Task<MediaInfo?> ProbeAsync(HandBrakeRunner runner, string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        var output = await runner.ScanJsonAsync(path, token);
        if (output == null) return null;

        const string marker = "JSON Title Set:";
        int i = output.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
        {
            Log.Warn($"Analyse: geen 'JSON Title Set' in de uitvoer voor {path}");
            return null;
        }
        int start = output.IndexOf('{', i);
        if (start < 0) return null;
        return Parse(output.Substring(start), path);
    }

    /// <summary>Parse the "JSON Title Set" object (text starting at its '{'; trailing output is ignored).</summary>
    public static MediaInfo? Parse(string json, string path)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true });
            using var doc = JsonDocument.ParseValue(ref reader);
            var titles = doc.RootElement.GetProperty("TitleList");
            if (titles.GetArrayLength() == 0) return null;
            var t = titles[0];

            string codec = Str(t, "VideoCodec") ?? "?";
            string container = Str(t, "Container") ?? "";
            int w = 0, h = 0;
            if (t.TryGetProperty("Geometry", out var g))
            {
                w = Int(g, "Width");
                h = Int(g, "Height");
            }
            double fps = 0;
            if (t.TryGetProperty("FrameRate", out var fr))
            {
                var num = Int(fr, "Num");
                var den = Int(fr, "Den");
                if (den > 0) fps = num / (double)den;
            }
            var dur = TimeSpan.Zero;
            if (t.TryGetProperty("Duration", out var d))
            {
                long ticks = d.TryGetProperty("Ticks", out var tk) && tk.TryGetInt64(out var tv) ? tv : 0;
                dur = ticks > 0
                    ? TimeSpan.FromSeconds(ticks / 90000.0)
                    : new TimeSpan(Int(d, "Hours"), Int(d, "Minutes"), Int(d, "Seconds"));
            }

            var audio = new List<AudioTrackInfo>();
            if (t.TryGetProperty("AudioList", out var al) && al.ValueKind == JsonValueKind.Array)
                foreach (var a in al.EnumerateArray())
                    audio.Add(new AudioTrackInfo(
                        Str(a, "CodecName") ?? Str(a, "Codec") ?? "?",
                        Int(a, "ChannelCount"),
                        a.TryGetProperty("BitRate", out var br) && br.TryGetInt64(out var bv) ? bv : 0,
                        Str(a, "Description") ?? ""));

            int subs = t.TryGetProperty("SubtitleList", out var sl) && sl.ValueKind == JsonValueKind.Array ? sl.GetArrayLength() : 0;

            long size = File.Exists(path) ? new FileInfo(path).Length : 0;
            return new MediaInfo(path, size, container, codec, w, h, fps, dur, audio, subs);
        }
        catch (Exception ex)
        {
            Log.Warn($"Analyse: scan-uitvoer niet te lezen voor {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Explain why a conversion did (or would) not save space. Returns a short reason
    /// (for the file's status) and the full explanation lines (for the log).
    /// </summary>
    public static (string shortReason, List<string> lines) Explain(MediaInfo src, MediaInfo? outp, PresetSettings? preset)
    {
        var lines = new List<string>();
        var reasons = new List<string>();
        var codec = src.VideoCodec.ToLowerInvariant();

        lines.Add("Bron:  " + src.Describe());
        if (outp != null) lines.Add(".mkv:  " + outp.Describe());
        if (preset != null)
            lines.Add($"Preset '{preset.Name}': {preset.VideoEncoder}, " +
                      (preset.QualityType == 2 ? $"constante kwaliteit {preset.Quality:0.#}" : $"gemiddelde bitrate {preset.AvgBitrate} kbit/s") +
                      $", max {preset.MaxWidth}x{preset.MaxHeight}, framerate {preset.Framerate} ({preset.FramerateMode})" +
                      (preset.AudioEncoders.Count > 0 ? $", audio: {string.Join(" + ", preset.AudioEncoders.Select((e, k) => e + (k < preset.AudioBitrates.Count && preset.AudioBitrates[k] > 0 && !e.StartsWith("copy") ? $" {preset.AudioBitrates[k]}k" : "")))}" : ""));

        // 1) Already a modern codec.
        if (codec.Contains("hevc") || codec.Contains("h265") || codec.Contains("av1") || codec.Contains("vp9"))
        {
            reasons.Add($"bron is al {src.VideoCodec.ToUpperInvariant()}");
            lines.Add($"→ De bron is al {src.VideoCodec.ToUpperInvariant()}: even efficiënt (of efficiënter) dan H.265. " +
                      "Opnieuw comprimeren wint niets; je betaalt alleen kwaliteitsverlies en meestal extra bitrate.");
        }

        // 2) Source already squeezed hard.
        double bpp = src.BitsPerPixel;
        if (bpp > 0 && bpp < 0.05)
        {
            reasons.Add($"bron al sterk gecomprimeerd ({MediaInfo.Kbps(src.VideoBitRate)})");
            lines.Add($"→ De bron gebruikt maar {MediaInfo.Kbps(src.VideoBitRate)} voor {src.Width}x{src.Height}@{src.Fps:0.##} " +
                      $"({bpp:0.000} bits/pixel/frame) — dat is al zeer zuinig.");
        }

        // 3) Constant quality doesn't aim at "smaller than the source".
        if (preset?.QualityType == 2)
            lines.Add($"→ Constante kwaliteit ({preset.Quality:0.#}) mikt op een vaste beeldkwaliteit, niet op 'kleiner dan de bron'. " +
                      "Bij een bron die al weinig bits gebruikt, kiest de encoder er dan vaak méér. " +
                      "Bovendien zijn de compressie-artefacten/ruis van de bron extra 'detail' dat opnieuw bits kost.");

        // 4) Hardware encoder efficiency.
        if (preset != null && (preset.VideoEncoder.Contains("nvenc") || preset.VideoEncoder.Contains("qsv") ||
                               preset.VideoEncoder.Contains("vce") || preset.VideoEncoder.Contains("amf") || preset.VideoEncoder.Contains("mf_")))
        {
            reasons.Add("hardware-encoder (NVENC)");
            lines.Add("→ NVENC (videokaart) is veel sneller, maar bij dezelfde kwaliteit doorgaans 20–40% minder efficiënt dan " +
                      "x265 (software). Bij al zuinige bronnen valt het resultaat daardoor groter uit.");
        }

        // 5) Compare actual output with the source.
        if (outp != null)
        {
            if (src.VideoBitRate > 0 && outp.VideoBitRate > src.VideoBitRate * 1.05)
                lines.Add($"→ Video: {MediaInfo.Kbps(src.VideoBitRate)} → {MediaInfo.Kbps(outp.VideoBitRate)} " +
                          $"({(outp.VideoBitRate / src.VideoBitRate - 1) * 100:+0;-0}%).");
            if (outp.AudioBitRate > src.AudioBitRate * 1.2 && outp.AudioBitRate - src.AudioBitRate > 32_000)
            {
                reasons.Add("audio groter");
                lines.Add($"→ Audio: {MediaInfo.Kbps(src.AudioBitRate)} → {MediaInfo.Kbps(outp.AudioBitRate)} " +
                          $"({src.Audio.Count} → {outp.Audio.Count} spoor/sporen) — de preset maakt extra/zwaardere audiosporen.");
            }
            if (outp.Width * outp.Height > src.Width * src.Height)
                lines.Add($"→ Resolutie werd groter ({src.Width}x{src.Height} → {outp.Width}x{outp.Height}).");
            if (outp.Fps > src.Fps + 0.5)
                lines.Add($"→ Framerate werd hoger ({src.Fps:0.##} → {outp.Fps:0.##} fps): extra (dubbele) frames kosten bits.");
            if (Math.Abs((outp.Duration - src.Duration).TotalSeconds) > 2)
                lines.Add($"→ Let op: speelduur verschilt ({Format.Duration(src.Duration)} vs {Format.Duration(outp.Duration)}).");
        }
        else if (preset != null && preset.AudioBitrates.Count > 0 && src.AudioBitRate > 0)
        {
            double presetAudio = preset.AudioBitrates.Where(b => b > 0).Sum() * 1000.0;
            if (presetAudio > src.AudioBitRate * 1.2)
                lines.Add($"→ Audio: bron {MediaInfo.Kbps(src.AudioBitRate)}, preset maakt ±{MediaInfo.Kbps(presetAudio)}.");
        }

        if (reasons.Count == 0)
            reasons.Add("geen duidelijke oorzaak — zie log");
        lines.Add("Tip: zulke bestanden kun je beter zo laten (.nomkv), of omzetten met x265 (software) / een vaste, lagere bitrate.");
        return (string.Join(", ", reasons), lines);
    }

    private static string? Str(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string p)
        => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
