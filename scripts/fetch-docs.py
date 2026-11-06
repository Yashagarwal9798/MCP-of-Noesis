"""Builds data/docs.json: a local, searchable copy of the public NoesisGUI documentation.

Run once:  python scripts/fetch-docs.py
The output stays on your machine (data/ is gitignored); this repo does not redistribute Noesis docs.
Pages come from https://www.noesisengine.com/docs/sitemap.xml (current version only; robots.txt is respected).
"""
import concurrent.futures
import html
import json
import os
import re
import sys
import time
import urllib.request
from html.parser import HTMLParser

BASE = "https://www.noesisengine.com/docs/"
UA = "MCP-of-Noesis-docs-indexer/0.1 (+local search index)"
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "data", "docs.json")
CHUNK_WORDS = 300


def get(url, tries=3):
    for i in range(tries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=30) as r:
                return r.read().decode("utf-8", errors="replace")
        except Exception as e:  # network hiccup: back off and retry
            if i == tries - 1:
                print(f"  skip {url}: {e}", file=sys.stderr)
                return None
            time.sleep(1.5 * (i + 1))


class DocParser(HTMLParser):
    """Collects the page title and (heading, text) sections from a Noesis docs page."""

    SKIP = {"script", "style", "nav", "header", "footer"}
    BLOCK = {"p", "li", "pre", "td", "th", "dt", "dd", "div", "br", "tr", "blockquote"}

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.title = ""
        self.sections = [["", []]]
        self._skip = 0
        self._in_title = False
        self._heading = None
        self._buf = []

    def handle_starttag(self, tag, attrs):
        if tag in self.SKIP:
            self._skip += 1
        elif tag == "title":
            self._in_title = True
        elif tag in ("h1", "h2", "h3"):
            self._heading = []
        elif tag in self.BLOCK:
            self._buf.append("\n")

    def handle_endtag(self, tag):
        if tag in self.SKIP and self._skip:
            self._skip -= 1
        elif tag == "title":
            self._in_title = False
        elif tag in ("h1", "h2", "h3") and self._heading is not None:
            self._flush()
            name = " ".join("".join(self._heading).split()).replace("¶", "").strip()
            self.sections.append([name, []])
            self._heading = None
        elif tag in self.BLOCK:
            self._buf.append("\n")

    def handle_data(self, data):
        if self._skip:
            return
        if self._in_title:
            self.title += data
        elif self._heading is not None:
            self._heading.append(data)
        else:
            self._buf.append(data)

    def _flush(self):
        text = "".join(self._buf)
        self._buf = []
        if text.strip():
            self.sections[-1][1].append(text)

    def close(self):
        super().close()
        self._flush()


def clean(text):
    """Drops the emoji badges the docs put in titles and headings."""
    text = "".join(ch for ch in text if ord(ch) < 0x2190 or 0x3000 <= ord(ch) < 0xFE00)
    return " ".join(text.split())


def chunks_for(url, page):
    p = DocParser()
    p.feed(page)
    p.close()
    title = clean(re.sub(r"\s*[-|—]\s*(NoesisGUI|Documentation).*$", "", html.unescape(p.title))) or url.rsplit("/", 1)[-1]
    out = []
    for heading, parts in p.sections:
        heading = clean(heading)
        # Inherited-member lists and hierarchy boxes repeat on every class page; they drown out real content.
        if heading == "Inheritance Hierarchy" or heading.startswith("From "):
            continue
        lines = [" ".join(l.split()) for l in "".join(parts).splitlines()]
        words = " ".join(l for l in lines if l).split()
        for i in range(0, len(words), CHUNK_WORDS):
            piece = " ".join(words[i:i + CHUNK_WORDS])
            if len(piece) > 40:
                out.append({"title": title, "section": heading or title, "url": url, "text": piece})
    return out


def main():
    sitemap = get(BASE + "sitemap.xml")
    if not sitemap:
        raise SystemExit("could not download the docs sitemap")
    urls = [u for u in re.findall(r"<loc>([^<]+)</loc>", sitemap)
            if u.startswith(BASE) and not re.match(re.escape(BASE) + r"\d", u)
            and "Changelog_v" not in u]  # old-version changelogs are noise for "how do I" questions
    print(f"{len(urls)} pages in sitemap")
    all_chunks, done = [], 0
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        for url, page in zip(urls, pool.map(get, urls)):
            done += 1
            if page:
                all_chunks.extend(chunks_for(url, page))
            if done % 50 == 0:
                print(f"  {done}/{len(urls)} pages, {len(all_chunks)} chunks")
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(all_chunks, f, ensure_ascii=False)
    print(f"wrote {len(all_chunks)} chunks to {OUT}")


if __name__ == "__main__":
    main()
