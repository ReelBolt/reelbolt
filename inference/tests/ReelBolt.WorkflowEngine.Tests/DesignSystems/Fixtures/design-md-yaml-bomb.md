---
name: Bomb
a: &anchor
  b: *anchor
  c: &c [*anchor, *anchor]
d: &d [*c, *c]
e: &e [*d, *d, *d, *d]
---

# Bomb

## Overview

Anchors and aliases must be refused, not expanded.
