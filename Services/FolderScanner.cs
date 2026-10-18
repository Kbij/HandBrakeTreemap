using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HandBrakeTreemap.Models;

namespace HandBrakeTreemap.Services;

public sealed class ScanResult
{
    public required FolderNode Root { get; init; }
    /// <summary>Left-over temporary files from an interrupted conversion (*.hbtmp.mkv).</summary>
    public List<string> TempFiles { get; init; } = new();
}

public static class FolderScanner
{
    /// <summary>Extensions treated as "video".</summary>
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkv", "mp4", "m4v", "avi", "mov", "wmv", "flv", "webm",
        "mpg", "mpeg", "m2ts", "mts", "ts", "vob", "divx", "ogv",
        "3gp", "rm", "rmvb", "asf", "f4v"
    };

    /// <summary>Suffix used while HandBrake is still writing; renamed to .mkv on success.</summary>
    public const string TempSuffix = ".hbtmp.mkv";

    /// <summary>
    /// The .mkv a given original converts to. Normally "movie.avi" → "movie.mkv"; if another
    /// video in the same folder shares the base name, use "movie.avi.mkv" to avoid a clash.
    /// </summary>
    public static string TargetFor(string fullPath, bool baseNameClash)
        => baseNameClash ? fullPath + ".mkv" : Path.ChangeExtension(fullPath, ".mkv");

    public static string TempFor(string targetPath)
        => Path.Combine(Path.GetDirectoryName(targetPath) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(targetPath) + TempSuffix);

    /// <summary>
    /// Walk <paramref name="root"/> recursively and build the folder tree. Only folders that
    /// (somewhere below them) contain non-mkv videos are kept. Unreadable folders are skipped.
    /// </summary>
    public static ScanResult Scan(string root, CancellationToken token)
    {
        root = Path.GetFullPath(root);
        var temp = new List<string>();
        var name = new DirectoryInfo(root).Name;
        var rootNode = new FolderNode(root, string.IsNullOrEmpty(name) ? root : name, null);

        Build(rootNode, root, temp, token);
        rootNode.SortRecursive();
        rootNode.IsExpanded = true;
        return new ScanResult { Root = rootNode, TempFiles = temp };
    }

    private static void Build(FolderNode node, string root, List<string> temp, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dir = new DirectoryInfo(node.FullPath);

        List<FileInfo> files;
        try
        {
            files = dir.EnumerateFiles().ToList();
        }
        catch (Exception)
        {
            files = new List<FileInfo>(); // no permission / gone
        }

        // Existing .mkv files in this folder (name → size).
        var mkvs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var originals = new List<FileInfo>();
        foreach (var f in files)
        {
            if (f.Name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
            {
                temp.Add(f.FullName);
                continue;
            }
            var ext = f.Extension.TrimStart('.');
            if (!VideoExtensions.Contains(ext))
                continue;
            if (ext.Equals("mkv", StringComparison.OrdinalIgnoreCase))
                mkvs[f.Name] = f.Length;
            else
                originals.Add(f);
        }

        // "*.nomkv.*" = already tried, not worth converting: keep as-is, never pair or queue.
        foreach (var f in originals.Where(f => VideoFile.HasNoMkvMarker(f.Name)).ToList())
        {
            originals.Remove(f);
            node.Files.Add(new VideoFile(f.FullName, f.Length, root, f.FullName) { Folder = node, Status = FileStatus.NotWorth });
        }

        var baseCounts = originals
            .GroupBy(f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var f in originals.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            bool clash = baseCounts[Path.GetFileNameWithoutExtension(f.Name)] > 1;
            var target = TargetFor(f.FullName, clash);
            var vf = new VideoFile(f.FullName, f.Length, root, target) { Folder = node };

            if (mkvs.TryGetValue(Path.GetFileName(target), out var mkvSize))
            {
                vf.MkvBytes = mkvSize;
                vf.Status = FileStatus.Converted;
            }
            node.Files.Add(vf);
        }

        // Sub-folders.
        IEnumerable<DirectoryInfo> subs;
        try
        {
            subs = dir.EnumerateDirectories()
                      .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0) // no junction loops
                      .ToList();
        }
        catch (Exception)
        {
            subs = Array.Empty<DirectoryInfo>();
        }

        foreach (var sub in subs)
        {
            var child = new FolderNode(sub.FullName, sub.Name, node);
            Build(child, root, temp, token);
            if (!child.IsEmpty)
                node.Children.Add(child);
        }

        node.RecalculateSelf();
    }
}
