import DOMPurify from "dompurify";
import MarkdownIt from "markdown-it";
import type { DocumentMap, OutgoingLinkView } from "../types/api";

const allowedSchemes = new Set(["http:", "https:", "mailto:"]);

const escapeMarkdown = (value: string): string =>
  value.replace(/([\\[\]])/g, "\\$1");

const readPathScheme = (value: string): string | null => {
  if (value.startsWith("/documents/")) {
    return "internal";
  }
  try {
    const parsed = new URL(value, window.location.origin);
    return parsed.protocol;
  } catch {
    return null;
  }
};

const replaceWikiLinks = (
  markdown: string,
  map: DocumentMap | null,
): string => {
  if (!map?.outgoingLinks.length) {
    return markdown;
  }
  let result = markdown;
  const links = [...map.outgoingLinks]
    .filter(
      (link) =>
        link.resolutionStatus === "resolved" &&
        link.target &&
        link.startOffset >= 0 &&
        link.endOffset <= markdown.length &&
        link.endOffset > link.startOffset,
    )
    .sort((left, right) => right.startOffset - left.startOffset);

  for (const link of links) {
    const current = result.slice(link.startOffset, link.endOffset);
    if (!current.startsWith("[[") || !current.endsWith("]]")) {
      continue;
    }
    const target = link.target!;
    const label = resolveLabel(link);
    result =
      result.slice(0, link.startOffset) +
      `[${escapeMarkdown(label)}](/documents/${target.documentId})` +
      result.slice(link.endOffset);
  }
  return result;
};

const resolveLabel = (link: OutgoingLinkView): string => {
  if (
    link.displayText &&
    link.displayText !== link.rawTarget &&
    !link.displayText.startsWith(link.rawTarget)
  ) {
    return link.displayText;
  }
  return link.target?.title ?? link.rawTarget;
};

const createMarkdown = () => {
  const markdown = new MarkdownIt({
    html: false,
    linkify: false,
    breaks: true,
    typographer: false,
  });

  markdown.validateLink = (url: string): boolean => {
    const scheme = readPathScheme(url);
    return (
      scheme === "internal" || (scheme !== null && allowedSchemes.has(scheme))
    );
  };

  markdown.renderer.rules.image = (tokens, index) => {
    const token = tokens[index]!;
    const alt = token.content || "图片";
    const sourceValue = token.attrGet("src");
    const source = sourceValue === null ? "" : String(sourceValue);
    return `<span class="markdown-image-placeholder">[图片：${escapeHtml(alt)}] ${escapeHtml(source)}</span>`;
  };

  markdown.renderer.rules.link_open = (
    tokens,
    index,
    options,
    _environment,
    self,
  ) => {
    const token = tokens[index]!;
    const hrefValue = token.attrGet("href");
    const href = hrefValue === null ? "" : String(hrefValue);
    if (href.startsWith("/documents/")) {
      token.attrSet("class", "wiki-link");
      token.attrSet("data-document-id", href.slice("/documents/".length));
    } else {
      token.attrSet("target", "_blank");
      token.attrSet("rel", "noopener noreferrer");
    }
    return self.renderToken(tokens, index, options);
  };

  return markdown;
};

const escapeHtml = (value: string): string =>
  value
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");

const markdown = createMarkdown();

export const renderMarkdown = (
  source: string,
  map: DocumentMap | null = null,
): string => {
  const raw = markdown.render(replaceWikiLinks(source, map));
  return DOMPurify.sanitize(raw, {
    ALLOWED_TAGS: [
      "a",
      "blockquote",
      "br",
      "code",
      "del",
      "em",
      "h1",
      "h2",
      "h3",
      "h4",
      "h5",
      "h6",
      "hr",
      "li",
      "ol",
      "p",
      "pre",
      "span",
      "strong",
      "table",
      "tbody",
      "td",
      "th",
      "thead",
      "tr",
      "ul",
    ],
    ALLOWED_ATTR: [
      "class",
      "data-document-id",
      "href",
      "rel",
      "target",
      "title",
    ],
    FORBID_TAGS: ["form", "iframe", "img", "input", "script", "style"],
  });
};
