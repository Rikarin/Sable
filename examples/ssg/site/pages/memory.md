---
title: Three tiers of memory
date: 2026-04-11
summary: Values, references and regions — and when to reach for each.
tags: memory, language
---
Sable gives a program three ways to hold data:

1. **Values** live inline, copied on assignment.
2. **References** are counted: ARC frees them when the last one goes.
3. **Regions** hand out memory that is freed all at once.

## Choosing

- Small, immutable data: a `struct`.
- Shared, mutable state: a `class`, with `weak` to break cycles.
- Many short-lived nodes — a parse tree, say:
  - allocate them in a region,
  - and drop the region when the pass ends.

## Costs

Retains and releases are not free. The optimiser removes the pairs it can
prove redundant; what is left costs *about* a load and a store each.

---

Escaping matters too: 2 < 3 && "quotes" are 'fine', and \*this\* is not
emphasis.
