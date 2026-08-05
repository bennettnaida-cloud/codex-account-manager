using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace CodexAccountManager;

/// <summary>
/// One passive, locally-observed quota sample. The monetary value is the API-equivalent
/// cost for this interval; it is never interpreted as an official account balance.
/// </summary>
internal sealed record QuotaChartModelUsage(
    string Model,
    double ApiEquivalentCostUsd,
    long TotalTokens,
    int EventCount);

internal sealed record QuotaChartSample(
    DateTimeOffset Timestamp,
    double IncrementalCostUsd,
    double? RemainingPercent,
    long TotalTokens,
    TimeSpan BucketDuration,
    IReadOnlyList<QuotaChartModelUsage>? ModelUsage = null);

internal enum QuotaTrendMetric
{
    ApiEquivalent,
    Tokens
}

/// <summary>
/// A compact, double-buffered quota gauge. It only paints values supplied by its owner and
/// performs no I/O, polling, account login, or network activity.
/// </summary>
internal sealed class PassiveQuotaGauge : Control
{
    private const int WaveAnimationIntervalMilliseconds = 50;
    private const float WavePhaseStep = 0.075F;
    private const float PlanetRingWidthRatio = 1.46F;
    private const float PlanetRingHeightRatio = 0.38F;
    private const float PlanetRingRotationDegrees = -13F;
    // The gauge intentionally owns one visual identity. AccentColor is still accepted so
    // existing Form1 bindings do not need special cases, but quota health must never turn
    // the liquid red/green: health is communicated by the status label outside the sphere.
    private static readonly Color _LiquidSky = Color.FromArgb(79, 172, 254);
    private static readonly Color _LiquidBlue = Color.FromArgb(59, 103, 246);
    private static readonly Color _LiquidIndigo = Color.FromArgb(92, 72, 224);
    private static readonly Color _LiquidViolet = Color.FromArgb(124, 58, 237);
    private static readonly Color _LiquidDeep = Color.FromArgb(76, 29, 149);
    private static readonly Color _EnergyBlue = Color.FromArgb(56, 189, 248);
    private static readonly Color _EnergyViolet = Color.FromArgb(167, 139, 250);
    private static readonly Color _GlassTint = Color.FromArgb(224, 231, 255);
    private static readonly Color _PlanetNight = Color.FromArgb(15, 23, 67);
    private static readonly Color _PlanetBlue = Color.FromArgb(37, 74, 170);
    private static readonly Color _PlanetViolet = Color.FromArgb(101, 64, 205);
    private static readonly Color _AtmosphereBlue = Color.FromArgb(72, 176, 255);
    private static readonly Color _AtmosphereViolet = Color.FromArgb(158, 112, 255);
    private static readonly BubbleSpec[] BubbleSpecs =
    [
        new(0.18F, 0.08F, 1F, 1F, 0.018F),
        new(0.43F, 0.71F, 1F, 2F, 0.022F),
        new(0.56F, 0.23F, 2F, 1F, 0.015F),
        new(0.78F, 0.86F, 1F, 2F, 0.019F),
        new(0.87F, 0.35F, 2F, 3F, 0.013F)
    ];
    private static readonly StarDustSpec[] StarDustSpecs =
    [
        new(-154F, 0.72F, 1.15F, 0.22F),
        new(-121F, 0.83F, 0.72F, -0.16F),
        new(-58F, 0.78F, 0.88F, 0.13F),
        new(-22F, 0.69F, 1.28F, -0.11F),
        new(34F, 0.81F, 0.74F, 0.18F),
        new(143F, 0.76F, 0.96F, -0.14F),
        new(174F, 0.88F, 0.66F, 0.10F)
    ];
    private readonly System.Windows.Forms.Timer _waveAnimationTimer;
    private Form? _animationHostForm;
    private float _wavePhase;
    private Rectangle _lastAnimationBounds = Rectangle.Empty;
    private float? _offlineDpiScale;
    private bool _offlineCaptionVisible = true;
    private double? _remainingPercent;
    private string _statusText = string.Empty;
    private string _caption = "官方剩余额度";
    private string _placeholderText = "采集中";
    private Color _accentColor = Color.FromArgb(16, 185, 129);
    private Color _trackColor = Color.FromArgb(226, 232, 240);
    private Color _textColor = Color.FromArgb(15, 23, 42);
    private Color _mutedColor = Color.FromArgb(100, 116, 139);

    private readonly record struct BubbleSpec(
        float HorizontalPosition,
        float StartProgress,
        float RiseSpeed,
        float SwaySpeed,
        float RadiusRatio);

    private readonly record struct StarDustSpec(
        float AngleDegrees,
        float DistanceRatio,
        float RadiusRatio,
        float DriftSpeed);

    private readonly record struct PlanetGeometry(
        RectangleF SphereBounds,
        RectangleF RingBounds,
        Rectangle AnimationBounds);

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double? RemainingPercent
    {
        get => _remainingPercent;
        set
        {
            double? normalized = value.HasValue && double.IsFinite(value.Value)
                ? Math.Clamp(value.Value, 0D, 100D)
                : null;
            if (_remainingPercent == normalized)
            {
                return;
            }

            _remainingPercent = normalized;
            UpdateAccessibility();
            UpdateWaveAnimationState();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string StatusText
    {
        get => _statusText;
        set
        {
            var normalized = value ?? string.Empty;
            if (_statusText == normalized)
            {
                return;
            }

            _statusText = normalized;
            UpdateAccessibility();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Caption
    {
        get => _caption;
        set
        {
            var normalized = value ?? string.Empty;
            if (_caption == normalized)
            {
                return;
            }

            _caption = normalized;
            UpdateAccessibility();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PlaceholderText
    {
        get => _placeholderText;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "采集中" : value;
            if (_placeholderText == normalized)
            {
                return;
            }

            _placeholderText = normalized;
            UpdateAccessibility();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentColor
    {
        get => _accentColor;
        set => SetColor(ref _accentColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TrackColor
    {
        get => _trackColor;
        set => SetColor(ref _trackColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TextColor
    {
        get => _textColor;
        set => SetColor(ref _textColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color MutedColor
    {
        get => _mutedColor;
        set => SetColor(ref _mutedColor, value);
    }

    public PassiveQuotaGauge()
    {
        _waveAnimationTimer = new System.Windows.Forms.Timer
        {
            Interval = WaveAnimationIntervalMilliseconds
        };
        _waveAnimationTimer.Tick += HandleWaveAnimationTick;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            // A transparent 420px owner-drawn gauge makes WinForms ask the rounded parent to
            // repaint its gradient for every wave frame.  Besides the cost, that parent-copy
            // path is prone to stale pixels while AutoScroll moves child windows.
            ControlStyles.Opaque,
            true);
        DoubleBuffered = true;
        BackColor = Color.FromArgb(247, 250, 252);
        Size = new Size(320, 220);
        MinimumSize = new Size(150, 138);
        TabStop = false;
        UpdateAccessibility();
    }

    internal void RefreshAnimationStateForViewport() => UpdateWaveAnimationState();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AttachAnimationHost();
        UpdateWaveAnimationState();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        StopWaveAnimation();
        DetachAnimationHost();
        base.OnHandleDestroyed(e);
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        AttachAnimationHost();
        UpdateWaveAnimationState();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateWaveAnimationState();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopWaveAnimation();
            DetachAnimationHost();
        }

        base.Dispose(disposing);

        if (disposing)
        {
            _waveAnimationTimer.Tick -= HandleWaveAnimationTick;
            _waveAnimationTimer.Dispose();
        }
    }

    internal void SetWavePhaseForOfflineValidation(float phase)
    {
        _wavePhase = float.IsFinite(phase)
            ? phase % MathF.Tau
            : 0F;
        Invalidate();
    }

    internal void SetDpiScaleForOfflineValidation(float? scale)
    {
        _offlineDpiScale = scale is > 0F && float.IsFinite(scale.Value)
            ? scale.Value
            : null;
        Invalidate();
    }

    internal void SetCaptionVisibleForOfflineValidation(bool visible)
    {
        _offlineCaptionVisible = visible;
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Opaque is intentional: paint our stage before any planet layer so scroll exposure
        // never samples a transparent parent buffer from a previous viewport position.
        using (var background = new SolidBrush(ResolveOpaqueStageColor()))
        {
            e.Graphics.FillRectangle(background, e.ClipRectangle);
        }
        base.OnPaint(e);
        // A gauge can be constructed before its card is attached to the visible form.
        // Reconfirm the host and timer on the first real paint so a newly rendered detail
        // page always starts animating without requiring a resize or visibility change.
        if (_animationHostForm is null)
        {
            AttachAnimationHost();
        }
        UpdateWaveAnimationState();

        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

        var dpi = _offlineDpiScale ?? Math.Max(1F, DeviceDpi / 96F);
        var inset = Math.Max(3F, 5F * dpi);
        var content = RectangleF.Inflate(ClientRectangle, -inset, -inset);
        if (content.Width < 70F || content.Height < 76F)
        {
            QuotaDashboardDrawing.DrawFittedText(
                graphics,
                _remainingPercent.HasValue
                    ? _remainingPercent.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                    : _placeholderText,
                Font,
                FontStyle.Bold,
                _remainingPercent.HasValue ? _textColor : _mutedColor,
                content,
                StringAlignment.Center,
                StringAlignment.Center,
                Math.Max(5.5F, Font.Size * 0.65F));
            return;
        }

        // Reserve horizontal room for the tilted Saturn-style ring instead of clipping it
        // at the control edge. All measurements derive from DeviceDpi so the atmosphere,
        // liquid edge and ring remain crisp when moving between monitors.
        var planetGeometry = CalculatePlanetGeometry(content, dpi);
        var sphereBounds = planetGeometry.SphereBounds;
        var ringBounds = planetGeometry.RingBounds;
        var diameter = sphereBounds.Width;
        _lastAnimationBounds = planetGeometry.AnimationBounds;
        var darkSurface = RelativeLuminance(_textColor) > RelativeLuminance(_trackColor);
        // Decorative energy continues at the terminal 0%/100% values.  The represented
        // liquid level remains fixed; only glass light, particles and orbital accents move.
        var animationPhase = _remainingPercent.HasValue
            ? NormalizePhase(_wavePhase)
            : 0F;
        var energyBlend = 0.48F +
                          (0.16F * ((MathF.Sin(animationPhase - 0.4F) + 1F) / 2F));
        var energyColor = QuotaDashboardDrawing.Blend(_EnergyBlue, _EnergyViolet, energyBlend);
        DrawAmbientEnergyHalo(
            graphics,
            sphereBounds,
            animationPhase,
            dpi,
            darkSurface,
            energyColor);
        DrawPlanetStarDust(
            graphics,
            sphereBounds,
            animationPhase,
            dpi,
            darkSurface,
            energyColor);
        DrawSphereShadow(graphics, sphereBounds, dpi, darkSurface);
        DrawPlanetRingHalf(
            graphics,
            sphereBounds,
            ringBounds,
            animationPhase,
            frontHalf: false,
            dpi,
            darkSurface,
            energyColor);

        using var spherePath = new GraphicsPath();
        spherePath.AddEllipse(sphereBounds);

        var shellTop = QuotaDashboardDrawing.Blend(
            _PlanetNight,
            darkSurface ? _PlanetBlue : _GlassTint,
            darkSurface ? 0.18F : 0.10F);
        var shellMiddle = QuotaDashboardDrawing.Blend(_PlanetNight, _PlanetBlue, 0.38F);
        var shellBottom = QuotaDashboardDrawing.Blend(_PlanetNight, _PlanetViolet, 0.46F);
        using (var shellBrush = new LinearGradientBrush(
                   new PointF(sphereBounds.Left, sphereBounds.Top),
                   new PointF(sphereBounds.Left, sphereBounds.Bottom),
                   shellTop,
                   shellBottom))
        {
            shellBrush.InterpolationColors = new ColorBlend
            {
                Colors = new[] { shellTop, shellMiddle, shellBottom },
                Positions = new[] { 0F, 0.52F, 1F }
            };
            graphics.FillEllipse(shellBrush, sphereBounds);
        }

        var innerInset = Math.Max(1.4F * dpi, diameter * 0.008F);
        var liquidBounds = RectangleF.Inflate(sphereBounds, -innerInset, -innerInset);
        var percent = _remainingPercent.HasValue
            ? (float)_remainingPercent.Value
            : 0F;
        var liquidLevel = liquidBounds.Bottom;
        var waveAmplitude = 0F;
        var liquidSurfaceColor = QuotaDashboardDrawing.Blend(_LiquidSky, Color.White, 0.18F);

        if (_remainingPercent.HasValue && percent > 0F)
        {
            liquidLevel = liquidBounds.Bottom - (liquidBounds.Height * percent / 100F);
            // Keep a visible ripple at very low/high (but non-terminal) percentages.
            // The old factor reduced a 1% liquid surface to well below one physical pixel,
            // which made a running animation look completely static.
            var edgeFactor = percent >= 100F
                ? 0F
                : Math.Max(0.46F, Math.Min(1F, Math.Min(percent, 100F - percent) / 8F));
            waveAmplitude = Math.Clamp(
                diameter * 0.027F,
                1.8F * dpi,
                6F * dpi) * edgeFactor;

            // The rear wave has a slightly different wavelength and direction. Both layers
            // use the exact same percentage-controlled baseline: animation changes only the
            // local ripple, never the represented liquid level.
            using var rearLiquidPath = CreateWavePath(
                liquidBounds,
                liquidLevel,
                waveAmplitude * 0.72F,
                -animationPhase + 1.18F,
                closeAtBottom: true,
                primaryCycles: 1.34F,
                secondaryCycles: 2.62F,
                primaryWeight: 0.74F);
            var rearSurfaceColor = QuotaDashboardDrawing.Blend(
                _EnergyBlue,
                _EnergyViolet,
                0.46F);
            var liquidTop = Math.Max(liquidBounds.Top, liquidLevel - waveAmplitude - (1F * dpi));
            using (var rearLiquidBrush = new LinearGradientBrush(
                       new PointF(liquidBounds.Left, liquidTop),
                       new PointF(liquidBounds.Left, liquidBounds.Bottom),
                       Color.FromArgb(184, rearSurfaceColor),
                       Color.FromArgb(232, _LiquidViolet)))
            {
                var rearState = graphics.Save();
                graphics.SetClip(spherePath, CombineMode.Intersect);
                graphics.FillPath(rearLiquidBrush, rearLiquidPath);
                graphics.Restore(rearState);
            }

            using var liquidPath = CreateWavePath(
                liquidBounds,
                liquidLevel,
                waveAmplitude,
                animationPhase,
                closeAtBottom: true);
            using (var liquidBrush = new LinearGradientBrush(
                       new PointF(liquidBounds.Left, liquidTop),
                       new PointF(liquidBounds.Right, liquidBounds.Bottom),
                       liquidSurfaceColor,
                       _LiquidDeep))
            {
                liquidBrush.InterpolationColors = new ColorBlend
                {
                    Colors = new[]
                    {
                        Color.FromArgb(226, liquidSurfaceColor),
                        Color.FromArgb(242, _LiquidBlue),
                        Color.FromArgb(250, _LiquidIndigo),
                        Color.FromArgb(252, _LiquidViolet),
                        Color.FromArgb(255, _LiquidDeep)
                    },
                    Positions = new[] { 0F, 0.24F, 0.52F, 0.76F, 1F }
                };

                var state = graphics.Save();
                graphics.SetClip(spherePath, CombineMode.Intersect);
                graphics.FillPath(liquidBrush, liquidPath);
                graphics.Restore(state);
            }

            using var wavePath = CreateWavePath(
                liquidBounds,
                liquidLevel,
                waveAmplitude,
                animationPhase,
                closeAtBottom: false);
            using var rearWavePath = CreateWavePath(
                liquidBounds,
                liquidLevel,
                waveAmplitude * 0.72F,
                -animationPhase + 1.18F,
                closeAtBottom: false,
                primaryCycles: 1.34F,
                secondaryCycles: 2.62F,
                primaryWeight: 0.74F);
            var waveState = graphics.Save();
            graphics.SetClip(spherePath, CombineMode.Intersect);
            using (var rearWaveGlow = new Pen(
                       Color.FromArgb(darkSurface ? 96 : 74,
                           QuotaDashboardDrawing.Blend(energyColor, Color.White, 0.48F)),
                       Math.Max(1.6F * dpi, diameter * 0.011F))
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round,
                       LineJoin = LineJoin.Round
                   })
            {
                graphics.DrawPath(rearWaveGlow, rearWavePath);
            }
            using (var waveGlow = new Pen(
                       Color.FromArgb(darkSurface ? 44 : 34, Color.White),
                       Math.Max(2.8F * dpi, diameter * 0.018F))
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round,
                       LineJoin = LineJoin.Round
                   })
            {
                graphics.DrawPath(waveGlow, wavePath);
            }
            using (var wavePen = new Pen(
                       Color.FromArgb(darkSurface ? 205 : 176,
                           QuotaDashboardDrawing.Blend(_EnergyBlue, Color.White, 0.66F)),
                       Math.Max(0.9F * dpi, diameter * 0.006F))
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round,
                       LineJoin = LineJoin.Round
                   })
            {
                graphics.DrawPath(wavePen, wavePath);
            }
            graphics.Restore(waveState);

            // Do not run full-width caustics or energy lanes through the liquid. They read
            // as a scanner sweeping over the value. The wave surface and sparse bubbles
            // carry the data motion while local planet texture adds depth.
            DrawDeterministicBubbles(
                graphics,
                spherePath,
                liquidBounds,
                liquidLevel,
                waveAmplitude,
                animationPhase,
                dpi,
                energyColor,
                darkSurface);
        }

        DrawPlanetSurfaceTexture(
            graphics,
            spherePath,
            sphereBounds,
            animationPhase,
            dpi,
            darkSurface,
            energyColor);

        // A transparent radial vignette and two restrained highlights make the flat fill
        // read as a glass sphere without adding visual noise or implying animation.
        using (var edgeShade = new PathGradientBrush(spherePath)
               {
                   CenterPoint = new PointF(
                       sphereBounds.Left + (sphereBounds.Width * 0.42F),
                       sphereBounds.Top + (sphereBounds.Height * 0.38F)),
                   CenterColor = Color.FromArgb(0, Color.Black),
                   SurroundColors = new[]
                   {
                       Color.FromArgb(darkSurface ? 32 : 20, Color.Black)
                   }
               })
        {
            graphics.FillEllipse(edgeShade, sphereBounds);
        }
        using (var glassWash = new LinearGradientBrush(
                   sphereBounds,
                   Color.FromArgb(darkSurface ? 27 : 39, Color.White),
                   Color.FromArgb(0, Color.White),
                   LinearGradientMode.ForwardDiagonal))
        {
            graphics.FillEllipse(glassWash, sphereBounds);
        }

        DrawStationaryGlassReflections(
            graphics,
            spherePath,
            sphereBounds,
            animationPhase,
            dpi,
            darkSurface,
            energyColor);

        var rimWidth = Math.Max(2F * dpi, diameter * 0.008F);
        using (var rimPen = new Pen(_trackColor, rimWidth))
        {
            graphics.DrawEllipse(rimPen, sphereBounds);
        }
        var innerRimBounds = RectangleF.Inflate(sphereBounds, -rimWidth * 1.15F, -rimWidth * 1.15F);
        using (var innerRimPen = new Pen(
                   Color.FromArgb(darkSurface ? 82 : 126,
                       QuotaDashboardDrawing.Blend(_trackColor, Color.White, 0.72F)),
                   Math.Max(0.7F * dpi, 1F)))
        {
            graphics.DrawEllipse(innerRimPen, innerRimBounds);
        }
        DrawPlanetAtmosphereRim(
            graphics,
            sphereBounds,
            animationPhase,
            dpi,
            darkSurface,
            energyColor);
        DrawPlanetRingHalf(
            graphics,
            sphereBounds,
            ringBounds,
            animationPhase,
            frontHalf: true,
            dpi,
            darkSurface,
            energyColor);
        using (var highlightPen = new Pen(
                   Color.FromArgb(darkSurface ? 112 : 156, Color.White),
                   Math.Max(1.05F * dpi, diameter * 0.006F))
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(
                highlightPen,
                RectangleF.Inflate(sphereBounds, -4.2F * dpi, -4.2F * dpi),
                198F,
                67F);
        }

        var valueBounds = new RectangleF(
            sphereBounds.Left + (sphereBounds.Width * 0.115F),
            sphereBounds.Top + (sphereBounds.Height * 0.26F),
            sphereBounds.Width * 0.77F,
            sphereBounds.Height * 0.25F);
        var valueText = _remainingPercent.HasValue
            ? _remainingPercent.Value.ToString("0.#", CultureInfo.InvariantCulture)
            : _placeholderText;
        var valueBackground = ResolveBackgroundAt(
            valueBounds.Top + (valueBounds.Height / 2F),
            liquidLevel,
            waveAmplitude,
            shellMiddle,
            QuotaDashboardDrawing.Blend(_LiquidIndigo, Color.White, 0.08F));
        var valueColor = ResolveReadableTextColor(
            valueBackground,
            _remainingPercent.HasValue ? _textColor : _mutedColor,
            4.2D);
        var maximumValueSize = Math.Clamp(
            (diameter / dpi) * 0.16F,
            Math.Max(18F, Font.Size * 1.9F),
            38F);
        if (_remainingPercent.HasValue)
        {
            DrawCenteredPercentage(
                graphics,
                valueText,
                valueBounds,
                valueColor,
                Math.Max(9F, Font.Size * 0.86F),
                maximumValueSize,
                dpi);
        }
        else
        {
            QuotaDashboardDrawing.DrawFittedText(
                graphics,
                valueText,
                Font,
                FontStyle.Bold,
                valueColor,
                valueBounds,
                StringAlignment.Center,
                StringAlignment.Center,
                Math.Max(7.2F, Font.Size * 0.74F),
                Math.Max(18F, maximumValueSize * 0.62F));
        }

        var captionBounds = new RectangleF(
            sphereBounds.Left + (sphereBounds.Width * 0.11F),
            sphereBounds.Top + (sphereBounds.Height * 0.56F),
            sphereBounds.Width * 0.78F,
            sphereBounds.Height * 0.20F);
        var caption = string.IsNullOrWhiteSpace(_caption)
            ? "剩余额度"
            : _caption;
        var captionBackground = ResolveBackgroundAt(
            captionBounds.Top + (captionBounds.Height / 2F),
            liquidLevel,
            waveAmplitude,
            shellBottom,
            QuotaDashboardDrawing.Blend(_LiquidViolet, Color.Black, 0.04F));
        var captionColor = ResolveReadableTextColor(
            captionBackground,
            _remainingPercent.HasValue ? _mutedColor : Color.FromArgb(205, _mutedColor),
            3.15D);
        if (_offlineCaptionVisible)
        {
            QuotaDashboardDrawing.DrawFittedText(
                graphics,
                caption,
                Font,
                FontStyle.Regular,
                captionColor,
                captionBounds,
                StringAlignment.Center,
                StringAlignment.Center,
                Math.Max(5.2F, Font.Size * 0.56F),
                Math.Clamp((diameter / dpi) * 0.065F, Font.Size * 0.9F, 14F),
                allowWrap: false);
        }
    }

    private Color ResolveOpaqueStageColor()
    {
        if (BackColor.A == byte.MaxValue)
        {
            return BackColor;
        }

        // Defensive fallback for callers built against older versions that still supplied a
        // transparent BackColor.  Never re-enable transparent-parent painting on an animation.
        return RelativeLuminance(_textColor) > RelativeLuminance(_trackColor)
            ? Color.FromArgb(23, 31, 55)
            : Color.FromArgb(247, 250, 252);
    }

    private void HandleWaveAnimationTick(object? sender, EventArgs e)
    {
        if (!ShouldAnimateWave())
        {
            StopWaveAnimation();
            return;
        }

        _wavePhase += WavePhaseStep;
        if (_wavePhase >= MathF.Tau)
        {
            _wavePhase -= MathF.Tau;
        }
        if (_lastAnimationBounds.IsEmpty)
        {
            Invalidate();
        }
        else
        {
            Invalidate(_lastAnimationBounds, invalidateChildren: false);
        }
    }

    private void AttachAnimationHost()
    {
        var host = FindForm();
        if (ReferenceEquals(host, _animationHostForm))
        {
            return;
        }

        DetachAnimationHost();
        _animationHostForm = host;
        if (_animationHostForm is null)
        {
            return;
        }

        _animationHostForm.Resize += HandleAnimationHostStateChanged;
        _animationHostForm.VisibleChanged += HandleAnimationHostStateChanged;
        _animationHostForm.Activated += HandleAnimationHostStateChanged;
        _animationHostForm.Deactivate += HandleAnimationHostStateChanged;
    }

    private void DetachAnimationHost()
    {
        if (_animationHostForm is null)
        {
            return;
        }

        _animationHostForm.Resize -= HandleAnimationHostStateChanged;
        _animationHostForm.VisibleChanged -= HandleAnimationHostStateChanged;
        _animationHostForm.Activated -= HandleAnimationHostStateChanged;
        _animationHostForm.Deactivate -= HandleAnimationHostStateChanged;
        _animationHostForm = null;
    }

    private void HandleAnimationHostStateChanged(object? sender, EventArgs e)
    {
        UpdateWaveAnimationState();
    }

    private void UpdateWaveAnimationState()
    {
        if (ShouldAnimateWave())
        {
            if (!_waveAnimationTimer.Enabled)
            {
                _waveAnimationTimer.Start();
            }
            return;
        }

        StopWaveAnimation();
    }

    private bool ShouldAnimateWave()
    {
        return !IsDisposed &&
               !Disposing &&
               IsHandleCreated &&
               Visible &&
               !ControlViewport.HasActiveScrollAncestor(this) &&
               ControlViewport.IsInsideScrollableViewport(this) &&
               _remainingPercent.HasValue &&
               _animationHostForm is { Visible: true, WindowState: not FormWindowState.Minimized };
    }

    private void StopWaveAnimation()
    {
        if (_waveAnimationTimer.Enabled)
        {
            _waveAnimationTimer.Stop();
        }
    }

    private static PlanetGeometry CalculatePlanetGeometry(RectangleF content, float dpi)
    {
        var safeDpi = Math.Max(1F, dpi);
        var shortestSide = Math.Min(content.Width, content.Height);
        var outerReserve = Math.Max(8F * safeDpi, shortestSide * 0.048F);
        var heightLimitedDiameter = Math.Max(
            54F * safeDpi,
            shortestSide - (outerReserve * 2F));
        var widthLimitedDiameter = Math.Max(
            54F * safeDpi,
            (content.Width - (8F * safeDpi)) / PlanetRingWidthRatio);
        var diameter = Math.Min(heightLimitedDiameter, widthLimitedDiameter);
        diameter = Math.Min(diameter, shortestSide);

        var center = new PointF(
            content.Left + (content.Width / 2F),
            content.Top + (content.Height / 2F));
        var sphereBounds = new RectangleF(
            center.X - (diameter / 2F),
            center.Y - (diameter / 2F),
            diameter,
            diameter);
        var ringBounds = new RectangleF(
            center.X - ((diameter * PlanetRingWidthRatio) / 2F),
            center.Y - ((diameter * PlanetRingHeightRatio) / 2F) + (diameter * 0.035F),
            diameter * PlanetRingWidthRatio,
            diameter * PlanetRingHeightRatio);

        var angle = MathF.Abs(PlanetRingRotationDegrees) * MathF.PI / 180F;
        var ringHalfWidth = ringBounds.Width / 2F;
        var ringHalfHeight = ringBounds.Height / 2F;
        var rotatedHalfWidth = (MathF.Cos(angle) * ringHalfWidth) +
                               (MathF.Sin(angle) * ringHalfHeight);
        var rotatedHalfHeight = (MathF.Sin(angle) * ringHalfWidth) +
                                (MathF.Cos(angle) * ringHalfHeight);
        var ringCenter = new PointF(
            ringBounds.Left + ringHalfWidth,
            ringBounds.Top + ringHalfHeight);
        var rotatedRingBounds = new RectangleF(
            ringCenter.X - rotatedHalfWidth,
            ringCenter.Y - rotatedHalfHeight,
            rotatedHalfWidth * 2F,
            rotatedHalfHeight * 2F);
        var animationBounds = RectangleF.Union(
            RectangleF.Inflate(sphereBounds, 12F * safeDpi, 12F * safeDpi),
            RectangleF.Inflate(rotatedRingBounds, 8F * safeDpi, 8F * safeDpi));
        return new PlanetGeometry(
            sphereBounds,
            ringBounds,
            Rectangle.Ceiling(animationBounds));
    }

    internal static void ValidatePlanetGeometryForOfflineRendering()
    {
        var geometry = CalculatePlanetGeometry(new RectangleF(5F, 5F, 310F, 210F), 1F);
        if (geometry.SphereBounds.Width <= 0F ||
            Math.Abs(geometry.SphereBounds.Width - geometry.SphereBounds.Height) > 0.01F ||
            geometry.RingBounds.Width < geometry.SphereBounds.Width * 1.40F ||
            geometry.RingBounds.Height >= geometry.SphereBounds.Height * 0.50F ||
            !geometry.AnimationBounds.Contains(Rectangle.Ceiling(geometry.SphereBounds)) ||
            WaveAnimationIntervalMilliseconds < 40 ||
            BubbleSpecs.Length > 6 ||
            StarDustSpecs.Length > 8 ||
            BubbleSpecs.Any(spec =>
                Math.Abs(spec.RiseSpeed - MathF.Round(spec.RiseSpeed)) > 0.0001F ||
                Math.Abs(spec.SwaySpeed - MathF.Round(spec.SwaySpeed)) > 0.0001F))
        {
            throw new InvalidOperationException(
                "Passive quota planet geometry or low-cost animation budget is invalid.");
        }

        ValidateStationaryGlassReflectionBoundsForOfflineRendering();
    }

    private static void ValidateStationaryGlassReflectionBoundsForOfflineRendering()
    {
        // A transient layout can briefly make the gauge smaller than its preferred size.
        // At 400% DPI the narrow prism highlight then collapses after its 10.5-DIP inset.
        // Exercise that exact geometry against a real GDI+ surface so a future refactor
        // cannot feed a negative RectangleF back into Graphics.DrawArc.
        var compactSphere = new RectangleF(28F, 28F, 70F, 70F);
        var collapsedPrismArc = RectangleF.Inflate(compactSphere, -10.5F * 4F, -10.5F * 4F);
        if (IsRenderableRectangle(collapsedPrismArc))
        {
            throw new InvalidOperationException(
                "The high-DPI glass-reflection validation must retain a collapsed arc case.");
        }

        using var bitmap = new Bitmap(128, 128);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var spherePath = new GraphicsPath();
        spherePath.AddEllipse(compactSphere);
        DrawStationaryGlassReflections(
            graphics,
            spherePath,
            compactSphere,
            MathF.Tau * 0.31F,
            4F,
            darkSurface: false,
            _EnergyBlue);

        // Non-finite scale input is normalized, while a non-finite surface is rejected
        // before any native Brush, Pen, Path or clipping object is created.
        DrawStationaryGlassReflections(
            graphics,
            spherePath,
            compactSphere,
            0F,
            float.NaN,
            darkSurface: true,
            _EnergyViolet);
        DrawStationaryGlassReflections(
            graphics,
            spherePath,
            new RectangleF(float.NaN, 0F, 70F, 70F),
            0F,
            4F,
            darkSurface: false,
            _EnergyBlue);
    }

    internal static (RectangleF SphereBounds, RectangleF RingBounds)
        GetPlanetGeometryForOfflineRendering(Size size, float dpi)
    {
        var safeDpi = Math.Max(1F, dpi);
        var inset = Math.Max(3F, 5F * safeDpi);
        var content = RectangleF.Inflate(
            new RectangleF(PointF.Empty, size),
            -inset,
            -inset);
        var geometry = CalculatePlanetGeometry(content, safeDpi);
        return (geometry.SphereBounds, geometry.RingBounds);
    }

    private static void DrawPlanetStarDust(
        Graphics graphics,
        RectangleF sphereBounds,
        float phase,
        float dpi,
        bool darkSurface,
        Color energyColor)
    {
        var center = new PointF(
            sphereBounds.Left + (sphereBounds.Width / 2F),
            sphereBounds.Top + (sphereBounds.Height / 2F));
        var safePhase = NormalizePhase(phase);
        using var glowBrush = new SolidBrush(Color.Transparent);
        using var coreBrush = new SolidBrush(Color.Transparent);
        for (var index = 0; index < StarDustSpecs.Length; index++)
        {
            var dust = StarDustSpecs[index];
            var baseAngle = dust.AngleDegrees * MathF.PI / 180F;
            var periodicDrift = MathF.Sin(safePhase + (index * 0.91F));
            var angle = baseAngle + (periodicDrift * dust.DriftSpeed * 0.10F);
            var distance = sphereBounds.Width *
                           (dust.DistanceRatio + (MathF.Cos(safePhase + index) * 0.012F));
            var point = new PointF(
                center.X + (MathF.Cos(angle) * distance),
                center.Y + (MathF.Sin(angle) * distance));
            var radius = Math.Max(0.65F * dpi, dust.RadiusRatio * 1.05F * dpi);
            var pulse = 0.68F +
                        (0.32F * ((MathF.Sin(safePhase + (index * 1.27F)) + 1F) / 2F));
            var dustColor = index % 3 == 0
                ? QuotaDashboardDrawing.Blend(_AtmosphereViolet, Color.White, 0.36F)
                : QuotaDashboardDrawing.Blend(energyColor, Color.White, 0.50F);
            glowBrush.Color = Color.FromArgb(
                (int)((darkSurface ? 52F : 36F) * pulse),
                dustColor);
            coreBrush.Color = Color.FromArgb(
                (int)((darkSurface ? 220F : 184F) * pulse),
                Color.White);
            graphics.FillEllipse(
                glowBrush,
                point.X - (radius * 2.6F),
                point.Y - (radius * 2.6F),
                radius * 5.2F,
                radius * 5.2F);
            graphics.FillEllipse(
                coreBrush,
                point.X - (radius * 0.55F),
                point.Y - (radius * 0.55F),
                radius * 1.1F,
                radius * 1.1F);
        }
    }

    private static void DrawPlanetRingHalf(
        Graphics graphics,
        RectangleF sphereBounds,
        RectangleF ringBounds,
        float phase,
        bool frontHalf,
        float dpi,
        bool darkSurface,
        Color energyColor)
    {
        var center = new PointF(
            sphereBounds.Left + (sphereBounds.Width / 2F),
            sphereBounds.Top + (sphereBounds.Height / 2F));
        var state = graphics.Save();
        graphics.TranslateTransform(center.X, center.Y);
        graphics.RotateTransform(PlanetRingRotationDegrees);
        graphics.TranslateTransform(-center.X, -center.Y);

        var startAngle = frontHalf ? 0F : 180F;
        var alphaScale = frontHalf ? 1F : 0.70F;
        var ringColor = QuotaDashboardDrawing.Blend(_AtmosphereBlue, _AtmosphereViolet, 0.48F);
        using (var haloPen = new Pen(
                   Color.FromArgb(
                       (int)((darkSurface ? 54F : 38F) * alphaScale),
                       energyColor),
                   Math.Max(5F * dpi, sphereBounds.Width * 0.070F))
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(haloPen, ringBounds, startAngle, 180F);
        }
        using (var bodyPen = new Pen(
                   Color.FromArgb(
                       (int)((darkSurface ? 205F : 178F) * alphaScale),
                       ringColor),
                   Math.Max(2.1F * dpi, sphereBounds.Width * 0.032F))
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(bodyPen, ringBounds, startAngle, 180F);
        }
        using (var corePen = new Pen(
                   Color.FromArgb(
                       (int)((darkSurface ? 232F : 210F) * alphaScale),
                       QuotaDashboardDrawing.Blend(ringColor, Color.White, 0.58F)),
                   Math.Max(0.85F * dpi, sphereBounds.Width * 0.007F))
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(corePen, ringBounds, startAngle, 180F);
        }

        var innerBandBounds = RectangleF.Inflate(
            ringBounds,
            -Math.Max(2F * dpi, sphereBounds.Width * 0.022F),
            -Math.Max(0.8F * dpi, sphereBounds.Width * 0.008F));
        using (var innerBand = new Pen(
                   Color.FromArgb(
                       (int)((darkSurface ? 116F : 94F) * alphaScale),
                       _AtmosphereViolet),
                   Math.Max(0.65F * dpi, 0.9F))
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(innerBand, innerBandBounds, startAngle, 180F);
        }

        if (frontHalf)
        {
            // One slow, periodic glint moves only across the foreground half. Sinusoidal
            // motion returns to the same point and velocity at the phase boundary, so the
            // complete animation cycle has no wraparound jump.
            var glintAngle = (90F + (MathF.Sin(NormalizePhase(phase)) * 70F)) *
                             MathF.PI / 180F;
            var ringCenter = new PointF(
                ringBounds.Left + (ringBounds.Width / 2F),
                ringBounds.Top + (ringBounds.Height / 2F));
            var glint = new PointF(
                ringCenter.X + (MathF.Cos(glintAngle) * ringBounds.Width / 2F),
                ringCenter.Y + (MathF.Sin(glintAngle) * ringBounds.Height / 2F));
            var glintRadius = Math.Max(1.1F * dpi, sphereBounds.Width * 0.007F);
            using var glintGlow = new SolidBrush(Color.FromArgb(
                darkSurface ? 76 : 54,
                energyColor));
            using var glintCore = new SolidBrush(Color.FromArgb(228, Color.White));
            graphics.FillEllipse(
                glintGlow,
                glint.X - (glintRadius * 2.4F),
                glint.Y - (glintRadius * 2.4F),
                glintRadius * 4.8F,
                glintRadius * 4.8F);
            graphics.FillEllipse(
                glintCore,
                glint.X - (glintRadius * 0.55F),
                glint.Y - (glintRadius * 0.55F),
                glintRadius * 1.1F,
                glintRadius * 1.1F);
        }
        graphics.Restore(state);
    }

    private static void DrawAmbientEnergyHalo(
        Graphics graphics,
        RectangleF sphereBounds,
        float phase,
        float dpi,
        bool darkSurface,
        Color energyColor)
    {
        var pulse = 0.72F + (0.28F * ((MathF.Sin(phase - 0.45F) + 1F) / 2F));
        var haloBounds = RectangleF.Inflate(sphereBounds, 6.2F * dpi, 6.2F * dpi);
        using (var haloPath = new GraphicsPath())
        {
            haloPath.AddEllipse(haloBounds);
            using var haloBrush = new PathGradientBrush(haloPath)
            {
                CenterPoint = new PointF(
                    haloBounds.Left + (haloBounds.Width * 0.5F),
                    haloBounds.Top + (haloBounds.Height * 0.52F)),
                CenterColor = Color.FromArgb(
                    (int)((darkSurface ? 30F : 19F) * pulse),
                    energyColor),
                SurroundColors = new[] { Color.FromArgb(0, energyColor) }
            };
            graphics.FillEllipse(haloBrush, haloBounds);
        }

        using (var broadGlow = new Pen(
                   Color.FromArgb((int)((darkSurface ? 42F : 26F) * pulse), energyColor),
                   Math.Max(3.4F * dpi, sphereBounds.Width * 0.025F)))
        {
            graphics.DrawEllipse(broadGlow, RectangleF.Inflate(sphereBounds, 2.7F * dpi, 2.7F * dpi));
        }
        using var fineGlow = new Pen(
            Color.FromArgb((int)((darkSurface ? 104F : 72F) * pulse), energyColor),
            Math.Max(0.7F * dpi, 1F));
        graphics.DrawEllipse(fineGlow, RectangleF.Inflate(sphereBounds, 3.7F * dpi, 3.7F * dpi));
    }

    private static void DrawDeterministicBubbles(
        Graphics graphics,
        GraphicsPath spherePath,
        RectangleF liquidBounds,
        float liquidLevel,
        float waveAmplitude,
        float phase,
        float dpi,
        Color energyColor,
        bool darkSurface)
    {
        var liquidDepth = liquidBounds.Bottom - liquidLevel;
        if (liquidDepth < 7F * dpi)
        {
            return;
        }

        var phaseProgress = NormalizePhase(phase) / MathF.Tau;
        var state = graphics.Save();
        graphics.SetClip(spherePath, CombineMode.Intersect);
        foreach (var bubble in BubbleSpecs)
        {
            var radius = Math.Clamp(
                liquidBounds.Width * bubble.RadiusRatio,
                0.85F * dpi,
                3.5F * dpi);
            var top = liquidLevel + waveAmplitude + (radius * 1.6F);
            var bottom = liquidBounds.Bottom - (radius * 1.8F);
            if (bottom <= top)
            {
                continue;
            }

            var progress = Fraction(
                bubble.StartProgress + (phaseProgress * bubble.RiseSpeed));
            var sway = MathF.Sin(
                (phase * bubble.SwaySpeed) + (bubble.StartProgress * MathF.Tau));
            var centerX = liquidBounds.Left +
                          (liquidBounds.Width * bubble.HorizontalPosition) +
                          (sway * liquidBounds.Width * 0.018F);
            var centerY = bottom - ((bottom - top) * progress);
            var bubbleBounds = new RectangleF(
                centerX - radius,
                centerY - radius,
                radius * 2F,
                radius * 2F);
            var fade = Math.Clamp(
                Math.Min(progress / 0.14F, (1F - progress) / 0.18F),
                0F,
                1F);

            using (var glowBrush = new SolidBrush(Color.FromArgb(
                       (int)((darkSurface ? 42F : 30F) * fade),
                       energyColor)))
            {
                graphics.FillEllipse(
                    glowBrush,
                    RectangleF.Inflate(bubbleBounds, radius * 0.55F, radius * 0.55F));
            }
            using (var bubblePen = new Pen(
                       Color.FromArgb((int)((darkSurface ? 134F : 105F) * fade), Color.White),
                       Math.Max(0.55F * dpi, 0.8F)))
            {
                graphics.DrawEllipse(bubblePen, bubbleBounds);
            }
            var sparkleSize = Math.Max(0.7F * dpi, radius * 0.5F);
            using var sparkleBrush = new SolidBrush(Color.FromArgb(
                (int)((darkSurface ? 174F : 146F) * fade),
                Color.White));
            graphics.FillEllipse(
                sparkleBrush,
                centerX - (radius * 0.48F),
                centerY - (radius * 0.5F),
                sparkleSize,
                sparkleSize);
        }
        graphics.Restore(state);
    }

    private static void DrawStationaryGlassReflections(
        Graphics graphics,
        GraphicsPath spherePath,
        RectangleF sphereBounds,
        float phase,
        float dpi,
        bool darkSurface,
        Color energyColor)
    {
        if (!IsRenderableRectangle(sphereBounds))
        {
            return;
        }

        var safeDpi = float.IsFinite(dpi) && dpi > 0F ? dpi : 1F;
        // A full-width moving light sheet looked like a scanner passing over the gauge.
        // Keep the glass premium without moving a large object across the content: the
        // reflections stay anchored to the shell and only breathe very gently in opacity.
        // Liquid waves, bubbles and the outer orbit remain the readable motion cues.
        var pulse = 0.88F +
                    (0.12F * ((MathF.Sin(NormalizePhase(phase) + 0.7F) + 1F) / 2F));
        var state = graphics.Save();
        graphics.SetClip(spherePath, CombineMode.Intersect);

        var prismBounds = new RectangleF(
            sphereBounds.Left + (sphereBounds.Width * 0.07F),
            sphereBounds.Top + (sphereBounds.Height * 0.06F),
            sphereBounds.Width * 0.45F,
            sphereBounds.Height * 0.31F);
        using (var prismPath = new GraphicsPath())
        {
            prismPath.AddEllipse(prismBounds);
            using var prismBrush = new PathGradientBrush(prismPath)
            {
                CenterPoint = new PointF(
                    prismBounds.Left + (prismBounds.Width * 0.33F),
                    prismBounds.Top + (prismBounds.Height * 0.34F)),
                CenterColor = Color.FromArgb(
                    (int)((darkSurface ? 26F : 34F) * pulse),
                    Color.White),
                SurroundColors = new[] { Color.FromArgb(0, energyColor) }
            };
            graphics.FillPath(prismBrush, prismPath);
        }

        var reflectionBounds = new RectangleF(
            sphereBounds.Left + (sphereBounds.Width * 0.18F),
            sphereBounds.Top + (sphereBounds.Height * 0.15F),
            sphereBounds.Width * 0.18F,
            sphereBounds.Height * 0.058F);
        using (var reflectionBrush = new LinearGradientBrush(
                   reflectionBounds,
                   Color.FromArgb(
                       (int)((darkSurface ? 74F : 92F) * pulse),
                       Color.White),
                   Color.FromArgb(4, Color.White),
                   LinearGradientMode.Horizontal))
        {
            graphics.FillEllipse(reflectionBrush, reflectionBounds);
        }
        var hotspotRadius = Math.Max(1.2F * safeDpi, sphereBounds.Width * 0.008F);
        var hotspot = new PointF(
            reflectionBounds.Left + (reflectionBounds.Width * 0.16F),
            reflectionBounds.Top + (reflectionBounds.Height * 0.38F));
        using (var hotspotGlow = new SolidBrush(Color.FromArgb(
                   (int)((darkSurface ? 58F : 72F) * pulse),
                   Color.White)))
        {
            graphics.FillEllipse(
                hotspotGlow,
                hotspot.X - (hotspotRadius * 2F),
                hotspot.Y - (hotspotRadius * 2F),
                hotspotRadius * 4F,
                hotspotRadius * 4F);
        }
        using (var hotspotCore = new SolidBrush(Color.FromArgb(224, Color.White)))
        {
            graphics.FillEllipse(
                hotspotCore,
                hotspot.X - (hotspotRadius * 0.5F),
                hotspot.Y - (hotspotRadius * 0.5F),
                hotspotRadius,
                hotspotRadius);
        }
        var glassArcBounds = RectangleF.Inflate(
            sphereBounds,
            -7F * safeDpi,
            -7F * safeDpi);
        if (IsRenderableRectangle(glassArcBounds))
        {
            using var glassArc = new Pen(
                Color.FromArgb(
                    (int)((darkSurface ? 82F : 104F) * pulse),
                    Color.White),
                Math.Max(1.2F * safeDpi, sphereBounds.Width * 0.009F))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawArc(glassArc, glassArcBounds, 205F, 52F);
        }

        var prismArcBounds = RectangleF.Inflate(
            sphereBounds,
            -10.5F * safeDpi,
            -10.5F * safeDpi);
        if (IsRenderableRectangle(prismArcBounds))
        {
            using var prismArc = new Pen(
                Color.FromArgb(
                    (int)((darkSurface ? 54F : 66F) * pulse),
                    QuotaDashboardDrawing.Blend(energyColor, Color.White, 0.62F)),
                Math.Max(0.75F * safeDpi, sphereBounds.Width * 0.0045F))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawArc(prismArc, prismArcBounds, 282F, 34F);
        }
        graphics.Restore(state);
    }

    private static bool IsRenderableRectangle(RectangleF bounds) =>
        float.IsFinite(bounds.X) &&
        float.IsFinite(bounds.Y) &&
        float.IsFinite(bounds.Width) &&
        float.IsFinite(bounds.Height) &&
        bounds.Width > 1F &&
        bounds.Height > 1F;

    private static void DrawPlanetSurfaceTexture(
        Graphics graphics,
        GraphicsPath spherePath,
        RectangleF sphereBounds,
        float phase,
        float dpi,
        bool darkSurface,
        Color energyColor)
    {
        var safePhase = NormalizePhase(phase);
        var driftX = MathF.Sin(safePhase) * sphereBounds.Width * 0.012F;
        var driftY = MathF.Cos(safePhase) * sphereBounds.Height * 0.006F;
        var pulse = 0.76F +
                    (0.24F * ((MathF.Sin(safePhase + 0.45F) + 1F) / 2F));
        var state = graphics.Save();
        graphics.SetClip(spherePath, CombineMode.Intersect);

        using var cloudGlow = new SolidBrush(Color.FromArgb(
            (int)((darkSurface ? 26F : 20F) * pulse),
            QuotaDashboardDrawing.Blend(_AtmosphereBlue, Color.White, 0.46F)));
        using var violetCloud = new SolidBrush(Color.FromArgb(
            (int)((darkSurface ? 30F : 22F) * pulse),
            QuotaDashboardDrawing.Blend(_AtmosphereViolet, Color.White, 0.28F)));
        RectangleF[] cloudPatches =
        [
            new(
                sphereBounds.Left + (sphereBounds.Width * 0.10F) + driftX,
                sphereBounds.Top + (sphereBounds.Height * 0.25F) + driftY,
                sphereBounds.Width * 0.34F,
                sphereBounds.Height * 0.105F),
            new(
                sphereBounds.Left + (sphereBounds.Width * 0.56F) - (driftX * 0.7F),
                sphereBounds.Top + (sphereBounds.Height * 0.38F) - driftY,
                sphereBounds.Width * 0.27F,
                sphereBounds.Height * 0.082F),
            new(
                sphereBounds.Left + (sphereBounds.Width * 0.18F) - (driftX * 0.45F),
                sphereBounds.Top + (sphereBounds.Height * 0.69F) + (driftY * 0.8F),
                sphereBounds.Width * 0.24F,
                sphereBounds.Height * 0.074F),
            new(
                sphereBounds.Left + (sphereBounds.Width * 0.63F) + (driftX * 0.55F),
                sphereBounds.Top + (sphereBounds.Height * 0.76F) - (driftY * 0.6F),
                sphereBounds.Width * 0.18F,
                sphereBounds.Height * 0.060F)
        ];
        for (var index = 0; index < cloudPatches.Length; index++)
        {
            graphics.FillEllipse(index % 2 == 0 ? cloudGlow : violetCloud, cloudPatches[index]);
        }

        using (var texturePen = new Pen(
                   Color.FromArgb(
                       (int)((darkSurface ? 48F : 36F) * pulse),
                       QuotaDashboardDrawing.Blend(energyColor, Color.White, 0.34F)),
                   Math.Max(0.65F * dpi, sphereBounds.Width * 0.004F))
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(
                texturePen,
                new RectangleF(
                    sphereBounds.Left + (sphereBounds.Width * 0.14F) + driftX,
                    sphereBounds.Top + (sphereBounds.Height * 0.18F),
                    sphereBounds.Width * 0.48F,
                    sphereBounds.Height * 0.31F),
                204F,
                61F);
            graphics.DrawArc(
                texturePen,
                new RectangleF(
                    sphereBounds.Left + (sphereBounds.Width * 0.46F) - driftX,
                    sphereBounds.Top + (sphereBounds.Height * 0.57F),
                    sphereBounds.Width * 0.36F,
                    sphereBounds.Height * 0.24F),
                18F,
                72F);
        }

        graphics.Restore(state);
    }

    private static void DrawPlanetAtmosphereRim(
        Graphics graphics,
        RectangleF sphereBounds,
        float phase,
        float dpi,
        bool darkSurface,
        Color energyColor)
    {
        var pulse = 0.72F +
                    (0.28F * ((MathF.Sin(NormalizePhase(phase) + 0.25F) + 1F) / 2F));
        var atmosphereBounds = RectangleF.Inflate(
            sphereBounds,
            2.2F * dpi,
            2.2F * dpi);
        using (var backlight = new Pen(
                   Color.FromArgb(
                       (int)((darkSurface ? 72F : 52F) * pulse),
                       QuotaDashboardDrawing.Blend(_AtmosphereBlue, energyColor, 0.44F)),
                   Math.Max(4.2F * dpi, sphereBounds.Width * 0.026F))
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(backlight, atmosphereBounds, 276F, 128F);
        }
        using (var violetBacklight = new Pen(
                   Color.FromArgb(
                       (int)((darkSurface ? 48F : 34F) * pulse),
                       _AtmosphereViolet),
                   Math.Max(3F * dpi, sphereBounds.Width * 0.017F))
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(violetBacklight, atmosphereBounds, 105F, 74F);
        }
        using var rimCore = new Pen(
            Color.FromArgb(
                (int)((darkSurface ? 214F : 176F) * pulse),
                QuotaDashboardDrawing.Blend(_AtmosphereBlue, Color.White, 0.62F)),
            Math.Max(0.8F * dpi, 1F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawArc(rimCore, atmosphereBounds, 284F, 112F);
    }

    private static float NormalizePhase(float phase)
    {
        if (!float.IsFinite(phase))
        {
            return 0F;
        }
        var normalized = phase % MathF.Tau;
        return normalized < 0F ? normalized + MathF.Tau : normalized;
    }

    private static float Fraction(float value) =>
        value - MathF.Floor(value);

    private void DrawSphereShadow(
        Graphics graphics,
        RectangleF sphereBounds,
        float dpi,
        bool darkSurface)
    {
        var shadowBounds = RectangleF.Inflate(sphereBounds, 4.5F * dpi, 4.5F * dpi);
        shadowBounds.Offset(0F, 2.8F * dpi);
        using var shadowPath = new GraphicsPath();
        shadowPath.AddEllipse(shadowBounds);
        using var shadowBrush = new PathGradientBrush(shadowPath)
        {
            CenterPoint = new PointF(
                shadowBounds.Left + (shadowBounds.Width / 2F),
                shadowBounds.Top + (shadowBounds.Height * 0.58F)),
            CenterColor = darkSurface
                ? Color.FromArgb(28, _EnergyViolet)
                : Color.FromArgb(24, _LiquidDeep),
            SurroundColors = new[] { Color.FromArgb(0, Color.Black) }
        };
        graphics.FillEllipse(shadowBrush, shadowBounds);
    }

    private static GraphicsPath CreateWavePath(
        RectangleF bounds,
        float levelY,
        float amplitude,
        float phase,
        bool closeAtBottom,
        float primaryCycles = 1F,
        float secondaryCycles = 2F,
        float primaryWeight = 0.8F)
    {
        var path = new GraphicsPath();
        var left = bounds.Left - 1F;
        var right = bounds.Right + 1F;
        var width = right - left;
        const int segmentCount = 32;
        var points = new PointF[segmentCount + 1];
        for (var index = 0; index <= segmentCount; index++)
        {
            var progress = index / (float)segmentCount;
            var primaryWave = MathF.Sin(
                phase + (MathF.Tau * primaryCycles * progress));
            var secondaryWave = MathF.Sin(
                -phase +
                (MathF.Tau * secondaryCycles * progress) +
                0.65F);
            var clampedPrimaryWeight = Math.Clamp(primaryWeight, 0F, 1F);
            var offset = amplitude *
                         ((primaryWave * clampedPrimaryWeight) +
                          (secondaryWave * (1F - clampedPrimaryWeight)));
            points[index] = new PointF(
                left + (width * progress),
                levelY + offset);
        }

        path.StartFigure();
        path.AddLines(points);
        if (closeAtBottom)
        {
            var bottomRight = new PointF(right, bounds.Bottom + 2F);
            var bottomLeft = new PointF(left, bounds.Bottom + 2F);
            path.AddLine(points[^1], bottomRight);
            path.AddLine(bottomRight, bottomLeft);
            path.CloseFigure();
        }
        return path;
    }

    private static Color ResolveBackgroundAt(
        float y,
        float liquidLevel,
        float waveAmplitude,
        Color shellColor,
        Color liquidColor)
    {
        if (waveAmplitude <= 0.01F)
        {
            return y >= liquidLevel ? liquidColor : shellColor;
        }

        var mix = Math.Clamp(
            (y - (liquidLevel - waveAmplitude)) / (waveAmplitude * 2F),
            0F,
            1F);
        return QuotaDashboardDrawing.Blend(shellColor, liquidColor, mix);
    }

    private static Color ResolveReadableTextColor(
        Color background,
        Color preferred,
        double minimumContrast)
    {
        if (ContrastRatio(background, preferred) >= minimumContrast)
        {
            return preferred;
        }

        var darkInk = Color.FromArgb(15, 23, 42);
        var lightInk = Color.FromArgb(248, 250, 252);
        return ContrastRatio(background, darkInk) >= ContrastRatio(background, lightInk)
            ? darkInk
            : lightInk;
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05D) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05D);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte component)
        {
            var value = component / 255D;
            return value <= 0.04045D
                ? value / 12.92D
                : Math.Pow((value + 0.055D) / 1.055D, 2.4D);
        }

        return (0.2126D * Linearize(color.R)) +
               (0.7152D * Linearize(color.G)) +
               (0.0722D * Linearize(color.B));
    }

    private void DrawCenteredPercentage(
        Graphics graphics,
        string value,
        RectangleF bounds,
        Color color,
        float minimumPointSize,
        float maximumPointSize,
        float dpi)
    {
        Font? valueFont = null;
        Font? suffixFont = null;
        SizeF valueSize = SizeF.Empty;
        SizeF suffixSize = SizeF.Empty;
        var gap = Math.Max(1F * dpi, bounds.Width * 0.012F);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.MeasureTrailingSpaces |
                          StringFormatFlags.NoWrap
        };

        for (var pointSize = maximumPointSize;
             pointSize >= minimumPointSize;
             pointSize -= 0.5F)
        {
            var candidateValue = new Font(
                Font.FontFamily,
                pointSize,
                FontStyle.Bold,
                GraphicsUnit.Point);
            var candidateSuffix = new Font(
                Font.FontFamily,
                Math.Max(5F, pointSize * 0.46F),
                FontStyle.Bold,
                GraphicsUnit.Point);
            var candidateValueSize = graphics.MeasureString(value, candidateValue, PointF.Empty, format);
            var candidateSuffixSize = graphics.MeasureString("%", candidateSuffix, PointF.Empty, format);
            var totalWidth = candidateValueSize.Width + gap + candidateSuffixSize.Width;
            var totalHeight = Math.Max(
                candidateValueSize.Height,
                candidateSuffixSize.Height + (candidateValueSize.Height * 0.1F));
            if (totalWidth <= bounds.Width + 0.5F && totalHeight <= bounds.Height + 0.5F)
            {
                valueFont = candidateValue;
                suffixFont = candidateSuffix;
                valueSize = candidateValueSize;
                suffixSize = candidateSuffixSize;
                break;
            }

            candidateValue.Dispose();
            candidateSuffix.Dispose();
        }

        valueFont ??= new Font(
            Font.FontFamily,
            Math.Max(5F, minimumPointSize),
            FontStyle.Bold,
            GraphicsUnit.Point);
        suffixFont ??= new Font(
            Font.FontFamily,
            Math.Max(5F, minimumPointSize * 0.46F),
            FontStyle.Bold,
            GraphicsUnit.Point);
        if (valueSize.IsEmpty)
        {
            valueSize = graphics.MeasureString(value, valueFont, PointF.Empty, format);
            suffixSize = graphics.MeasureString("%", suffixFont, PointF.Empty, format);
        }

        using (valueFont)
        using (suffixFont)
        {
            var totalWidth = valueSize.Width + gap + suffixSize.Width;
            var valueLeft = bounds.Left + ((bounds.Width - totalWidth) / 2F);
            var valueTop = bounds.Top + ((bounds.Height - valueSize.Height) / 2F);
            var suffixLeft = valueLeft + valueSize.Width + gap;
            var suffixTop = valueTop + (valueSize.Height * 0.1F);
            var shadowColor = RelativeLuminance(color) > 0.5D
                ? Color.FromArgb(38, Color.Black)
                : Color.FromArgb(30, Color.White);
            var shadowOffset = Math.Max(0.6F * dpi, 0.75F);
            using (var shadowBrush = new SolidBrush(shadowColor))
            {
                graphics.DrawString(
                    value,
                    valueFont,
                    shadowBrush,
                    new PointF(valueLeft, valueTop + shadowOffset),
                    format);
                graphics.DrawString(
                    "%",
                    suffixFont,
                    shadowBrush,
                    new PointF(suffixLeft, suffixTop + shadowOffset),
                    format);
            }
            using var textBrush = new SolidBrush(color);
            graphics.DrawString(value, valueFont, textBrush, new PointF(valueLeft, valueTop), format);
            graphics.DrawString("%", suffixFont, textBrush, new PointF(suffixLeft, suffixTop), format);
        }
    }

    private void SetColor(ref Color field, Color value)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        Invalidate();
    }

    private void UpdateAccessibility()
    {
        var value = _remainingPercent.HasValue
            ? _remainingPercent.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : _placeholderText;
        AccessibleName = string.IsNullOrWhiteSpace(_caption) ? "额度仪表" : _caption;
        AccessibleDescription = string.IsNullOrWhiteSpace(_statusText)
            ? value
            : value + "，" + _statusText;
    }
}

/// <summary>
/// A passive trend chart for locally supplied usage samples. The headline is the cumulative
/// API-equivalent cost for the selected range, while the plot shows non-cumulative, per-bucket
/// model cost so it can rise and fall like a usage profile. Official remaining percentage uses
/// the right axis. No charting package or network is used.
/// </summary>
internal sealed class QuotaTrendChart : Control
{
    private QuotaChartSample[] _samples = [];
    private PassiveQuotaAssessmentWindow[] _assessmentWindows = [];
    private string _emptyText = "暂无历史用量，开始自然使用后将显示趋势";
    private Color _tokenColor = Color.FromArgb(59, 130, 246);
    private Color _tokenFillColor = Color.FromArgb(82, 96, 165, 250);
    private Color _modelSecondaryColor = Color.FromArgb(79, 70, 229);
    private Color _modelTertiaryColor = Color.FromArgb(139, 92, 246);
    private Color _modelAccentColor = Color.FromArgb(6, 182, 212);
    private Color _modelOtherColor = Color.FromArgb(148, 163, 184);
    private Color _remainingColor = Color.FromArgb(16, 185, 129);
    private Color _abnormalRemainingColor = Color.FromArgb(239, 68, 68);
    private Color _gridColor = Color.FromArgb(38, 100, 116, 139);
    private Color _textColor = Color.FromArgb(30, 41, 59);
    private Color _mutedColor = Color.FromArgb(100, 116, 139);
    private QuotaTrendMetric _metric = QuotaTrendMetric.ApiEquivalent;
    private int _hoveredSampleIndex = -1;
    private RectangleF _lastPlotBounds = RectangleF.Empty;
    private DateTimeOffset _lastPlotStart;
    private DateTimeOffset _lastPlotEnd;

    private sealed record ChartModelSeries(
        string Model,
        Color Color,
        double[] Values,
        double TotalCostUsd);

    private readonly record struct ModelTooltipLayout(
        int ExplicitModelRows,
        int HiddenModelRows,
        int DisplayRows,
        float Height);

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<QuotaChartSample> Samples
    {
        get => _samples;
        set
        {
            var normalized = value?
                .Where(sample => sample != null)
                .OrderBy(sample => sample.Timestamp)
                .ToArray() ?? [];
            if (SamplesEqual(_samples, normalized))
            {
                return;
            }

            var hoveredTimestamp = _hoveredSampleIndex >= 0 && _hoveredSampleIndex < _samples.Length
                ? _samples[_hoveredSampleIndex].Timestamp
                : (DateTimeOffset?)null;
            _samples = normalized;
            _hoveredSampleIndex = hoveredTimestamp.HasValue
                ? Array.FindIndex(_samples, sample => sample.Timestamp == hoveredTimestamp.Value)
                : -1;
            if (_hoveredSampleIndex >= _samples.Length)
            {
                _hoveredSampleIndex = -1;
            }
            UpdateAccessibility();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public QuotaTrendMetric Metric
    {
        get => _metric;
        set
        {
            if (_metric == value)
            {
                return;
            }

            _metric = value;
            UpdateAccessibility();
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<PassiveQuotaAssessmentWindow> AssessmentWindows
    {
        get => _assessmentWindows;
        set
        {
            var normalized = value?
                .Where(item =>
                    item != null &&
                    item.Status == PassiveQuotaStatus.Abnormal &&
                    item.ThroughUtc > item.FromUtc &&
                    double.IsFinite(item.EstimatedTotalUsd) &&
                    double.IsFinite(item.ThresholdUsd))
                .OrderBy(item => item.FromUtc)
                .ThenBy(item => item.ThroughUtc)
                .ToArray() ?? [];
            if (_assessmentWindows.SequenceEqual(normalized))
            {
                return;
            }

            _assessmentWindows = normalized;
            UpdateAccessibility();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyText
    {
        get => _emptyText;
        set
        {
            var normalized = value ?? string.Empty;
            if (_emptyText == normalized)
            {
                return;
            }

            _emptyText = normalized;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color CostColor
    {
        get => _tokenColor;
        set => SetColor(ref _tokenColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color CostFillColor
    {
        get => _tokenFillColor;
        set => SetColor(ref _tokenFillColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ModelSecondaryColor
    {
        get => _modelSecondaryColor;
        set => SetColor(ref _modelSecondaryColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ModelTertiaryColor
    {
        get => _modelTertiaryColor;
        set => SetColor(ref _modelTertiaryColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ModelAccentColor
    {
        get => _modelAccentColor;
        set => SetColor(ref _modelAccentColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ModelOtherColor
    {
        get => _modelOtherColor;
        set => SetColor(ref _modelOtherColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color RemainingColor
    {
        get => _remainingColor;
        set => SetColor(ref _remainingColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AbnormalRemainingColor
    {
        get => _abnormalRemainingColor;
        set => SetColor(ref _abnormalRemainingColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color GridColor
    {
        get => _gridColor;
        set => SetColor(ref _gridColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TextColor
    {
        get => _textColor;
        set => SetColor(ref _textColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color MutedColor
    {
        get => _mutedColor;
        set => SetColor(ref _mutedColor, value);
    }

    public QuotaTrendChart()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Size = new Size(760, 320);
        MinimumSize = new Size(160, 140);
        TabStop = false;
        Cursor = Cursors.Cross;
        UpdateAccessibility();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var nextIndex = -1;
        var hitBounds = RectangleF.Inflate(_lastPlotBounds, 8F, 12F);
        if (_samples.Length > 0 &&
            !_lastPlotBounds.IsEmpty &&
            hitBounds.Contains(e.Location) &&
            e.X >= _lastPlotBounds.Left &&
            e.X <= _lastPlotBounds.Right)
        {
            nextIndex = FindSampleIndexAtX(e.X);
        }

        if (_hoveredSampleIndex != nextIndex)
        {
            _hoveredSampleIndex = nextIndex;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredSampleIndex >= 0)
        {
            _hoveredSampleIndex = -1;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var dpi = Math.Max(1F, DeviceDpi / 96F);
        var outerInset = Math.Max(3F, 6F * dpi);
        var outer = RectangleF.Inflate(ClientRectangle, -outerInset, -outerInset);
        if (outer.Width < 70F || outer.Height < 70F)
        {
            _lastPlotBounds = RectangleF.Empty;
            DrawEmptyState(graphics, outer);
            return;
        }

        var modelSeries = BuildModelSeries();
        var compactSummary = outer.Height < (220F * dpi);
        var summaryHeight = compactSummary ? 34F * dpi : 58F * dpi;
        var legendEntryCount = modelSeries.Count + (_samples.Any(sample => sample.RemainingPercent.HasValue) ? 1 : 0);
        var legendColumns = GetLegendColumnCount(outer.Width, legendEntryCount, dpi);
        var legendRows = legendEntryCount == 0
            ? 0
            : (int)Math.Ceiling(legendEntryCount / (double)Math.Max(1, legendColumns));
        var legendHeight = legendRows == 0 ? 0F : (legendRows * 24F * dpi) + (6F * dpi);
        var headerHeight = summaryHeight;
        var bottomAxisHeight = outer.Height < (180F * dpi) ? 21F * dpi : 29F * dpi;
        var maximumBucketValue = QuotaDashboardDrawing.NiceMaximum(GetMaximumBucketValue());
        var axisLabel = FormatMetricAxis(maximumBucketValue, maximumBucketValue);
        var measuredAxis = graphics.MeasureString(axisLabel, Font);
        var leftAxisWidth = Math.Max(
            outer.Width < (280F * dpi) ? 39F * dpi : 48F * dpi,
            measuredAxis.Width + (12F * dpi));
        var rightAxisWidth = _samples.Any(sample => sample.RemainingPercent.HasValue)
            ? (outer.Width < (280F * dpi) ? 35F * dpi : 46F * dpi)
            : 8F * dpi;
        var plot = new RectangleF(
            outer.Left + leftAxisWidth,
            outer.Top + headerHeight + (3F * dpi),
            outer.Width - leftAxisWidth - rightAxisWidth,
            outer.Height - headerHeight - bottomAxisHeight - legendHeight - (8F * dpi));

        var totalCost = GetTotalCost();
        var totalTokens = GetTotalTokens();
        DrawCostSummary(
            graphics,
            new RectangleF(outer.Left, outer.Top, outer.Width, summaryHeight),
            totalCost,
            totalTokens,
            compactSummary,
            dpi);
        if (plot.Width < 32F || plot.Height < 28F)
        {
            _lastPlotBounds = RectangleF.Empty;
            DrawEmptyState(graphics, new RectangleF(
                outer.Left,
                outer.Top + headerHeight,
                outer.Width,
                Math.Max(18F, outer.Height - headerHeight)));
            return;
        }

        DrawUsageGridAndAxes(graphics, outer, plot, maximumBucketValue, dpi);

        if (_samples.Length == 0)
        {
            _lastPlotBounds = RectangleF.Empty;
            DrawEmptyState(graphics, plot);
            return;
        }

        var start = _samples[0].Timestamp;
        var end = GetSampleEnd(_samples[^1]);
        _lastPlotBounds = plot;
        _lastPlotStart = start;
        _lastPlotEnd = end;
        DrawStackedModelSeries(graphics, plot, start, end, maximumBucketValue, modelSeries, dpi);
        var remainingCurves = DrawRemainingSeries(graphics, plot, start, end, dpi);
        DrawAbnormalRemainingWindows(graphics, plot, start, end, remainingCurves, dpi);
        DrawTimeLabels(graphics, plot, start, end, dpi);
        DrawModelLegend(
            graphics,
            new RectangleF(
                outer.Left,
                plot.Bottom + bottomAxisHeight,
                outer.Width,
                legendHeight),
            modelSeries,
            legendColumns,
            dpi);
        DrawModelHoverOverlay(graphics, plot, start, end, maximumBucketValue, modelSeries, dpi);
    }

    private double GetTotalCost()
    {
        var total = 0D;
        foreach (var sample in _samples)
        {
            total += QuotaDashboardDrawing.SafeCost(sample.IncrementalCostUsd);
        }
        return total;
    }

    private long GetTotalTokens()
    {
        var total = 0L;
        foreach (var sample in _samples)
        {
            var tokens = Math.Max(0L, sample.TotalTokens);
            if (tokens > long.MaxValue - total)
            {
                return long.MaxValue;
            }
            total += tokens;
        }
        return total;
    }

    private double GetMaximumBucketValue()
    {
        var maximum = 0D;
        foreach (var sample in _samples)
        {
            maximum = Math.Max(maximum, GetSampleMetricValue(sample));
        }
        return maximum;
    }

    private double GetSampleMetricValue(QuotaChartSample sample) =>
        _metric == QuotaTrendMetric.Tokens
            ? Math.Max(0L, sample.TotalTokens)
            : QuotaDashboardDrawing.SafeCost(sample.IncrementalCostUsd);

    private double GetModelMetricValue(QuotaChartModelUsage usage) =>
        _metric == QuotaTrendMetric.Tokens
            ? Math.Max(0L, usage.TotalTokens)
            : QuotaDashboardDrawing.SafeCost(usage.ApiEquivalentCostUsd);

    private string FormatMetricAxis(double value, double maximum) =>
        _metric == QuotaTrendMetric.Tokens
            ? QuotaDashboardDrawing.FormatTokenAxis(value)
            : QuotaDashboardDrawing.FormatUsdAxis(value, maximum);

    private string FormatMetricValue(double value)
    {
        if (_metric != QuotaTrendMetric.Tokens)
        {
            return QuotaDashboardDrawing.FormatUsdValue(value);
        }

        var tokens = !double.IsFinite(value) || value <= 0D
            ? 0L
            : value >= long.MaxValue
                ? long.MaxValue
                : (long)Math.Round(value, MidpointRounding.AwayFromZero);
        return QuotaDashboardDrawing.FormatTokenValue(tokens) + " Token";
    }

    private IReadOnlyList<ChartModelSeries> BuildModelSeries()
    {
        if (_samples.Length == 0)
        {
            return [];
        }

        var normalizedPerSample = new Dictionary<string, double>[_samples.Length];
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var sampleIndex = 0; sampleIndex < _samples.Length; sampleIndex++)
        {
            var sample = _samples[sampleIndex];
            var expected = GetSampleMetricValue(sample);
            var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in sample.ModelUsage ?? [])
            {
                var model = NormalizeChartModel(item.Model);
                var value = GetModelMetricValue(item);
                if (value <= 0D)
                {
                    continue;
                }
                values[model] = values.GetValueOrDefault(model) + value;
            }

            var classified = values.Values.Sum();
            if (expected > 0D && classified <= 0D)
            {
                values["未识别模型"] = expected;
            }
            else if (expected > 0D && classified > 0D)
            {
                if (classified > expected + 0.000_000_001D)
                {
                    var scale = expected / classified;
                    foreach (var model in values.Keys.ToArray())
                    {
                        values[model] *= scale;
                    }
                }
                else if (classified + 0.000_000_001D < expected)
                {
                    values["未识别模型"] = values.GetValueOrDefault("未识别模型") +
                        (expected - classified);
                }
            }

            normalizedPerSample[sampleIndex] = values;
            foreach (var pair in values)
            {
                totals[pair.Key] = totals.GetValueOrDefault(pair.Key) + pair.Value;
            }
        }

        var orderedModels = totals
            .Where(pair => pair.Value > 0D)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => GetModelSortOrder(pair.Key))
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (orderedModels.Length == 0)
        {
            return [];
        }

        const int maximumNamedSeries = 4;
        var visibleModels = orderedModels
            .Take(maximumNamedSeries)
            .Select(pair => pair.Key)
            .ToArray();
        var visibleSet = new HashSet<string>(visibleModels, StringComparer.OrdinalIgnoreCase);
        var collapseOther = orderedModels.Length > maximumNamedSeries;
        var result = new List<ChartModelSeries>(visibleModels.Length + (collapseOther ? 1 : 0));
        foreach (var model in visibleModels)
        {
            var values = new double[_samples.Length];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = normalizedPerSample[index].GetValueOrDefault(model);
            }
            result.Add(new ChartModelSeries(model, GetModelColor(model), values, values.Sum()));
        }

        if (collapseOther)
        {
            var values = new double[_samples.Length];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = normalizedPerSample[index]
                    .Where(pair => !visibleSet.Contains(pair.Key))
                    .Sum(pair => pair.Value);
            }
            result.Add(new ChartModelSeries("其他模型", _modelOtherColor, values, values.Sum()));
        }
        return result;
    }

    private void DrawUsageGridAndAxes(
        Graphics graphics,
        RectangleF outer,
        RectangleF plot,
        double maximumCost,
        float dpi)
    {
        using var horizontalGrid = new Pen(Color.FromArgb(
            Math.Max(30, (int)_gridColor.A),
            _gridColor), Math.Max(1F, dpi))
        {
            DashStyle = DashStyle.Dash
        };
        using var verticalGrid = new Pen(Color.FromArgb(
            Math.Max(18, _gridColor.A / 2),
            _gridColor), Math.Max(1F, dpi))
        {
            DashStyle = DashStyle.Dot
        };
        var showRemainingAxis = _samples.Any(sample => sample.RemainingPercent.HasValue);
        const int horizontalSteps = 4;
        for (var index = 0; index <= horizontalSteps; index++)
        {
            var ratio = index / (float)horizontalSteps;
            var y = plot.Bottom - (plot.Height * ratio);
            graphics.DrawLine(horizontalGrid, plot.Left, y, plot.Right, y);
            var labelHeight = Math.Max(14F * dpi, Font.Height * 0.86F);
            QuotaDashboardDrawing.DrawFittedText(
                graphics,
                FormatMetricAxis(maximumCost * ratio, maximumCost),
                Font,
                FontStyle.Regular,
                _mutedColor,
                new RectangleF(
                    outer.Left,
                    y - (labelHeight / 2F),
                    Math.Max(16F, plot.Left - outer.Left - (6F * dpi)),
                    labelHeight),
                StringAlignment.Far,
                StringAlignment.Center,
                Math.Max(5.4F, Font.Size * 0.58F),
                Font.Size * 0.78F);
            if (showRemainingAxis)
            {
                QuotaDashboardDrawing.DrawFittedText(
                    graphics,
                    (ratio * 100D).ToString("0", CultureInfo.InvariantCulture) + "%",
                    Font,
                    FontStyle.Regular,
                    Color.FromArgb(Math.Min(210, (int)_mutedColor.A), _mutedColor),
                    new RectangleF(
                        plot.Right + (6F * dpi),
                        y - (labelHeight / 2F),
                        Math.Max(16F, outer.Right - plot.Right - (5F * dpi)),
                        labelHeight),
                    StringAlignment.Near,
                    StringAlignment.Center,
                    Math.Max(5.4F, Font.Size * 0.58F),
                    Font.Size * 0.76F);
            }
        }

        for (var index = 1; index < 4; index++)
        {
            var x = plot.Left + (plot.Width * index / 4F);
            graphics.DrawLine(verticalGrid, x, plot.Top, x, plot.Bottom);
        }
        using var baseline = new Pen(Color.FromArgb(Math.Max(58, (int)_gridColor.A), _gridColor), Math.Max(1F, dpi));
        graphics.DrawLine(baseline, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
    }

    private void DrawStackedModelSeries(
        Graphics graphics,
        RectangleF plot,
        DateTimeOffset start,
        DateTimeOffset end,
        double maximumCost,
        IReadOnlyList<ChartModelSeries> series,
        float dpi)
    {
        if (_samples.Length == 0 || series.Count == 0 || maximumCost <= 0D)
        {
            return;
        }

        var cumulative = new double[_samples.Length];
        foreach (var modelSeries in series)
        {
            var lower = new PointF[_samples.Length];
            var upper = new PointF[_samples.Length];
            var hasValue = false;
            for (var index = 0; index < _samples.Length; index++)
            {
                var x = MapX(GetSampleCenter(_samples[index]), index, _samples.Length, start, end, plot);
                lower[index] = new PointF(x, MapCostY(cumulative[index], maximumCost, plot));
                var value = QuotaDashboardDrawing.SafeCost(modelSeries.Values[index]);
                hasValue |= value > 0D;
                cumulative[index] += value;
                upper[index] = new PointF(x, MapCostY(cumulative[index], maximumCost, plot));
            }
            if (!hasValue)
            {
                continue;
            }

            var lowerCurve = QuotaDashboardDrawing.BuildClampedMonotoneCurve(lower, plot);
            var upperCurve = QuotaDashboardDrawing.BuildClampedMonotoneCurve(upper, plot);
            if (lowerCurve.Length == 0 || upperCurve.Length == 0)
            {
                continue;
            }
            var count = Math.Min(lowerCurve.Length, upperCurve.Length);
            for (var index = 0; index < count; index++)
            {
                upperCurve[index].Y = Math.Min(upperCurve[index].Y, lowerCurve[index].Y);
            }

            using var areaPath = new GraphicsPath();
            if (count == 1)
            {
                var width = Math.Max(3F * dpi, plot.Width / Math.Max(24F, _samples.Length * 2F));
                var band = RectangleF.FromLTRB(
                    upperCurve[0].X - width,
                    upperCurve[0].Y,
                    upperCurve[0].X + width,
                    lowerCurve[0].Y);
                using var singleFill = new SolidBrush(Color.FromArgb(118, modelSeries.Color));
                graphics.FillRectangle(singleFill, band);
                continue;
            }
            areaPath.AddLines(upperCurve.Take(count).ToArray());
            areaPath.AddLines(lowerCurve.Take(count).Reverse().ToArray());
            areaPath.CloseFigure();
            using var areaBrush = new LinearGradientBrush(
                plot,
                Color.FromArgb(146, modelSeries.Color),
                Color.FromArgb(30, modelSeries.Color),
                LinearGradientMode.Vertical);
            graphics.FillPath(areaBrush, areaPath);
            using var outline = new Pen(Color.FromArgb(226, modelSeries.Color), Math.Max(1.15F, 1.55F * dpi))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawLines(outline, upperCurve.Take(count).ToArray());
        }
    }

    private void DrawModelLegend(
        Graphics graphics,
        RectangleF bounds,
        IReadOnlyList<ChartModelSeries> series,
        int columns,
        float dpi)
    {
        var entries = series
            .Select(item => (item.Model, item.Color, false))
            .ToList();
        if (_samples.Any(sample => sample.RemainingPercent.HasValue))
        {
            entries.Add(("官方剩余百分比", _remainingColor, true));
        }
        if (entries.Count == 0 || bounds.Width <= 4F || bounds.Height <= 4F)
        {
            return;
        }

        columns = Math.Clamp(columns, 1, entries.Count);
        var rows = (int)Math.Ceiling(entries.Count / (double)columns);
        var rowHeight = bounds.Height / Math.Max(1, rows);
        var columnWidth = bounds.Width / columns;
        for (var index = 0; index < entries.Count; index++)
        {
            var row = index / columns;
            var column = index % columns;
            var cell = new RectangleF(
                bounds.Left + (column * columnWidth),
                bounds.Top + (row * rowHeight),
                columnWidth,
                rowHeight);
            var glyphLeft = cell.Left + (5F * dpi);
            var glyphCenterY = cell.Top + (cell.Height / 2F);
            if (entries[index].Item3)
            {
                using var pen = new Pen(entries[index].Color, Math.Max(1F, 1.4F * dpi))
                {
                    DashStyle = DashStyle.Dash,
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                graphics.DrawLine(
                    pen,
                    glyphLeft,
                    glyphCenterY,
                    glyphLeft + (18F * dpi),
                    glyphCenterY);
            }
            else
            {
                var dot = Math.Max(5F, 7F * dpi);
                using var brush = new SolidBrush(entries[index].Color);
                graphics.FillEllipse(
                    brush,
                    glyphLeft,
                    glyphCenterY - (dot / 2F),
                    dot,
                    dot);
            }
            QuotaDashboardDrawing.DrawFittedText(
                graphics,
                entries[index].Model,
                Font,
                FontStyle.Regular,
                _textColor,
                new RectangleF(
                    glyphLeft + (25F * dpi),
                    cell.Top,
                    Math.Max(10F, cell.Width - (30F * dpi)),
                    cell.Height),
                StringAlignment.Near,
                StringAlignment.Center,
                Math.Max(5.7F, Font.Size * 0.62F),
                Font.Size * 0.82F);
        }
    }

    private void DrawModelHoverOverlay(
        Graphics graphics,
        RectangleF plot,
        DateTimeOffset start,
        DateTimeOffset end,
        double maximumCost,
        IReadOnlyList<ChartModelSeries> series,
        float dpi)
    {
        if (_hoveredSampleIndex < 0 ||
            _hoveredSampleIndex >= _samples.Length ||
            maximumCost <= 0D)
        {
            return;
        }

        var sample = _samples[_hoveredSampleIndex];
        var abnormalWindow = FindAbnormalAssessmentWindow(
            sample.Timestamp,
            GetSampleEnd(sample));
        var total = GetSampleMetricValue(sample);
        var x = MapX(GetSampleCenter(sample), _hoveredSampleIndex, _samples.Length, start, end, plot);
        var y = Math.Clamp(MapCostY(total, maximumCost, plot), plot.Top, plot.Bottom);
        using (var guide = new Pen(Color.FromArgb(112, _tokenColor), Math.Max(1F, dpi)))
        {
            guide.DashStyle = DashStyle.Dash;
            graphics.DrawLine(guide, x, plot.Top, x, plot.Bottom);
        }
        var haloRadius = Math.Max(5F, 6F * dpi);
        using (var halo = new SolidBrush(Color.FromArgb(48, _tokenColor)))
        {
            graphics.FillEllipse(halo, x - haloRadius, y - haloRadius, haloRadius * 2F, haloRadius * 2F);
        }
        var pointRadius = Math.Max(2.7F, 3.3F * dpi);
        using (var point = new SolidBrush(_tokenColor))
        {
            graphics.FillEllipse(point, x - pointRadius, y - pointRadius, pointRadius * 2F, pointRadius * 2F);
        }

        var modelRows = series
            .Select(item => (item.Model, item.Color, Value: QuotaDashboardDrawing.SafeCost(item.Values[_hoveredSampleIndex])))
            .Where(item => item.Value > 0D)
            .OrderByDescending(item => item.Value)
            .ToList();
        var tooltipWidth = Math.Min(plot.Width - (12F * dpi), 340F * dpi);
        var tooltipLayout = CalculateModelTooltipLayout(
            plot.Height - (10F * dpi),
            dpi,
            modelRows.Count,
            sample.RemainingPercent.HasValue,
            abnormalWindow != null);
        var tooltipHeight = tooltipLayout.Height;
        if (tooltipWidth < 176F * dpi || tooltipLayout.DisplayRows == 0)
        {
            return;
        }
        var tooltipLeft = x + (15F * dpi);
        if (tooltipLeft + tooltipWidth > plot.Right - (4F * dpi))
        {
            tooltipLeft = x - tooltipWidth - (15F * dpi);
        }
        tooltipLeft = Math.Clamp(tooltipLeft, plot.Left + (4F * dpi), plot.Right - tooltipWidth - (4F * dpi));
        var tooltipTop = Math.Clamp(
            y - (tooltipHeight * 0.52F),
            plot.Top + (4F * dpi),
            plot.Bottom - tooltipHeight - (4F * dpi));
        var tooltip = new RectangleF(tooltipLeft, tooltipTop, tooltipWidth, tooltipHeight);
        var lightSurface = _textColor.GetBrightness() < 0.52F;
        var tooltipBack = lightSurface
            ? Color.FromArgb(248, 255, 255, 255)
            : Color.FromArgb(248, 15, 23, 42);
        var tooltipText = lightSurface ? Color.FromArgb(15, 23, 42) : Color.FromArgb(241, 245, 249);
        var tooltipMuted = lightSurface ? Color.FromArgb(71, 85, 105) : Color.FromArgb(186, 199, 219);
        var shadowBounds = tooltip;
        shadowBounds.Offset(0F, 3F * dpi);
        using (var shadowPath = UiDesign.CreateRoundedPath(shadowBounds, 14F * dpi))
        using (var shadow = new SolidBrush(Color.FromArgb(34, Color.Black)))
        using (var path = UiDesign.CreateRoundedPath(tooltip, 14F * dpi))
        using (var fill = new SolidBrush(tooltipBack))
        using (var border = new Pen(Color.FromArgb(126, _gridColor), Math.Max(1F, dpi)))
        {
            graphics.FillPath(shadow, shadowPath);
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
        }

        var left = tooltip.Left + (16F * dpi);
        var right = tooltip.Right - (16F * dpi);
        var contentWidth = right - left;
        var cursorY = tooltip.Top + (10F * dpi);
        QuotaDashboardDrawing.DrawFittedText(
            graphics,
            _metric == QuotaTrendMetric.Tokens
                ? $"{QuotaDashboardDrawing.FormatTokenValue(sample.TotalTokens)} Token"
                : QuotaDashboardDrawing.FormatUsdValue(total),
            Font,
            FontStyle.Bold,
            tooltipText,
            new RectangleF(left, cursorY, contentWidth, 25F * dpi),
            StringAlignment.Near,
            StringAlignment.Center,
            Math.Max(7F, Font.Size * 0.75F),
            Font.Size * 1.16F);
        cursorY += 25F * dpi;
        QuotaDashboardDrawing.DrawFittedText(
            graphics,
            FormatBucketInterval(sample.Timestamp, NormalizeBucketDuration(sample.BucketDuration)),
            Font,
            FontStyle.Regular,
            tooltipMuted,
            new RectangleF(left, cursorY, contentWidth, 22F * dpi),
            StringAlignment.Near,
            StringAlignment.Center,
            Math.Max(5.9F, Font.Size * 0.64F),
            Font.Size * 0.82F);
        cursorY += 28F * dpi;
        using (var divider = new Pen(Color.FromArgb(82, _gridColor), Math.Max(1F, dpi)))
        {
            graphics.DrawLine(divider, left, cursorY, right, cursorY);
        }
        cursorY += 5F * dpi;

        if (modelRows.Count == 0)
        {
            QuotaDashboardDrawing.DrawFittedText(
                graphics,
                "该时间段没有用量",
                Font,
                FontStyle.Regular,
                tooltipMuted,
                new RectangleF(left, cursorY, contentWidth, 23F * dpi),
                StringAlignment.Near,
                StringAlignment.Center,
                Math.Max(5.9F, Font.Size * 0.64F),
                Font.Size * 0.82F);
            cursorY += 23F * dpi;
        }
        else
        {
            foreach (var row in modelRows.Take(tooltipLayout.ExplicitModelRows))
            {
                var dot = 7F * dpi;
                using var dotBrush = new SolidBrush(row.Color);
                graphics.FillEllipse(dotBrush, left, cursorY + ((23F * dpi - dot) / 2F), dot, dot);
                QuotaDashboardDrawing.DrawFittedText(
                    graphics,
                    row.Model,
                    Font,
                    FontStyle.Regular,
                    tooltipText,
                    new RectangleF(left + (14F * dpi), cursorY, contentWidth * 0.64F, 23F * dpi),
                    StringAlignment.Near,
                    StringAlignment.Center,
                    Math.Max(5.8F, Font.Size * 0.62F),
                    Font.Size * 0.82F);
                QuotaDashboardDrawing.DrawFittedText(
                    graphics,
                    FormatMetricValue(row.Value),
                    Font,
                    FontStyle.Bold,
                    tooltipText,
                    new RectangleF(left + (contentWidth * 0.60F), cursorY, contentWidth * 0.40F, 23F * dpi),
                    StringAlignment.Far,
                    StringAlignment.Center,
                    Math.Max(5.8F, Font.Size * 0.62F),
                    Font.Size * 0.82F);
                cursorY += 23F * dpi;
            }

            if (tooltipLayout.HiddenModelRows > 0)
            {
                var hiddenRows = modelRows.Skip(tooltipLayout.ExplicitModelRows).ToArray();
                var hiddenValue = hiddenRows.Sum(row => row.Value);
                var dot = 7F * dpi;
                using var dotBrush = new SolidBrush(_modelOtherColor);
                graphics.FillEllipse(dotBrush, left, cursorY + ((23F * dpi - dot) / 2F), dot, dot);
                QuotaDashboardDrawing.DrawFittedText(
                    graphics,
                    $"其余 {tooltipLayout.HiddenModelRows} 个模型",
                    Font,
                    FontStyle.Regular,
                    tooltipText,
                    new RectangleF(left + (14F * dpi), cursorY, contentWidth * 0.64F, 23F * dpi),
                    StringAlignment.Near,
                    StringAlignment.Center,
                    Math.Max(5.8F, Font.Size * 0.62F),
                    Font.Size * 0.82F);
                QuotaDashboardDrawing.DrawFittedText(
                    graphics,
                    FormatMetricValue(hiddenValue),
                    Font,
                    FontStyle.Bold,
                    tooltipText,
                    new RectangleF(left + (contentWidth * 0.60F), cursorY, contentWidth * 0.40F, 23F * dpi),
                    StringAlignment.Far,
                    StringAlignment.Center,
                    Math.Max(5.8F, Font.Size * 0.62F),
                    Font.Size * 0.82F);
                cursorY += 23F * dpi;
            }
        }

        var duration = NormalizeBucketDuration(sample.BucketDuration);
        var hourlyRate = total / Math.Max(1D / 3_600D, duration.TotalHours);
        var rollingHourValue = GetRollingOneHourMetric(_hoveredSampleIndex);
        QuotaDashboardDrawing.DrawFittedText(
            graphics,
            $"速率 {FormatMetricValue(hourlyRate)} / 小时  ·  近 1 小时 {FormatMetricValue(rollingHourValue)}",
            Font,
            FontStyle.Regular,
            tooltipMuted,
            new RectangleF(left, cursorY + (3F * dpi), contentWidth, 21F * dpi),
            StringAlignment.Near,
            StringAlignment.Center,
            Math.Max(5.6F, Font.Size * 0.60F),
            Font.Size * 0.78F);
        cursorY += 21F * dpi;
        if (sample.RemainingPercent.HasValue)
        {
            QuotaDashboardDrawing.DrawFittedText(
                graphics,
                $"官方剩余 {Math.Clamp(sample.RemainingPercent.Value, 0D, 100D):0.#}%",
                Font,
                FontStyle.Bold,
                _remainingColor,
                new RectangleF(left, cursorY, contentWidth, 21F * dpi),
                StringAlignment.Near,
                StringAlignment.Center,
                Math.Max(5.6F, Font.Size * 0.60F),
                Font.Size * 0.78F);
            cursorY += 21F * dpi;
        }
        if (abnormalWindow != null)
        {
            QuotaDashboardDrawing.DrawFittedText(
                graphics,
                $"容量推测偏差段（非官方额度异常）· 推测总容量 {QuotaDashboardDrawing.FormatUsdValue(abnormalWindow.EstimatedTotalUsd)} < " +
                $"阈值 {QuotaDashboardDrawing.FormatUsdValue(abnormalWindow.ThresholdUsd)} · " +
                $"官方剩余 {abnormalWindow.FromRemainingPercent:0}% → {abnormalWindow.ThroughRemainingPercent:0}%",
                Font,
                FontStyle.Bold,
                _abnormalRemainingColor,
                new RectangleF(left, cursorY, contentWidth, 21F * dpi),
                StringAlignment.Near,
                StringAlignment.Center,
                Math.Max(5.4F, Font.Size * 0.58F),
                Font.Size * 0.76F);
        }
    }

    private PassiveQuotaAssessmentWindow? FindAbnormalAssessmentWindow(
        DateTimeOffset from,
        DateTimeOffset through)
    {
        if (through <= from)
        {
            return null;
        }

        return _assessmentWindows
            .Where(item => item.FromUtc < through && item.ThroughUtc > from)
            .OrderByDescending(item => item.ThroughUtc)
            .FirstOrDefault();
    }

    private static ModelTooltipLayout CalculateModelTooltipLayout(
        float availableHeight,
        float dpi,
        int modelRowCount,
        bool hasRemainingPercent,
        bool hasAbnormalAssessment = false)
    {
        dpi = Math.Max(1F, dpi);
        modelRowCount = Math.Max(0, modelRowCount);
        var footerRows = 1 +
                         (hasRemainingPercent ? 1 : 0) +
                         (hasAbnormalAssessment ? 1 : 0);
        var fixedHeight = (68F + (footerRows * 21F) + 18F) * dpi;
        var rowHeight = 23F * dpi;
        var maximumSlots = (int)Math.Floor((availableHeight - fixedHeight) / rowHeight);
        maximumSlots = Math.Clamp(maximumSlots, 0, 5);
        if (maximumSlots <= 0)
        {
            return default;
        }

        var requestedRows = modelRowCount == 0 ? 1 : modelRowCount;
        var displayRows = Math.Min(requestedRows, maximumSlots);
        var explicitRows = modelRowCount <= displayRows
            ? modelRowCount
            : Math.Max(0, displayRows - 1);
        var hiddenRows = Math.Max(0, modelRowCount - explicitRows);
        return new ModelTooltipLayout(
            explicitRows,
            hiddenRows,
            displayRows,
            fixedHeight + (displayRows * rowHeight));
    }

    private int GetLegendColumnCount(float width, int entryCount, float dpi)
    {
        if (entryCount <= 1)
        {
            return Math.Max(1, entryCount);
        }
        if (width >= 980F * dpi)
        {
            return Math.Min(entryCount, 6);
        }
        if (width >= 620F * dpi)
        {
            return Math.Min(entryCount, 3);
        }
        return Math.Min(entryCount, 2);
    }

    private Color GetModelColor(string model)
    {
        if (model.Contains("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase))
        {
            return _tokenColor;
        }
        if (model.Contains("gpt-5.6-terra", StringComparison.OrdinalIgnoreCase))
        {
            return _modelSecondaryColor;
        }
        if (model.Contains("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase))
        {
            return _modelTertiaryColor;
        }
        if (model.Contains("gpt-5.5", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("chat-latest", StringComparison.OrdinalIgnoreCase))
        {
            return _modelAccentColor;
        }
        if (model.StartsWith("未识别", StringComparison.OrdinalIgnoreCase) ||
            model.Equals("其他模型", StringComparison.OrdinalIgnoreCase))
        {
            return _modelOtherColor;
        }

        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(model);
        var amount = 0.20F + ((Math.Abs((long)hash) % 61L) / 100F);
        return QuotaDashboardDrawing.Blend(_modelSecondaryColor, _modelTertiaryColor, amount);
    }

    private static int GetModelSortOrder(string model)
    {
        if (model.Contains("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase)) return 0;
        if (model.Contains("gpt-5.6-terra", StringComparison.OrdinalIgnoreCase)) return 1;
        if (model.Contains("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase)) return 2;
        if (model.Contains("gpt-5.5", StringComparison.OrdinalIgnoreCase)) return 3;
        return 10;
    }

    private static string NormalizeChartModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "未识别模型";
        }
        var normalized = model.Trim();
        if (normalized.Contains("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase)) return "gpt-5.6-sol";
        if (normalized.Contains("gpt-5.6-terra", StringComparison.OrdinalIgnoreCase)) return "gpt-5.6-terra";
        if (normalized.Contains("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase)) return "gpt-5.6-luna";
        if (normalized.Contains("gpt-5.5", StringComparison.OrdinalIgnoreCase)) return "gpt-5.5";
        return normalized;
    }

    private static float MapCostY(double cost, double maximumCost, RectangleF plot)
    {
        if (maximumCost <= 0D)
        {
            return plot.Bottom;
        }
        var ratio = Math.Clamp(QuotaDashboardDrawing.SafeCost(cost) / maximumCost, 0D, 1D);
        return plot.Bottom - ((float)ratio * plot.Height);
    }

    private static string FormatBucketInterval(DateTimeOffset start, TimeSpan duration)
    {
        var localStart = start.ToLocalTime();
        var localEnd = (start + duration).ToLocalTime();
        if (duration < TimeSpan.FromDays(1))
        {
            return localStart.Date == localEnd.Date
                ? $"{localStart:MM-dd HH:mm} – {localEnd:HH:mm}"
                : $"{localStart:MM-dd HH:mm} – {localEnd:MM-dd HH:mm}";
        }
        return $"{localStart:MM-dd} – {localEnd:MM-dd}";
    }

    private static bool SamplesEqual(
        IReadOnlyList<QuotaChartSample> left,
        IReadOnlyList<QuotaChartSample> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Timestamp != second.Timestamp ||
                first.IncrementalCostUsd != second.IncrementalCostUsd ||
                first.RemainingPercent != second.RemainingPercent ||
                first.TotalTokens != second.TotalTokens ||
                first.BucketDuration != second.BucketDuration)
            {
                return false;
            }
            var firstModels = first.ModelUsage ?? [];
            var secondModels = second.ModelUsage ?? [];
            if (firstModels.Count != secondModels.Count)
            {
                return false;
            }
            for (var modelIndex = 0; modelIndex < firstModels.Count; modelIndex++)
            {
                if (firstModels[modelIndex] != secondModels[modelIndex])
                {
                    return false;
                }
            }
        }
        return true;
    }

    private void DrawCostSummary(
        Graphics graphics,
        RectangleF bounds,
        double totalCost,
        long totalTokens,
        bool compact,
        float dpi)
    {
        var titleWidth = compact ? Math.Min(bounds.Width * 0.44F, 120F * dpi) : bounds.Width;
        var titleHeight = compact ? bounds.Height : Math.Min(bounds.Height * 0.42F, 18F * dpi);
        QuotaDashboardDrawing.DrawFittedText(
            graphics,
            _metric == QuotaTrendMetric.Tokens ? "本视图 Token 用量" : "本视图 API 等值用量",
            Font,
            FontStyle.Regular,
            _mutedColor,
            new RectangleF(bounds.Left + (2F * dpi), bounds.Top, titleWidth, titleHeight),
            StringAlignment.Near,
            StringAlignment.Center,
            Math.Max(5.8F, Font.Size * 0.64F),
            Font.Size * 0.92F);

        var valueBounds = compact
            ? new RectangleF(
                bounds.Left + titleWidth + (6F * dpi),
                bounds.Top,
                Math.Max(24F, bounds.Width - titleWidth - (8F * dpi)),
                bounds.Height)
            : new RectangleF(
                bounds.Left + (2F * dpi),
                bounds.Top + titleHeight,
                bounds.Width - (4F * dpi),
                Math.Max(16F, bounds.Height - titleHeight));
        QuotaDashboardDrawing.DrawFittedText(
            graphics,
            _metric == QuotaTrendMetric.Tokens
                ? $"{QuotaDashboardDrawing.FormatTokenValue(totalTokens)} Token"
                : QuotaDashboardDrawing.FormatUsdValue(totalCost),
            Font,
            FontStyle.Bold,
            _textColor,
            valueBounds,
            StringAlignment.Near,
            StringAlignment.Center,
            Math.Max(7F, Font.Size * 0.78F),
            compact ? Font.Size * 1.15F : Font.Size * 1.55F);
    }

    private void DrawLegend(Graphics graphics, RectangleF outer, bool stacked, float dpi)
    {
        var lineHeight = 20F * dpi;
        var swatchWidth = 24F * dpi;
        var left = outer.Left + (2F * dpi);
        var firstTop = outer.Top + (2F * dpi);
        var secondLeft = stacked ? left : left + Math.Min(190F * dpi, outer.Width * 0.48F);
        var secondTop = stacked ? firstTop + lineHeight : firstTop;
        var firstWidth = stacked
            ? outer.Width - (4F * dpi)
            : Math.Max(80F * dpi, secondLeft - left - (8F * dpi));
        var secondWidth = stacked
            ? outer.Width - (4F * dpi)
            : Math.Max(80F * dpi, outer.Right - secondLeft - (2F * dpi));

        DrawCostLegendGlyph(graphics, new RectangleF(left, firstTop, swatchWidth, lineHeight), dpi);
        QuotaDashboardDrawing.DrawFittedText(
            graphics,
            "本视图日志 API 等值",
            Font,
            FontStyle.Regular,
            _textColor,
            new RectangleF(
                left + swatchWidth + (6F * dpi),
                firstTop,
                Math.Max(20F, firstWidth - swatchWidth - (6F * dpi)),
                lineHeight),
            StringAlignment.Near,
            StringAlignment.Center,
            Math.Max(5.8F, Font.Size * 0.64F),
            Font.Size * 0.9F);

        DrawRemainingLegendGlyph(
            graphics,
            new RectangleF(secondLeft, secondTop, swatchWidth, lineHeight),
            dpi);
        QuotaDashboardDrawing.DrawFittedText(
            graphics,
            "官方剩余百分比",
            Font,
            FontStyle.Regular,
            _textColor,
            new RectangleF(
                secondLeft + swatchWidth + (6F * dpi),
                secondTop,
                Math.Max(20F, secondWidth - swatchWidth - (6F * dpi)),
                lineHeight),
            StringAlignment.Near,
            StringAlignment.Center,
            Math.Max(5.8F, Font.Size * 0.64F),
            Font.Size * 0.9F);
    }

    private void DrawCostLegendGlyph(Graphics graphics, RectangleF bounds, float dpi)
    {
        var y = bounds.Top + (bounds.Height / 2F);
        using var fill = new SolidBrush(_tokenFillColor);
        graphics.FillRectangle(fill, bounds.Left, y, bounds.Width, Math.Max(2F, 5F * dpi));
        using var pen = new Pen(_tokenColor, Math.Max(1.4F, 2F * dpi))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(pen, bounds.Left, y, bounds.Right, y);
    }

    private void DrawRemainingLegendGlyph(Graphics graphics, RectangleF bounds, float dpi)
    {
        var y = bounds.Top + (bounds.Height / 2F);
        using var pen = new Pen(_remainingColor, Math.Max(1.4F, 2F * dpi))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(pen, bounds.Left, y, bounds.Right, y);
        var radius = Math.Max(2F, 3F * dpi);
        using var dot = new SolidBrush(_remainingColor);
        graphics.FillEllipse(dot, bounds.Left + (bounds.Width / 2F) - radius, y - radius, radius * 2F, radius * 2F);
    }

    private void DrawGridAndAxes(
        Graphics graphics,
        RectangleF outer,
        RectangleF plot,
        double maximumCost,
        float dpi)
    {
        using var gridPen = new Pen(_gridColor, Math.Max(1F, 1F * dpi));
        using var verticalGridPen = new Pen(Color.FromArgb(
            Math.Max(18, _gridColor.A / 2),
            _gridColor), Math.Max(1F, 1F * dpi))
        {
            DashStyle = DashStyle.Dot
        };

        const int horizontalSteps = 4;
        for (var index = 0; index <= horizontalSteps; index++)
        {
            var ratio = index / (float)horizontalSteps;
            var y = plot.Bottom - (plot.Height * ratio);
            graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);

            var cost = maximumCost * ratio;
            var percentage = ratio * 100D;
            var labelHeight = Math.Max(14F * dpi, Font.Height * 0.86F);
            QuotaDashboardDrawing.DrawFittedText(
                graphics,
                QuotaDashboardDrawing.FormatUsdAxis(cost, maximumCost),
                Font,
                FontStyle.Regular,
                _mutedColor,
                new RectangleF(
                    outer.Left,
                    y - (labelHeight / 2F),
                    Math.Max(16F, plot.Left - outer.Left - (5F * dpi)),
                    labelHeight),
                StringAlignment.Far,
                StringAlignment.Center,
                Math.Max(5.4F, Font.Size * 0.58F),
                Font.Size * 0.78F);
            QuotaDashboardDrawing.DrawFittedText(
                graphics,
                percentage.ToString("0", CultureInfo.InvariantCulture) + "%",
                Font,
                FontStyle.Regular,
                _mutedColor,
                new RectangleF(
                    plot.Right + (5F * dpi),
                    y - (labelHeight / 2F),
                    Math.Max(16F, outer.Right - plot.Right - (5F * dpi)),
                    labelHeight),
                StringAlignment.Near,
                StringAlignment.Center,
                Math.Max(5.4F, Font.Size * 0.58F),
                Font.Size * 0.78F);
        }

        const int verticalSteps = 4;
        for (var index = 1; index < verticalSteps; index++)
        {
            var x = plot.Left + (plot.Width * index / verticalSteps);
            graphics.DrawLine(verticalGridPen, x, plot.Top, x, plot.Bottom);
        }

        using var borderPen = new Pen(Color.FromArgb(
            Math.Max(52, (int)_gridColor.A),
            _gridColor), Math.Max(1F, 1F * dpi));
        graphics.DrawRectangle(borderPen, plot.X, plot.Y, plot.Width, plot.Height);
    }

    private void DrawCostSeries(
        Graphics graphics,
        RectangleF plot,
        DateTimeOffset start,
        DateTimeOffset end,
        double maximumCost,
        float dpi)
    {
        var cumulativeCost = 0D;
        var points = new PointF[_samples.Length];
        for (var index = 0; index < _samples.Length; index++)
        {
            cumulativeCost += QuotaDashboardDrawing.SafeCost(_samples[index].IncrementalCostUsd);
            points[index] = new PointF(
                MapX(_samples[index].Timestamp, index, _samples.Length, start, end, plot),
                plot.Bottom - ((float)(cumulativeCost / maximumCost) * plot.Height));
        }
        if (points.Length == 0)
        {
            return;
        }

        var curve = QuotaDashboardDrawing.BuildClampedMonotoneCurve(points, plot);
        if (curve.Length > 1)
        {
            using var areaPath = new GraphicsPath();
            areaPath.AddLine(curve[0].X, plot.Bottom, curve[0].X, curve[0].Y);
            areaPath.AddLines(curve);
            areaPath.AddLine(curve[^1].X, curve[^1].Y, curve[^1].X, plot.Bottom);
            areaPath.CloseFigure();
            using var areaBrush = new LinearGradientBrush(
                plot,
                _tokenFillColor,
                Color.FromArgb(3, _tokenColor),
                LinearGradientMode.Vertical);
            graphics.FillPath(areaBrush, areaPath);

            using var linePath = new GraphicsPath();
            linePath.AddLines(curve);
            using var linePen = new Pen(_tokenColor, Math.Max(1.5F, 2.2F * dpi))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawPath(linePen, linePath);
        }
        else
        {
            using var stemPen = new Pen(Color.FromArgb(105, _tokenColor), Math.Max(1F, 1.5F * dpi));
            graphics.DrawLine(stemPen, points[0].X, plot.Bottom, points[0].X, points[0].Y);
        }

        var lastRadius = Math.Max(2.5F, 3.3F * dpi);
        using var lastPointBrush = new SolidBrush(_tokenColor);
        graphics.FillEllipse(
            lastPointBrush,
            curve[^1].X - lastRadius,
            curve[^1].Y - lastRadius,
            lastRadius * 2F,
            lastRadius * 2F);
    }

    private void DrawHoverOverlay(
        Graphics graphics,
        RectangleF plot,
        DateTimeOffset start,
        DateTimeOffset end,
        double maximumCost,
        float dpi)
    {
        if (_hoveredSampleIndex < 0 ||
            _hoveredSampleIndex >= _samples.Length ||
            maximumCost <= 0D)
        {
            return;
        }

        var sample = _samples[_hoveredSampleIndex];
        var x = MapX(sample.Timestamp, _hoveredSampleIndex, _samples.Length, start, end, plot);
        var cumulativeCost = 0D;
        for (var index = 0; index <= _hoveredSampleIndex; index++)
        {
            cumulativeCost += QuotaDashboardDrawing.SafeCost(_samples[index].IncrementalCostUsd);
        }
        var y = plot.Bottom - ((float)(cumulativeCost / maximumCost) * plot.Height);
        y = Math.Clamp(y, plot.Top, plot.Bottom);

        using (var guide = new Pen(Color.FromArgb(126, _tokenColor), Math.Max(1F, dpi)))
        {
            guide.DashStyle = DashStyle.Dash;
            graphics.DrawLine(guide, x, plot.Top, x, plot.Bottom);
        }
        var haloRadius = Math.Max(5F, 6F * dpi);
        using (var halo = new SolidBrush(Color.FromArgb(44, _tokenColor)))
        {
            graphics.FillEllipse(halo, x - haloRadius, y - haloRadius, haloRadius * 2F, haloRadius * 2F);
        }
        var pointRadius = Math.Max(2.8F, 3.4F * dpi);
        using (var point = new SolidBrush(_tokenColor))
        {
            graphics.FillEllipse(point, x - pointRadius, y - pointRadius, pointRadius * 2F, pointRadius * 2F);
        }

        var duration = NormalizeBucketDuration(sample.BucketDuration);
        var intervalCost = QuotaDashboardDrawing.SafeCost(sample.IncrementalCostUsd);
        var hourlyRate = intervalCost / Math.Max(1D / 3_600D, duration.TotalHours);
        var rollingHourCost = GetRollingOneHourMetric(_hoveredSampleIndex);
        var localTime = sample.Timestamp.ToLocalTime();

        var tooltipWidth = Math.Min(plot.Width - (12F * dpi), 304F * dpi);
        var tooltipHeight = Math.Min(plot.Height - (12F * dpi), 132F * dpi);
        if (tooltipWidth < 150F * dpi || tooltipHeight < 92F * dpi)
        {
            return;
        }
        var tooltipLeft = x + (14F * dpi);
        if (tooltipLeft + tooltipWidth > plot.Right - (4F * dpi))
        {
            tooltipLeft = x - tooltipWidth - (14F * dpi);
        }
        tooltipLeft = Math.Clamp(tooltipLeft, plot.Left + (4F * dpi), plot.Right - tooltipWidth - (4F * dpi));
        var tooltipTop = Math.Clamp(
            y - tooltipHeight - (12F * dpi),
            plot.Top + (4F * dpi),
            plot.Bottom - tooltipHeight - (4F * dpi));
        var tooltipBounds = new RectangleF(tooltipLeft, tooltipTop, tooltipWidth, tooltipHeight);
        var lightSurface = _textColor.GetBrightness() < 0.52F;
        var tooltipBack = lightSurface
            ? Color.FromArgb(242, 255, 255, 255)
            : Color.FromArgb(244, 15, 23, 42);
        var tooltipText = lightSurface ? Color.FromArgb(15, 23, 42) : Color.FromArgb(241, 245, 249);
        var tooltipMuted = lightSurface ? Color.FromArgb(71, 85, 105) : Color.FromArgb(186, 199, 219);
        using (var path = UiDesign.CreateRoundedPath(tooltipBounds, 12F * dpi))
        using (var shadow = new SolidBrush(Color.FromArgb(34, Color.Black)))
        using (var fill = new SolidBrush(tooltipBack))
        using (var border = new Pen(Color.FromArgb(156, _tokenColor), Math.Max(1F, dpi)))
        {
            var shadowBounds = tooltipBounds;
            shadowBounds.Offset(0F, 3F * dpi);
            using var shadowPath = UiDesign.CreateRoundedPath(shadowBounds, 12F * dpi);
            graphics.FillPath(shadow, shadowPath);
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
        }

        var textLeft = tooltipBounds.Left + (16F * dpi);
        var textWidth = tooltipBounds.Width - (32F * dpi);
        var verticalPadding = Math.Clamp(
            tooltipBounds.Height * 0.08F,
            5F * dpi,
            12F * dpi);
        var rowGap = Math.Clamp(tooltipBounds.Height * 0.025F, 2F * dpi, 4F * dpi);
        var rowHeight = Math.Max(
            16F * dpi,
            (tooltipBounds.Height - (verticalPadding * 2F) - (rowGap * 2F)) / 3F);
        var firstRowTop = tooltipBounds.Top + verticalPadding;
        var secondRowTop = firstRowTop + rowHeight + rowGap;
        var thirdRowTop = secondRowTop + rowHeight + rowGap;
        QuotaDashboardDrawing.DrawFittedText(
            graphics,
            localTime.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture),
            Font,
            FontStyle.Bold,
            tooltipText,
            new RectangleF(textLeft, firstRowTop, textWidth, rowHeight),
            StringAlignment.Near,
            StringAlignment.Center,
            Math.Max(6.6F, Font.Size * 0.72F),
            Font.Size * 0.96F);
        QuotaDashboardDrawing.DrawFittedText(
            graphics,
            $"使用速率  {QuotaDashboardDrawing.FormatUsdValue(hourlyRate)} / 小时",
            Font,
            FontStyle.Regular,
            tooltipMuted,
            new RectangleF(textLeft, secondRowTop, textWidth, rowHeight),
            StringAlignment.Near,
            StringAlignment.Center,
            Math.Max(6.2F, Font.Size * 0.68F),
            Font.Size * 0.88F);
        QuotaDashboardDrawing.DrawFittedText(
            graphics,
            $"近 1 小时  {QuotaDashboardDrawing.FormatUsdValue(rollingHourCost)}",
            Font,
            FontStyle.Bold,
            _tokenColor,
            new RectangleF(textLeft, thirdRowTop, textWidth, rowHeight),
            StringAlignment.Near,
            StringAlignment.Center,
            Math.Max(6.2F, Font.Size * 0.68F),
            Font.Size * 0.92F);
    }

    private double GetRollingOneHourMetric(int selectedIndex)
    {
        if (selectedIndex < 0 || selectedIndex >= _samples.Length)
        {
            return 0D;
        }

        var selected = _samples[selectedIndex];
        var selectedDuration = NormalizeBucketDuration(selected.BucketDuration);
        var through = GetSampleMetricValue(selected) > 0D
            ? selected.Timestamp + selectedDuration
            : selected.Timestamp;
        var from = through - TimeSpan.FromHours(1);
        var total = 0D;
        foreach (var candidate in _samples)
        {
            var value = GetSampleMetricValue(candidate);
            if (value <= 0D)
            {
                continue;
            }
            var duration = NormalizeBucketDuration(candidate.BucketDuration);
            var candidateStart = candidate.Timestamp;
            var candidateEnd = candidateStart + duration;
            var overlapStart = candidateStart > from ? candidateStart : from;
            var overlapEnd = candidateEnd < through ? candidateEnd : through;
            if (overlapEnd <= overlapStart)
            {
                continue;
            }
            var ratio = (overlapEnd - overlapStart).TotalSeconds / Math.Max(1D, duration.TotalSeconds);
            total += value * Math.Clamp(ratio, 0D, 1D);
        }
        return total;
    }

    private static TimeSpan NormalizeBucketDuration(TimeSpan value) =>
        value > TimeSpan.Zero ? value : TimeSpan.FromHours(1);

    private void DrawAbnormalRemainingWindows(
        Graphics graphics,
        RectangleF plot,
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<PointF[]> remainingCurves,
        float dpi)
    {
        if (_assessmentWindows.Length == 0 || remainingCurves.Count == 0 || end <= start)
        {
            return;
        }

        using var glow = new Pen(
            Color.FromArgb(58, _abnormalRemainingColor),
            Math.Max(4.6F, 6.2F * dpi))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var line = new Pen(
            _abnormalRemainingColor,
            Math.Max(1.8F, 2.6F * dpi))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        foreach (var window in _assessmentWindows)
        {
            var windowFrom = window.FromUtc.ToUniversalTime();
            var windowThrough = window.ThroughUtc.ToUniversalTime();
            var visibleFrom = windowFrom < start ? start : windowFrom;
            var visibleThrough = windowThrough > end ? end : windowThrough;
            if (visibleThrough <= visibleFrom)
            {
                continue;
            }

            var left = Math.Clamp(
                MapX(visibleFrom, 0, 1, start, end, plot),
                plot.Left,
                plot.Right);
            var right = Math.Clamp(
                MapX(visibleThrough, 0, 1, start, end, plot),
                plot.Left,
                plot.Right);
            if (right - left < 0.25F)
            {
                continue;
            }

            // Do not infer a second y-value from the assessment window.  Clip and redraw the
            // already-smoothed official green path instead, so the red interval has the exact
            // same geometry and tangent before, during, and after an abnormal assessment.
            var state = graphics.Save();
            try
            {
                graphics.SetClip(
                    new RectangleF(left, plot.Top - (8F * dpi), right - left, plot.Height + (16F * dpi)),
                    CombineMode.Intersect);
                foreach (var curve in remainingCurves)
                {
                    if (curve.Length > 1)
                    {
                        graphics.DrawLines(glow, curve);
                        graphics.DrawLines(line, curve);
                    }
                }
            }
            finally
            {
                graphics.Restore(state);
            }

            // A tiny cap on the same curve keeps the color transition round without creating
            // a detached marker or a parallel red segment.
            using var endpointBrush = new SolidBrush(_abnormalRemainingColor);
            var capRadius = Math.Max(1.4F, 1.9F * dpi);
            if (TryGetRemainingCurvePointAtX(remainingCurves, left, out var fromPoint))
            {
                graphics.FillEllipse(
                    endpointBrush,
                    fromPoint.X - capRadius,
                    fromPoint.Y - capRadius,
                    capRadius * 2F,
                    capRadius * 2F);
            }
            if (TryGetRemainingCurvePointAtX(remainingCurves, right, out var throughPoint))
            {
                graphics.FillEllipse(
                    endpointBrush,
                    throughPoint.X - capRadius,
                    throughPoint.Y - capRadius,
                    capRadius * 2F,
                    capRadius * 2F);
            }
        }
    }

    private IReadOnlyList<PointF[]> DrawRemainingSeries(
        Graphics graphics,
        RectangleF plot,
        DateTimeOffset start,
        DateTimeOffset end,
        float dpi)
    {
        var segment = new List<PointF>();
        var curves = new List<PointF[]>();
        PointF? lastPoint = null;
        for (var index = 0; index < _samples.Length; index++)
        {
            var sample = _samples[index];
            if (!sample.RemainingPercent.HasValue ||
                !double.IsFinite(sample.RemainingPercent.Value))
            {
                var curve = DrawRemainingSegment(graphics, segment, dpi);
                if (curve.Length > 0)
                {
                    curves.Add(curve);
                }
                segment.Clear();
                continue;
            }

            var percentage = Math.Clamp(sample.RemainingPercent.Value, 0D, 100D);
            var point = new PointF(
                MapX(GetSampleCenter(sample), index, _samples.Length, start, end, plot),
                plot.Bottom - ((float)(percentage / 100D) * plot.Height));
            segment.Add(point);
            lastPoint = point;
        }
        var finalCurve = DrawRemainingSegment(graphics, segment, dpi);
        if (finalCurve.Length > 0)
        {
            curves.Add(finalCurve);
        }

        if (lastPoint.HasValue)
        {
            var radius = Math.Max(2.4F, 3.1F * dpi);
            using var halo = new SolidBrush(Color.FromArgb(42, _remainingColor));
            graphics.FillEllipse(
                halo,
                lastPoint.Value.X - (radius * 2F),
                lastPoint.Value.Y - (radius * 2F),
                radius * 4F,
                radius * 4F);
            using var dot = new SolidBrush(_remainingColor);
            graphics.FillEllipse(
                dot,
                lastPoint.Value.X - radius,
                lastPoint.Value.Y - radius,
                radius * 2F,
                radius * 2F);
        }
        return curves;
    }

    private PointF[] DrawRemainingSegment(Graphics graphics, IReadOnlyList<PointF> segment, float dpi)
    {
        if (segment.Count == 0)
        {
            return [];
        }

        using var pen = new Pen(_remainingColor, Math.Max(1.5F, 2.2F * dpi))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        if (segment.Count == 1)
        {
            var radius = Math.Max(1.8F, 2.4F * dpi);
            using var brush = new SolidBrush(_remainingColor);
            graphics.FillEllipse(
                brush,
                segment[0].X - radius,
                segment[0].Y - radius,
                radius * 2F,
                radius * 2F);
            return [segment[0]];
        }

        var bounds = RectangleF.FromLTRB(
            segment.Min(point => point.X),
            segment.Min(point => point.Y),
            segment.Max(point => point.X),
            segment.Max(point => point.Y));
        // Percentage points have already been clamped to the plot's 0-100% range. Expand
        // the local bounds minimally so a flat series remains valid for the interpolator.
        if (bounds.Width < 1F)
        {
            bounds.Width = 1F;
        }
        if (bounds.Height < 1F)
        {
            bounds.Y -= 0.5F;
            bounds.Height = 1F;
        }
        var curve = QuotaDashboardDrawing.BuildClampedMonotoneCurve(segment, bounds);
        if (curve.Length == 1)
        {
            var radius = Math.Max(1.8F, 2.4F * dpi);
            using var brush = new SolidBrush(_remainingColor);
            graphics.FillEllipse(
                brush,
                curve[0].X - radius,
                curve[0].Y - radius,
                radius * 2F,
                radius * 2F);
            return curve;
        }
        if (curve.Length > 1)
        {
            graphics.DrawLines(pen, curve);
        }
        return curve;
    }

    private static bool TryGetRemainingCurvePointAtX(
        IReadOnlyList<PointF[]> curves,
        float x,
        out PointF point)
    {
        foreach (var curve in curves)
        {
            for (var index = 1; index < curve.Length; index++)
            {
                var from = curve[index - 1];
                var through = curve[index];
                var minimum = Math.Min(from.X, through.X) - 0.25F;
                var maximum = Math.Max(from.X, through.X) + 0.25F;
                if (x < minimum || x > maximum)
                {
                    continue;
                }

                var horizontalSpan = through.X - from.X;
                var ratio = Math.Abs(horizontalSpan) < 0.001F
                    ? 0F
                    : Math.Clamp((x - from.X) / horizontalSpan, 0F, 1F);
                point = new PointF(
                    x,
                    from.Y + ((through.Y - from.Y) * ratio));
                return true;
            }

            if (curve.Length == 1 && Math.Abs(curve[0].X - x) <= 0.75F)
            {
                point = curve[0];
                return true;
            }
        }

        point = PointF.Empty;
        return false;
    }

    private void DrawTimeLabels(
        Graphics graphics,
        RectangleF plot,
        DateTimeOffset start,
        DateTimeOffset end,
        float dpi)
    {
        var labelTop = plot.Bottom + (5F * dpi);
        var labelHeight = Math.Max(15F * dpi, Font.Height * 0.92F);
        var span = end - start;
        if (_samples.Length == 0)
        {
            return;
        }

        // Keep one small tick per bucket. For the 24-hour view this means every hour has
        // an exact visual/hit-test position even though only a few labels are printed.
        using (var tickPen = new Pen(Color.FromArgb(Math.Max(42, (int)_gridColor.A), _gridColor), Math.Max(1F, dpi)))
        {
            for (var index = 0; index < _samples.Length; index++)
            {
                var x = MapX(_samples[index].Timestamp, index, _samples.Length, start, end, plot);
                graphics.DrawLine(tickPen, x, plot.Bottom, x, plot.Bottom + (3F * dpi));
            }
            var endX = MapX(end, _samples.Length, _samples.Length + 1, start, end, plot);
            graphics.DrawLine(tickPen, endX, plot.Bottom, endX, plot.Bottom + (3F * dpi));
        }

        var desiredLabels = plot.Width >= (760F * dpi)
            ? 5
            : plot.Width >= (420F * dpi) ? 3 : 2;
        var labelIndexes = new SortedSet<int>();
        for (var label = 0; label < desiredLabels; label++)
        {
            labelIndexes.Add((int)Math.Round(
                label * Math.Max(0, _samples.Length - 1D) /
                Math.Max(1, desiredLabels - 1D)));
        }
        var labelWidth = Math.Max(48F * dpi, plot.Width / Math.Max(2F, desiredLabels - 0.3F));
        foreach (var index in labelIndexes)
        {
            var x = MapX(_samples[index].Timestamp, index, _samples.Length, start, end, plot);
            var left = Math.Clamp(
                x - (labelWidth / 2F),
                plot.Left,
                plot.Right - labelWidth);
            QuotaDashboardDrawing.DrawFittedText(
                graphics,
                FormatTimeLabel(_samples[index].Timestamp, span),
                Font,
                FontStyle.Regular,
                _mutedColor,
                new RectangleF(left, labelTop, labelWidth, labelHeight),
                StringAlignment.Center,
                StringAlignment.Center,
                Math.Max(5.5F, Font.Size * 0.58F),
                Font.Size * 0.78F);
        }
    }

    private void DrawEmptyState(Graphics graphics, RectangleF bounds)
    {
        if (bounds.Width <= 4F || bounds.Height <= 4F)
        {
            return;
        }

        QuotaDashboardDrawing.DrawFittedText(
            graphics,
            string.IsNullOrWhiteSpace(_emptyText) ? "暂无历史数据" : _emptyText,
            Font,
            FontStyle.Regular,
            _mutedColor,
            RectangleF.Inflate(bounds, -6F, -6F),
            StringAlignment.Center,
            StringAlignment.Center,
            Math.Max(5.8F, Font.Size * 0.62F),
            Font.Size,
            allowWrap: true);
    }

    private int FindSampleIndexAtX(float x)
    {
        if (_samples.Length == 0 || _lastPlotBounds.IsEmpty)
        {
            return -1;
        }

        for (var index = 0; index < _samples.Length; index++)
        {
            var sample = _samples[index];
            var left = MapX(
                sample.Timestamp,
                index,
                _samples.Length,
                _lastPlotStart,
                _lastPlotEnd,
                _lastPlotBounds);
            var right = MapX(
                GetSampleEnd(sample),
                index + 1,
                _samples.Length + 1,
                _lastPlotStart,
                _lastPlotEnd,
                _lastPlotBounds);
            if (x >= left && (x < right || (index == _samples.Length - 1 && x <= right)))
            {
                return index;
            }
        }

        return -1;
    }

    private static DateTimeOffset GetSampleEnd(QuotaChartSample sample) =>
        sample.Timestamp + NormalizeBucketDuration(sample.BucketDuration);

    private static DateTimeOffset GetSampleCenter(QuotaChartSample sample)
    {
        var duration = NormalizeBucketDuration(sample.BucketDuration);
        return sample.Timestamp + TimeSpan.FromTicks(duration.Ticks / 2L);
    }

    private static float MapX(
        DateTimeOffset timestamp,
        int index,
        int count,
        DateTimeOffset start,
        DateTimeOffset end,
        RectangleF plot)
    {
        var totalTicks = (end - start).Ticks;
        if (totalTicks <= 0)
        {
            return count <= 1
                ? plot.Left + (plot.Width / 2F)
                : plot.Left + (plot.Width * index / Math.Max(1, count - 1F));
        }

        var elapsedTicks = Math.Clamp((timestamp - start).Ticks, 0L, totalTicks);
        return plot.Left + ((float)(elapsedTicks / (double)totalTicks) * plot.Width);
    }

    private static string FormatTimeLabel(DateTimeOffset value, TimeSpan totalSpan)
    {
        var local = value.ToLocalTime();
        if (totalSpan <= TimeSpan.FromHours(24))
        {
            return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        }
        if (totalSpan <= TimeSpan.FromDays(7))
        {
            return local.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
        }
        return local.ToString("MM-dd", CultureInfo.CurrentCulture);
    }

    private void SetColor(ref Color field, Color value)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        Invalidate();
    }

    private void UpdateAccessibility()
    {
        AccessibleName = "额度用量趋势";
        var totalCost = GetTotalCost();
        var totalTokens = GetTotalTokens();
        AccessibleDescription = _samples.Length == 0
            ? _emptyText
            : "共 " + _samples.Length.ToString(CultureInfo.CurrentCulture) +
              " 个时间点，本视图日志用量 " +
              QuotaDashboardDrawing.FormatUsdValue(totalCost) +
              "，" + QuotaDashboardDrawing.FormatTokenValue(totalTokens) + " Token" +
              (_assessmentWindows.Length == 0
                  ? string.Empty
                  : $"，其中 {_assessmentWindows.Length} 个容量推测偏差区间已标红");
    }

    internal static void ValidateHighDpiTooltipLayout()
    {
        foreach (var dpi in new[] { 1F, 1.5F, 2F })
        {
            var availableHeight = 360F * dpi;
            var layout = CalculateModelTooltipLayout(
                availableHeight,
                dpi,
                modelRowCount: 5,
                hasRemainingPercent: true,
                hasAbnormalAssessment: true);
            if (layout.DisplayRows <= 0 ||
                layout.Height > availableHeight + 0.01F ||
                layout.ExplicitModelRows + layout.HiddenModelRows != 5)
            {
                throw new InvalidOperationException(
                    $"Quota trend tooltip layout overflowed at {dpi * 100F:0}% DPI.");
            }

            var compactHeight = 174F * dpi;
            var compact = CalculateModelTooltipLayout(
                compactHeight,
                dpi,
                modelRowCount: 5,
                hasRemainingPercent: true);
            if (compact.DisplayRows <= 0 ||
                compact.Height > compactHeight + 0.01F ||
                compact.HiddenModelRows <= 0 ||
                compact.ExplicitModelRows + compact.HiddenModelRows != 5)
            {
                throw new InvalidOperationException(
                    $"Quota trend compact tooltip did not fold model rows at {dpi * 100F:0}% DPI.");
            }
        }

        var start = new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero);
        using var chart = new QuotaTrendChart
        {
            Samples =
            [
                new QuotaChartSample(start, 0D, null, 0L, TimeSpan.FromHours(1), []),
                new QuotaChartSample(start.AddHours(1), 0D, null, 0L, TimeSpan.FromHours(1), [])
            ]
        };
        chart._lastPlotBounds = new RectangleF(0F, 0F, 240F, 100F);
        chart._lastPlotStart = start;
        chart._lastPlotEnd = start.AddHours(2);
        if (chart.FindSampleIndexAtX(0F) != 0 ||
            chart.FindSampleIndexAtX(119.9F) != 0 ||
            chart.FindSampleIndexAtX(120F) != 1 ||
            chart.FindSampleIndexAtX(240F) != 1 ||
            GetSampleCenter(chart._samples[0]) != start.AddMinutes(30) ||
            GetSampleEnd(chart._samples[^1]) != start.AddHours(2))
        {
            throw new InvalidOperationException(
                "Quota trend bucket hit-testing must cover every half-open time bucket exactly once.");
        }

        chart.Samples = [];
        if (chart.GetTotalTokens() != 0L)
        {
            throw new InvalidOperationException("An empty quota trend must report zero Token usage.");
        }
        chart.Samples =
        [
            new QuotaChartSample(start, 0D, null, -1L, TimeSpan.FromHours(1), []),
            new QuotaChartSample(start.AddHours(1), 0D, null, long.MaxValue, TimeSpan.FromHours(1), []),
            new QuotaChartSample(start.AddHours(2), 0D, null, 1L, TimeSpan.FromHours(1), [])
        ];
        if (chart.GetTotalTokens() != long.MaxValue)
        {
            throw new InvalidOperationException("Quota trend Token totals must clamp negatives and saturate on overflow.");
        }

        chart.Samples =
        [
            new QuotaChartSample(
                start,
                3D,
                82D,
                3_000L,
                TimeSpan.FromHours(1),
                [
                    new QuotaChartModelUsage("gpt-5.6-sol", 1D, 1_000L, 1),
                    new QuotaChartModelUsage("gpt-5.6-terra", 2D, 2_000L, 1)
                ]),
            new QuotaChartSample(
                start.AddHours(1),
                4D,
                80D,
                4_000L,
                TimeSpan.FromHours(1),
                [new QuotaChartModelUsage("gpt-5.6-sol", 4D, 4_000L, 1)])
        ];
        var series = chart.BuildModelSeries();
        var sol = series.SingleOrDefault(item => item.Model == "gpt-5.6-sol");
        var terra = series.SingleOrDefault(item => item.Model == "gpt-5.6-terra");
        if (series.Count != 2 ||
            sol == null ||
            terra == null ||
            sol.Values.Length != 2 ||
            sol.Values[0] != 1D ||
            sol.Values[1] != 4D ||
            terra.Values[0] != 2D ||
            terra.Values[1] != 0D ||
            chart.GetTotalCost() != 7D ||
            chart.GetTotalTokens() != 7_000L)
        {
            throw new InvalidOperationException(
                "Quota trend model areas must retain non-cumulative per-bucket API-equivalent values.");
        }

        chart.Metric = QuotaTrendMetric.Tokens;
        var tokenSeries = chart.BuildModelSeries();
        var tokenSol = tokenSeries.SingleOrDefault(item => item.Model == "gpt-5.6-sol");
        var tokenTerra = tokenSeries.SingleOrDefault(item => item.Model == "gpt-5.6-terra");
        if (tokenSeries.Count != 2 ||
            tokenSol == null ||
            tokenTerra == null ||
            tokenSol.Values[0] != 1_000D ||
            tokenSol.Values[1] != 4_000D ||
            tokenTerra.Values[0] != 2_000D ||
            tokenTerra.Values[1] != 0D ||
            chart.GetMaximumBucketValue() != 4_000D ||
            chart.FormatMetricAxis(4_000D, 4_000D) != "4K" ||
            chart.FormatMetricValue(4_000D) != "4K Token")
        {
            throw new InvalidOperationException(
                "Quota trend Token mode must retain real per-model, per-bucket Token values.");
        }

        chart.Size = new Size(680, 460);
        chart._hoveredSampleIndex = 0;
        using var bitmap = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(bitmap, chart.ClientRectangle);

        chart._hoveredSampleIndex = -1;
        chart.AbnormalRemainingColor = Color.FromArgb(239, 32, 48);
        using var normalBitmap = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(normalBitmap, chart.ClientRectangle);
        chart.AssessmentWindows =
        [
            new PassiveQuotaAssessmentWindow(
                start.AddMinutes(30),
                start.AddMinutes(90),
                18,
                20,
                7D,
                10D,
                PassiveQuotaStatus.Abnormal)
        ];
        if (chart.FindAbnormalAssessmentWindow(start, start.AddHours(1)) == null)
        {
            throw new InvalidOperationException(
                "Quota trend abnormal-window hit testing did not retain the monitoring interval.");
        }
        using var abnormalBitmap = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(abnormalBitmap, chart.ClientRectangle);
        var changedPixels = 0;
        var redPixels = 0;
        for (var y = 0; y < chart.Height; y++)
        {
            for (var x = 0; x < chart.Width; x++)
            {
                var normalPixel = normalBitmap.GetPixel(x, y);
                var abnormalPixel = abnormalBitmap.GetPixel(x, y);
                if (normalPixel.ToArgb() != abnormalPixel.ToArgb())
                {
                    changedPixels++;
                }
                if (abnormalPixel.R > abnormalPixel.G + 45 &&
                    abnormalPixel.R > abnormalPixel.B + 30)
                {
                    redPixels++;
                }
            }
        }
        if (changedPixels == 0 || redPixels == 0)
        {
            throw new InvalidOperationException(
                "Quota trend monitoring intervals must visibly overlay the official percentage line in red.");
        }

        // Realtime scope supplies no assessment windows. Clearing them must restore the
        // untouched chart and a normal assessment must never be converted to a red span.
        chart.AssessmentWindows =
        [
            new PassiveQuotaAssessmentWindow(
                start.AddMinutes(30),
                start.AddMinutes(90),
                18,
                20,
                12D,
                10D,
                PassiveQuotaStatus.Normal)
        ];
        if (chart.AssessmentWindows.Count != 0)
        {
            throw new InvalidOperationException(
                "Quota trend must ignore normal assessment windows when selecting red overlays.");
        }
        using var realtimeBitmap = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(realtimeBitmap, chart.ClientRectangle);
        for (var y = 0; y < chart.Height; y++)
        {
            for (var x = 0; x < chart.Width; x++)
            {
                if (normalBitmap.GetPixel(x, y).ToArgb() != realtimeBitmap.GetPixel(x, y).ToArgb())
                {
                    throw new InvalidOperationException(
                        "Quota trend realtime rendering must remain unchanged when no abnormal intervals are supplied.");
                }
            }
        }
    }
}

internal static class QuotaDashboardDrawing
{
    public static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0F, 1F);
        return Color.FromArgb(
            (int)Math.Round(from.A + ((to.A - from.A) * amount)),
            (int)Math.Round(from.R + ((to.R - from.R) * amount)),
            (int)Math.Round(from.G + ((to.G - from.G) * amount)),
            (int)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    public static double SafeCost(double value) =>
        double.IsFinite(value) && value > 0D ? value : 0D;

    /// <summary>
    /// Builds a sampled monotone cubic Hermite curve. Tangents use the
    /// Fritsch-Carlson/PCHIP limiter and every generated point is additionally clamped to
    /// its source interval and the plot rectangle. Consequently a smooth cost curve cannot
    /// dip below zero and a percentage curve cannot pass the supplied 0-100% plot bounds.
    /// </summary>
    public static PointF[] BuildClampedMonotoneCurve(
        IReadOnlyList<PointF> source,
        RectangleF plotBounds)
    {
        if (source == null || source.Count == 0)
        {
            return [];
        }

        var left = Math.Min(plotBounds.Left, plotBounds.Right);
        var right = Math.Max(plotBounds.Left, plotBounds.Right);
        var top = Math.Min(plotBounds.Top, plotBounds.Bottom);
        var bottom = Math.Max(plotBounds.Top, plotBounds.Bottom);
        if (right - left < 0.001F || bottom - top < 0.001F)
        {
            return source
                .Where(point => float.IsFinite(point.X) && float.IsFinite(point.Y))
                .Take(1)
                .ToArray();
        }

        var ordered = source
            .Where(point => float.IsFinite(point.X) && float.IsFinite(point.Y))
            .Select(point => new PointF(
                Math.Clamp(point.X, left, right),
                Math.Clamp(point.Y, top, bottom)))
            .OrderBy(point => point.X)
            .ToList();
        if (ordered.Count == 0)
        {
            return [];
        }

        // Multiple observations can share one timestamp. Keep the most recent value at that
        // horizontal position so the interpolation always receives strictly increasing X.
        var points = new List<PointF>(ordered.Count);
        foreach (var point in ordered)
        {
            if (points.Count > 0 && point.X - points[^1].X <= 0.01F)
            {
                points[^1] = new PointF(Math.Max(points[^1].X, point.X), point.Y);
            }
            else
            {
                points.Add(point);
            }
        }
        if (points.Count <= 1)
        {
            return points.ToArray();
        }

        var count = points.Count;
        var intervalWidths = new double[count - 1];
        var secants = new double[count - 1];
        for (var index = 0; index < count - 1; index++)
        {
            intervalWidths[index] = Math.Max(0.000_001D, points[index + 1].X - points[index].X);
            secants[index] = (points[index + 1].Y - points[index].Y) / intervalWidths[index];
        }

        var tangents = new double[count];
        if (count == 2)
        {
            tangents[0] = secants[0];
            tangents[1] = secants[0];
        }
        else
        {
            tangents[0] = LimitedEndpointSlope(
                intervalWidths[0],
                intervalWidths[1],
                secants[0],
                secants[1]);
            tangents[^1] = LimitedEndpointSlope(
                intervalWidths[^1],
                intervalWidths[^2],
                secants[^1],
                secants[^2]);
            for (var index = 1; index < count - 1; index++)
            {
                var previous = secants[index - 1];
                var next = secants[index];
                if (previous == 0D || next == 0D || Math.Sign(previous) != Math.Sign(next))
                {
                    tangents[index] = 0D;
                    continue;
                }

                var previousWidth = intervalWidths[index - 1];
                var nextWidth = intervalWidths[index];
                var firstWeight = (2D * nextWidth) + previousWidth;
                var secondWeight = nextWidth + (2D * previousWidth);
                tangents[index] =
                    (firstWeight + secondWeight) /
                    ((firstWeight / previous) + (secondWeight / next));
            }
        }

        var curve = new List<PointF>(Math.Min(2_048, count * 16))
        {
            points[0]
        };
        for (var index = 0; index < count - 1; index++)
        {
            var first = points[index];
            var second = points[index + 1];
            var width = intervalWidths[index];
            var steps = Math.Clamp((int)Math.Ceiling(width / 6D), 4, 48);
            var lowerY = Math.Max(top, Math.Min(first.Y, second.Y));
            var upperY = Math.Min(bottom, Math.Max(first.Y, second.Y));
            for (var step = 1; step <= steps; step++)
            {
                var t = step / (double)steps;
                var t2 = t * t;
                var t3 = t2 * t;
                var h00 = (2D * t3) - (3D * t2) + 1D;
                var h10 = t3 - (2D * t2) + t;
                var h01 = (-2D * t3) + (3D * t2);
                var h11 = t3 - t2;
                var y =
                    (h00 * first.Y) +
                    (h10 * width * tangents[index]) +
                    (h01 * second.Y) +
                    (h11 * width * tangents[index + 1]);
                y = Math.Clamp(y, lowerY, upperY);
                var x = first.X + (width * t);
                curve.Add(new PointF(
                    Math.Clamp((float)x, left, right),
                    Math.Clamp((float)y, top, bottom)));
            }
        }

        return curve.ToArray();

        static double LimitedEndpointSlope(
            double adjacentWidth,
            double nextWidth,
            double adjacentSecant,
            double nextSecant)
        {
            var slope =
                (((2D * adjacentWidth) + nextWidth) * adjacentSecant -
                 (adjacentWidth * nextSecant)) /
                (adjacentWidth + nextWidth);
            if (slope == 0D || Math.Sign(slope) != Math.Sign(adjacentSecant))
            {
                return 0D;
            }
            if (Math.Sign(adjacentSecant) != Math.Sign(nextSecant) &&
                Math.Abs(slope) > Math.Abs(3D * adjacentSecant))
            {
                return 3D * adjacentSecant;
            }
            return slope;
        }
    }

    public static double NiceMaximum(double value)
    {
        if (!double.IsFinite(value) || value <= 0D)
        {
            return 0.01D;
        }

        var exponent = Math.Floor(Math.Log10(value));
        var magnitude = Math.Pow(10D, exponent);
        var normalized = value / magnitude;
        var nice = normalized switch
        {
            <= 1D => 1D,
            <= 2D => 2D,
            <= 5D => 5D,
            _ => 10D
        };
        return Math.Max(0.000_001D, nice * magnitude);
    }

    public static string FormatTokenAxis(double value)
    {
        if (!double.IsFinite(value) || value <= 0D)
        {
            return "0";
        }
        if (value >= 1_000_000_000D)
        {
            return (value / 1_000_000_000D).ToString("0.#", CultureInfo.InvariantCulture) + "B";
        }
        if (value >= 1_000_000D)
        {
            return (value / 1_000_000D).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }
        if (value >= 1_000D)
        {
            return (value / 1_000D).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        }
        return value.ToString("0", CultureInfo.InvariantCulture);
    }

    public static string FormatTokenValue(long value)
    {
        var safeValue = Math.Max(0L, value);
        if (safeValue >= 1_000_000_000L)
        {
            return (safeValue / 1_000_000_000D).ToString("0.##", CultureInfo.InvariantCulture) + "B";
        }
        if (safeValue >= 1_000_000L)
        {
            return (safeValue / 1_000_000D).ToString("0.##", CultureInfo.InvariantCulture) + "M";
        }
        if (safeValue >= 1_000L)
        {
            return (safeValue / 1_000D).ToString("0.##", CultureInfo.InvariantCulture) + "K";
        }
        return safeValue.ToString("0", CultureInfo.InvariantCulture);
    }

    public static string FormatUsdAxis(double value, double maximum)
    {
        if (Math.Abs(value) >= 1_000D)
        {
            return "$" + (value / 1_000D).ToString("0.#", CultureInfo.InvariantCulture) + "k";
        }
        if (maximum < 0.1D)
        {
            return "$" + value.ToString("0.000", CultureInfo.InvariantCulture);
        }
        if (maximum < 10D)
        {
            return "$" + value.ToString("0.00", CultureInfo.InvariantCulture);
        }
        if (maximum < 100D)
        {
            return "$" + value.ToString("0.0", CultureInfo.InvariantCulture);
        }
        return "$" + value.ToString("0", CultureInfo.InvariantCulture);
    }

    public static string FormatUsdValue(double value)
    {
        var normalized = SafeCost(value);
        if (normalized >= 1_000D)
        {
            return "$" + normalized.ToString("N0", CultureInfo.CurrentCulture);
        }
        if (normalized >= 10D)
        {
            return "$" + normalized.ToString("0.00", CultureInfo.CurrentCulture);
        }
        if (normalized >= 0.1D)
        {
            return "$" + normalized.ToString("0.000", CultureInfo.CurrentCulture);
        }
        return "$" + normalized.ToString("0.0000", CultureInfo.CurrentCulture);
    }

    public static void DrawFittedText(
        Graphics graphics,
        string text,
        Font baseFont,
        FontStyle style,
        Color color,
        RectangleF bounds,
        StringAlignment horizontalAlignment,
        StringAlignment verticalAlignment,
        float minimumPointSize,
        float? maximumPointSize = null,
        bool allowWrap = false)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 2F || bounds.Height <= 2F)
        {
            return;
        }

        var maximum = Math.Max(
            minimumPointSize,
            maximumPointSize.HasValue
                ? Math.Max(minimumPointSize, maximumPointSize.Value)
                : baseFont.Size);
        Font? selectedFont = null;
        using var format = new StringFormat(StringFormat.GenericDefault)
        {
            Alignment = horizontalAlignment,
            LineAlignment = verticalAlignment,
            Trimming = StringTrimming.None,
            FormatFlags = allowWrap ? 0 : StringFormatFlags.NoWrap
        };

        for (var pointSize = maximum; pointSize >= minimumPointSize; pointSize -= 0.5F)
        {
            var candidate = new Font(baseFont.FontFamily, pointSize, style, GraphicsUnit.Point);
            var measured = allowWrap
                ? graphics.MeasureString(text, candidate, new SizeF(bounds.Width, bounds.Height), format)
                : graphics.MeasureString(text, candidate, int.MaxValue, format);
            var fits = measured.Width <= bounds.Width + 0.5F &&
                       measured.Height <= bounds.Height + 0.5F;
            if (fits)
            {
                selectedFont = candidate;
                break;
            }
            candidate.Dispose();
        }

        selectedFont ??= new Font(
            baseFont.FontFamily,
            Math.Max(4F, minimumPointSize),
            style,
            GraphicsUnit.Point);
        using (selectedFont)
        using (var brush = new SolidBrush(color))
        {
            var finalMeasurement = allowWrap
                ? graphics.MeasureString(text, selectedFont, new SizeF(bounds.Width, bounds.Height), format)
                : graphics.MeasureString(text, selectedFont, int.MaxValue, format);
            if (finalMeasurement.Width > bounds.Width + 1F ||
                finalMeasurement.Height > bounds.Height + 1F)
            {
                // At extreme sizes, omitting an unreadable label is preferable to painting
                // half a glyph outside the control. Normal responsive sizes always fit.
                return;
            }
            graphics.DrawString(text, selectedFont, brush, bounds, format);
        }
    }
}

/// <summary>
/// Deterministic, offline smoke validation for the owner-drawn controls. This method never
/// opens a window and never reads account data; it only renders synthetic values to bitmaps.
/// </summary>
internal static class QuotaDashboardControls
{
    public static void Validate()
    {
        PassiveQuotaGauge.ValidatePlanetGeometryForOfflineRendering();
        using var gauge = new PassiveQuotaGauge
        {
            BackColor = Color.White,
            Size = new Size(180, 150),
            Caption = "5h 官方剩余",
            StatusText = "历史数据采集中"
        };
        RenderToBitmap(gauge);
        gauge.Size = new Size(180, 180);
        gauge.RemainingPercent = 40D;
        gauge.StatusText = "额度正常";
        gauge.AccentColor = Color.FromArgb(12, 190, 125);
        gauge.TrackColor = Color.FromArgb(25, 35, 45);
        RenderToBitmap(gauge);
        // Exercise several deterministic animation phases. The fill assertion deliberately
        // samples broad regions above and below the percentage-controlled level, so it does
        // not depend on whichever phase a UI timer happens to be painting.
        foreach (var phase in new[] { 0F, MathF.Tau * 0.25F, MathF.Tau * 0.5F, MathF.Tau * 0.75F })
        {
            gauge.SetWavePhaseForOfflineValidation(phase);
            ValidateLiquidFill(gauge);
        }
        gauge.RemainingPercent = 1D;
        ValidateVisibleWaveMotion(gauge);
        gauge.RemainingPercent = 58D;
        ValidateDeterministicPlanetMotion(gauge);
        ValidatePlanetRingPresence(gauge);
        ValidateFixedBluePurplePalette(gauge);
        ValidateGaugeCaptionAtHighDpi(gauge);
        ValidateStatusIsNotPaintedInsideGauge(gauge);
        gauge.RemainingPercent = 0D;
        ValidateTerminalFrameHasDecorativeMotion(gauge);
        gauge.RemainingPercent = 100D;
        ValidateTerminalFrameHasDecorativeMotion(gauge);
        gauge.SetWavePhaseForOfflineValidation(0F);
        gauge.RemainingPercent = double.NaN;
        gauge.StatusText = "历史数据采集中";
        RenderToBitmap(gauge);

        using var chart = new QuotaTrendChart
        {
            BackColor = Color.White,
            Size = new Size(160, 140),
            EmptyText = "暂无历史数据"
        };
        QuotaTrendChart.ValidateHighDpiTooltipLayout();
        RenderToBitmap(chart);

        var now = new DateTimeOffset(2026, 7, 12, 12, 0, 0, TimeSpan.FromHours(8));
        chart.Samples =
        [
            new QuotaChartSample(now, 0.04D, 82D, 9_600, TimeSpan.FromHours(1))
        ];
        chart.Metric = QuotaTrendMetric.Tokens;
        RenderToBitmap(chart);

        chart.Size = new Size(680, 300);
        chart.Samples =
        [
            new QuotaChartSample(now.AddHours(-5), 0D, 100D, 9_600, TimeSpan.FromHours(1)),
            new QuotaChartSample(now.AddHours(-4), 0.20D, 40D, 18_200, TimeSpan.FromHours(1)),
            new QuotaChartSample(now.AddHours(-3), 0.01D, 0D, 6_300, TimeSpan.FromHours(1)),
            new QuotaChartSample(now.AddHours(-2), double.NaN, null, 0, TimeSpan.FromHours(1)),
            new QuotaChartSample(now.AddHours(-1), 0.18D, 100D, 25_400, TimeSpan.FromHours(1)),
            new QuotaChartSample(now, 0.02D, 20D, 7_100, TimeSpan.FromHours(1))
        ];
        RenderToBitmap(chart);
        var expectedCostTotal = QuotaDashboardDrawing.FormatUsdValue(0.41D);
        const long expectedTokenTotal = 66_600L;
        var expectedTokenText = QuotaDashboardDrawing.FormatTokenValue(expectedTokenTotal);

        var interpolationBounds = new RectangleF(0F, 0F, 300F, 100F);
        PointF[] interpolationSource =
        [
            new PointF(0F, 100F),
            new PointF(75F, 0F),
            new PointF(150F, 96F),
            new PointF(225F, 4F),
            new PointF(300F, 100F)
        ];
        var smoothCurve = QuotaDashboardDrawing.BuildClampedMonotoneCurve(
            interpolationSource,
            interpolationBounds);
        if (chart.AccessibleDescription?.Contains(expectedCostTotal, StringComparison.Ordinal) != true ||
            chart.AccessibleDescription?.Contains(expectedTokenText, StringComparison.Ordinal) != true ||
            QuotaDashboardDrawing.FormatTokenAxis(50_000_000D) != "50M" ||
            QuotaDashboardDrawing.FormatTokenAxis(1_200_000D) != "1.2M" ||
            QuotaDashboardDrawing.FormatTokenValue(49_980_000L) != "49.98M" ||
            QuotaDashboardDrawing.FormatTokenValue(1_230_000_000L) != "1.23B" ||
            QuotaDashboardDrawing.FormatTokenValue(652_000L) != "652K" ||
            QuotaDashboardDrawing.FormatTokenValue(-1L) != "0" ||
            QuotaDashboardDrawing.FormatUsdValue(0.41D) != expectedCostTotal ||
            Math.Abs(QuotaDashboardDrawing.NiceMaximum(0.16D) - 0.2D) > 0.000_001D ||
            QuotaDashboardDrawing.SafeCost(double.NaN) != 0D ||
            QuotaDashboardDrawing.SafeCost(-1D) != 0D ||
            smoothCurve.Length <= interpolationSource.Length ||
            smoothCurve.Any(point =>
                !float.IsFinite(point.X) ||
                !float.IsFinite(point.Y) ||
                point.X < interpolationBounds.Left ||
                point.X > interpolationBounds.Right ||
                point.Y < interpolationBounds.Top ||
                point.Y > interpolationBounds.Bottom))
        {
            throw new InvalidOperationException(
                "Quota dashboard numeric normalization or clamped smoothing failed.");
        }
    }

    private static void ValidateLiquidFill(PassiveQuotaGauge gauge)
    {
        using var bitmap = new Bitmap(
            Math.Max(1, gauge.Width),
            Math.Max(1, gauge.Height));
        gauge.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));

        // At 40% remaining, the lower part of the glass sphere must contain materially
        // more saturated blue-purple liquid than the upper part. AccentColor is deliberately
        // not used here: normal and abnormal accounts share the exact same gauge palette.
        var lowerLiquidPixels = 0;
        var upperLiquidPixels = 0;
        var left = (int)(bitmap.Width * 0.18D);
        var right = (int)(bitmap.Width * 0.82D);
        for (var y = (int)(bitmap.Height * 0.14D); y < (int)(bitmap.Height * 0.86D); y++)
        {
            for (var x = left; x < right; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                var bluePurpleLiquid =
                    pixel.B >= 145 &&
                    pixel.B - pixel.R >= 30 &&
                    pixel.B - pixel.G >= 22;
                if (bluePurpleLiquid)
                {
                    if (y >= bitmap.Height * 0.62D)
                    {
                        lowerLiquidPixels++;
                    }
                    else if (y <= bitmap.Height * 0.38D)
                    {
                        upperLiquidPixels++;
                    }
                }
            }
        }
        if (lowerLiquidPixels < 160 || lowerLiquidPixels <= upperLiquidPixels * 2)
        {
            throw new InvalidOperationException(
                "Passive quota gauge did not paint a level-controlled liquid fill.");
        }
    }

    private static void ValidateVisibleWaveMotion(PassiveQuotaGauge gauge)
    {
        using var first = new Bitmap(Math.Max(1, gauge.Width), Math.Max(1, gauge.Height));
        using var second = new Bitmap(Math.Max(1, gauge.Width), Math.Max(1, gauge.Height));
        gauge.SetWavePhaseForOfflineValidation(0F);
        gauge.DrawToBitmap(first, new Rectangle(Point.Empty, first.Size));
        gauge.SetWavePhaseForOfflineValidation(MathF.Tau * 0.31F);
        gauge.DrawToBitmap(second, new Rectangle(Point.Empty, second.Size));

        var changedPixels = 0;
        for (var y = (int)(first.Height * 0.72D); y < (int)(first.Height * 0.92D); y++)
        {
            for (var x = (int)(first.Width * 0.18D); x < (int)(first.Width * 0.82D); x++)
            {
                if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb())
                {
                    changedPixels++;
                }
            }
        }

        if (changedPixels < 12)
        {
            throw new InvalidOperationException(
                "Passive quota gauge wave is not visibly animated at a 1% liquid level.");
        }
    }

    private static void ValidateDeterministicPlanetMotion(PassiveQuotaGauge gauge)
    {
        using var first = RenderGaugeFrame(gauge, MathF.Tau * 0.18F);
        using var repeated = RenderGaugeFrame(gauge, MathF.Tau * 0.18F);
        using var advanced = RenderGaugeFrame(gauge, MathF.Tau * 0.61F);
        var repeatedDifferences = CountChangedPixels(first, repeated);
        var animatedDifferences = CountChangedPixels(first, advanced);
        if (repeatedDifferences != 0 || animatedDifferences < 120)
        {
            throw new InvalidOperationException(
                "Passive quota planet animation is not deterministic or visible.");
        }
    }

    private static void ValidatePlanetRingPresence(PassiveQuotaGauge gauge)
    {
        var previousSize = gauge.Size;
        gauge.Size = new Size(260, 210);
        gauge.RemainingPercent = 0D;
        using var frame = RenderGaugeFrame(gauge, MathF.Tau * 0.31F);
        var geometry = PassiveQuotaGauge.GetPlanetGeometryForOfflineRendering(frame.Size, 1F);
        var ringPixelsOutsideSphere = 0;
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                if (geometry.SphereBounds.Contains(x, y))
                {
                    continue;
                }

                var pixel = frame.GetPixel(x, y);
                if (pixel.B >= 150 &&
                    pixel.B - pixel.R >= 35 &&
                    pixel.B - pixel.G >= 20)
                {
                    ringPixelsOutsideSphere++;
                }
            }
        }

        gauge.Size = previousSize;
        gauge.RemainingPercent = 58D;
        if (ringPixelsOutsideSphere < 90)
        {
            throw new InvalidOperationException(
                "Passive quota planet did not paint a visible Saturn-style ring outside the sphere.");
        }
    }

    private static void ValidateFixedBluePurplePalette(PassiveQuotaGauge gauge)
    {
        gauge.RemainingPercent = 58D;
        gauge.StatusText = "额度正常";
        gauge.AccentColor = Color.FromArgb(16, 185, 129);
        using var normalFrame = RenderGaugeFrame(gauge, MathF.Tau * 0.23F);
        gauge.StatusText = "额度异常";
        gauge.AccentColor = Color.FromArgb(239, 68, 68);
        using var abnormalFrame = RenderGaugeFrame(gauge, MathF.Tau * 0.23F);
        if (CountChangedPixels(normalFrame, abnormalFrame) != 0)
        {
            throw new InvalidOperationException(
                "Passive quota gauge palette must remain blue-purple regardless of quota health accent.");
        }

        var saturatedBluePurplePixels = 0;
        for (var y = 0; y < normalFrame.Height; y++)
        {
            for (var x = 0; x < normalFrame.Width; x++)
            {
                var pixel = normalFrame.GetPixel(x, y);
                if (pixel.B >= 150 &&
                    pixel.B - pixel.R >= 28 &&
                    pixel.B - pixel.G >= 20)
                {
                    saturatedBluePurplePixels++;
                }
            }
        }
        if (saturatedBluePurplePixels < 900)
        {
            throw new InvalidOperationException(
                "Passive quota gauge did not paint a materially visible blue-purple gradient.");
        }

        gauge.RemainingPercent = null;
        gauge.StatusText = "未测量";
        gauge.AccentColor = Color.FromArgb(148, 163, 184);
        using var unmeasuredNeutralFrame = RenderGaugeFrame(gauge, 0F);
        gauge.AccentColor = Color.FromArgb(239, 68, 68);
        using var unmeasuredWarningFrame = RenderGaugeFrame(gauge, 0F);
        if (CountChangedPixels(unmeasuredNeutralFrame, unmeasuredWarningFrame) != 0)
        {
            throw new InvalidOperationException(
                "Unmeasured quota gauge must keep the same blue-purple glass palette regardless of accent.");
        }

        gauge.RemainingPercent = 58D;
    }

    private static void ValidateGaugeCaptionAtHighDpi(PassiveQuotaGauge gauge)
    {
        gauge.Size = new Size(190, 190);
        gauge.RemainingPercent = 97D;
        gauge.Caption = "周剩余";
        gauge.SetDpiScaleForOfflineValidation(2F);
        gauge.SetCaptionVisibleForOfflineValidation(false);
        using var withoutCaption = RenderGaugeFrame(gauge, MathF.Tau * 0.21F);
        gauge.SetCaptionVisibleForOfflineValidation(true);
        using var withCaption = RenderGaugeFrame(gauge, MathF.Tau * 0.21F);

        var changedPixels = 0;
        var minX = withCaption.Width;
        var maxX = -1;
        var minY = withCaption.Height;
        var maxY = -1;
        for (var y = 0; y < withCaption.Height; y++)
        {
            for (var x = 0; x < withCaption.Width; x++)
            {
                if (withoutCaption.GetPixel(x, y).ToArgb() == withCaption.GetPixel(x, y).ToArgb())
                {
                    continue;
                }

                changedPixels++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        gauge.SetDpiScaleForOfflineValidation(null);
        gauge.SetCaptionVisibleForOfflineValidation(true);
        gauge.RemainingPercent = 58D;
        if (changedPixels < 40 ||
            maxX - minX < 24 ||
            minY < withCaption.Height * 0.48D ||
            maxY > withCaption.Height * 0.80D)
        {
            throw new InvalidOperationException(
                "Passive quota gauge caption is missing, clipped, or overlapping the percentage at 200% DPI.");
        }
    }

    private static void ValidateStatusIsNotPaintedInsideGauge(PassiveQuotaGauge gauge)
    {
        gauge.StatusText = "额度正常";
        using var normalFrame = RenderGaugeFrame(gauge, MathF.Tau * 0.37F);
        gauge.StatusText = "额度异常";
        using var abnormalFrame = RenderGaugeFrame(gauge, MathF.Tau * 0.37F);
        if (CountChangedPixels(normalFrame, abnormalFrame) != 0)
        {
            throw new InvalidOperationException(
                "Quota health text must be rendered outside the gauge; the sphere only paints percentage and caption.");
        }
    }

    private static void ValidateTerminalFrameHasDecorativeMotion(PassiveQuotaGauge gauge)
    {
        using var first = RenderGaugeFrame(gauge, 0F);
        using var advanced = RenderGaugeFrame(gauge, MathF.Tau * 0.67F);
        if (CountChangedPixels(first, advanced) < 120)
        {
            throw new InvalidOperationException(
                "Passive quota gauge terminal levels must keep their fixed value while decorative energy remains animated.");
        }
    }

    private static Bitmap RenderGaugeFrame(PassiveQuotaGauge gauge, float phase)
    {
        gauge.SetWavePhaseForOfflineValidation(phase);
        var bitmap = new Bitmap(Math.Max(1, gauge.Width), Math.Max(1, gauge.Height));
        gauge.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }

    private static int CountChangedPixels(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size)
        {
            return int.MaxValue;
        }

        var changedPixels = 0;
        for (var y = 0; y < first.Height; y++)
        {
            for (var x = 0; x < first.Width; x++)
            {
                if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb())
                {
                    changedPixels++;
                }
            }
        }
        return changedPixels;
    }

    private static void RenderToBitmap(Control control)
    {
        using var bitmap = new Bitmap(
            Math.Max(1, control.Width),
            Math.Max(1, control.Height));
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
    }
}
