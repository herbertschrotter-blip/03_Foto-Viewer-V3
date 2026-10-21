using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Lumora.Settings.Info;

/// <summary>
/// Namen der Grafikkarten für Einstellungen › Info (ADR-0042) über user32!EnumDisplayDevices — ohne WMI-Paket und
/// ohne die DirectML-Laufzeit zu laden. Die Karte am Hauptbildschirm zuerst, doppelte Namen einmal.
/// </summary>
internal static class GraphicsAdapters
{
    private const int DisplayDeviceAttachedToDesktop = 0x1;
    private const int DisplayDevicePrimaryDevice = 0x4;

    public static IReadOnlyList<string> GetNames()
    {
        var names = new List<string>();
        try
        {
            var device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            for (uint index = 0; EnumDisplayDevices(null, index, ref device, 0); index++)
            {
                var name = device.DeviceString?.Trim();
                if (!string.IsNullOrEmpty(name) && (device.StateFlags & DisplayDeviceAttachedToDesktop) != 0
                    && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    if ((device.StateFlags & DisplayDevicePrimaryDevice) != 0)
                    {
                        names.Insert(0, name);
                    }
                    else
                    {
                        names.Add(name);
                    }
                }

                device = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            }
        }
        catch (Exception)
        {
            // Nur Anzeige — ohne Angabe bleibt „unbekannt“
        }

        return names;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }
}
