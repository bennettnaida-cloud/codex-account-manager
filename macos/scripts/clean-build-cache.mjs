import { readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';

const tempRoot = path.resolve(tmpdir());
const targets = (await readdir(tempRoot, { withFileTypes: true }))
  .filter((entry) => entry.isDirectory() && /^cam-macos-build-[A-Za-z0-9_-]+$/.test(entry.name))
  .map((entry) => path.resolve(tempRoot, entry.name));

for (const target of targets) {
  if (path.dirname(target) !== tempRoot || !path.basename(target).startsWith('cam-macos-build-')) {
    throw new Error(`拒绝清理非构建目录：${target}`);
  }
  await rm(target, { recursive: true, force: true });
  console.log(`已清理：${target}`);
}

if (targets.length === 0) console.log('没有残留的 macOS 临时构建目录。');
