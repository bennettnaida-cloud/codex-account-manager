using System.Runtime.CompilerServices;
using System.Text;
using System.Globalization;

namespace CodexAccountManager;

/// <summary>
/// A ToolTip that keeps long diagnostic text inside the owning window.
///
/// WinForms delegates the native tooltip layout to the shell.  On some Windows
/// builds a long, unbroken string is therefore rendered as one very wide line,
/// which can cover the rest of the application (especially for account paths and
/// the fingerprint-forwarding explanation).  We retain the original caption for
/// accessibility and callers of GetToolTip, while giving the native control a
/// conservatively wrapped presentation string.
/// </summary>
internal sealed class ConstrainedToolTip : ToolTip
{
    private const int DefaultMaximumWidth = 620;
    private const int MaximumWidth = 680;
    private const int WindowHorizontalMargin = 32;

    private readonly ConditionalWeakTable<Control, OriginalCaption> _originalCaptions = new();

    public ConstrainedToolTip()
    {
        // Keep the same interaction defaults used by the manager's existing tips.
        AutoPopDelay = 30000;
        InitialDelay = 450;
        ReshowDelay = 150;
        ShowAlways = true;
    }

    /// <summary>
    /// Stores the unmodified caption for accessibility/tests and wraps only the
    /// string sent to the native tooltip window.
    /// </summary>
    public new void SetToolTip(Control control, string? caption)
    {
        ArgumentNullException.ThrowIfNull(control);

        var width = CalculateMaximumWidth(control);
        var font = control.Font;
        if (_originalCaptions.TryGetValue(control, out var previous) &&
            previous.Value == caption && previous.Width == width &&
            previous.FontName == font.Name && previous.FontSize == font.Size &&
            previous.FontStyle == font.Style && previous.Dpi == control.DeviceDpi)
            return;
        _originalCaptions.Remove(control);
        if (caption != null)
        {
            _originalCaptions.Add(control, new OriginalCaption(caption, width, font.Name, font.Size, font.Style, control.DeviceDpi));
        }

        base.SetToolTip(control, caption == null ? null : WrapForDisplay(control, caption));
    }

    /// <summary>
    /// Returns the complete, unwrapped caption.  This is intentional: copying a
    /// tooltip to another control must never copy a presentation-only line break.
    /// </summary>
    public new string? GetToolTip(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return _originalCaptions.TryGetValue(control, out var caption)
            ? caption.Value
            : base.GetToolTip(control);
    }

    private static string WrapForDisplay(Control control, string caption)
    {
        if (string.IsNullOrEmpty(caption))
        {
            return caption;
        }

        var maxWidth = CalculateMaximumWidth(control);
        Font? font = null;
        try
        {
            font = control.Font;
        }
        catch
        {
            font = SystemFonts.MessageBoxFont!;
        }
        var safeFont = font ?? SystemFonts.MessageBoxFont!;

        // Preserve explicit line breaks while also wrapping long paths, URLs and
        // Chinese prose that contain no whitespace boundaries.
        var normalized = caption.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var wrapped = new StringBuilder(caption.Length + Math.Max(0, lines.Length - 1) * 2);
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                wrapped.Append(Environment.NewLine);
            }

            AppendWrappedLine(wrapped, lines[index], safeFont, maxWidth);
        }

        return wrapped.ToString();
    }

    private static void AppendWrappedLine(StringBuilder destination, string line, Font font, int maxWidth)
    {
        if (line.Length == 0)
        {
            return;
        }

        // Fast path: most tooltips fit within a single line and need only one GDI measurement.
        if (MeasureWidth(line, font) <= maxWidth)
        {
            destination.Append(line);
            return;
        }

        var elementStarts = StringInfo.ParseCombiningCharacters(line);
        var elementCount = elementStarts.Length;
        if (elementCount <= 1)
        {
            destination.Append(line);
            return;
        }

        var startElement = 0;
        while (startElement < elementCount)
        {
            var charStart = elementStarts[startElement];
            if (startElement == elementCount - 1)
            {
                destination.Append(line.AsSpan(charStart));
                break;
            }

            if (startElement > 0 && MeasureWidth(line.Substring(charStart), font) <= maxWidth)
            {
                destination.Append(line.AsSpan(charStart));
                break;
            }

            var low = startElement + 1;
            var high = elementCount - 1;
            var best = startElement + 1;
            while (low <= high)
            {
                var mid = low + ((high - low) / 2);
                var charEnd = elementStarts[mid];
                if (MeasureWidth(line.Substring(charStart, charEnd - charStart), font) <= maxWidth)
                {
                    best = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            destination.Append(line.AsSpan(charStart, elementStarts[best] - charStart));
            destination.Append(Environment.NewLine);
            startElement = best;
        }
    }

    private static int MeasureWidth(string value, Font font)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        try
        {
            return TextRenderer.MeasureText(
                    value,
                    font,
                    new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.SingleLine |
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix)
                .Width;
        }
        catch
        {
            // Tooltip creation can race disposal while a page is being rebuilt.
            // A conservative character estimate still prevents an unbounded line.
            return value.Length * 16;
        }
    }

    private static int CalculateMaximumWidth(Control control)
    {
        var width = 0;
        try
        {
            var form = control.FindForm();
            width = form?.ClientSize.Width ?? 0;
            if (width <= 0 && control.TopLevelControl != null)
            {
                width = control.TopLevelControl.ClientSize.Width;
            }

            if (width <= 0 && control.IsHandleCreated)
            {
                width = Screen.FromControl(control).WorkingArea.Width;
            }
        }
        catch
        {
            // Use the safe fallback below when a control is being disposed.
        }

        if (width <= 0)
        {
            return DefaultMaximumWidth;
        }

        // The result is always narrower than the owner window.  The upper cap
        // prevents a very wide monitor from recreating the original giant tooltip.
        return Math.Min(MaximumWidth, Math.Max(1, width - WindowHorizontalMargin));
    }

    private sealed class OriginalCaption(string value, int width, string fontName, float fontSize, FontStyle fontStyle, int dpi)
    {
        internal string Value { get; } = value;
        internal int Width { get; } = width;
        internal string FontName { get; } = fontName;
        internal float FontSize { get; } = fontSize;
        internal FontStyle FontStyle { get; } = fontStyle;
        internal int Dpi { get; } = dpi;
    }
}
