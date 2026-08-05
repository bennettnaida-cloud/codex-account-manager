const assert = require('node:assert/strict');
const test = require('node:test');

const {
  compareVersions,
  normalizeVersion,
} = require('../src/services/update-service');

test('update versions use numeric ordering and tolerate a leading v', () => {
  assert.equal(normalizeVersion('v2.0.12'), '2.0.12');
  assert.equal(compareVersions('2.0.12', '2.0.11'), 1);
  assert.equal(compareVersions('2.0.12', '2.0.12'), 0);
  assert.equal(compareVersions('2.0.11', '2.0.12'), -1);
});

test('invalid update versions are ignored by the comparison helper', () => {
  assert.equal(normalizeVersion('latest'), null);
  assert.equal(compareVersions('latest', '1.1.5'), -1);
});
