using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace CodexAccountManager;

/// <summary>
/// One locally observed model-usage aggregate. Records are token_count records rather than
/// guaranteed HTTP request counts; EquivalentCostUsd is an API-equivalent estimate only.
/// </summary>
internal sealed record ModelUsageDistributionItem(
    string Model,
    int Records,
    long TotalTokens,
    double EquivalentCostUsd);

/// <summary>
/// A double-buffered, local-only model distribution card. Every positive model receives a
/// complete identity ring while exact Token share remains textual in the centre and table;
/// no polling, model call, login, or network request is performed by this control.
/// </summary>
internal sealed class ModelUsageDistributionControl : Control
{
    // WinForms reports the runtime control width in device pixels while our visual metrics
    // are DPI-scaled.  A 780 logical-pixel threshold keeps the common 125-200% DPI quota
    // detail window in the intended left-visual/right-detail layout, without forcing the
    // genuinely narrow card into two columns.
    private const int WideLayoutThreshold = 780;
    private const int TwoLineTableThreshold = 560;
    private const int MaxNamedRings = 4;
    private const int NoHoverIndex = -1;
    private const int OtherGroupHoverIndex = -2;
    private const float RingSweepAngle = 360F;
    private const float RingStartAngle = -90F;
    private const int CometTailSegmentCount = 5;
    // The sky meteors use one more short history segment than the orbiting ring comet.  This
    // preserves a visibly curved tail without growing the fixed per-frame rendering budget.
    private const int BackdropMeteorTrailSegmentCount = 6;
    // The static scene is cached, so 30 FPS keeps the curved atmospheric motion fluid while
    // still leaving the WinForms UI thread responsive during scrolling and window interaction.
    private const int ActiveAnimationIntervalMilliseconds = 33;
    private const int InactiveAnimationIntervalMilliseconds = 64;
    private const double CometOrbitPeriodSeconds = 8.8D;
    // Keep every animated orbit inside its reserved lane at the narrowest supported
    // layout.  A 2.2% breath is still perceptible, while the previous 3.2% could let
    // four dense rings touch at one animation phase on a 440px card.
    private const float OrbitBreathAmplitude = 0.022F;
    private const float FixedPlanetRadiusLogicalPixels = 72F;
    private const float MaximumPlanetRadiusStageRatio = 0.25F;
    private const int MaximumPlanetDustCount = 4;
    private const float PlanetOrbitStrokeRatio = 0.065F;
    private const float PlanetOrbitMinimumDpiWidth = 0.75F;
    private const float PlanetOrbitMaximumDpiWidth = 1.20F;
    private const float PlanetUsageStrokeRatio = 0.68F;
    private const float PlanetUsageMinimumDpiWidth = 7.00F;
    private const float PlanetUsageMaximumDpiWidth = 12.00F;
    private static readonly Color SolRingColor = Color.FromArgb(55, 124, 255);
    private static readonly Color TerraRingColor = Color.FromArgb(113, 104, 255);
    private static readonly Color LunaRingColor = Color.FromArgb(199, 92, 255);
    private static readonly Color Gpt55RingColor = Color.FromArgb(49, 213, 200);
    private static readonly Color OtherRingColor = Color.FromArgb(244, 116, 210);
    // The white surface keeps a clean glass treatment while every model family still has
    // a clearly identifiable hue: cyan, indigo, violet, teal, and rose respectively.
    private static readonly Color LightSolRingColor = Color.FromArgb(59, 130, 246);
    private static readonly Color LightTerraRingColor = Color.FromArgb(99, 102, 241);
    private static readonly Color LightLunaRingColor = Color.FromArgb(168, 85, 247);
    private static readonly Color LightGpt55RingColor = Color.FromArgb(20, 184, 166);
    private static readonly Color LightOtherRingColor = Color.FromArgb(216, 99, 169);
    private static readonly Color DarkSolRingColor = Color.FromArgb(40, 100, 255);
    private static readonly Color DarkTerraRingColor = Color.FromArgb(139, 77, 255);
    private static readonly Color DarkLunaRingColor = Color.FromArgb(240, 91, 216);
    private static readonly Color DarkGpt55RingColor = Color.FromArgb(32, 200, 255);
    private static readonly Color WarmOrbitAccentColor = Color.FromArgb(255, 154, 77);
    private static readonly Color NeutralOrbitColor = Color.FromArgb(96, 114, 146);
    private static readonly (float X, float Y, float Radius, bool Violet)[] OrbitalBackdropStars =
    [
        (0.07F, 0.23F, 0.62F, false),
        (0.12F, 0.67F, 0.82F, true),
        (0.19F, 0.42F, 0.48F, false),
        (0.26F, 0.84F, 0.68F, true),
        (0.34F, 0.12F, 0.52F, true),
        (0.43F, 0.71F, 0.44F, false),
        (0.51F, 0.25F, 0.74F, false),
        (0.60F, 0.89F, 0.58F, true),
        (0.67F, 0.09F, 0.65F, false),
        (0.73F, 0.55F, 0.46F, true),
        (0.81F, 0.19F, 0.72F, true),
        (0.87F, 0.78F, 0.86F, false),
        (0.93F, 0.36F, 0.48F, false),
        (0.96F, 0.62F, 0.58F, true)
    ];
    private ModelUsageDistributionItem[] _items = [];
    private Color _surfaceColor = Color.White;
    private Color _borderColor = Color.FromArgb(226, 232, 240);
    private Color _textColor = Color.FromArgb(30, 41, 59);
    private Color _mutedColor = Color.FromArgb(100, 116, 139);
    private Color _primaryColor = Color.FromArgb(59, 130, 246);
    private Color _secondaryColor = Color.FromArgb(99, 102, 241);
    private Color _tertiaryColor = Color.FromArgb(139, 92, 246);
    private Color _accentColor = Color.FromArgb(6, 182, 212);
    private string _rangeLabel = "本月";
    private int _hoveredIndex = NoHoverIndex;
    private RectangleF[] _lastRowBounds = [];
    private RingHitTarget[] _lastRingTargets = [];
    private readonly System.Windows.Forms.Timer _animationTimer;
    private readonly Stopwatch _animationClock = new();
    private Form? _animationHostForm;
    private RectangleF _lastAnimationOuterBounds = RectangleF.Empty;
    private RectangleF _lastAnimationInnerBounds = RectangleF.Empty;
    private RectangleF _lastMeteorAnimationBounds = RectangleF.Empty;
    private TimeSpan _animationElapsed = TimeSpan.Zero;
    private float _animationPhase;
    private float? _validationDpiOverride;
    private Bitmap? _staticDonutCache;
    private Size _staticDonutCacheSize;
    private RectangleF _staticDonutCacheBounds = RectangleF.Empty;
    private float _staticDonutCacheDpi;
    private int _staticDonutCacheVersion = -1;
    private int _donutVisualVersion;
    private bool _isRenderingStaticDonutCache;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<ModelUsageDistributionItem> Items
    {
        get => _items;
        set
        {
            var normalized = value?
                .Where(item => item != null && (item.TotalTokens > 0 || item.Records > 0))
                .OrderByDescending(item => item.TotalTokens)
                .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
            if (_items.SequenceEqual(normalized))
            {
                return;
            }

            _items = normalized;
            if ((_hoveredIndex >= _items.Length && _hoveredIndex != OtherGroupHoverIndex) ||
                (_hoveredIndex == OtherGroupHoverIndex && CountPositiveTokenItems(_items) <= MaxNamedRings))
            {
                _hoveredIndex = NoHoverIndex;
            }
            // A timer tick can already be queued when usage changes from populated to empty.
            // Clear every retained partial-paint target before stopping the clock so that a
            // stale annular invalidation can never clip the subsequent empty-state repaint.
            _lastAnimationOuterBounds = RectangleF.Empty;
            _lastAnimationInnerBounds = RectangleF.Empty;
            _lastMeteorAnimationBounds = RectangleF.Empty;
            _lastRingTargets = [];
            _lastRowBounds = [];
            InvalidateStaticDonutCache();
            UpdateAccessibility();
            UpdateAnimationState();
            Invalidate(ClientRectangle, false);
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SurfaceColor
    {
        get => _surfaceColor;
        set
        {
            if (_surfaceColor == value)
            {
                return;
            }
            _surfaceColor = value;
            BackColor = value;
            InvalidateStaticDonutCache();
            UpdateAnimationState();
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor
    {
        get => _borderColor;
        set => SetColor(ref _borderColor, value);
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

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PrimaryColor
    {
        get => _primaryColor;
        set => SetColor(ref _primaryColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SecondaryColor
    {
        get => _secondaryColor;
        set => SetColor(ref _secondaryColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TertiaryColor
    {
        get => _tertiaryColor;
        set => SetColor(ref _tertiaryColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentColor
    {
        get => _accentColor;
        set => SetColor(ref _accentColor, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string RangeLabel
    {
        get => _rangeLabel;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "本月" : value.Trim();
            if (string.Equals(_rangeLabel, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _rangeLabel = normalized;
            InvalidateStaticDonutCache();
            UpdateAccessibility();
            Invalidate();
        }
    }

    public ModelUsageDistributionControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.Opaque,
            true);
        DoubleBuffered = true;
        BackColor = _surfaceColor;
        _animationTimer = new System.Windows.Forms.Timer
        {
            Interval = ActiveAnimationIntervalMilliseconds
        };
        _animationTimer.Tick += AnimationTimer_Tick;
        MinimumSize = new Size(320, 300);
        TabStop = false;
        Cursor = Cursors.Default;
        UpdateAccessibility();
    }

    internal void RefreshAnimationStateForViewport() => UpdateAnimationState();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        BindAnimationHost();
        UpdateAnimationState();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _animationTimer.Stop();
        PauseAnimationClock();
        UnbindAnimationHost();
        base.OnHandleDestroyed(e);
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        BindAnimationHost();
        UpdateAnimationState();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateAnimationState();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateStaticDonutCache();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        InvalidateStaticDonutCache();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Stop();
            PauseAnimationClock();
            _animationTimer.Tick -= AnimationTimer_Tick;
            _animationTimer.Dispose();
            UnbindAnimationHost();
            DisposeStaticDonutCache();
        }
        base.Dispose(disposing);
    }

    internal static int GetPreferredHeight(int width, int itemCount, float dpiScale)
    {
        var scale = IsFinitePositive(dpiScale) ? Math.Max(1F, dpiScale) : 1F;
        var safeWidth = Math.Max(1, width);
        var rows = Math.Max(1, itemCount);
        var wide = safeWidth >= Scale(WideLayoutThreshold, scale);
        var twoLineRows = !wide && safeWidth < Scale(TwoLineTableThreshold, scale);
        var header = Scale(72, scale);
        var donut = Scale(wide ? 360 : 260, scale);
        var tableHeader = Scale(38, scale);
        var rowHeight = Scale(twoLineRows ? 62 : wide ? 64 : 48, scale);
        var bottom = Scale(24, scale);

        return wide
            ? header + Math.Max(donut, tableHeader + (rows * rowHeight)) + bottom
            : header + donut + Scale(18, scale) + tableHeader + (rows * rowHeight) + bottom;
    }

    internal static void ValidateResponsiveLayout()
    {
        foreach (var scale in new[] { 1F, 1.25F, 1.5F, 2F, 4F })
        {
            foreach (var logicalWidth in new[] { 440, 680, 920, 1200 })
            {
                foreach (var itemCount in new[] { 0, 1, 2, 3, 4, 5, 8 })
                {
                    var width = Scale(logicalWidth, scale);
                    var height = GetPreferredHeight(width, itemCount, scale);
                    var layout = CalculateLayout(new RectangleF(0, 0, width, height), scale, itemCount);
                    var ringCount = Math.Min(5, Math.Max(0, itemCount));
                    var geometry = CalculateRingGeometry(layout.Donut, scale, ringCount);
                    if (!Contains(layout.Outer, layout.Title) ||
                        !Contains(layout.Outer, layout.Subtitle) ||
                        !Contains(layout.Outer, layout.Donut) ||
                        !Contains(layout.Outer, layout.Table) ||
                        layout.Donut.Width < Scale(170, scale) ||
                        layout.Donut.Height < Scale(170, scale) ||
                        layout.RowHeight < Scale(46, scale) ||
                        (layout.IsWide && layout.Donut.Right > layout.Table.Left) ||
                        (!layout.IsWide && layout.Donut.Bottom > layout.Table.Top) ||
                        (ringCount > 0 &&
                         (geometry.Radii.Length != ringCount ||
                          geometry.StrokeWidth < (5F * scale) ||
                          geometry.CenterRadius < Scale(34, scale) ||
                          !Contains(layout.Donut, geometry.OuterBounds))))
                    {
                        throw new InvalidOperationException(
                            $"Model distribution layout clips at {logicalWidth}px / {scale * 100F:0}% DPI / {itemCount} items.");
                    }

                    var usageStroke = CalculatePlanetUsageStroke(
                        geometry.StrokeWidth,
                        scale,
                        ringCount);
                    for (var index = 1; index < geometry.Radii.Length; index++)
                    {
                        var outerEnvelope = CalculateMaximumVisualHalfWidth(
                            usageStroke,
                            scale,
                            ShouldDrawOrbitSatellite(index - 1, ringCount));
                        var innerEnvelope = CalculateMaximumVisualHalfWidth(
                            usageStroke,
                            scale,
                            ShouldDrawOrbitSatellite(index, ringCount));
                        var centerLineDistance = geometry.Radii[index - 1] - geometry.Radii[index];
                        if (centerLineDistance <
                            outerEnvelope + innerEnvelope + Scale(3, scale))
                        {
                            throw new InvalidOperationException(
                                $"Model distribution ring effects overlap at {logicalWidth}px / " +
                                $"{scale * 100F:0}% DPI / {ringCount} rings.");
                        }
                    }

                    if (geometry.Radii.Length > 1)
                    {
                        for (var frame = 0; frame < 64; frame++)
                        {
                            var phase = frame / 64F;
                            for (var index = 1; index < geometry.Radii.Length; index++)
                            {
                                var outerEnvelope = CalculateMaximumVisualHalfWidth(
                                    usageStroke,
                                    scale,
                                    ShouldDrawOrbitSatellite(index - 1, ringCount));
                                var innerEnvelope = CalculateMaximumVisualHalfWidth(
                                    usageStroke,
                                    scale,
                                    ShouldDrawOrbitSatellite(index, ringCount));
                                var outerAnimatedRadius = geometry.Radii[index - 1] *
                                    CalculateOrbitBreathScale(index - 1, ringCount, phase);
                                var innerAnimatedRadius = geometry.Radii[index] *
                                    CalculateOrbitBreathScale(index, ringCount, phase);
                                if (outerAnimatedRadius - innerAnimatedRadius <
                                    outerEnvelope + innerEnvelope + Scale(3, scale))
                                {
                                    throw new InvalidOperationException(
                                        $"Breathing model rings overlap at {logicalWidth}px / " +
                                        $"{scale * 100F:0}% DPI / {ringCount} rings / phase {phase:0.###}.");
                                }
                            }
                        }
                    }
                }
            }
        }

        ValidateArcPresentation();
    }

    internal static void ValidateOffscreenRendering()
    {
        IReadOnlyList<ModelUsageDistributionItem>[] samples =
        {
            new[]
            {
                new ModelUsageDistributionItem("gpt-5.6-sol", 82, 98_400_000L, 76.23D)
            },
            new[]
            {
                // Mirrors the two-ring proportions observed in the real quota detail card.
                new ModelUsageDistributionItem("gpt-5.6-sol", 81, 95_470_000L, 74.44D),
                new ModelUsageDistributionItem("gpt-5.6-luna", 9, 6_960_000L, 4.31D)
            },
            new[]
            {
                // The last ring is deliberately sub-pixel.  It must be represented in the
                // table without feeding a degenerate short arc to GDI+.
                new ModelUsageDistributionItem("gpt-5.6-sol", 112, 31_530_000L, 27.94D),
                new ModelUsageDistributionItem("gpt-5.6-terra", 24, 3_620_000L, 2.82D),
                new ModelUsageDistributionItem("gpt-5.6-luna", 13, 1_410_000L, 1.07D),
                new ModelUsageDistributionItem("gpt-5.5", 3, 91_000L, 0.05D),
                new ModelUsageDistributionItem("custom-model", 1, 1L, 0D)
            },
            Array.Empty<ModelUsageDistributionItem>()
        };
        var phases = new[] { 0F, 0.001F, 0.249F, 0.501F, 0.751F, 0.999F };
        foreach (var sample in samples)
        {
            using var control = new ModelUsageDistributionControl
            {
                Font = SystemFonts.MessageBoxFont,
                Items = sample
            };
            foreach (var dpi in new[] { 1F, 1.25F, 1.5F, 2F, 4F })
            {
                var logicalWidths = dpi >= 4F
                    ? new[] { 440 }
                    : new[] { 440, 680, 920 };
                foreach (var logicalWidth in logicalWidths)
                {
                    var width = Scale(logicalWidth, dpi);
                    var height = GetPreferredHeight(width, sample.Count, dpi);
                    control.Size = new Size(width, height);
                    using var bitmap = new Bitmap(width, height);
                    bitmap.SetResolution(96F * dpi, 96F * dpi);
                    using var graphics = Graphics.FromImage(bitmap);
                    foreach (var phase in phases)
                    {
                        try
                        {
                            control._animationPhase = phase;
                            RenderValidationFrame(control, graphics, width, height, dpi);
                        }
                        catch (Exception exception)
                        {
                            throw new InvalidOperationException(
                                $"Model distribution render failed with {sample.Count} rings, " +
                                $"{logicalWidth}px, {dpi * 100F:0}% DPI, phase {phase:0.###}.",
                                exception);
                        }
                    }
                }
            }
        }

        // Regression for the field failure where a queued annular repaint survived after
        // usage became empty and clipped the placeholder planet to a horizontal strip.
        using (var transitionControl = new ModelUsageDistributionControl
               {
                   Font = SystemFonts.MessageBoxFont,
                   Items = samples[1]
               })
        {
            const int transitionWidth = 920;
            var transitionHeight = GetPreferredHeight(transitionWidth, samples[1].Count, 1F);
            using var transitionBitmap = new Bitmap(transitionWidth, transitionHeight);
            using var transitionGraphics = Graphics.FromImage(transitionBitmap);
            RenderValidationFrame(
                transitionControl,
                transitionGraphics,
                transitionWidth,
                transitionHeight,
                1F);
            if (!IsDrawableBounds(transitionControl._lastAnimationOuterBounds))
            {
                throw new InvalidOperationException(
                    "Populated model distribution must establish an animation envelope.");
            }

            transitionControl.Items = [];
            var clearedOuterBounds = transitionControl._lastAnimationOuterBounds;
            var clearedInnerBounds = transitionControl._lastAnimationInnerBounds;
            var clearedMeteorBounds = transitionControl._lastMeteorAnimationBounds;
            if (!clearedOuterBounds.IsEmpty ||
                !clearedInnerBounds.IsEmpty ||
                !clearedMeteorBounds.IsEmpty ||
                transitionControl._lastRingTargets.Length != 0)
            {
                throw new InvalidOperationException(
                    "Empty model distribution must clear every retained partial-paint target immediately.");
            }
            RenderValidationFrame(
                transitionControl,
                transitionGraphics,
                transitionWidth,
                GetPreferredHeight(transitionWidth, 0, 1F),
                1F);
        }

        // Continuous two-ring animation pressure test.  This is intentionally a single
        // retained bitmap/Graphics pair: it reproduces the runtime repaint pattern and
        // catches native resource churn that isolated screenshots cannot expose.
        var stressSample = samples[1];
        using var stressControl = new ModelUsageDistributionControl
        {
            Font = SystemFonts.MessageBoxFont,
            Items = stressSample
        };
        const float stressDpi = 1.25F;
        var stressWidth = Scale(920, stressDpi);
        var stressHeight = GetPreferredHeight(stressWidth, stressSample.Count, stressDpi);
        stressControl.Size = new Size(stressWidth, stressHeight);
        using var stressBitmap = new Bitmap(stressWidth, stressHeight);
        stressBitmap.SetResolution(96F * stressDpi, 96F * stressDpi);
        using var stressGraphics = Graphics.FromImage(stressBitmap);
        for (var frame = 0; frame < 180; frame++)
        {
            stressControl._animationPhase = CalculateAnimationPhase(frame / 30D);
            RenderValidationFrame(
                stressControl,
                stressGraphics,
                stressWidth,
                stressHeight,
                stressDpi);
        }

        // Reproduce the failing field geometry exactly: WinForms reported Size=825x526
        // while DeviceDpi was 192.  Do not multiply Size by dpi here; that mistake made the
        // old test exercise a much larger, safer canvas and miss the GDI+ failure.
        IReadOnlyList<ModelUsageDistributionItem> runtimeSample =
        [
            new ModelUsageDistributionItem("gpt-5.6-sol", 146, 31_530_000L, 27.94D),
            new ModelUsageDistributionItem("gpt-5.6-terra", 31, 3_620_000L, 2.82D),
            new ModelUsageDistributionItem("gpt-5.6-luna", 14, 1_410_000L, 1.07D)
        ];
        const int runtimeWidth = 825;
        const int runtimeHeight = 526;
        const float runtimeDpi = 2F;
        using var runtimeControl = new ModelUsageDistributionControl
        {
            Size = new Size(runtimeWidth, runtimeHeight),
            Font = SystemFonts.MessageBoxFont,
            Items = runtimeSample
        };
        using var runtimeBitmap = new Bitmap(runtimeWidth, runtimeHeight);
        runtimeBitmap.SetResolution(96F * runtimeDpi, 96F * runtimeDpi);
        using var runtimeGraphics = Graphics.FromImage(runtimeBitmap);
        runtimeControl.CreateControl();
        runtimeControl._validationDpiOverride = runtimeDpi;
        using (var runtimeFullPaint = new PaintEventArgs(
                   runtimeGraphics,
                   new Rectangle(0, 0, runtimeWidth, runtimeHeight)))
        {
            runtimeControl.OnPaint(runtimeFullPaint);
        }
        using var runtimeInnerPath = new GraphicsPath();
        runtimeInnerPath.AddEllipse(runtimeControl._lastAnimationInnerBounds);
        using var runtimeAnimationRegion = new Region(runtimeControl._lastAnimationOuterBounds);
        runtimeAnimationRegion.Exclude(runtimeInnerPath);
        var runtimeAnimationClip = Rectangle.Ceiling(runtimeControl._lastAnimationOuterBounds);
        for (var frame = 0; frame < 240; frame++)
        {
            runtimeControl._animationPhase = CalculateAnimationPhase(frame / 30D);
            runtimeGraphics.ResetClip();
            runtimeGraphics.SetClip(runtimeAnimationRegion, CombineMode.Replace);
            using var runtimePartialPaint = new PaintEventArgs(
                runtimeGraphics,
                runtimeAnimationClip);
            runtimeControl.OnPaint(runtimePartialPaint);
        }
        runtimeGraphics.ResetClip();
        runtimeControl._validationDpiOverride = null;

        // Also exercise the actual partial OnPaint path used by the animation Timer.  Its
        // annular clip must repaint the planets while skipping header/table allocations.
        const int partialWidth = 920;
        var partialHeight = GetPreferredHeight(partialWidth, stressSample.Count, 1F);
        using var partialControl = new ModelUsageDistributionControl
        {
            Size = new Size(partialWidth, partialHeight),
            Font = SystemFonts.MessageBoxFont,
            Items = runtimeSample
        };
        partialControl.CreateControl();
        using var partialBitmap = new Bitmap(partialWidth, partialHeight);
        using var partialGraphics = Graphics.FromImage(partialBitmap);
        using (var fullPaint = new PaintEventArgs(
                   partialGraphics,
                   new Rectangle(0, 0, partialWidth, partialHeight)))
        {
            partialControl.OnPaint(fullPaint);
        }
        using var innerPath = new GraphicsPath();
        innerPath.AddEllipse(partialControl._lastAnimationInnerBounds);
        using var animationRegion = new Region(partialControl._lastAnimationOuterBounds);
        animationRegion.Exclude(innerPath);
        var animationClip = Rectangle.Ceiling(partialControl._lastAnimationOuterBounds);
        for (var frame = 0; frame < 180; frame++)
        {
            partialControl._animationPhase = CalculateAnimationPhase(frame / 30D);
            partialGraphics.ResetClip();
            partialGraphics.SetClip(animationRegion, CombineMode.Replace);
            using var partialPaint = new PaintEventArgs(partialGraphics, animationClip);
            partialControl.OnPaint(partialPaint);
        }
        partialGraphics.ResetClip();

        // At 400% DPI a transient physical size can be much smaller than the minimum
        // logical painting surface while a top-level window is being restored.  The real
        // OnPaint path exits early; this guard test ensures no invalid brush/arc bounds are
        // constructed during that frame.
        using var tinyControl = new ModelUsageDistributionControl
        {
            MinimumSize = Size.Empty,
            Size = new Size(320, 220),
            Font = SystemFonts.MessageBoxFont,
            Items = stressSample
        };
        using var tinyBitmap = new Bitmap(320, 220);
        using var tinyGraphics = Graphics.FromImage(tinyBitmap);
        RenderValidationFrame(tinyControl, tinyGraphics, 320, 220, 4F);

        // The reference-inspired deep-space treatment has theme-specific bloom, planet and
        // energy-edge colours.  Exercise that branch explicitly so a future palette change
        // cannot leave the dark card with invalid alpha or gradient bounds.
        using var darkControl = new ModelUsageDistributionControl
        {
            Size = new Size(920, GetPreferredHeight(920, runtimeSample.Count, 1F)),
            Font = SystemFonts.MessageBoxFont,
            Items = runtimeSample,
            SurfaceColor = Color.FromArgb(18, 36, 59),
            BorderColor = Color.FromArgb(42, 70, 108),
            TextColor = Color.FromArgb(245, 247, 251),
            MutedColor = Color.FromArgb(166, 182, 204),
            PrimaryColor = Color.FromArgb(47, 107, 255),
            SecondaryColor = Color.FromArgb(96, 165, 250),
            TertiaryColor = Color.FromArgb(167, 139, 250),
            AccentColor = Color.FromArgb(34, 211, 238)
        };
        using var darkBitmap = new Bitmap(darkControl.Width, darkControl.Height);
        using var darkGraphics = Graphics.FromImage(darkBitmap);
        foreach (var phase in new[] { 0F, 0.5F, 0.999F })
        {
            darkControl._animationPhase = phase;
            RenderValidationFrame(
                darkControl,
                darkGraphics,
                darkControl.Width,
                darkControl.Height,
                1F);
        }
    }

    internal static void RenderSyntheticPreview(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Preview output path is required.", nameof(outputPath));
        }

        IReadOnlyList<ModelUsageDistributionItem> previewItems =
        [
            new ModelUsageDistributionItem("gpt-5.6-sol", 146, 214_050_000L, 160.93D),
            new ModelUsageDistributionItem("gpt-5.6-terra", 31, 5_420_000L, 4.82D),
            new ModelUsageDistributionItem("gpt-5.6-luna", 14, 2_110_000L, 1.77D),
            new ModelUsageDistributionItem("gpt-5.5", 7, 530_000L, 0.38D)
        ];
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        RenderSyntheticPreviewVariant(fullPath, previewItems, darkSurface: false);

        var directory = Path.GetDirectoryName(fullPath)!;
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var extension = Path.GetExtension(fullPath);
        RenderSyntheticPreviewVariant(
            Path.Combine(directory, stem + "-light-empty" + extension),
            [],
            darkSurface: false);
        RenderSyntheticPreviewVariant(
            Path.Combine(directory, stem + "-dark-data" + extension),
            previewItems,
            darkSurface: true);
    }

    private static void RenderSyntheticPreviewVariant(
        string outputPath,
        IReadOnlyList<ModelUsageDistributionItem> items,
        bool darkSurface)
    {
        const int width = 1400;
        var height = GetPreferredHeight(width, items.Count, 1F);
        using var control = new ModelUsageDistributionControl
        {
            Size = new Size(width, height),
            Font = SystemFonts.MessageBoxFont,
            RangeLabel = "本月",
            Items = items,
            SurfaceColor = darkSurface
                ? Color.FromArgb(18, 36, 59)
                : Color.White,
            BorderColor = darkSurface
                ? Color.FromArgb(42, 70, 108)
                : Color.FromArgb(226, 232, 240),
            TextColor = darkSurface
                ? Color.FromArgb(245, 247, 251)
                : Color.FromArgb(16, 24, 40),
            MutedColor = darkSurface
                ? Color.FromArgb(166, 182, 204)
                : Color.FromArgb(100, 116, 139),
            PrimaryColor = darkSurface
                ? Color.FromArgb(47, 107, 255)
                : Color.FromArgb(88, 105, 246),
            SecondaryColor = darkSurface
                ? Color.FromArgb(96, 165, 250)
                : Color.FromArgb(139, 92, 246),
            TertiaryColor = darkSurface
                ? Color.FromArgb(167, 139, 250)
                : Color.FromArgb(34, 184, 207),
            AccentColor = darkSurface
                ? Color.FromArgb(34, 211, 238)
                : Color.FromArgb(77, 141, 255)
        };
        using var bitmap = new Bitmap(width, height);
        bitmap.SetResolution(96F, 96F);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            control._animationPhase = 0.37F;
            RenderValidationFrame(control, graphics, width, height, 1F);
        }
        bitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void RenderValidationFrame(
        ModelUsageDistributionControl control,
        Graphics graphics,
        int width,
        int height,
        float dpi)
    {
        control.Size = new Size(width, height);
        graphics.ResetTransform();
        graphics.ResetClip();
        control.RenderPaintFrame(
            graphics,
            new Rectangle(0, 0, width, height),
            dpi);
    }

    private static void ValidateArcPresentation()
    {
        var colors = new[]
        {
            SolRingColor,
            TerraRingColor,
            LunaRingColor,
            Gpt55RingColor,
            OtherRingColor
        };
        if (colors.Distinct().Count() != colors.Length || colors.Any(color => GetSaturation(color) < 0.52F))
        {
            throw new InvalidOperationException("Model distribution arcs must retain a distinct vivid palette.");
        }

        if (Math.Abs(RingSweepAngle - 360F) > 0.0001F)
        {
            throw new InvalidOperationException("Model distribution tracks must remain complete 360-degree rings.");
        }
        if (CometTailSegmentCount > 7 || MaximumPlanetDustCount > 4)
        {
            throw new InvalidOperationException(
                "Model ring detail layers must remain inside the fixed per-ring rendering budget.");
        }
        if (OrbitBreathAmplitude <= 0F || OrbitBreathAmplitude > 0.05F ||
            FixedPlanetRadiusLogicalPixels < 48F ||
            FixedPlanetRadiusLogicalPixels > 72F)
        {
            throw new InvalidOperationException(
                "Model orbit breathing must remain subtle and the central planet must retain a fixed DPI-scaled size.");
        }
        if (GetSaturation(NeutralOrbitColor) > 0.38F ||
            colors.Any(color => color.ToArgb() == NeutralOrbitColor.ToArgb()))
        {
            throw new InvalidOperationException(
                "Unused model tracks must retain a shared cool blue-gray base.");
        }
        if (PlanetOrbitStrokeRatio <= 0F ||
            PlanetUsageStrokeRatio <= PlanetOrbitStrokeRatio ||
            PlanetOrbitMinimumDpiWidth <= 0F ||
            PlanetOrbitMaximumDpiWidth <= PlanetOrbitMinimumDpiWidth ||
            PlanetUsageMinimumDpiWidth <= PlanetOrbitMaximumDpiWidth ||
            PlanetUsageMaximumDpiWidth <= PlanetUsageMinimumDpiWidth)
        {
            throw new InvalidOperationException(
                "Planet guide orbits must remain lighter and thinner than complete model energy rings.");
        }

        foreach (var baseColor in colors)
        {
            var seamStart = SampleRingTone(baseColor, 0F);
            var seamEnd = SampleRingTone(baseColor, 1F);
            var coolTone = SampleRingTone(baseColor, 0.25F);
            var primaryTone = SampleRingTone(baseColor, 0.5F);
            var warmTone = SampleRingTone(baseColor, 0.75F);
            var maximumToneDistance = new[]
            {
                ColorDistance(coolTone, primaryTone),
                ColorDistance(primaryTone, warmTone),
                ColorDistance(coolTone, warmTone)
            }.Max();
            var maximumHueDistance = new[]
            {
                GetHueDistance(baseColor, coolTone),
                GetHueDistance(baseColor, primaryTone),
                GetHueDistance(baseColor, warmTone)
            }.Max();
            if (seamStart.ToArgb() != seamEnd.ToArgb() ||
                maximumToneDistance < 28D ||
                maximumHueDistance > 5F)
            {
                throw new InvalidOperationException(
                    "Model ring gradients must be tonal, same-family and seamless at 0/360 degrees.");
            }
        }

        if (Math.Abs(CalculateVisualRingSweep(1) - RingSweepAngle) > 0.0001F ||
            Math.Abs(CalculateVisualRingSweep(long.MaxValue) - RingSweepAngle) > 0.0001F ||
            CalculateVisualRingSweep(0) != 0F ||
            CalculateVisualRingSweep(-1) != 0F)
        {
            throw new InvalidOperationException(
                "Every positive model must render one complete 360-degree star track; " +
                "Token share belongs in the centre summary and detail rows.");
        }

        var wrappedSegments = CalculateWrappedArcSegments(358F, 12F);
        if (wrappedSegments.Length != 2 ||
            Math.Abs(wrappedSegments.Sum(segment => segment.Sweep) - 12F) > 0.0001F)
        {
            throw new InvalidOperationException(
                "Model distribution highlights must cross the 0/360-degree seam continuously.");
        }

        var irregularFrames = new[] { 0D, 0.033D, 0.117D, 0.364D, 1.919D, 4.199D, 4.233D };
        var previousUnwrapped = 0D;
        for (var index = 0; index < irregularFrames.Length; index++)
        {
            var phase = CalculateAnimationPhase(irregularFrames[index]);
            var expected = irregularFrames[index] / CometOrbitPeriodSeconds;
            var unwrapped = Math.Floor(expected) + phase;
            if (Math.Abs(unwrapped - expected) > 0.0001D ||
                (index > 0 && unwrapped <= previousUnwrapped))
            {
                throw new InvalidOperationException(
                    "Model distribution animation must follow elapsed time across delayed frames and seams.");
            }
            previousUnwrapped = unwrapped;
        }

        // Normalize in double precision before converting to float.  Casting the total
        // rotation count first loses its fractional part after a long uptime and makes the
        // comet appear to stop, then jump several degrees at once.
        var longUptimeSeconds = TimeSpan.FromDays(30).TotalSeconds;
        var longUptimePhase = CalculateAnimationPhase(longUptimeSeconds);
        var nextFramePhase = CalculateAnimationPhase(longUptimeSeconds + 0.033D);
        var longUptimeAdvance = NormalizeProgress(nextFramePhase - longUptimePhase);
        if (longUptimeAdvance < 0.003F || longUptimeAdvance > 0.005F)
        {
            throw new InvalidOperationException(
                "Model distribution animation must retain sub-frame precision after long uptime.");
        }
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (!ShouldAnimate())
        {
            _animationTimer.Stop();
            PauseAnimationClock();
            return;
        }

        ConfigureAnimationCadence();
        _animationPhase = SampleAnimationPhase();
        InvalidateAnimationRegion();
    }

    private void BindAnimationHost()
    {
        var host = FindForm();
        if (ReferenceEquals(host, _animationHostForm))
        {
            return;
        }

        UnbindAnimationHost();
        _animationHostForm = host;
        if (_animationHostForm == null)
        {
            return;
        }

        _animationHostForm.Activated += AnimationHost_ActivityChanged;
        _animationHostForm.Deactivate += AnimationHost_ActivityChanged;
        _animationHostForm.Resize += AnimationHost_ActivityChanged;
        _animationHostForm.VisibleChanged += AnimationHost_ActivityChanged;
        _animationHostForm.FormClosed += AnimationHost_ActivityChanged;
    }

    private void UnbindAnimationHost()
    {
        if (_animationHostForm == null)
        {
            return;
        }

        _animationHostForm.Activated -= AnimationHost_ActivityChanged;
        _animationHostForm.Deactivate -= AnimationHost_ActivityChanged;
        _animationHostForm.Resize -= AnimationHost_ActivityChanged;
        _animationHostForm.VisibleChanged -= AnimationHost_ActivityChanged;
        _animationHostForm.FormClosed -= AnimationHost_ActivityChanged;
        _animationHostForm = null;
    }

    private void AnimationHost_ActivityChanged(object? sender, EventArgs e) => UpdateAnimationState();

    private bool ShouldAnimate()
    {
        if (IsDisposed || Disposing || !IsHandleCreated || !Visible ||
            !_items.Any(item => item.TotalTokens > 0) ||
            ControlViewport.HasActiveScrollAncestor(this) ||
            !ControlViewport.IsInsideScrollableViewport(this))
        {
            return false;
        }

        var host = _animationHostForm ?? FindForm();
        return host != null &&
               host.Visible &&
               host.WindowState != FormWindowState.Minimized;
    }

    private void UpdateAnimationState()
    {
        if (ShouldAnimate())
        {
            ResumeAnimationClock();
            _animationPhase = SampleAnimationPhase();
            ConfigureAnimationCadence();
            if (!_animationTimer.Enabled)
            {
                _animationTimer.Start();
            }
            return;
        }

        _animationTimer.Stop();
        PauseAnimationClock();
    }

    private void ConfigureAnimationCadence()
    {
        var host = _animationHostForm ?? FindForm();
        var isActive = host != null &&
            (ReferenceEquals(Form.ActiveForm, host) || host.ContainsFocus);
        var desiredInterval = isActive
            ? ActiveAnimationIntervalMilliseconds
            : InactiveAnimationIntervalMilliseconds;
        if (_animationTimer.Interval != desiredInterval)
        {
            _animationTimer.Interval = desiredInterval;
        }
    }

    private void ResumeAnimationClock()
    {
        if (!_animationClock.IsRunning)
        {
            _animationClock.Restart();
        }
    }

    private void PauseAnimationClock()
    {
        if (!_animationClock.IsRunning)
        {
            return;
        }

        _animationClock.Stop();
        _animationElapsed += _animationClock.Elapsed;
        _animationClock.Reset();
        _animationPhase = CalculateAnimationPhase(_animationElapsed.TotalSeconds);
    }

    private float SampleAnimationPhase()
    {
        var elapsedSeconds = _animationElapsed.TotalSeconds;
        if (_animationClock.IsRunning)
        {
            elapsedSeconds += _animationClock.Elapsed.TotalSeconds;
        }
        return CalculateAnimationPhase(elapsedSeconds);
    }

    private static float CalculateAnimationPhase(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0D)
        {
            return 0F;
        }

        var rotations = elapsedSeconds / CometOrbitPeriodSeconds;
        var normalized = rotations - Math.Floor(rotations);
        return (float)normalized;
    }

    private void InvalidateAnimationRegion()
    {
        var hasOrbitRegion = IsDrawableBounds(_lastAnimationOuterBounds) &&
            IsDrawableBounds(_lastAnimationInnerBounds);
        var hasMeteorRegion = IsDrawableBounds(_lastMeteorAnimationBounds);
        if (!hasOrbitRegion && !hasMeteorRegion)
        {
            return;
        }

        using var region = CreateAnimationRegion(
            _lastAnimationOuterBounds,
            _lastAnimationInnerBounds,
            _lastMeteorAnimationBounds,
            ClientRectangle);
        Invalidate(region, false);
    }

    private static Region CreateAnimationRegion(
        RectangleF outerBounds,
        RectangleF innerBounds,
        RectangleF meteorBounds,
        Rectangle clientBounds)
    {
        var region = new Region();
        region.MakeEmpty();
        if (IsDrawableBounds(outerBounds))
        {
            // Rotated perspective ellipses can reach the corners of their rectangular
            // maximum envelope; using an outer ellipse here would leave breathing trails.
            region.Union(outerBounds);
        }
        if (IsDrawableBounds(meteorBounds))
        {
            region.Union(meteorBounds);
        }
        if (IsDrawableBounds(innerBounds))
        {
            // Exclude the protected planet after every animated area has been combined.
            // Excluding it before Union(meteor) allowed the meteor strip to add the upper
            // hemisphere back and erase it with the light stage background on partial frames.
            using var innerPath = new GraphicsPath();
            innerPath.AddEllipse(innerBounds);
            region.Exclude(innerPath);
        }
        region.Intersect(clientBounds);
        return region;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var next = HitTest(e.Location);
        if (_hoveredIndex == next)
        {
            return;
        }

        _hoveredIndex = next;
        Cursor = next != NoHoverIndex ? Cursors.Hand : Cursors.Default;
        InvalidateStaticDonutCache();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredIndex == NoHoverIndex)
        {
            return;
        }

        _hoveredIndex = NoHoverIndex;
        Cursor = Cursors.Default;
        InvalidateStaticDonutCache();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // A dynamically-created card may have performed its first visibility check before
        // the parent FlowLayoutPanel finalized its scroll position. If Windows is painting
        // this control now, its viewport geometry is settled and the animation must be awake.
        // This self-heals page-switch timing without keeping an off-screen polling timer alive.
        if (!_animationTimer.Enabled && ShouldAnimate())
        {
            UpdateAnimationState();
        }
        RenderPaintFrame(
            e.Graphics,
            e.ClipRectangle,
            _validationDpiOverride ?? Math.Max(1F, DeviceDpi / 96F));
    }

    private void RenderPaintFrame(Graphics graphics, Rectangle clipRectangle, float dpi)
    {
        dpi = IsFinitePositive(dpi) ? Math.Max(1F, dpi) : 1F;
        using (var background = new SolidBrush(_surfaceColor))
        {
            graphics.FillRectangle(background, clipRectangle);
        }
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var client = RectangleF.Inflate(ClientRectangle, -Math.Max(1F, dpi), -Math.Max(1F, dpi));
        if (client.Width < Scale(250, dpi) || client.Height < Scale(230, dpi))
        {
            return;
        }

        var layout = CalculateLayout(client, dpi, _items.Length);
        DrawBackground(graphics, layout.Outer, dpi);
        // Animation invalidates only the annulus around the model planets.  Respect the
        // native clip region so those 30-FPS frames do not recreate all header/table
        // Fonts, Brushes and Pens even though Windows will discard their pixels.
        if (graphics.IsVisible(layout.Title) || graphics.IsVisible(layout.Subtitle))
        {
            DrawHeader(graphics, layout, dpi);
        }
        if (graphics.IsVisible(layout.Donut))
        {
            DrawDonut(graphics, layout.Donut, dpi);
        }
        if (graphics.IsVisible(layout.Table))
        {
            DrawTable(graphics, layout, dpi);
        }
    }

    private void DrawBackground(Graphics graphics, RectangleF bounds, float dpi)
    {
        if (!IsDrawableBounds(bounds) || !IsFinitePositive(dpi))
        {
            return;
        }
        using var path = CreateRoundedRectangle(bounds, Scale(16, dpi));
        using var fill = new LinearGradientBrush(
            bounds,
            _surfaceColor,
            Blend(_surfaceColor, _primaryColor, 0.035F),
            LinearGradientMode.ForwardDiagonal);
        using var border = new Pen(_borderColor, Math.Max(1F, dpi));
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);
    }

    private void DrawHeader(Graphics graphics, LayoutMetrics layout, float dpi)
    {
        using var titleFont = new Font(Font.FontFamily, 11.2F, FontStyle.Bold);
        using var subtitleFont = new Font(Font.FontFamily, 8.2F, FontStyle.Regular);
        using var titleBrush = new SolidBrush(_textColor);
        using var mutedBrush = new SolidBrush(_mutedColor);
        using var accentBrush = new SolidBrush(_primaryColor);
        using var leftFormat = CenterLeftFormat();
        var accent = new RectangleF(
            layout.Title.Left,
            layout.Title.Top + Scale(6, dpi),
            Scale(4, dpi),
            Math.Max(Scale(16, dpi), layout.Title.Height - Scale(12, dpi)));
        using var accentPath = CreateRoundedRectangle(accent, Scale(2, dpi));
        graphics.FillPath(accentBrush, accentPath);
        graphics.DrawString(
            $"模型星系 · {_rangeLabel}",
            titleFont,
            titleBrush,
            new RectangleF(
                layout.Title.Left + Scale(14, dpi),
                layout.Title.Top,
                layout.Title.Width - Scale(14, dpi),
                layout.Title.Height),
            leftFormat);
        graphics.DrawString(
            "每个模型一条完整星轨 · 占比与 API 等值见右侧",
            subtitleFont,
            mutedBrush,
            layout.Subtitle,
            leftFormat);
    }

    private void DrawDonut(Graphics graphics, RectangleF bounds, float dpi)
    {
        if (!IsDrawableBounds(bounds) || !IsFinitePositive(dpi))
        {
            _lastAnimationOuterBounds = RectangleF.Empty;
            _lastAnimationInnerBounds = RectangleF.Empty;
            _lastMeteorAnimationBounds = RectangleF.Empty;
            _lastRingTargets = [];
            return;
        }
        if (!_isRenderingStaticDonutCache && TryDrawCachedDonut(graphics, bounds, dpi))
        {
            return;
        }
        var visualRings = BuildVisualRings();
        var totalTokens = _items.Sum(item => Math.Max(0L, item.TotalTokens));
        var totalCost = _items.Sum(item => Math.Max(0D, item.EquivalentCostUsd));
        var geometry = CalculateRingGeometry(bounds, dpi, visualRings.Length);
        var targets = new List<RingHitTarget>(visualRings.Length);
        var hasUsage = visualRings.Length > 0 && totalTokens > 0;
        var darkSurface = IsDarkVisualSurface();
        var backdropSystemBounds = hasUsage
            ? geometry.OuterBounds
            : BoundsFromRadius(
                geometry.Center,
                Math.Min(
                    Math.Min(bounds.Width, bounds.Height) * 0.33F,
                    geometry.CenterRadius * 2.42F));
        _lastMeteorAnimationBounds = RectangleF.Empty;
        DrawOrbitalBackdrop(
            graphics,
            bounds,
            backdropSystemBounds,
            dpi,
            hasUsage,
            darkSurface);
        if (visualRings.Length > 0 && totalTokens > 0)
        {
            // Geometry.OuterBounds already reserves the largest breathing phase.  The final
            // inflation covers the comet bloom and anti-aliased edge at fractional DPI.
            _lastAnimationOuterBounds = RectangleF.Inflate(
                geometry.OuterBounds,
                Scale(9, dpi),
                Scale(9, dpi));
            _lastAnimationInnerBounds = BoundsFromRadius(
                geometry.Center,
                geometry.CenterRadius + Scale(10, dpi));
        }
        else
        {
            _lastAnimationOuterBounds = RectangleF.Empty;
            _lastAnimationInnerBounds = RectangleF.Empty;
        }

        // Decorative trails deliberately have no data meaning.  A single model otherwise
        // produces only one visible identity orbit, which makes a 100% distribution feel
        // sparse.  These distant paths give the scene scale while all hit testing and the
        // brighter foreground rings remain tied exclusively to real models.
        DrawAmbientOrbitTrails(
            graphics,
            bounds,
            geometry.Center,
            dpi,
            visualRings.Length,
            darkSurface);

        if (visualRings.Length == 0 || totalTokens <= 0)
        {
            DrawEmptyGalaxyPlaceholder(
                graphics,
                geometry.Center,
                geometry.CenterRadius,
                dpi,
                darkSurface);
        }
        else
        {
            for (var index = 0; index < visualRings.Length; index++)
            {
                var visualRing = visualRings[index];
                // The orbital rail itself is fixed. Motion comes from the light packets,
                // which reads cleaner than every ring physically pulsing and lets the static
                // scene cache restore a frame without introducing a jittery edge.
                var radius = geometry.Radii[index];
                var arcBounds = CalculatePerspectiveOrbitBounds(
                    geometry.Center,
                    radius,
                    index,
                    visualRings.Length);
                var orbitRotation = GetPerspectiveOrbitRotation(index, visualRings.Length);
                var color = GetVisualRingColor(visualRing);
                var hovered = IsRingHovered(visualRing);
                var startAngle = RingStartAngle + (index * 47F);
                var laneStroke = geometry.StrokeWidth;
                var orbitStroke = Math.Clamp(
                    laneStroke * PlanetOrbitStrokeRatio,
                    PlanetOrbitMinimumDpiWidth * dpi,
                    PlanetOrbitMaximumDpiWidth * dpi);
                var usageStroke = CalculatePlanetUsageStroke(
                    laneStroke,
                    dpi,
                    visualRings.Length);
                var visualSweep = CalculateVisualRingSweep(visualRing.TotalTokens);
                // Every positive model owns one complete star track.  The ring is an identity
                // marker, not a progress meter; the exact Token share remains in the centre
                // summary and the detail row so small models never disappear visually.
                var orbitState = graphics.Save();
                try
                {
                    RotateAround(graphics, geometry.Center, orbitRotation);
                    DrawPlanetOrbit(
                        graphics,
                        arcBounds,
                        orbitStroke,
                        color,
                        hovered,
                        dpi);

                    if (visualSweep > 0F)
                    {
                        var glowAlpha = darkSurface
                            ? hovered ? 82 : 52
                            : hovered ? 50 : 30;
                        using (var glow = new Pen(
                                   Color.FromArgb(glowAlpha, color),
                                   usageStroke + ((darkSurface ? 4.6F : 3.6F) * dpi)))
                        {
                            // The broad layer is deliberately faint; it provides atmospheric
                            // depth without turning the complete identity orbit into a plastic
                            // neon tube.
                            graphics.DrawEllipse(glow, arcBounds);
                        }

                        DrawTonalArc(
                            graphics,
                            arcBounds,
                            startAngle,
                            visualSweep,
                            usageStroke,
                            index,
                            color,
                            false);
                        DrawUsageEnergyEdges(
                            graphics,
                            arcBounds,
                            startAngle,
                            visualSweep,
                            usageStroke,
                            color,
                            false,
                            dpi,
                            darkSurface);
                        DrawOrbitalDepthAccents(
                            graphics,
                            arcBounds,
                            startAngle,
                            usageStroke,
                            color,
                            hovered,
                            dpi);
                        DrawPlanetDust(
                            graphics,
                            arcBounds,
                            startAngle,
                            visualSweep,
                            usageStroke,
                            index,
                            color,
                            dpi,
                            darkSurface,
                            ShouldDrawOrbitSatellite(index, visualRings.Length));
                    }

                    if (!_isRenderingStaticDonutCache)
                    {
                        DrawOrbitingHighlights(
                            graphics,
                            arcBounds,
                            startAngle,
                            index,
                            color,
                            usageStroke,
                            hovered,
                            darkSurface,
                            dpi);
                    }
                }
                finally
                {
                    graphics.Restore(orbitState);
                }

                targets.Add(new RingHitTarget(
                    visualRing.HoverIndex,
                    geometry.Center,
                    arcBounds,
                    orbitRotation,
                    geometry.StrokeWidth));
            }
        }
        _lastRingTargets = targets.ToArray();

        var centerDiameter = Math.Max(Scale(64, dpi), geometry.CenterRadius * 2F - Scale(4, dpi));
        var center = new RectangleF(
            geometry.Center.X - (centerDiameter / 2F),
            geometry.Center.Y - (centerDiameter / 2F),
            centerDiameter,
            centerDiameter);
        center = RectangleF.Inflate(center, -Scale(1, dpi), -Scale(1, dpi));
        var hoveredSummary = GetHoveredSummary(visualRings);
        var defaultSummary = _items.FirstOrDefault(item => item.TotalTokens > 0L);
        var displayedSummary = hoveredSummary ?? defaultSummary;
        var displayedColor = hoveredSummary != null
            ? GetHoveredColor(visualRings)
            : defaultSummary != null
                ? GetTableItemColor(Array.IndexOf(_items, defaultSummary), defaultSummary, visualRings)
                : _primaryColor;
        var planetSphere = RectangleF.Inflate(center, -Scale(2, dpi), -Scale(2, dpi));
        DrawCentralPlanetBelt(
            graphics,
            planetSphere,
            displayedColor,
            dpi,
            darkSurface,
            foreground: false);
        if (!IsCircularVisualVisible(
                graphics,
                geometry.Center,
                Math.Max(1F, planetSphere.Width / 2F + Scale(9, dpi))))
        {
            // The animation clip excludes the static glass planet, but the wider belt tips
            // still intersect the orbit annulus.  Repaint only their inexpensive front/back
            // arcs and skip the sphere gradient, halo and text allocations on every frame.
            DrawCentralPlanetBelt(
                graphics,
                planetSphere,
                displayedColor,
                dpi,
                darkSurface,
                foreground: true);
            return;
        }
        DrawCentralGlassPlanet(graphics, center, displayedColor, dpi, darkSurface);
        DrawCentralPlanetBelt(
            graphics,
            planetSphere,
            displayedColor,
            dpi,
            darkSurface,
            foreground: true);
        using var valueBrush = new SolidBrush(
            darkSurface ? Color.FromArgb(248, 250, 255) : Color.FromArgb(22, 69, 132));
        using var mutedBrush = new SolidBrush(
            darkSurface ? Color.FromArgb(208, 220, 244) : Color.FromArgb(60, 105, 162));
        using var accentBrush = new SolidBrush(
            darkSurface ? Color.FromArgb(232, 242, 255) : Color.FromArgb(29, 82, 151));
        if (displayedSummary != null && totalTokens > 0)
        {
            DrawFittedCenteredText(
                graphics,
                displayedSummary.Model,
                new RectangleF(center.Left, center.Top + center.Height * 0.15F, center.Width, center.Height * 0.20F),
                accentBrush,
                7.8F,
                5.1F,
                FontStyle.Bold);
            DrawFittedCenteredText(
                graphics,
                FormatUsagePercent(displayedSummary.TotalTokens, totalTokens),
                new RectangleF(center.Left, center.Top + center.Height * 0.35F, center.Width, center.Height * 0.28F),
                valueBrush,
                14.5F,
                8.0F,
                FontStyle.Bold);
            DrawFittedCenteredText(
                graphics,
                $"{FormatTokens(displayedSummary.TotalTokens)} · {FormatUsd(displayedSummary.EquivalentCostUsd)}",
                new RectangleF(center.Left, center.Top + center.Height * 0.65F, center.Width, center.Height * 0.17F),
                mutedBrush,
                6.8F,
                4.8F,
                FontStyle.Regular);
        }
        else if (totalTokens > 0)
        {
            DrawFittedCenteredText(
                graphics,
                FormatTokens(totalTokens),
                new RectangleF(center.Left, center.Top + center.Height * 0.22F, center.Width, center.Height * 0.28F),
                valueBrush,
                15.2F,
                8.4F,
                FontStyle.Bold);
            DrawFittedCenteredText(
                graphics,
                $"{_rangeLabel} Token",
                new RectangleF(center.Left, center.Top + center.Height * 0.50F, center.Width, center.Height * 0.18F),
                mutedBrush,
                7.8F,
                5.5F,
                FontStyle.Bold);
            DrawFittedCenteredText(
                graphics,
                "API " + FormatUsd(totalCost),
                new RectangleF(center.Left, center.Top + center.Height * 0.69F, center.Width, center.Height * 0.17F),
                accentBrush,
                7.4F,
                5.2F,
                FontStyle.Regular);
        }
        else
        {
            DrawFittedCenteredText(
                graphics,
                "暂无记录",
                center,
                mutedBrush,
                8.2F,
                5.5F,
                FontStyle.Bold);
        }
    }

    private bool TryDrawCachedDonut(Graphics graphics, RectangleF bounds, float dpi)
    {
        if (!EnsureStaticDonutCache(bounds, dpi) || _staticDonutCache == null)
        {
            return false;
        }

        // The native Graphics clip from the timer limits this blit to the changing stage
        // area. The expensive stars, gradients, rings and planet were rendered once into
        // the cache, so a frame only composites that background plus a few moving lights.
        graphics.DrawImageUnscaled(_staticDonutCache, Point.Empty);
        DrawDynamicDonutOverlay(graphics, bounds, dpi);
        return true;
    }

    private bool EnsureStaticDonutCache(RectangleF bounds, float dpi)
    {
        var cacheSize = ClientSize;
        if (cacheSize.Width <= 0 || cacheSize.Height <= 0)
        {
            return false;
        }

        if (_staticDonutCache != null &&
            _staticDonutCacheSize == cacheSize &&
            _staticDonutCacheBounds == bounds &&
            Math.Abs(_staticDonutCacheDpi - dpi) < 0.0001F &&
            _staticDonutCacheVersion == _donutVisualVersion)
        {
            return true;
        }

        DisposeStaticDonutCache();
        Bitmap? cache = null;
        try
        {
            cache = new Bitmap(cacheSize.Width, cacheSize.Height);
            using (var cacheGraphics = Graphics.FromImage(cache))
            {
                cacheGraphics.Clear(Color.Transparent);
                cacheGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                cacheGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                cacheGraphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                _isRenderingStaticDonutCache = true;
                DrawDonut(cacheGraphics, bounds, dpi);
            }

            _staticDonutCache = cache;
            cache = null;
            _staticDonutCacheSize = cacheSize;
            _staticDonutCacheBounds = bounds;
            _staticDonutCacheDpi = dpi;
            _staticDonutCacheVersion = _donutVisualVersion;
            return true;
        }
        finally
        {
            _isRenderingStaticDonutCache = false;
            cache?.Dispose();
        }
    }

    private void DrawDynamicDonutOverlay(Graphics graphics, RectangleF bounds, float dpi)
    {
        var visualRings = BuildVisualRings();
        var totalTokens = _items.Sum(item => Math.Max(0L, item.TotalTokens));
        var hasUsage = visualRings.Length > 0 && totalTokens > 0;
        _lastMeteorAnimationBounds = RectangleF.Empty;
        if (!hasUsage)
        {
            return;
        }

        var geometry = CalculateRingGeometry(bounds, dpi, visualRings.Length);
        var darkSurface = IsDarkVisualSurface();
        DrawAnimatedBackdropMeteors(graphics, bounds, dpi, darkSurface);

        var dynamicState = graphics.Save();
        try
        {
            // In the uncached scene the central glass planet is painted after the orbital
            // lights. Exclude it here so cached frames preserve the same depth ordering.
            using var protectedPlanet = new GraphicsPath();
            protectedPlanet.AddEllipse(BoundsFromRadius(
                geometry.Center,
                geometry.CenterRadius + Scale(9, dpi)));
            graphics.SetClip(protectedPlanet, CombineMode.Exclude);

            for (var index = 0; index < visualRings.Length; index++)
            {
                var visualRing = visualRings[index];
                var arcBounds = CalculatePerspectiveOrbitBounds(
                    geometry.Center,
                    geometry.Radii[index],
                    index,
                    visualRings.Length);
                var color = GetVisualRingColor(visualRing);
                var laneStroke = geometry.StrokeWidth;
                var usageStroke = CalculatePlanetUsageStroke(
                    laneStroke,
                    dpi,
                    visualRings.Length);
                var orbitState = graphics.Save();
                try
                {
                    RotateAround(
                        graphics,
                        geometry.Center,
                        GetPerspectiveOrbitRotation(index, visualRings.Length));
                    DrawOrbitingHighlights(
                        graphics,
                        arcBounds,
                        RingStartAngle + (index * 47F),
                        index,
                        color,
                        usageStroke,
                        IsRingHovered(visualRing),
                        darkSurface,
                        dpi);
                }
                finally
                {
                    graphics.Restore(orbitState);
                }
            }
        }
        finally
        {
            graphics.Restore(dynamicState);
        }
    }

    private void DrawOrbitingHighlights(
        Graphics graphics,
        RectangleF arcBounds,
        float startAngle,
        int ringIndex,
        Color color,
        float usageStroke,
        bool hovered,
        bool darkSurface,
        float dpi)
    {
        var trackNodeProgress = NormalizeProgress(_animationPhase + (ringIndex * 0.31F));
        var trackNodeColor = Blend(
            SampleRingTone(color, trackNodeProgress + (ringIndex * 0.15F)),
            Color.White,
            0.34F);
        DrawOrbitingHighlight(
            graphics,
            arcBounds,
            startAngle,
            trackNodeProgress,
            12F,
            trackNodeColor,
            Math.Max(1.85F * dpi, usageStroke * 0.54F),
            Math.Max(0.82F * dpi, usageStroke * 0.16F),
            darkSurface ? hovered ? 156 : 132 : hovered ? 122 : 92,
            darkSurface ? hovered ? 255 : 246 : hovered ? 242 : 224);

        if (!darkSurface)
        {
            // A second, dimmer packet keeps the white glass rings lively without turning
            // the scene into a dense neon display.
            var echoProgress = NormalizeProgress(
                (_animationPhase * 0.72F) + (ringIndex * 0.23F) + 0.47F);
            DrawOrbitingHighlight(
                graphics,
                arcBounds,
                startAngle,
                echoProgress,
                8F,
                Blend(SampleRingTone(color, echoProgress), Color.White, 0.48F),
                Math.Max(1.30F * dpi, usageStroke * 0.34F),
                Math.Max(0.62F * dpi, usageStroke * 0.10F),
                hovered ? 74 : 54,
                hovered ? 214 : 196);
        }
    }

    private void DrawAnimatedBackdropMeteors(
        Graphics graphics,
        RectangleF stageBounds,
        float dpi,
        bool darkSurface)
    {
        var stage = RectangleF.Inflate(stageBounds, -Scale(2, dpi), -Scale(2, dpi));
        if (!IsDrawableBounds(stage))
        {
            return;
        }

        _lastMeteorAnimationBounds = stage;
        using var stagePath = CreateRoundedRectangle(stage, Scale(18, dpi));
        var state = graphics.Save();
        try
        {
            graphics.SetClip(stagePath, CombineMode.Intersect);
            DrawDistantMeteors(graphics, stage, dpi, _animationPhase, darkSurface);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private void InvalidateStaticDonutCache()
    {
        unchecked
        {
            _donutVisualVersion++;
        }
        DisposeStaticDonutCache();
    }

    private void DisposeStaticDonutCache()
    {
        _staticDonutCache?.Dispose();
        _staticDonutCache = null;
        _staticDonutCacheSize = Size.Empty;
        _staticDonutCacheBounds = RectangleF.Empty;
        _staticDonutCacheDpi = 0F;
        _staticDonutCacheVersion = -1;
    }

    private void DrawTable(Graphics graphics, LayoutMetrics layout, float dpi)
    {
        var table = layout.Table;
        if (layout.IsWide)
        {
            DrawWideModelCards(graphics, table, layout.RowHeight, dpi);
            return;
        }

        var header = new RectangleF(table.Left, table.Top, table.Width, layout.TableHeaderHeight);
        using var headerFill = new SolidBrush(Blend(_surfaceColor, _primaryColor, 0.055F));
        using var headerPath = CreateRoundedRectangle(header, Scale(9, dpi));
        graphics.FillPath(headerFill, headerPath);

        using var headerFont = new Font(Font.FontFamily, 8.2F, FontStyle.Bold);
        using var bodyFont = new Font(Font.FontFamily, 8.8F, FontStyle.Regular);
        using var bodyBoldFont = new Font(Font.FontFamily, 8.8F, FontStyle.Bold);
        using var headerBrush = new SolidBrush(_mutedColor);
        using var textBrush = new SolidBrush(_textColor);
        using var mutedBrush = new SolidBrush(_mutedColor);
        using var divider = new Pen(Blend(_borderColor, _surfaceColor, 0.18F), Math.Max(1F, dpi));
        using var leftFormat = CenterLeftFormat();
        using var rightFormat = CenterRightFormat();
        using var centerFormat = CenterFormat();

        if (layout.TwoLineRows)
        {
            var leftHeader = RectangleF.Inflate(header, -Scale(12, dpi), 0F);
            graphics.DrawString("模型与占比 / 本地记录", headerFont, headerBrush, leftHeader, leftFormat);
            graphics.DrawString("Token / API 等值", headerFont, headerBrush, leftHeader, rightFormat);
        }
        else
        {
            var columns = GetColumns(table, dpi);
            DrawCellText(graphics, "模型 / 占比", headerFont, headerBrush, columns.Model with { Y = header.Y, Height = header.Height }, false);
            DrawCellText(graphics, "记录", headerFont, headerBrush, columns.Records with { Y = header.Y, Height = header.Height }, true);
            DrawCellText(graphics, "Token", headerFont, headerBrush, columns.Tokens with { Y = header.Y, Height = header.Height }, true);
            DrawCellText(graphics, "API 等值", headerFont, headerBrush, columns.Cost with { Y = header.Y, Height = header.Height }, true);
        }

        _lastRowBounds = new RectangleF[_items.Length];
        if (_items.Length == 0)
        {
            var empty = new RectangleF(
                table.Left,
                header.Bottom,
                table.Width,
                Math.Max(Scale(74, dpi), table.Height - header.Height));
            graphics.DrawString(
                $"{_rangeLabel}还没有可归类的模型用量",
                bodyFont,
                mutedBrush,
                empty,
                centerFormat);
            return;
        }

        var rowTop = header.Bottom;
        var visualRings = BuildVisualRings();
        var totalTokens = _items.Sum(item => Math.Max(0L, item.TotalTokens));
        var hoveredOtherIndexes = _hoveredIndex == OtherGroupHoverIndex
            ? visualRings
                .FirstOrDefault(ring => ring.HoverIndex == OtherGroupHoverIndex)?
                .ItemIndexes ?? []
            : [];
        for (var index = 0; index < _items.Length; index++)
        {
            var row = new RectangleF(table.Left, rowTop, table.Width, layout.RowHeight);
            _lastRowBounds[index] = row;
            var item = _items[index];
            var modelLabel = FormatModelWithUsagePercent(item.Model, item.TotalTokens, totalTokens);
            var itemColor = GetTableItemColor(index, item, visualRings);
            var hovered = index == _hoveredIndex || hoveredOtherIndexes.Contains(index);
            if (hovered)
            {
                using var hoverFill = new SolidBrush(Color.FromArgb(24, itemColor));
                graphics.FillRectangle(hoverFill, row);
            }

            var dotSize = Scale(9, dpi);
            using var dotBrush = new SolidBrush(itemColor);
            graphics.FillEllipse(
                dotBrush,
                row.Left + Scale(10, dpi),
                row.Top + ((row.Height - dotSize) / 2F),
                dotSize,
                dotSize);

            if (layout.TwoLineRows)
            {
                var contentLeft = row.Left + Scale(27, dpi);
                var contentWidth = row.Width - Scale(39, dpi);
                var modelBounds = new RectangleF(contentLeft, row.Top + Scale(5, dpi), contentWidth * 0.67F, row.Height * 0.44F);
                var recordsBounds = new RectangleF(modelBounds.Right, modelBounds.Top, contentWidth - modelBounds.Width, modelBounds.Height);
                var tokenBounds = new RectangleF(contentLeft, row.Top + row.Height * 0.49F, contentWidth * 0.55F, row.Height * 0.40F);
                var costBounds = new RectangleF(tokenBounds.Right, tokenBounds.Top, contentWidth - tokenBounds.Width, tokenBounds.Height);
                DrawCellText(graphics, modelLabel, bodyBoldFont, textBrush, modelBounds, false);
                DrawCellText(graphics, item.Records + " 条", bodyFont, mutedBrush, recordsBounds, true);
                DrawCellText(graphics, FormatTokens(item.TotalTokens), bodyFont, mutedBrush, tokenBounds, false);
                DrawCellText(graphics, FormatUsd(item.EquivalentCostUsd), bodyBoldFont, textBrush, costBounds, true);
            }
            else
            {
                var columns = GetColumns(row, dpi);
                var modelBounds = columns.Model with { X = columns.Model.X + Scale(17, dpi), Width = columns.Model.Width - Scale(17, dpi) };
                DrawCellText(graphics, modelLabel, bodyBoldFont, textBrush, modelBounds, false);
                DrawCellText(graphics, item.Records.ToString("N0", CultureInfo.InvariantCulture), bodyFont, mutedBrush, columns.Records, true);
                DrawCellText(graphics, FormatTokens(item.TotalTokens), bodyFont, mutedBrush, columns.Tokens, true);
                DrawCellText(graphics, FormatUsd(item.EquivalentCostUsd), bodyBoldFont, textBrush, columns.Cost, true);
            }

            if (index < _items.Length - 1)
            {
                graphics.DrawLine(
                    divider,
                    row.Left + Scale(10, dpi),
                    row.Bottom,
                    row.Right - Scale(10, dpi),
                    row.Bottom);
            }
            rowTop += layout.RowHeight;
        }
    }

    private void DrawWideModelCards(
        Graphics graphics,
        RectangleF bounds,
        float rowHeight,
        float dpi)
    {
        if (!IsDrawableBounds(bounds))
        {
            _lastRowBounds = [];
            return;
        }

        using var panelPath = CreateRoundedRectangle(bounds, Scale(18, dpi));
        using var panelFill = new LinearGradientBrush(
            bounds,
            Blend(_surfaceColor, _primaryColor, 0.045F),
            Blend(_surfaceColor, _secondaryColor, 0.020F),
            LinearGradientMode.ForwardDiagonal);
        using var panelBorder = new Pen(Color.FromArgb(64, _borderColor), Math.Max(0.8F * dpi, 1F));
        graphics.FillPath(panelFill, panelPath);
        graphics.DrawPath(panelBorder, panelPath);

        var headerHeight = Scale(48, dpi);
        var header = new RectangleF(
            bounds.Left + Scale(16, dpi),
            bounds.Top + Scale(4, dpi),
            bounds.Width - Scale(32, dpi),
            headerHeight - Scale(4, dpi));
        using var titleFont = new Font(Font.FontFamily, 9.4F, FontStyle.Bold);
        using var headerFont = new Font(Font.FontFamily, 7.4F, FontStyle.Bold);
        using var titleBrush = new SolidBrush(_textColor);
        using var mutedBrush = new SolidBrush(_mutedColor);
        using var leftFormat = CenterLeftFormat();
        using var rightFormat = CenterRightFormat();
        graphics.DrawString("模型用量明细", titleFont, titleBrush, header, leftFormat);
        graphics.DrawString("占比 · Token · API 等值", headerFont, mutedBrush, header, rightFormat);

        _lastRowBounds = new RectangleF[_items.Length];
        if (_items.Length == 0)
        {
            var available = new RectangleF(
                bounds.Left + Scale(18, dpi),
                header.Bottom + Scale(12, dpi),
                Math.Max(1F, bounds.Width - Scale(36, dpi)),
                Math.Max(1F, bounds.Bottom - header.Bottom - Scale(24, dpi)));
            var emptyCardWidth = Math.Min(available.Width, Scale(330, dpi));
            var emptyCardHeight = Math.Min(available.Height, Scale(84, dpi));
            var emptyCard = new RectangleF(
                available.Left + (available.Width - emptyCardWidth) / 2F,
                available.Top + (available.Height - emptyCardHeight) / 2F,
                emptyCardWidth,
                emptyCardHeight);
            using (var emptyPath = CreateRoundedRectangle(emptyCard, Scale(14, dpi)))
            using (var emptyFill = new LinearGradientBrush(
                       emptyCard,
                       Blend(_surfaceColor, _primaryColor, 0.045F),
                       Blend(_surfaceColor, _secondaryColor, 0.018F),
                       LinearGradientMode.Horizontal))
            using (var emptyBorder = new Pen(
                       Color.FromArgb(64, Blend(_borderColor, _primaryColor, 0.18F)),
                       Math.Max(0.8F * dpi, 1F)))
            {
                graphics.FillPath(emptyFill, emptyPath);
                graphics.DrawPath(emptyBorder, emptyPath);
            }
            using var emptyFont = new Font(Font.FontFamily, 9F, FontStyle.Regular);
            using var emptyFormat = CenterFormat();
            graphics.DrawString(
                $"{_rangeLabel}还没有可归类的模型用量",
                emptyFont,
                mutedBrush,
                emptyCard,
                emptyFormat);
            return;
        }

        var totalTokens = _items.Sum(item => Math.Max(0L, item.TotalTokens));
        var visualRings = BuildVisualRings();
        var hoveredOtherIndexes = _hoveredIndex == OtherGroupHoverIndex
            ? visualRings.FirstOrDefault(ring => ring.HoverIndex == OtherGroupHoverIndex)?.ItemIndexes ?? []
            : [];
        using var modelFont = new Font(Font.FontFamily, 9.1F, FontStyle.Bold);
        using var metaFont = new Font(Font.FontFamily, 7.4F, FontStyle.Regular);
        using var valueFont = new Font(Font.FontFamily, 9.2F, FontStyle.Bold);
        using var captionFont = new Font(Font.FontFamily, 6.8F, FontStyle.Bold);
        using var textBrush = new SolidBrush(_textColor);
        using var dynamicMutedBrush = new SolidBrush(_mutedColor);
        using var dynamicColorBrush = new SolidBrush(_primaryColor);
        var rowTop = bounds.Top + headerHeight;
        for (var index = 0; index < _items.Length; index++)
        {
            var slot = new RectangleF(
                bounds.Left + Scale(10, dpi),
                rowTop,
                bounds.Width - Scale(20, dpi),
                rowHeight);
            var card = RectangleF.Inflate(slot, 0F, -Scale(4, dpi));
            _lastRowBounds[index] = card;
            var item = _items[index];
            var itemColor = GetTableItemColor(index, item, visualRings);
            var hovered = index == _hoveredIndex || hoveredOtherIndexes.Contains(index);
            using (var cardPath = CreateRoundedRectangle(card, Scale(12, dpi)))
            using (var cardFill = new LinearGradientBrush(
                       card,
                       Blend(_surfaceColor, itemColor, hovered ? 0.13F : 0.075F),
                       Blend(_surfaceColor, _primaryColor, hovered ? 0.055F : 0.018F),
                       LinearGradientMode.Horizontal))
            using (var cardBorder = new Pen(
                       Color.FromArgb(hovered ? 112 : 54, itemColor),
                       Math.Max(0.75F * dpi, 1F)))
            {
                graphics.FillPath(cardFill, cardPath);
                graphics.DrawPath(cardBorder, cardPath);
            }

            var strip = new RectangleF(
                card.Left + Scale(4, dpi),
                card.Top + Scale(9, dpi),
                Scale(3, dpi),
                Math.Max(Scale(18, dpi), card.Height - Scale(18, dpi)));
            using (var stripPath = CreateRoundedRectangle(strip, Scale(2, dpi)))
            using (var stripBrush = new SolidBrush(itemColor))
            {
                graphics.FillPath(stripBrush, stripPath);
            }
            var nodeRadius = 4.2F * dpi;
            var nodeCenter = new PointF(card.Left + Scale(18, dpi), card.Top + card.Height / 2F);
            using (var nodeGlow = new SolidBrush(Color.FromArgb(42, itemColor)))
            using (var node = new SolidBrush(itemColor))
            {
                graphics.FillEllipse(
                    nodeGlow,
                    nodeCenter.X - nodeRadius * 1.8F,
                    nodeCenter.Y - nodeRadius * 1.8F,
                    nodeRadius * 3.6F,
                    nodeRadius * 3.6F);
                graphics.FillEllipse(
                    node,
                    nodeCenter.X - nodeRadius,
                    nodeCenter.Y - nodeRadius,
                    nodeRadius * 2F,
                    nodeRadius * 2F);
            }

            var modelLeft = card.Left + Scale(31, dpi);
            var modelWidth = Math.Max(Scale(130, dpi), card.Width * 0.43F);
            var modelTop = card.Top + Scale(6, dpi);
            DrawCellText(
                graphics,
                item.Model,
                modelFont,
                textBrush,
                new RectangleF(modelLeft, modelTop, modelWidth, card.Height * 0.50F),
                false);
            dynamicColorBrush.Color = Blend(itemColor, _textColor, 0.20F);
            DrawCellText(
                graphics,
                $"{FormatUsagePercent(item.TotalTokens, totalTokens)}  ·  {item.Records:N0} 条记录",
                metaFont,
                dynamicColorBrush,
                new RectangleF(modelLeft, card.Top + card.Height * 0.48F, modelWidth, card.Height * 0.36F),
                false);

            var tokenLeft = card.Left + card.Width * 0.55F;
            var tokenWidth = card.Width * 0.19F;
            var costLeft = card.Left + card.Width * 0.76F;
            var costWidth = card.Right - costLeft - Scale(12, dpi);
            DrawCellText(
                graphics,
                "TOKEN",
                captionFont,
                dynamicMutedBrush,
                new RectangleF(tokenLeft, modelTop, tokenWidth, card.Height * 0.35F),
                true);
            DrawCellText(
                graphics,
                FormatTokens(item.TotalTokens),
                valueFont,
                textBrush,
                new RectangleF(tokenLeft, card.Top + card.Height * 0.37F, tokenWidth, card.Height * 0.48F),
                true);
            DrawCellText(
                graphics,
                "API 等值",
                captionFont,
                dynamicMutedBrush,
                new RectangleF(costLeft, modelTop, costWidth, card.Height * 0.35F),
                true);
            dynamicColorBrush.Color = itemColor;
            DrawCellText(
                graphics,
                FormatUsd(item.EquivalentCostUsd),
                valueFont,
                dynamicColorBrush,
                new RectangleF(costLeft, card.Top + card.Height * 0.37F, costWidth, card.Height * 0.48F),
                true);
            rowTop += rowHeight;
        }
    }

    private int HitTest(Point point)
    {
        for (var index = 0; index < _lastRowBounds.Length; index++)
        {
            if (_lastRowBounds[index].Contains(point))
            {
                return index;
            }
        }

        if (_lastRingTargets.Length == 0)
        {
            return NoHoverIndex;
        }

        var dpi = Math.Max(1F, DeviceDpi / 96F);
        var tolerance = Scale(3, dpi);
        var bestDistance = double.MaxValue;
        var bestIndex = NoHoverIndex;
        foreach (var target in _lastRingTargets)
        {
            var dx = point.X - target.Center.X;
            var dy = point.Y - target.Center.Y;
            var radians = -target.RotationDegrees * (Math.PI / 180D);
            var cosine = Math.Cos(radians);
            var sine = Math.Sin(radians);
            var localX = (dx * cosine) - (dy * sine);
            var localY = (dx * sine) + (dy * cosine);
            var radiusX = Math.Max(1D, target.Bounds.Width / 2D);
            var radiusY = Math.Max(1D, target.Bounds.Height / 2D);
            var normalizedDistance = Math.Sqrt(
                ((localX * localX) / (radiusX * radiusX)) +
                ((localY * localY) / (radiusY * radiusY)));
            var radialDistance = Math.Abs(normalizedDistance - 1D) * Math.Min(radiusX, radiusY);
            if (radialDistance <= (target.StrokeWidth / 2F) + tolerance && radialDistance < bestDistance)
            {
                bestDistance = radialDistance;
                bestIndex = target.HoverIndex;
            }
        }
        return bestIndex;
    }

    private VisualUsageRing[] BuildVisualRings()
    {
        var positive = _items
            .Select((item, index) => new IndexedUsageItem(index, item))
            .Where(entry => entry.Item.TotalTokens > 0)
            .ToArray();
        if (positive.Length <= MaxNamedRings)
        {
            return positive
                .Select(entry => new VisualUsageRing(
                    entry.Item.Model,
                    entry.Item.Records,
                    entry.Item.TotalTokens,
                    entry.Item.EquivalentCostUsd,
                    entry.Index,
                    [entry.Index]))
                .ToArray();
        }

        var rings = positive
            .Take(MaxNamedRings)
            .Select(entry => new VisualUsageRing(
                entry.Item.Model,
                entry.Item.Records,
                entry.Item.TotalTokens,
                entry.Item.EquivalentCostUsd,
                entry.Index,
                [entry.Index]))
            .ToList();
        var other = positive.Skip(MaxNamedRings).ToArray();
        rings.Add(new VisualUsageRing(
            "其他模型",
            other.Sum(entry => entry.Item.Records),
            other.Sum(entry => Math.Max(0L, entry.Item.TotalTokens)),
            other.Sum(entry => Math.Max(0D, entry.Item.EquivalentCostUsd)),
            OtherGroupHoverIndex,
            other.Select(entry => entry.Index).ToArray()));
        return rings.ToArray();
    }

    private bool IsRingHovered(VisualUsageRing ring)
    {
        if (_hoveredIndex == NoHoverIndex)
        {
            return false;
        }
        if (_hoveredIndex == OtherGroupHoverIndex)
        {
            return ring.HoverIndex == OtherGroupHoverIndex;
        }
        return ring.ItemIndexes.Contains(_hoveredIndex);
    }

    private ModelUsageDistributionItem? GetHoveredSummary(IReadOnlyList<VisualUsageRing> rings)
    {
        if (_hoveredIndex >= 0 && _hoveredIndex < _items.Length)
        {
            return _items[_hoveredIndex];
        }
        if (_hoveredIndex != OtherGroupHoverIndex)
        {
            return null;
        }

        var other = rings.FirstOrDefault(ring => ring.HoverIndex == OtherGroupHoverIndex);
        return other == null
            ? null
            : new ModelUsageDistributionItem(
                other.Model,
                other.Records,
                other.TotalTokens,
                other.EquivalentCostUsd);
    }

    private Color GetHoveredColor(IReadOnlyList<VisualUsageRing> rings)
    {
        if (_hoveredIndex >= 0 && _hoveredIndex < _items.Length)
        {
            var ring = rings.FirstOrDefault(entry => entry.ItemIndexes.Contains(_hoveredIndex));
            return ring == null
                ? GetModelColor(_items[_hoveredIndex].Model)
                : GetVisualRingColor(ring);
        }
        if (_hoveredIndex == OtherGroupHoverIndex)
        {
            return IsDarkVisualSurface() ? OtherRingColor : LightOtherRingColor;
        }
        return _primaryColor;
    }

    private Color GetVisualRingColor(VisualUsageRing ring)
    {
        if (ring.HoverIndex == OtherGroupHoverIndex)
        {
            return IsDarkVisualSurface() ? OtherRingColor : LightOtherRingColor;
        }

        return GetModelColor(ring.Model);
    }

    private static void DrawTonalArc(
        Graphics graphics,
        RectangleF bounds,
        float startAngle,
        float sweep,
        float stroke,
        int ringIndex,
        Color ringColor,
        bool roundCaps)
    {
        if (!IsDrawableBounds(bounds) ||
            !AreFinite(startAngle, sweep, stroke) ||
            sweep <= 0F ||
            stroke <= 0F)
        {
            return;
        }

        // Short overlapping segments create one continuous 360-degree tonal star track.
        // Token share is intentionally textual; color identifies the model and never bleeds
        // into a neighbouring ring.
        // Ten-degree samples remain smooth across the stronger 3.8-6.8px energy-band core and
        // halve the animated DrawArc workload of the former thick neon-band treatment.
        var segmentCount = Math.Clamp((int)Math.Ceiling(sweep / 10F), 4, 36);
        var segmentSweep = sweep / segmentCount;
        var overlap = Math.Min(0.9F, segmentSweep * 0.22F);
        var isCompleteRing = sweep >= RingSweepAngle - 0.5F;
        // Reuse one native GDI+ Pen for every tonal segment.  Creating up to 120 Pens per
        // ring on every 30-FPS frame caused severe handle churn; after scrolling the card
        // into view GDI+ could transiently fail and WinForms displayed the red-cross
        // placeholder.  Color/caps are mutable and do not require a new native object.
        using var segmentPen = new Pen(ringColor, stroke)
        {
            LineJoin = LineJoin.Round
        };
        for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            var drawStart = startAngle + (segmentIndex * segmentSweep);
            var drawSweep = segmentSweep;
            if (isCompleteRing)
            {
                // Overlap both sides, including the 0/360 seam. SampleRingTone is periodic,
                // so this closes the ring without introducing a mismatched cap colour.
                drawStart -= overlap * 0.5F;
                drawSweep += overlap;
            }
            else if (segmentIndex > 0)
            {
                drawStart -= overlap * 0.5F;
                drawSweep += overlap * 0.5F;
            }
            if (!isCompleteRing && segmentIndex < segmentCount - 1)
            {
                drawSweep += overlap * 0.5F;
            }

            var angularProgress = (segmentIndex + 0.5F) / segmentCount;
            var color = SampleRingTone(ringColor, angularProgress + (ringIndex * 0.071F));
            segmentPen.Color = color;
            segmentPen.StartCap = roundCaps && segmentIndex == 0 ? LineCap.Round : LineCap.Flat;
            segmentPen.EndCap = roundCaps && segmentIndex == segmentCount - 1 ? LineCap.Round : LineCap.Flat;
            graphics.DrawArc(segmentPen, bounds, drawStart, drawSweep);
        }

        if (!isCompleteRing)
        {
            return;
        }

        // Three fixed scan windows give the otherwise continuous identity ring a precise
        // optical-instrument feel. They are deliberately thin, static and phase-shifted by
        // model so the white surface stays calm while each orbit feels individually alive.
        using var glintPen = new Pen(
            Color.Transparent,
            Math.Max(0.92F, stroke * 0.19F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        for (var glintIndex = 0; glintIndex < 3; glintIndex++)
        {
            var phase = NormalizeProgress(
                0.10F + (ringIndex * 0.137F) + (glintIndex * 0.31F));
            var glintSweep = 13F + (((ringIndex + glintIndex) % 3) * 4F);
            glintPen.Color = Color.FromArgb(
                222,
                Blend(
                    SampleRingTone(ringColor, phase + (ringIndex * 0.071F)),
                    Color.White,
                    0.58F));
            graphics.DrawArc(
                glintPen,
                bounds,
                startAngle + (phase * RingSweepAngle) - (glintSweep / 2F),
                glintSweep);
        }
    }

    private static void DrawOrbitalDepthAccents(
        Graphics graphics,
        RectangleF bounds,
        float startAngle,
        float stroke,
        Color ringColor,
        bool hovered,
        float dpi)
    {
        if (!IsDrawableBounds(bounds) ||
            !AreFinite(startAngle, stroke, dpi) ||
            stroke <= 0F ||
            dpi <= 0F)
        {
            return;
        }

        // Two restrained tonal accents imply a foreground and a distant side of the orbit.
        // The underlying identity line is still a complete, unbroken 360-degree ring.
        using var depthPen = new Pen(
            Color.FromArgb(hovered ? 188 : 142, Blend(ringColor, Color.White, 0.66F)),
            Math.Max(0.72F * dpi, stroke * 0.16F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawArc(depthPen, bounds, startAngle + 44F, 74F);

        depthPen.Color = Color.FromArgb(72, Blend(ringColor, Color.Black, 0.52F));
        depthPen.Width = Math.Max(0.82F * dpi, stroke * 0.22F);
        graphics.DrawArc(depthPen, bounds, startAngle + 205F, 92F);
    }

    private static void DrawPlanetDust(
        Graphics graphics,
        RectangleF bounds,
        float startAngle,
        float sweep,
        float stroke,
        int ringIndex,
        Color ringColor,
        float dpi,
        bool darkSurface,
        bool drawSatellite)
    {
        if (!IsDrawableBounds(bounds) ||
            !AreFinite(startAngle, sweep, stroke, dpi) ||
            sweep < 18F ||
            stroke <= 0F ||
            dpi <= 0F)
        {
            return;
        }

        // Sparse stationary stars provide planetary texture without the old continuous
        // cloud belts, grid lines or custom dash patterns.
        var dustCount = Math.Clamp(
            2 + (int)Math.Floor(sweep / 150F),
            2,
            MaximumPlanetDustCount);
        using var glowBrush = new SolidBrush(Color.Transparent);
        using var coreBrush = new SolidBrush(Color.White);
        using var shadeBrush = new SolidBrush(Color.Transparent);
        using var highlightBrush = new SolidBrush(Color.Transparent);
        using var relayPen = new Pen(
            Color.Transparent,
            Math.Max(0.62F * dpi, stroke * 0.045F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        for (var index = 0; index < dustCount; index++)
        {
            var orbit = NormalizeProgress(
                (index * 0.618034F) + (ringIndex * 0.173F) + 0.11F);
            var ratio = 0.14F + (orbit * 0.72F);
            var point = GetEllipsePoint(bounds, startAngle + (sweep * ratio));
            if (!AreFinite(point.X, point.Y))
            {
                continue;
            }
            var sparkle = Blend(
                SampleRingTone(ringColor, ratio + (ringIndex * 0.09F)),
                Color.White,
                0.70F);
            if (index == 0 && drawSatellite)
            {
                // The first point on the two outermost tracks is a tiny satellite.  Three
                // inexpensive filled circles provide a halo, shaded globe and specular spot;
                // unlike a per-frame gradient brush this stays cheap at 200% DPI.
                var moonRadius = Math.Clamp(
                    stroke * 0.48F,
                    2.6F * dpi,
                    5.2F * dpi);
                var moonGlowRadius = moonRadius * 1.82F;
                glowBrush.Color = Color.FromArgb(
                    darkSurface ? 74 : 42,
                    Blend(ringColor, Color.White, 0.44F));
                coreBrush.Color = Blend(
                    Blend(ringColor, Color.FromArgb(21, 35, 86), 0.40F),
                    Color.White,
                    darkSurface ? 0.18F : 0.30F);
                shadeBrush.Color = Color.FromArgb(
                    darkSurface ? 118 : 82,
                    Blend(ringColor, Color.FromArgb(4, 9, 35), 0.74F));
                highlightBrush.Color = Color.FromArgb(
                    darkSurface ? 218 : 184,
                    Blend(ringColor, Color.White, 0.84F));
                graphics.FillEllipse(
                    glowBrush,
                    point.X - moonGlowRadius,
                    point.Y - moonGlowRadius,
                    moonGlowRadius * 2F,
                    moonGlowRadius * 2F);
                graphics.FillEllipse(
                    coreBrush,
                    point.X - moonRadius,
                    point.Y - moonRadius,
                    moonRadius * 2F,
                    moonRadius * 2F);
                var shadowRadius = moonRadius * 0.62F;
                graphics.FillEllipse(
                    shadeBrush,
                    point.X + moonRadius * 0.02F,
                    point.Y - moonRadius * 0.34F,
                    shadowRadius * 1.42F,
                    shadowRadius * 1.54F);
                var highlightRadius = moonRadius * 0.24F;
                graphics.FillEllipse(
                    highlightBrush,
                    point.X - moonRadius * 0.48F,
                    point.Y - moonRadius * 0.47F,
                    highlightRadius * 2F,
                    highlightRadius * 1.52F);
                continue;
            }
            var coreRadius = Math.Max(0.65F, stroke * (0.045F + ((index % 2) * 0.012F)));
            var glowRadius = coreRadius * 2.25F;
            glowBrush.Color = Color.FromArgb(42 + ((index % 2) * 12), sparkle);
            coreBrush.Color = Color.FromArgb(176 + ((index % 2) * 38), sparkle);
            graphics.FillEllipse(
                glowBrush,
                point.X - glowRadius,
                point.Y - glowRadius,
                glowRadius * 2F,
                glowRadius * 2F);
            graphics.FillEllipse(
                coreBrush,
                point.X - coreRadius,
                point.Y - coreRadius,
                coreRadius * 2F,
                coreRadius * 2F);
            if (index == dustCount - 1)
            {
                // Turn one existing dust point into a tiny split-ring relay station. This
                // adds a readable piece of orbital instrumentation without increasing the
                // star budget or introducing an extra data marker.
                var relayRadius = Math.Max(2.05F * dpi, stroke * 0.23F);
                var relayBounds = new RectangleF(
                    point.X - relayRadius,
                    point.Y - relayRadius,
                    relayRadius * 2F,
                    relayRadius * 2F);
                relayPen.Color = Color.FromArgb(
                    darkSurface ? 206 : 164,
                    Blend(sparkle, Color.White, darkSurface ? 0.56F : 0.68F));
                graphics.DrawArc(relayPen, relayBounds, -28F, 126F);
                graphics.DrawArc(relayPen, relayBounds, 152F, 126F);

                var relayCoreRadius = Math.Max(0.48F * dpi, coreRadius * 0.62F);
                coreBrush.Color = Color.FromArgb(darkSurface ? 238 : 226, Color.White);
                graphics.FillEllipse(
                    coreBrush,
                    point.X - relayCoreRadius,
                    point.Y - relayCoreRadius,
                    relayCoreRadius * 2F,
                    relayCoreRadius * 2F);
            }
        }
    }

    private static void DrawUsageEnergyEdges(
        Graphics graphics,
        RectangleF bounds,
        float startAngle,
        float sweep,
        float stroke,
        Color ringColor,
        bool roundCaps,
        float dpi,
        bool darkSurface)
    {
        if (!IsDrawableBounds(bounds) ||
            !AreFinite(startAngle, sweep, stroke, dpi) ||
            sweep <= 0F ||
            stroke <= 0F ||
            dpi <= 0F)
        {
            return;
        }

        var rimOffset = Math.Max(0.48F * dpi, stroke * 0.075F);
        var outerBounds = RectangleF.Inflate(bounds, rimOffset, rimOffset);
        var innerBounds = RectangleF.Inflate(bounds, -rimOffset, -rimOffset);
        if (!IsDrawableBounds(outerBounds) || !IsDrawableBounds(innerBounds))
        {
            return;
        }

        var cap = roundCaps ? LineCap.Round : LineCap.Flat;
        var rimWidth = Math.Max(0.68F * dpi, stroke * 0.06F);
        using var innerRim = new Pen(
            Color.FromArgb(
                darkSurface ? 96 : 62,
                Blend(ringColor, Color.Black, darkSurface ? 0.30F : 0.20F)),
            rimWidth)
        {
            StartCap = cap,
            EndCap = cap
        };
        using var outerRim = new Pen(
            Color.FromArgb(
                darkSurface ? 214 : 168,
                Blend(ringColor, Color.White, darkSurface ? 0.72F : 0.62F)),
            rimWidth)
        {
            StartCap = cap,
            EndCap = cap
        };
        if (sweep >= RingSweepAngle - 0.5F)
        {
            graphics.DrawEllipse(innerRim, innerBounds);
            graphics.DrawEllipse(outerRim, outerBounds);
        }
        else
        {
            graphics.DrawArc(innerRim, innerBounds, startAngle, sweep);
            graphics.DrawArc(outerRim, outerBounds, startAngle, sweep);
        }

        if (sweep < RingSweepAngle - 0.5F)
        {
            return;
        }

        // A pair of sharply focused refraction strokes makes the outer glass rail read as
        // a scanning energy track instead of a uniformly plastic tube. The model's own
        // hue remains visible beneath the highlight and the marks are static by design.
        var scannerOffset = Math.Max(1.60F * dpi, stroke * 0.20F);
        var scannerBounds = RectangleF.Inflate(outerBounds, scannerOffset, scannerOffset);
        if (!IsDrawableBounds(scannerBounds))
        {
            return;
        }
        using var scannerGlowPen = new Pen(
            Color.FromArgb(
                darkSurface ? 92 : 54,
                Blend(ringColor, Color.White, darkSurface ? 0.34F : 0.48F)),
            Math.Max(2.80F * dpi, stroke * 0.32F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var edgeGlintPen = new Pen(
            Color.FromArgb(
                darkSurface ? 236 : 212,
                Blend(ringColor, Color.White, darkSurface ? 0.68F : 0.72F)),
            Math.Max(0.90F * dpi, stroke * 0.12F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        for (var glintIndex = 0; glintIndex < 2; glintIndex++)
        {
            var glintStart = startAngle + (glintIndex == 0 ? 38F : 206F);
            var glintSweep = glintIndex == 0 ? 30F : 23F;
            graphics.DrawArc(scannerGlowPen, scannerBounds, glintStart, glintSweep);
            graphics.DrawArc(edgeGlintPen, scannerBounds, glintStart, glintSweep);
        }

        if (!darkSurface)
        {
            // On the static white surface, a phased six-piece sweep gives each real model
            // orbit an unmistakable energy direction without reintroducing a timer. Keep
            // this out of the animated dark scene, whose comet already supplies movement.
            const int sweepSegmentCount = 6;
            const float sweepSegmentAngle = 7F;
            var scanStart = startAngle + 282F;
            var baseWidth = Math.Max(0.90F * dpi, stroke * 0.12F);
            for (var segmentIndex = 0; segmentIndex < sweepSegmentCount; segmentIndex++)
            {
                var strength = (segmentIndex + 1F) / sweepSegmentCount;
                edgeGlintPen.Width = baseWidth * (0.70F + (strength * 0.68F));
                edgeGlintPen.Color = Color.FromArgb(
                    (int)Math.Round(48F + (188F * strength * strength)),
                    Blend(ringColor, Color.White, 0.54F + (strength * 0.24F)));
                graphics.DrawArc(
                    edgeGlintPen,
                    scannerBounds,
                    scanStart + (segmentIndex * sweepSegmentAngle),
                    sweepSegmentAngle + 0.8F);
            }
        }
    }

    private void DrawPlanetOrbit(
        Graphics graphics,
        RectangleF bounds,
        float orbitStroke,
        Color ringColor,
        bool hovered,
        float dpi)
    {
        if (!IsDrawableBounds(bounds) ||
            !AreFinite(orbitStroke, dpi) ||
            orbitStroke <= 0F ||
            dpi <= 0F)
        {
            return;
        }

        // A hairline guide and a very soft bloom sit behind the coloured identity line.  Both
        // remain subtle so the system reads as orbital instrumentation, not nested tubing.
        var orbitColor = Blend(ringColor, Color.White, 0.28F);
        using var halo = new Pen(
            Color.FromArgb(hovered ? 48 : 22, ringColor),
            orbitStroke + (2.1F * dpi));
        using var core = new Pen(
            Color.FromArgb(hovered ? 196 : 104, orbitColor),
            orbitStroke);
        graphics.DrawEllipse(halo, bounds);
        graphics.DrawEllipse(core, bounds);
    }

    private static void DrawAmbientOrbitTrails(
        Graphics graphics,
        RectangleF stageBounds,
        PointF center,
        float dpi,
        int dataRingCount,
        bool darkSurface)
    {
        if (!IsDrawableBounds(stageBounds) ||
            !AreFinite(center.X, center.Y) ||
            !IsFinitePositive(dpi))
        {
            return;
        }

        var scene = RectangleF.Inflate(stageBounds, -Scale(4, dpi), -Scale(4, dpi));
        if (!IsDrawableBounds(scene))
        {
            return;
        }

        // The supporting trails recede as more real model identities are present.  They
        // are most pronounced for the common one-model, 100% case represented by the
        // central planet, never pretending to be additional data series.
        var density = dataRingCount switch
        {
            <= 1 => 1F,
            2 => 0.66F,
            _ => 0.38F
        };
        // On the white surface these become pale holographic construction lines rather
        // than a second set of saturated data rings.
        density *= darkSurface ? 1F : 0.24F;
        var baseRadius = Math.Max(
            Scale(48, dpi),
            Math.Min(scene.Width * 0.47F, scene.Height * 0.58F));
        var outerColor = darkSurface ? DarkSolRingColor : LightSolRingColor;
        var middleColor = darkSurface ? DarkLunaRingColor : LightLunaRingColor;
        var innerColor = darkSurface ? WarmOrbitAccentColor : LightGpt55RingColor;
        var closeColor = darkSurface ? DarkTerraRingColor : LightTerraRingColor;
        using var clipPath = CreateRoundedRectangle(scene, Scale(17, dpi));
        var state = graphics.Save();
        try
        {
            graphics.SetClip(clipPath, CombineMode.Intersect);
            DrawAmbientOrbitTrail(
                graphics,
                center,
                baseRadius * 1.10F,
                0.47F,
                -16F,
                184F,
                244F,
                outerColor,
                density,
                dpi,
                0.42F);
            DrawAmbientOrbitTrail(
                graphics,
                center,
                baseRadius * 0.98F,
                0.56F,
                9F,
                18F,
                248F,
                middleColor,
                density,
                dpi,
                0.78F);
            DrawAmbientOrbitTrail(
                graphics,
                center,
                baseRadius * 0.76F,
                0.42F,
                -5F,
                118F,
                190F,
                innerColor,
                density * 0.84F,
                dpi,
                0.34F);

            if (dataRingCount <= 1)
            {
                DrawAmbientOrbitTrail(
                    graphics,
                    center,
                    baseRadius * 0.60F,
                    0.34F,
                    3F,
                    206F,
                    126F,
                    closeColor,
                    density * 0.68F,
                    dpi,
                    0.67F);
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawAmbientOrbitTrail(
        Graphics graphics,
        PointF center,
        float radius,
        float verticalScale,
        float rotation,
        float startAngle,
        float sweepAngle,
        Color color,
        float density,
        float dpi,
        float nodeProgress)
    {
        if (!AreFinite(center.X, center.Y, radius, verticalScale) ||
            !AreFinite(rotation, startAngle, sweepAngle) ||
            radius <= 0F ||
            verticalScale <= 0F ||
            sweepAngle <= 0F ||
            density <= 0.01F)
        {
            return;
        }

        var bounds = new RectangleF(
            center.X - radius,
            center.Y - (radius * verticalScale),
            radius * 2F,
            radius * verticalScale * 2F);
        if (!IsDrawableBounds(bounds))
        {
            return;
        }

        var alpha = Math.Clamp((int)Math.Round(214F * density), 18, 214);
        var state = graphics.Save();
        try
        {
            RotateAround(graphics, center, rotation);
            using var glow = new Pen(
                Color.FromArgb(Math.Clamp((int)Math.Round(54F * density), 8, 72), color),
                Math.Max(2.8F * dpi, radius * 0.026F))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var core = new Pen(
                Color.FromArgb(alpha, Blend(color, Color.White, 0.36F)),
                Math.Max(0.82F * dpi, radius * 0.007F))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawArc(glow, bounds, startAngle, sweepAngle);
            graphics.DrawArc(core, bounds, startAngle, sweepAngle);

            var nodeAngle = startAngle + (sweepAngle * Math.Clamp(nodeProgress, 0F, 1F));
            var node = GetEllipsePoint(bounds, nodeAngle);
            DrawAmbientOrbitNode(graphics, node, color, density, dpi);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawAmbientOrbitNode(
        Graphics graphics,
        PointF center,
        Color color,
        float density,
        float dpi)
    {
        if (!AreFinite(center.X, center.Y) || density <= 0.01F)
        {
            return;
        }

        var radius = Math.Max(1.7F * dpi, 2.5F * dpi * density);
        using var halo = new SolidBrush(Color.FromArgb(
            Math.Clamp((int)Math.Round(86F * density), 12, 96),
            Blend(color, Color.White, 0.34F)));
        using var core = new SolidBrush(Color.FromArgb(
            Math.Clamp((int)Math.Round(238F * density), 90, 246),
            Blend(color, Color.White, 0.72F)));
        graphics.FillEllipse(
            halo,
            center.X - radius * 2.2F,
            center.Y - radius * 2.2F,
            radius * 4.4F,
            radius * 4.4F);
        graphics.FillEllipse(
            core,
            center.X - radius,
            center.Y - radius,
            radius * 2F,
            radius * 2F);
    }

    private void DrawEmptyGalaxyPlaceholder(
        Graphics graphics,
        PointF center,
        float planetRadius,
        float dpi,
        bool forceDarkSurface)
    {
        if (!AreFinite(center.X, center.Y, planetRadius) ||
            planetRadius <= 0F ||
            !IsFinitePositive(dpi))
        {
            return;
        }

        var darkSurface = forceDarkSurface || IsDarkVisualSurface();
        var primary = Blend(_primaryColor, _accentColor, 0.28F);
        var innerOrbit = new RectangleF(
            center.X - planetRadius * 1.58F,
            center.Y - planetRadius * 0.60F,
            planetRadius * 3.16F,
            planetRadius * 1.20F);
        var outerOrbit = new RectangleF(
            center.X - planetRadius * 2.08F,
            center.Y - planetRadius * 0.83F,
            planetRadius * 4.16F,
            planetRadius * 1.66F);

        var state = graphics.Save();
        try
        {
            RotateAround(graphics, center, -9F);
            using var outerGlow = new Pen(
                Color.FromArgb(darkSurface ? 18 : 10, primary),
                4.2F * dpi)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var outerLine = new Pen(
                Color.FromArgb(darkSurface ? 78 : 50, primary),
                Math.Max(0.72F * dpi, 1F))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawArc(outerGlow, outerOrbit, 194F, 238F);
            graphics.DrawArc(outerLine, outerOrbit, 194F, 238F);

            outerLine.Color = Color.FromArgb(
                darkSurface ? 94 : 58,
                Blend(_secondaryColor, _accentColor, 0.42F));
            outerLine.Width = Math.Max(0.82F * dpi, 1F);
            graphics.DrawArc(outerLine, innerOrbit, 8F, 164F);
        }
        finally
        {
            graphics.Restore(state);
        }

        using var dust = new SolidBrush(Color.FromArgb(
            darkSurface ? 124 : 74,
            Blend(primary, Color.White, darkSurface ? 0.58F : 0.16F)));
        foreach (var point in new[]
                 {
                     new PointF(center.X - planetRadius * 1.72F, center.Y - planetRadius * 0.55F),
                     new PointF(center.X + planetRadius * 1.84F, center.Y + planetRadius * 0.31F),
                     new PointF(center.X + planetRadius * 1.30F, center.Y - planetRadius * 0.72F)
                 })
        {
            var radius = 0.82F * dpi;
            graphics.FillEllipse(
                dust,
                point.X - radius,
                point.Y - radius,
                radius * 2F,
                radius * 2F);
        }
    }

    private void DrawOrbitalBackdrop(
        Graphics graphics,
        RectangleF stageBounds,
        RectangleF systemBounds,
        float dpi,
        bool animateMeteor,
        bool forceDarkSurface)
    {
        if (!IsDrawableBounds(stageBounds) ||
            !IsDrawableBounds(systemBounds) ||
            !IsFinitePositive(dpi))
        {
            return;
        }

        var stage = RectangleF.Inflate(stageBounds, -Scale(2, dpi), -Scale(2, dpi));
        if (!IsDrawableBounds(stage))
        {
            return;
        }

        var darkSurface = forceDarkSurface || IsDarkVisualSurface();
        var stageStart = darkSurface
            ? Color.FromArgb(3, 10, 35)
            : Color.FromArgb(253, 254, 255);
        var stageEnd = darkSurface
            ? Color.FromArgb(26, 13, 66)
            : Color.FromArgb(245, 249, 255);
        using var stagePath = CreateRoundedRectangle(stage, Scale(18, dpi));
        using var stageFill = new LinearGradientBrush(
            stage,
            stageStart,
            stageEnd,
            LinearGradientMode.ForwardDiagonal);
        graphics.FillPath(stageFill, stagePath);
        using var stageOuterGlow = new Pen(
            darkSurface
                ? Color.FromArgb(54, Blend(DarkSolRingColor, DarkLunaRingColor, 0.42F))
                : Color.FromArgb(32, Blend(_borderColor, _primaryColor, 0.18F)),
            Math.Max(darkSurface ? 3.6F * dpi : 2.4F * dpi, 1.5F));
        graphics.DrawPath(stageOuterGlow, stagePath);
        using var stageBorder = new Pen(
            darkSurface
                ? Color.FromArgb(164, Blend(DarkSolRingColor, DarkLunaRingColor, 0.30F))
                : Color.FromArgb(126, Blend(_borderColor, _primaryColor, 0.18F)),
            Math.Max(0.8F * dpi, 1F));
        graphics.DrawPath(stageBorder, stagePath);

        // Layered radial nebulas add depth without leaving the flat translucent blobs that
        // made the old backdrop feel pasted together.
        var backdropState = graphics.Save();
        graphics.SetClip(stagePath, CombineMode.Intersect);
        DrawNebulaCloud(
            graphics,
            new RectangleF(
                stage.Left - stage.Width * 0.20F,
                stage.Top - stage.Height * 0.30F,
                stage.Width * 0.92F,
                stage.Height * 1.02F),
            SolRingColor,
            darkSurface ? 104 : 13);
        DrawNebulaCloud(
            graphics,
            new RectangleF(
                stage.Left + stage.Width * 0.38F,
                stage.Top + stage.Height * 0.22F,
                stage.Width * 0.90F,
                stage.Height * 0.92F),
            LunaRingColor,
            darkSurface ? 92 : 10);
        DrawNebulaCloud(
            graphics,
            new RectangleF(
                stage.Left + stage.Width * 0.08F,
                stage.Top + stage.Height * 0.58F,
                stage.Width * 0.62F,
                stage.Height * 0.54F),
            TerraRingColor,
            darkSurface ? 58 : 7);

        // A distant orbital plane sits behind the model identities and adds scale.  It is
        // intentionally a single hairline ellipse rather than another coloured data ring.
        var planeState = graphics.Save();
        RotateAround(graphics, new PointF(stage.Left + stage.Width / 2F, stage.Top + stage.Height / 2F), -9F);
        var planeBounds = RectangleF.Inflate(systemBounds, Scale(10, dpi), -systemBounds.Height * 0.16F);
        if (IsDrawableBounds(planeBounds))
        {
            using var planeGlow = new Pen(
                Color.FromArgb(darkSurface ? 20 : 9, Blend(_primaryColor, _accentColor, 0.45F)),
                3.2F * dpi);
            using var planeCore = new Pen(
                Color.FromArgb(darkSurface ? 74 : 42, Blend(_primaryColor, _accentColor, 0.36F)),
                Math.Max(0.68F * dpi, 1F));
            graphics.DrawEllipse(planeGlow, planeBounds);
            graphics.DrawEllipse(planeCore, planeBounds);
        }
        graphics.Restore(planeState);

        using var blueStar = new SolidBrush(Color.FromArgb(
            darkSurface ? 178 : 92,
            darkSurface ? Blend(SolRingColor, Color.White, 0.78F) : SolRingColor));
        using var violetStar = new SolidBrush(Color.FromArgb(
            darkSurface ? 166 : 82,
            darkSurface ? Blend(LunaRingColor, Color.White, 0.78F) : LunaRingColor));
        foreach (var star in OrbitalBackdropStars)
        {
            var radius = Math.Max(0.55F * dpi, star.Radius * dpi);
            var x = stage.Left + stage.Width * star.X;
            var y = stage.Top + stage.Height * star.Y;
            graphics.FillEllipse(
                star.Violet ? violetStar : blueStar,
                x - radius,
                y - radius,
                radius * 2F,
                radius * 2F);
        }

        // Deterministic micro-stars keep the scene rich without allocating Random or
        // changing between frames. Two larger stars receive a restrained cross flare.
        using var microStar = new SolidBrush(
            darkSurface
                ? Color.FromArgb(146, 222, 235, 255)
                : Color.FromArgb(66, Blend(_primaryColor, _accentColor, 0.52F)));
        using var flarePen = new Pen(
            darkSurface
                ? Color.FromArgb(92, 196, 216, 255)
                : Color.FromArgb(52, Blend(_secondaryColor, _accentColor, 0.48F)),
            Math.Max(0.55F * dpi, 1F));
        var microStarCount = darkSurface ? 64 : 22;
        for (var index = 0; index < microStarCount; index++)
        {
            var xRatio = ((index * 37) % 101) / 100F;
            var yRatio = ((index * 61 + 17) % 97) / 96F;
            var radius = (0.34F + ((index % 4) * 0.13F)) * dpi;
            var x = stage.Left + stage.Width * xRatio;
            var y = stage.Top + stage.Height * yRatio;
            graphics.FillEllipse(microStar, x - radius, y - radius, radius * 2F, radius * 2F);
            if (index is 7 or 19 or 33)
            {
                var ray = (index == 7 ? 5.6F : 4.2F) * dpi;
                graphics.DrawLine(flarePen, x - ray, y, x + ray, y);
                graphics.DrawLine(flarePen, x, y - ray, x, y + ray);
            }
        }

        DrawBackdropPlanet(
            graphics,
            new PointF(stage.Left + stage.Width * 0.84F, stage.Top + stage.Height * 0.18F),
            5.4F * dpi,
            SolRingColor,
            darkSurface);
        DrawBackdropPlanet(
            graphics,
            new PointF(stage.Left + stage.Width * 0.14F, stage.Top + stage.Height * 0.79F),
            4.1F * dpi,
            LunaRingColor,
            darkSurface);

        if (animateMeteor && !_isRenderingStaticDonutCache)
        {
            _lastMeteorAnimationBounds = new RectangleF(
                stage.Left + stage.Width * 0.02F,
                stage.Top,
                stage.Width * 0.92F,
                Math.Max(Scale(64, dpi), stage.Height * 0.285F));
            DrawDistantMeteors(graphics, stage, dpi, _animationPhase, darkSurface);
        }
        graphics.Restore(backdropState);
    }

    private static void DrawBackdropPlanet(
        Graphics graphics,
        PointF center,
        float radius,
        Color color,
        bool darkSurface)
    {
        if (!AreFinite(center.X, center.Y, radius) || radius <= 0F)
        {
            return;
        }

        var haloRadius = radius * 1.72F;
        using var halo = new SolidBrush(Color.FromArgb(
            darkSurface ? 50 : 20,
            Blend(color, Color.White, 0.38F)));
        using var globe = new SolidBrush(Color.FromArgb(
            darkSurface ? 236 : 138,
            Blend(color, Color.FromArgb(45, 78, 184), 0.30F)));
        using var shadow = new SolidBrush(Color.FromArgb(
            darkSurface ? 176 : 92,
            Color.FromArgb(3, 8, 31)));
        using var highlight = new SolidBrush(Color.FromArgb(
            darkSurface ? 232 : 158,
            Blend(color, Color.White, 0.82F)));
        graphics.FillEllipse(
            halo,
            center.X - haloRadius,
            center.Y - haloRadius,
            haloRadius * 2F,
            haloRadius * 2F);
        graphics.FillEllipse(
            globe,
            center.X - radius,
            center.Y - radius,
            radius * 2F,
            radius * 2F);
        graphics.FillEllipse(
            shadow,
            center.X - radius * 0.02F,
            center.Y - radius * 0.60F,
            radius * 1.42F,
            radius * 1.72F);
        var highlightRadius = radius * 0.24F;
        graphics.FillEllipse(
            highlight,
            center.X - radius * 0.52F,
            center.Y - radius * 0.52F,
            highlightRadius * 2F,
            highlightRadius * 1.56F);
    }

    private static void DrawNebulaCloud(
        Graphics graphics,
        RectangleF bounds,
        Color color,
        int centerAlpha)
    {
        if (!IsDrawableBounds(bounds))
        {
            return;
        }

        using var path = new GraphicsPath();
        path.AddEllipse(bounds);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(Math.Clamp(centerAlpha, 0, 255), color),
            SurroundColors = [Color.FromArgb(0, color)],
            CenterPoint = new PointF(
                bounds.Left + bounds.Width * 0.46F,
                bounds.Top + bounds.Height * 0.44F),
            FocusScales = new PointF(0.16F, 0.14F)
        };
        graphics.FillPath(brush, path);
    }

    private static void DrawDistantMeteors(
        Graphics graphics,
        RectangleF stage,
        float dpi,
        float animationPhase,
        bool darkSurface)
    {
        // Different tempos, lanes and hues make this read as a living sky rather than a
        // single line repeatedly sliding through the same corridor. Six staggered routes
        // ensure a dense but still legible night sky at every phase; all are deterministic,
        // so the scene never flickers between frames.
        DrawDistantMeteor(
            graphics, stage, dpi, animationPhase, darkSurface,
            phaseOffset: 0.08F, phaseRate: 1.00F,
            startX: 0.12F, startY: 0.07F, travelX: 0.76F, travelY: 0.15F,
            curvature: 0.052F, tailSpan: 0.18F, color: SolRingColor, intensity: 1F);
        DrawDistantMeteor(
            graphics, stage, dpi, animationPhase, darkSurface,
            phaseOffset: 0.47F, phaseRate: 1.42F,
            startX: 0.58F, startY: 0.12F, travelX: 0.28F, travelY: 0.10F,
            curvature: -0.044F, tailSpan: 0.12F, color: TerraRingColor, intensity: 0.70F);
        DrawDistantMeteor(
            graphics, stage, dpi, animationPhase, darkSurface,
            phaseOffset: 0.73F, phaseRate: 0.66F,
            startX: 0.14F, startY: 0.79F, travelX: 0.54F, travelY: -0.10F,
            curvature: 0.042F, tailSpan: 0.11F, color: LunaRingColor, intensity: 0.56F);
        DrawDistantMeteor(
            graphics, stage, dpi, animationPhase, darkSurface,
            phaseOffset: 0.22F, phaseRate: 0.86F,
            startX: 0.69F, startY: 0.77F, travelX: 0.22F, travelY: -0.31F,
            curvature: -0.050F, tailSpan: 0.10F, color: Gpt55RingColor, intensity: 0.50F);
        DrawDistantMeteor(
            graphics, stage, dpi, animationPhase, darkSurface,
            phaseOffset: 0.59F, phaseRate: 1.18F,
            startX: 0.05F, startY: 0.48F, travelX: 0.37F, travelY: -0.22F,
            curvature: 0.034F, tailSpan: 0.09F, color: OtherRingColor, intensity: 0.43F);
        DrawDistantMeteor(
            graphics, stage, dpi, animationPhase, darkSurface,
            phaseOffset: 0.91F, phaseRate: 0.54F,
            startX: 0.63F, startY: 0.90F, travelX: 0.25F, travelY: -0.08F,
            curvature: 0.045F, tailSpan: 0.13F, color: TerraRingColor, intensity: 0.46F);
    }

    private static void DrawDistantMeteor(
        Graphics graphics,
        RectangleF stage,
        float dpi,
        float animationPhase,
        bool darkSurface,
        float phaseOffset,
        float phaseRate,
        float startX,
        float startY,
        float travelX,
        float travelY,
        float curvature,
        float tailSpan,
        Color color,
        float intensity)
    {
        var progress = NormalizeProgress((animationPhase * phaseRate) + phaseOffset);
        // Fade completely at both ends of a route, making the phase wrap invisible.
        var visibility = Math.Clamp(MathF.Sin(progress * MathF.PI) * 1.62F, 0F, 1F);
        if (visibility <= 0.015F || intensity <= 0F)
        {
            return;
        }

        // Offset the path along the normal of its travel vector, rather than only the vertical
        // axis.  This keeps every lane genuinely curved even when a meteor travels diagonally.
        var travelLength = MathF.Sqrt((travelX * travelX) + (travelY * travelY));
        var normalX = travelLength > 0.0001F ? -travelY / travelLength : 0F;
        var normalY = travelLength > 0.0001F ? travelX / travelLength : 1F;
        PointF GetPoint(float phase)
        {
            var boundedPhase = Math.Clamp(phase, 0F, 1F);
            var curve = MathF.Sin(boundedPhase * MathF.PI) * curvature;
            return new PointF(
                stage.Left + stage.Width * (startX + (travelX * boundedPhase) + (normalX * curve)),
                stage.Top + stage.Height * (startY + (travelY * boundedPhase) + (normalY * curve)));
        }

        // Sample earlier positions on the exact route.  Drawing short consecutive segments
        // instead of one tail-to-head chord keeps the visible wake aligned with the motion at
        // every point on the bend; it also gives the tail a controlled tail-to-head brightening.
        var tailProgress = Math.Max(0F, progress - tailSpan);
        var visibleSpan = Math.Max(0.0001F, progress - tailProgress);
        Span<PointF> history = stackalloc PointF[BackdropMeteorTrailSegmentCount + 1];
        for (var index = 0; index <= BackdropMeteorTrailSegmentCount; index++)
        {
            var sampleProgress = tailProgress + (visibleSpan * index / BackdropMeteorTrailSegmentCount);
            history[index] = GetPoint(sampleProgress);
        }

        var head = history[BackdropMeteorTrailSegmentCount];
        // Light cards retain the same moving energy as the dark presentation without becoming
        // a colored block behind the content.  The prior 58% multiplier made the flow vanish.
        var alphaScale = (darkSurface ? 1F : 0.76F) * Math.Clamp(intensity, 0F, 1F);
        var glowColor = Blend(color, Color.White, 0.28F);
        var trailColor = Blend(color, Color.White, 0.52F);
        using var glow = new Pen(Color.Transparent, 1F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var trail = new Pen(Color.Transparent, 1F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var core = new Pen(Color.Transparent, 1F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        for (var index = 0; index < BackdropMeteorTrailSegmentCount; index++)
        {
            var strength = MathF.Pow((index + 1F) / BackdropMeteorTrailSegmentCount, 1.32F);
            var segmentAlpha = visibility * alphaScale * (0.06F + (0.94F * strength));
            glow.Color = Color.FromArgb(
                (int)Math.Round(42F * segmentAlpha),
                glowColor);
            glow.Width = Math.Max(
                1.55F * dpi,
                5.5F * dpi * (0.30F + (0.70F * strength)) * (0.72F + (intensity * 0.28F)));
            trail.Color = Color.FromArgb(
                (int)Math.Round(146F * segmentAlpha),
                trailColor);
            trail.Width = Math.Max(
                0.72F * dpi,
                1.95F * dpi * (0.54F + (0.46F * strength)) * (0.72F + (intensity * 0.28F)));
            core.Color = Color.FromArgb(
                (int)Math.Round(232F * segmentAlpha),
                Color.White);
            core.Width = Math.Max(0.52F * dpi, 0.92F * dpi * (0.56F + (0.44F * strength)));
            graphics.DrawLine(glow, history[index], history[index + 1]);
            graphics.DrawLine(trail, history[index], history[index + 1]);
            graphics.DrawLine(core, history[index], history[index + 1]);
        }

        var radius = 2.1F * dpi * (0.72F + (intensity * 0.28F));
        using var headGlow = new SolidBrush(Color.FromArgb(
            (int)Math.Round(156F * visibility * alphaScale),
            Blend(color, Color.White, 0.48F)));
        using var headCore = new SolidBrush(Color.FromArgb(
            (int)Math.Round(250F * visibility * alphaScale),
            Color.White));
        graphics.FillEllipse(
            headGlow,
            head.X - radius * 1.8F,
            head.Y - radius * 1.8F,
            radius * 3.6F,
            radius * 3.6F);
        graphics.FillEllipse(
            headCore,
            head.X - radius * 0.62F,
            head.Y - radius * 0.62F,
            radius * 1.24F,
            radius * 1.24F);
    }

    private void DrawCentralGlassPlanet(
        Graphics graphics,
        RectangleF bounds,
        Color accentColor,
        float dpi,
        bool forceDarkSurface)
    {
        if (!IsDrawableBounds(bounds) || !IsFinitePositive(dpi))
        {
            return;
        }

        var sphere = RectangleF.Inflate(bounds, -Scale(2, dpi), -Scale(2, dpi));
        if (!IsDrawableBounds(sphere))
        {
            return;
        }

        var darkSurface = forceDarkSurface || IsDarkVisualSurface();

        // Three concentric atmospheric blooms create depth while remaining static and cheap.
        foreach (var layer in new[]
                 {
                     (Inflate: 9.0F, DarkAlpha: 24, LightAlpha: 12),
                     (Inflate: 5.4F, DarkAlpha: 38, LightAlpha: 22),
                     (Inflate: 2.7F, DarkAlpha: 54, LightAlpha: 34)
                 })
        {
            var haloBounds = RectangleF.Inflate(sphere, layer.Inflate * dpi, layer.Inflate * dpi);
            using var haloBrush = new SolidBrush(Color.FromArgb(
                darkSurface ? layer.DarkAlpha : layer.LightAlpha,
                Blend(accentColor, SampleRingTone(accentColor, 0.61F), 0.34F)));
            graphics.FillEllipse(haloBrush, haloBounds);
        }

        using var spherePath = new GraphicsPath();
        spherePath.AddEllipse(sphere);
        using (var sphereBrush = new PathGradientBrush(spherePath)
        {
            CenterColor = darkSurface
                ? Blend(accentColor, Color.White, 0.20F)
                : Blend(accentColor, Color.White, 0.54F),
            SurroundColors = [
                darkSurface
                    ? Blend(accentColor, Color.FromArgb(3, 7, 29), 0.72F)
                    : Blend(accentColor, Color.White, 0.88F)],
            CenterPoint = new PointF(
                sphere.Left + sphere.Width * 0.30F,
                sphere.Top + sphere.Height * 0.24F),
            FocusScales = new PointF(0.56F, 0.52F)
        })
        {
            graphics.FillPath(sphereBrush, spherePath);
        }

        // Subtle gas belts are clipped inside the planet and make the centre feel volumetric,
        // not like a flat badge.  They do not animate, so only the lightweight comets repaint.
        var gasState = graphics.Save();
        graphics.SetClip(spherePath, CombineMode.Intersect);
        var upperBand = new RectangleF(
            sphere.Left - sphere.Width * 0.14F,
            sphere.Top + sphere.Height * 0.26F,
            sphere.Width * 1.28F,
            sphere.Height * 0.34F);
        var lowerBand = upperBand with { Y = sphere.Top + sphere.Height * 0.49F };
        using var gasPen = new Pen(
            Color.FromArgb(44, Blend(accentColor, Color.White, 0.48F)),
            Math.Max(2.0F * dpi, sphere.Height * 0.045F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawArc(gasPen, upperBand, 18F, 146F);
        gasPen.Color = Color.FromArgb(
            34,
            Blend(SampleRingTone(accentColor, 0.68F), Color.White, 0.30F));
        gasPen.Width = Math.Max(1.5F * dpi, sphere.Height * 0.032F);
        graphics.DrawArc(gasPen, lowerBand, 198F, 142F);
        using var terminator = new SolidBrush(
            darkSurface
                ? Color.FromArgb(56, 2, 5, 24)
                : Color.FromArgb(23, Blend(accentColor, Color.White, 0.42F)));
        graphics.FillEllipse(
            terminator,
            sphere.Left + sphere.Width * 0.55F,
            sphere.Top + sphere.Height * 0.12F,
            sphere.Width * 0.60F,
            sphere.Height * 0.88F);
        graphics.Restore(gasState);

        using var atmosphere = new Pen(
            Color.FromArgb(
                darkSurface ? 108 : 82,
                Blend(accentColor, Color.White, 0.62F)),
            Math.Max(3.0F * dpi, sphere.Width * 0.032F));
        graphics.DrawEllipse(atmosphere, sphere);
        using var rim = new Pen(
            Color.FromArgb(
                darkSurface ? 202 : 164,
                Blend(accentColor, Color.White, 0.68F)),
            Math.Max(0.72F * dpi, 1F));
        graphics.DrawEllipse(rim, sphere);

        var innerRimBounds = RectangleF.Inflate(sphere, -Scale(3, dpi), -Scale(3, dpi));
        if (IsDrawableBounds(innerRimBounds))
        {
            using var innerRim = new Pen(
                Color.FromArgb(
                    darkSurface ? 58 : 42,
                    SampleRingTone(accentColor, 0.42F)),
                Math.Max(0.62F * dpi, 1F));
            graphics.DrawEllipse(innerRim, innerRimBounds);
        }

        var sheen = new RectangleF(
            sphere.Left + sphere.Width * 0.19F,
            sphere.Top + sphere.Height * 0.13F,
            sphere.Width * 0.31F,
            sphere.Height * 0.16F);
        using var sheenGlow = new SolidBrush(Color.FromArgb(30, Color.White));
        using var sheenCore = new SolidBrush(Color.FromArgb(94, Color.White));
        graphics.FillEllipse(sheenGlow, RectangleF.Inflate(sheen, 2.2F * dpi, 2.2F * dpi));
        graphics.FillEllipse(sheenCore, sheen);

    }

    private static void DrawCentralPlanetBelt(
        Graphics graphics,
        RectangleF sphere,
        Color accentColor,
        float dpi,
        bool darkSurface,
        bool foreground)
    {
        if (!IsDrawableBounds(sphere) || !IsFinitePositive(dpi))
        {
            return;
        }

        var beltBounds = new RectangleF(
            sphere.Left - sphere.Width * 0.16F,
            sphere.Top + sphere.Height * 0.455F,
            sphere.Width * 1.32F,
            sphere.Height * 0.23F);
        if (!IsDrawableBounds(beltBounds))
        {
            return;
        }

        var state = graphics.Save();
        try
        {
            var center = new PointF(
                sphere.Left + sphere.Width / 2F,
                sphere.Top + sphere.Height / 2F);
            RotateAround(graphics, center, -4F);
            var start = foreground ? 0F : 180F;
            var firstColor = Blend(
                SampleRingTone(accentColor, foreground ? 0.18F : 0.62F),
                Color.White,
                foreground ? 0.28F : 0.18F);
            var secondColor = Blend(
                SampleRingTone(accentColor, foreground ? 0.76F : 0.34F),
                Color.White,
                foreground ? 0.20F : 0.14F);
            using var glow = new Pen(
                Color.FromArgb(darkSurface ? 70 : 38, firstColor),
                (darkSurface ? 4.2F : 3.2F) * dpi)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var core = new Pen(
                Color.FromArgb(darkSurface ? 232 : 194, firstColor),
                Math.Max(1.10F * dpi, 1F))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawArc(glow, beltBounds, start, 92F);
            graphics.DrawArc(core, beltBounds, start, 92F);
            glow.Color = Color.FromArgb(darkSurface ? 70 : 38, secondColor);
            core.Color = Color.FromArgb(darkSurface ? 232 : 194, secondColor);
            graphics.DrawArc(glow, beltBounds, start + 88F, 92F);
            graphics.DrawArc(core, beltBounds, start + 88F, 92F);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static Color SampleRingTone(Color baseColor, float progress)
    {
        var normalized = NormalizeProgress(progress);
        var radians = normalized * MathF.PI * 2F;

        // Keep the model identity stable: hue moves only +/-4 degrees while the visible
        // planet depth comes mainly from periodic lightness and saturation changes.
        var hueShift = -4F * MathF.Sin(radians);
        var lightnessShift = -0.055F +
            (0.18F * (0.5F - (0.5F * MathF.Cos(radians))));
        var saturationShift = 0.045F + (0.025F * MathF.Cos(radians * 2F));
        return ShiftHsl(baseColor, hueShift, saturationShift, lightnessShift);
    }

    private static Color ShiftHsl(
        Color color,
        float hueShift,
        float saturationShift,
        float lightnessShift)
    {
        var hue = NormalizeAngle(color.GetHue() + hueShift) / 360F;
        var saturation = Math.Clamp(color.GetSaturation() + saturationShift, 0F, 1F);
        var lightness = Math.Clamp(color.GetBrightness() + lightnessShift, 0F, 1F);
        if (saturation <= 0.0001F)
        {
            var gray = (int)Math.Round(lightness * 255F);
            return Color.FromArgb(color.A, gray, gray, gray);
        }

        var upper = lightness < 0.5F
            ? lightness * (1F + saturation)
            : lightness + saturation - (lightness * saturation);
        var lower = (2F * lightness) - upper;
        var red = HueToRgb(lower, upper, hue + (1F / 3F));
        var green = HueToRgb(lower, upper, hue);
        var blue = HueToRgb(lower, upper, hue - (1F / 3F));
        return Color.FromArgb(
            color.A,
            (int)Math.Round(red * 255F),
            (int)Math.Round(green * 255F),
            (int)Math.Round(blue * 255F));
    }

    private static float HueToRgb(float lower, float upper, float hue)
    {
        var normalized = hue;
        if (normalized < 0F)
        {
            normalized += 1F;
        }
        else if (normalized > 1F)
        {
            normalized -= 1F;
        }

        if (normalized < 1F / 6F)
        {
            return lower + ((upper - lower) * 6F * normalized);
        }
        if (normalized < 0.5F)
        {
            return upper;
        }
        if (normalized < 2F / 3F)
        {
            return lower + ((upper - lower) * ((2F / 3F) - normalized) * 6F);
        }
        return lower;
    }

    private static void DrawOrbitingHighlight(
        Graphics graphics,
        RectangleF bounds,
        float startAngle,
        float progress,
        float nodeSweep,
        Color color,
        float glowWidth,
        float coreWidth,
        int glowAlpha,
        int coreAlpha)
    {
        if (!IsDrawableBounds(bounds) ||
            !AreFinite(startAngle, progress, nodeSweep, glowWidth) ||
            !IsFinitePositive(coreWidth) ||
            nodeSweep <= 0F ||
            glowWidth <= 0F)
        {
            return;
        }

        // GDI+ repeatedly failed inside the arc-based comet at 200% DPI even after Pen
        // reuse.  A particle comet has the same single-head visual, crosses 0/360 degrees
        // naturally, and uses only two immutable brushes plus FillEllipse.
        var normalizedProgress = NormalizeProgress(progress);
        var tailProgressSpan = Math.Clamp(nodeSweep / 360F, 0F, 1F);
        var glowColor = Color.FromArgb(
            Math.Clamp(glowAlpha, 0, 255),
            Blend(color, Color.White, 0.26F));
        var ionColor = Color.FromArgb(
            Math.Clamp(coreAlpha, 0, 255),
            Blend(color, Color.White, 0.60F));
        using var glowBrush = new SolidBrush(glowColor);
        using var ionBrush = new SolidBrush(ionColor);
        using var coreBrush = new SolidBrush(
            Color.FromArgb(Math.Clamp(coreAlpha, 0, 255), Color.White));
        for (var index = 0; index < CometTailSegmentCount; index++)
        {
            var strength = MathF.Pow((index + 1F) / CometTailSegmentCount, 1.25F);
            var trailOffset = tailProgressSpan *
                (1F - ((index + 0.55F) / CometTailSegmentCount));
            var particleProgress = NormalizeProgress(normalizedProgress - trailOffset);
            var point = GetEllipsePoint(
                bounds,
                startAngle + (particleProgress * 360F));
            if (!AreFinite(point.X, point.Y))
            {
                continue;
            }

            // Shrinking geometry supplies the fade; brush colors stay immutable.
            var glowRadius = glowWidth * (0.12F + (strength * 0.42F));
            var coreRadius = coreWidth * (0.08F + (strength * 0.36F));
            graphics.FillEllipse(
                glowBrush,
                point.X - glowRadius,
                point.Y - glowRadius,
                glowRadius * 2F,
                glowRadius * 2F);
            graphics.FillEllipse(
                ionBrush,
                point.X - coreRadius,
                point.Y - coreRadius,
                coreRadius * 2F,
                coreRadius * 2F);
        }

        var head = GetEllipsePoint(
            bounds,
            startAngle + (normalizedProgress * 360F));
        if (!AreFinite(head.X, head.Y))
        {
            return;
        }
        var headGlowRadius = glowWidth * 0.78F;
        var headCoreRadius = coreWidth * 0.74F;
        graphics.FillEllipse(
            glowBrush,
            head.X - headGlowRadius,
            head.Y - headGlowRadius,
            headGlowRadius * 2F,
            headGlowRadius * 2F);
        graphics.FillEllipse(
            ionBrush,
            head.X - headCoreRadius,
            head.Y - headCoreRadius,
            headCoreRadius * 2F,
            headCoreRadius * 2F);
        var headHighlightRadius = headCoreRadius * 0.38F;
        graphics.FillEllipse(
            coreBrush,
            head.X - headHighlightRadius,
            head.Y - headHighlightRadius,
            headHighlightRadius * 2F,
            headHighlightRadius * 2F);
    }

    private static WrappedArcSegment[] CalculateWrappedArcSegments(
        float centerAngle,
        float nodeSweep)
    {
        if (!AreFinite(centerAngle, nodeSweep))
        {
            return [];
        }
        var sweep = Math.Clamp(nodeSweep, 0F, 360F);
        if (sweep <= 0F)
        {
            return [];
        }
        if (sweep >= 359.999F)
        {
            return [new WrappedArcSegment(0F, 360F, false, false)];
        }

        var center = NormalizeAngle(centerAngle);
        var halfSweep = sweep / 2F;
        var start = center - halfSweep;
        var end = center + halfSweep;
        if (start < 0F)
        {
            return
            [
                new WrappedArcSegment(start + 360F, -start, false, true),
                new WrappedArcSegment(0F, end, true, false)
            ];
        }
        if (end > 360F)
        {
            return
            [
                new WrappedArcSegment(start, 360F - start, false, true),
                new WrappedArcSegment(0F, end - 360F, true, false)
            ];
        }

        return [new WrappedArcSegment(start, sweep, false, false)];
    }

    private static float NormalizeProgress(float progress)
    {
        if (!float.IsFinite(progress))
        {
            return 0F;
        }
        var normalized = progress % 1F;
        return normalized < 0F ? normalized + 1F : normalized;
    }

    private Color GetTableItemColor(
        int itemIndex,
        ModelUsageDistributionItem item,
        IReadOnlyList<VisualUsageRing> rings)
    {
        var visualRing = rings.FirstOrDefault(ring => ring.ItemIndexes.Contains(itemIndex));
        return visualRing == null
            ? GetModelColor(item.Model)
            : GetVisualRingColor(visualRing);
    }

    private Color GetModelColor(string model)
    {
        var useLightPalette = !IsDarkVisualSurface();
        if (model.Contains("gpt-5.6-sol", StringComparison.OrdinalIgnoreCase))
        {
            return useLightPalette ? LightSolRingColor : SolRingColor;
        }
        if (model.Contains("gpt-5.6-terra", StringComparison.OrdinalIgnoreCase))
        {
            return useLightPalette ? LightTerraRingColor : TerraRingColor;
        }
        if (model.Contains("gpt-5.6-luna", StringComparison.OrdinalIgnoreCase))
        {
            return useLightPalette ? LightLunaRingColor : LunaRingColor;
        }
        if (model.Contains("gpt-5.5", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("chat-latest", StringComparison.OrdinalIgnoreCase))
        {
            return useLightPalette ? LightGpt55RingColor : Gpt55RingColor;
        }
        if (model.StartsWith("未识别", StringComparison.OrdinalIgnoreCase))
        {
            return _mutedColor;
        }

        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(model);
        var ratio = 0.25F + ((Math.Abs((long)hash) % 51L) / 100F);
        return useLightPalette
            ? Blend(LightSolRingColor, LightLunaRingColor, ratio)
            : Blend(SolRingColor, LunaRingColor, ratio);
    }

    private void UpdateAccessibility()
    {
        var totalTokens = _items.Sum(item => Math.Max(0L, item.TotalTokens));
        AccessibleName = $"{_rangeLabel}模型分布";
        AccessibleDescription = _items.Length == 0
            ? $"{_rangeLabel}暂无可归类的模型用量"
            : string.Join("；", _items.Select(item =>
                $"{item.Model}，占比 {FormatUsagePercent(item.TotalTokens, totalTokens)}，" +
                $"{item.Records} 条记录，{item.TotalTokens} Token，API 等值 {item.EquivalentCostUsd:0.####} 美元"));
    }

    private void SetColor(ref Color field, Color value)
    {
        if (field == value)
        {
            return;
        }
        field = value;
        InvalidateStaticDonutCache();
        UpdateAnimationState();
        Invalidate();
    }

    private static int CountPositiveTokenItems(IEnumerable<ModelUsageDistributionItem> items) =>
        items.Count(item => item.TotalTokens > 0);

    private static RectangleF CalculatePerspectiveOrbitBounds(
        PointF center,
        float radius,
        int ringIndex,
        int ringCount)
    {
        if (!AreFinite(center.X, center.Y, radius) || radius <= 0F)
        {
            return RectangleF.Empty;
        }

        ReadOnlySpan<float> inclinations = [0.68F, 0.82F, 0.70F, 0.88F, 0.76F];
        var verticalScale = ringCount <= 1
            ? 0.72F
            : inclinations[Math.Clamp(ringIndex, 0, inclinations.Length - 1)];
        return new RectangleF(
            center.X - radius,
            center.Y - (radius * verticalScale),
            radius * 2F,
            radius * verticalScale * 2F);
    }

    private static float GetPerspectiveOrbitRotation(int ringIndex, int ringCount)
    {
        ReadOnlySpan<float> rotations = [-14F, 9F, -7F, 15F, -3F];
        if (ringCount <= 1)
        {
            return -11F;
        }
        return rotations[Math.Clamp(ringIndex, 0, rotations.Length - 1)];
    }

    private static void RotateAround(Graphics graphics, PointF center, float degrees)
    {
        if (!AreFinite(center.X, center.Y, degrees) || Math.Abs(degrees) < 0.001F)
        {
            return;
        }
        // Matrix.RotateAt keeps the ellipse anchored to the planetary centre.  The older
        // append-order translate/rotate sequence displaced each orbit and made the system
        // look like several unrelated circles overlapping one another.
        using var rotation = new Matrix();
        rotation.RotateAt(degrees, center, MatrixOrder.Prepend);
        graphics.MultiplyTransform(rotation, MatrixOrder.Prepend);
    }

    private static RingGeometry CalculateRingGeometry(RectangleF bounds, float dpi, int ringCount)
    {
        if (!IsDrawableBounds(bounds) || !IsFinitePositive(dpi))
        {
            return new RingGeometry(PointF.Empty, RectangleF.Empty, 0F, 0F, []);
        }
        var safeCount = Math.Clamp(ringCount, 0, MaxNamedRings + 1);
        var padding = Scale(6, dpi);
        var maximumBreathScale = 1F + OrbitBreathAmplitude;
        var center = new PointF(bounds.Left + (bounds.Width / 2F), bounds.Top + (bounds.Height / 2F));
        // Radii below are the resting radii.  Reserve the largest breath phase up front so
        // animation grows into an already-owned envelope rather than being clipped by the
        // stage or changing the control's preferred size.
        const float maximumPerspectiveVerticalEnvelope = 0.90F;
        var maximumVisualRadius = Math.Max(
            1F,
            Math.Min(
                (bounds.Width / 2F) - padding,
                ((bounds.Height / 2F) - padding) / maximumPerspectiveVerticalEnvelope));
        var outerRadius = maximumVisualRadius / maximumBreathScale;
        var outerBounds = new RectangleF(
            center.X - maximumVisualRadius,
            center.Y - maximumVisualRadius * maximumPerspectiveVerticalEnvelope,
            maximumVisualRadius * 2F,
            maximumVisualRadius * maximumPerspectiveVerticalEnvelope * 2F);
        var fixedPlanetRadius = CalculateFixedPlanetRadius(bounds, dpi);
        var minimumCenterRadius = fixedPlanetRadius + Scale(16, dpi);
        if (safeCount == 0)
        {
            return new RingGeometry(
                center,
                outerBounds,
                fixedPlanetRadius,
                0F,
                []);
        }

        // Keep a real background gutter between neighbouring planets.  It remains visible
        // even with the hovered edge and the compact comet glow at fractional DPI values.
        var gap = Scale(
            safeCount >= 5 ? 8 :
            safeCount == 4 ? 10 :
            12,
            dpi);
        var minimumStroke = 5F * Math.Max(1F, dpi);
        var maximumStroke = Scale(18, dpi);
        var available = outerRadius - minimumCenterRadius - (gap * (safeCount - 1));
        var stroke = available / safeCount;
        if (stroke < minimumStroke)
        {
            // Keep the fixed central planet intact and reclaim only the decorative clearance.
            available = outerRadius - fixedPlanetRadius - Scale(8, dpi) -
                (gap * (safeCount - 1));
            stroke = available / safeCount;
        }
        stroke = Math.Clamp(stroke, Math.Min(minimumStroke, Math.Max(1F, stroke)), maximumStroke);

        var radii = new float[safeCount];
        for (var index = 0; index < safeCount; index++)
        {
            radii[index] = outerRadius - (stroke / 2F) - (index * (stroke + gap));
        }

        return new RingGeometry(center, outerBounds, fixedPlanetRadius, stroke, radii);
    }

    private static float CalculateFixedPlanetRadius(RectangleF bounds, float dpi)
    {
        if (!IsDrawableBounds(bounds) || !IsFinitePositive(dpi))
        {
            return 0F;
        }

        var stageDiameter = Math.Min(bounds.Width, bounds.Height);
        var target = FixedPlanetRadiusLogicalPixels * Math.Max(1F, dpi);
        var responsiveMaximum = stageDiameter * MaximumPlanetRadiusStageRatio;
        return Math.Max(
            Scale(34, dpi),
            Math.Min(target, responsiveMaximum));
    }

    private static float CalculateOrbitBreathScale(
        int ringIndex,
        int ringCount,
        float animationPhase)
    {
        if (ringCount <= 0)
        {
            return 1F;
        }

        var phase = NormalizeProgress(animationPhase) * MathF.PI * 2F;
        // A restrained offset keeps adjacent paths visibly asynchronous while preserving
        // their order and the clear lane between them throughout the animation cycle.
        var offset = ringIndex * 0.82F;
        return 1F + (OrbitBreathAmplitude * MathF.Sin(phase + offset));
    }

    private static float CalculatePlanetUsageStroke(
        float laneStroke,
        float dpi,
        int ringCount)
    {
        var scale = Math.Max(1F, dpi);
        var densityScale = ringCount switch
        {
            >= 5 => 0.55F,
            4 => 0.72F,
            3 => 0.86F,
            _ => 1F
        };
        return Math.Clamp(
            Math.Max(0F, laneStroke) * PlanetUsageStrokeRatio * densityScale,
            PlanetUsageMinimumDpiWidth * scale * densityScale,
            PlanetUsageMaximumDpiWidth * scale * densityScale);
    }

    private static float CalculateMaximumVisualHalfWidth(
        float stroke,
        float dpi,
        bool includeSatellite = false)
    {
        var safeStroke = Math.Max(0F, stroke);
        var scale = Math.Max(1F, dpi);

        // Every value below is a radial half-envelope of a layer painted around the same
        // centreline.  Keep this calculation beside the geometry so future visual effects
        // cannot silently grow across the reserved gutter.
        var hoveredUsageGlow = (safeStroke + Scale(2, scale)) / 2F;
        var neutralTrackHalo =
            (safeStroke + Math.Max(1F * scale, safeStroke * 0.12F)) / 2F;
        var usageRim =
            Math.Max(0.65F * scale, safeStroke * 0.09F) +
            (Math.Max(0.72F * scale, safeStroke * 0.055F) / 2F);
        var scannerRail =
            Math.Max(0.48F * scale, safeStroke * 0.075F) +
            Math.Max(1.60F * scale, safeStroke * 0.20F) +
            (Math.Max(2.80F * scale, safeStroke * 0.32F) / 2F);
        var cometGlow =
            Math.Max(2.4F * scale, safeStroke * 0.46F) * 1.28F / 2F;
        var cometCore =
            Math.Max(1.05F * scale, safeStroke * 0.13F) * 1.42F / 2F;
        var satelliteGlow = includeSatellite
            ? Math.Clamp(
                safeStroke * 0.48F,
                2.6F * scale,
                5.2F * scale) * 1.82F
            : 0F;

        return new[]
        {
            safeStroke / 2F,
            hoveredUsageGlow,
            neutralTrackHalo,
            usageRim,
            scannerRail,
            cometGlow,
            cometCore,
            satelliteGlow
        }.Max();
    }

    private static bool ShouldDrawOrbitSatellite(int ringIndex, int ringCount) =>
        ringCount is > 0 and <= 4 && ringIndex is >= 0 and < 2;

    private static RectangleF BoundsFromRadius(PointF center, float radius)
    {
        if (!AreFinite(center.X, center.Y, radius) || radius <= 0F)
        {
            return RectangleF.Empty;
        }
        return new RectangleF(
            center.X - radius,
            center.Y - radius,
            radius * 2F,
            radius * 2F);
    }

    private static PointF GetEllipsePoint(RectangleF bounds, float angle)
    {
        if (!IsDrawableBounds(bounds) || !float.IsFinite(angle))
        {
            return PointF.Empty;
        }
        var radians = angle * (MathF.PI / 180F);
        return new PointF(
            bounds.Left + (bounds.Width / 2F) + (MathF.Cos(radians) * bounds.Width / 2F),
            bounds.Top + (bounds.Height / 2F) + (MathF.Sin(radians) * bounds.Height / 2F));
    }

    private static LayoutMetrics CalculateLayout(RectangleF bounds, float dpi, int itemCount)
    {
        dpi = IsFinitePositive(dpi) ? Math.Max(1F, dpi) : 1F;
        var inset = Scale(20, dpi);
        var outer = RectangleF.Inflate(bounds, -Math.Max(1F, dpi), -Math.Max(1F, dpi));
        var content = RectangleF.Inflate(outer, -inset, -inset);
        var titleHeight = Scale(30, dpi);
        var subtitleHeight = Scale(24, dpi);
        var title = new RectangleF(content.Left, content.Top, content.Width, titleHeight);
        var subtitle = new RectangleF(content.Left, title.Bottom, content.Width, subtitleHeight);
        var bodyTop = subtitle.Bottom + Scale(8, dpi);
        var isWide = outer.Width >= Scale(WideLayoutThreshold, dpi);
        var rows = Math.Max(1, itemCount);
        var twoLineRows = !isWide && outer.Width < Scale(TwoLineTableThreshold, dpi);
        var tableHeader = Scale(38, dpi);
        var rowHeight = Scale(twoLineRows ? 62 : isWide ? 64 : 48, dpi);
        RectangleF donut;
        RectangleF table;
        if (isWide)
        {
            var gap = Scale(30, dpi);
            var desiredDonutWidth = Math.Min(content.Width * 0.40F, Scale(480, dpi));
            var minimumTableWidth = Scale(380, dpi);
            var maximumDonutWidth = Math.Max(Scale(220, dpi), content.Width - gap - minimumTableWidth);
            var donutWidth = Math.Min(desiredDonutWidth, maximumDonutWidth);
            donut = new RectangleF(
                content.Left,
                bodyTop,
                donutWidth,
                Math.Max(Scale(360, dpi), outer.Bottom - inset - bodyTop));
            table = new RectangleF(
                donut.Right + gap,
                bodyTop,
                Math.Max(1F, content.Right - donut.Right - gap),
                Math.Max(donut.Height, tableHeader + (rows * rowHeight)));
        }
        else
        {
            var donutHeight = Scale(260, dpi);
            donut = new RectangleF(content.Left, bodyTop, content.Width, donutHeight);
            var tableTop = donut.Bottom + Scale(18, dpi);
            table = new RectangleF(
                content.Left,
                tableTop,
                content.Width,
                tableHeader + (rows * rowHeight));
        }

        return new LayoutMetrics(
            outer,
            title,
            subtitle,
            donut,
            table,
            isWide,
            twoLineRows,
            tableHeader,
            rowHeight);
    }

    private static ColumnBounds GetColumns(RectangleF row, float dpi)
    {
        var leftInset = Scale(10, dpi);
        var rightInset = Scale(10, dpi);
        var usable = Math.Max(1F, row.Width - leftInset - rightInset);
        var modelWidth = usable * 0.43F;
        var recordsWidth = usable * 0.14F;
        var tokenWidth = usable * 0.21F;
        var costWidth = usable - modelWidth - recordsWidth - tokenWidth;
        var x = row.Left + leftInset;
        var model = new RectangleF(x, row.Top, modelWidth, row.Height);
        var records = new RectangleF(model.Right, row.Top, recordsWidth, row.Height);
        var tokens = new RectangleF(records.Right, row.Top, tokenWidth, row.Height);
        var cost = new RectangleF(tokens.Right, row.Top, costWidth, row.Height);
        return new ColumnBounds(model, records, tokens, cost);
    }

    private void DrawFittedCenteredText(
        Graphics graphics,
        string text,
        RectangleF bounds,
        Brush brush,
        float preferredPointSize,
        float minimumPointSize,
        FontStyle style)
    {
        if (string.IsNullOrWhiteSpace(text) || bounds.Width <= 1F || bounds.Height <= 1F)
        {
            return;
        }

        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.NoWrap
        };
        var low = Math.Max(3.8F, minimumPointSize);
        var high = Math.Max(low, preferredPointSize);
        var best = low;
        for (var iteration = 0; iteration < 7; iteration++)
        {
            var candidateSize = (low + high) / 2F;
            using var candidate = new Font(Font.FontFamily, candidateSize, style);
            var measured = graphics.MeasureString(
                text,
                candidate,
                new SizeF(10000F, 10000F),
                format);
            if (measured.Width <= bounds.Width - 1F && measured.Height <= bounds.Height - 1F)
            {
                best = candidateSize;
                low = candidateSize;
            }
            else
            {
                high = candidateSize;
            }
        }

        using var fitted = new Font(Font.FontFamily, best, style);
        graphics.DrawString(text, fitted, brush, bounds, format);
    }

    private static void DrawCellText(
        Graphics graphics,
        string text,
        Font font,
        Brush brush,
        RectangleF bounds,
        bool alignRight)
    {
        using var format = new StringFormat(StringFormat.GenericDefault)
        {
            Alignment = alignRight ? StringAlignment.Far : StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static StringFormat CenterFormat() => new(StringFormat.GenericDefault)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap
    };

    private static StringFormat CenterLeftFormat() => new(StringFormat.GenericDefault)
    {
        Alignment = StringAlignment.Near,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap
    };

    private static StringFormat CenterRightFormat() => new(StringFormat.GenericDefault)
    {
        Alignment = StringAlignment.Far,
        LineAlignment = StringAlignment.Center,
        Trimming = StringTrimming.EllipsisCharacter,
        FormatFlags = StringFormatFlags.NoWrap
    };

    private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
    {
        if (!IsDrawableBounds(bounds) || !float.IsFinite(radius))
        {
            return new GraphicsPath();
        }
        var safeRadius = Math.Max(0F, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2F));
        var diameter = safeRadius * 2F;
        var path = new GraphicsPath();
        if (diameter <= 0F)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180F, 90F);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270F, 90F);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0F, 90F);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90F, 90F);
        path.CloseFigure();
        return path;
    }

    private static Color Blend(Color first, Color second, float ratio)
    {
        var amount = Math.Clamp(ratio, 0F, 1F);
        return Color.FromArgb(
            (int)Math.Round(first.A + ((second.A - first.A) * amount)),
            (int)Math.Round(first.R + ((second.R - first.R) * amount)),
            (int)Math.Round(first.G + ((second.G - first.G) * amount)),
            (int)Math.Round(first.B + ((second.B - first.B) * amount)));
    }

    private bool IsDarkVisualSurface()
    {
        var surfaceLuminance = GetRelativeLuminance(_surfaceColor);
        var textLuminance = GetRelativeLuminance(_textColor);
        // Theme names are deliberately not consulted: custom and future palettes inherit
        // the correct treatment from the actual colours supplied to the control.
        return surfaceLuminance < 0.42F && textLuminance > surfaceLuminance + 0.18F;
    }

    private static float GetRelativeLuminance(Color color)
    {
        static float Linearize(byte channel)
        {
            var value = channel / 255F;
            return value <= 0.04045F
                ? value / 12.92F
                : MathF.Pow((value + 0.055F) / 1.055F, 2.4F);
        }

        return (0.2126F * Linearize(color.R)) +
               (0.7152F * Linearize(color.G)) +
               (0.0722F * Linearize(color.B));
    }

    private static int Scale(int value, float dpi)
    {
        var safeDpi = IsFinitePositive(dpi) ? Math.Max(1F, dpi) : 1F;
        return (int)Math.Ceiling(value * safeDpi);
    }

    private static bool IsDrawableBounds(RectangleF bounds) =>
        AreFinite(bounds.X, bounds.Y, bounds.Width, bounds.Height) &&
        bounds.Width > 0.5F &&
        bounds.Height > 0.5F &&
        float.IsFinite(bounds.Right) &&
        float.IsFinite(bounds.Bottom);

    private static bool IsCircularVisualVisible(
        Graphics graphics,
        PointF center,
        float radius)
    {
        if (!AreFinite(center.X, center.Y, radius) || radius <= 0F)
        {
            return false;
        }
        if (graphics.IsVisible(center))
        {
            return true;
        }

        // Partial scroll/exposure paints are not guaranteed to contain the centre point.
        // Sample both the atmospheric rim and inner globe so every visible slice redraws
        // the clipped planet instead of leaving a rectangular background scar.
        for (var ring = 0; ring < 2; ring++)
        {
            var sampleRadius = radius * (ring == 0 ? 0.96F : 0.54F);
            var sampleCount = ring == 0 ? 16 : 8;
            for (var index = 0; index < sampleCount; index++)
            {
                var radians = MathF.PI * 2F * index / sampleCount;
                var point = new PointF(
                    center.X + MathF.Cos(radians) * sampleRadius,
                    center.Y + MathF.Sin(radians) * sampleRadius);
                if (graphics.IsVisible(point))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsFinitePositive(float value) =>
        float.IsFinite(value) && value > 0F;

    private static bool AreFinite(float first, float second) =>
        float.IsFinite(first) && float.IsFinite(second);

    private static bool AreFinite(float first, float second, float third) =>
        AreFinite(first, second) && float.IsFinite(third);

    private static bool AreFinite(float first, float second, float third, float fourth) =>
        AreFinite(first, second, third) && float.IsFinite(fourth);

    private static bool Contains(RectangleF outer, RectangleF inner) =>
        inner.Left >= outer.Left - 0.5F &&
        inner.Top >= outer.Top - 0.5F &&
        inner.Right <= outer.Right + 0.5F &&
        inner.Bottom <= outer.Bottom + 0.5F;

    private static float NormalizeAngle(float angle)
    {
        if (!float.IsFinite(angle))
        {
            return 0F;
        }
        var normalized = angle % 360F;
        return normalized < 0F ? normalized + 360F : normalized;
    }

    private static float CalculateVisualRingSweep(long tokens) =>
        tokens > 0L ? RingSweepAngle : 0F;

    private static float GetSaturation(Color color)
    {
        var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
        var minimum = Math.Min(color.R, Math.Min(color.G, color.B));
        return maximum <= 0 ? 0F : (maximum - minimum) / (float)maximum;
    }

    private static double ColorDistance(Color first, Color second)
    {
        var red = first.R - second.R;
        var green = first.G - second.G;
        var blue = first.B - second.B;
        return Math.Sqrt((red * red) + (green * green) + (blue * blue));
    }

    private static float GetHueDistance(Color first, Color second)
    {
        var distance = Math.Abs(first.GetHue() - second.GetHue());
        return Math.Min(distance, 360F - distance);
    }

    private static string FormatModelWithUsagePercent(string model, long tokens, long totalTokens) =>
        $"{model} · {FormatUsagePercent(tokens, totalTokens)}";

    private static string FormatUsagePercent(long tokens, long totalTokens)
    {
        if (tokens <= 0L || totalTokens <= 0L)
        {
            return "0%";
        }

        var percent = Math.Clamp(tokens * 100D / totalTokens, 0D, 100D);
        if (percent < 0.01D)
        {
            return "<0.01%";
        }

        var format = percent >= 10D ? "0.#" : "0.##";
        return percent.ToString(format, CultureInfo.InvariantCulture) + "%";
    }

    private static string FormatTokens(long value)
    {
        var abs = Math.Abs(value);
        if (abs >= 1_000_000_000L)
        {
            return (value / 1_000_000_000D).ToString("0.##B", CultureInfo.InvariantCulture);
        }
        if (abs >= 1_000_000L)
        {
            return (value / 1_000_000D).ToString("0.##M", CultureInfo.InvariantCulture);
        }
        if (abs >= 10_000L)
        {
            return (value / 1_000D).ToString("0.#K", CultureInfo.InvariantCulture);
        }
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string FormatUsd(double value)
    {
        if (value <= 0D)
        {
            return "$0.00";
        }
        if (value < 0.01D)
        {
            return "<$0.01";
        }
        return "$" + value.ToString("#,0.00", CultureInfo.InvariantCulture);
    }

    private sealed record VisualUsageRing(
        string Model,
        int Records,
        long TotalTokens,
        double EquivalentCostUsd,
        int HoverIndex,
        int[] ItemIndexes);

    private readonly record struct IndexedUsageItem(
        int Index,
        ModelUsageDistributionItem Item);

    private readonly record struct RingHitTarget(
        int HoverIndex,
        PointF Center,
        RectangleF Bounds,
        float RotationDegrees,
        float StrokeWidth);

    private readonly record struct WrappedArcSegment(
        float Start,
        float Sweep,
        bool FlatStart,
        bool FlatEnd);

    private readonly record struct RingGeometry(
        PointF Center,
        RectangleF OuterBounds,
        float CenterRadius,
        float StrokeWidth,
        float[] Radii);

    private readonly record struct LayoutMetrics(
        RectangleF Outer,
        RectangleF Title,
        RectangleF Subtitle,
        RectangleF Donut,
        RectangleF Table,
        bool IsWide,
        bool TwoLineRows,
        float TableHeaderHeight,
        float RowHeight);

    private readonly record struct ColumnBounds(
        RectangleF Model,
        RectangleF Records,
        RectangleF Tokens,
        RectangleF Cost);
}
