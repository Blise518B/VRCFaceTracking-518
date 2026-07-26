using System;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using VRCFaceTracking.Core.Params.Expressions;

namespace VRCFaceTracking.Core.Params.Data.Mutation;

/// <summary>
/// Per-expression range and sensitivity calibration. Every unified expression gets its own
/// floor/ceiling remap plus a response curve, so individual expressions can be made more or
/// less sensitive across specific parts of their range. Guided capture buttons fill in the
/// floors (neutral face) and ceilings (maximum expressions) automatically; everything can
/// then be fine-tuned per expression from the UI.
/// </summary>
public class RangeCalibration : TrackingMutation
{
    private const float NeutralMargin = 0.02f;      // headroom added above observed neutral noise
    private const float MaxNeutralFloor = 0.4f;     // a neutral capture may never eat more than this much range
    private const float MinSpan = 0.05f;            // minimum floor-to-ceiling distance
    private static readonly TimeSpan NeutralCaptureDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxCaptureDuration = TimeSpan.FromSeconds(10);

    private enum CaptureMode
    {
        None,
        Neutral,
        Maximum
    }

    public ShapeRangeConfig[] Shapes = CreateDefaultConfig();

    [MutationProperty("Effect Blend", true)]
    public float blend = 1f;

    private volatile CaptureMode _captureMode = CaptureMode.None;
    private DateTime _captureUntil;
    private float[] _captureBuffer;

    public override string Name => "Range Calibration";

    public override string Description =>
        "Remaps each expression from its own floor/ceiling range and applies a per-expression sensitivity curve " +
        "(curve < 1 reacts stronger to small movements, curve > 1 requires bigger movements). " +
        "Use \"Capture Neutral\" while holding a relaxed face, then \"Capture Maximums\" while exaggerating expressions.";

    public override MutationPriority Step => MutationPriority.Preprocessor;

    public override bool IsSaved => true;

    public override bool IsActive { get; set; } = false;

    private static ShapeRangeConfig[] CreateDefaultConfig()
    {
        var configs = new ShapeRangeConfig[(int)UnifiedExpressions.Max];
        for (var i = 0; i < configs.Length; i++)
        {
            configs[i] = new ShapeRangeConfig();
        }
        return configs;
    }

    public override void Initialize(UnifiedTrackingData data) => EnsureConfigValid();

    /// <summary>
    /// Guards against missing/short config from older saves or enum growth. Idempotent;
    /// must run before anything indexes into <see cref="Shapes"/>.
    /// </summary>
    private void EnsureConfigValid()
    {
        if (Shapes == null || Shapes.Length != (int)UnifiedExpressions.Max)
        {
            var fresh = CreateDefaultConfig();
            if (Shapes != null)
            {
                for (var i = 0; i < Math.Min(Shapes.Length, fresh.Length); i++)
                {
                    if (Shapes[i] != null)
                    {
                        fresh[i] = Shapes[i];
                    }
                }
            }
            Shapes = fresh;
        }
        for (var i = 0; i < Shapes.Length; i++)
        {
            Shapes[i] ??= new ShapeRangeConfig();
        }
    }

    public override void MutateData(ref UnifiedTrackingData data)
    {
        SampleCapture(ref data);

        for (var i = 0; i < (int)UnifiedExpressions.Max; i++)
        {
            var cfg = Shapes[i];
            if (cfg is not { Enabled: true })
            {
                continue;
            }

            var raw = data.Shapes[i].Weight;
            if (float.IsNaN(raw) || float.IsInfinity(raw))
            {
                continue;
            }

            var span = cfg.Ceil - cfg.Floor;
            if (span < MinSpan)
            {
                continue;
            }

            var v = (raw - cfg.Floor) / span;
            v = Math.Clamp(v, 0f, 1f);

            if (cfg.Curve > 0f && cfg.Curve != 1f)
            {
                v = MathF.Pow(v, cfg.Curve);
            }

            data.Shapes[i].Weight = blend * v + (1f - blend) * raw;
        }
    }

    private void SampleCapture(ref UnifiedTrackingData data)
    {
        var mode = _captureMode;
        if (mode == CaptureMode.None)
        {
            return;
        }

        if (_captureBuffer == null)
        {
            return;
        }

        for (var i = 0; i < (int)UnifiedExpressions.Max; i++)
        {
            var raw = data.Shapes[i].Weight;
            if (!float.IsNaN(raw) && !float.IsInfinity(raw) && raw > _captureBuffer[i])
            {
                _captureBuffer[i] = raw;
            }
        }

        if (DateTime.UtcNow >= _captureUntil)
        {
            FinishCapture(mode);
            _captureMode = CaptureMode.None;
        }
    }

    private void FinishCapture(CaptureMode mode)
    {
        var updated = 0;
        for (var i = 0; i < (int)UnifiedExpressions.Max; i++)
        {
            var observedMax = _captureBuffer[i];
            if (observedMax < 0f)
            {
                continue; // never saw a usable sample for this shape
            }

            var cfg = Shapes[i];
            switch (mode)
            {
                case CaptureMode.Neutral:
                    cfg.Floor = Math.Min(observedMax + NeutralMargin, MaxNeutralFloor);
                    if (cfg.Ceil < cfg.Floor + MinSpan)
                    {
                        cfg.Ceil = Math.Min(cfg.Floor + MinSpan, 1f);
                    }
                    updated++;
                    break;

                case CaptureMode.Maximum:
                    // Only adopt a new ceiling for expressions the user actually performed
                    if (observedMax > cfg.Floor + MinSpan)
                    {
                        cfg.Ceil = Math.Min(observedMax, 1f);
                        updated++;
                    }
                    break;
            }
        }

        Logger?.LogInformation("Range Calibration: {mode} capture finished, {count} expressions updated.", mode, updated);
        RefreshRowComponents();
    }

    private void BeginCapture(CaptureMode mode, TimeSpan duration)
    {
        _captureBuffer = new float[(int)UnifiedExpressions.Max];
        Array.Fill(_captureBuffer, -1f);
        _captureUntil = DateTime.UtcNow + duration;
        _captureMode = mode;
        Logger?.LogInformation("Range Calibration: {mode} capture running for {seconds}s...", mode, duration.TotalSeconds);
    }

    private void RefreshRowComponents()
    {
        if (Components == null)
        {
            return;
        }
        foreach (var component in Components)
        {
            if (component is MutationExpressionRow row)
            {
                row.NotifyAllChanged();
            }
        }
    }

    [MutationButton("Capture Neutral (3s)")]
    public void CaptureNeutral()
    {
        if (!IsActive)
        {
            Logger?.LogWarning("Range Calibration: enable the mutation before capturing.");
            return;
        }
        BeginCapture(CaptureMode.Neutral, NeutralCaptureDuration);
    }

    [MutationButton("Capture Maximums (10s)")]
    public void CaptureMaximums()
    {
        if (!IsActive)
        {
            Logger?.LogWarning("Range Calibration: enable the mutation before capturing.");
            return;
        }
        BeginCapture(CaptureMode.Maximum, MaxCaptureDuration);
    }

    [MutationButton("Reset All Ranges")]
    public void ResetAll()
    {
        _captureMode = CaptureMode.None;
        foreach (var cfg in Shapes)
        {
            cfg.Enabled = true;
            cfg.Floor = 0f;
            cfg.Ceil = 1f;
            cfg.Curve = 1f;
        }
        Logger?.LogInformation("Range Calibration: all ranges reset.");
        RefreshRowComponents();
    }

    [MutationButton("Save Ranges Now")]
    public async void SaveNow()
    {
        await Save();
        Logger?.LogInformation("Range Calibration: configuration saved.");
    }

    public override void CreateProperties()
    {
        EnsureConfigValid();
        base.CreateProperties();
        for (var i = 0; i < (int)UnifiedExpressions.Max; i++)
        {
            Components.Add(new MutationExpressionRow(((UnifiedExpressions)i).ToString(), Shapes[i]));
        }
    }
}
