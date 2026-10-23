// Minimal hand-written WASAPI interop: just what Nightverb needs.
// Render to a device, and capture one app's audio via Windows "process loopback"
// (Windows 10 2004+ / build 19041+).
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Nightverb.Audio;

internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey { public Guid FmtId; public uint Pid; }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(uint access, out IntPtr store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

// Marker interface: ActivateAudioInterfaceAsync requires an agile completion handler.
[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject { }

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, IntPtr propVariant);
    [PreserveSig] int SetValue(ref PropertyKey key, IntPtr propVariant);
    [PreserveSig] int Commit();
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort Size;

    public static WaveFormatEx Float(int sampleRate, int channels) => new()
    {
        FormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
        Channels = (ushort)channels,
        SamplesPerSec = (uint)sampleRate,
        BitsPerSample = 32,
        BlockAlign = (ushort)(channels * 4),
        AvgBytesPerSec = (uint)(sampleRate * channels * 4),
        Size = 0,
    };
}

internal static class Wasapi
{
    public const int ShareModeShared = 0;
    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    public const uint BufferFlagsSilent = 0x2;
    public const uint ClsCtxAll = 0x17;

    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);

    public static Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new AudioException(what, hr);
    }

    public static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

    public static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com))
        {
            try { Marshal.ReleaseComObject(com); } catch { }
        }
    }

    /// <summary>Sample rate the device mixes at (what Windows will run our stream at without resampling).</summary>
    public static int GetMixSampleRate(IAudioClient client)
    {
        Check(client.GetMixFormat(out var p), "GetMixFormat");
        try { return (int)Marshal.PtrToStructure<WaveFormatEx>(p).SamplesPerSec; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr pv);

    /// <summary>Human-readable device name, e.g. "Speakers (Realtek(R) Audio)".</summary>
    public static string GetFriendlyName(IMMDevice device)
    {
        IntPtr pv = Marshal.AllocHGlobal(24);
        IPropertyStore? store = null;
        try
        {
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(pv, i, 0);
            if (device.OpenPropertyStore(0 /* STGM_READ */, out var storePtr) < 0) return "?";
            store = (IPropertyStore)Marshal.GetObjectForIUnknown(storePtr);
            Marshal.Release(storePtr);
            var key = new PropertyKey { FmtId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };
            if (store.GetValue(ref key, pv) < 0) return "?";
            if (Marshal.ReadInt16(pv) != 31 /* VT_LPWSTR */) return "?";
            return Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8)) ?? "?";
        }
        catch { return "?"; }
        finally
        {
            PropVariantClear(pv);
            Marshal.FreeHGlobal(pv);
            Release(store);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    private const string ProcessLoopbackPath = "VAD\\Process_Loopback";

    /// <summary>
    /// Get an IAudioClient that hears only the given process and its children
    /// (includeTree=true) or everything except them (includeTree=false).
    /// </summary>
    public static async Task<IAudioClient> ActivateProcessLoopbackAsync(int pid, bool includeTree)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType; TargetProcessId; ProcessLoopbackMode }
        IntPtr acParams = Marshal.AllocHGlobal(12);
        // PROPVARIANT (VT_BLOB), x64 layout: vt @0, BLOB.cbSize @8, BLOB.pBlobData @16
        IntPtr propVar = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(acParams, 0, 1);                      // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            Marshal.WriteInt32(acParams, 4, pid);
            Marshal.WriteInt32(acParams, 8, includeTree ? 0 : 1);    // INCLUDE / EXCLUDE target process tree

            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(propVar, i, 0);
            Marshal.WriteInt16(propVar, 0, 65);                      // VT_BLOB
            Marshal.WriteInt32(propVar, 8, 12);
            Marshal.WriteIntPtr(propVar, 16, acParams);

            var handler = new ActivationHandler();
            var iid = IID_IAudioClient;
            Check(ActivateAudioInterfaceAsync(ProcessLoopbackPath, ref iid, propVar, handler, out var op), "ActivateAudioInterfaceAsync");
            try
            {
                return await handler.Task.ConfigureAwait(false);
            }
            finally
            {
                Release(op);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(propVar);
            Marshal.FreeHGlobal(acParams);
        }
    }

    [ComVisible(true)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IAudioClient> Task => _tcs.Task;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                int hr = operation.GetActivateResult(out int activateHr, out object iface);
                if (hr < 0) _tcs.TrySetException(new AudioException("GetActivateResult", hr));
                else if (activateHr < 0) _tcs.TrySetException(new AudioException("Process loopback activation", activateHr));
                else _tcs.TrySetResult((IAudioClient)iface);
            }
            catch (Exception e)
            {
                _tcs.TrySetException(e);
            }
            return 0;
        }
    }
}

internal sealed class AudioException : Exception
{
    public int HResult32 { get; }
    public AudioException(string what, int hr) : base($"{what} failed (0x{hr:X8})") { HResult32 = hr; HResult = hr; }
}
