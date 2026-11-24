import assert from 'node:assert/strict';
import { test } from 'node:test';
import { toMinorUnits } from '../lib/money.mjs';

test('whole and fractional major units convert with integer math', () => {
  assert.equal(toMinorUnits(29, 'USD'), 2900);
  assert.equal(toMinorUnits(105, 'usd'), 10500);
  assert.equal(toMinorUnits(18, 'USD'), 1800);
  assert.equal(toMinorUnits(0.5, 'USD'), 50);
  assert.equal(toMinorUnits('12.34', 'USD'), 1234);
  assert.equal(toMinorUnits(19.99, 'USD'), 1999);
  assert.equal(toMinorUnits(1.1, 'USD'), 110);
});

test('zero-decimal currencies use exponent 0', () => {
  assert.equal(toMinorUnits(500, 'JPY'), 500);
  assert.throws(() => toMinorUnits(0.5, 'JPY'), /not representable/);
});

test('amounts that are not exactly representable are rejected', () => {
  assert.throws(() => toMinorUnits(0.001, 'USD'), /not representable|plain/);
  assert.throws(() => toMinorUnits('10.005', 'USD'), /not representable/);
  assert.throws(() => toMinorUnits(1e-7, 'USD'), /plain decimal/);
});

test('zero, negative and junk are rejected', () => {
  assert.throws(() => toMinorUnits(0, 'USD'), /greater than zero/);
  assert.throws(() => toMinorUnits(-5, 'USD'), /plain positive decimal/);
  assert.throws(() => toMinorUnits('abc', 'USD'), /plain positive decimal/);
  assert.throws(() => toMinorUnits(Number.NaN, 'USD'), /not finite/);
  assert.throws(() => toMinorUnits(5, 'US'), /invalid currency/);
  assert.throws(() => toMinorUnits(5, 'KWD'), /three decimals/);
});
