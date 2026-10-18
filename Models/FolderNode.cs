using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HandBrakeTreemap.ViewModels;

namespace HandBrakeTreemap.Models;

/// <summary>
/// One folder in the WinDirStat-style tree. Holds its own video files plus sub-folders,
/// and aggregates (recursively) what's still to convert and what's already been saved.
/// </summary>
public sealed class FolderNode : ViewModelBase
{
    public FolderNode(string fullPath, string name, FolderNode? parent)
    {
        FullPath = fullPath;
        Name = name;
        Parent = parent;
    }

    public string FullPath { get; }
    public string Name { get; }
    public FolderNode? Parent { get; }

    public ObservableCollection<FolderNode> Children { get; } = new();
    public List<VideoFile> Files { get; } = new();

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    // --- Aggregates (recursive) ---------------------------------------------------------
    public long PendingBytes { get; private set; }
    public int PendingCount { get; private set; }
    public int ConvertedCount { get; private set; }
    public long ConvertedOriginalBytes { get; private set; }
    public long ConvertedMkvBytes { get; private set; }
    public int FailedCount { get; private set; }
    public int NotWorthCount { get; private set; }
    public long NotWorthBytes { get; private set; }

    public long SavedBytes => ConvertedOriginalBytes - ConvertedMkvBytes;

    /// <summary>Everything in this folder is converted.</summary>
    public bool IsDone => PendingCount == 0 && (ConvertedCount > 0 || NotWorthCount > 0);

    /// <summary>Share (0..100) of the parent's still-to-convert bytes, for the bar.</summary>
    public double PendingFraction =>
        Parent == null
            ? (PendingBytes > 0 ? 100 : 0)
            : Parent.PendingBytes <= 0 ? 0 : PendingBytes * 100.0 / Parent.PendingBytes;

    public string PendingDisplay => PendingBytes > 0 ? Format.Bytes(PendingBytes) : "—";
    public string CountDisplay => PendingCount > 0 ? PendingCount.ToString() : "";
    public string SavedDisplay => ConvertedCount > 0 ? Format.Bytes(SavedBytes) : "";

    public bool IsEmpty => Files.Count == 0 && Children.Count == 0;

    public IEnumerable<VideoFile> AllFiles()
    {
        foreach (var f in Files)
            yield return f;
        foreach (var c in Children)
            foreach (var f in c.AllFiles())
                yield return f;
    }

    /// <summary>Recompute this folder from its own files and its children's (already current) totals.</summary>
    public void RecalculateSelf()
    {
        long pBytes = 0, cOrig = 0, cMkv = 0;
        int pCount = 0, cCount = 0, failed = 0, nwCount = 0;
        long nwBytes = 0;

        foreach (var f in Files)
        {
            if (f.IsPending || f.IsConverting)
            {
                pBytes += f.SizeBytes;
                pCount++;
                if (f.Status == FileStatus.Failed) failed++;
            }
            else if (f.IsNotWorth)
            {
                nwCount++;
                nwBytes += f.SizeBytes;
            }
            else if (f.IsConverted)
            {
                cCount++;
                cOrig += f.SizeBytes;
                cMkv += f.MkvBytes ?? 0;
            }
        }

        foreach (var c in Children)
        {
            pBytes += c.PendingBytes;
            pCount += c.PendingCount;
            failed += c.FailedCount;
            cCount += c.ConvertedCount;
            cOrig += c.ConvertedOriginalBytes;
            cMkv += c.ConvertedMkvBytes;
            nwCount += c.NotWorthCount;
            nwBytes += c.NotWorthBytes;
        }

        PendingBytes = pBytes;
        PendingCount = pCount;
        FailedCount = failed;
        ConvertedCount = cCount;
        ConvertedOriginalBytes = cOrig;
        ConvertedMkvBytes = cMkv;
        NotWorthCount = nwCount;
        NotWorthBytes = nwBytes;

        Raise(nameof(PendingBytes), nameof(PendingCount), nameof(FailedCount),
              nameof(ConvertedCount), nameof(ConvertedOriginalBytes), nameof(ConvertedMkvBytes),
              nameof(NotWorthCount), nameof(NotWorthBytes),
              nameof(SavedBytes), nameof(IsDone), nameof(PendingFraction),
              nameof(PendingDisplay), nameof(CountDisplay), nameof(SavedDisplay));

        // Siblings' share of this folder changed too.
        foreach (var c in Children)
            c.Raise(nameof(PendingFraction));
    }

    /// <summary>Recompute this folder and every ancestor up to the root.</summary>
    public void RecalculateUpwards()
    {
        for (var n = this; n != null; n = n.Parent)
            n.RecalculateSelf();
    }

    /// <summary>Remove empty sub-folders (after originals were deleted).</summary>
    public void PruneEmpty()
    {
        foreach (var c in Children.ToList())
        {
            c.PruneEmpty();
            if (c.IsEmpty)
                Children.Remove(c);
        }
    }

    public FolderNode? Find(string fullPath)
    {
        if (string.Equals(FullPath, fullPath, System.StringComparison.OrdinalIgnoreCase))
            return this;
        foreach (var c in Children)
        {
            var hit = c.Find(fullPath);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>Sort sub-folders: most still-to-convert first, then most saved, then by name.</summary>
    public void SortRecursive()
    {
        var sorted = Children
            .OrderByDescending(c => c.PendingBytes)
            .ThenByDescending(c => c.ConvertedOriginalBytes)
            .ThenBy(c => c.Name, System.StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Children.Clear();
        foreach (var c in sorted)
        {
            c.SortRecursive();
            Children.Add(c);
        }
    }
}
