using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodexAccountManager;

/// <summary>
/// Full-bleed cosmic starry-sky workspace canvas ("宇宙星空底板") that renders and caches the
/// active theme's continuous deep-space backdrop (multi-stop cosmic sky gradient, Milky Way
/// dust band, layered nebula clouds, silky aurora curtains, celestial orbital arcs, multi-layer
/// starfield with 4-point star glints &amp; constellations, and signature curved diagonal shooting
/// meteors) into a pre-multiplied 32bpp offscreen bitmap. Child containers and rounded controls
/// blit aligned slices from this single cached surface so painting and scrolling never recompute
/// gradients, nebulae, stars, or meteors per frame.
/// </summary>
internal sealed class WorkspaceBackdropPanel : Panel, IBackdropSurface
{
    private const int StarfieldSeed = 0x51C0A719;
    private const int MeteorTrailSegmentCount = 10;
    private Bitmap? _backdropCache;
    private Size _cacheSize;
    private int _cacheDpi;
    private ThemeMode _resolvedThemeMode = ThemeMode.Light;
    private int _cachePaletteHash;
    private ThemePalette? _palette;

    /// <summary>
    /// All themes now share a rich deep cosmic starry-sky canvas behind floating frosted cards.
    /// </summary>
    public static bool UsesDarkCosmicBackdrop(ThemeMode mode) => true;

    public WorkspaceBackdropPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        UpdateStyles();
    }

    public void SetTheme(ThemeMode resolvedThemeMode, ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var paletteHash = ComputePaletteHash(palette);
        var cosmicBase = ResolveBackdropSpec(resolvedThemeMode, palette).GradientStops[0];
        if (_resolvedThemeMode == resolvedThemeMode &&
            _cachePaletteHash == paletteHash &&
            _palette != null)
        {
            _palette = palette;
            BackColor = cosmicBase;
            return;
        }

        _resolvedThemeMode = resolvedThemeMode;
        _palette = palette;
        BackColor = cosmicBase;
        ReleaseBackdropCache();
        if (IsHandleCreated && !IsDisposed)
        {
            Invalidate(invalidateChildren: true);
        }
    }

    public void ReleaseBackdropCache()
    {
        _backdropCache?.Dispose();
        _backdropCache = null;
        _cacheSize = Size.Empty;
        _cacheDpi = 0;
        _cachePaletteHash = 0;
    }

    public bool TryPaintBackdropSlice(Graphics graphics, Control target, Rectangle targetClip)
    {
        if (IsDisposed || Disposing || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return false;
        }

        var cache = EnsureBackdropCache();
        if (cache == null)
        {
            return false;
        }

        var clip = targetClip;
        if (clip.Width <= 0 || clip.Height <= 0)
        {
            clip = target.ClientRectangle;
        }
        if (clip.Width <= 0 || clip.Height <= 0)
        {
            return true;
        }

        // During WinForms PaintTransparentBackground, Graphics is translated into parent
        // coordinates while e.ClipRectangle may still be in child coordinates. Intersecting
        // with graphics.ClipBounds recovers the exact dirty slice in target's coordinate space.
        var graphicsClip = graphics.ClipBounds;
        if (!graphicsClip.IsEmpty &&
            float.IsFinite(graphicsClip.X) &&
            float.IsFinite(graphicsClip.Y) &&
            float.IsFinite(graphicsClip.Width) &&
            float.IsFinite(graphicsClip.Height) &&
            graphicsClip.Width < 1_000_000F &&
            graphicsClip.Height < 1_000_000F)
        {
            var visibleClip = Rectangle.FromLTRB(
                (int)MathF.Floor(graphicsClip.Left) - 1,
                (int)MathF.Floor(graphicsClip.Top) - 1,
                (int)MathF.Ceiling(graphicsClip.Right) + 1,
                (int)MathF.Ceiling(graphicsClip.Bottom) + 1);
            clip = Rectangle.Intersect(clip, visibleClip);
            if (clip.Width <= 0 || clip.Height <= 0)
            {
                return true;
            }
        }

        if (!TryGetOffsetRelativeToSurface(target, out var offsetX, out var offsetY))
        {
            return false;
        }

        var srcX = offsetX + clip.X;
        var srcY = offsetY + clip.Y;
        var requestedSrc = new Rectangle(srcX, srcY, clip.Width, clip.Height);
        var cacheBounds = new Rectangle(0, 0, cache.Width, cache.Height);
        var clampedSrc = Rectangle.Intersect(requestedSrc, cacheBounds);

        if (clampedSrc != requestedSrc)
        {
            var fallbackColor = ResolveBackdropSpec(_resolvedThemeMode, _palette).GradientStops[0];
            using var fallbackBrush = new SolidBrush(Color.FromArgb(255, fallbackColor));
            graphics.FillRectangle(fallbackBrush, clip);
        }

        if (clampedSrc.Width > 0 && clampedSrc.Height > 0)
        {
            var dstRect = new Rectangle(
                clip.X + (clampedSrc.X - srcX),
                clip.Y + (clampedSrc.Y - srcY),
                clampedSrc.Width,
                clampedSrc.Height);
            var previousSmoothing = graphics.SmoothingMode;
            var previousPixelOffset = graphics.PixelOffsetMode;
            var previousInterpolation = graphics.InterpolationMode;
            var previousCompositing = graphics.CompositingMode;
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.CompositingMode = CompositingMode.SourceCopy;
            try
            {
                using var attrs = new ImageAttributes();
                attrs.SetWrapMode(WrapMode.TileFlipXY);
                graphics.DrawImage(
                    cache,
                    dstRect,
                    clampedSrc.X,
                    clampedSrc.Y,
                    clampedSrc.Width,
                    clampedSrc.Height,
                    GraphicsUnit.Pixel,
                    attrs);
            }
            finally
            {
                graphics.CompositingMode = previousCompositing;
                graphics.InterpolationMode = previousInterpolation;
                graphics.PixelOffsetMode = previousPixelOffset;
                graphics.SmoothingMode = previousSmoothing;
            }
        }

        return true;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_backdropCache != null && _cacheSize != ClientSize)
        {
            ReleaseBackdropCache();
            if (IsHandleCreated && !IsDisposed)
            {
                Invalidate(invalidateChildren: true);
            }
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ReleaseBackdropCache();
        if (IsHandleCreated && !IsDisposed)
        {
            Invalidate(invalidateChildren: true);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        var cache = EnsureBackdropCache();
        if (cache == null)
        {
            base.OnPaintBackground(e);
            return;
        }

        var clip = Rectangle.Intersect(e.ClipRectangle, new Rectangle(0, 0, cache.Width, cache.Height));
        if (clip.Width <= 0 || clip.Height <= 0)
        {
            return;
        }

        var previousSmoothing = e.Graphics.SmoothingMode;
        var previousPixelOffset = e.Graphics.PixelOffsetMode;
        var previousInterpolation = e.Graphics.InterpolationMode;
        var previousCompositing = e.Graphics.CompositingMode;
        e.Graphics.SmoothingMode = SmoothingMode.None;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.CompositingMode = CompositingMode.SourceCopy;
        try
        {
            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            e.Graphics.DrawImage(
                cache,
                clip,
                clip.X,
                clip.Y,
                clip.Width,
                clip.Height,
                GraphicsUnit.Pixel,
                attrs);
        }
        finally
        {
            e.Graphics.CompositingMode = previousCompositing;
            e.Graphics.InterpolationMode = previousInterpolation;
            e.Graphics.PixelOffsetMode = previousPixelOffset;
            e.Graphics.SmoothingMode = previousSmoothing;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleaseBackdropCache();
        }
        base.Dispose(disposing);
    }

    private bool TryGetOffsetRelativeToSurface(Control target, out int offsetX, out int offsetY)
    {
        offsetX = 0;
        offsetY = 0;
        if (ReferenceEquals(target, this))
        {
            return true;
        }

        var x = 0;
        var y = 0;
        for (Control? current = target; current != null; current = current.Parent)
        {
            if (ReferenceEquals(current, this))
            {
                offsetX = x;
                offsetY = y;
                return true;
            }
            x += current.Left;
            y += current.Top;
        }

        return false;
    }

    private Bitmap? EnsureBackdropCache()
    {
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }

        var palette = _palette;
        var paletteHash = palette == null ? BackColor.ToArgb() : ComputePaletteHash(palette);
        if (_backdropCache != null &&
            _cacheSize == size &&
            _cacheDpi == DeviceDpi &&
            _cachePaletteHash == paletteHash)
        {
            return _backdropCache;
        }

        ReleaseBackdropCache();
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(DeviceDpi > 0 ? DeviceDpi : 96F, DeviceDpi > 0 ? DeviceDpi : 96F);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            RenderBackdrop(graphics, new RectangleF(0F, 0F, size.Width, size.Height), _resolvedThemeMode, palette, Math.Max(1F, DeviceDpi / 96F));
        }

        _backdropCache = bitmap;
        _cacheSize = size;
        _cacheDpi = DeviceDpi;
        _cachePaletteHash = paletteHash;
        return bitmap;
    }

    private static int ComputePaletteHash(ThemePalette palette) =>
        HashCode.Combine(
            palette.FormBackColor.ToArgb(),
            palette.PrimaryColor.ToArgb(),
            palette.AccentColor.ToArgb(),
            palette.SecondaryAccentColor.ToArgb(),
            palette.TertiaryAccentColor.ToArgb(),
            palette.HeroStartColor.ToArgb(),
            palette.HeroEndColor.ToArgb());

    private static void RenderBackdrop(
        Graphics graphics,
        RectangleF bounds,
        ThemeMode themeMode,
        ThemePalette? palette,
        float dpiScale)
    {
        var spec = ResolveBackdropSpec(themeMode, palette);

        // 1. Full-bleed deep cosmic sky gradient spanning top header through bottom status bar.
        using (var baseGradient = new LinearGradientBrush(
                   new PointF(bounds.Left, bounds.Top),
                   new PointF(bounds.Right, bounds.Bottom),
                   spec.GradientStops[0],
                   spec.GradientStops[^1]))
        {
            baseGradient.InterpolationColors = new ColorBlend
            {
                Colors = spec.GradientStops,
                Positions = [0F, 0.34F, 0.70F, 1F]
            };
            graphics.FillRectangle(baseGradient, bounds);
        }

        // 2. Sweeping diagonal Milky Way luminous star-dust band flowing bottom-left -> top-right.
        DrawMilkyWayBand(graphics, bounds, spec);

        // 3. Rich layered cosmic nebula clouds distributed across upper header, margins, and lower sky.
        DrawRadialGlow(
            graphics,
            new RectangleF(
                bounds.Left + bounds.Width * 0.34F,
                bounds.Top - bounds.Height * 0.22F,
                bounds.Width * 0.68F,
                bounds.Height * 0.56F),
            Color.FromArgb(spec.TopRightBloomAlpha, spec.BloomPrimary));

        DrawRadialGlow(
            graphics,
            new RectangleF(
                bounds.Left + bounds.Width * 0.56F,
                bounds.Top + bounds.Height * 0.14F,
                bounds.Width * 0.62F,
                bounds.Height * 0.64F),
            Color.FromArgb(spec.MidRightBloomAlpha, spec.BloomSecondary));

        DrawRadialGlow(
            graphics,
            new RectangleF(
                bounds.Left - bounds.Width * 0.18F,
                bounds.Top + bounds.Height * 0.44F,
                bounds.Width * 0.64F,
                bounds.Height * 0.68F),
            Color.FromArgb(spec.BottomLeftBloomAlpha, spec.BloomTertiary));

        DrawRadialGlow(
            graphics,
            new RectangleF(
                bounds.Left + bounds.Width * 0.36F,
                bounds.Top + bounds.Height * 0.56F,
                bounds.Width * 0.78F,
                bounds.Height * 0.60F),
            Color.FromArgb(spec.BottomRightBloomAlpha, spec.BloomHighlight));

        // Concentrated jewel nebula pocket in the open upper-center-right header sky.
        DrawRadialGlow(
            graphics,
            new RectangleF(
                bounds.Left + bounds.Width * 0.28F,
                bounds.Top - bounds.Height * 0.08F,
                bounds.Width * 0.42F,
                bounds.Height * 0.26F),
            Color.FromArgb(spec.HeaderNebulaAlpha, spec.BloomSecondary));

        // 4. Silky translucent cosmic aurora curtains entering and exiting off-canvas.
        DrawAuroraRibbon(
            graphics,
            bounds,
            start: new PointF(-0.14F, 0.20F),
            c1: new PointF(0.24F, -0.02F),
            c2: new PointF(0.68F, 0.24F),
            end: new PointF(1.14F, 0.04F),
            color: spec.RibbonPrimary,
            alpha: 32,
            coreAlpha: 26,
            width: 62F * dpiScale);

        DrawAuroraRibbon(
            graphics,
            bounds,
            start: new PointF(-0.14F, 0.96F),
            c1: new PointF(0.28F, 0.70F),
            c2: new PointF(0.72F, 0.88F),
            end: new PointF(1.16F, 0.54F),
            color: spec.RibbonSecondary,
            alpha: 30,
            coreAlpha: 22,
            width: 56F * dpiScale);

        DrawAuroraRibbon(
            graphics,
            bounds,
            start: new PointF(-0.16F, 0.58F),
            c1: new PointF(0.22F, 0.40F),
            c2: new PointF(0.66F, 0.62F),
            end: new PointF(1.14F, 0.30F),
            color: spec.RibbonTertiary,
            alpha: 22,
            coreAlpha: 18,
            width: 44F * dpiScale);

        // 5. Subtle celestial orbital arcs & distant planet rim echoing the manager's cosmic identity.
        DrawOrbitalArc(
            graphics,
            bounds,
            centerX: 0.86F,
            centerY: 0.22F,
            radiusX: 0.27F,
            radiusY: 0.21F,
            rotationDegrees: -14F,
            startAngle: 142F,
            sweepAngle: 196F,
            color: spec.OrbitPrimary,
            alpha: 46,
            dpiScale: dpiScale);

        DrawOrbitalArc(
            graphics,
            bounds,
            centerX: 0.80F,
            centerY: 0.82F,
            radiusX: 0.33F,
            radiusY: 0.23F,
            rotationDegrees: 11F,
            startAngle: 184F,
            sweepAngle: 168F,
            color: spec.OrbitSecondary,
            alpha: 38,
            dpiScale: dpiScale);

        DrawDistantCosmicAura(graphics, bounds, spec, dpiScale);

        // 6. Rich multi-layer starfield: deep-field micro-dust, mid-field bright stars,
        //    delicate constellations, and jewel-like 4-point star glints.
        DrawStarfield(graphics, bounds, spec, dpiScale);
        DrawConstellations(graphics, bounds, spec, dpiScale);
        DrawHeroStarGlints(graphics, bounds, spec, dpiScale);

        // 7. Signature luminous curved diagonal shooting meteors across header, margins & lower sky.
        DrawCosmicMeteors(graphics, bounds, spec, dpiScale);

        // 8. Subtle vertical depth seam along the sidebar boundary for clean edge separation.
        using var seamBrush = new LinearGradientBrush(
            new RectangleF(bounds.Left, bounds.Top, Math.Max(4F, 12F * dpiScale), Math.Max(1F, bounds.Height)),
            Color.FromArgb(62, Color.Black),
            Color.FromArgb(0, Color.Black),
            LinearGradientMode.Horizontal);
        graphics.FillRectangle(seamBrush, bounds.Left, bounds.Top, Math.Max(4F, 12F * dpiScale), bounds.Height);
    }

    private static void DrawMilkyWayBand(Graphics graphics, RectangleF bounds, BackdropSpec spec)
    {
        var state = graphics.Save();
        try
        {
            var cx = bounds.Left + bounds.Width * 0.50F;
            var cy = bounds.Top + bounds.Height * 0.46F;
            graphics.TranslateTransform(cx, cy);
            graphics.RotateTransform(-26F);

            var bandWidth = MathF.Sqrt((bounds.Width * bounds.Width) + (bounds.Height * bounds.Height)) * 1.15F;
            var bandHeight = Math.Max(bounds.Width, bounds.Height) * 0.34F;

            DrawRadialGlow(
                graphics,
                new RectangleF(-bandWidth * 0.50F, -bandHeight * 0.52F, bandWidth, bandHeight),
                Color.FromArgb(44, spec.MilkyWayOuter));
            DrawRadialGlow(
                graphics,
                new RectangleF(-bandWidth * 0.44F, -bandHeight * 0.28F, bandWidth * 0.88F, bandHeight * 0.56F),
                Color.FromArgb(52, spec.MilkyWayCore));
            DrawRadialGlow(
                graphics,
                new RectangleF(-bandWidth * 0.22F, -bandHeight * 0.16F, bandWidth * 0.54F, bandHeight * 0.32F),
                Color.FromArgb(34, UiDesign.Blend(spec.MilkyWayCore, Color.White, 0.42F)));
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawDistantCosmicAura(
        Graphics graphics,
        RectangleF bounds,
        BackdropSpec spec,
        float dpiScale)
    {
        // Soft luminous planetary rim peeking subtly along the far lower-right horizon.
        var radius = Math.Min(bounds.Width, bounds.Height) * 0.24F;
        var cx = bounds.Right + radius * 0.18F;
        var cy = bounds.Bottom - radius * 0.12F;
        var sphere = new RectangleF(cx - radius, cy - radius, radius * 2F, radius * 2F);

        DrawRadialGlow(
            graphics,
            RectangleF.Inflate(sphere, radius * 0.42F, radius * 0.42F),
            Color.FromArgb(42, spec.BloomPrimary));

        using var rimGlow = new Pen(Color.FromArgb(32, spec.OrbitPrimary), 6F * dpiScale);
        using var rimCore = new Pen(
            Color.FromArgb(68, UiDesign.Blend(spec.OrbitPrimary, Color.White, 0.52F)),
            1.25F * dpiScale);
        graphics.DrawArc(rimGlow, sphere, 175F, 112F);
        graphics.DrawArc(rimCore, sphere, 178F, 106F);
    }

    private static void DrawRadialGlow(Graphics graphics, RectangleF bounds, Color centerColor)
    {
        if (bounds.Width <= 1F || bounds.Height <= 1F || centerColor.A == 0)
        {
            return;
        }

        using var path = new GraphicsPath();
        path.AddEllipse(bounds);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = centerColor,
            SurroundColors = [Color.FromArgb(0, centerColor)],
            FocusScales = new PointF(0.22F, 0.22F)
        };
        graphics.FillPath(brush, path);
    }

    private static void DrawAuroraRibbon(
        Graphics graphics,
        RectangleF bounds,
        PointF start,
        PointF c1,
        PointF c2,
        PointF end,
        Color color,
        int alpha,
        int coreAlpha,
        float width)
    {
        var p0 = new PointF(bounds.Left + bounds.Width * start.X, bounds.Top + bounds.Height * start.Y);
        var p1 = new PointF(bounds.Left + bounds.Width * c1.X, bounds.Top + bounds.Height * c1.Y);
        var p2 = new PointF(bounds.Left + bounds.Width * c2.X, bounds.Top + bounds.Height * c2.Y);
        var p3 = new PointF(bounds.Left + bounds.Width * end.X, bounds.Top + bounds.Height * end.Y);

        using var path = new GraphicsPath();
        path.AddBezier(p0, p1, p2, p3);

        using (var wideHalo = new Pen(Color.FromArgb(Math.Clamp(alpha / 2, 0, 255), color), width * 1.48F)
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round,
                   LineJoin = LineJoin.Round
               })
        {
            graphics.DrawPath(wideHalo, path);
        }

        using (var midGlow = new Pen(Color.FromArgb(Math.Clamp(alpha, 0, 255), color), width * 0.68F)
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round,
                   LineJoin = LineJoin.Round
               })
        {
            graphics.DrawPath(midGlow, path);
        }

        using var filament = new Pen(
            Color.FromArgb(Math.Clamp(coreAlpha, 0, 255), UiDesign.Blend(color, Color.White, 0.28F)),
            Math.Max(2.2F, width * 0.14F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawPath(filament, path);
    }

    private static void DrawOrbitalArc(
        Graphics graphics,
        RectangleF bounds,
        float centerX,
        float centerY,
        float radiusX,
        float radiusY,
        float rotationDegrees,
        float startAngle,
        float sweepAngle,
        Color color,
        int alpha,
        float dpiScale)
    {
        var cx = bounds.Left + bounds.Width * centerX;
        var cy = bounds.Top + bounds.Height * centerY;
        var rx = Math.Max(40F * dpiScale, bounds.Width * radiusX);
        var ry = Math.Max(30F * dpiScale, bounds.Height * radiusY);
        var ellipse = new RectangleF(-rx, -ry, rx * 2F, ry * 2F);

        var state = graphics.Save();
        try
        {
            graphics.TranslateTransform(cx, cy);
            graphics.RotateTransform(rotationDegrees);

            using (var glow = new Pen(Color.FromArgb(Math.Clamp(alpha / 2, 0, 255), color), 4.0F * dpiScale)
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round
                   })
            {
                graphics.DrawArc(glow, ellipse, startAngle, sweepAngle);
            }

            using (var stroke = new Pen(Color.FromArgb(Math.Clamp(alpha, 0, 255), color), 1.05F * dpiScale)
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round
                   })
            {
                graphics.DrawArc(stroke, ellipse, startAngle, sweepAngle);
            }

            // Two subtle glowing orbital moon nodes along the arc.
            foreach (var fraction in new[] { 0.34F, 0.82F })
            {
                var rad = (startAngle + (sweepAngle * fraction)) * MathF.PI / 180F;
                var px = MathF.Cos(rad) * rx;
                var py = MathF.Sin(rad) * ry;
                var nodeR = 1.65F * dpiScale;
                using var nodeHalo = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 16, 72), color));
                graphics.FillEllipse(nodeHalo, px - nodeR * 2.4F, py - nodeR * 2.4F, nodeR * 4.8F, nodeR * 4.8F);
                using var nodeCore = new SolidBrush(Color.FromArgb(228, UiDesign.Blend(color, Color.White, 0.62F)));
                graphics.FillEllipse(nodeCore, px - nodeR, py - nodeR, nodeR * 2F, nodeR * 2F);
            }
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private static void DrawStarfield(
        Graphics graphics,
        RectangleF bounds,
        BackdropSpec spec,
        float dpiScale)
    {
        var random = new Random(StarfieldSeed);

        // Layer A: 230 deep-field micro-dust stars with slightly higher density along the Milky Way diagonal.
        for (var i = 0; i < 230; i++)
        {
            float nx;
            float ny;
            if (i % 3 == 0)
            {
                // Cluster gently along the bottom-left -> top-right Milky Way diagonal.
                var t = (float)random.NextDouble();
                var spread = ((float)random.NextDouble() - 0.5F) * 0.34F;
                nx = Math.Clamp(t + spread * 0.55F, 0.005F, 0.995F);
                ny = Math.Clamp((0.92F - t * 0.88F) + spread, 0.005F, 0.995F);
            }
            else
            {
                nx = (float)random.NextDouble();
                ny = (float)random.NextDouble();
            }

            var radius = (0.48F + (float)(random.NextDouble() * 0.68D)) * dpiScale;
            var x = bounds.Left + nx * bounds.Width;
            var y = bounds.Top + ny * bounds.Height;
            var tint = (i % 8) switch
            {
                0 => spec.StarPrimary,
                1 => spec.StarSecondary,
                2 => spec.StarHighlight,
                3 => spec.StarWarm,
                _ => spec.StarNeutral
            };

            var framingZone = nx < 0.06F || nx > 0.93F || ny < 0.14F || ny > 0.90F;
            var baseAlpha = 68 + random.Next(0, 110);
            var alpha = Math.Clamp((int)MathF.Round(baseAlpha * (framingZone ? 1.24F : 0.96F)), 44, 240);
            using var brush = new SolidBrush(Color.FromArgb(alpha, tint));
            graphics.FillEllipse(brush, x - radius, y - radius, radius * 2F, radius * 2F);
        }

        // Layer B: 96 mid-field luminous stars with soft halos on the brightest third.
        for (var i = 0; i < 96; i++)
        {
            var nx = (float)random.NextDouble();
            var ny = (float)random.NextDouble();
            var radius = (0.98F + (float)(random.NextDouble() * 0.92D)) * dpiScale;
            var x = bounds.Left + nx * bounds.Width;
            var y = bounds.Top + ny * bounds.Height;

            var tint = (i % 7) switch
            {
                0 or 4 => spec.StarPrimary,
                1 => spec.StarSecondary,
                2 => spec.StarHighlight,
                3 => spec.StarWarm,
                _ => spec.StarNeutral
            };

            var framingZone = nx < 0.07F || nx > 0.90F || ny < 0.15F || ny > 0.88F;
            var alpha = Math.Clamp((int)MathF.Round((148 + random.Next(0, 94)) * (framingZone ? 1.15F : 0.95F)), 112, 250);

            if (i % 3 == 0 || radius > 1.45F * dpiScale)
            {
                var haloRadius = radius * 3.3F;
                DrawRadialGlow(
                    graphics,
                    new RectangleF(x - haloRadius, y - haloRadius, haloRadius * 2F, haloRadius * 2F),
                    Color.FromArgb(Math.Clamp(alpha / 2, 24, 108), tint));
            }

            using var starBrush = new SolidBrush(Color.FromArgb(alpha, UiDesign.Blend(tint, Color.White, 0.32F)));
            graphics.FillEllipse(starBrush, x - radius, y - radius, radius * 2F, radius * 2F);
        }
    }

    private static void DrawConstellations(
        Graphics graphics,
        RectangleF bounds,
        BackdropSpec spec,
        float dpiScale)
    {
        static PointF Pt(RectangleF b, float nx, float ny) =>
            new(b.Left + b.Width * nx, b.Top + b.Height * ny);

        // Upper-center-right constellation spanning the header sky and upper edge.
        PointF[] upperConstellation =
        [
            Pt(bounds, 0.37F, 0.056F),
            Pt(bounds, 0.44F, 0.024F),
            Pt(bounds, 0.51F, 0.062F),
            Pt(bounds, 0.59F, 0.032F),
            Pt(bounds, 0.66F, 0.078F)
        ];

        // Lower-right constellation peeking through lower gaps and status bar.
        PointF[] lowerConstellation =
        [
            Pt(bounds, 0.72F, 0.86F),
            Pt(bounds, 0.81F, 0.82F),
            Pt(bounds, 0.89F, 0.88F),
            Pt(bounds, 0.96F, 0.955F)
        ];

        using var linePen = new Pen(Color.FromArgb(46, spec.StarPrimary), Math.Max(0.85F, 0.8F * dpiScale));
        graphics.DrawLines(linePen, upperConstellation);
        graphics.DrawLines(linePen, lowerConstellation);

        foreach (var node in upperConstellation.Concat(lowerConstellation))
        {
            var r = 1.65F * dpiScale;
            DrawRadialGlow(
                graphics,
                new RectangleF(node.X - r * 3.2F, node.Y - r * 3.2F, r * 6.4F, r * 6.4F),
                Color.FromArgb(72, spec.StarPrimary));
            using var brush = new SolidBrush(Color.FromArgb(236, Color.White));
            graphics.FillEllipse(brush, node.X - r, node.Y - r, r * 2F, r * 2F);
        }
    }

    private static void DrawHeroStarGlints(
        Graphics graphics,
        RectangleF bounds,
        BackdropSpec spec,
        float dpiScale)
    {
        // Authored jewel-like 4-point star glints placed in the open header sky, breathing margins,
        // card-gap corridors, and bottom status bar so starlight unmistakably sparkles everywhere.
        ReadOnlySpan<(float X, float Y, float Size, byte Tone, float Intensity)> glints =
        [
            // Upper header sky band (visible around title, open center-right corridor & search rim)
            (0.030F, 0.026F, 4.2F, 0, 0.82F),
            (0.355F, 0.032F, 5.8F, 1, 0.96F),
            (0.475F, 0.068F, 7.2F, 0, 1.00F),
            (0.565F, 0.022F, 5.2F, 2, 0.92F),
            (0.642F, 0.072F, 4.8F, 3, 0.88F),
            (0.978F, 0.022F, 6.2F, 1, 0.96F),

            // Left vertical breathing margin (centered cleanly inside the 22px left margin)
            (0.012F, 0.240F, 3.8F, 1, 0.90F),
            (0.012F, 0.515F, 4.2F, 2, 0.92F),
            (0.012F, 0.785F, 3.6F, 0, 0.88F),

            // Right vertical breathing margin & outer cosmos
            (0.974F, 0.215F, 4.6F, 2, 0.94F),
            (0.978F, 0.455F, 4.2F, 3, 0.90F),
            (0.974F, 0.695F, 4.8F, 1, 0.95F),

            // Card-gap & translucent-glass shimmer accents
            (0.260F, 0.365F, 5.4F, 0, 0.86F),
            (0.710F, 0.545F, 6.0F, 2, 0.90F),

            // Lower sky & translucent bottom status bar band
            (0.185F, 0.968F, 5.2F, 1, 0.92F),
            (0.540F, 0.974F, 4.6F, 3, 0.88F),
            (0.845F, 0.966F, 6.0F, 0, 0.96F)
        ];

        foreach (var glint in glints)
        {
            var color = glint.Tone switch
            {
                1 => spec.StarPrimary,
                2 => spec.StarSecondary,
                3 => spec.StarHighlight,
                _ => spec.StarNeutral
            };
            DrawFourPointStarGlint(
                graphics,
                bounds.Left + bounds.Width * glint.X,
                bounds.Top + bounds.Height * glint.Y,
                glint.Size * dpiScale,
                color,
                glint.Intensity);
        }
    }

    private static void DrawFourPointStarGlint(
        Graphics graphics,
        float x,
        float y,
        float armLength,
        Color color,
        float intensity)
    {
        intensity = Math.Clamp(intensity, 0F, 1F);
        var haloRadius = armLength * 1.9F;
        DrawRadialGlow(
            graphics,
            new RectangleF(x - haloRadius, y - haloRadius, haloRadius * 2F, haloRadius * 2F),
            Color.FromArgb(ScaleAlpha(intensity, 92), color));

        // Crisp tapered 4-point star diamond (elegant astronomical diffraction glint).
        var halfWaist = Math.Max(0.75F, armLength * 0.145F);
        var brightColor = UiDesign.Blend(color, Color.White, 0.64F);
        using (var starPath = new GraphicsPath())
        {
            starPath.AddPolygon(
            [
                new PointF(x, y - armLength),
                new PointF(x + halfWaist, y - halfWaist),
                new PointF(x + armLength, y),
                new PointF(x + halfWaist, y + halfWaist),
                new PointF(x, y + armLength),
                new PointF(x - halfWaist, y + halfWaist),
                new PointF(x - armLength, y),
                new PointF(x - halfWaist, y - halfWaist)
            ]);
            using var starBrush = new SolidBrush(Color.FromArgb(ScaleAlpha(intensity, 212), brightColor));
            graphics.FillPath(starBrush, starPath);
        }

        var coreRadius = Math.Max(1.0F, armLength * 0.24F);
        using var coreBrush = new SolidBrush(Color.FromArgb(ScaleAlpha(intensity, 252), Color.White));
        graphics.FillEllipse(coreBrush, x - coreRadius, y - coreRadius, coreRadius * 2F, coreRadius * 2F);
    }

    private static void DrawCosmicMeteors(
        Graphics graphics,
        RectangleF bounds,
        BackdropSpec spec,
        float dpiScale)
    {
        // Six curved diagonal shooting meteors positioned so they read immediately in the
        // compact 68px header open sky, upper-right rim, breathing margins, card gaps, and lower cosmos.
        ReadOnlySpan<(
            float StartX,
            float StartY,
            float ControlX,
            float ControlY,
            float EndX,
            float EndY,
            float VisualScale,
            float Intensity,
            byte Tone)> meteors =
        [
            // 1. Hero upper-center header meteor (prominent right between title and search bar)
            (0.618F, 0.008F, 0.535F, 0.036F, 0.422F, 0.082F, 1.24F, 1.00F, 1),
            // 2. Companion upper-center header meteor (delicate violet-white streak)
            (0.495F, 0.012F, 0.435F, 0.038F, 0.358F, 0.072F, 0.90F, 0.82F, 2),
            // 3. Upper-right rim meteor entering from top-right sky into right breathing margin
            (0.996F, 0.010F, 0.932F, 0.064F, 0.835F, 0.142F, 1.08F, 0.94F, 3),
            // 4. Mid-right cosmic meteor crossing right margin & card gaps
            (0.992F, 0.285F, 0.902F, 0.372F, 0.768F, 0.492F, 1.10F, 0.90F, 0),
            // 5. Lower-left cosmic meteor sweeping out into left breathing margin & lower gaps
            (0.265F, 0.545F, 0.148F, 0.652F, 0.016F, 0.788F, 1.02F, 0.88F, 2),
            // 6. Lower-right deep-space meteor gliding into the translucent bottom status bar
            (0.915F, 0.778F, 0.798F, 0.868F, 0.642F, 0.974F, 1.12F, 0.94F, 1)
        ];

        foreach (var meteor in meteors)
        {
            var color = meteor.Tone switch
            {
                1 => spec.MeteorCyan,
                2 => spec.MeteorViolet,
                3 => spec.MeteorRose,
                _ => spec.StarNeutral
            };
            DrawCurvedMeteor(
                graphics,
                bounds,
                new PointF(meteor.StartX, meteor.StartY),
                new PointF(meteor.ControlX, meteor.ControlY),
                new PointF(meteor.EndX, meteor.EndY),
                color,
                dpiScale * meteor.VisualScale,
                meteor.Intensity);
        }
    }

    private static void DrawCurvedMeteor(
        Graphics graphics,
        RectangleF bounds,
        PointF startNorm,
        PointF controlNorm,
        PointF endNorm,
        Color color,
        float scale,
        float intensity)
    {
        Span<PointF> points = stackalloc PointF[MeteorTrailSegmentCount + 1];
        for (var i = 0; i <= MeteorTrailSegmentCount; i++)
        {
            var t = i / (float)MeteorTrailSegmentCount;
            var inv = 1F - t;
            var nx = (inv * inv * startNorm.X) + (2F * inv * t * controlNorm.X) + (t * t * endNorm.X);
            var ny = (inv * inv * startNorm.Y) + (2F * inv * t * controlNorm.Y) + (t * t * endNorm.Y);
            points[i] = new PointF(bounds.Left + bounds.Width * nx, bounds.Top + bounds.Height * ny);
        }

        var glowColor = UiDesign.Blend(color, Color.White, 0.22F);
        var ionColor = UiDesign.Blend(color, Color.White, 0.52F);
        var filamentColor = UiDesign.Blend(color, Color.White, 0.82F);

        using var glowPen = new Pen(Color.Transparent, 1F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var ionPen = new Pen(Color.Transparent, 1F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var filamentPen = new Pen(Color.Transparent, 1F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        for (var i = 0; i < MeteorTrailSegmentCount; i++)
        {
            var strength = MathF.Pow((i + 1F) / MeteorTrailSegmentCount, 1.38F);
            var segIntensity = intensity * (0.04F + (0.96F * strength));
            glowPen.Color = Color.FromArgb(ScaleAlpha(segIntensity, 74), glowColor);
            glowPen.Width = Math.Max(2.0F, 6.8F * scale * (0.28F + (0.72F * strength)));
            ionPen.Color = Color.FromArgb(ScaleAlpha(segIntensity, 188), ionColor);
            ionPen.Width = Math.Max(0.75F, 1.95F * scale * (0.50F + (0.50F * strength)));
            filamentPen.Color = Color.FromArgb(ScaleAlpha(segIntensity, 212), filamentColor);
            filamentPen.Width = Math.Max(0.48F, 0.78F * scale * (0.52F + (0.48F * strength)));

            graphics.DrawLine(glowPen, points[i], points[i + 1]);
            graphics.DrawLine(ionPen, points[i], points[i + 1]);
            graphics.DrawLine(filamentPen, points[i], points[i + 1]);
        }

        var prev = points[MeteorTrailSegmentCount - 1];
        var head = points[MeteorTrailSegmentCount];
        var dx = head.X - prev.X;
        var dy = head.Y - prev.Y;
        var len = Math.Max(1F, MathF.Sqrt((dx * dx) + (dy * dy)));
        var angleDeg = MathF.Atan2(dy / len, dx / len) * 180F / MathF.PI;

        var haloRadius = Math.Max(4.2F, 7.8F * scale);
        DrawRadialGlow(
            graphics,
            new RectangleF(head.X - haloRadius, head.Y - haloRadius, haloRadius * 2F, haloRadius * 2F),
            Color.FromArgb(ScaleAlpha(intensity, 118), color));

        var nucleusLen = Math.Max(3.6F, 6.4F * scale);
        var nucleusWidth = Math.Max(1.6F, 2.5F * scale);
        var state = graphics.Save();
        try
        {
            graphics.TranslateTransform(head.X, head.Y);
            graphics.RotateTransform(angleDeg);
            using var nucleus = new LinearGradientBrush(
                new PointF(-nucleusLen * 0.58F, 0F),
                new PointF(nucleusLen * 0.52F, 0F),
                Color.FromArgb(ScaleAlpha(intensity, 112), color),
                Color.FromArgb(ScaleAlpha(intensity, 250), UiDesign.Blend(color, Color.White, 0.84F)));
            graphics.FillEllipse(nucleus, -nucleusLen * 0.58F, -nucleusWidth * 0.5F, nucleusLen, nucleusWidth);
        }
        finally
        {
            graphics.Restore(state);
        }

        var headRadius = Math.Max(1.15F, 1.65F * scale);
        using var headBrush = new SolidBrush(Color.FromArgb(ScaleAlpha(intensity, 255), Color.White));
        graphics.FillEllipse(headBrush, head.X - headRadius, head.Y - headRadius, headRadius * 2F, headRadius * 2F);
    }

    private static int ScaleAlpha(float intensity, int alpha) =>
        Math.Clamp((int)MathF.Round(Math.Clamp(intensity, 0F, 1F) * alpha), 0, 255);

    private static BackdropSpec ResolveBackdropSpec(ThemeMode mode, ThemePalette? palette)
    {
        return mode switch
        {
            ThemeMode.Dark => new BackdropSpec(
                // 深海夜色 (Deep Sea Cosmic Night): abyssal ocean-indigo cosmos + cyan-sapphire aurora.
                GradientStops:
                [
                    Color.FromArgb(7, 17, 39),
                    Color.FromArgb(13, 33, 74),
                    Color.FromArgb(22, 42, 98),
                    Color.FromArgb(32, 39, 104)
                ],
                MilkyWayOuter: Color.FromArgb(38, 128, 224),
                MilkyWayCore: Color.FromArgb(62, 206, 246),
                BloomPrimary: Color.FromArgb(34, 211, 238),
                BloomSecondary: Color.FromArgb(124, 106, 248),
                BloomTertiary: Color.FromArgb(38, 134, 236),
                BloomHighlight: Color.FromArgb(56, 189, 248),
                TopRightBloomAlpha: 84,
                MidRightBloomAlpha: 82,
                BottomLeftBloomAlpha: 60,
                BottomRightBloomAlpha: 72,
                HeaderNebulaAlpha: 68,
                RibbonPrimary: Color.FromArgb(56, 216, 248),
                RibbonSecondary: Color.FromArgb(148, 122, 252),
                RibbonTertiary: Color.FromArgb(74, 158, 255),
                OrbitPrimary: Color.FromArgb(106, 214, 255),
                OrbitSecondary: Color.FromArgb(164, 140, 255),
                StarPrimary: Color.FromArgb(136, 226, 255),
                StarSecondary: Color.FromArgb(196, 172, 255),
                StarHighlight: Color.FromArgb(104, 244, 248),
                StarWarm: Color.FromArgb(255, 232, 194),
                StarNeutral: Color.FromArgb(245, 250, 255),
                MeteorCyan: Color.FromArgb(92, 232, 255),
                MeteorViolet: Color.FromArgb(174, 146, 255),
                MeteorRose: Color.FromArgb(124, 220, 255)),

            ThemeMode.NebulaDark => new BackdropSpec(
                // 星云夜色 (Nebula Cosmic Night): deep ultraviolet space + luminous orchid/magenta nebula clouds.
                GradientStops:
                [
                    Color.FromArgb(15, 9, 38),
                    Color.FromArgb(31, 19, 76),
                    Color.FromArgb(55, 27, 110),
                    Color.FromArgb(76, 30, 116)
                ],
                MilkyWayOuter: Color.FromArgb(146, 84, 238),
                MilkyWayCore: Color.FromArgb(236, 124, 206),
                BloomPrimary: Color.FromArgb(192, 132, 252),
                BloomSecondary: Color.FromArgb(244, 114, 182),
                BloomTertiary: Color.FromArgb(124, 92, 252),
                BloomHighlight: Color.FromArgb(56, 208, 238),
                TopRightBloomAlpha: 96,
                MidRightBloomAlpha: 86,
                BottomLeftBloomAlpha: 68,
                BottomRightBloomAlpha: 72,
                HeaderNebulaAlpha: 82,
                RibbonPrimary: Color.FromArgb(212, 148, 255),
                RibbonSecondary: Color.FromArgb(246, 128, 198),
                RibbonTertiary: Color.FromArgb(84, 210, 248),
                OrbitPrimary: Color.FromArgb(220, 172, 255),
                OrbitSecondary: Color.FromArgb(248, 150, 206),
                StarPrimary: Color.FromArgb(222, 188, 255),
                StarSecondary: Color.FromArgb(255, 174, 220),
                StarHighlight: Color.FromArgb(126, 238, 255),
                StarWarm: Color.FromArgb(255, 224, 196),
                StarNeutral: Color.FromArgb(253, 249, 255),
                MeteorCyan: Color.FromArgb(128, 234, 255),
                MeteorViolet: Color.FromArgb(218, 148, 255),
                MeteorRose: Color.FromArgb(255, 138, 208)),

            ThemeMode.PorcelainLight => new BackdropSpec(
                // 青瓷浅色 (Porcelain Cosmic Night): nocturnal starry celadon-jade cosmos behind frosted porcelain cards.
                GradientStops:
                [
                    Color.FromArgb(14, 38, 40),
                    Color.FromArgb(22, 58, 63),
                    Color.FromArgb(31, 75, 86),
                    Color.FromArgb(27, 57, 79)
                ],
                MilkyWayOuter: Color.FromArgb(62, 162, 152),
                MilkyWayCore: Color.FromArgb(116, 214, 196),
                BloomPrimary: Color.FromArgb(72, 196, 176),
                BloomSecondary: Color.FromArgb(98, 178, 212),
                BloomTertiary: Color.FromArgb(64, 156, 148),
                BloomHighlight: Color.FromArgb(226, 194, 122),
                TopRightBloomAlpha: 80,
                MidRightBloomAlpha: 72,
                BottomLeftBloomAlpha: 56,
                BottomRightBloomAlpha: 64,
                HeaderNebulaAlpha: 66,
                RibbonPrimary: Color.FromArgb(96, 216, 194),
                RibbonSecondary: Color.FromArgb(122, 192, 224),
                RibbonTertiary: Color.FromArgb(230, 198, 132),
                OrbitPrimary: Color.FromArgb(126, 226, 208),
                OrbitSecondary: Color.FromArgb(226, 196, 136),
                StarPrimary: Color.FromArgb(148, 242, 226),
                StarSecondary: Color.FromArgb(174, 222, 250),
                StarHighlight: Color.FromArgb(252, 226, 162),
                StarWarm: Color.FromArgb(255, 234, 184),
                StarNeutral: Color.FromArgb(246, 253, 251),
                MeteorCyan: Color.FromArgb(122, 242, 224),
                MeteorViolet: Color.FromArgb(156, 212, 255),
                MeteorRose: Color.FromArgb(250, 216, 152)),

            _ => new BackdropSpec(
                // 极光浅色 (Aurora Cosmic Night): beloved 2.4.3 deep cosmic indigo-violet starry sky
                // (#101738 -> #1D225C -> #342982 -> #233478) behind crisp frosted luminous-white cards!
                GradientStops:
                [
                    Color.FromArgb(16, 23, 56),
                    Color.FromArgb(29, 34, 92),
                    Color.FromArgb(52, 41, 130),
                    Color.FromArgb(35, 52, 120)
                ],
                MilkyWayOuter: Color.FromArgb(92, 118, 242),
                MilkyWayCore: Color.FromArgb(152, 132, 255),
                BloomPrimary: Color.FromArgb(92, 158, 255),
                BloomSecondary: Color.FromArgb(162, 114, 255),
                BloomTertiary: Color.FromArgb(72, 132, 246),
                BloomHighlight: Color.FromArgb(64, 214, 242),
                TopRightBloomAlpha: 92,
                MidRightBloomAlpha: 86,
                BottomLeftBloomAlpha: 64,
                BottomRightBloomAlpha: 76,
                HeaderNebulaAlpha: 82,
                RibbonPrimary: Color.FromArgb(98, 176, 255),
                RibbonSecondary: Color.FromArgb(186, 126, 255),
                RibbonTertiary: Color.FromArgb(72, 218, 244),
                OrbitPrimary: Color.FromArgb(132, 198, 255),
                OrbitSecondary: Color.FromArgb(196, 148, 255),
                StarPrimary: Color.FromArgb(138, 218, 255),
                StarSecondary: Color.FromArgb(212, 162, 255),
                StarHighlight: Color.FromArgb(118, 240, 252),
                StarWarm: Color.FromArgb(255, 228, 194),
                StarNeutral: Color.FromArgb(247, 250, 255),
                MeteorCyan: Color.FromArgb(112, 224, 255),
                MeteorViolet: Color.FromArgb(204, 148, 255),
                MeteorRose: Color.FromArgb(255, 148, 220))
        };
    }

    private sealed record BackdropSpec(
        Color[] GradientStops,
        Color MilkyWayOuter,
        Color MilkyWayCore,
        Color BloomPrimary,
        Color BloomSecondary,
        Color BloomTertiary,
        Color BloomHighlight,
        int TopRightBloomAlpha,
        int MidRightBloomAlpha,
        int BottomLeftBloomAlpha,
        int BottomRightBloomAlpha,
        int HeaderNebulaAlpha,
        Color RibbonPrimary,
        Color RibbonSecondary,
        Color RibbonTertiary,
        Color OrbitPrimary,
        Color OrbitSecondary,
        Color StarPrimary,
        Color StarSecondary,
        Color StarHighlight,
        Color StarWarm,
        Color StarNeutral,
        Color MeteorCyan,
        Color MeteorViolet,
        Color MeteorRose);
}

/// <summary>
/// Forwards WM_MOUSEWHEEL anywhere over the workspace list panel (including over child
/// cards, labels, pills, and buttons) smoothly to the target BufferedFlowLayoutPanel
/// without hijacking modal dialogs, ComboBox dropdowns, ListViews, or inner scrollable inputs.
/// </summary>
internal sealed class WorkspaceMouseWheelFilter : IMessageFilter
{
    private const int WmMouseWheel = 0x020A;
    private readonly Form _hostForm;
    private readonly BufferedFlowLayoutPanel _scrollTarget;

    public WorkspaceMouseWheelFilter(Form hostForm, BufferedFlowLayoutPanel scrollTarget)
    {
        _hostForm = hostForm;
        _scrollTarget = scrollTarget;
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmMouseWheel)
        {
            return false;
        }

        if (_hostForm.IsDisposed ||
            _hostForm.Disposing ||
            !_hostForm.Visible ||
            !_hostForm.IsHandleCreated ||
            _scrollTarget.IsDisposed ||
            !_scrollTarget.Visible ||
            !_scrollTarget.IsHandleCreated)
        {
            return false;
        }

        if (!ReferenceEquals(Form.ActiveForm, _hostForm) ||
            _hostForm.ActiveControl is ComboBox { DroppedDown: true })
        {
            return false;
        }

        // Extract screen coordinates from lParam (signed 16-bit X/Y).
        var lParam = m.LParam.ToInt64();
        var screenX = unchecked((short)(lParam & 0xFFFF));
        var screenY = unchecked((short)((lParam >> 16) & 0xFFFF));
        var screenPoint = new Point(screenX, screenY);

        var targetScreenBounds = _scrollTarget.RectangleToScreen(_scrollTarget.ClientRectangle);
        if (!targetScreenBounds.Contains(screenPoint))
        {
            return false;
        }

        var hwndUnderCursor = WindowFromPoint(new NativePoint(screenX, screenY));
        if (hwndUnderCursor == IntPtr.Zero)
        {
            return false;
        }

        // Native popup windows (such as ComboBox dropdown ComboLBox or ContextMenuStrip)
        // either have no managed Control or belong to a different top-level window.
        var hoveredControl = Control.FromHandle(hwndUnderCursor) ?? Control.FromChildHandle(hwndUnderCursor);
        if (hoveredControl == null || !ReferenceEquals(hoveredControl.FindForm(), _hostForm))
        {
            return false;
        }

        var wheelDelta = unchecked((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
        if (wheelDelta == 0)
        {
            return false;
        }

        // Walk up from the hovered child to _scrollTarget; yield to any inner scrollable control.
        var reachedTarget = false;
        for (Control? current = hoveredControl; current != null; current = current.Parent)
        {
            if (ReferenceEquals(current, _scrollTarget))
            {
                reachedTarget = true;
                break;
            }

            if (ShouldYieldToInnerControl(current, wheelDelta))
            {
                return false;
            }
        }

        if (!reachedTarget)
        {
            return false;
        }

        return _scrollTarget.ScrollByMouseWheelDelta(wheelDelta);
    }

    private static bool ShouldYieldToInnerControl(Control control, int wheelDelta)
    {
        switch (control)
        {
            case ComboBox:
            case ListView:
            case RichTextBox:
            case DataGridView:
            case NumericUpDown:
            case TreeView:
            case ListBox:
                return true;
            case TextBoxBase { Multiline: true } textBox when
                textBox is TextBox { ScrollBars: ScrollBars.Vertical or ScrollBars.Both }:
                return true;
            case ScrollableControl { AutoScroll: true } scrollable when
                scrollable is not RoundedPanel:
                var maxScroll = Math.Max(0, scrollable.DisplayRectangle.Height - scrollable.ClientSize.Height);
                if (maxScroll > 0)
                {
                    var currentOffset = Math.Max(0, -scrollable.AutoScrollPosition.Y);
                    var scrollingDown = wheelDelta < 0;
                    if ((scrollingDown && currentOffset < maxScroll) ||
                        (!scrollingDown && currentOffset > 0))
                    {
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
}
