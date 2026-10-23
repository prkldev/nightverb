using System;
using System.Runtime.InteropServices;
using System.Threading;
using Nightverb.Util;

namespace Nightverb.Audio;

/// <summary>
/// Real-time loop: hear Firefox (process loopback), run the reverb, and play
/// only the reverb tail on the default output device. Firefox's own (dry)
/// sound keeps playing untouched, so the two mix in your speakers.
/// </summary>
internal sealed class AudioEngine : IDisposable
{
    public enum EngineState { Off, Starting, Running, WaitingForFirefox, Error }

    private volatile ReverbParams _params = ReverbParams.Default;
    private volatile bool _enabled;
    private volatile int _targetPid;
    private volatile bool _restart;
    private volatile bool _disposed;

    private readonly IntPtr _wake = Native.CreateEventW(IntPtr.Zero, false, false, null);
    private readonly Thread _thread;
    private DeviceWatcher? _watcher;
    private IMMDeviceEnumerator? _watchEnumerator;

    private const int StatsIntervalMs = 60_000;

    private Reverb? _reverb;
    private float _inPeak, _outPeak;
    private long _underruns;

    public EngineState State { get; private set; } = EngineState.Off;
    public string? Error { get; private set; }
    public string? DeviceName { get; private set; }
    public int SampleRate { get; private set; }
    public double LatencyMs { get; private set; }
    public long Underruns => Interlocked.Read(ref _underruns);
    public int TargetPid => _targetPid;
    public bool Enabled => _enabled;
    public ReverbParams Params => _params;

    /// <summary>Raised (on the audio thread) when State/Error/Device change.</summary>
    public event Action? StatusChanged;

    public AudioEngine()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Nightverb audio", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
    }

    public void Start()
    {
        try
        {
            _watchEnumerator = Wasapi.CreateEnumerator();
            _watcher = new DeviceWatcher(this);
            _watchEnumerator.RegisterEndpointNotificationCallback(_watcher);
        }
        catch (Exception e)
        {
            Log.Write("device watcher failed: " + e.Message);
        }
        _thread.Start();
    }

    public void Configure(bool enabled, ReverbParams p)
    {
        _params = p;
        if (_enabled != enabled)
        {
            _enabled = enabled;
            Native.SetEvent(_wake);
        }
    }

    public void SetTarget(int pid)
    {
        if (pid == _targetPid) return;
        Log.Write($"target Firefox pid {_targetPid} -> {pid}");
        _targetPid = pid;
        RequestRestart();
    }

    public void RequestRestart()
    {
        _restart = true;
        Native.SetEvent(_wake);
    }

    /// <summary>Peak levels since the last call (0..1+).</summary>
    public (float input, float output) TakePeaks()
    {
        float i = Interlocked.Exchange(ref _inPeak, 0f);
        float o = Interlocked.Exchange(ref _outPeak, 0f);
        return (i, o);
    }

    private void SetState(EngineState s, string? error = null)
    {
        if (State == s && Error == error) return;
        State = s;
        Error = error;
        Log.Write($"state {s}{(error != null ? ": " + error : "")}");
        try { StatusChanged?.Invoke(); } catch { }
    }

    private void WaitWake(uint ms) => Native.WaitForMultipleObjects(1, new[] { _wake }, false, ms);

    private void Run()
    {
        uint taskIndex = 0;
        IntPtr mmcss = Native.AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        try
        {
            while (!_disposed)
            {
                if (!_enabled) { SetState(EngineState.Off); WaitWake(1000); continue; }
                if (_targetPid == 0) { SetState(EngineState.WaitingForFirefox); WaitWake(1000); continue; }

                _restart = false;
                try
                {
                    RunSession();
                }
                catch (AudioException e)
                {
                    Log.Write("audio session ended: " + e.Message);
                    if (!IsProcessAlive(_targetPid)) SetState(EngineState.WaitingForFirefox);
                    else SetState(EngineState.Error, DescribeError(e));
                    WaitWake(1500);
                }
                catch (Exception e)
                {
                    Log.Write("audio session crashed: " + e);
                    SetState(EngineState.Error, e.Message);
                    WaitWake(2000);
                }
            }
        }
        finally
        {
            if (mmcss != IntPtr.Zero) Native.AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    // Known problems go to the extension as codes, which it shows in the browser's language.
    private static string DescribeError(AudioException e) => e.HResult32 switch
    {
        unchecked((int)0x88890004) => "device-lost",
        unchecked((int)0x80070005) => "access-denied",
        unchecked((int)0x80070057) => "firefox-not-found",
        _ => e.Message,
    };

    private static bool IsProcessAlive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private void RunSession()
    {
        SetState(EngineState.Starting);
        int pid = _targetPid;

        IMMDeviceEnumerator? en = null;
        IMMDevice? device = null;
        IAudioClient? render = null, capture = null;
        IAudioRenderClient? renderClient = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr renderEvent = Native.CreateEventW(IntPtr.Zero, false, false, null);
        IntPtr captureEvent = Native.CreateEventW(IntPtr.Zero, false, false, null);
        IntPtr fmtPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        bool renderStarted = false, captureStarted = false;

        try
        {
            // ---- output: default device, its native mix rate, stereo float (Windows up/down-mixes) ----
            en = Wasapi.CreateEnumerator();
            Wasapi.Check(en.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device), "Default output device");
            DeviceName = Wasapi.GetFriendlyName(device);
            var iid = Wasapi.IID_IAudioClient;
            Wasapi.Check(device.Activate(ref iid, Wasapi.ClsCtxAll, IntPtr.Zero, out var renderObj), "Activate output");
            render = (IAudioClient)renderObj;
            int sr = Wasapi.GetMixSampleRate(render);
            SampleRate = sr;

            Marshal.StructureToPtr(WaveFormatEx.Float(sr, 2), fmtPtr, false);
            const uint convert = Wasapi.StreamFlagsAutoConvertPcm | Wasapi.StreamFlagsSrcDefaultQuality;
            Wasapi.Check(render.Initialize(Wasapi.ShareModeShared, Wasapi.StreamFlagsEventCallback | convert,
                100_000 /* 10 ms */, 0, fmtPtr, IntPtr.Zero), "Init output");
            Wasapi.Check(render.SetEventHandle(renderEvent), "Output event");
            Wasapi.Check(render.GetBufferSize(out uint renderFrames), "Output buffer size");
            var riid = Wasapi.IID_IAudioRenderClient;
            Wasapi.Check(render.GetService(ref riid, out var rcObj), "Output service");
            renderClient = (IAudioRenderClient)rcObj;

            // ---- input: everything Firefox (and its child processes) plays ----
            capture = Wasapi.ActivateProcessLoopbackAsync(pid, includeTree: true).GetAwaiter().GetResult();
            Wasapi.Check(capture.Initialize(Wasapi.ShareModeShared,
                Wasapi.StreamFlagsLoopback | Wasapi.StreamFlagsEventCallback | convert,
                200_000 /* 20 ms */, 0, fmtPtr, IntPtr.Zero), "Init Firefox capture");
            Wasapi.Check(capture.SetEventHandle(captureEvent), "Capture event");
            var ciid = Wasapi.IID_IAudioCaptureClient;
            Wasapi.Check(capture.GetService(ref ciid, out var ccObj), "Capture service");
            captureClient = (IAudioCaptureClient)ccObj;

            // ---- DSP ----
            if (_reverb == null || SampleRate != sr) _reverb = new Reverb(sr);
            else _reverb.Reset();
            var reverb = _reverb;

            int targetFill = (int)(sr * 0.012);            // jitter cushion between capture and output
            int maxBacklog = targetFill + (int)(sr * 0.05); // drift guard
            LatencyMs = 1000.0 * (renderFrames + targetFill) / sr + 10.0;
            reverb.LatencyCompensationMs = LatencyMs;
            var applied = _params;
            reverb.SetParams(applied);

            var ring = new StereoRing(sr);
            var inBuf = new float[renderFrames * 2];
            var outBuf = new float[renderFrames * 2];
            var capBuf = new float[sr]; // 0.5 s stereo, more than any packet

            // Pre-fill output with silence so the first period doesn't glitch.
            Wasapi.Check(renderClient.GetBuffer(renderFrames, out _), "Prefill");
            renderClient.ReleaseBuffer(renderFrames, Wasapi.BufferFlagsSilent);

            Wasapi.Check(capture.Start(), "Start capture"); captureStarted = true;
            Wasapi.Check(render.Start(), "Start output"); renderStarted = true;
            Log.Write($"session up: pid={pid} device=\"{DeviceName}\" sr={sr} buf={renderFrames} latency~{LatencyMs:F0}ms");
            SetState(EngineState.Running);

            bool primed = false;
            int overfullBlocks = 0;
            float lastWetPeak = 1f;
            float fade = 0f;
            float fadeStep = 1f / (0.25f * sr);
            var handles = new[] { captureEvent, renderEvent, _wake };

            long statWakes = 0, statBlocks = 0, statSkipped = 0;
            var statClock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                Native.WaitForMultipleObjects(3, handles, false, 200);
                statWakes++;
                if (statClock.ElapsedMilliseconds >= StatsIntervalMs)
                {
                    Log.Write($"stats: {statWakes} wakes, {statBlocks} blocks, {statSkipped} idle, ring {ring.Count}, underruns {Underruns}, lastWet {lastWetPeak:E1}");
                    statWakes = statBlocks = statSkipped = 0;
                    statClock.Restart();
                }
                if (_disposed || _restart) break;
                if (!_enabled && fade <= 0f) break;

                if (!ReferenceEquals(applied, _params))
                {
                    applied = _params;
                    reverb.SetParams(applied);
                }

                // ---- capture: move every available packet into the ring ----
                while (true)
                {
                    int hr = captureClient.GetNextPacketSize(out uint packet);
                    Wasapi.Check(hr, "Capture packet");
                    if (packet == 0) break;
                    Wasapi.Check(captureClient.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _), "Capture read");
                    int count = (int)frames * 2;
                    if (count > capBuf.Length) capBuf = new float[count];
                    if ((flags & Wasapi.BufferFlagsSilent) != 0 || data == IntPtr.Zero)
                        Array.Clear(capBuf, 0, count);
                    else
                        Marshal.Copy(data, capBuf, 0, count);
                    captureClient.ReleaseBuffer(frames);

                    float peak = 0;
                    for (int i = 0; i < count; i++) { float a = Math.Abs(capBuf[i]); if (a > peak) peak = a; }
                    if (peak > _inPeak) _inPeak = peak;

                    ring.Write(capBuf, (int)frames);
                }

                // ---- output: fill whatever space the device has ----
                Wasapi.Check(render.GetCurrentPadding(out uint padding), "Output padding");
                int need = (int)(renderFrames - padding);
                if (need <= 0) continue;

                int fill = ring.Count;
                if (!primed && fill >= targetFill + need) primed = true;
                int take = 0;
                if (primed)
                {
                    take = Math.Min(fill, need);
                    if (take < need) { primed = false; Interlocked.Increment(ref _underruns); }
                }
                ring.Read(inBuf, take);
                if (take < need) Array.Clear(inBuf, take * 2, (need - take) * 2);
                // Keep the cushion small: if more than needed has piled up for half a
                // second (start-up burst or clock drift), drop the excess once.
                if (ring.Count > maxBacklog) ring.Skip(ring.Count - targetFill);
                else if (ring.Count > targetFill * 2)
                {
                    if (++overfullBlocks > 50) { ring.Skip(ring.Count - targetFill); overfullBlocks = 0; }
                }
                else overfullBlocks = 0;

                // Nothing playing and the tail has died out: skip the DSP entirely.
                float blockIn = 0;
                for (int i = 0; i < need * 2; i++) { float a = Math.Abs(inBuf[i]); if (a > blockIn) blockIn = a; }
                statBlocks++;
                if (blockIn < 1e-6f && lastWetPeak < 1e-6f)
                {
                    Array.Clear(outBuf, 0, need * 2);
                    statSkipped++;
                }
                else
                {
                    reverb.Process(inBuf, outBuf, need);
                    lastWetPeak = 0;
                    for (int i = 0; i < need * 2; i++) { float a = Math.Abs(outBuf[i]); if (a > lastWetPeak) lastWetPeak = a; }
                }

                // fade in on enable, fade out (letting the tail ring) on disable
                float outPeak = 0;
                float fadeTarget = _enabled ? 1f : 0f;
                for (int i = 0; i < need; i++)
                {
                    if (fade < fadeTarget) fade = Math.Min(fadeTarget, fade + fadeStep);
                    else if (fade > fadeTarget) fade = Math.Max(fadeTarget, fade - fadeStep);
                    float l = outBuf[2 * i] * fade, r = outBuf[2 * i + 1] * fade;
                    outBuf[2 * i] = l; outBuf[2 * i + 1] = r;
                    float a = Math.Max(Math.Abs(l), Math.Abs(r));
                    if (a > outPeak) outPeak = a;
                }
                if (outPeak > _outPeak) _outPeak = outPeak;

                Wasapi.Check(renderClient.GetBuffer((uint)need, out IntPtr dst), "Output write");
                Marshal.Copy(outBuf, 0, dst, need * 2);
                renderClient.ReleaseBuffer((uint)need, 0);
            }
        }
        finally
        {
            if (captureStarted) try { capture!.Stop(); } catch { }
            if (renderStarted) try { render!.Stop(); } catch { }
            Wasapi.Release(captureClient);
            Wasapi.Release(renderClient);
            Wasapi.Release(capture);
            Wasapi.Release(render);
            Wasapi.Release(device);
            Wasapi.Release(en);
            Native.CloseHandle(renderEvent);
            Native.CloseHandle(captureEvent);
            Marshal.FreeHGlobal(fmtPtr);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Native.SetEvent(_wake);
        _thread.Join(2000);
        if (_watchEnumerator != null && _watcher != null)
        {
            try { _watchEnumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { }
        }
        Wasapi.Release(_watchEnumerator);
    }

    /// <summary>Restarts the session when the default output device changes.</summary>
    [ComVisible(true)]
    private sealed class DeviceWatcher : IMMNotificationClient
    {
        private readonly AudioEngine _engine;
        public DeviceWatcher(AudioEngine engine) => _engine = engine;

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id)
        {
            if (flow == EDataFlow.Render && role == ERole.Console)
            {
                Log.Write("default output device changed");
                _engine.RequestRestart();
            }
            return 0;
        }

        public int OnDeviceStateChanged(string id, uint state) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }

    /// <summary>Single-threaded stereo FIFO (used only on the audio thread).</summary>
    private sealed class StereoRing
    {
        private readonly float[] _buf;
        private readonly int _frames;
        private int _read, _count;

        public StereoRing(int capacityFrames) { _frames = capacityFrames; _buf = new float[capacityFrames * 2]; }

        public int Count => _count;

        public void Write(float[] src, int frames)
        {
            if (frames > _frames) { Skip(_count); frames = _frames; }
            if (_count + frames > _frames) Skip(_count + frames - _frames); // overflow: drop oldest
            int w = (_read + _count) % _frames;
            for (int i = 0; i < frames; i++)
            {
                _buf[2 * w] = src[2 * i];
                _buf[2 * w + 1] = src[2 * i + 1];
                if (++w == _frames) w = 0;
            }
            _count += frames;
        }

        public void Read(float[] dst, int frames)
        {
            frames = Math.Min(frames, _count);
            int r = _read;
            for (int i = 0; i < frames; i++)
            {
                dst[2 * i] = _buf[2 * r];
                dst[2 * i + 1] = _buf[2 * r + 1];
                if (++r == _frames) r = 0;
            }
            _read = r;
            _count -= frames;
        }

        public void Skip(int frames)
        {
            frames = Math.Min(frames, _count);
            _read = (_read + frames) % _frames;
            _count -= frames;
        }
    }
}
