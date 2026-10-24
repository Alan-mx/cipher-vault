using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using CipherVault.Core.Documents;

namespace CipherVault.Ui.Wpf;

internal static class MarkdownWpfRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();

    public static FlowDocument Render(
        string markdown,
        IReadOnlyList<VaultOutgoingLink> links,
        Action<string> navigate)
    {
        var prepared = ReplaceWikiLinks(markdown, links);
        var document = Markdown.Parse(prepared, Pipeline);
        var flow = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(0),
            LineHeight = 22
        };
        RenderBlocks(document, flow.Blocks, navigate);
        return flow;
    }

    private static string ReplaceWikiLinks(
        string markdown,
        IReadOnlyList<VaultOutgoingLink> links)
    {
        var result = markdown;
        for (var index = links.Count - 1; index >= 0; index--)
        {
            var link = links[index];
            if (link.StartOffset < 0 ||
                link.EndOffset > result.Length ||
                link.EndOffset <= link.StartOffset)
            {
                continue;
            }
            var uri = link.Target is null
                ? $"ciphervault://unresolved/{index}"
                : $"ciphervault://document/{link.Target.DocumentId:D}";
            var display = link.DisplayText
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("[", "\\[", StringComparison.Ordinal)
                .Replace("]", "\\]", StringComparison.Ordinal);
            result = string.Concat(
                result.AsSpan(0, link.StartOffset),
                $"[{display}]({uri})",
                result.AsSpan(link.EndOffset));
        }
        return result;
    }

    private static void RenderBlocks(
        ContainerBlock container,
        BlockCollection target,
        Action<string> navigate)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var headingParagraph = new Paragraph
                    {
                        FontSize = Math.Max(16, 25 - heading.Level * 2),
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 12, 0, 6)
                    };
                    if (heading.Inline is not null)
                    {
                        RenderInlines(heading.Inline, headingParagraph.Inlines, navigate);
                    }
                    target.Add(headingParagraph);
                    break;
                case ParagraphBlock paragraph:
                    var renderedParagraph = new Paragraph
                    {
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    if (paragraph.Inline is not null)
                    {
                        RenderInlines(
                            paragraph.Inline,
                            renderedParagraph.Inlines,
                            navigate);
                    }
                    target.Add(renderedParagraph);
                    break;
                case ListBlock list:
                    var renderedList = new List
                    {
                        MarkerStyle = list.IsOrdered
                            ? TextMarkerStyle.Decimal
                            : TextMarkerStyle.Disc,
                        Margin = new Thickness(22, 0, 0, 10)
                    };
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var renderedItem = new ListItem();
                        RenderBlocks(item, renderedItem.Blocks, navigate);
                        renderedList.ListItems.Add(renderedItem);
                    }
                    target.Add(renderedList);
                    break;
                case CodeBlock code:
                    target.Add(CreateCodeParagraph(code.Lines.ToString()));
                    break;
                case HtmlBlock html:
                    target.Add(CreateCodeParagraph(html.Lines.ToString()));
                    break;
                case ThematicBreakBlock:
                    target.Add(new Paragraph
                    {
                        BorderBrush = Brushes.LightGray,
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Margin = new Thickness(0, 12, 0, 12)
                    });
                    break;
                case QuoteBlock quote:
                    var quoteSection = new Section
                    {
                        Foreground = Brushes.DimGray,
                        FontStyle = FontStyles.Italic,
                        Margin = new Thickness(16, 0, 0, 10)
                    };
                    RenderBlocks(quote, quoteSection.Blocks, navigate);
                    target.Add(quoteSection);
                    break;
                case ContainerBlock nested:
                    RenderBlocks(nested, target, navigate);
                    break;
                case LeafBlock leaf when leaf.Lines.Count > 0:
                    target.Add(new Paragraph(
                        new Run(leaf.Lines.ToString())));
                    break;
            }
        }
    }

    private static Paragraph CreateCodeParagraph(string content)
    {
        return new Paragraph(new Run(content))
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Background = new SolidColorBrush(Color.FromRgb(245, 247, 250)),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 10)
        };
    }

    private static void RenderInlines(
        ContainerInline container,
        InlineCollection target,
        Action<string> navigate)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        Background = new SolidColorBrush(
                            Color.FromRgb(245, 247, 250))
                    });
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterCount >= 2)
                    {
                        span.FontWeight = FontWeights.Bold;
                    }
                    else
                    {
                        span.FontStyle = FontStyles.Italic;
                    }
                    RenderInlines(emphasis, span.Inlines, navigate);
                    target.Add(span);
                    break;
                case LinkInline link:
                    var hyperlink = new Hyperlink();
                    if (link.Url is not null)
                    {
                        hyperlink.Tag = link.Url;
                        hyperlink.Click += (_, args) =>
                        {
                            args.Handled = true;
                            navigate(link.Url);
                        };
                    }
                    RenderInlines(link, hyperlink.Inlines, navigate);
                    target.Add(hyperlink);
                    break;
                case AutolinkInline autolink:
                    var automatic = new Hyperlink(new Run(autolink.Url))
                    {
                        Tag = autolink.Url
                    };
                    automatic.Click += (_, args) =>
                    {
                        args.Handled = true;
                        navigate(autolink.Url);
                    };
                    target.Add(automatic);
                    break;
                case HtmlInline html:
                    target.Add(new Run(html.Tag));
                    break;
                case ContainerInline nested:
                    RenderInlines(nested, target, navigate);
                    break;
            }
        }
    }
}
