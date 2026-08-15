using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Magnetoskop.Core.Models;

namespace Magnetoskop.Capture.Video;

/// <summary>
/// Enumerates DirectShow video input devices and their stable device paths. OpenCV
/// only opens this backend by numeric index, so mapping a path to the current
/// enumeration index is best-effort: DirectShow and OpenCV do not guarantee that
/// their independently obtained orders are identical.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DirectShowDeviceEnumerator
{
    private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    private static readonly Guid SystemDeviceEnumClsid = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");

    public static IReadOnlyList<CaptureDeviceInfo> Enumerate()
        => EnumerateEntries().Select(entry => entry.Device).ToArray();

    /// <summary>
    /// Resolves a persisted DirectShow device path/moniker to the current transient
    /// OpenCV index. Numeric ids remain supported for settings created by older builds.
    /// </summary>
    public static int? ResolveCurrentIndex(string stableId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);

        var entries = EnumerateEntries();
        return ResolveIndex(
            stableId,
            entries.Select(entry => entry.Device).ToArray());
    }

    /// <summary>
    /// Pure stable-id resolver used by the Windows enumerator and deterministic
    /// tests. Friendly names deliberately do not participate because duplicates
    /// are common and USB reorder must be resolved by the persisted path.
    /// </summary>
    internal static int? ResolveIndex(
        string stableId,
        IReadOnlyList<CaptureDeviceInfo> currentEnumeration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        ArgumentNullException.ThrowIfNull(currentEnumeration);

        for (var index = 0; index < currentEnumeration.Count; index++)
        {
            if (string.Equals(
                    currentEnumeration[index].Id,
                    stableId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.TryParse(stableId, out var legacyIndex)
               && legacyIndex >= 0
               && legacyIndex < currentEnumeration.Count
            ? legacyIndex
            : null;
    }

    private static IReadOnlyList<DirectShowDeviceEntry> EnumerateEntries()
    {
        var result = new List<DirectShowDeviceEntry>();

        var enumeratorType = Type.GetTypeFromCLSID(SystemDeviceEnumClsid)
            ?? throw new PlatformNotSupportedException("DirectShow is unavailable.");
        var deviceEnum = (ICreateDevEnum)Activator.CreateInstance(enumeratorType)!;
        try
        {
            var hr = deviceEnum.CreateClassEnumerator(VideoInputDeviceCategory, out var enumMoniker, 0);
            if (hr != 0 || enumMoniker is null)
            {
                return result; // no devices
            }

            try
            {
                var monikers = new IMoniker[1];
                var index = 0;
                while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    var moniker = monikers[0];
                    try
                    {
                        var name = ReadFriendlyName(moniker) ?? $"Video device {index}";
                        var stableId = ReadProperty(moniker, "DevicePath")
                            ?? ReadDisplayName(moniker)
                            ?? $"legacy-index:{index}";
                        result.Add(new DirectShowDeviceEntry(
                            new CaptureDeviceInfo
                            {
                                Id = stableId,
                                Name = name,
                                IsDefault = index == 0,
                            },
                            index));
                        index++;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(moniker);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumMoniker);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(deviceEnum);
        }

        return result;
    }

    private static string? ReadFriendlyName(IMoniker moniker)
        => ReadProperty(moniker, "FriendlyName");

    private static string? ReadProperty(IMoniker moniker, string propertyName)
    {
        object? bagObject = null;
        try
        {
            var propertyBagGuid = typeof(IPropertyBag).GUID;
            moniker.BindToStorage(null!, null!, ref propertyBagGuid, out bagObject);
            if (bagObject is not IPropertyBag bag) return null;

            object? value = null;
            var hr = bag.Read(propertyName, ref value, IntPtr.Zero);
            return hr == 0 && value is string text && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (bagObject is not null && Marshal.IsComObject(bagObject))
            {
                Marshal.ReleaseComObject(bagObject);
            }
        }
    }

    private static string? ReadDisplayName(IMoniker moniker)
    {
        var hr = CreateBindCtx(0, out var bindContext);
        if (hr != 0 || bindContext is null) return null;
        try
        {
            moniker.GetDisplayName(bindContext, null, out var displayName);
            return string.IsNullOrWhiteSpace(displayName) ? null : displayName;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(bindContext);
        }
    }

    private sealed record DirectShowDeviceEntry(CaptureDeviceInfo Device, int Index);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx? bindContext);

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator([In] in Guid type, out IEnumMoniker? enumMoniker, [In] int flags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig]
        int Read([In, MarshalAs(UnmanagedType.LPWStr)] string propertyName,
                 [In, Out, MarshalAs(UnmanagedType.Struct)] ref object? value,
                 IntPtr errorLog);

        [PreserveSig]
        int Write([In, MarshalAs(UnmanagedType.LPWStr)] string propertyName,
                  [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}
