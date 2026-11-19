---
title: Building this site
date: 2026-11-17
summary: The generator, its Markdown, and the bugs it found.
tags: tools, intro
---
# Building this site

The generator reads `pages/*.md`, pours each into `template.html`, and
writes the index and a page of tags. Run it as:

```sh
sable run examples/ssg/ssg.sb -- examples/ssg/site out
```

## The Markdown it reads

- Headings, from `#` to `######`
- Paragraphs, _emphasis_ and **strong text**
- Lists, nested by indenting
- Quotes, rules and fenced code

![The pipeline](pipeline.png)

## Numbers

| it does not do tables, so this line is a paragraph |

Every size it reports is written by a format spec, in Sable: 1.5 KB is
`"\(1536.0 / 1024.0:.1f) KB"`.

## Building

### Again

Headings with the same text get distinct ids.

## Building
