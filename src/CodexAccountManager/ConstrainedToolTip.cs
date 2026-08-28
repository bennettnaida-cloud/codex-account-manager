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

        _originalCaptions.Remove(control);
        if (caption != null)
        {
            _originalCaptions.Add(control, new OriginalCaption(caption));
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

        var current = new StringBuilder(line.Length);
        foreach (var textElement in EnumerateTextElements(line))
        {
            current.Append(textElement);
            if (MeasureWidth(current, font) <= maxWidth || current.Length == textElement.Length)
            {
                continue;
            }

            // Move the element that overflowed to the next line.  This keeps all
            // content (including long email/path segments) while guaranteeing the
            // native tooltip receives a finite line width.
            current.Length -= textElement.Length;
            if (current.Length > 0)
            {
                destination.Append(current);
                destination.Append(Environment.NewLine);
            }

            current.Clear();
            current.Append(textElement);
        }

        destination.Append(current);
    }

    private static IEnumerable<string> EnumerateTextElements(string value)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            yield return enumerator.GetTextElement();
        }
    }

    private static int MeasureWidth(StringBuilder value, Font font)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        try
        {
            return TextRenderer.MeasureText(
                    value.ToString(),
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

    private sealed class OriginalCaption(string value)
    {
        internal string Value { get; } = value;
    }
}
