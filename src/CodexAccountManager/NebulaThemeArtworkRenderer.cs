using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace CodexAccountManager;

/// <summary>
/// Produces the four bundled Account Manager wallpapers from the same celestial-system
/// visual language used by the quota dashboard. Each theme has its own planet material,
/// moon arrangement and atmosphere while retaining one shared animation coordinate system.
/// The renderer is deterministic so the artwork can be regenerated without a network API.
/// </summary>
internal static class NebulaThemeArtworkRenderer
{
    private const int CanvasWidth = 2560;
    private const int CanvasHeight = 1440;
    private const long JpegQuality = 94L;
    private const int StarfieldSeed = 0x41A70A11;

    private static readonly ArtworkPalette AuroraLight = new(
        Id: "manager-light",
        IsLight: true,
        Background: Color.FromArgb(251, 252, 255),
        Panel: Color.White,
        PanelAlt: Color.FromArgb(247, 249, 255),
        Accent: Color.FromArgb(104, 157, 238),
        AccentAlt: Color.FromArgb(155, 186, 244),
        Secondary: Color.FromArgb(169, 139, 232),
        Highlight: Color.FromArgb(105, 203, 207),
        Text: Color.FromArgb(23, 32, 51),
        BackdropStart: Color.FromArgb(253, 253, 254),
        BackdropMiddle: Color.FromArgb(249, 251, 255),
        BackdropEnd: Color.FromArgb(235, 232, 250),
        PlanetShadow: Color.FromArgb(73, 85, 161),
        PlanetMid: Color.FromArgb(137, 174, 235),
        PlanetLight: Color.FromArgb(249, 252, 255));

    private static readonly ArtworkPalette PorcelainLight = new(
        Id: "manager-porcelain-light",
        IsLight: true,
        Background: Color.FromArgb(244, 249, 247),
        Panel: Color.FromArgb(249, 252, 251),
        PanelAlt: Color.White,
        Accent: Color.FromArgb(67, 135, 124),
        AccentAlt: Color.FromArgb(124, 183, 167),
        Secondary: Color.FromArgb(105, 142, 153),
        Highlight: Color.FromArgb(199, 164, 99),
        Text: Color.FromArgb(24, 60, 57),
        BackdropStart: Color.FromArgb(247, 251, 249),
        BackdropMiddle: Color.FromArgb(228, 241, 236),
        BackdropEnd: Color.FromArgb(229, 232, 218),
        PlanetShadow: Color.FromArgb(41, 83, 77),
        PlanetMid: Color.FromArgb(100, 157, 143),
        PlanetLight: Color.FromArgb(221, 239, 232));

    private static readonly ArtworkPalette DeepSeaDark = new(
        Id: "manager-dark",
        IsLight: false,
        Background: Color.FromArgb(7, 16, 30),
        Panel: Color.FromArgb(9, 21, 38),
        PanelAlt: Color.FromArgb(18, 36, 59),
        Accent: Color.FromArgb(96, 165, 250),
        AccentAlt: Color.FromArgb(131, 188, 255),
        Secondary: Color.FromArgb(167, 139, 250),
        Highlight: Color.FromArgb(34, 211, 238),
        Text: Color.FromArgb(241, 246, 255),
        BackdropStart: Color.FromArgb(9, 24, 43),
        BackdropMiddle: Color.FromArgb(12, 49, 77),
        BackdropEnd: Color.FromArgb(34, 42, 91),
        PlanetShadow: Color.FromArgb(3, 24, 64),
        PlanetMid: Color.FromArgb(21, 82, 161),
        PlanetLight: Color.FromArgb(127, 220, 255));

    private static readonly ArtworkPalette NebulaDark = new(
        Id: "manager-nebula-dark",
        IsLight: false,
        Background: Color.FromArgb(11, 7, 22),
        Panel: Color.FromArgb(23, 18, 41),
        PanelAlt: Color.FromArgb(33, 24, 58),
        Accent: Color.FromArgb(180, 154, 255),
        AccentAlt: Color.FromArgb(192, 132, 252),
        Secondary: Color.FromArgb(244, 114, 182),
        Highlight: Color.FromArgb(34, 211, 238),
        Text: Color.FromArgb(252, 250, 255),
        // Deliberately brighter than the old near-black wallpaper. The exact manager colors
        // still drive Codex chrome, while the artwork gains enough mid-tone purple to remain
        // visible behind the native task surface.
        BackdropStart: Color.FromArgb(24, 15, 43),
        BackdropMiddle: Color.FromArgb(48, 24, 78),
        BackdropEnd: Color.FromArgb(78, 29, 101),
        PlanetShadow: Color.FromArgb(47, 20, 91),
        PlanetMid: Color.FromArgb(123, 48, 151),
        PlanetLight: Color.FromArgb(248, 132, 213));

    /// <summary>
    /// Renders an artwork variant inferred from its canonical output filename. Unknown names
    /// retain the historical behavior and render the nebula-dark variant.
    /// </summary>
    internal static string Render(string outputPath) =>
        Render(outputPath, InferThemeIdFromOutputPath(outputPath));

    internal static string Render(string outputPath, string themeId)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Account Manager artwork output path is required.", nameof(outputPath));
        }

        var fullPath = Path.GetFullPath(outputPath);
        var extension = Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension is not ".jpg" and not ".jpeg" and not ".png")
        {
            throw new ArgumentException("Account Manager artwork must be written as JPG or PNG.", nameof(outputPath));
        }

        var palette = ResolvePalette(themeId);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var bitmap = new Bitmap(CanvasWidth, CanvasHeight, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(96F, 96F);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            ConfigureGraphics(graphics);
            DrawBackdrop(graphics, palette);
            DrawStarfield(graphics, palette);
            DrawCelestialSystem(graphics, palette);
            DrawMeteorLanes(graphics, palette);
            DrawReadabilityVeil(graphics, palette);
            DrawVignette(graphics, palette);
        }

        if (extension == ".png")
        {
            bitmap.Save(fullPath, ImageFormat.Png);
        }
        else
        {
            SaveJpeg(bitmap, fullPath);
        }

        return fullPath;
    }

    private static string InferThemeIdFromOutputPath(string? outputPath)
    {
        var name = Path.GetFileNameWithoutExtension(outputPath ?? string.Empty);
        if (name.Contains("aurora-light", StringComparison.OrdinalIgnoreCase))
        {
            return AuroraLight.Id;
        }
        if (name.Contains("porcelain-light", StringComparison.OrdinalIgnoreCase))
        {
            return PorcelainLight.Id;
        }
        if (name.Contains("deep-sea", StringComparison.OrdinalIgnoreCase))
        {
            return DeepSeaDark.Id;
        }
        return NebulaDark.Id;
    }

    private static ArtworkPalette ResolvePalette(string? themeId) =>
        (themeId ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "manager-light" or "aurora-light" => AuroraLight,
            "manager-porcelain-light" or "porcelain-light" => PorcelainLight,
            "manager-dark" or "deep-sea" or "deep-sea-dark" => DeepSeaDark,
            "manager-nebula-dark" or "nebula" or "nebula-dark" => NebulaDark,
            _ => throw new ArgumentException($"Unsupported Account Manager artwork theme: {themeId}", nameof(themeId))
        };

    private static void ConfigureGraphics(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    private static void DrawBackdrop(Graphics graphics, ArtworkPalette palette)
    {
        var canvas = new Rectangle(0, 0, CanvasWidth, CanvasHeight);
        using (var background = new LinearGradientBrush(
                   canvas,
                   palette.BackdropStart,
                   palette.BackdropEnd,
                   0F))
        {
            background.InterpolationColors = new ColorBlend
            {
                Colors =
                [
                    palette.BackdropStart,
                    Blend(palette.BackdropStart, palette.Panel, palette.IsLight ? 0.22F : 0.20F),
                    palette.BackdropMiddle,
                    palette.BackdropEnd
                ],
                Positions = [0F, 0.46F, 0.76F, 1F]
            };
            graphics.FillRectangle(background, canvas);
        }

        if (palette.Id == AuroraLight.Id)
        {
            // The Aurora theme deliberately keeps the reading half neutral white. Colour is
            // confined to the planetary side and reads as reflected light, not a blue wash.
            DrawRadialGlow(graphics, new RectangleF(1500F, -270F, 1220F, 920F), Color.FromArgb(50, palette.Secondary));
            DrawRadialGlow(graphics, new RectangleF(1610F, 390F, 1150F, 970F), Color.FromArgb(42, palette.Accent));
            DrawRadialGlow(graphics, new RectangleF(1920F, 80F, 710F, 660F), Color.FromArgb(30, palette.Highlight));
        }
        else if (palette.Id == PorcelainLight.Id)
        {
            DrawRadialGlow(graphics, new RectangleF(1350F, -210F, 1320F, 1080F), Color.FromArgb(46, palette.AccentAlt));
            DrawRadialGlow(graphics, new RectangleF(1620F, 470F, 1170F, 980F), Color.FromArgb(34, palette.Highlight));
            DrawRadialGlow(graphics, new RectangleF(1950F, 80F, 650F, 650F), Color.FromArgb(30, palette.Accent));
        }
        else if (palette.Id == DeepSeaDark.Id)
        {
            DrawRadialGlow(graphics, new RectangleF(1130F, -340F, 1690F, 1250F), Color.FromArgb(92, palette.Secondary));
            DrawRadialGlow(graphics, new RectangleF(1490F, 300F, 1370F, 1180F), Color.FromArgb(104, palette.Accent));
            DrawRadialGlow(graphics, new RectangleF(1830F, 40F, 880F, 760F), Color.FromArgb(62, palette.Highlight));
        }
        else
        {
            DrawRadialGlow(graphics, new RectangleF(1100F, -360F, 1710F, 1300F), Color.FromArgb(112, palette.Secondary));
            DrawRadialGlow(graphics, new RectangleF(1380F, 330F, 1430F, 1180F), Color.FromArgb(92, palette.Accent));
            DrawRadialGlow(graphics, new RectangleF(1810F, 40F, 900F, 760F), Color.FromArgb(74, palette.Highlight));
        }

        // A restrained grid starts beyond the reading column. It gives the four wallpapers a
        // shared technical rhythm without tinting the large neutral surface at the left.
        var gridLeft = palette.IsLight ? 1160 : 0;
        var minorAlpha = palette.IsLight ? 7 : 11;
        var majorAlpha = palette.IsLight ? 12 : 17;
        using var minorGrid = new Pen(Color.FromArgb(minorAlpha, palette.Accent), 1F);
        using var majorGrid = new Pen(Color.FromArgb(majorAlpha, palette.AccentAlt), 1F);
        const int spacing = 80;
        for (var x = gridLeft; x <= CanvasWidth; x += spacing)
        {
            graphics.DrawLine(x % (spacing * 4) == 0 ? majorGrid : minorGrid, x, 0, x, CanvasHeight);
        }
        for (var y = 0; y <= CanvasHeight; y += spacing)
        {
            graphics.DrawLine(y % (spacing * 4) == 0 ? majorGrid : minorGrid, gridLeft, y, CanvasWidth, y);
        }

        // One broad atmospheric ribbon replaces the old heavy comet-like lower stroke.
        using var atmosphere = new GraphicsPath();
        atmosphere.AddBezier(1060F, 1195F, 1450F, 915F, 2030F, 1095F, 2670F, 760F);
        DrawGlowPath(
            graphics,
            atmosphere,
            Color.FromArgb(palette.IsLight ? 34 : 58, palette.Secondary),
            palette.IsLight ? 46F : 58F,
            palette.IsLight ? 2.2F : 2.8F);
    }

    private static void DrawStarfield(Graphics graphics, ArtworkPalette palette)
    {
        // Stars are deliberately circular or softly irregular. There are no plus signs,
        // crosses or linear flares anywhere in the bundled artwork.
        var random = new Random(StarfieldSeed);
        const int count = 178;
        for (var index = 0; index < count; index++)
        {
            var x = (float)(random.NextDouble() * CanvasWidth);
            var y = (float)(random.NextDouble() * CanvasHeight);
            var rightBias = x / CanvasWidth;
            if (rightBias < 0.45F && random.NextDouble() < 0.78D)
            {
                continue;
            }

            var radius = 0.55F + (float)(random.NextDouble() * 1.55D);
            var tint = (index % 7) switch
            {
                0 => palette.AccentAlt,
                1 => palette.Secondary,
                2 => palette.Highlight,
                _ => palette.IsLight ? Blend(palette.Text, palette.Accent, 0.28F) : Color.White
            };
            var alpha = palette.IsLight ? 28 + random.Next(0, 62) : 48 + random.Next(0, 102);
            using var star = new SolidBrush(Color.FromArgb(alpha, tint));
            graphics.FillEllipse(star, x - radius, y - radius, radius * 2F, radius * 2F);

            if (radius > 1.62F && index % 5 == 0)
            {
                DrawSoftParticle(graphics, new PointF(x, y), radius * 1.8F, tint, palette.IsLight);
            }
        }
    }

    private static void DrawCelestialSystem(Graphics graphics, ArtworkPalette palette)
    {
        var center = new PointF(CanvasWidth * 0.765F, CanvasHeight * 0.435F);

        // These four radii and rotations are shared with renderer-inject.js. Keep them exact so
        // the animated highlights continue to travel directly over the static tracks. The two
        // dominant tracks intentionally echo the thick blue/violet model rings used by the
        // Account Manager dashboard; the remaining two recede into supporting instrumentation.
        var orbitColors = ResolveGalaxyOrbitColors(palette);
        DrawOrbit(graphics, center, 572F, 332F, 7F, orbitColors[3], 0.86F, 4.6F, palette.IsLight, prominent: false);
        DrawOrbit(graphics, center, 398F, 256F, -34F, orbitColors[2], 0.68F, 5.4F, palette.IsLight, prominent: false);
        DrawOrbit(graphics, center, 456F, 302F, 17F, orbitColors[1], 0.42F, 23F, palette.IsLight, prominent: true);
        DrawOrbit(graphics, center, 520F, 218F, -12F, orbitColors[0], 0.12F, 27F, palette.IsLight, prominent: true);

        float planetRadius;
        float ringRadiusX;
        float ringRadiusY;
        float ringRotation;
        float[] moonPhases;
        float[] moonRadii;
        bool ringFirstMoon;
        switch (palette.Id)
        {
            case "manager-light":
                planetRadius = 248F;
                ringRadiusX = 374F;
                ringRadiusY = 103F;
                ringRotation = -11F;
                moonPhases = [202F, 318F, 118F, 31F];
                moonRadii = [23F, 12F, 8F, 6F];
                ringFirstMoon = false;
                break;
            case "manager-porcelain-light":
                planetRadius = 252F;
                ringRadiusX = 382F;
                ringRadiusY = 99F;
                ringRotation = -9F;
                moonPhases = [214F, 337F, 102F, 18F];
                moonRadii = [24F, 11F, 7F, 6F];
                ringFirstMoon = true;
                break;
            case "manager-dark":
                planetRadius = 264F;
                ringRadiusX = 394F;
                ringRadiusY = 112F;
                ringRotation = -13F;
                moonPhases = [196F, 321F, 126F, 29F];
                moonRadii = [25F, 14F, 9F, 7F];
                ringFirstMoon = true;
                break;
            default:
                planetRadius = 258F;
                ringRadiusX = 390F;
                ringRadiusY = 108F;
                ringRotation = -12F;
                moonPhases = [208F, 331F, 108F, 23F];
                moonRadii = [24F, 16F, 10F, 7F];
                ringFirstMoon = true;
                break;
        }

        var blueMoon = OrbitPoint(center, 520F, 218F, -12F, moonPhases[0]);
        var violetMoon = OrbitPoint(center, 456F, 302F, 17F, moonPhases[1]);
        var magentaMoon = OrbitPoint(center, 398F, 256F, -34F, moonPhases[2]);
        var highlightMoon = OrbitPoint(center, 572F, 332F, 7F, moonPhases[3]);
        DrawSmallPlanet(
            graphics,
            blueMoon,
            moonRadii[0],
            Blend(orbitColors[0], palette.Text, palette.IsLight ? 0.38F : 0.12F),
            Blend(orbitColors[0], Color.White, 0.42F),
            ringFirstMoon,
            palette.IsLight);
        DrawSmallPlanet(
            graphics,
            violetMoon,
            moonRadii[1],
            Blend(orbitColors[1], palette.Text, palette.IsLight ? 0.34F : 0.12F),
            Blend(orbitColors[1], Color.White, 0.36F),
            false,
            palette.IsLight);
        DrawSmallPlanet(
            graphics,
            magentaMoon,
            moonRadii[2],
            Blend(orbitColors[2], palette.Text, palette.IsLight ? 0.34F : 0.10F),
            Blend(orbitColors[2], Color.White, 0.34F),
            false,
            palette.IsLight);
        DrawSmallPlanet(
            graphics,
            highlightMoon,
            moonRadii[3],
            Blend(orbitColors[3], palette.Text, palette.IsLight ? 0.36F : 0.12F),
            Blend(orbitColors[3], Color.White, 0.36F),
            false,
            palette.IsLight);

        DrawOrbitHalf(graphics, center, ringRadiusX, ringRadiusY, ringRotation, false, palette);
        DrawGalaxyGlassPlanet(graphics, center, planetRadius, palette);
        DrawOrbitHalf(graphics, center, ringRadiusX, ringRadiusY, ringRotation, true, palette);

        // Sparse orbital particles make the static wallpaper feel alive without competing
        // with Codex text and composer controls.
        foreach (var particle in new[]
                 {
                     (Angle: 18F, Distance: 1.42F, Radius: 2.6F, Color: palette.AccentAlt),
                     (Angle: 132F, Distance: 1.48F, Radius: 2.2F, Color: palette.Highlight),
                     (Angle: 226F, Distance: 1.44F, Radius: 2.0F, Color: palette.Accent),
                     (Angle: 304F, Distance: 1.50F, Radius: 2.3F, Color: palette.Secondary)
                 })
        {
            var radians = particle.Angle * MathF.PI / 180F;
            var point = new PointF(
                center.X + MathF.Cos(radians) * planetRadius * particle.Distance,
                center.Y + MathF.Sin(radians) * planetRadius * particle.Distance);
            DrawGlowingDot(graphics, point, particle.Radius, particle.Color, palette.IsLight);
        }
    }

    private static void DrawGalaxyGlassPlanet(
        Graphics graphics,
        PointF center,
        float radius,
        ArtworkPalette palette)
    {
        var sphere = new RectangleF(center.X - radius, center.Y - radius, radius * 2F, radius * 2F);
        var lightSurface = palette.IsLight;
        var glassBlue = Color.FromArgb(66, 137, 255);
        var glassIndigo = Color.FromArgb(91, 78, 234);
        var glassViolet = Color.FromArgb(153, 78, 236);
        var glowColor = lightSurface ? glassBlue : glassViolet;

        // Three transparent blooms establish a soft atmosphere without the opaque disc that
        // made the previous wallpapers feel heavy. Their size mirrors the dashboard glass orb.
        foreach (var halo in new[]
                 {
                     (Inflate: 0.30F, Alpha: lightSurface ? 16 : 30),
                     (Inflate: 0.19F, Alpha: lightSurface ? 23 : 42),
                     (Inflate: 0.10F, Alpha: lightSurface ? 31 : 56)
                 })
        {
            DrawRadialGlow(
                graphics,
                RectangleF.Inflate(sphere, radius * halo.Inflate, radius * halo.Inflate),
                Color.FromArgb(halo.Alpha, glowColor));
        }

        using var spherePath = new GraphicsPath();
        spherePath.AddEllipse(sphere);

        var shellColors = lightSurface
            ? new[]
            {
                Color.FromArgb(206, 232, 244, 255),
                Color.FromArgb(202, 181, 213, 255),
                Color.FromArgb(210, 146, 174, 244),
                Color.FromArgb(220, 102, 119, 211)
            }
            : new[]
            {
                Color.FromArgb(232, 183, 137, 247),
                Color.FromArgb(234, 128, 83, 218),
                Color.FromArgb(238, 84, 45, 161),
                Color.FromArgb(242, 36, 18, 78)
            };
        using (var shell = new LinearGradientBrush(
                   new PointF(sphere.Left + radius * 0.30F, sphere.Top),
                   new PointF(sphere.Right - radius * 0.08F, sphere.Bottom),
                   shellColors[0],
                   shellColors[^1]))
        {
            shell.InterpolationColors = new ColorBlend
            {
                Colors = shellColors,
                Positions = [0F, 0.31F, 0.68F, 1F]
            };
            graphics.FillEllipse(shell, sphere);
        }

        // The interior is deliberately glassy and quiet: broad lens light plus one localised
        // lower glow. No horizontal cloud belts, facets, model labels or numerical text.
        var surfaceState = graphics.Save();
        graphics.SetClip(spherePath, CombineMode.Intersect);
        DrawRadialGlow(
            graphics,
            new RectangleF(
                sphere.Left + sphere.Width * 0.04F,
                sphere.Top + sphere.Height * 0.02F,
                sphere.Width * 0.72F,
                sphere.Height * 0.60F),
            Color.FromArgb(lightSurface ? 106 : 72, Color.White));
        DrawRadialGlow(
            graphics,
            new RectangleF(
                sphere.Left + sphere.Width * 0.30F,
                sphere.Top + sphere.Height * 0.48F,
                sphere.Width * 0.78F,
                sphere.Height * 0.58F),
            Color.FromArgb(lightSurface ? 38 : 66, lightSurface ? glassIndigo : glassViolet));

        var lensBounds = new RectangleF(
            sphere.Left + sphere.Width * 0.10F,
            sphere.Top + sphere.Height * 0.12F,
            sphere.Width * 0.74F,
            sphere.Height * 0.25F);
        using (var lens = new LinearGradientBrush(
                   lensBounds,
                   Color.FromArgb(lightSurface ? 64 : 42, Color.White),
                   Color.FromArgb(0, Color.White),
                   LinearGradientMode.Vertical))
        {
            graphics.FillEllipse(lens, lensBounds);
        }
        graphics.Restore(surfaceState);

        using (var edgeShade = new PathGradientBrush(spherePath)
               {
                   CenterPoint = new PointF(center.X - radius * 0.25F, center.Y - radius * 0.27F),
                   CenterColor = Color.FromArgb(0, Color.Black),
                   SurroundColors =
                   [
                       Color.FromArgb(
                           lightSurface ? 52 : 102,
                           lightSurface ? glassIndigo : Color.FromArgb(28, 11, 67))
                   ],
                   FocusScales = new PointF(0.73F, 0.69F)
               })
        {
            graphics.FillEllipse(edgeShade, sphere);
        }

        using (var glassWash = new LinearGradientBrush(
                   sphere,
                   Color.FromArgb(lightSurface ? 46 : 32, Color.White),
                   Color.FromArgb(0, Color.White),
                   LinearGradientMode.ForwardDiagonal))
        {
            graphics.FillEllipse(glassWash, sphere);
        }

        using (var reflectionGlow = new Pen(Color.FromArgb(lightSurface ? 42 : 34, Color.White), 28F)
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(reflectionGlow, sphere.Left + 29F, sphere.Top + 27F, sphere.Width * 0.70F, sphere.Height * 0.48F, 194F, 91F);
        }
        using (var reflection = new Pen(Color.FromArgb(lightSurface ? 182 : 158, Color.White), 8.5F)
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(reflection, sphere.Left + 30F, sphere.Top + 28F, sphere.Width * 0.69F, sphere.Height * 0.47F, 197F, 83F);
        }

        using var atmosphereRim = new Pen(Color.FromArgb(lightSurface ? 48 : 74, glowColor), 17F);
        using var outerRim = new Pen(
            Color.FromArgb(lightSurface ? 210 : 226, Blend(glowColor, Color.White, lightSurface ? 0.48F : 0.36F)),
            4.2F);
        using var innerRim = new Pen(Color.FromArgb(lightSurface ? 118 : 104, Color.White), 1.45F);
        graphics.DrawEllipse(atmosphereRim, RectangleF.Inflate(sphere, 3F, 3F));
        graphics.DrawEllipse(outerRim, sphere);
        graphics.DrawEllipse(innerRim, RectangleF.Inflate(sphere, -7.5F, -7.5F));
    }

    private static void DrawAtmosphericCloudTexture(
        Graphics graphics,
        RectangleF sphere,
        ArtworkPalette palette)
    {
        var seed = palette.Id switch
        {
            "manager-light" => 0x1A7701,
            "manager-porcelain-light" => 0x1A7702,
            "manager-dark" => 0x1A7703,
            _ => 0x1A7704
        };
        var random = new Random(seed);
        var count = palette.Id == NebulaDark.Id ? 24 : palette.Id == DeepSeaDark.Id ? 21 : 16;
        for (var index = 0; index < count; index++)
        {
            var width = sphere.Width * (0.15F + (float)random.NextDouble() * 0.31F);
            var height = sphere.Height * (0.035F + (float)random.NextDouble() * 0.085F);
            var x = sphere.Left + sphere.Width * (0.04F + (float)random.NextDouble() * 0.84F) - width * 0.5F;
            var y = sphere.Top + sphere.Height * (0.09F + (float)random.NextDouble() * 0.80F) - height * 0.5F;
            var color = ((index + seed) % 4) switch
            {
                0 => palette.PlanetLight,
                1 => palette.AccentAlt,
                2 => palette.Secondary,
                _ => palette.IsLight ? Color.White : palette.Highlight
            };
            var alpha = palette.IsLight
                ? 12 + random.Next(0, 18)
                : 14 + random.Next(0, 24);
            DrawRadialGlow(graphics, new RectangleF(x, y, width, height), Color.FromArgb(alpha, color));
        }

        // Fine, curved atmospheric contours break up the old flat-gradient-ball silhouette.
        // They stay inside the existing sphere clip and are intentionally irregular in length.
        var contourColor = palette.Id switch
        {
            "manager-porcelain-light" => Blend(palette.Accent, palette.Highlight, 0.28F),
            "manager-dark" => Blend(palette.AccentAlt, palette.Highlight, 0.38F),
            "manager-nebula-dark" => Blend(palette.Secondary, palette.AccentAlt, 0.42F),
            _ => Blend(palette.Accent, palette.Secondary, 0.32F)
        };
        using var contour = new Pen(Color.FromArgb(palette.IsLight ? 30 : 38, contourColor), 1.15F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        for (var index = 0; index < 7; index++)
        {
            var y = sphere.Top + sphere.Height * (0.17F + (index * 0.105F));
            var inset = sphere.Width * (0.055F + ((index % 3) * 0.025F));
            var height = sphere.Height * (0.13F + ((index % 2) * 0.025F));
            graphics.DrawArc(
                contour,
                sphere.Left + inset,
                y - height * 0.5F,
                sphere.Width - inset * 2F,
                height,
                index % 2 == 0 ? 186F : 8F,
                index % 2 == 0 ? 152F : 146F);
        }
    }

    private static void DrawPlanetRibbon(
        Graphics graphics,
        RectangleF sphere,
        float verticalPosition,
        float relativeThickness,
        float relativeWave,
        Color leftColor,
        Color rightColor)
    {
        var left = sphere.Left - 22F;
        var right = sphere.Right + 22F;
        var width = right - left;
        var y = sphere.Top + (sphere.Height * verticalPosition);
        var thickness = sphere.Height * relativeThickness;
        var wave = sphere.Height * relativeWave;

        using var ribbon = new GraphicsPath();
        ribbon.AddBezier(
            left,
            y,
            left + width * 0.24F,
            y - wave,
            left + width * 0.67F,
            y + wave,
            right,
            y - wave * 0.18F);
        ribbon.AddBezier(
            right,
            y + thickness,
            left + width * 0.72F,
            y + thickness + wave * 0.72F,
            left + width * 0.28F,
            y + thickness - wave * 0.64F,
            left,
            y + thickness);
        ribbon.CloseFigure();

        using (var fill = new LinearGradientBrush(
                   new PointF(left, y),
                   new PointF(right, y + thickness),
                   leftColor,
                   rightColor))
        {
            graphics.FillPath(fill, ribbon);
        }

        using var crest = new Pen(Color.FromArgb(Math.Min(72, leftColor.A + 12), Blend(leftColor, Color.White, 0.55F)), 1.2F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawBezier(
            crest,
            left,
            y,
            left + width * 0.24F,
            y - wave,
            left + width * 0.67F,
            y + wave,
            right,
            y - wave * 0.18F);
    }

    private static void DrawPearlCaustics(Graphics graphics, RectangleF sphere, ArtworkPalette palette)
    {
        using var cool = new Pen(Color.FromArgb(38, palette.Accent), 9F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var warm = new Pen(Color.FromArgb(28, palette.Secondary), 6F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawArc(cool, sphere.Left + 46F, sphere.Top + 94F, sphere.Width - 92F, sphere.Height * 0.38F, 188F, 150F);
        graphics.DrawArc(warm, sphere.Left + 68F, sphere.Top + 156F, sphere.Width - 126F, sphere.Height * 0.30F, 12F, 150F);
    }

    private static void DrawJadeVeins(Graphics graphics, RectangleF sphere, ArtworkPalette palette)
    {
        using var jadeVein = new Pen(Color.FromArgb(58, Blend(palette.PlanetShadow, palette.Accent, 0.42F)), 2.1F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var giltVein = new Pen(Color.FromArgb(72, palette.Highlight), 1.25F)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawBezier(jadeVein, sphere.Left + 30F, sphere.Top + 178F, sphere.Left + 128F, sphere.Top + 118F, sphere.Right - 128F, sphere.Top + 240F, sphere.Right - 24F, sphere.Top + 168F);
        graphics.DrawBezier(giltVein, sphere.Left + 54F, sphere.Top + 274F, sphere.Left + 160F, sphere.Top + 214F, sphere.Right - 108F, sphere.Top + 316F, sphere.Right - 36F, sphere.Top + 260F);
    }

    private static void DrawDeepSeaStorm(Graphics graphics, RectangleF sphere, ArtworkPalette palette)
    {
        var storm = new RectangleF(sphere.Left + sphere.Width * 0.53F, sphere.Top + sphere.Height * 0.49F, sphere.Width * 0.29F, sphere.Height * 0.115F);
        using var stormPath = new GraphicsPath();
        stormPath.AddEllipse(storm);
        using (var stormFill = new PathGradientBrush(stormPath)
               {
                   CenterPoint = new PointF(storm.Left + storm.Width * 0.36F, storm.Top + storm.Height * 0.42F),
                   CenterColor = Color.FromArgb(148, Blend(palette.Highlight, Color.White, 0.46F)),
                   SurroundColors = [Color.FromArgb(18, palette.PlanetShadow)]
               })
        {
            graphics.FillEllipse(stormFill, storm);
        }
        using var spiral = new Pen(Color.FromArgb(112, palette.PlanetLight), 2F);
        graphics.DrawArc(spiral, RectangleF.Inflate(storm, -9F, -7F), 18F, 292F);
    }

    private static void DrawNebulaGemFacets(Graphics graphics, RectangleF sphere, ArtworkPalette palette)
    {
        using var upperFacet = new SolidBrush(Color.FromArgb(24, Color.White));
        using var violetFacet = new SolidBrush(Color.FromArgb(30, palette.AccentAlt));
        using var roseFacet = new SolidBrush(Color.FromArgb(30, palette.Secondary));
        graphics.FillPolygon(
            upperFacet,
            [
                new PointF(sphere.Left + 62F, sphere.Top + 36F),
                new PointF(sphere.Right - 98F, sphere.Top + 70F),
                new PointF(sphere.Left + sphere.Width * 0.57F, sphere.Top + sphere.Height * 0.48F),
                new PointF(sphere.Left + 36F, sphere.Top + sphere.Height * 0.36F)
            ]);
        graphics.FillPolygon(
            violetFacet,
            [
                new PointF(sphere.Left + sphere.Width * 0.57F, sphere.Top + sphere.Height * 0.48F),
                new PointF(sphere.Right - 42F, sphere.Top + 112F),
                new PointF(sphere.Right - 68F, sphere.Bottom - 74F),
                new PointF(sphere.Left + sphere.Width * 0.48F, sphere.Bottom - 28F)
            ]);
        graphics.FillPolygon(
            roseFacet,
            [
                new PointF(sphere.Left + 32F, sphere.Top + sphere.Height * 0.38F),
                new PointF(sphere.Left + sphere.Width * 0.57F, sphere.Top + sphere.Height * 0.48F),
                new PointF(sphere.Left + sphere.Width * 0.48F, sphere.Bottom - 28F),
                new PointF(sphere.Left + 72F, sphere.Bottom - 82F)
            ]);
        DrawRadialGlow(
            graphics,
            new RectangleF(sphere.Left + sphere.Width * 0.33F, sphere.Top + sphere.Height * 0.30F, sphere.Width * 0.46F, sphere.Height * 0.46F),
            Color.FromArgb(54, palette.PlanetLight));
    }

    private static void DrawOrbit(
        Graphics graphics,
        PointF center,
        float radiusX,
        float radiusY,
        float rotation,
        Color color,
        float phase,
        float width,
        bool isLight,
        bool prominent)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(center.X, center.Y);
        graphics.RotateTransform(rotation);
        var bounds = new RectangleF(-radiusX, -radiusY, radiusX * 2F, radiusY * 2F);

        if (prominent)
        {
            using (var outerBloom = new Pen(Color.FromArgb(isLight ? 18 : 32, color), width * 2.35F))
            {
                graphics.DrawEllipse(outerBloom, bounds);
            }
            using (var innerBloom = new Pen(Color.FromArgb(isLight ? 36 : 50, color), width * 1.52F))
            {
                graphics.DrawEllipse(innerBloom, bounds);
            }
            using (var shadow = new Pen(
                       Color.FromArgb(isLight ? 190 : 222, Blend(color, Color.FromArgb(18, 18, 78), isLight ? 0.20F : 0.28F)),
                       width + 3.6F))
            {
                graphics.DrawEllipse(shadow, bounds);
            }

            // Sample the full ellipse in short overlapping arcs. This reproduces the polished
            // tonal tubes from the model-distribution card without baking any data into them.
            const int segmentCount = 48;
            const float segmentSweep = 360F / segmentCount;
            using var body = new Pen(color, width)
            {
                StartCap = LineCap.Flat,
                EndCap = LineCap.Flat
            };
            for (var segment = 0; segment < segmentCount; segment++)
            {
                var progress = (segment + 0.5F) / segmentCount;
                var wave = (MathF.Sin((progress + phase) * MathF.Tau) + 1F) * 0.5F;
                var tone = wave >= 0.5F
                    ? Blend(color, Color.White, (wave - 0.5F) * (isLight ? 0.34F : 0.28F))
                    : Blend(color, Color.FromArgb(24, 24, 104), (0.5F - wave) * 0.34F);
                body.Color = Color.FromArgb(isLight ? 238 : 246, tone);
                graphics.DrawArc(body, bounds, segment * segmentSweep - 0.45F, segmentSweep + 0.90F);
            }

            using (var tubeHighlight = new Pen(
                       Color.FromArgb(isLight ? 178 : 190, Blend(color, Color.White, 0.72F)),
                       Math.Max(2.2F, width * 0.13F))
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round
                   })
            {
                graphics.DrawArc(tubeHighlight, bounds, phase * 360F + 8F, 54F);
                graphics.DrawArc(tubeHighlight, bounds, phase * 360F + 178F, 25F);
            }

            using (var packetGlow = new Pen(
                       Color.FromArgb(isLight ? 76 : 104, Blend(color, Color.White, 0.46F)),
                       width * 0.72F)
                   {
                       StartCap = LineCap.Round,
                       EndCap = LineCap.Round
                   })
            {
                graphics.DrawArc(packetGlow, bounds, phase * 360F + 74F, 15F);
                graphics.DrawArc(packetGlow, bounds, phase * 360F + 258F, 9F);
            }
        }
        else
        {
            using (var halo = new Pen(Color.FromArgb(isLight ? 14 : 21, color), width * 2.6F))
            {
                graphics.DrawEllipse(halo, bounds);
            }
            using (var track = new Pen(Color.FromArgb(isLight ? 58 : 72, color), Math.Max(1.1F, width * 0.24F)))
            {
                graphics.DrawEllipse(track, bounds);
            }
        }

        using (var energy = new Pen(
                   Color.FromArgb(isLight ? 184 : 216, Blend(color, Color.White, prominent ? 0.16F : 0.08F)),
                   prominent ? width * 0.42F : width)
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(energy, bounds, phase * 360F, 78F);
        }
        using (var core = new Pen(
                   Color.FromArgb(218, Blend(color, Color.White, 0.66F)),
                   Math.Max(1F, prominent ? width * 0.105F : width * 0.22F))
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(core, bounds, phase * 360F + 7F, 58F);
        }
        graphics.Restore(state);
    }

    private static Color[] ResolveGalaxyOrbitColors(ArtworkPalette palette) =>
        palette.IsLight
            ?
            [
                Color.FromArgb(43, 126, 255),
                Color.FromArgb(78, 70, 244),
                Color.FromArgb(132, 92, 244),
                Color.FromArgb(45, 185, 213)
            ]
            :
            [
                Color.FromArgb(55, 121, 255),
                Color.FromArgb(124, 76, 255),
                Color.FromArgb(219, 76, 218),
                Color.FromArgb(55, 196, 238)
            ];

    private static void DrawOrbitHalf(
        Graphics graphics,
        PointF center,
        float radiusX,
        float radiusY,
        float rotation,
        bool front,
        ArtworkPalette palette)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(center.X, center.Y);
        graphics.RotateTransform(rotation);
        var bounds = new RectangleF(-radiusX, -radiusY, radiusX * 2F, radiusY * 2F);
        var start = front ? 0F : 180F;
        var ring = palette.IsLight
            ? Color.FromArgb(88, 104, 244)
            : Color.FromArgb(190, 82, 245);
        using (var halo = new Pen(Color.FromArgb(palette.IsLight ? 28 : 42, ring), 14F)
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(halo, bounds, start, 180F);
        }
        using (var body = new Pen(Color.FromArgb(218, ring), 5.2F)
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(body, bounds, start, 180F);
        }
        using (var core = new Pen(Color.FromArgb(222, Blend(ring, Color.White, 0.66F)), 1.5F)
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round
               })
        {
            graphics.DrawArc(core, bounds, start + 3F, 174F);
        }
        graphics.Restore(state);
    }

    private static void DrawSmallPlanet(
        Graphics graphics,
        PointF center,
        float radius,
        Color shadow,
        Color light,
        bool ringed,
        bool isLight)
    {
        DrawRadialGlow(
            graphics,
            new RectangleF(center.X - radius * 2.2F, center.Y - radius * 2.2F, radius * 4.4F, radius * 4.4F),
            Color.FromArgb(isLight ? 42 : 62, light));

        if (ringed)
        {
            var state = graphics.Save();
            graphics.TranslateTransform(center.X, center.Y);
            graphics.RotateTransform(-14F);
            using var rearRing = new Pen(Color.FromArgb(isLight ? 116 : 142, light), Math.Max(1.8F, radius * 0.11F));
            graphics.DrawArc(rearRing, -radius * 1.52F, -radius * 0.40F, radius * 3.04F, radius * 0.80F, 180F, 180F);
            graphics.Restore(state);
        }

        var bounds = new RectangleF(center.X - radius, center.Y - radius, radius * 2F, radius * 2F);
        using (var fill = new LinearGradientBrush(bounds, light, shadow, LinearGradientMode.ForwardDiagonal))
        {
            graphics.FillEllipse(fill, bounds);
        }
        using (var shadePath = new GraphicsPath())
        {
            shadePath.AddEllipse(bounds);
            using var shade = new PathGradientBrush(shadePath)
            {
                CenterPoint = new PointF(center.X - radius * 0.28F, center.Y - radius * 0.30F),
                CenterColor = Color.FromArgb(isLight ? 58 : 42, Color.White),
                SurroundColors = [Color.FromArgb(isLight ? 92 : 108, shadow)]
            };
            graphics.FillEllipse(shade, bounds);
        }
        using var rim = new Pen(Color.FromArgb(192, Blend(light, Color.White, 0.42F)), Math.Max(1.2F, radius * 0.07F));
        graphics.DrawEllipse(rim, bounds);

        if (ringed)
        {
            var state = graphics.Save();
            graphics.TranslateTransform(center.X, center.Y);
            graphics.RotateTransform(-14F);
            using var frontRing = new Pen(Color.FromArgb(222, Blend(light, Color.White, 0.35F)), Math.Max(1.8F, radius * 0.11F))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawArc(frontRing, -radius * 1.52F, -radius * 0.40F, radius * 3.04F, radius * 0.80F, 0F, 180F);
            graphics.Restore(state);
        }
    }

    private static void DrawMeteorLanes(Graphics graphics, ArtworkPalette palette)
    {
        // Long streaks belong to renderer-inject.js, where they actually move. Keeping the JPG
        // free of frozen meteors prevents a static trail from sitting under the animated one.
        DrawSoftParticle(graphics, new PointF(2194F, 175F), 4.8F, palette.Secondary, palette.IsLight);
        DrawSoftParticle(graphics, new PointF(2422F, 205F), 3.6F, palette.AccentAlt, palette.IsLight);
    }

    private static void DrawReadabilityVeil(Graphics graphics, ArtworkPalette palette)
    {
        var canvas = new RectangleF(0F, 0F, CanvasWidth, CanvasHeight);
        var leftAlpha = palette.IsLight ? 226 : palette.Id == NebulaDark.Id ? 184 : 202;
        using (var leftVeil = new LinearGradientBrush(
                   canvas,
                   Color.FromArgb(leftAlpha, palette.Background),
                   Color.FromArgb(0, palette.Background),
                   0F))
        {
            leftVeil.InterpolationColors = new ColorBlend
            {
                Colors =
                [
                    Color.FromArgb(leftAlpha, palette.Background),
                    Color.FromArgb(leftAlpha - (palette.IsLight ? 34 : 42), palette.Background),
                    Color.FromArgb(palette.IsLight ? 70 : 58, palette.Background),
                    Color.FromArgb(0, palette.Background),
                    Color.FromArgb(0, palette.Background)
                ],
                Positions = [0F, 0.28F, 0.48F, 0.67F, 1F]
            };
            graphics.FillRectangle(leftVeil, canvas);
        }

        using var horizon = new LinearGradientBrush(
            canvas,
            Color.FromArgb(0, palette.Background),
            Color.FromArgb(palette.IsLight ? 42 : 66, palette.Background),
            90F);
        horizon.InterpolationColors = new ColorBlend
        {
            Colors =
            [
                Color.FromArgb(palette.IsLight ? 22 : 28, palette.Background),
                Color.FromArgb(0, palette.Background),
                Color.FromArgb(palette.IsLight ? 56 : 78, palette.Background)
            ],
            Positions = [0F, 0.52F, 1F]
        };
        graphics.FillRectangle(horizon, canvas);
    }

    private static void DrawVignette(Graphics graphics, ArtworkPalette palette)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(-280F, -210F, CanvasWidth + 560F, CanvasHeight + 420F);
        using var vignette = new PathGradientBrush(path)
        {
            CenterPoint = new PointF(CanvasWidth * 0.61F, CanvasHeight * 0.43F),
            CenterColor = Color.FromArgb(0, palette.Background),
            SurroundColors =
            [
                Color.FromArgb(
                    palette.IsLight ? 32 : palette.Id == NebulaDark.Id ? 78 : 104,
                    palette.IsLight ? palette.Accent : palette.Background)
            ]
        };
        graphics.FillRectangle(vignette, 0F, 0F, CanvasWidth, CanvasHeight);
    }

    private static void DrawRadialGlow(Graphics graphics, RectangleF bounds, Color centerColor)
    {
        if (bounds.Width <= 0F || bounds.Height <= 0F)
        {
            return;
        }
        using var path = new GraphicsPath();
        path.AddEllipse(bounds);
        using var glow = new PathGradientBrush(path)
        {
            CenterColor = centerColor,
            SurroundColors = [Color.FromArgb(0, centerColor)]
        };
        graphics.FillEllipse(glow, bounds);
    }

    private static void DrawGlowPath(
        Graphics graphics,
        GraphicsPath path,
        Color color,
        float haloWidth,
        float coreWidth)
    {
        using var halo = new Pen(Color.FromArgb(Math.Min(72, (int)color.A), color), haloWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var core = new Pen(Color.FromArgb(Math.Min(136, (int)color.A), color), coreWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        graphics.DrawPath(halo, path);
        graphics.DrawPath(core, path);
    }

    private static void DrawGlowingDot(
        Graphics graphics,
        PointF point,
        float radius,
        Color color,
        bool isLight)
    {
        DrawRadialGlow(
            graphics,
            new RectangleF(point.X - radius * 3.2F, point.Y - radius * 3.2F, radius * 6.4F, radius * 6.4F),
            Color.FromArgb(isLight ? 76 : 108, color));
        using var core = new SolidBrush(Color.FromArgb(238, Blend(color, Color.White, 0.68F)));
        graphics.FillEllipse(core, point.X - radius, point.Y - radius, radius * 2F, radius * 2F);
    }

    private static void DrawSoftParticle(
        Graphics graphics,
        PointF point,
        float radius,
        Color color,
        bool isLight)
    {
        DrawRadialGlow(
            graphics,
            new RectangleF(point.X - radius * 2.4F, point.Y - radius * 2.4F, radius * 4.8F, radius * 4.8F),
            Color.FromArgb(isLight ? 48 : 78, color));
        using var core = new SolidBrush(Color.FromArgb(isLight ? 164 : 218, Blend(color, Color.White, 0.62F)));
        graphics.FillEllipse(core, point.X - radius * 0.48F, point.Y - radius * 0.48F, radius * 0.96F, radius * 0.96F);
        using var mote = new SolidBrush(Color.FromArgb(isLight ? 78 : 126, color));
        graphics.FillEllipse(mote, point.X + radius * 0.72F, point.Y - radius * 0.20F, radius * 0.34F, radius * 0.28F);
        graphics.FillEllipse(mote, point.X - radius * 0.64F, point.Y + radius * 0.42F, radius * 0.24F, radius * 0.31F);
    }

    private static PointF OrbitPoint(
        PointF center,
        float radiusX,
        float radiusY,
        float rotationDegrees,
        float phaseDegrees)
    {
        var phase = phaseDegrees * MathF.PI / 180F;
        var rotation = rotationDegrees * MathF.PI / 180F;
        var x = MathF.Cos(phase) * radiusX;
        var y = MathF.Sin(phase) * radiusY;
        return new PointF(
            center.X + (x * MathF.Cos(rotation)) - (y * MathF.Sin(rotation)),
            center.Y + (x * MathF.Sin(rotation)) + (y * MathF.Cos(rotation)));
    }

    private static Color Blend(Color from, Color to, float amount)
    {
        var ratio = Math.Clamp(amount, 0F, 1F);
        return Color.FromArgb(
            (int)Math.Round(from.A + ((to.A - from.A) * ratio)),
            (int)Math.Round(from.R + ((to.R - from.R) * ratio)),
            (int)Math.Round(from.G + ((to.G - from.G) * ratio)),
            (int)Math.Round(from.B + ((to.B - from.B) * ratio)));
    }

    private static void SaveJpeg(Bitmap bitmap, string outputPath)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
        bitmap.Save(outputPath, encoder, parameters);
    }

    private sealed record ArtworkPalette(
        string Id,
        bool IsLight,
        Color Background,
        Color Panel,
        Color PanelAlt,
        Color Accent,
        Color AccentAlt,
        Color Secondary,
        Color Highlight,
        Color Text,
        Color BackdropStart,
        Color BackdropMiddle,
        Color BackdropEnd,
        Color PlanetShadow,
        Color PlanetMid,
        Color PlanetLight);
}
