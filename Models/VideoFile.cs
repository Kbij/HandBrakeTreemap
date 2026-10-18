using System;
using System.IO;
using HandBrakeTreemap.ViewModels;

namespace HandBrakeTreemap.Models;

public enum FileStatus
{
    Pending,     // no .mkv yet
    Converting,
    Converted,   // .mkv exists next to the original; original still on disk
    Failed,
    NotWorth     // ".nomkv." in the name: converting saves too little, keep the original
}

public enum VerifyStatus
{
    NotChecked,
    Checking,
    Ok,
    Mismatch,
    Error
}

/// <summary>
/// One non-mkv video file on disk, paired with the .mkv it is (or will be) converted to.
/// </summary>
public sealed class VideoFile : ViewModelBase
{
    private readonly string _rootPath;

    public VideoFile(string fullPath, long sizeBytes, string rootPath, string targetPath)
    {
        _rootPath = rootPath;
        SizeBytes = sizeBytes;
        TargetPath = targetPath;
        Extension = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
        DirectoryPath = Path.GetDirectoryName(fullPath) ?? string.Empty;
        SetPath(fullPath);
    }

    /// <summary>Marker in a file name meaning "tried, not worth converting — keep the original".</summary>
    public const string NoMkvMarker = ".nomkv.";

    public static bool HasNoMkvMarker(string fileName)
        => fileName.Contains(NoMkvMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>"film.mp4" → "film.nomkv.mp4" (same folder).</summary>
    public static string NoMkvPathFor(string fullPath)
        => Path.Combine(Path.GetDirectoryName(fullPath) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(fullPath) + ".nomkv" + Path.GetExtension(fullPath));

    public string FullPath { get; private set; } = string.Empty;
    public string DirectoryPath { get; }
    public string Name { get; private set; } = string.Empty;
    public string RelativePath { get; private set; } = string.Empty;
    public string Extension { get; }

    /// <summary>Update the path after the original was renamed (e.g. to *.nomkv.*).</summary>
    public void SetPath(string fullPath)
    {
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
        var rel = Path.GetRelativePath(_rootPath, fullPath);
        RelativePath = string.IsNullOrEmpty(rel) ? Name : rel;
        Raise(nameof(FullPath), nameof(Name), nameof(RelativePath));
    }

    /// <summary>Size of the original file.</summary>
    public long SizeBytes { get; }

    /// <summary>The .mkv this file converts to (sibling of the original).</summary>
    public string TargetPath { get; }

    /// <summary>Folder in the tree that owns this file.</summary>
    public FolderNode? Folder { get; set; }

    private long? _mkvBytes;
    public long? MkvBytes
    {
        get => _mkvBytes;
        set
        {
            if (SetField(ref _mkvBytes, value))
                Raise(nameof(SavedBytes), nameof(SizeInfo));
        }
    }

    private FileStatus _status = FileStatus.Pending;
    public FileStatus Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
                Raise(nameof(IsPending), nameof(IsConverted), nameof(IsConverting), nameof(IsNotWorth),
                      nameof(SavedBytes), nameof(SizeInfo), nameof(StatusDisplay));
        }
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    private VerifyStatus _verify = VerifyStatus.NotChecked;
    public VerifyStatus Verify
    {
        get => _verify;
        set
        {
            if (SetField(ref _verify, value))
                Raise(nameof(StatusDisplay));
        }
    }

    private string _verifyInfo = string.Empty;
    public string VerifyInfo
    {
        get => _verifyInfo;
        set
        {
            if (SetField(ref _verifyInfo, value))
                Raise(nameof(StatusDisplay));
        }
    }

    private string _error = string.Empty;
    public string Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
                Raise(nameof(StatusDisplay));
        }
    }

    /// <summary>Still to do (never converted, or a previous attempt failed).</summary>
    public bool IsPending => Status is FileStatus.Pending or FileStatus.Failed;
    public bool IsConverted => Status == FileStatus.Converted;
    public bool IsConverting => Status == FileStatus.Converting;
    public bool IsNotWorth => Status == FileStatus.NotWorth;

    /// <summary>Original minus mkv (only meaningful once converted).</summary>
    public long SavedBytes => IsConverted && MkvBytes is long m ? SizeBytes - m : 0;

    public string SizeInfo => IsConverted && MkvBytes is long m
        ? $"{Format.Bytes(SizeBytes)} → {Format.Bytes(m)}   ({Format.Change(SizeBytes, m)})"
        : Format.Bytes(SizeBytes);

    public string StatusDisplay => Status switch
    {
        FileStatus.Pending => "te doen",
        FileStatus.Converting => "bezig…",
        FileStatus.NotWorth => "niet de moeite (.nomkv) — origineel behouden" + (string.IsNullOrEmpty(Error) ? "" : $" • {Error}"),
        FileStatus.Failed => "✗ mislukt" + (string.IsNullOrEmpty(Error) ? "" : $": {Error}"),
        FileStatus.Converted => Verify switch
        {
            VerifyStatus.Checking => "omgezet • controleren…",
            VerifyStatus.Ok => "omgezet • ✓ " + VerifyInfo,
            VerifyStatus.Mismatch => "omgezet • ⚠ " + VerifyInfo,
            VerifyStatus.Error => "omgezet • ? " + VerifyInfo,
            _ => "omgezet • niet gecontroleerd"
        },
        _ => ""
    };
}

public static class Format
{
    public static string Bytes(long bytes)
    {
        string sign = bytes < 0 ? "-" : "";
        double size = Math.Abs((double)bytes);
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{sign}{size:0} B" : $"{sign}{size:0.##} {units[unit]}";
    }

    /// <summary>"−68%" when the new file is smaller, "+5%" when it grew.</summary>
    public static string Change(long original, long converted)
    {
        if (original <= 0) return "";
        double pct = (converted - original) * 100.0 / original;
        return pct <= 0 ? $"−{-pct:0}%" : $"+{pct:0}%";
    }

    public static string Duration(TimeSpan t)
        => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
