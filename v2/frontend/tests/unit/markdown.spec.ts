import { describe, expect, it } from "vitest";
import { renderMarkdown } from "../../src/markdown/renderer";
import type { DocumentMap } from "../../src/types/api";

describe("Markdown renderer", () => {
  it("does not execute raw HTML or load remote images", () => {
    const html = renderMarkdown(
      "<script>alert(1)</script>\n\n![remote](https://example.com/a.png)\n\n**safe**",
    );

    expect(html).not.toContain("<script");
    expect(html).not.toContain("<img");
    expect(html).not.toContain("src=");
    expect(html).toContain("markdown-image-placeholder");
    expect(html).toContain("<strong>safe</strong>");
  });

  it("rejects unsafe link schemes and hardens external links", () => {
    const unsafe = renderMarkdown("[bad](javascript:alert(1))");
    const external = renderMarkdown("[site](https://example.com)");

    expect(unsafe).not.toContain("href=");
    expect(external).toContain('target="_blank"');
    expect(external).toContain('rel="noopener noreferrer"');
  });

  it("uses backend WikiLink resolution instead of parsing locally", () => {
    const map = {
      outgoingLinks: [
        {
          rawTarget: "Target",
          displayText: "Target",
          targetHeading: null,
          resolutionStatus: "resolved",
          target: {
            documentId: "00000000-0000-4000-8000-000000000001",
            title: "Target",
            virtualPath: "Target.md",
          },
          candidates: [],
          startOffset: 0,
          endOffset: 10,
        },
      ],
    } as DocumentMap;

    const html = renderMarkdown("[[Target]] body", map);

    expect(html).toContain('class="wiki-link"');
    expect(html).toContain(
      'data-document-id="00000000-0000-4000-8000-000000000001"',
    );
    expect(html).toContain(
      'href="/documents/00000000-0000-4000-8000-000000000001"',
    );
  });
});
