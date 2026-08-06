using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexAccountManager;

internal static class UiDesign
{
    public const int RadiusSmall = 8;
    public const int RadiusMedium = 11;
    public const int RadiusLarge = 16;
    public const int ControlHeight = 42;

    public static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0F, 1F);
        return Color.FromArgb(
            (int)Math.Round(from.A + ((to.A - from.A) * amount)),
            (int)Math.Round(from.R + ((to.R - from.R) * amount)),
            (int)Math.Round(from.G + ((to.G - from.G) * amount)),
            (int)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    public static Rectangle CenterTextVertically(Graphics graphics, string text, Font font, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return Rectangle.Empty;
        }

        var measured = TextRenderer.MeasureText(
            graphics,
            string.IsNullOrEmpty(text) ? " " : text,
            font,
            new Size(Math.Max(1, bounds.Width), int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        var height = Math.Min(bounds.Height, Math.Max(1, measured.Height));
        return new Rectangle(
            bounds.Left,
            bounds.Top + ((bounds.Height - height) / 2),
            bounds.Width,
            height);
    }

    public static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        var diameter = Math.Min(Math.Min(radius * 2F, bounds.Width), bounds.Height);
        if (diameter <= 1F)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new RectangleF(bounds.X, bounds.Y, diameter, diameter);
        path.AddArc(arc, 180F, 90F);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270F, 90F);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0F, 90F);
        arc.X = bounds.Left;
        path.AddArc(arc, 90F, 90F);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ModernButton : Button
{
    private bool _hovered;
    private bool _pressed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = UiDesign.RadiusMedium;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BaseBackColor { get; set; } = Color.RoyalBlue;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color HoverBackColor { get; set; } = Color.CornflowerBlue;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PressedBackColor { get; set; } = Color.MidnightBlue;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TextColor { get; set; } = Color.White;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DisabledBackColor { get; set; } = Color.LightGray;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DisabledTextColor { get; set; } = Color.Gray;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FocusColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color AccentColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color GradientBackColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ShadowColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color IconTileColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color IconTileBorderColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowAccent { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowIconTile { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool UseSurfaceSheen { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string IconText { get; set; } = string.Empty;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int IconWidth { get; set; } = 30;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AutoShrinkText { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float MinimumFontSize { get; set; } = 7.2F;

    public ModernButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseMnemonic = false;
        Cursor = Cursors.Hand;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.Selectable,
            true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnKeyDown(KeyEventArgs kevent)
    {
        if (kevent.KeyCode is Keys.Space or Keys.Enter)
        {
            _pressed = true;
            Invalidate();
        }

        base.OnKeyDown(kevent);
    }

    protected override void OnKeyUp(KeyEventArgs kevent)
    {
        _pressed = false;
        Invalidate();
        base.OnKeyUp(kevent);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        Region?.Dispose();
        if (Width <= 1 || Height <= 1)
        {
            return;
        }

        using var path = UiDesign.CreateRoundedPath(new RectangleF(0F, 0F, Width, Height), Radius);
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var graphics = pevent.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var fill = !Enabled
            ? DisabledBackColor
            : _pressed
                ? PressedBackColor
                : _hovered
                    ? HoverBackColor
                    : BaseBackColor;
        var textColor = Enabled ? TextColor : DisabledTextColor;
        var bounds = new RectangleF(0.75F, 0.75F, Math.Max(1F, Width - 1.5F), Math.Max(1F, Height - 1.5F));
        using var path = UiDesign.CreateRoundedPath(bounds, Radius);
        if (Enabled && ShadowColor.A > 0)
        {
            var shadowBounds = bounds;
            shadowBounds.Offset(0F, 1.4F);
            shadowBounds.Height = Math.Max(1F, shadowBounds.Height - 1.4F);
            using var shadowPath = UiDesign.CreateRoundedPath(shadowBounds, Radius);
            using var shadowBrush = new SolidBrush(ShadowColor);
            graphics.FillPath(shadowBrush, shadowPath);
        }
        var topFill = Enabled && UseSurfaceSheen
            ? UiDesign.Blend(
                fill,
                Color.White,
                fill.GetBrightness() < 0.45F ? 0.075F : 0.035F)
            : fill;
        using (var brush = new LinearGradientBrush(bounds, topFill, fill, 90F))
        {
            graphics.FillPath(brush, path);
        }

        if (Enabled && GradientBackColor.A > 0)
        {
            var overlayAlpha = Math.Clamp(
                GradientBackColor.A + (_hovered ? 22 : 0) - (_pressed ? 10 : 0),
                0,
                255);
            var overlayStart = Color.FromArgb(
                0,
                GradientBackColor.R,
                GradientBackColor.G,
                GradientBackColor.B);
            var overlayEnd = Color.FromArgb(
                overlayAlpha,
                GradientBackColor.R,
                GradientBackColor.G,
                GradientBackColor.B);
            using var overlay = new LinearGradientBrush(bounds, overlayStart, overlayEnd, 0F);
            graphics.FillPath(overlay, path);
        }

        if (Enabled && UseSurfaceSheen)
        {
            var highlightBounds = RectangleF.Inflate(bounds, -1.25F, -1.25F);
            highlightBounds.Height = Math.Max(1F, highlightBounds.Height - 1F);
            using var highlightPath = UiDesign.CreateRoundedPath(highlightBounds, Math.Max(2, Radius - 1));
            using var highlightPen = new Pen(Color.FromArgb(
                fill.GetBrightness() < 0.45F ? 34 : 54,
                Color.White), 1F);
            graphics.DrawPath(highlightPen, highlightPath);
        }

        if (BorderColor.A > 0)
        {
            using var borderPen = new Pen(BorderColor, 1F);
            graphics.DrawPath(borderPen, path);
        }

        if (ShowAccent && AccentColor.A > 0)
        {
            using var accentBrush = new SolidBrush(AccentColor);
            graphics.FillRoundedRectangle(accentBrush, new RectangleF(4F, 10F, 3F, Math.Max(8F, Height - 20F)), 1.5F);
        }

        var horizontalPadding = Math.Max(10, Math.Max(Padding.Left, Padding.Right));
        var content = Rectangle.Inflate(ClientRectangle, -horizontalPadding, -2);
        if (_pressed)
        {
            content.Offset(0, 1);
        }
        var flags = TextFormatFlags.SingleLine |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix;
        flags |= TextAlign switch
        {
            ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft => TextFormatFlags.Left,
            ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.HorizontalCenter
        };

        if (!string.IsNullOrEmpty(IconText))
        {
            var iconRect = new Rectangle(content.Left, content.Top, IconWidth, content.Height);
            if (ShowIconTile && IconTileColor.A > 0)
            {
                var tileSize = Math.Min(28, Math.Max(22, content.Height - 4));
                var tileBounds = new RectangleF(
                    iconRect.Left + Math.Max(0F, (iconRect.Width - tileSize) / 2F),
                    iconRect.Top + Math.Max(0F, (iconRect.Height - tileSize) / 2F),
                    tileSize,
                    tileSize);
                using var tilePath = UiDesign.CreateRoundedPath(tileBounds, 8F);
                using var tileBrush = new SolidBrush(IconTileColor);
                graphics.FillPath(tileBrush, tilePath);
                if (IconTileBorderColor.A > 0)
                {
                    using var tilePen = new Pen(IconTileBorderColor, 1F);
                    graphics.DrawPath(tilePen, tilePath);
                }
            }
            using var iconFont = new Font(Font.FontFamily, Math.Max(9F, Font.Size + 1F), FontStyle.Regular, GraphicsUnit.Point);
            iconRect = UiDesign.CenterTextVertically(graphics, IconText, iconFont, iconRect);
            TextRenderer.DrawText(graphics, IconText, iconFont, iconRect, textColor,
                TextFormatFlags.SingleLine |
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding |
                TextFormatFlags.NoPrefix);
            content.X += IconWidth + 8;
            content.Width = Math.Max(1, content.Width - IconWidth - 8);
            flags &= ~TextFormatFlags.HorizontalCenter;
            flags |= TextFormatFlags.Left;
        }

        Font? fittedFont = null;
        var textFont = Font;
        if (AutoShrinkText && !string.IsNullOrEmpty(Text) && content.Width > 0)
        {
            var measured = TextRenderer.MeasureText(
                graphics,
                Text,
                Font,
                new Size(int.MaxValue, Math.Max(1, content.Height)),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (measured.Width > content.Width)
            {
                var size = Math.Max(MinimumFontSize, Font.Size * content.Width / measured.Width * 0.94F);
                if (size < Font.Size - 0.05F)
                {
                    fittedFont = new Font(Font.FontFamily, size, Font.Style, GraphicsUnit.Point);
                    textFont = fittedFont;
                }
            }
        }

        var textBounds = UiDesign.CenterTextVertically(graphics, Text, textFont, content);
        TextRenderer.DrawText(graphics, Text, textFont, textBounds, textColor, flags);
        fittedFont?.Dispose();

        if (Focused && ShowFocusCues && FocusColor.A > 0)
        {
            var focusBounds = RectangleF.Inflate(bounds, -2.5F, -2.5F);
            using var focusPath = UiDesign.CreateRoundedPath(focusBounds, Math.Max(2, Radius - 2));
            using var focusPen = new Pen(FocusColor, 1F) { DashStyle = DashStyle.Dot };
            graphics.DrawPath(focusPen, focusPath);
        }
    }
}

internal sealed class ModernInputShell : Panel
{
    private readonly Control _input;
    private bool _focused;
    private bool _layingOutInput;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Radius { get; set; } = UiDesign.RadiusSmall;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = Color.White;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color BorderColor { get; set; } = Color.LightGray;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FocusBorderColor { get; set; } = Color.RoyalBlue;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color GlyphColor { get; set; } = Color.Gray;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowSearchGlyph { get; }

    public ModernInputShell(Control input, bool showSearchGlyph = false)
    {
        _input = input;
        ShowSearchGlyph = showSearchGlyph;
        Height = UiDesign.ControlHeight;
        Padding = new Padding(showSearchGlyph ? 42 : 14, 9, 12, 8);
        Margin = Padding.Empty;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);

        if (input.Parent != null)
        {
            input.Parent.Controls.Remove(input);
        }

        input.Dock = DockStyle.None;
        input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        input.Margin = Padding.Empty;
        if (input is TextBox textBox)
        {
            textBox.BorderStyle = BorderStyle.None;
            textBox.HandleCreated += (_, _) => ApplyTextBoxMargins(textBox);
            if (!textBox.Multiline)
            {
                textBox.AutoSize = true;
            }
        }
        else if (input is ComboBox comboBox)
        {
            comboBox.FlatStyle = FlatStyle.Flat;
        }

        input.GotFocus += (_, _) => SetFocused(true);
        input.LostFocus += (_, _) => SetFocused(false);
        input.FontChanged += (_, _) => PerformLayout();
        Controls.Add(input);
        LayoutInput();
    }

    public void ApplyPalette(ThemePalette palette)
    {
        FillColor = palette.InputBackColor;
        BorderColor = ThemeStyler.IsDark(palette)
            ? palette.BorderColor
            : UiDesign.Blend(palette.BorderColor, palette.MutedTextColor, 0.22F);
        FocusBorderColor = palette.FocusColor;
        GlyphColor = palette.MutedTextColor;
        _input.BackColor = FillColor;
        _input.ForeColor = palette.TextColor;
        if (_input is TextBox textBox)
        {
            textBox.BorderStyle = BorderStyle.None;
            if (!textBox.Multiline)
            {
                textBox.AutoSize = true;
            }
        }
        else if (_input is ComboBox comboBox)
        {
            comboBox.FlatStyle = FlatStyle.Flat;
        }

        if (_input is ThemedComboBox themedComboBox)
        {
            themedComboBox.ApplyPalette(palette);
        }
        PerformLayout();
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutInput();
    }

    private void LayoutInput()
    {
        if (_layingOutInput || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        _layingOutInput = true;
        try
        {
            var left = Math.Max(2, Padding.Left);
            var right = Math.Max(2, Padding.Right);
            var width = Math.Max(1, ClientSize.Width - left - right);
            var maximumHeight = Math.Max(1, ClientSize.Height - 4);
            var preferredHeight = _input switch
            {
                TextBox textBox when !textBox.Multiline => textBox.PreferredHeight,
                ComboBox comboBox => comboBox.PreferredHeight,
                _ => maximumHeight
            };
            var height = Math.Min(maximumHeight, Math.Max(1, preferredHeight));
            var top = Math.Max(2, (ClientSize.Height - height) / 2);
            _input.SetBounds(left, top, width, height);
            if (_input is TextBox inputTextBox)
            {
                ApplyTextBoxMargins(inputTextBox);
            }

            // Native single-line inputs can normalize their own height after SetBounds.
            // Recenter using the resulting height so high-DPI glyphs keep equal breathing room.
            var centeredTop = Math.Max(2, (ClientSize.Height - _input.Height) / 2);
            if (_input.Top != centeredTop)
            {
                _input.Top = centeredTop;
            }
        }
        finally
        {
            _layingOutInput = false;
        }
    }

    private void ApplyTextBoxMargins(TextBox textBox)
    {
        if (!textBox.IsHandleCreated)
        {
            return;
        }

        // Borderless native EDIT controls can place the first glyph at x=0. At
        // 200% DPI the left edge of a drive letter such as "C" is then clipped
        // into two dots and visually looks like a leading colon. Give every
        // shell-hosted text box a small DPI-aware internal breathing margin.
        var margin = Math.Max(10, (int)Math.Round(DeviceDpi / 12D));
        var packedMargins = (margin & 0xFFFF) | ((margin & 0xFFFF) << 16);
        SendMessage(
            textBox.Handle,
            EmSetMargins,
            (IntPtr)(EcLeftMargin | EcRightMargin),
            (IntPtr)packedMargins);
    }

    private void SetFocused(bool focused)
    {
        _focused = focused;
        Invalidate();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Region?.Dispose();
        if (Width <= 1 || Height <= 1)
        {
            return;
        }

        using var path = UiDesign.CreateRoundedPath(new RectangleF(0F, 0F, Width, Height), Radius);
        Region = new Region(path);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiDesign.CreateRoundedPath(
            new RectangleF(0.5F, 0.5F, Math.Max(1F, Width - 1F), Math.Max(1F, Height - 1F)),
            Radius);
        using var brush = new SolidBrush(FillColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var dpiScale = Math.Max(1F, DeviceDpi / 96F);
        var borderWidth = _focused
            ? Math.Max(1.6F, 1.35F * dpiScale)
            : Math.Max(1F, dpiScale);
        var inset = Math.Max(0.5F, borderWidth / 2F);
        var bounds = new RectangleF(
            inset,
            inset,
            Math.Max(1F, Width - (inset * 2F)),
            Math.Max(1F, Height - (inset * 2F)));
        using var path = UiDesign.CreateRoundedPath(bounds, Radius);
        using var pen = new Pen(_focused ? FocusBorderColor : BorderColor, borderWidth);
        e.Graphics.DrawPath(pen, path);

        if (ShowSearchGlyph)
        {
            using var glyphPen = new Pen(
                _focused ? FocusBorderColor : GlyphColor,
                Math.Max(1.7F, 1.35F * dpiScale))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawEllipse(glyphPen, 16F, (Height / 2F) - 7F, 11F, 11F);
            e.Graphics.DrawLine(glyphPen, 25F, (Height / 2F) + 3F, 30F, (Height / 2F) + 8F);
        }

        base.OnPaint(e);
    }

    private const uint EmSetMargins = 0x00D3;
    private const int EcLeftMargin = 0x0001;
    private const int EcRightMargin = 0x0002;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}

internal sealed class ThemedComboBox : ComboBox
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ItemBackColor { get; set; } = Color.White;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color ItemTextColor { get; set; } = Color.Black;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SelectedItemBackColor { get; set; } = Color.RoyalBlue;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color SelectedItemTextColor { get; set; } = Color.White;

    public ThemedComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        IntegralHeight = false;
        FlatStyle = FlatStyle.Flat;
        UpdateItemMetrics();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateItemMetrics();
        Parent?.PerformLayout();
    }

    private void UpdateItemMetrics()
    {
        var itemHeight = Math.Max(28, Font.Height + 8);
        if (ItemHeight != itemHeight)
        {
            ItemHeight = itemHeight;
        }

        DropDownHeight = itemHeight * 7;
    }

    public void ApplyPalette(ThemePalette palette)
    {
        BackColor = palette.InputBackColor;
        ForeColor = palette.TextColor;
        ItemBackColor = palette.SurfaceColor;
        ItemTextColor = palette.TextColor;
        SelectedItemBackColor = UiDesign.Blend(palette.PrimaryColor, palette.SurfaceColor, 0.18F);
        SelectedItemTextColor = Color.White;
        Invalidate();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0)
        {
            return;
        }

        var isComboBoxEdit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        var selected = !isComboBoxEdit && (e.State & DrawItemState.Selected) != 0;
        var backgroundColor = isComboBoxEdit
            ? BackColor
            : selected ? SelectedItemBackColor : ItemBackColor;
        using var background = new SolidBrush(backgroundColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        var textColor = isComboBoxEdit
            ? ForeColor
            : selected ? SelectedItemTextColor : ItemTextColor;
        var itemText = GetItemText(Items[e.Index]) ?? string.Empty;
        var textBounds = Rectangle.Inflate(e.Bounds, -10, 0);
        textBounds = UiDesign.CenterTextVertically(e.Graphics, itemText, Font, textBounds);
        TextRenderer.DrawText(
            e.Graphics,
            itemText,
            Font,
            textBounds,
            textColor,
            TextFormatFlags.SingleLine |
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix);
        if (!isComboBoxEdit)
        {
            e.DrawFocusRectangle();
        }
    }
}

internal sealed class ThemePicker : Control
{
    private readonly List<string> _items = [];
    private readonly ContextMenuStrip _menu = new()
    {
        AutoSize = false,
        ShowCheckMargin = false,
        ShowImageMargin = false,
        Padding = new Padding(6),
        Margin = Padding.Empty
    };
    private int _selectedIndex = -1;
    private bool _hovered;
    private bool _pressed;
    private bool _menuOpen;
    private long _skipClickUntil;
    private Color _fillColor = Color.White;
    private Color _hoverColor = Color.WhiteSmoke;
    private Color _pressedColor = Color.Gainsboro;
    private Color _borderColor = Color.LightGray;
    private Color _focusColor = Color.RoyalBlue;
    private Color _textColor = Color.Black;
    private Color _chevronColor = Color.DimGray;
    private Color _menuBackColor = Color.White;
    private Color _menuBackColorEnd = Color.WhiteSmoke;
    private Color _menuTextColor = Color.Black;
    private Color _menuSelectedColor = Color.AliceBlue;

    public ThemePicker()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.Selectable,
            true);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.ComboBox;
        AccessibleName = "外观模式";
        Size = new Size(216, 46);
        Font = new Font("Microsoft YaHei UI", 9F);
        _menu.Opened += (_, _) =>
        {
            _menuOpen = true;
            Invalidate();
        };
        _menu.Closed += (_, args) =>
        {
            _menuOpen = false;
            _pressed = false;
            // When the popup closes because the user clicked the owner (which is
            // common when it opens upward at the bottom of the sidebar), WinForms
            // raises the owner's Click after Closed. Ignore only that immediate
            // follow-up click so the menu does not reopen by itself.
            _skipClickUntil = args.CloseReason is
                ToolStripDropDownCloseReason.AppFocusChange or
                ToolStripDropDownCloseReason.AppClicked or
                ToolStripDropDownCloseReason.CloseCalled
                ? Environment.TickCount64 + 350
                : 0;
            Invalidate();
        };
    }

    public event EventHandler? SelectedIndexChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var normalized = _items.Count == 0 ? -1 : Math.Clamp(value, 0, _items.Count - 1);
            if (_selectedIndex == normalized)
            {
                return;
            }

            _selectedIndex = normalized;
            Text = normalized >= 0 ? _items[normalized] : "";
            SyncMenuItems();
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetItems(IEnumerable<string> items)
    {
        _items.Clear();
        _items.AddRange(items.Where(value => !string.IsNullOrWhiteSpace(value)));
        RebuildMenu();
        if (_items.Count == 0)
        {
            _selectedIndex = -1;
            Text = "";
        }
        else if (_selectedIndex < 0 || _selectedIndex >= _items.Count)
        {
            _selectedIndex = 0;
            Text = _items[0];
        }
        SyncMenuItems();
        Invalidate();
    }

    public void ApplyPalette(ThemePalette palette)
    {
        _fillColor = palette.SidebarHoverColor;
        _hoverColor = UiDesign.Blend(palette.SidebarHoverColor, palette.SecondaryAccentColor, 0.12F);
        _pressedColor = UiDesign.Blend(palette.SidebarSelectedColor, palette.AccentColor, 0.12F);
        _borderColor = palette.SidebarBorderColor;
        _focusColor = palette.AccentColor;
        _textColor = palette.SidebarTextColor;
        _chevronColor = palette.SidebarMutedTextColor;
        // The picker lives in the sidebar, so its popup must read as part of the
        // selected theme instead of opening a detached white native menu.
        _menuBackColor = palette.SidebarColor;
        _menuBackColorEnd = UiDesign.Blend(palette.SidebarColor, palette.HeroEndColor, 0.22F);
        _menuTextColor = palette.SidebarTextColor;
        _menuSelectedColor = UiDesign.Blend(palette.SidebarSelectedColor, palette.AccentColor, 0.18F);
        ForeColor = _textColor;
        BackColor = Color.Transparent;
        ApplyMenuPalette();
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        RebuildMenu();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            if (_menuOpen || _menu.Visible)
            {
                _menu.Close(ToolStripDropDownCloseReason.CloseCalled);
                _pressed = false;
                Invalidate();
                return;
            }

            _pressed = true;
            Focus();
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (_skipClickUntil > Environment.TickCount64)
        {
            _skipClickUntil = 0;
            return;
        }
        _skipClickUntil = 0;
        if (_menu.Visible)
        {
            // The popup can open upward when the picker is in the bottom sidebar.
            // Treat a second click on the picker as an explicit toggle so it never
            // appears stuck open behind the control.
            _menu.Close(ToolStripDropDownCloseReason.Keyboard);
            return;
        }
        ShowMenu();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space or Keys.F4 ||
            (e.Alt && e.KeyCode == Keys.Down))
        {
            ShowMenu();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (_items.Count == 0 || e.KeyCode is not (Keys.Up or Keys.Down))
        {
            return;
        }

        SelectedIndex = e.KeyCode == Keys.Up
            ? Math.Max(0, SelectedIndex - 1)
            : Math.Min(_items.Count - 1, SelectedIndex + 1);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.75F, 0.75F, Math.Max(1F, Width - 1.5F), Math.Max(1F, Height - 1.5F));
        using var path = UiDesign.CreateRoundedPath(bounds, Math.Max(8, ScaleLogical(10)));
        var fill = _pressed || _menuOpen
            ? _pressedColor
            : _hovered
                ? _hoverColor
                : _fillColor;
        using (var brush = new SolidBrush(fill))
        {
            e.Graphics.FillPath(brush, path);
        }

        using (var pen = new Pen(Focused || _menuOpen ? _focusColor : _borderColor, Focused || _menuOpen ? 1.6F : 1F))
        {
            e.Graphics.DrawPath(pen, path);
        }

        var chevronAreaWidth = ScaleLogical(38);
        var textBounds = new Rectangle(
            ScaleLogical(14),
            2,
            Math.Max(1, Width - ScaleLogical(14) - chevronAreaWidth),
            Math.Max(1, Height - 4));
        textBounds = UiDesign.CenterTextVertically(e.Graphics, Text, Font, textBounds);
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            textBounds,
            Enabled ? _textColor : ControlPaint.Dark(_textColor),
            TextFormatFlags.SingleLine |
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix |
            TextFormatFlags.NoPadding);

        var centerX = Width - (chevronAreaWidth / 2);
        var centerY = Height / 2;
        var half = Math.Max(3, ScaleLogical(4));
        using var chevronPen = new Pen(_menuOpen ? _focusColor : _chevronColor, Math.Max(1.5F, DeviceDpi / 96F * 1.45F))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        if (_menuOpen)
        {
            e.Graphics.DrawLines(chevronPen,
            [
                new Point(centerX - half, centerY + 2),
                new Point(centerX, centerY - half + 1),
                new Point(centerX + half, centerY + 2)
            ]);
        }
        else
        {
            e.Graphics.DrawLines(chevronPen,
            [
                new Point(centerX - half, centerY - 2),
                new Point(centerX, centerY + half - 1),
                new Point(centerX + half, centerY - 2)
            ]);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ShowMenu()
    {
        if (_items.Count == 0)
        {
            return;
        }

        SyncMenuItems();
        var itemHeight = Math.Max(32, Font.Height + ScaleLogical(9));
        var preferredMenuWidth = Math.Max(Width, ScaleLogical(188));
        var preferredMenuHeight = (itemHeight * _menu.Items.Count) + _menu.Padding.Vertical + 2;
        var popupConstraintBounds = GetPopupConstraintBounds();
        var menuWidth = popupConstraintBounds.Width > 0
            ? Math.Min(preferredMenuWidth, popupConstraintBounds.Width)
            : preferredMenuWidth;
        var menuHeight = popupConstraintBounds.Height > 0
            ? Math.Min(preferredMenuHeight, popupConstraintBounds.Height)
            : preferredMenuHeight;
        foreach (ToolStripItem item in _menu.Items)
        {
            item.AutoSize = false;
            item.Size = new Size(menuWidth - _menu.Padding.Horizontal, itemHeight);
        }
        _menu.MaximumSize = new Size(menuWidth, menuHeight);
        _menu.Size = new Size(menuWidth, menuHeight);

        if (popupConstraintBounds.Width <= 0 || popupConstraintBounds.Height <= 0)
        {
            _menu.Show(this, new Point(0, Height), ToolStripDropDownDirection.BelowRight);
            return;
        }

        var anchorBounds = RectangleToScreen(ClientRectangle);
        var popupBounds = CalculateMenuBounds(popupConstraintBounds, anchorBounds, _menu.Size);
        _menu.Show(popupBounds.Location);
        if (_menu.Bounds != popupBounds)
        {
            // ContextMenuStrip is a top-level window and is not clipped by its owner.
            // Enforce the sidebar bounds again after the native handle applies DPI scaling.
            _menu.Bounds = popupBounds;
        }
    }

    private Rectangle GetPopupConstraintBounds()
    {
        Control? container = Parent;
        while (container != null &&
               !string.Equals(container.Name, "Sidebar", StringComparison.Ordinal))
        {
            container = container.Parent;
        }

        container ??= FindForm();
        if (container == null || container.ClientSize.Width <= 0 || container.ClientSize.Height <= 0)
        {
            return Rectangle.Empty;
        }

        var bounds = new Rectangle(container.PointToScreen(Point.Empty), container.ClientSize);
        var inset = Math.Min(ScaleLogical(6), Math.Max(0, Math.Min(bounds.Width, bounds.Height) / 4));
        if (bounds.Width > inset * 2 && bounds.Height > inset * 2)
        {
            bounds.Inflate(-inset, -inset);
        }
        return bounds;
    }

    private static Rectangle CalculateMenuBounds(
        Rectangle ownerClientBounds,
        Rectangle anchorBounds,
        Size menuSize)
    {
        var width = Math.Min(Math.Max(1, menuSize.Width), Math.Max(1, ownerClientBounds.Width));
        var height = Math.Min(Math.Max(1, menuSize.Height), Math.Max(1, ownerClientBounds.Height));
        var maxX = Math.Max(ownerClientBounds.Left, ownerClientBounds.Right - width);
        var x = Math.Clamp(anchorBounds.Left, ownerClientBounds.Left, maxX);

        var spaceBelow = Math.Max(0, ownerClientBounds.Bottom - anchorBounds.Bottom);
        var spaceAbove = Math.Max(0, anchorBounds.Top - ownerClientBounds.Top);
        var openAbove = spaceBelow < height && spaceAbove >= spaceBelow;
        var preferredY = openAbove
            ? anchorBounds.Top - height
            : anchorBounds.Bottom;
        var maxY = Math.Max(ownerClientBounds.Top, ownerClientBounds.Bottom - height);
        var y = Math.Clamp(preferredY, ownerClientBounds.Top, maxY);
        return new Rectangle(x, y, width, height);
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        for (var index = 0; index < _items.Count; index++)
        {
            var itemIndex = index;
            var item = new ToolStripMenuItem
            {
                AutoSize = false,
                Font = Font,
                Padding = new Padding(ScaleLogical(8), 0, ScaleLogical(8), 0),
                Tag = itemIndex
            };
            item.Click += (_, _) => SelectedIndex = itemIndex;
            _menu.Items.Add(item);
        }
        ApplyMenuPalette();
        SyncMenuItems();
    }

    private void SyncMenuItems()
    {
        for (var index = 0; index < _menu.Items.Count; index++)
        {
            var selected = index == _selectedIndex;
            _menu.Items[index].Text = (selected ? "✓  " : "    ") + _items[index];
            _menu.Items[index].AccessibleName = _items[index] + (selected ? "，当前" : "");
        }
    }

    private void ApplyMenuPalette()
    {
        _menu.BackColor = _menuBackColor;
        _menu.ForeColor = _menuTextColor;
        _menu.Renderer = new ThemePickerMenuRenderer(
            _menuBackColor,
            _menuBackColorEnd,
            _menuTextColor,
            _menuSelectedColor,
            _borderColor,
            _focusColor);
        foreach (ToolStripItem item in _menu.Items)
        {
            item.BackColor = _menuBackColor;
            item.ForeColor = _menuTextColor;
        }
    }

    private int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96D));
}

internal sealed class ThemePickerMenuRenderer(
    Color backColor,
    Color backColorEnd,
    Color textColor,
    Color selectedColor,
    Color borderColor,
    Color accentColor) : ToolStripRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new LinearGradientBrush(
            e.AffectedBounds,
            backColor,
            backColorEnd,
            LinearGradientMode.Horizontal);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(borderColor, 1F);
        var bounds = new Rectangle(0, 0, Math.Max(1, e.ToolStrip.Width - 1), Math.Max(1, e.ToolStrip.Height - 1));
        e.Graphics.DrawRectangle(pen, bounds);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(2.5F, 1.5F, Math.Max(1, e.Item.Width - 5F), Math.Max(1, e.Item.Height - 3F));
        var color = e.Item.Selected ? selectedColor : Color.FromArgb(0, backColor);
        using var path = UiDesign.CreateRoundedPath(bounds, 8F);
        using var brush = new SolidBrush(color);
        e.Graphics.FillPath(brush, path);
        if (e.Item.Selected)
        {
            using var pen = new Pen(Color.FromArgb(110, accentColor), 1F);
            e.Graphics.DrawPath(pen, path);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = textColor;
        e.TextFormat = TextFormatFlags.SingleLine |
                       TextFormatFlags.Left |
                       TextFormatFlags.VerticalCenter |
                       TextFormatFlags.NoPrefix |
                       TextFormatFlags.EndEllipsis;
        base.OnRenderItemText(e);
    }
}

internal sealed class PillLabel : Label
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color StrokeColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DotColor { get; set; } = Color.Transparent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowDot { get; set; }

    public PillLabel()
    {
        AutoSize = false;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        base.OnPaintBackground(pevent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5F, 0.5F, Math.Max(1F, Width - 1F), Math.Max(1F, Height - 1F));
        using var path = UiDesign.CreateRoundedPath(bounds, Height / 2F);
        using (var brush = new SolidBrush(FillColor))
        {
            e.Graphics.FillPath(brush, path);
        }

        if (StrokeColor.A > 0)
        {
            using var pen = new Pen(StrokeColor, 1F);
            e.Graphics.DrawPath(pen, path);
        }

        var textRect = Rectangle.Inflate(ClientRectangle, -10, -2);
        if (ShowDot)
        {
            var dotSize = Math.Max(5, Math.Min(8, Height / 4));
            var dotY = (Height - dotSize) / 2;
            using var dotBrush = new SolidBrush(DotColor);
            e.Graphics.FillEllipse(dotBrush, textRect.Left, dotY, dotSize, dotSize);
            textRect.X += dotSize + 7;
            textRect.Width = Math.Max(1, textRect.Width - dotSize - 7);
        }

        var flags = TextFormatFlags.SingleLine |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix;
        flags |= TextAlign switch
        {
            ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft => TextFormatFlags.Left,
            ContentAlignment.MiddleRight or ContentAlignment.TopRight or ContentAlignment.BottomRight => TextFormatFlags.Right,
            _ => TextFormatFlags.HorizontalCenter
        };
        Font? fittedFont = null;
        var textFont = Font;
        var measured = TextRenderer.MeasureText(
            e.Graphics,
            Text,
            Font,
            new Size(int.MaxValue, Math.Max(1, textRect.Height)),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        if (measured.Width > textRect.Width && textRect.Width > 0)
        {
            var size = Math.Max(7F, Font.Size * textRect.Width / measured.Width * 0.94F);
            if (size < Font.Size - 0.05F)
            {
                fittedFont = new Font(Font.FontFamily, size, Font.Style, GraphicsUnit.Point);
                textFont = fittedFont;
            }
        }

        var textBounds = UiDesign.CenterTextVertically(e.Graphics, Text, textFont, textRect);
        TextRenderer.DrawText(e.Graphics, Text, textFont, textBounds, ForeColor, flags);
        fittedFont?.Dispose();
    }
}

internal sealed class QuotaProgressBar : Control
{
    private double _value;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0D, 100D);
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color TrackColor { get; set; } = Color.LightGray;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = Color.RoyalBlue;

    public QuotaProgressBar()
    {
        Height = 5;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(0F, 0F, Width, Height);
        using var trackPath = UiDesign.CreateRoundedPath(track, Height / 2F);
        using var trackBrush = new SolidBrush(TrackColor);
        e.Graphics.FillPath(trackBrush, trackPath);

        var fillWidth = (float)(Width * (_value / 100D));
        if (fillWidth < 1F)
        {
            return;
        }

        var fill = new RectangleF(0F, 0F, Math.Max(Height, fillWidth), Height);
        using var fillPath = UiDesign.CreateRoundedPath(fill, Height / 2F);
        using var fillBrush = new LinearGradientBrush(fill, FillColor, UiDesign.Blend(FillColor, Color.White, 0.18F), 0F);
        e.Graphics.FillPath(fillBrush, fillPath);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = UiDesign.CreateRoundedPath(bounds, radius);
        graphics.FillPath(brush, path);
    }
}

internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    private const int ScrollFrameIntervalMilliseconds = 33;
    private const int ScrollSettleDelayMilliseconds = 90;
    private readonly System.Windows.Forms.Timer _scrollFrameTimer;
    private readonly System.Windows.Forms.Timer _scrollSettleTimer;
    private bool _scrollFramePending;
    private bool _scrollInProgress;
    private int _scrollRefreshVersion;

    public event EventHandler? ViewportChanged;

    internal bool IsScrollInProgress => _scrollInProgress;

    public BufferedFlowLayoutPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        UpdateStyles();
        _scrollFrameTimer = new System.Windows.Forms.Timer
        {
            Interval = ScrollFrameIntervalMilliseconds
        };
        _scrollFrameTimer.Tick += HandleScrollFrame;
        _scrollSettleTimer = new System.Windows.Forms.Timer
        {
            Interval = ScrollSettleDelayMilliseconds
        };
        _scrollSettleTimer.Tick += HandleScrollSettled;
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        QueueScrollRefresh();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // ScrollableControl's wheel path can call SetDisplayRectLocation/ScrollWindowEx
        // without raising OnScroll. Compare the native display position around the base
        // call so wheel and precision-touchpad scrolling receive the same repaint policy.
        var before = AutoScrollPosition;
        var refreshVersion = _scrollRefreshVersion;
        base.OnMouseWheel(e);
        if (AutoScrollPosition != before && refreshVersion == _scrollRefreshVersion)
        {
            QueueScrollRefresh();
        }
    }

    private void QueueScrollRefresh()
    {
        unchecked
        {
            _scrollRefreshVersion++;
        }
        _scrollInProgress = true;
        _scrollFramePending = true;
        if (!_scrollFrameTimer.Enabled)
        {
            ProcessScrollFrame();
            _scrollFrameTimer.Start();
        }
        _scrollSettleTimer.Stop();
        _scrollSettleTimer.Start();
    }

    private void HandleScrollFrame(object? sender, EventArgs e)
    {
        if (!_scrollFramePending)
        {
            _scrollFrameTimer.Stop();
            return;
        }
        ProcessScrollFrame();
    }

    private void ProcessScrollFrame()
    {
        _scrollFramePending = false;
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        ViewportChanged?.Invoke(this, EventArgs.Empty);
        // Native AutoScroll moves child windows with a bit-block transfer. Repaint the
        // visible descendants synchronously at a throttled 30 FPS so an owner-drawn child
        // cannot leave copied horizontal strips behind while the wheel is still moving.
        InvalidateVisibleViewport(flush: true);
    }

    private void HandleScrollSettled(object? sender, EventArgs e)
    {
        _scrollSettleTimer.Stop();
        _scrollFrameTimer.Stop();
        _scrollFramePending = false;
        _scrollInProgress = false;
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        ViewportChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisibleViewport(flush: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scrollFrameTimer.Stop();
            _scrollSettleTimer.Stop();
            _scrollInProgress = false;
            _scrollFrameTimer.Tick -= HandleScrollFrame;
            _scrollSettleTimer.Tick -= HandleScrollSettled;
            _scrollFrameTimer.Dispose();
            _scrollSettleTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Invalidates just the currently visible portions of card children.  This is deliberately
    /// not Refresh(): native scrolling remains responsive and Windows can coalesce this with
    /// its own exposed-strip repaint.
    /// </summary>
    internal void InvalidateVisibleViewport(bool flush = false)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        // Repaint only the currently exposed slice of every descendant. Invalidating only the
        // top-level card does not invalidate separately-windowed owner-drawn grandchildren;
        // after ScrollWindowEx copies the viewport those stale pixels appear as repeated rows
        // and planet bands. Screen-space clipping keeps off-screen chart/table work untouched.
        var viewportScreen = RectangleToScreen(ClientRectangle);
        var visibleSlices = new List<(Control Control, Rectangle Clip)>();
        Invalidate(ClientRectangle, invalidateChildren: false);
        CollectVisibleDescendantSlices(this, viewportScreen, visibleSlices);
        foreach (var (control, clip) in visibleSlices)
        {
            control.Invalidate(clip, invalidateChildren: false);
        }

        if (flush)
        {
            // Parents are collected before their children, preserving background/foreground
            // paint order while closing the native exposed-strip gap immediately.
            Update();
            foreach (var (control, _) in visibleSlices)
            {
                control.Update();
            }
        }
    }

    private static void CollectVisibleDescendantSlices(
        Control parent,
        Rectangle viewportScreen,
        ICollection<(Control Control, Rectangle Clip)> destination)
    {
        foreach (Control child in parent.Controls)
        {
            if (child.IsDisposed || !child.Visible || !child.IsHandleCreated)
            {
                continue;
            }

            var visibleScreen = Rectangle.Intersect(
                viewportScreen,
                child.RectangleToScreen(child.ClientRectangle));
            if (visibleScreen.Width <= 0 || visibleScreen.Height <= 0)
            {
                continue;
            }

            destination.Add((child, child.RectangleToClient(visibleScreen)));
            if (child.HasChildren)
            {
                CollectVisibleDescendantSlices(child, viewportScreen, destination);
            }
        }
    }

    internal static void ValidateNestedViewportRedraw()
    {
        using var panel = new BufferedFlowLayoutPanel
        {
            Size = new Size(320, 220),
            AutoScroll = true,
            WrapContents = false,
            FlowDirection = FlowDirection.TopDown
        };
        using var card = new Panel { Size = new Size(280, 620), Margin = Padding.Empty };
        using var visibleGrandchild = new Panel { Bounds = new Rectangle(12, 24, 240, 120) };
        using var hiddenGrandchild = new Panel { Bounds = new Rectangle(12, 430, 240, 120) };
        card.Controls.Add(visibleGrandchild);
        card.Controls.Add(hiddenGrandchild);
        panel.Controls.Add(card);
        panel.CreateControl();
        card.CreateControl();
        visibleGrandchild.CreateControl();
        hiddenGrandchild.CreateControl();
        panel.PerformLayout();

        var visibleInvalidations = 0;
        var hiddenInvalidations = 0;
        visibleGrandchild.Invalidated += (_, _) => visibleInvalidations++;
        hiddenGrandchild.Invalidated += (_, _) => hiddenInvalidations++;
        panel.InvalidateVisibleViewport(flush: false);
        if (visibleInvalidations == 0 || hiddenInvalidations != 0)
        {
            throw new InvalidOperationException(
                "Scrollable viewport redraw must directly invalidate visible nested controls only.");
        }

        visibleInvalidations = 0;
        var beforeWheel = panel.AutoScrollPosition;
        panel.OnMouseWheel(new MouseEventArgs(
            MouseButtons.None,
            0,
            0,
            0,
            -SystemInformation.MouseWheelScrollDelta));
        if (panel.AutoScrollPosition == beforeWheel ||
            !panel.IsScrollInProgress ||
            visibleInvalidations == 0)
        {
            throw new InvalidOperationException(
                "Mouse-wheel scrolling must enter the throttled nested-control redraw path.");
        }
    }
}

internal sealed class BufferedTableLayoutPanel : TableLayoutPanel
{
    public BufferedTableLayoutPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        UpdateStyles();
    }
}

internal static class ControlViewport
{
    /// <summary>
    /// WinForms keeps a child control Visible even after an AutoScroll parent moves it far
    /// outside the viewport.  Animated children must use screen-space intersection instead
    /// of Visible alone so off-screen timers cannot consume the UI thread while a list moves.
    /// </summary>
    public static bool IsInsideScrollableViewport(Control control)
    {
        if (control.IsDisposed || control.Disposing || !control.IsHandleCreated || !control.Visible)
        {
            return false;
        }

        var controlBounds = control.RectangleToScreen(control.ClientRectangle);
        if (controlBounds.Width <= 0 || controlBounds.Height <= 0)
        {
            return false;
        }

        for (Control? parent = control.Parent; parent != null; parent = parent.Parent)
        {
            if (parent.IsDisposed || parent.Disposing || !parent.Visible)
            {
                return false;
            }

            if (parent is not ScrollableControl { AutoScroll: true })
            {
                continue;
            }

            var viewport = parent.RectangleToScreen(parent.ClientRectangle);
            if (!controlBounds.IntersectsWith(viewport))
            {
                return false;
            }
        }

        return true;
    }

    public static bool HasActiveScrollAncestor(Control control)
    {
        for (Control? parent = control.Parent; parent != null; parent = parent.Parent)
        {
            if (parent is BufferedFlowLayoutPanel { IsScrollInProgress: true })
            {
                return true;
            }
        }
        return false;
    }
}

internal static class NativeWindowTheme
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int WmSetRedraw = 0x000B;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetWindowTheme(
        IntPtr windowHandle,
        string subAppName,
        string? subIdList);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(
        IntPtr windowHandle,
        IntPtr updateRectangle,
        IntPtr updateRegion,
        uint flags);

    public static IDisposable SuspendRedraw(Control control)
    {
        var isTopLevelWindow = control is Form || ReferenceEquals(control.TopLevelControl, control);
        if (!ShouldSuspendRedraw(isTopLevelWindow) ||
            !OperatingSystem.IsWindows() ||
            control.IsDisposed ||
            !control.IsHandleCreated)
        {
            return RedrawScope.Empty;
        }

        return new RedrawScope(control);
    }

    private static bool ShouldSuspendRedraw(bool isTopLevelWindow) => !isTopLevelWindow;

    internal static void ValidateRedrawPolicy()
    {
        if (ShouldSuspendRedraw(isTopLevelWindow: true) ||
            !ShouldSuspendRedraw(isTopLevelWindow: false))
        {
            throw new InvalidOperationException(
                "WM_SETREDRAW must never be sent to the top-level application window.");
        }
    }

    public static void Apply(Form form, bool dark)
    {
        if (!OperatingSystem.IsWindows() || !form.IsHandleCreated)
        {
            return;
        }

        try
        {
            var enabled = dark ? 1 : 0;
            var size = Marshal.SizeOf<int>();
            if (DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref enabled, size) != 0)
            {
                DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, size);
            }
        }
        catch
        {
            // Title-bar theming is cosmetic and must never block account management.
        }
    }

    public static void ApplyScrollable(Control control, bool dark)
    {
        if (!OperatingSystem.IsWindows() || !control.IsHandleCreated)
        {
            return;
        }

        try
        {
            _ = SetWindowTheme(
                control.Handle,
                dark ? "DarkMode_Explorer" : "Explorer",
                null);
        }
        catch
        {
            // Native scrollbar styling is cosmetic and varies between Windows builds.
        }
    }

    private sealed class RedrawScope : IDisposable
    {
        public static readonly IDisposable Empty = new RedrawScope();

        private Control? _control;
        private readonly IntPtr _windowHandle;

        private RedrawScope()
        {
        }

        public RedrawScope(Control control)
        {
            _control = control;
            _windowHandle = control.Handle;
            _ = SendMessage(_windowHandle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
        }

        public void Dispose()
        {
            var control = Interlocked.Exchange(ref _control, null);
            if (control == null || control.IsDisposed || !control.IsHandleCreated)
            {
                return;
            }

            _ = SendMessage(_windowHandle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
            _ = RedrawWindow(
                _windowHandle,
                IntPtr.Zero,
                IntPtr.Zero,
                RdwInvalidate | RdwAllChildren | RdwUpdateNow);
        }
    }
}
