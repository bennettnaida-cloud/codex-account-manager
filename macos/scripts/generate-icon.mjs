import { readFile, writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import png2icons from 'png2icons';

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const macosDirectory = path.resolve(scriptDirectory, '..');
const source = path.resolve(macosDirectory, '..', 'assets', 'CodexAccountManager.png');
const destinationDirectory = path.join(macosDirectory, 'assets');
const destination = path.join(destinationDirectory, 'AppIcon.icns');
const png = await readFile(source);
const icns = png2icons.createICNS(png, png2icons.BILINEAR, 0);
if (!icns) throw new Error('无法从 PNG 生成 AppIcon.icns');
await mkdir(destinationDirectory, { recursive: true });
await writeFile(destination, icns);
console.log(destination);
