import DOMPurify from "dompurify";
import MarkdownIt from "markdown-it";
import type { DocumentMap, OutgoingLinkView } from "../types/api";

const allowedSchemes = new Set(["http:", "https:", "mailto:"]);

const escapeMarkdown = (value: string): string =>
  value.replace(/([\\[\]])/g, "\\$1");

const headingKey = (value: string): string =>
  value.normalize("NFC").trim().split(/\s+/).join(" ").toUpperCase();

const headingId = (value: string): string =>
  `cv-heading-${encodeURIComponent(headingKey(value))}`;

const readPathScheme = (value: string): string | null => {
  if (value.startsWith("/documents/")) {
    return "internal";
  }
  if (value.startsWith("#")) {
    return "fragment";
  }
  if (!/^[A-Za-z][A-Za-z0-9+.-]*:/.test(value)) {
    return null;
  }
  try {
    const parsed = new URL(value);
    return parsed.protocol;
  } catch {
    return null;
  }
};

const documentHref = (link: OutgoingLinkView): string => {
  const target = link.target!;
  const fragment = link.targetHeading
    ? `#${headingId(link.targetHeading)}`
    : "";
  return `/documents/${target.documentId}${fragment}`;
};

const replaceDocumentLinks = (
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
        link.startOffset >= 0,
    )
    .sort((left, right) => right.startOffset - left.startOffset);

  for (const link of links) {
    if (link.linkKind === "wiki") {
      if (
        link.endOffset > markdown.length ||
        link.endOffset <= link.startOffset
      ) {
        continue;
      }
      const current = markdown.slice(link.startOffset, link.endOffset);
      if (!current.startsWith("[[") || !current.endsWith("]]")) {
        continue;
      }
      const label = resolveLabel(link);
      result =
        result.slice(0, link.startOffset) +
        `[${escapeMarkdown(label)}](${documentHref(link)})` +
        result.slice(link.endOffset);
      continue;
    }

    const start = link.destinationStartOffset;
    const end = link.destinationEndOffset;
    if (
      start === null ||
      start === undefined ||
      end === null ||
      end === undefined ||
      start < 0 ||
      end > markdown.length ||
      end <= start
    ) {
      continue;
    }
    result = result.slice(0, start) + documentHref(link) + result.slice(end);
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
      const [path, fragment = ""] = href.split("#", 2);
      token.attrSet("class", "document-link");
      token.attrSet("data-document-id", path!.slice("/documents/".length));
      if (fragment) {
        token.attrSet("data-heading-key", fragment);
      }
    } else {
      token.attrSet("target", "_blank");
      token.attrSet("rel", "noopener noreferrer");
    }
    return self.renderToken(tokens, index, options);
  };

  markdown.renderer.rules.heading_open = (
    tokens,
    index,
    options,
    environment,
    self,
  ) => {
    const token = tokens[index]!;
    const inline = tokens[index + 1];
    const text = inline?.content ?? "";
    const key = headingKey(text);
    const seen =
      (environment?.seenHeadingKeys as Set<string> | undefined) ??
      new Set<string>();
    if (key && !seen.has(key)) {
      seen.add(key);
      token.attrSet("id", `cv-heading-${encodeURIComponent(key)}`);
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
  const raw = markdown.render(replaceDocumentLinks(source, map), {
    seenHeadingKeys: new Set<string>(),
  });
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
      "data-heading-key",
      "href",
      "id",
      "rel",
      "target",
      "title",
    ],
    FORBID_TAGS: ["form", "iframe", "img", "input", "script", "style"],
  });
};
