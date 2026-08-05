import assert from "node:assert/strict";
import fs from "node:fs/promises";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const assetRoot = path.resolve(here, "..", "assets");
const rendererPath = path.join(assetRoot, "renderer-inject.js");
const cssPath = path.join(assetRoot, "dream-skin.css");

class FakeClassList {
  constructor(initial = []) { this.values = new Set(initial); }
  add(...values) { values.forEach((value) => this.values.add(value)); }
  remove(...values) { values.forEach((value) => this.values.delete(value)); }
  contains(value) { return this.values.has(value); }
  toggle(value, force) {
    const enabled = force === undefined ? !this.values.has(value) : Boolean(force);
    if (enabled) this.values.add(value);
    else this.values.delete(value);
    return enabled;
  }
  toString() { return [...this.values].join(" "); }
}

class FakeStyle {
  constructor() { this.properties = new Map(); }
  setProperty(name, value) { this.properties.set(name, String(value)); }
  removeProperty(name) { this.properties.delete(name); }
  getPropertyValue(name) { return this.properties.get(name) ?? ""; }
}

class FakeGradient {
  constructor() { this.stops = []; }
  addColorStop(offset, color) { this.stops.push([offset, color]); }
}

class FakeCanvasContext {
  constructor() { this.drawCalls = 0; }
  save() {}
  restore() {}
  setTransform() {}
  clearRect() { this.drawCalls += 1; }
  beginPath() {}
  moveTo() {}
  lineTo() {}
  translate() {}
  rotate() {}
  ellipse() {}
  arc() {}
  stroke() { this.drawCalls += 1; }
  fill() { this.drawCalls += 1; }
  setLineDash() {}
  createLinearGradient() { return new FakeGradient(); }
  createRadialGradient() { return new FakeGradient(); }
}

class FakeElement {
  constructor(tagName, ownerDocument) {
    this.tagName = tagName.toUpperCase();
    this.ownerDocument = ownerDocument;
    this.id = "";
    this.parentElement = null;
    this.children = [];
    this.classList = new FakeClassList();
    this.style = new FakeStyle();
    this.dataset = {};
    this.attributes = new Map();
    this.width = 0;
    this.height = 0;
    this.textContent = "";
  }
  get className() { return this.classList.toString(); }
  get isConnected() {
    return this === this.ownerDocument.documentElement || Boolean(this.parentElement?.isConnected);
  }
  appendChild(child) {
    child.remove();
    child.parentElement = this;
    this.children.push(child);
    return child;
  }
  remove() {
    if (!this.parentElement) return;
    const siblings = this.parentElement.children;
    const index = siblings.indexOf(this);
    if (index >= 0) siblings.splice(index, 1);
    this.parentElement = null;
  }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  querySelectorAll() { return []; }
}

class FakeCanvas extends FakeElement {
  constructor(ownerDocument) {
    super("canvas", ownerDocument);
    this.context = new FakeCanvasContext();
  }
  getContext() { return this.context; }
}

function descendants(root) {
  return [root, ...root.children.flatMap((child) => descendants(child))];
}

function createEventTarget(target = {}) {
  const listeners = new Map();
  target.addEventListener = (name, listener) => {
    const values = listeners.get(name) ?? new Set();
    values.add(listener);
    listeners.set(name, values);
  };
  target.removeEventListener = (name, listener) => listeners.get(name)?.delete(listener);
  target.dispatch = (name) => {
    for (const listener of [...(listeners.get(name) ?? [])]) listener({ type: name });
  };
  target.listenerCount = () => [...listeners.values()].reduce((total, values) => total + values.size, 0);
  return target;
}

function createEnvironment({ reducedMotion = false, hidden = false, focused = true, width = 1280, height = 720, dpr = 1.5 } = {}) {
  const document = createEventTarget({ hidden });
  document.documentElement = new FakeElement("html", document);
  document.head = new FakeElement("head", document);
  document.body = new FakeElement("body", document);
  document.documentElement.appendChild(document.head);
  document.documentElement.appendChild(document.body);
  document.hasFocus = () => focused;
  document.createElement = (tagName) => tagName.toLowerCase() === "canvas"
    ? new FakeCanvas(document) : new FakeElement(tagName, document);
  document.getElementById = (id) => descendants(document.documentElement).find((node) => node.id === id) ?? null;

  const shell = new FakeElement("main", document);
  shell.classList.add("main-surface");
  const sidebar = new FakeElement("aside", document);
  sidebar.classList.add("app-shell-left-panel");
  const roleMain = new FakeElement("section", document);
  roleMain.setAttribute("role", "main");
  const application = new FakeElement("div", document);
  application.appendChild(sidebar);
  application.appendChild(shell);
  shell.appendChild(roleMain);
  document.body.appendChild(application);

  const classMatches = (selector) => {
    const className = selector.slice(1).replace(/\\(.)/g, "$1");
    return descendants(document.documentElement).filter((node) => node.classList.contains(className));
  };
  document.querySelector = (selector) => {
    if (selector === "main.main-surface") return shell;
    if (selector === "aside.app-shell-left-panel") return sidebar;
    if (selector === '[role="main"]:has([data-testid="home-icon"])') return null;
    if (selector === '[role="main"]') return roleMain;
    if (selector.startsWith(".")) return classMatches(selector)[0] ?? null;
    return null;
  };
  document.querySelectorAll = (selector) => {
    if (selector === '[role="main"]') return [roleMain];
    if (selector.startsWith(".")) return classMatches(selector);
    return [];
  };

  const media = createEventTarget({ matches: reducedMotion });
  media.addListener = (listener) => media.addEventListener("change", listener);
  media.removeListener = (listener) => media.removeEventListener("change", listener);
  let nextHandle = 1;
  const timeouts = new Map();
  const intervals = new Map();
  const animationFrames = new Map();
  const window = createEventTarget({
    innerWidth: width,
    innerHeight: height,
    devicePixelRatio: dpr,
    performance: { now: () => 1000 },
    matchMedia: () => media,
  });
  window.requestAnimationFrame = (callback) => {
    const handle = nextHandle++;
    animationFrames.set(handle, callback);
    return handle;
  };
  window.cancelAnimationFrame = (handle) => animationFrames.delete(handle);

  class FakeMutationObserver {
    constructor(callback) { this.callback = callback; this.disconnected = false; }
    observe() {}
    disconnect() { this.disconnected = true; }
    takeRecords() { return []; }
  }

  const sandbox = {
    window,
    document,
    MutationObserver: FakeMutationObserver,
    Blob: class FakeBlob {},
    URL: { createObjectURL: () => "blob:motion-test", revokeObjectURL: () => {} },
    atob: (value) => Buffer.from(value, "base64").toString("binary"),
    setTimeout: (callback, delay) => {
      const handle = nextHandle++;
      timeouts.set(handle, { callback, delay });
      return handle;
    },
    clearTimeout: (handle) => timeouts.delete(handle),
    setInterval: (callback, delay) => {
      const handle = nextHandle++;
      intervals.set(handle, { callback, delay });
      return handle;
    },
    clearInterval: (handle) => intervals.delete(handle),
    getComputedStyle: () => ({ colorScheme: "dark", pointerEvents: "none" }),
    console,
  };
  return { sandbox, window, document, media, timeouts, intervals, animationFrames };
}

const managerTheme = {
  id: "manager-nebula-dark",
  appearance: "dark",
  art: { focusX: .76, focusY: .43, safeArea: "left", taskMode: "ambient" },
  colors: {
    background: "#0b0716",
    panel: "#171229",
    panelAlt: "#21183a",
    accent: "#b49aff",
    accentAlt: "#c084fc",
    secondary: "#f472b6",
    highlight: "#22d3ee",
    text: "#fcfaff",
    muted: "#b9adce",
    line: "rgba(180, 154, 255, .32)",
  },
  artMetadata: { width: 2560, height: 1440, ratio: 16 / 9 },
};

const [rendererTemplate, css] = await Promise.all([
  fs.readFile(rendererPath, "utf8"),
  fs.readFile(cssPath, "utf8"),
]);

async function inject(environment, theme) {
  const source = rendererTemplate
    .replace("__DREAM_CSS_JSON__", JSON.stringify("/* motion self-test */"))
    .replace("__DREAM_ART_JSON__", JSON.stringify("data:image/png;base64,AA=="))
    .replace("__DREAM_THEME_JSON__", JSON.stringify(theme));
  const result = vm.runInNewContext(source, environment.sandbox, {
    filename: rendererPath,
    timeout: 2000,
  });
  await Promise.resolve();
  return result;
}

async function install(theme, environmentOptions = {}) {
  const environment = createEnvironment(environmentOptions);
  const result = await inject(environment, theme);
  return { ...environment, result };
}

assert.match(css, /dream-manager-motion\s+#codex-dream-skin-chrome/);
assert.match(css, /#codex-dream-skin-motion[\s\S]*?pointer-events:\s*none\s*!important/);
assert.doesNotMatch(css, /mix-blend-mode:\s*multiply/);
assert.match(css, /dream-manager-motion[\s\S]*?backdrop-filter:\s*none\s*!important/);
assert.match(css, /dream-manager-motion\.dream-art-wide[\s\S]*?background-attachment:\s*scroll\s*!important/);
assert.match(rendererTemplate, /frameInterval = 1000 \/ 12/);
assert.match(rendererTemplate, /maxPixels = 1500000/);
assert.match(rendererTemplate, /maxPixelRatio = 1/);
assert.match(rendererTemplate, /const segments = 8/);
assert.match(rendererTemplate, /const segmentCount = 4/);
assert.doesNotMatch(rendererTemplate, /shadow(?:Blur|Color)\s*=/);

const active = await install(managerTheme);
assert.equal(active.result.installed, true);
assert.equal(active.document.documentElement.classList.contains("dream-manager-motion"), true);
const activeCanvas = active.document.getElementById("codex-dream-skin-motion");
assert.ok(activeCanvas, "manager theme should create the motion canvas");
assert.equal(activeCanvas.parentElement?.id, "codex-dream-skin-chrome");
const activeState = active.window.__CODEX_DREAM_SKIN_STATE__;
let activeSnapshot = activeState.motion.snapshot();
assert.equal(activeSnapshot.status, "running");
assert.equal(activeSnapshot.running, true);
assert.equal(activeSnapshot.pixelRatio, 1);
assert.equal(activeSnapshot.frameRate, 12);
assert.equal(activeSnapshot.scrolling, false);
assert.deepEqual(Array.from(activeSnapshot.palette), ["#3779ff", "#7c4cff", "#db4cda", "#37c4ee"]);
assert.deepEqual({ ...activeSnapshot.focus }, { x: .76, y: .43 });
assert.equal(activeSnapshot.artCover.scale, .5);
assert.equal(activeSnapshot.artCover.centerX, 1280 * .765);
assert.equal(activeSnapshot.artCover.centerY, 720 * .435);
assert.deepEqual(
  { ...activeSnapshot.orbitGeometry[0] },
  { centerX: 1280 * .765, centerY: 720 * .435, radiusX: 260, radiusY: 109, rotation: -12 },
);
const originalFrameCount = activeSnapshot.frameCount;
activeSnapshot = activeState.motion.testFrame(3400);
assert.ok(activeSnapshot.frameCount > originalFrameCount);
assert.ok(activeCanvas.context.drawCalls > 0);

active.document.dispatch("scroll");
activeSnapshot = activeState.motion.snapshot();
assert.equal(activeSnapshot.status, "scrolling");
assert.equal(activeSnapshot.running, false);
assert.equal(activeSnapshot.scrolling, true);
const scrollResumeTimeout = [...active.timeouts].find(([, item]) => item.delay === 200);
assert.ok(scrollResumeTimeout, "scrolling should schedule a debounced animation resume");
active.timeouts.delete(scrollResumeTimeout[0]);
scrollResumeTimeout[1].callback();
activeSnapshot = activeState.motion.snapshot();
assert.equal(activeSnapshot.status, "running");
assert.equal(activeSnapshot.running, true);
assert.equal(activeSnapshot.scrolling, false);

active.window.dispatch("blur");
assert.equal(activeState.motion.snapshot().status, "window-blurred");
assert.equal(activeState.motion.snapshot().running, false);
active.document.hidden = true;
active.window.dispatch("focus");
active.document.dispatch("visibilitychange");
assert.equal(activeState.motion.snapshot().status, "document-hidden");
active.document.hidden = false;
active.document.dispatch("visibilitychange");
assert.equal(activeState.motion.snapshot().status, "running");

active.window.innerWidth = 1440;
active.window.innerHeight = 900;
active.window.dispatch("resize");
const resizeTimeout = [...active.timeouts].find(([, item]) => item.delay === 100);
assert.ok(resizeTimeout, "resize should be debounced");
active.timeouts.delete(resizeTimeout[0]);
resizeTimeout[1].callback();
const resizedSnapshot = activeState.motion.snapshot();
assert.equal(resizedSnapshot.cssWidth, 1440);
assert.equal(resizedSnapshot.cssHeight, 900);
assert.equal(resizedSnapshot.artCover.scale, .625);
assert.equal(resizedSnapshot.artCover.renderedWidth, 1600);
assert.ok(Math.abs(resizedSnapshot.artCover.offsetX - (-121.6)) < .0001);
assert.ok(Math.abs(resizedSnapshot.artCover.centerX - 1102.4) < .0001);
assert.equal(resizedSnapshot.artCover.centerY, 391.5);
assert.equal(resizedSnapshot.orbitGeometry[0].radiusX, 325);
assert.equal(resizedSnapshot.orbitGeometry[0].radiusY, 136.25);
active.document.dispatch("scroll");
const cleanupScrollTimeout = [...active.timeouts].find(([, item]) => item.delay === 200);
assert.ok(cleanupScrollTimeout, "cleanup case should have a pending scroll resume timer");
assert.equal(activeState.cleanup(), true);
assert.equal(active.timeouts.has(cleanupScrollTimeout[0]), false);
assert.equal(active.document.getElementById("codex-dream-skin-motion"), null);
assert.equal(active.window.__CODEX_DREAM_SKIN_STATE__, undefined);

const reduced = await install(managerTheme, { reducedMotion: true });
const reducedSnapshot = reduced.window.__CODEX_DREAM_SKIN_STATE__.motion.snapshot();
assert.equal(reducedSnapshot.status, "reduced-motion");
assert.equal(reducedSnapshot.running, false);
assert.ok(reducedSnapshot.frameCount >= 2, "reduced motion should retain a static scene");
reduced.window.__CODEX_DREAM_SKIN_STATE__.cleanup();

const hidden = await install(managerTheme, { hidden: true });
assert.equal(hidden.window.__CODEX_DREAM_SKIN_STATE__.motion.snapshot().status, "document-hidden");
assert.equal(hidden.window.__CODEX_DREAM_SKIN_STATE__.motion.snapshot().running, false);
hidden.window.__CODEX_DREAM_SKIN_STATE__.cleanup();

const managerPalettes = [
  [
    "manager-light", "light",
    ["#4d8dff", "#73a8ff", "#8b5cf6", "#22b8cf"],
    ["#2b7eff", "#4e46f4", "#845cf4", "#2db9d5"],
  ],
  [
    "manager-porcelain-light", "light",
    ["#4e8f84", "#75b7a9", "#7397a4", "#c2a468"],
    ["#2b7eff", "#4e46f4", "#845cf4", "#2db9d5"],
  ],
  [
    "manager-dark", "dark",
    ["#60a5fa", "#83bcff", "#a78bfa", "#22d3ee"],
    ["#3779ff", "#7c4cff", "#db4cda", "#37c4ee"],
  ],
  [
    "manager-nebula-dark", "dark",
    ["#b49aff", "#c084fc", "#f472b6", "#22d3ee"],
    ["#3779ff", "#7c4cff", "#db4cda", "#37c4ee"],
  ],
];
for (const [id, appearance, colors, expectedMotionPalette] of managerPalettes) {
  const themed = await install({
    ...managerTheme,
    id,
    appearance,
    colors: {
      ...managerTheme.colors,
      accent: colors[0],
      accentAlt: colors[1],
      secondary: colors[2],
      highlight: colors[3],
    },
  });
  assert.deepEqual(
    Array.from(themed.window.__CODEX_DREAM_SKIN_STATE__.motion.snapshot().palette),
    expectedMotionPalette,
  );
  themed.window.__CODEX_DREAM_SKIN_STATE__.cleanup();
}

const highDpi = await install(managerTheme, { width: 2560, height: 1440, dpr: 3 });
const highDpiSnapshot = highDpi.window.__CODEX_DREAM_SKIN_STATE__.motion.snapshot();
assert.ok(highDpiSnapshot.pixelRatio <= 1);
assert.ok(highDpiSnapshot.backingWidth * highDpiSnapshot.backingHeight <= 1505000);
highDpi.window.__CODEX_DREAM_SKIN_STATE__.cleanup();

const repeated = await install(managerTheme);
const firstRepeatedState = repeated.window.__CODEX_DREAM_SKIN_STATE__;
const firstRepeatedMotion = firstRepeatedState.motion;
const listenerCounts = {
  window: repeated.window.listenerCount(),
  document: repeated.document.listenerCount(),
  media: repeated.media.listenerCount(),
};
await inject(repeated, { ...managerTheme, id: "manager-dark" });
const secondRepeatedState = repeated.window.__CODEX_DREAM_SKIN_STATE__;
assert.notEqual(secondRepeatedState, firstRepeatedState);
assert.equal(firstRepeatedMotion.snapshot().status, "destroyed");
assert.equal(descendants(repeated.document.documentElement)
  .filter((node) => node.id === "codex-dream-skin-motion").length, 1);
assert.equal(repeated.window.listenerCount(), listenerCounts.window);
assert.equal(repeated.document.listenerCount(), listenerCounts.document);
assert.equal(repeated.media.listenerCount(), listenerCounts.media);
assert.equal(repeated.intervals.size, 1);
secondRepeatedState.cleanup();

const preset = await install({ ...managerTheme, id: "preset-midnight-aurora" });
assert.equal(preset.document.documentElement.classList.contains("dream-manager-motion"), false);
assert.equal(preset.document.getElementById("codex-dream-skin-motion"), null);
assert.equal(preset.window.__CODEX_DREAM_SKIN_STATE__.motion, null);
preset.window.__CODEX_DREAM_SKIN_STATE__.cleanup();

console.log(JSON.stringify({
  pass: true,
  test: "renderer-manager-motion",
  cases: [
    "active", "scroll-pause-resume-cleanup", "cover-16:9-to-16:10-resize", "blur-hidden-resume", "reduced-motion",
    "hidden", "four-manager-palettes", "high-dpi-budget", "repeat-injection-cleanup", "non-manager",
  ],
}));
