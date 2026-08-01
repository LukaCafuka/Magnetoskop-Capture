using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using Magnetoskop.Core.Models;

namespace Magnetoskop.Capture.Video;

/// <summary>
/// Enumerates DirectShow video input devices (names + order). OpenCV only exposes
/// numeric indices; the DirectShow enumeration order matches OpenCV's DSHOW backend,
/// so index i here corresponds to VideoCapture(i, CAP_DSHOW).
/// </summary>
[SupportedOSPlatform("windows")]
public static class DirectShowDeviceEnumerator
{
    private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    private static readonly Guid SystemDeviceEnumClsid = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");

    public static IReadOnlyList<CaptureDeviceInfo> Enumerate()
    {
        var result = new List<CaptureDeviceInfo>();

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
                        result.Add(new CaptureDeviceInfo
                        {
                            Id = index.ToString(),
                            Name = name,
                            IsDefault = index == 0,
                        });
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
    {
        var propertyBagGuid = typeof(IPropertyBag).GUID;
        moniker.BindToStorage(null!, null!, ref propertyBagGuid, out var bagObject);
        if (bagObject is not IPropertyBag bag) return null;
        try
        {
            object? value = null;
            var hr = bag.Read("FriendlyName", ref value, IntPtr.Zero);
            return hr == 0 ? value as string : null;
        }
        finally
        {
            Marshal.ReleaseComObject(bag);
        }
    }

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