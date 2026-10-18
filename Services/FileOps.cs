using System;
using System.Diagnostics;
using System.IO;

namespace HandBrakeTreemap.Services;

/// <summary>Deleting (optionally to the recycle bin) and opening files/folders in the OS shell.</summary>
public static class FileOps
{
    public static bool RecycleBinSupported => OperatingSystem.IsWindows();

    public static void Delete(string path, bool toRecycleBin)
    {
        if (toRecycleBin && OperatingSystem.IsWindows())
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else
        {
            File.Delete(path);
        }
        Log.Info($"Verwijderd{(toRecycleBin && OperatingSystem.IsWindows() ? " (prullenbak)" : "")}: {path}");
    }

    /// <summary>
    /// Open a file in its default app (video player) or a folder in Explorer.
    /// Returns false (and logs why) when nothing could be started.
    /// </summary>
    public static bool Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Log.Warn($"Openen: bestaat niet: {path}");
            return false;
        }

        // 1) Normal shell open (uses the file association).
        try
        {
            Log.Info($"Openen: {path}");
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Openen via bestandskoppeling mislukt ({ex.Message}) — probeer via Verkenner.");
        }

        // 2) Fallback on Windows: let Explorer open it (also follows the association).
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Openen via Verkenner mislukt", ex);
            }
        }
        return false;
    }

    /// <summary>Show a file selected in Explorer (Windows) or open its folder elsewhere.</summary>
    public static void Reveal(string filePath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Log.Info($"Toon in Verkenner: {filePath}");
                // Explorer wants exactly: /select,"C:\path\file.ext"
                using var p = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
                {
                    UseShellExecute = false
                });
            }
            else
            {
                Open(Path.GetDirectoryName(filePath) ?? filePath);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Toon in Verkenner mislukt", ex);
        }
    }
}
