using System;

namespace Nightverb.Audio;

/// <summary>
/// Stereo algorithmic reverb that outputs only the wet signal.
///
/// Signal path:
///   input -> pre-delay -> low cut / high cut (12 dB/oct) -> 4 allpass diffusers per channel
///         -> 16-line feedback delay network (Hadamard mixing, per-line damping and
///            slow pitch modulation for a smooth, non-metallic tail)
///         -> stereo width -> wet gain -> soft limiter
/// </summary>
internal sealed class Reverb
{
    private const int N = 16;

    // Base line lengths in ms at Size = 100%. Spread out and mutually non-harmonic
    // so the echoes never line up into a pitched ring.
    private static readonly double[] BaseMs =
    {
        29.3, 31.7, 34.1, 37.9, 41.3, 43.7, 47.9, 51.1,
        55.3, 59.9, 62.7, 67.1, 71.9, 76.3, 81.7, 89.3,
    };

    // Per-line LFO rates (Hz), all different so the modulation never pulses in sync.
    private static readonly double[] LfoHz =
    {
        0.31, 0.47, 0.53, 0.67, 0.73, 0.89, 0.97, 1.07,
        0.37, 0.43, 0.59, 0.61, 0.79, 0.83, 1.01, 1.13,
    };

    // Sign patterns for injecting input and tapping output. Different patterns
    // for left and right give a wide, decorrelated stereo image.
    private static readonly float[] InSign = { 1, 1, -1, 1, -1, -1, 1, -1, 1, -1, 1, 1, -1, 1, -1, -1 };
    private static readonly float[] OutL = { 1, -1, 1, 1, -1, 1, -1, -1, 1, 1, -1, 1, 1, -1, -1, 1 };
    private static readonly float[] OutR = { 1, 1, -1, 1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1, -1, -1 };

    private static readonly double[] DiffMsL = { 4.77, 3.59, 12.73, 9.31 };
    private static readonly double[] DiffMsR = { 4.93, 3.77, 13.11, 8.87 };

    // Approximate wet-signal delay added by capture + re-render; subtracted from
    // the requested pre-delay so the setting means "total" pre-delay.
    public double LatencyCompensationMs { get; set; }

    private readonly int _sr;

    // FDN state
    private readonly float[][] _lines = new float[N][];
    private readonly int _mask;
    private int _w;
    private readonly double[] _len = new double[N];
    private readonly double[] _lenTarget = new double[N];
    private readonly float[] _gain = new float[N];
    private readonly float[] _dampState = new float[N];
    private float _dampA;
    private readonly double[] _lfoSin = new double[N];
    private readonly double[] _lfoCos = new double[N];
    private readonly double[] _lfoRotS = new double[N];
    private readonly double[] _lfoRotC = new double[N];
    private double _modDepth, _modDepthTarget;
    private readonly float[] _tmp = new float[N];
    private readonly float[] _taps = new float[N];

    // Pre-delay
    private readonly float[] _preL, _preR;
    private readonly int _preMask;
    private int _preW;
    private double _preDelay, _preDelayTarget;

    // Tone filters (biquads), per channel
    private Biquad _hpL, _hpR, _lpL, _lpR;

    // Diffusers
    private readonly Allpass[] _apL = new Allpass[4];
    private readonly Allpass[] _apR = new Allpass[4];
    private float _diffG1, _diffG2;

    private float _wetGain, _wetGainTarget;
    private float _width = 1f, _widthTarget = 1f;
    private float _denormal = 1e-18f;

    private ReverbParams _params = ReverbParams.Default;
    private bool _first = true;

    public Reverb(int sampleRate)
    {
        _sr = sampleRate;

        double maxMs = BaseMs[^1] * 2.0 + 3.0; // Size up to 200%, plus modulation and interpolation headroom
        int size = NextPow2((int)(maxMs * _sr / 1000.0) + 8);
        _mask = size - 1;
        for (int i = 0; i < N; i++) _lines[i] = new float[size];

        int preSize = NextPow2((int)(0.31 * _sr) + 8);
        _preMask = preSize - 1;
        _preL = new float[preSize];
        _preR = new float[preSize];

        for (int i = 0; i < 4; i++)
        {
            _apL[i] = new Allpass((int)(DiffMsL[i] * 2.0 * _sr / 1000.0) + 4);
            _apR[i] = new Allpass((int)(DiffMsR[i] * 2.0 * _sr / 1000.0) + 4);
        }

        for (int i = 0; i < N; i++)
        {
            double phase = i * 2.39996; // golden-angle spread of starting phases
            _lfoSin[i] = Math.Sin(phase);
            _lfoCos[i] = Math.Cos(phase);
            double w = 2 * Math.PI * LfoHz[i] / _sr;
            _lfoRotS[i] = Math.Sin(w);
            _lfoRotC[i] = Math.Cos(w);
        }

        SetParams(ReverbParams.Default);
    }

    public void SetParams(ReverbParams p)
    {
        _params = p;
        double size = p.Size / 100.0;

        double meanLenSec = 0;
        for (int i = 0; i < N; i++)
        {
            _lenTarget[i] = BaseMs[i] * size * _sr / 1000.0;
            meanLenSec += _lenTarget[i] / _sr;
            _gain[i] = (float)Math.Pow(10.0, -3.0 * _lenTarget[i] / (p.Decay * DecayCorrection * _sr));
        }
        meanLenSec /= N;

        _dampA = (float)Math.Exp(-2 * Math.PI * Math.Min(p.Damping, 0.45 * _sr) / _sr);
        _modDepthTarget = p.Modulation / 100.0 * 0.0011 * _sr;

        _preDelayTarget = Math.Max(0, p.PreDelay - LatencyCompensationMs) * _sr / 1000.0;

        double lc = Math.Min(p.LowCut, 0.45 * _sr), hc = Math.Min(p.HighCut, 0.45 * _sr);
        _hpL.SetHighPass(lc, _sr); _hpR.SetHighPass(lc, _sr);
        _lpL.SetLowPass(hc, _sr); _lpR.SetLowPass(hc, _sr);

        double d = p.Diffusion / 100.0;
        _diffG1 = (float)(0.75 * d);
        _diffG2 = (float)(0.625 * d);
        double apScale = 0.5 + 0.5 * size;
        for (int i = 0; i < 4; i++)
        {
            _apL[i].Delay = Math.Max(1, (int)(DiffMsL[i] * apScale * _sr / 1000.0));
            _apR[i].Delay = Math.Max(1, (int)(DiffMsR[i] * apScale * _sr / 1000.0));
        }

        // Loudness normalization: a lossless FDN's energy grows roughly with
        // decay / line length, so scale it back to keep the wet level steady
        // when you change Size or Decay. Long tails still build up a bit louder.
        double norm = Math.Sqrt(13.8 * meanLenSec / p.Decay) * Math.Pow(p.Decay / 2.0, 0.12);
        double w = p.Wet / 100.0;
        _wetGainTarget = (float)(w * w * 2.0 * norm * CalibrationGain);
        _widthTarget = (float)(p.Width / 100.0);

        if (_first)
        {
            _first = false;
            Array.Copy(_lenTarget, _len, N);
            _modDepth = _modDepthTarget;
            _preDelay = _preDelayTarget;
            _wetGain = _wetGainTarget;
            _width = _widthTarget;
        }
    }

    /// <summary>Re-apply current params (e.g. after latency compensation changed).</summary>
    public void Refresh() => SetParams(_params);

    // Measured with the self-test (pink noise in, wet out) so that Wet = 50%
    // sits around -7 dB below the dry track.
    internal const double CalibrationGain = 2.9;

    // Interpolation and diffusion lose a little energy each pass; this makes
    // the measured RT60 match the Decay setting.
    private const double DecayCorrection = 1.11;

    /// <summary>Clear all internal state (tail is cut).</summary>
    public void Reset()
    {
        foreach (var l in _lines) Array.Clear(l);
        Array.Clear(_dampState);
        Array.Clear(_preL); Array.Clear(_preR);
        foreach (var a in _apL) a.Clear();
        foreach (var a in _apR) a.Clear();
        _hpL.Clear(); _hpR.Clear(); _lpL.Clear(); _lpR.Clear();
    }

    /// <summary>Process interleaved stereo. input and output may not alias.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output, int frames)
    {
        const float lenSlew = 0.0004f;   // ~50 ms glide when Size changes
        const float paramSlew = 0.002f;  // ~10 ms for gains
        float invSqrtN = 0.25f;          // 1/sqrt(16)

        for (int n = 0; n < frames; n++)
        {
            float inL = input[2 * n], inR = input[2 * n + 1];

            // --- pre-delay (fractional, linear interpolation; smooth when changed) ---
            _preL[_preW] = inL;
            _preR[_preW] = inR;
            _preDelay += (_preDelayTarget - _preDelay) * paramSlew;
            float pL, pR;
            if (_preDelay < 1.0)
            {
                pL = inL; pR = inR;
            }
            else
            {
                double pos = _preW - _preDelay;
                int i0 = (int)Math.Floor(pos);
                float f = (float)(pos - i0);
                int a = i0 & _preMask, b = (i0 + 1) & _preMask;
                pL = _preL[a] + (_preL[b] - _preL[a]) * f;
                pR = _preR[a] + (_preR[b] - _preR[a]) * f;
            }
            _preW = (_preW + 1) & _preMask;

            // --- tone ---
            // A tiny alternating offset (-360 dB) keeps every filter and delay out of
            // denormal numbers on silence, which would otherwise spike the CPU.
            _denormal = -_denormal;
            pL = _lpL.Process(_hpL.Process(pL + _denormal));
            pR = _lpR.Process(_hpR.Process(pR + _denormal));

            // --- input diffusion ---
            pL = _apL[0].Process(pL, _diffG1); pL = _apL[1].Process(pL, _diffG1);
            pL = _apL[2].Process(pL, _diffG2); pL = _apL[3].Process(pL, _diffG2);
            pR = _apR[0].Process(pR, _diffG1); pR = _apR[1].Process(pR, _diffG1);
            pR = _apR[2].Process(pR, _diffG2); pR = _apR[3].Process(pR, _diffG2);

            // --- FDN: read every line (modulated, cubic interpolation) ---
            _modDepth += (_modDepthTarget - _modDepth) * paramSlew;
            for (int i = 0; i < N; i++)
            {
                // rotate LFO phasor
                double s = _lfoSin[i], c = _lfoCos[i];
                _lfoSin[i] = s * _lfoRotC[i] + c * _lfoRotS[i];
                _lfoCos[i] = c * _lfoRotC[i] - s * _lfoRotS[i];

                _len[i] += (_lenTarget[i] - _len[i]) * lenSlew;
                double delay = _len[i] + _modDepth * (1.0 + _lfoSin[i]) + 2.0;
                float v = ReadHermite(_lines[i], _w - delay);
                _taps[i] = v;

                // damping (one-pole low-pass) and decay
                float ds = v + (_dampState[i] - v) * _dampA;
                _dampState[i] = ds;
                _tmp[i] = ds * _gain[i];
            }

            // Renormalize the LFO phasors now and then to stop numeric drift.
            if ((_w & 4095) == 0) RenormalizeLfos();

            // --- mix: 16-point Hadamard (energy preserving) ---
            Hadamard16(_tmp);

            // --- inject input and write back ---
            for (int i = 0; i < N; i++)
            {
                float src = (i & 1) == 0 ? pL : pR;
                _lines[i][_w] = _tmp[i] * invSqrtN + src * InSign[i] * 0.5f + _denormal;
            }
            _w = (_w + 1) & _mask;

            // --- outputs ---
            float oL = 0, oR = 0;
            for (int i = 0; i < N; i++)
            {
                oL += _taps[i] * OutL[i];
                oR += _taps[i] * OutR[i];
            }
            oL *= invSqrtN;
            oR *= invSqrtN;

            _width += (_widthTarget - _width) * paramSlew;
            float mid = 0.5f * (oL + oR), side = 0.5f * (oL - oR) * _width;
            _wetGain += (_wetGainTarget - _wetGain) * paramSlew;
            oL = (mid + side) * _wetGain;
            oR = (mid - side) * _wetGain;

            output[2 * n] = SoftLimit(oL);
            output[2 * n + 1] = SoftLimit(oR);
        }
    }

    private float ReadHermite(float[] buf, double pos)
    {
        int i = (int)Math.Floor(pos);
        float f = (float)(pos - i);
        float xm1 = buf[(i - 1) & _mask];
        float x0 = buf[i & _mask];
        float x1 = buf[(i + 1) & _mask];
        float x2 = buf[(i + 2) & _mask];
        float c1 = 0.5f * (x1 - xm1);
        float c2 = xm1 - 2.5f * x0 + 2f * x1 - 0.5f * x2;
        float c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
        return ((c3 * f + c2) * f + c1) * f + x0;
    }

    private static void Hadamard16(float[] x)
    {
        for (int h = 1; h < 16; h <<= 1)
        {
            for (int i = 0; i < 16; i += h << 1)
            {
                for (int j = i; j < i + h; j++)
                {
                    float a = x[j], b = x[j + h];
                    x[j] = a + b;
                    x[j + h] = a - b;
                }
            }
        }
    }

    private void RenormalizeLfos()
    {
        for (int i = 0; i < N; i++)
        {
            double m = Math.Sqrt(_lfoSin[i] * _lfoSin[i] + _lfoCos[i] * _lfoCos[i]);
            if (m > 0) { _lfoSin[i] /= m; _lfoCos[i] /= m; }
        }
    }

    // Transparent below 0.7, rounds off peaks above so the wet signal can never clip hard.
    private static float SoftLimit(float x)
    {
        float a = Math.Abs(x);
        if (a <= 0.7f) return x;
        float y = 0.7f + 0.3f * MathF.Tanh((a - 0.7f) / 0.3f);
        return x < 0 ? -y : y;
    }

    private static int NextPow2(int v)
    {
        int p = 1;
        while (p < v) p <<= 1;
        return p;
    }

    private sealed class Allpass
    {
        private readonly float[] _buf;
        private int _pos;
        public int Delay;

        public Allpass(int maxDelay) { _buf = new float[maxDelay + 1]; Delay = maxDelay; }

        public void Clear() => Array.Clear(_buf);

        public float Process(float x, float g)
        {
            int len = _buf.Length;
            int r = _pos - Delay; if (r < 0) r += len;
            float delayed = _buf[r];
            float w = x + g * delayed;
            _buf[_pos] = w;
            _pos++; if (_pos >= len) _pos = 0;
            return delayed - g * w;
        }
    }

    private struct Biquad
    {
        private float _b0, _b1, _b2, _a1, _a2;
        private float _z1, _z2;

        public void SetLowPass(double fc, int sr) => Set(fc, sr, false);
        public void SetHighPass(double fc, int sr) => Set(fc, sr, true);

        private void Set(double fc, int sr, bool high)
        {
            // RBJ cookbook, Q = 0.7071 (Butterworth)
            double w0 = 2 * Math.PI * fc / sr;
            double cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * 0.70710678);
            double a0 = 1 + alpha;
            double b0, b1, b2;
            if (high) { b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2; }
            else { b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2; }
            _b0 = (float)(b0 / a0); _b1 = (float)(b1 / a0); _b2 = (float)(b2 / a0);
            _a1 = (float)(-2 * cos / a0); _a2 = (float)((1 - alpha) / a0);
        }

        public void Clear() { _z1 = 0; _z2 = 0; }

        // Transposed direct form II
        public float Process(float x)
        {
            float y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}
