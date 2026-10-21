using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace Lumora.PerfTests.Spikes.Storage;

public enum StorageProfile
{
    Unknown,
    LocalSsd,
    LocalHddOrUsb,
    NetworkShare,
}

public sealed record StorageDetectionResult(
    string Path,
    StorageProfile Profile,
    string Reason,
    bool RootReachable,
    TimeSpan Duration);

/// <summary>
/// Storage-Profile-Detection gemäß ADR-0008. Strategie:
/// 1. UNC oder `DriveType.Network` → NetworkShare (mit Reachability-Check + Timeout)
/// 2. Removable → LocalHddOrUsb
/// 3. Fixed → WMI MSFT_PhysicalDisk.MediaType (4=SSD, 3=HDD)
/// 4. Bei Unsicherheit konservativ als LocalHddOrUsb.
/// </summary>
public static class StorageProfileDetector
{
    private static readonly TimeSpan UncReachabilityTimeout = TimeSpan.FromMilliseconds(2500);

    public static async Task<StorageDetectionResult> DetectAsync(string path, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var (profile, reason, reachable) = await DetectInternalAsync(path, ct).ConfigureAwait(false);
        sw.Stop();
        return new StorageDetectionResult(path, profile, reason, reachable, sw.Elapsed);
    }

    private static async Task<(StorageProfile Profile, string Reason, bool Reachable)> DetectInternalAsync(
        string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (StorageProfile.Unknown, "leerer Pfad", false);
        }

        // UNC-Detection (\\server\share)
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var reachable = await CheckUncReachabilityAsync(path, UncReachabilityTimeout, ct).ConfigureAwait(false);
            return (StorageProfile.NetworkShare,
                    reachable ? "UNC-Prefix, Root erreichbar" : "UNC-Prefix, Root NICHT erreichbar (Offline)",
                    reachable);
        }

        // Lokal: DriveInfo
        DriveInfo drive;
        try
        {
            drive = new DriveInfo(Path.GetPathRoot(path)!);
        }
        catch (Exception ex)
        {
            return (StorageProfile.Unknown, $"Pfad-Root nicht ermittelbar: {ex.GetType().Name}", false);
        }

        switch (drive.DriveType)
        {
            case DriveType.Network:
                return (StorageProfile.NetworkShare, "DriveType.Network", drive.IsReady);
            case DriveType.Removable:
                return (StorageProfile.LocalHddOrUsb, "DriveType.Removable (USB)", drive.IsReady);
            case DriveType.CDRom:
            case DriveType.Ram:
                return (StorageProfile.Unknown, $"DriveType.{drive.DriveType} (kein Spike-Profil)", drive.IsReady);
            case DriveType.NoRootDirectory:
            case DriveType.Unknown:
                return (StorageProfile.LocalHddOrUsb, $"DriveType.{drive.DriveType} → konservativ HDD/USB", drive.IsReady);
            case DriveType.Fixed:
                // WMI für SSD/HDD-Unterscheidung
                var (profile, reason) = ProbeMsftPhysicalDisk(drive.Name);
                return (profile, reason, drive.IsReady);
            default:
                return (StorageProfile.LocalHddOrUsb, $"DriveType.{drive.DriveType} (Fallback)", drive.IsReady);
        }
    }

    private static async Task<bool> CheckUncReachabilityAsync(string uncPath, TimeSpan timeout, CancellationToken outerCt)
    {
        // Directory.Exists ist nicht cancellable. Pattern: Task im Hintergrund laufen lassen,
        // gegen Timeout-Delay racen. Bei Timeout läuft der Worker weiter (best-effort), wir geben false.
        var probeTask = Task.Run(() =>
        {
            try
            {
                return Directory.Exists(uncPath);
            }
            catch
            {
                return false;
            }
        });

        try
        {
            var winner = await Task.WhenAny(probeTask, Task.Delay(timeout, outerCt)).ConfigureAwait(false);
            return winner == probeTask && await probeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static (StorageProfile Profile, string Reason) ProbeMsftPhysicalDisk(string driveName)
    {
        var letter = driveName.Length > 0 ? driveName.Substring(0, 1) : null;
        if (string.IsNullOrEmpty(letter))
        {
            return (StorageProfile.LocalHddOrUsb, "Driveletter nicht ermittelbar → konservativ HDD");
        }

        try
        {
            using var partSearcher = new ManagementObjectSearcher(
                "ASSOCIATORS OF {Win32_LogicalDisk.DeviceID='" + letter + ":'} WHERE AssocClass=Win32_LogicalDiskToPartition");
            foreach (var partition in partSearcher.Get())
            {
                var diskIndexObj = partition["DiskIndex"];
                if (diskIndexObj is null || !int.TryParse(diskIndexObj.ToString(), out var diskNumber))
                {
                    continue;
                }

                // Primary: MSFT_PhysicalDisk (im Storage-WMI-Namespace)
                var primary = QueryMsftPhysicalDisk(diskNumber);
                if (primary.Profile != StorageProfile.LocalHddOrUsb || !primary.Reason.Contains("ohne Treffer"))
                {
                    return primary;
                }

                // Fallback: Win32_DiskDrive.Model-Heuristik
                var fallback = QueryWin32DiskDriveModel(diskNumber);
                return fallback;
            }
        }
        catch (Exception ex)
        {
            return (StorageProfile.LocalHddOrUsb, $"WMI Win32_DiskPartition fehlgeschlagen: {ex.GetType().Name} → konservativ HDD");
        }

        return (StorageProfile.LocalHddOrUsb, "DiskIndex nicht ermittelbar → konservativ HDD");
    }

    private static (StorageProfile Profile, string Reason) QueryWin32DiskDriveModel(int diskNumber)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Model, MediaType, InterfaceType FROM Win32_DiskDrive WHERE Index={diskNumber}");
            foreach (var drive in searcher.Get())
            {
                var model = drive["Model"]?.ToString() ?? string.Empty;
                var iface = drive["InterfaceType"]?.ToString() ?? string.Empty;
                var lower = model.ToLowerInvariant();
                if (lower.Contains("ssd") || lower.Contains("nvme") || lower.Contains("evo") || lower.Contains("samsung 9") || lower.Contains("samsung 8"))
                {
                    return (StorageProfile.LocalSsd, $"Win32_DiskDrive Model-Heuristik (SSD): {model} ({iface})");
                }

                return (StorageProfile.LocalHddOrUsb, $"Win32_DiskDrive Model-Heuristik (HDD/USB): {model} ({iface})");
            }
        }
        catch (Exception ex)
        {
            return (StorageProfile.LocalHddOrUsb, $"Win32_DiskDrive-Fallback fehlgeschlagen: {ex.GetType().Name} → konservativ HDD");
        }

        return (StorageProfile.LocalHddOrUsb, $"Win32_DiskDrive Index={diskNumber} kein Treffer → konservativ HDD");
    }

    private static (StorageProfile Profile, string Reason) QueryMsftPhysicalDisk(int diskNumber)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery($"SELECT MediaType, BusType, FriendlyName FROM MSFT_PhysicalDisk WHERE DeviceId='{diskNumber}'"));
            foreach (var obj in searcher.Get())
            {
                var mediaType = Convert.ToInt32(obj["MediaType"] ?? 0);
                var busType = Convert.ToInt32(obj["BusType"] ?? 0);
                var friendly = obj["FriendlyName"]?.ToString() ?? string.Empty;
                // MediaType: 3=HDD, 4=SSD, 5=SCM, 0=Unknown
                return mediaType switch
                {
                    4 => (StorageProfile.LocalSsd, $"MSFT_PhysicalDisk MediaType=4 (SSD): {friendly}"),
                    5 => (StorageProfile.LocalSsd, $"MSFT_PhysicalDisk MediaType=5 (SCM): {friendly}"),
                    3 => (StorageProfile.LocalHddOrUsb, $"MSFT_PhysicalDisk MediaType=3 (HDD): {friendly}"),
                    _ => (StorageProfile.LocalHddOrUsb, $"MSFT_PhysicalDisk MediaType={mediaType} BusType={busType}: {friendly}"),
                };
            }
        }
        catch (Exception ex)
        {
            return (StorageProfile.LocalHddOrUsb, $"MSFT_PhysicalDisk-Query fehlgeschlagen: {ex.GetType().Name} → konservativ HDD");
        }

        return (StorageProfile.LocalHddOrUsb, "MSFT_PhysicalDisk ohne Treffer → konservativ HDD");
    }
}
