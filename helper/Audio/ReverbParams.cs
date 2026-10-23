using System;
using System.Text.Json.Nodes;

namespace Nightverb.Audio;

/// <summary>Reverb settings, in the same units the extension UI shows.</summary>
internal sealed record ReverbParams
{
    public double Wet { get; init; } = 45;          // %, 0..100
    public double Size { get; init; } = 130;        // %, 30..200
    public double Decay { get; init; } = 4.5;       // seconds, 0.3..30
    public double PreDelay { get; init; } = 20;     // ms, 0..300
    public double Diffusion { get; init; } = 75;    // %, 0..100
    public double LowCut { get; init; } = 180;      // Hz, 20..1000
    public double HighCut { get; init; } = 6500;    // Hz, 1000..20000
    public double Damping { get; init; } = 5000;    // Hz, 500..20000
    public double Modulation { get; init; } = 50;   // %, 0..100
    public double Width { get; init; } = 100;       // %, 0..150

    public static readonly ReverbParams Default = new();

    public ReverbParams Clamped() => this with
    {
        Wet = Clamp(Wet, 0, 100, Default.Wet),
        Size = Clamp(Size, 30, 200, Default.Size),
        Decay = Clamp(Decay, 0.3, 30, Default.Decay),
        PreDelay = Clamp(PreDelay, 0, 300, Default.PreDelay),
        Diffusion = Clamp(Diffusion, 0, 100, Default.Diffusion),
        LowCut = Clamp(LowCut, 20, 1000, Default.LowCut),
        HighCut = Clamp(HighCut, 1000, 20000, Default.HighCut),
        Damping = Clamp(Damping, 500, 20000, Default.Damping),
        Modulation = Clamp(Modulation, 0, 100, Default.Modulation),
        Width = Clamp(Width, 0, 150, Default.Width),
    };

    private static double Clamp(double v, double lo, double hi, double fallback) =>
        double.IsFinite(v) ? Math.Clamp(v, lo, hi) : fallback;

    /// <summary>Copy of this with any fields present in the JSON object replaced.</summary>
    public ReverbParams Merge(JsonObject? o)
    {
        if (o == null) return this;
        double Get(string name, double current) =>
            o.TryGetPropertyValue(name, out var n) && n is JsonValue v && v.TryGetValue<double>(out var d) ? d : current;
        return new ReverbParams
        {
            Wet = Get("wet", Wet),
            Size = Get("size", Size),
            Decay = Get("decay", Decay),
            PreDelay = Get("preDelay", PreDelay),
            Diffusion = Get("diffusion", Diffusion),
            LowCut = Get("lowCut", LowCut),
            HighCut = Get("highCut", HighCut),
            Damping = Get("damping", Damping),
            Modulation = Get("modulation", Modulation),
            Width = Get("width", Width),
        }.Clamped();
    }

    public JsonObject ToJson() => new()
    {
        ["wet"] = Wet,
        ["size"] = Size,
        ["decay"] = Decay,
        ["preDelay"] = PreDelay,
        ["diffusion"] = Diffusion,
        ["lowCut"] = LowCut,
        ["highCut"] = HighCut,
        ["damping"] = Damping,
        ["modulation"] = Modulation,
        ["width"] = Width,
    };
}
