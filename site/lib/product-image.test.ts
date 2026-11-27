import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';

import { PRODUCT_IMAGE_PATH, productImageUrl } from './product-image.ts';

test('the product image is an absolute URL', () => {
  assert.equal(productImageUrl('https://reelbolt.ai'), 'https://reelbolt.ai/favicon-512.png');
});

test('the product image file ships in public/', () => {
  assert.ok(existsSync(new URL(`../public${PRODUCT_IMAGE_PATH}`, import.meta.url)));
});

test('the Product-like schema (SoftwareApplication) sets image, and no Product node is emitted', () => {
  const src = readFileSync(new URL('./structured-data.ts', import.meta.url), 'utf8');
  // A Product with offers is a merchant snippet that wants review/aggregateRating we do not have.
  assert.doesNotMatch(src, /'@type': 'Product'/);
  assert.doesNotMatch(src, /^\s*(aggregateRating|review):/m);
  const nodes = src.match(/'@type': '(Product|SoftwareApplication)'[\s\S]*?\n  \};/g) ?? [];
  assert.equal(nodes.length, 1);
  for (const node of nodes) assert.match(node, /image: productImageUrl\(siteConfig\.url\)/);
});
