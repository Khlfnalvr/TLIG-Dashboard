using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

// Disambiguate conflicting type names between Markdig and WinUI 3.
using MdBlock   = Markdig.Syntax.Block;
using MdInline  = Markdig.Syntax.Inlines.Inline;
using XamlInline = Microsoft.UI.Xaml.Documents.Inline;

namespace TLIGDashboard.Helpers;

internal static partial class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseMathematics()
        .Build();

    // Convert \[...\] display-math and \(...\) inline-math to Markdig's $$ / $ notation.
    [GeneratedRegex(@"\\\[(.+?)\\\]", RegexOptions.Singleline)]
    private static partial Regex DisplayMathRx();

    [GeneratedRegex(@"\\\((.+?)\\\)")]
    private static partial Regex InlineMathRx();

    // Fallback: catch any $...$ that Markdig's parser didn't convert to MathInline.
    [GeneratedRegex(@"\$([^$\r\n]+?)\$")]
    private static partial Regex FallbackInlineMathRx();

    /// <summary>Parses <paramref name="markdown"/> and returns a rendered WinUI 3 UIElement.</summary>
    public static UIElement Render(string markdown, double fontSize = 13, bool isDark = false)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return Plain(markdown ?? "", fontSize);

        markdown = DisplayMathRx().Replace(markdown, m => $"\n$$\n{m.Groups[1].Value.Trim()}\n$$");
        markdown = InlineMathRx().Replace(markdown, m => $"${m.Groups[1].Value}$");

        var doc   = Markdown.Parse(markdown, Pipeline);
        var panel = new StackPanel { Spacing = 5 };

        foreach (var block in doc)
            AddBlock(block, panel, fontSize, isDark);

        return panel.Children.Count > 0 ? panel : Plain(markdown, fontSize);
    }

    // ── Block dispatch ────────────────────────────────────────────────────────

    private static void AddBlock(MdBlock block, StackPanel panel, double fs, bool dark)
    {
        UIElement? el = block switch
        {
            HeadingBlock h     => RenderHeading(h, dark),
            ParagraphBlock p   => RenderParagraph(p, fs, dark),
            // MathBlock extends FencedCodeBlock — must come first to avoid being shadowed.
            Markdig.Extensions.Mathematics.MathBlock m
                               => RenderMathBlock(m.Lines.ToString().Trim(), dark),
            FencedCodeBlock fc => RenderCodeBlock(fc.Lines.ToString().TrimEnd(), fc.Info, dark),
            CodeBlock c        => RenderCodeBlock(c.Lines.ToString().TrimEnd(), null, dark),
            ListBlock l        => RenderList(l, fs, dark),
            QuoteBlock q       => RenderQuote(q, fs, dark),
            ThematicBreakBlock => RenderHR(dark),
            Table t            => RenderTable(t, fs, dark),
            _                  => null
        };
        if (el is not null) panel.Children.Add(el);
    }

    // ── Heading ───────────────────────────────────────────────────────────────

    private static UIElement RenderHeading(HeadingBlock h, bool dark)
    {
        double[] sizes = [22, 18, 16, 15, 14, 13];
        double fs = sizes[Math.Clamp(h.Level - 1, 0, 5)];

        var rtb = new RichTextBlock
        {
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, h.Level <= 2 ? 4 : 2, 0, 2)
        };
        var para = new Paragraph { FontSize = fs, FontWeight = FontWeights.Bold };
        if (h.Inline is not null)
            foreach (var i in h.Inline) para.Inlines.Add(ToInline(i, fs, dark));
        rtb.Blocks.Add(para);

        if (h.Level > 2) return rtb;

        var sp = new StackPanel { Spacing = 0 };
        sp.Children.Add(rtb);
        sp.Children.Add(new Rectangle
        {
            Height              = 1,
            Opacity             = 0.2,
            Fill                = new SolidColorBrush(dark ? Colors.White : Colors.Black),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin              = new Thickness(0, 2, 0, 0)
        });
        return sp;
    }

    // ── Paragraph ─────────────────────────────────────────────────────────────

    private static UIElement RenderParagraph(ParagraphBlock p, double fs, bool dark)
    {
        var rtb  = new RichTextBlock { FontSize = fs, IsTextSelectionEnabled = true };
        var para = new Paragraph();
        if (p.Inline is not null)
            foreach (var i in p.Inline) para.Inlines.Add(ToInline(i, fs, dark));
        rtb.Blocks.Add(para);
        return rtb;
    }

    // ── Inline conversion ─────────────────────────────────────────────────────

    private static XamlInline ToInline(MdInline node, double fs, bool dark) =>
        node switch
        {
            // LiteralInline may still contain $...$ that Markdig's MathInlineParser skipped
            // (e.g. expressions with backslashes/braces). Split and style them here.
            LiteralInline l    => SplitLiteralMath(l.Content.ToString(), fs, dark),
            LineBreakInline lb => lb.IsHard ? (XamlInline)new LineBreak() : new Run { Text = " " },
            CodeInline ci      => new Run
            {
                Text       = ci.Content.ToString(),
                FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                Foreground = new SolidColorBrush(dark
                    ? Color.FromArgb(0xFF, 0xFF, 0xBB, 0x55)
                    : Color.FromArgb(0xFF, 0xC7, 0x25, 0x4E))
            },
            EmphasisInline em  => ToEmphasis(em, fs, dark),
            LinkInline lnk     => ToLink(lnk, fs, dark),
            Markdig.Extensions.Mathematics.MathInline mi => new Run
            {
                Text       = LatexToText(mi.Content.ToString()),
                FontFamily = new FontFamily("Cambria Math, Times New Roman"),
                Foreground = new SolidColorBrush(dark
                    ? Color.FromArgb(0xFF, 0xBB, 0xCC, 0xFF)
                    : Color.FromArgb(0xFF, 0x00, 0x50, 0xA0))
            },
            HtmlEntityInline he => new Run { Text = he.Transcoded.ToString() },
            HtmlInline          => new Run { Text = "" },
            _                   => new Run { Text = "" }
        };

    private static XamlInline ToEmphasis(EmphasisInline em, double fs, bool dark)
    {
        bool isStrike = em.DelimiterChar == '~' && em.DelimiterCount >= 2;
        bool isBold   = (em.DelimiterChar is '*' or '_') && em.DelimiterCount >= 2;
        bool isItalic = (em.DelimiterChar is '*' or '_') && em.DelimiterCount % 2 == 1;

        void Fill(Span s) { foreach (var c in em) s.Inlines.Add(ToInline(c, fs, dark)); }

        if (isStrike)
        {
            // WinUI 3 inline elements lack TextDecorations; approximate with dim foreground.
            var span = new Span
            {
                Foreground = new SolidColorBrush(dark
                    ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)
                    : Color.FromArgb(0x80, 0x00, 0x00, 0x00))
            };
            Fill(span);
            return span;
        }
        if (isBold && isItalic)
        {
            var bold   = new Bold();
            var italic = new Italic();
            foreach (var c in em) italic.Inlines.Add(ToInline(c, fs, dark));
            bold.Inlines.Add(italic);
            return bold;
        }
        if (isBold)   { var b = new Bold();   Fill(b); return b; }
        if (isItalic) { var i = new Italic(); Fill(i); return i; }
        var fb = new Span(); Fill(fb); return fb;
    }

    private static XamlInline ToLink(LinkInline link, double fs, bool dark)
    {
        var u = new Underline();
        if (link.FirstChild is not null)
            foreach (var c in link) u.Inlines.Add(ToInline(c, fs, dark));
        else
            u.Inlines.Add(new Run { Text = link.Url ?? "" });
        return u;
    }

    // ── Code block ────────────────────────────────────────────────────────────

    private static UIElement RenderCodeBlock(string code, string? lang, bool dark)
    {
        var bg = new SolidColorBrush(dark
            ? Color.FromArgb(0xFF, 0x1A, 0x1A, 0x2E)
            : Color.FromArgb(0xFF, 0xF4, 0xF4, 0xF8));

        var codeText = new TextBlock
        {
            Text                   = code,
            FontFamily             = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize               = 12,
            TextWrapping           = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
            Foreground             = new SolidColorBrush(dark
                ? Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD4)
                : Color.FromArgb(0xFF, 0x1E, 0x1E, 0x2E))
        };

        var codeArea = new Border
        {
            Background   = bg,
            CornerRadius = string.IsNullOrEmpty(lang) ? new CornerRadius(6) : new CornerRadius(0, 0, 6, 6),
            Padding      = new Thickness(12, 10, 12, 10),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
                Content                       = codeText
            }
        };

        if (string.IsNullOrEmpty(lang)) return codeArea;

        var header = new Border
        {
            Background   = new SolidColorBrush(dark
                ? Color.FromArgb(0xFF, 0x10, 0x10, 0x20)
                : Color.FromArgb(0xFF, 0xE6, 0xE6, 0xEE)),
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Padding      = new Thickness(12, 5, 12, 5),
            Child = new TextBlock
            {
                Text       = lang,
                FontSize   = 11,
                Opacity    = 0.65,
                FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New")
            }
        };

        var wrap = new StackPanel { Spacing = 0 };
        wrap.Children.Add(header);
        wrap.Children.Add(codeArea);
        return wrap;
    }

    // ── Math block ────────────────────────────────────────────────────────────

    private static UIElement RenderMathBlock(string content, bool dark) =>
        new Border
        {
            Background          = new SolidColorBrush(dark
                ? Color.FromArgb(0x28, 0x80, 0xA0, 0xFF)
                : Color.FromArgb(0x12, 0x00, 0x50, 0xA0)),
            CornerRadius        = new CornerRadius(6),
            Padding             = new Thickness(16, 10, 16, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new TextBlock
            {
                Text                   = LatexToText(content),
                FontFamily             = new FontFamily("Cambria Math, STIX Two Math, Times New Roman"),
                FontSize               = 15,
                TextWrapping           = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                HorizontalAlignment    = HorizontalAlignment.Center,
                Foreground             = new SolidColorBrush(dark
                    ? Color.FromArgb(0xFF, 0xBB, 0xCC, 0xFF)
                    : Color.FromArgb(0xFF, 0x00, 0x50, 0xA0))
            }
        };

    // ── List ──────────────────────────────────────────────────────────────────

    private static UIElement RenderList(ListBlock list, double fs, bool dark)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(4, 0, 0, 0) };
        int idx   = int.TryParse(list.OrderedStart, out int s) ? s : 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            string bullet = list.IsOrdered ? $"{idx++}." : "•";

            var bulletTb = new TextBlock
            {
                Text                   = bullet,
                FontSize               = fs,
                VerticalAlignment      = VerticalAlignment.Top,
                MinWidth               = list.IsOrdered ? 22 : 14,
                Margin                 = new Thickness(0, 0, 6, 0),
                IsTextSelectionEnabled = true
            };

            var content = new StackPanel { Spacing = 3 };
            foreach (var b in item) AddBlock(b, content, fs, dark);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(bulletTb, 0);
            Grid.SetColumn(content, 1);
            row.Children.Add(bulletTb);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    // ── Blockquote ────────────────────────────────────────────────────────────

    private static UIElement RenderQuote(QuoteBlock q, double fs, bool dark)
    {
        var inner = new StackPanel { Spacing = 4, Margin = new Thickness(10, 0, 0, 0) };
        foreach (var b in q) AddBlock(b, inner, fs, dark);

        var bar = new Rectangle
        {
            Width             = 3,
            Fill              = new SolidColorBrush(dark
                ? Color.FromArgb(0x80, 0xAA, 0xAA, 0xAA)
                : Color.FromArgb(0x80, 0x70, 0x70, 0x70)),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        var grid = new Grid { Opacity = 0.85 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(bar, 0);
        Grid.SetColumn(inner, 1);
        grid.Children.Add(bar);
        grid.Children.Add(inner);
        return grid;
    }

    // ── Horizontal rule ───────────────────────────────────────────────────────

    private static UIElement RenderHR(bool dark) =>
        new Rectangle
        {
            Height              = 1,
            Opacity             = 0.22,
            Fill                = new SolidColorBrush(dark ? Colors.White : Colors.Black),
            Margin              = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

    // ── Table ─────────────────────────────────────────────────────────────────

    private static UIElement RenderTable(Table table, double fs, bool dark)
    {
        var rows = table.OfType<TableRow>().ToList();
        if (rows.Count == 0) return Plain("[table]", fs);

        int colCount    = rows.Max(r => r.Count);
        var borderBrush = new SolidColorBrush(dark
            ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x40, 0x00, 0x00, 0x00));

        var grid = new Grid();
        for (int c = 0; c < colCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int r = 0; r < rows.Count; r++)
        {
            var row   = rows[r];
            var cells = row.OfType<TableCell>().ToList();
            for (int c = 0; c < cells.Count; c++)
            {
                var cell = cells[c];

                var rtb  = new RichTextBlock { FontSize = fs, IsTextSelectionEnabled = true };
                var para = new Paragraph
                {
                    FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal
                };
                var pb = cell.OfType<ParagraphBlock>().FirstOrDefault();
                if (pb?.Inline is not null)
                    foreach (var i in pb.Inline) para.Inlines.Add(ToInline(i, fs, dark));
                rtb.Blocks.Add(para);

                var cellBorder = new Border
                {
                    Padding         = new Thickness(8, 6, 8, 6),
                    BorderBrush     = borderBrush,
                    BorderThickness = new Thickness(0, 0,
                        c < cells.Count - 1 ? 1 : 0,
                        r < rows.Count  - 1 ? 1 : 0),
                    Background = row.IsHeader
                        ? new SolidColorBrush(dark
                            ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                            : Color.FromArgb(0x20, 0x00, 0x00, 0x00))
                        : null,
                    Child = rtb
                };

                Grid.SetRow(cellBorder, r);
                Grid.SetColumn(cellBorder, c);
                if (cell.ColumnSpan > 1) Grid.SetColumnSpan(cellBorder, cell.ColumnSpan);
                grid.Children.Add(cellBorder);
            }
        }

        return new Border
        {
            CornerRadius    = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush     = borderBrush,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Disabled,
                Content                       = grid
            }
        };
    }

    // ── Literal text with inline-math fallback ────────────────────────────────

    private static XamlInline SplitLiteralMath(string text, double fs, bool dark)
    {
        if (!text.Contains('$'))
            return new Run { Text = text };

        var span  = new Span();
        int last  = 0;
        var mathFg = new SolidColorBrush(dark
            ? Color.FromArgb(0xFF, 0xBB, 0xCC, 0xFF)
            : Color.FromArgb(0xFF, 0x00, 0x50, 0xA0));
        var mathFont = new FontFamily("Cambria Math, Times New Roman");

        foreach (System.Text.RegularExpressions.Match m in FallbackInlineMathRx().Matches(text))
        {
            if (m.Index > last)
                span.Inlines.Add(new Run { Text = text[last..m.Index] });

            span.Inlines.Add(new Run
            {
                Text       = LatexToText(m.Groups[1].Value),
                FontFamily = mathFont,
                Foreground = mathFg
            });
            last = m.Index + m.Length;
        }

        if (last == 0) return new Run { Text = text };  // no matches at all

        if (last < text.Length)
            span.Inlines.Add(new Run { Text = text[last..] });

        return span;
    }

    // ── Plain text fallback ───────────────────────────────────────────────────

    private static UIElement Plain(string text, double fs) =>
        new TextBlock
        {
            Text                   = text,
            FontSize               = fs,
            TextWrapping           = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };

    // ── LaTeX → Unicode text converter ───────────────────────────────────────
    // Converts common LaTeX commands to readable Unicode equivalents so math
    // renders clearly in native WinUI 3 text elements.

    internal static string LatexToText(string src)
    {
        if (string.IsNullOrEmpty(src)) return src;
        var sb = new System.Text.StringBuilder(src.Length + 4);
        int i  = 0;
        while (i < src.Length)
        {
            char c = src[i];
            switch (c)
            {
                case '\\':
                    i++;
                    if (i >= src.Length) break;

                    // Single non-letter escapes
                    if (!char.IsLetter(src[i]))
                    {
                        char sc = src[i++];
                        switch (sc)
                        {
                            case '\\': sb.Append('\n'); break;
                            case ',': case ';': case ':': case '!': sb.Append(' '); break;
                            case '(': sb.Append('('); break;
                            case ')': sb.Append(')'); break;
                            case '[': sb.Append('['); break;
                            case ']': sb.Append(']'); break;
                            case '{': sb.Append('{'); break;
                            case '}': sb.Append('}'); break;
                            default:  sb.Append('\\'); sb.Append(sc); break;
                        }
                        break;
                    }

                    // Read alphabetic command name
                    int cs = i;
                    while (i < src.Length && char.IsLetter(src[i])) i++;
                    string cmd = src[cs..i];
                    if (i < src.Length && src[i] == ' ') i++; // absorb trailing space

                    switch (cmd)
                    {
                        // ── Structural ────────────────────────────────────────
                        case "frac":
                        {
                            string num = ReadBraced(src, ref i);
                            string den = ReadBraced(src, ref i);
                            string nt  = LatexToText(num), dt = LatexToText(den);
                            sb.Append(nt.Length == 1 && dt.Length == 1
                                ? $"{nt}⁄{dt}"          // Unicode fraction slash ⁄
                                : $"({nt})⁄({dt})");
                            break;
                        }
                        case "sqrt":
                        {
                            string deg = "";
                            if (i < src.Length && src[i] == '[')
                            {
                                i++;
                                int cl = src.IndexOf(']', i);
                                if (cl >= 0) { deg = src[i..cl]; i = cl + 1; }
                            }
                            string inner = i < src.Length && src[i] == '{'
                                ? ReadBraced(src, ref i)
                                : (i < src.Length ? src[i++].ToString() : "");
                            string it = LatexToText(inner);
                            sb.Append(string.IsNullOrEmpty(deg)
                                ? $"√({it})"
                                : $"{ToSuperscript(deg)}√({it})");
                            break;
                        }
                        case "left": case "right":
                        {
                            if (i < src.Length)
                            {
                                if (src[i] == '\\') // \left\{ etc.
                                {
                                    i++;
                                    if (i < src.Length)
                                    {
                                        char br = src[i++];
                                        if (br is not '.') sb.Append(br);
                                    }
                                }
                                else
                                {
                                    char br = src[i++];
                                    if (br is not '.') sb.Append(br);
                                }
                            }
                            break;
                        }
                        case "text": case "mathrm": case "mathbf": case "mathit":
                        case "mathbb": case "mathcal": case "mathsf": case "operatorname":
                            sb.Append(LatexToText(ReadBraced(src, ref i)));
                            break;
                        case "hat": case "bar": case "vec": case "dot": case "ddot":
                        case "tilde": case "widehat": case "widetilde":
                        case "overline": case "underline":
                            sb.Append(LatexToText(ReadBraced(src, ref i)));
                            break;
                        case "begin": case "end":
                            ReadBraced(src, ref i); // skip env name
                            break;

                        // ── Greek (lowercase) ─────────────────────────────────
                        case "alpha":    sb.Append('α'); break;
                        case "beta":     sb.Append('β'); break;
                        case "gamma":    sb.Append('γ'); break;
                        case "delta":    sb.Append('δ'); break;
                        case "epsilon": case "varepsilon": sb.Append('ε'); break;
                        case "zeta":     sb.Append('ζ'); break;
                        case "eta":      sb.Append('η'); break;
                        case "theta": case "vartheta": sb.Append('θ'); break;
                        case "iota":     sb.Append('ι'); break;
                        case "kappa":    sb.Append('κ'); break;
                        case "lambda":   sb.Append('λ'); break;
                        case "mu":       sb.Append('μ'); break;
                        case "nu":       sb.Append('ν'); break;
                        case "xi":       sb.Append('ξ'); break;
                        case "pi":       sb.Append('π'); break;
                        case "rho": case "varrho":  sb.Append('ρ'); break;
                        case "sigma": case "varsigma": sb.Append('σ'); break;
                        case "tau":      sb.Append('τ'); break;
                        case "upsilon":  sb.Append('υ'); break;
                        case "phi": case "varphi":  sb.Append('φ'); break;
                        case "chi":      sb.Append('χ'); break;
                        case "psi":      sb.Append('ψ'); break;
                        case "omega":    sb.Append('ω'); break;
                        // ── Greek (uppercase) ─────────────────────────────────
                        case "Gamma":   sb.Append('Γ'); break;
                        case "Delta":   sb.Append('Δ'); break;
                        case "Theta":   sb.Append('Θ'); break;
                        case "Lambda":  sb.Append('Λ'); break;
                        case "Xi":      sb.Append('Ξ'); break;
                        case "Pi":      sb.Append('Π'); break;
                        case "Sigma":   sb.Append('Σ'); break;
                        case "Upsilon": sb.Append('Υ'); break;
                        case "Phi":     sb.Append('Φ'); break;
                        case "Psi":     sb.Append('Ψ'); break;
                        case "Omega":   sb.Append('Ω'); break;
                        // ── Operators ─────────────────────────────────────────
                        case "times":   sb.Append('×'); break;
                        case "div":     sb.Append('÷'); break;
                        case "pm":      sb.Append('±'); break;
                        case "mp":      sb.Append('∓'); break;
                        case "cdot":    sb.Append('·'); break;
                        case "leq": case "le": sb.Append('≤'); break;
                        case "geq": case "ge": sb.Append('≥'); break;
                        case "neq": case "ne": sb.Append('≠'); break;
                        case "approx":  sb.Append('≈'); break;
                        case "sim":     sb.Append('∼'); break;
                        case "equiv":   sb.Append('≡'); break;
                        case "infty":   sb.Append('∞'); break;
                        case "sum":     sb.Append('∑'); break;
                        case "prod":    sb.Append('∏'); break;
                        case "int":     sb.Append('∫'); break;
                        case "partial": sb.Append('∂'); break;
                        case "nabla":   sb.Append('∇'); break;
                        case "forall":  sb.Append('∀'); break;
                        case "exists":  sb.Append('∃'); break;
                        case "in":      sb.Append('∈'); break;
                        case "notin":   sb.Append('∉'); break;
                        case "subset":  sb.Append('⊂'); break;
                        case "subseteq":sb.Append('⊆'); break;
                        case "cup":     sb.Append('∪'); break;
                        case "cap":     sb.Append('∩'); break;
                        case "rightarrow": case "to": sb.Append('→'); break;
                        case "leftarrow":            sb.Append('←'); break;
                        case "leftrightarrow":       sb.Append('↔'); break;
                        case "Rightarrow":           sb.Append('⇒'); break;
                        case "Leftarrow":            sb.Append('⇐'); break;
                        case "Leftrightarrow":       sb.Append('⇔'); break;
                        case "cdots":   sb.Append('⋯'); break;
                        case "ldots":   sb.Append('…'); break;
                        case "vdots":   sb.Append('⋮'); break;
                        case "ddots":   sb.Append('⋱'); break;
                        // ── Spacing ───────────────────────────────────────────
                        case "quad":    sb.Append("  "); break;
                        case "qquad":   sb.Append("    "); break;
                        // ── Unknown → process braced arg if present ───────────
                        default:
                            if (i < src.Length && src[i] == '{')
                                sb.Append(LatexToText(ReadBraced(src, ref i)));
                            break;
                    }
                    break;

                case '^':
                    i++;
                    sb.Append(ToSuperscript(LatexToText(ReadArg(src, ref i))));
                    break;

                case '_':
                    i++;
                    sb.Append(ToSubscript(LatexToText(ReadArg(src, ref i))));
                    break;

                case '{':
                    sb.Append(LatexToText(ReadBraced(src, ref i)));
                    break;

                case '}':
                    i++;  // orphan closing brace
                    break;

                default:
                    sb.Append(c);
                    i++;
                    break;
            }
        }
        return sb.ToString();
    }

    private static string ReadArg(string src, ref int i)
    {
        if (i >= src.Length) return "";
        if (src[i] == '{') return ReadBraced(src, ref i);
        return src[i++].ToString();
    }

    private static string ReadBraced(string src, ref int i)
    {
        if (i >= src.Length || src[i] != '{') return "";
        i++;  // skip '{'
        int depth = 1, start = i;
        while (i < src.Length && depth > 0)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}') depth--;
            i++;
        }
        // i is now one past the closing '}'
        return depth == 0 ? src[start..(i - 1)] : src[start..];
    }

    private static string ToSuperscript(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(c switch
            {
                '0' => '⁰', '1' => '¹', '2' => '²', '3' => '³', '4' => '⁴',
                '5' => '⁵', '6' => '⁶', '7' => '⁷', '8' => '⁸', '9' => '⁹',
                '+' => '⁺', '-' => '⁻', '=' => '⁼', '(' => '⁽', ')' => '⁾',
                'a' => 'ᵃ', 'b' => 'ᵇ', 'c' => 'ᶜ', 'd' => 'ᵈ', 'e' => 'ᵉ',
                'f' => 'ᶠ', 'g' => 'ᵍ', 'h' => 'ʰ', 'i' => 'ⁱ', 'j' => 'ʲ',
                'k' => 'ᵏ', 'l' => 'ˡ', 'm' => 'ᵐ', 'n' => 'ⁿ', 'o' => 'ᵒ',
                'p' => 'ᵖ', 'r' => 'ʳ', 's' => 'ˢ', 't' => 'ᵗ', 'u' => 'ᵘ',
                'v' => 'ᵛ', 'w' => 'ʷ', 'x' => 'ˣ', 'y' => 'ʸ', 'z' => 'ᶻ',
                _ => c
            });
        return sb.ToString();
    }

    private static string ToSubscript(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(c switch
            {
                '0' => '₀', '1' => '₁', '2' => '₂', '3' => '₃', '4' => '₄',
                '5' => '₅', '6' => '₆', '7' => '₇', '8' => '₈', '9' => '₉',
                '+' => '₊', '-' => '₋', '=' => '₌', '(' => '₍', ')' => '₎',
                'a' => 'ₐ', 'e' => 'ₑ', 'o' => 'ₒ', 'x' => 'ₓ', 'n' => 'ₙ',
                'i' => 'ᵢ', 'j' => 'ⱼ', 'r' => 'ᵣ', 'u' => 'ᵤ', 'v' => 'ᵥ',
                _ => c
            });
        return sb.ToString();
    }
}
