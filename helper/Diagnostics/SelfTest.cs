using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Nightverb.Audio;
using Nightverb.Util;

namespace Nightverb.Diagnostics;

/// <summary>Offline checks of the reverb (no audio devices needed) and a live Firefox capture probe.</summary>
internal static class SelfTest
{
    private const int Sr = 48000;

    public static int Run(string? wavDir)
    {
        bool ok = true;
        var neutral = ReverbParams.Default with { PreDelay = 0, LowCut = 20, HighCut = 20000, Damping = 20000, Wet = 50 };

        Console.WriteLine("== RT60 (requested vs measured, broadband, no damping)");
        foreach (double decay in new[] { 0.8, 2.0, 4.5, 10.0 })
        {
            var rv = new Reverb(Sr);
            rv.SetParams(neutral with { Decay = decay });
            int frames = (int)(Sr * (decay * 1.6 + 1));
            var input = new float[frames * 2];
            input[0] = 1; input[1] = 1;
            var output = new float[frames * 2];
            rv.Process(input, output, frames);
            double rt = MeasureRt60(output, frames);
            bool finite = AllFinite(output);
            ok &= finite && Math.Abs(rt - decay) / decay < 0.25;
            Console.WriteLine($"  decay {decay,5:F1}s -> measured {rt,5:F2}s {(finite ? "" : " NaN!")}");
        }

        Console.WriteLine("== Wet level vs input (pink noise, Wet 50%), dB");
        foreach (double size in new[] { 50.0, 130.0, 200.0 })
        {
            Console.Write($"  size {size,3}%:");
            foreach (double decay in new[] { 1.0, 4.5, 12.0 })
            {
                var rv = new Reverb(Sr);
                rv.SetParams(ReverbParams.Default with { Size = size, Decay = decay, Wet = 50 });
                var (inRms, outRms, peak) = NoiseLevels(rv, 8);
                double db = 20 * Math.Log10(outRms / inRms);
                Console.Write($"  decay {decay,4:F1}s {db,6:F1} dB (peak {peak:F2})");
            }
            Console.WriteLine();
        }

        Console.WriteLine("== Stress: max wet, max decay, max size, loud input");
        {
            var rv = new Reverb(Sr);
            rv.SetParams(ReverbParams.Default with { Wet = 100, Decay = 30, Size = 200, Modulation = 100, Diffusion = 100, Damping = 20000, HighCut = 20000, LowCut = 20 });
            var (_, outRms, peak) = NoiseLevels(rv, 20, amp: 0.9f);
            bool bounded = peak <= 1.0f && double.IsFinite(outRms);
            ok &= bounded;
            Console.WriteLine($"  out rms {outRms:F3}, peak {peak:F3} {(bounded ? "(bounded ok)" : "UNBOUNDED!")}");
        }

        Console.WriteLine("== Silence after signal decays to nothing (no denormal / DC build-up)");
        {
            var rv = new Reverb(Sr);
            rv.SetParams(ReverbParams.Default with { Decay = 2 });
            NoiseLevels(rv, 2);
            int frames = Sr * 30;
            var zeros = new float[frames * 2];
            var output = new float[frames * 2];
            var sw = Stopwatch.StartNew();
            rv.Process(zeros, output, frames);
            sw.Stop();
            double tail = 0;
            for (int i = (frames - Sr) * 2; i < frames * 2; i++) tail = Math.Max(tail, Math.Abs(output[i]));
            ok &= tail < 1e-5;
            Console.WriteLine($"  residual after 30 s: {tail:E1}, 30 s of silence processed in {sw.ElapsedMilliseconds} ms");
        }

        Console.WriteLine("== CPU");
        {
            var rv = new Reverb(Sr);
            rv.SetParams(ReverbParams.Default);
            int block = 480, seconds = 60;
            var inp = new float[block * 2];
            var outp = new float[block * 2];
            var rnd = new Random(1);
            for (int i = 0; i < inp.Length; i++) inp[i] = (float)(rnd.NextDouble() - 0.5) * 0.5f;
            var sw = Stopwatch.StartNew();
            for (int b = 0; b < seconds * Sr / block; b++) rv.Process(inp, outp, block);
            sw.Stop();
            double load = sw.Elapsed.TotalSeconds / seconds * 100;
            Console.WriteLine($"  60 s of stereo audio in {sw.ElapsedMilliseconds} ms  ->  ~{load:F2}% of one CPU core");
        }

        if (wavDir != null) WriteDemo(wavDir);

        Console.WriteLine(ok ? "SELFTEST OK" : "SELFTEST FAILED");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// White-noise wet level (dB vs input) for each extension preset, to calibrate the
    /// browser's built-in reverb against this engine (tools/calibrate-builtin.mjs).
    /// </summary>
    public static int LevelTable()
    {
        var presets = new (string id, ReverbParams p)[]
        {
            ("slowed", new() { Wet = 45, Size = 130, Decay = 4.5, PreDelay = 20, Diffusion = 75, LowCut = 180, HighCut = 6500, Damping = 5000, Modulation = 50, Width = 100 }),
            ("vapor", new() { Wet = 55, Size = 150, Decay = 6, PreDelay = 10, Diffusion = 80, LowCut = 300, HighCut = 4000, Damping = 3000, Modulation = 100, Width = 120 }),
            ("dream", new() { Wet = 60, Size = 200, Decay = 15, PreDelay = 60, Diffusion = 90, LowCut = 250, HighCut = 5500, Damping = 4000, Modulation = 80, Width = 130 }),
            ("cathedral", new() { Wet = 40, Size = 185, Decay = 8, PreDelay = 45, Diffusion = 85, LowCut = 150, HighCut = 7500, Damping = 5500, Modulation = 40, Width = 110 }),
            ("hall", new() { Wet = 35, Size = 100, Decay = 2.6, PreDelay = 25, Diffusion = 70, LowCut = 120, HighCut = 9000, Damping = 7000, Modulation = 30, Width = 100 }),
            ("lofi", new() { Wet = 35, Size = 60, Decay = 1.4, PreDelay = 5, Diffusion = 70, LowCut = 300, HighCut = 3500, Damping = 2500, Modulation = 60, Width = 70 }),
            ("room", new() { Wet = 30, Size = 45, Decay = 0.9, PreDelay = 8, Diffusion = 60, LowCut = 200, HighCut = 7000, Damping = 5000, Modulation = 20, Width = 80 }),
        };
        foreach (var (id, p) in presets)
        {
            var rv = new Reverb(Sr);
            rv.SetParams(p);
            int frames = (int)(Sr * Math.Max(8, p.Decay * 2.5));
            var input = new float[frames * 2];
            var rnd = new Random(7);
            // same noise in both channels: music is mostly centred, and the browser
            // reverb takes a mono sum, so this compares like with like
            for (int i = 0; i < frames; i++) input[2 * i] = input[2 * i + 1] = (float)((rnd.NextDouble() * 2 - 1) * 0.1);
            var output = new float[frames * 2];
            rv.Process(input, output, frames);
            int from = frames - Sr * 3; // steady state: last 3 s
            double si = 0, so = 0;
            for (int i = from * 2; i < frames * 2; i++) { si += input[i] * input[i]; so += output[i] * output[i]; }
            Console.WriteLine($"{id} {20 * Math.Log10(Math.Sqrt(so / si)):F2}");
        }
        return 0;
    }

    private static (double inRms, double outRms, float peak) NoiseLevels(Reverb rv, int seconds, float amp = 0.25f)
    {
        int frames = Sr * seconds;
        var input = new float[frames * 2];
        var pinkL = new Pink(1); var pinkR = new Pink(2);
        for (int i = 0; i < frames; i++) { input[2 * i] = pinkL.Next() * amp; input[2 * i + 1] = pinkR.Next() * amp; }
        var output = new float[frames * 2];
        rv.Process(input, output, frames);
        int from = frames / 2; // steady state only
        double si = 0, so = 0; float peak = 0;
        for (int i = from * 2; i < frames * 2; i++)
        {
            si += input[i] * input[i];
            so += output[i] * output[i];
            peak = Math.Max(peak, Math.Abs(output[i]));
        }
        int n = (frames - from) * 2;
        return (Math.Sqrt(si / n), Math.Sqrt(so / n), peak);
    }

    private static double MeasureRt60(float[] stereo, int frames)
    {
        // Schroeder backward integration of the energy, fit between -5 and -35 dB.
        var edc = new double[frames];
        double acc = 0;
        for (int i = frames - 1; i >= 0; i--)
        {
            double m = 0.5 * (stereo[2 * i] + stereo[2 * i + 1]);
            acc += m * m;
            edc[i] = acc;
        }
        double total = edc[0];
        int t5 = -1, t35 = -1;
        for (int i = 0; i < frames; i++)
        {
            double db = 10 * Math.Log10(edc[i] / total + 1e-30);
            if (t5 < 0 && db <= -5) t5 = i;
            if (t35 < 0 && db <= -35) { t35 = i; break; }
        }
        if (t5 < 0 || t35 < 0) return double.NaN;
        return 2.0 * (t35 - t5) / Sr;
    }

    private static bool AllFinite(float[] a)
    {
        foreach (var v in a) if (!float.IsFinite(v)) return false;
        return true;
    }

    /// <summary>Writes a short synthetic chord loop dry, and dry + "Slowed + Reverb" wet, to compare by ear.</summary>
    private static void WriteDemo(string dir)
    {
        Directory.CreateDirectory(dir);
        int seconds = 14, frames = Sr * seconds;
        var dry = new float[frames * 2];
        double[][] chords = { new[] { 220.0, 277.18, 329.63 }, new[] { 196.0, 246.94, 293.66 }, new[] { 174.61, 220.0, 261.63 }, new[] { 196.0, 246.94, 311.13 } };
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / Sr;
            int bar = (int)(t / 1.5) % 4;
            double tb = t % 1.5;
            double env = Math.Exp(-tb * 2.2) * Math.Min(1, tb * 200);
            double s = 0;
            foreach (var f in chords[bar])
                for (int h = 1; h <= 6; h++) s += Math.Sin(2 * Math.PI * f * h * t) / (h * h);
            // little snare-ish click every half bar
            double tc = t % 0.75;
            double click = tc < 0.03 ? (Random.Shared.NextDouble() - 0.5) * Math.Exp(-tc * 120) : 0;
            float v = (float)(s * env * 0.12 + click * 0.5);
            if (t > seconds - 4) v = 0; // let the tail ring out
            dry[2 * i] = v; dry[2 * i + 1] = v;
        }
        var rv = new Reverb(Sr);
        rv.SetParams(ReverbParams.Default);
        var wet = new float[frames * 2];
        rv.Process(dry, wet, frames);
        var mix = new float[frames * 2];
        for (int i = 0; i < mix.Length; i++) mix[i] = Math.Clamp(dry[i] + wet[i], -1f, 1f);
        WriteWav(Path.Combine(dir, "demo-dry.wav"), dry);
        WriteWav(Path.Combine(dir, "demo-slowed-reverb.wav"), mix);
        Console.WriteLine($"  demo written to {dir}");
    }

    private static void WriteWav(string path, float[] stereo)
    {
        using var w = new BinaryWriter(File.Create(path));
        int dataBytes = stereo.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Sr); w.Write(Sr * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var v in stereo) w.Write((short)Math.Clamp(v * 32767f, -32768f, 32767f));
    }

    private sealed class Pink
    {
        private readonly Random _r;
        private double b0, b1, b2, b3, b4, b5, b6;
        public Pink(int seed) => _r = new Random(seed);
        public float Next()
        {
            double white = _r.NextDouble() * 2 - 1;
            b0 = 0.99886 * b0 + white * 0.0555179; b1 = 0.99332 * b1 + white * 0.0750759;
            b2 = 0.96900 * b2 + white * 0.1538520; b3 = 0.86650 * b3 + white * 0.3104856;
            b4 = 0.55000 * b4 + white * 0.5329522; b5 = -0.7616 * b5 - white * 0.0168980;
            double pink = b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362;
            b6 = white * 0.115926;
            return (float)(pink * 0.11);
        }
    }

    // ------------------------------------------------------------------ live probe

    /// <summary>Listens to Firefox for a few seconds and prints how loud it is.</summary>
    public static int Probe(int seconds, int pid = 0)
    {
        if (pid == 0) pid = Native.FindAnyFirefoxMain();
        if (pid == 0) { Console.WriteLine("Firefox is not running"); return 1; }
        Console.WriteLine($"Listening to Firefox (pid {pid}) for {seconds}s...");
        var client = Wasapi.ActivateProcessLoopbackAsync(pid, true).GetAwaiter().GetResult();
        IntPtr fmt = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        Marshal.StructureToPtr(WaveFormatEx.Float(48000, 2), fmt, false);
        Wasapi.Check(client.Initialize(0, Wasapi.StreamFlagsLoopback | Wasapi.StreamFlagsEventCallback | Wasapi.StreamFlagsAutoConvertPcm,
            200_000, 0, fmt, IntPtr.Zero), "init");
        IntPtr ev = Native.CreateEventW(IntPtr.Zero, false, false, null);
        client.SetEventHandle(ev);
        var iid = Wasapi.IID_IAudioCaptureClient;
        Wasapi.Check(client.GetService(ref iid, out var o), "service");
        var cap = (IAudioCaptureClient)o;
        client.Start();
        var buf = new float[48000];
        float overall = 0;
        for (int slice = 0; slice < seconds * 2; slice++)
        {
            float peak = 0; long frames = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 500)
            {
                Native.WaitForMultipleObjects(1, new[] { ev }, false, 100);
                while (cap.GetNextPacketSize(out uint n) >= 0 && n > 0)
                {
                    cap.GetBuffer(out IntPtr d, out uint fr, out uint fl, out _, out _);
                    int c = (int)fr * 2;
                    if (c > buf.Length) buf = new float[c];
                    if ((fl & Wasapi.BufferFlagsSilent) == 0) Marshal.Copy(d, buf, 0, c); else Array.Clear(buf, 0, c);
                    for (int i = 0; i < c; i++) peak = Math.Max(peak, Math.Abs(buf[i]));
                    frames += fr;
                    cap.ReleaseBuffer(fr);
                }
            }
            overall = Math.Max(overall, peak);
            int bars = (int)Math.Min(40, peak * 40);
            Console.WriteLine($"  {slice * 0.5,4:F1}s  {new string('#', bars),-40} {peak:F3}  ({frames} frames)");
        }
        client.Stop();
        Console.WriteLine(overall > 0.001 ? "PROBE: hearing Firefox OK" : "PROBE: silence (is something playing?)");
        return 0;
    }
}
