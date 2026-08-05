import assert from "node:assert/strict";
import { execFile, spawn } from "node:child_process";
import { once } from "node:events";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { promisify } from "node:util";
import { fileURLToPath, pathToFileURL } from "node:url";

const execFileAsync = promisify(execFile);
const here = path.dirname(fileURLToPath(import.meta.url));
const assetRoot = path.resolve(here, "..", "assets");
const rendererPath = path.join(assetRoot, "renderer-inject.js");
const cssPath = path.join(assetRoot, "dream-skin.css");
const edgePath = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const forbiddenPort = 8317;
const totalTimeoutMs = 75_000;

const managerThemes = [
  {
    id: "manager-light",
    image: "account-manager-aurora-light.jpg",
    appearance: "light",
    contrast: 86,
    colors: {
      background: "#FBFCFF", panel: "#FFFFFF", panelAlt: "#F7F9FF",
      accent: "#4D8DFF", accentAlt: "#73A8FF", secondary: "#8B5CF6",
      highlight: "#22B8CF", text: "#172033", muted: "#667085",
      line: "rgba(77, 141, 255, 0.26)",
    },
  },
  {
    id: "manager-porcelain-light",
    image: "account-manager-porcelain-light.jpg",
    appearance: "light",
    contrast: 88,
    colors: {
      background: "#EDF6F3", panel: "#F5FAF8", panelAlt: "#FFFFFF",
      accent: "#4E8F84", accentAlt: "#75B7A9", secondary: "#7397A4",
      highlight: "#C2A468", text: "#183C39", muted: "#617D78",
      line: "rgba(78, 143, 132, 0.28)",
    },
  },
  {
    id: "manager-dark",
    image: "account-manager-deep-sea.jpg",
    appearance: "dark",
    contrast: 92,
    colors: {
      background: "#07101E", panel: "#091526", panelAlt: "#12243B",
      accent: "#60A5FA", accentAlt: "#83BCFF", secondary: "#A78BFA",
      highlight: "#22D3EE", text: "#F1F6FF", muted: "#9CB0C9",
      line: "rgba(96, 165, 250, 0.30)",
    },
  },
  {
    id: "manager-nebula-dark",
    image: "account-manager-nebula-orbit.jpg",
    appearance: "dark",
    contrast: 94,
    colors: {
      background: "#0B0716", panel: "#171229", panelAlt: "#21183A",
      accent: "#B49AFF", accentAlt: "#C084FC", secondary: "#F472B6",
      highlight: "#22D3EE", text: "#FCFAFF", muted: "#B9ADCE",
      line: "rgba(180, 154, 255, 0.32)",
    },
  },
];

const delay = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds));

async function withTimeout(promise, timeoutMs, description) {
  let timer;
  const timeout = new Promise((resolve, reject) => {
    timer = setTimeout(() => reject(new Error(`${description} exceeded ${timeoutMs}ms`)), timeoutMs);
  });
  try {
    return await Promise.race([promise, timeout]);
  } finally {
    clearTimeout(timer);
  }
}

async function waitUntil(probe, description, timeoutMs = 8_000, intervalMs = 40) {
  const deadline = Date.now() + timeoutMs;
  let lastError;
  while (Date.now() < deadline) {
    try {
      const value = await probe();
      if (value) return value;
    } catch (error) {
      lastError = error;
    }
    await delay(intervalMs);
  }
  const suffix = lastError ? ` Last error: ${lastError.message}` : "";
  throw new Error(`Timed out waiting for ${description}.${suffix}`);
}

class CdpClient {
  constructor(url, label) {
    this.url = url;
    this.label = label;
    this.socket = null;
    this.nextId = 1;
    this.pending = new Map();
    this.closed = false;
  }

  async connect() {
    assert.equal(typeof WebSocket, "function", "This self-test requires Node.js with WebSocket support");
    const socket = new WebSocket(this.url);
    this.socket = socket;
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`Timed out connecting to ${this.label} CDP`)), 8_000);
      socket.addEventListener("open", () => {
        clearTimeout(timer);
        resolve();
      }, { once: true });
      socket.addEventListener("error", () => {
        clearTimeout(timer);
        reject(new Error(`Unable to connect to ${this.label} CDP`));
      }, { once: true });
    });
    socket.addEventListener("message", (event) => this.#onMessage(event.data));
    socket.addEventListener("close", () => this.#onClose());
    return this;
  }

  #onMessage(raw) {
    let message;
    try {
      message = JSON.parse(String(raw));
    } catch {
      return;
    }
    if (!message.id) return;
    const pending = this.pending.get(message.id);
    if (!pending) return;
    this.pending.delete(message.id);
    clearTimeout(pending.timer);
    if (message.error) {
      pending.reject(new Error(`${pending.method}: ${message.error.message}`));
    } else {
      pending.resolve(message.result ?? {});
    }
  }

  #onClose() {
    this.closed = true;
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(new Error(`${this.label} CDP closed during ${pending.method}`));
    }
    this.pending.clear();
  }

  call(method, params = {}, timeoutMs = 10_000) {
    if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
      return Promise.reject(new Error(`${this.label} CDP is not open for ${method}`));
    }
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`${this.label} CDP timed out during ${method}`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer, method });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }

  close() {
    if (!this.socket || this.socket.readyState > WebSocket.OPEN) return;
    this.socket.close();
  }
}

function makeThemeConfig(theme) {
  return {
    schemaVersion: 1,
    id: theme.id,
    name: theme.id,
    appearance: theme.appearance,
    contrast: theme.contrast,
    art: { focusX: 0.76, focusY: 0.435, safeArea: "left", taskMode: "ambient" },
    artMetadata: { width: 2560, height: 1440, ratio: 16 / 9 },
    palette: { accent: theme.colors.accent, contrast: theme.contrast },
    colors: theme.colors,
  };
}

function makeInjection(rendererTemplate, css, artDataUrl, theme) {
  return rendererTemplate
    .replace("__DREAM_CSS_JSON__", JSON.stringify(css))
    .replace("__DREAM_ART_JSON__", JSON.stringify(artDataUrl))
    .replace("__DREAM_THEME_JSON__", JSON.stringify(makeThemeConfig(theme)));
}

async function evaluate(client, expression, { awaitPromise = false, timeoutMs = 12_000 } = {}) {
  const response = await client.call("Runtime.evaluate", {
    expression,
    awaitPromise,
    returnByValue: true,
    userGesture: true,
  }, timeoutMs);
  if (response.exceptionDetails) {
    const detail = response.exceptionDetails.exception?.description
      || response.exceptionDetails.text
      || "Unknown Runtime.evaluate exception";
    throw new Error(detail);
  }
  return response.result?.value;
}

async function setViewport(client, width, height, deviceScaleFactor) {
  await client.call("Emulation.setDeviceMetricsOverride", {
    width,
    height,
    deviceScaleFactor,
    mobile: false,
    screenWidth: width,
    screenHeight: height,
  });
  await waitUntil(async () => {
    const dimensions = await evaluate(client, "({ width: innerWidth, height: innerHeight, dpr: devicePixelRatio })");
    return dimensions.width === width && dimensions.height === height
      && Math.abs(dimensions.dpr - deviceScaleFactor) < 0.01;
  }, `${width}x${height} DPR ${deviceScaleFactor}`);
}

async function inspectManager(client) {
  return evaluate(client, `(() => {
    const canvas = document.getElementById("codex-dream-skin-motion");
    const chrome = document.getElementById("codex-dream-skin-chrome");
    const state = window.__CODEX_DREAM_SKIN_STATE__;
    if (!canvas || !chrome || !state?.motion) return null;
    const canvasStyle = getComputedStyle(canvas);
    const chromeStyle = getComputedStyle(chrome);
    const bodyStyle = getComputedStyle(document.body);
    const headerStyle = getComputedStyle(document.querySelector("header.app-header-tint"));
    const menuStyle = getComputedStyle(document.getElementById("fixture-menu"));
    const composerStyle = getComputedStyle(document.querySelector(".composer-surface-chrome"));
    const suggestionStyle = getComputedStyle(document.getElementById("fixture-suggestion"));
    const rect = canvas.getBoundingClientRect();
    return {
      rootClasses: [...document.documentElement.classList],
      count: document.querySelectorAll("#codex-dream-skin-motion").length,
      dataset: { ...canvas.dataset },
      canvasStyle: {
        display: canvasStyle.display,
        opacity: canvasStyle.opacity,
        zIndex: canvasStyle.zIndex,
        pointerEvents: canvasStyle.pointerEvents,
        mixBlendMode: canvasStyle.mixBlendMode,
      },
      chromeStyle: {
        display: chromeStyle.display,
        opacity: chromeStyle.opacity,
        zIndex: chromeStyle.zIndex,
        pointerEvents: chromeStyle.pointerEvents,
        position: chromeStyle.position,
      },
      performanceStyle: {
        backgroundAttachment: bodyStyle.backgroundAttachment,
        headerBackdrop: headerStyle.backdropFilter,
        menuBackdrop: menuStyle.backdropFilter,
        composerBackdrop: composerStyle.backdropFilter,
        suggestionBackdrop: suggestionStyle.backdropFilter,
      },
      rect: { left: rect.left, top: rect.top, width: rect.width, height: rect.height },
      snapshot: state.motion.snapshot(),
    };
  })()`);
}

function assertManagerInspection(inspection, theme, width, height) {
  assert.ok(inspection, `${theme.id} should install a real manager motion canvas`);
  assert.equal(inspection.count, 1, `${theme.id} should have exactly one motion canvas`);
  assert.ok(inspection.rootClasses.includes("codex-dream-skin"));
  assert.ok(inspection.rootClasses.includes("dream-manager-motion"));
  assert.ok(inspection.rootClasses.includes(`dream-theme-${theme.appearance}`));
  assert.equal(inspection.dataset.dreamMotion, "manager");
  assert.equal(inspection.dataset.dreamThemeId, theme.id);
  assert.equal(inspection.canvasStyle.display, "block");
  assert.equal(inspection.canvasStyle.pointerEvents, "none");
  assert.equal(inspection.canvasStyle.zIndex, "auto");
  assert.ok(Math.abs(Number(inspection.canvasStyle.opacity) - (theme.appearance === "light" ? 0.78 : 0.9)) < 0.001);
  assert.equal(inspection.canvasStyle.mixBlendMode, "normal");
  assert.equal(inspection.chromeStyle.display, "block");
  assert.equal(inspection.chromeStyle.zIndex, "-1");
  assert.equal(inspection.chromeStyle.pointerEvents, "none");
  assert.equal(inspection.chromeStyle.position, "fixed");
  assert.equal(inspection.performanceStyle.backgroundAttachment, "scroll");
  assert.equal(inspection.performanceStyle.headerBackdrop, "none");
  assert.equal(inspection.performanceStyle.menuBackdrop, "none");
  assert.equal(inspection.performanceStyle.composerBackdrop, "none");
  assert.equal(inspection.performanceStyle.suggestionBackdrop, "none");
  assert.ok(Math.abs(inspection.rect.left) < 0.01);
  assert.ok(Math.abs(inspection.rect.top) < 0.01);
  assert.ok(Math.abs(inspection.rect.width - width) < 0.01);
  assert.ok(Math.abs(inspection.rect.height - height) < 0.01);
  assert.equal(inspection.snapshot.themeId, theme.id);
  assert.equal(inspection.snapshot.status, "running");
  assert.equal(inspection.snapshot.running, true);
  assert.equal(inspection.snapshot.cssWidth, width);
  assert.equal(inspection.snapshot.cssHeight, height);
  // The public snapshot rounds pixelRatio to three decimals while the backing
  // store uses the full-precision budget ratio, so a one-pixel delta is valid.
  assert.ok(Math.abs(inspection.snapshot.backingWidth - width * inspection.snapshot.pixelRatio) <= 2);
  assert.ok(Math.abs(inspection.snapshot.backingHeight - height * inspection.snapshot.pixelRatio) <= 2);
  assert.deepEqual(
    inspection.snapshot.palette,
    theme.appearance === "light"
      ? ["#2b7eff", "#4e46f4", "#845cf4", "#2db9d5"]
      : ["#3779ff", "#7c4cff", "#db4cda", "#37c4ee"],
  );
  assert.equal(inspection.snapshot.frameRate, 12);
  assert.ok(inspection.snapshot.backingWidth * inspection.snapshot.backingHeight <= 1_505_000);
}

async function sampleMovingFrames(client) {
  return evaluate(client, `(async () => {
    const state = window.__CODEX_DREAM_SKIN_STATE__;
    const canvas = document.getElementById("codex-dream-skin-motion");
    const context = canvas?.getContext("2d", { willReadFrequently: true });
    if (!state?.motion || !canvas || !context) return null;
    const start = state.motion.snapshot();
    const first = new Uint8ClampedArray(context.getImageData(0, 0, canvas.width, canvas.height).data);
    const targetFrame = start.frameCount + 5;
    const deadline = performance.now() + 2500;
    while (state.motion.snapshot().frameCount < targetFrame && performance.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
    const finish = state.motion.snapshot();
    const second = context.getImageData(0, 0, canvas.width, canvas.height).data;
    let changedPixels = 0;
    let totalDelta = 0;
    let visibleFirst = 0;
    let visibleSecond = 0;
    for (let index = 0; index < first.length; index += 4) {
      const delta = Math.abs(first[index] - second[index])
        + Math.abs(first[index + 1] - second[index + 1])
        + Math.abs(first[index + 2] - second[index + 2])
        + Math.abs(first[index + 3] - second[index + 3]);
      if (delta > 2) changedPixels += 1;
      totalDelta += delta;
      if (first[index + 3] > 2) visibleFirst += 1;
      if (second[index + 3] > 2) visibleSecond += 1;
    }
    return {
      startFrame: start.frameCount,
      finishFrame: finish.frameCount,
      changedPixels,
      totalDelta,
      visibleFirst,
      visibleSecond,
      backingWidth: canvas.width,
      backingHeight: canvas.height,
    };
  })()`, { awaitPromise: true, timeoutMs: 8_000 });
}

function assertMovingFrames(sample, label) {
  assert.ok(sample, `${label} should expose readable canvas pixels`);
  assert.ok(sample.finishFrame >= sample.startFrame + 5, `${label} animation should advance at least five frames`);
  assert.ok(sample.visibleFirst > 100 && sample.visibleSecond > 100, `${label} frames should contain visible pixels`);
  assert.ok(sample.changedPixels > 100, `${label} should visibly change pixels between animation frames`);
  assert.ok(sample.totalDelta > 2_000, `${label} frame delta should be materially visible`);
}

async function verifyScrollPause(client) {
  return evaluate(client, `(async () => {
    const motion = window.__CODEX_DREAM_SKIN_STATE__?.motion;
    if (!motion) return null;
    document.dispatchEvent(new Event("scroll"));
    const paused = motion.snapshot();
    await new Promise((resolve) => setTimeout(resolve, 80));
    const duringPause = motion.snapshot();
    const deadline = performance.now() + 1200;
    let resumed = duringPause;
    while (resumed.status !== "running" && performance.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, 25));
      resumed = motion.snapshot();
    }
    return { paused, duringPause, resumed };
  })()`, { awaitPromise: true, timeoutMs: 2_000 });
}

async function verifyClickThrough(client) {
  const probe = await evaluate(client, `(() => {
    window.__fixtureClickCount = 0;
    const button = document.getElementById("probe-button");
    const rect = button.getBoundingClientRect();
    const x = rect.left + rect.width / 2;
    const y = rect.top + rect.height / 2;
    const top = document.elementFromPoint(x, y);
    const quiet = document.elementFromPoint(innerWidth - 12, innerHeight - 12);
    return {
      x, y,
      topId: top?.id || "",
      quietId: quiet?.id || "",
      topTag: top?.tagName || "",
      quietTag: quiet?.tagName || "",
    };
  })()`);
  assert.equal(probe.topId, "probe-button", "elementFromPoint should reach content through the canvas");
  assert.notEqual(probe.quietId, "codex-dream-skin-motion");
  assert.notEqual(probe.quietId, "codex-dream-skin-chrome");
  await client.call("Input.dispatchMouseEvent", { type: "mouseMoved", x: probe.x, y: probe.y });
  await client.call("Input.dispatchMouseEvent", {
    type: "mousePressed", x: probe.x, y: probe.y, button: "left", buttons: 1, clickCount: 1,
  });
  await client.call("Input.dispatchMouseEvent", {
    type: "mouseReleased", x: probe.x, y: probe.y, button: "left", buttons: 0, clickCount: 1,
  });
  await waitUntil(async () => (await evaluate(client, "window.__fixtureClickCount")) === 1, "real click delivery");
  return probe;
}

async function fetchJson(url, description) {
  return waitUntil(async () => {
    const response = await fetch(url, { signal: AbortSignal.timeout(1_500) });
    if (!response.ok) return null;
    return response.json();
  }, description, 8_000, 80);
}

async function waitForEdgeExit(child, timeoutMs) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return true;
  return Promise.race([
    once(child, "exit").then(() => true),
    delay(timeoutMs).then(() => false),
  ]);
}

async function terminateOwnEdge(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  assert.ok(Number.isInteger(child.pid) && child.pid > 0, "Refusing to terminate an unknown process");
  try {
    await execFileAsync("taskkill.exe", ["/PID", String(child.pid), "/T", "/F"], {
      windowsHide: true,
      timeout: 8_000,
    });
  } catch (error) {
    if (child.exitCode === null && child.signalCode === null) throw error;
  }
  await waitForEdgeExit(child, 5_000);
}

async function createFixture(tempRoot) {
  const fixturePath = path.join(tempRoot, "renderer-motion-fixture.html");
  const html = `<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>Codex Dream Skin motion fixture</title>
  <style>
    html, body { width: 100%; height: 100%; margin: 0; overflow: hidden; }
    body { font: 14px/1.4 system-ui, sans-serif; background: #101522; }
    #fixture-shell { position: relative; z-index: 0; display: grid; grid-template-columns: 248px 1fr; width: 100%; height: 100%; }
    #fixture-menu { position: fixed; z-index: 10; left: 0; right: 0; top: 0; height: 28px; }
    aside.app-shell-left-panel { padding: 20px; color: #fff; }
    main.main-surface { min-width: 0; color: #fff; }
    header.app-header-tint { height: 52px; display: flex; align-items: center; padding: 0 24px; }
    [role="main"] { position: relative; height: calc(100% - 52px); }
    #probe-button { position: fixed; z-index: 20; left: 50%; top: 50%; width: 176px; height: 52px; transform: translate(-50%, -50%); }
  </style>
</head>
<body>
  <div id="fixture-menu" class="group/application-menu-top-bar">Menu</div>
  <div id="fixture-shell">
    <aside class="app-shell-left-panel"><strong>Codex</strong></aside>
    <main class="main-surface">
      <header class="app-header-tint">Offline renderer fixture</header>
      <section role="main">
        <span data-testid="home-icon"></span>
        <div class="group/home-suggestions"><button id="fixture-suggestion" type="button">Suggestion</button></div>
        <div class="composer-surface-chrome">Composer</div>
        <button id="probe-button" type="button">点击穿透验证</button>
      </section>
    </main>
  </div>
  <script>
    window.__fixtureClickCount = 0;
    document.getElementById("probe-button").addEventListener("click", () => { window.__fixtureClickCount += 1; });
    window.__fixtureReady = true;
  </script>
</body>
</html>`;
  await fs.writeFile(fixturePath, html, "utf8");
  return fixturePath;
}

async function runBrowserTest(resources) {
  const { rendererTemplate, css, images, fixturePath, profilePath } = resources;
  const fixtureUrl = pathToFileURL(fixturePath).href;
  const edge = spawn(edgePath, [
    "--headless=new",
    `--user-data-dir=${profilePath}`,
    "--remote-debugging-address=127.0.0.1",
    "--remote-debugging-port=0",
    "--no-first-run",
    "--no-default-browser-check",
    "--disable-extensions",
    "--disable-background-networking",
    "--disable-background-timer-throttling",
    "--disable-renderer-backgrounding",
    "--disable-backgrounding-occluded-windows",
    "--disable-component-update",
    "--disable-sync",
    "--metrics-recording-only",
    "--allow-file-access-from-files",
    "about:blank",
  ], { stdio: "ignore", windowsHide: true });
  resources.edge = edge;
  const spawnError = once(edge, "error").then(([error]) => { throw error; });
  const activePortPath = path.join(profilePath, "DevToolsActivePort");
  const activePort = await Promise.race([
    waitUntil(async () => {
      if (edge.exitCode !== null) throw new Error(`Edge exited early with code ${edge.exitCode}`);
      try {
        const lines = (await fs.readFile(activePortPath, "utf8")).trim().split(/\r?\n/);
        if (lines.length < 2) return null;
        return { port: Number(lines[0]), browserPath: lines[1] };
      } catch (error) {
        if (error.code === "ENOENT") return null;
        throw error;
      }
    }, "Edge DevToolsActivePort", 12_000, 50),
    spawnError,
  ]);
  assert.ok(Number.isInteger(activePort.port) && activePort.port > 0 && activePort.port <= 65_535);
  assert.notEqual(activePort.port, forbiddenPort, "Edge must never use the protected Account Manager gateway port");
  resources.debugPort = activePort.port;

  const browserCdp = await new CdpClient(
    `ws://127.0.0.1:${activePort.port}${activePort.browserPath}`,
    "browser",
  ).connect();
  resources.browserCdp = browserCdp;
  const browserVersion = await browserCdp.call("Browser.getVersion");
  const created = await browserCdp.call("Target.createTarget", { url: fixtureUrl });
  const targetsUrl = `http://127.0.0.1:${activePort.port}/json/list`;
  const target = await waitUntil(async () => {
    const targets = await fetchJson(targetsUrl, "Edge target list");
    return targets.find((candidate) => candidate.id === created.targetId && candidate.webSocketDebuggerUrl) || null;
  }, "fixture target", 10_000, 80);
  const pageCdp = await new CdpClient(target.webSocketDebuggerUrl, "fixture page").connect();
  resources.pageCdp = pageCdp;
  await Promise.all([
    pageCdp.call("Runtime.enable"),
    pageCdp.call("Page.enable"),
    pageCdp.call("Page.bringToFront"),
  ]);
  await waitUntil(async () => evaluate(pageCdp,
    "document.readyState === 'complete' && window.__fixtureReady === true"), "fixture DOM");
  await setViewport(pageCdp, 1280, 720, 1.25);

  const themeResults = [];
  const motionSamples = [];
  let clickProbe = null;
  for (const theme of managerThemes) {
    const artDataUrl = `data:image/jpeg;base64,${images.get(theme.image).toString("base64")}`;
    const injected = await evaluate(pageCdp,
      makeInjection(rendererTemplate, css, artDataUrl, theme), { awaitPromise: true, timeoutMs: 15_000 });
    assert.equal(injected?.installed, true, `${theme.id} renderer injection should report installed`);
    await pageCdp.call("Page.bringToFront");
    const inspection = await waitUntil(async () => {
      const value = await inspectManager(pageCdp);
      return value?.snapshot?.status === "running" && value.snapshot.frameCount >= 2 ? value : null;
    }, `${theme.id} running motion canvas`, 8_000, 50);
    assertManagerInspection(inspection, theme, 1280, 720);
    themeResults.push({
      id: theme.id,
      appearance: theme.appearance,
      display: inspection.canvasStyle.display,
      opacity: Number(inspection.canvasStyle.opacity),
      canvasZIndex: inspection.canvasStyle.zIndex,
      hostZIndex: inspection.chromeStyle.zIndex,
      size: [inspection.snapshot.cssWidth, inspection.snapshot.cssHeight],
      backing: [inspection.snapshot.backingWidth, inspection.snapshot.backingHeight],
      pixelRatio: inspection.snapshot.pixelRatio,
    });
    if (theme.id === "manager-light" || theme.id === "manager-dark") {
      const sample = await sampleMovingFrames(pageCdp);
      assertMovingFrames(sample, theme.id);
      motionSamples.push({ theme: theme.id, ...sample });
    }
    if (theme.id === "manager-light") clickProbe = await verifyClickThrough(pageCdp);
  }

  const scrollPause = await verifyScrollPause(pageCdp);
  assert.ok(scrollPause, "manager motion should expose scroll pause state");
  assert.equal(scrollPause.paused.status, "scrolling");
  assert.equal(scrollPause.paused.running, false);
  assert.equal(scrollPause.paused.scrolling, true);
  assert.equal(scrollPause.duringPause.frameCount, scrollPause.paused.frameCount);
  assert.equal(scrollPause.resumed.status, "running");
  assert.equal(scrollPause.resumed.scrolling, false);

  await setViewport(pageCdp, 1440, 900, 2);
  const resized = await waitUntil(async () => {
    const inspection = await inspectManager(pageCdp);
    return inspection?.snapshot?.cssWidth === 1440 && inspection.snapshot.cssHeight === 900
      && Math.abs(inspection.snapshot.pixelRatio - 1) < 0.001 ? inspection : null;
  }, "16:10 manager canvas resize");
  assertManagerInspection(resized, managerThemes.at(-1), 1440, 900);
  assert.ok(Math.abs(resized.snapshot.pixelRatio - 1) < 0.001);
  assert.ok(Math.abs(resized.snapshot.artCover.scale - 0.625) < 0.0001);
  assert.ok(Math.abs(resized.snapshot.artCover.renderedWidth - 1600) < 0.001);
  assert.ok(Math.abs(resized.snapshot.artCover.renderedHeight - 900) < 0.001);
  assert.ok(Math.abs(resized.snapshot.artCover.offsetX - (-121.6)) < 0.01);

  await setViewport(pageCdp, 2560, 1440, 3);
  const highDpi = await waitUntil(async () => {
    const inspection = await inspectManager(pageCdp);
    return inspection?.snapshot?.cssWidth === 2560 && inspection.snapshot.cssHeight === 1440
      ? inspection : null;
  }, "high-DPR pixel budget");
  assertManagerInspection(highDpi, managerThemes.at(-1), 2560, 1440);
  assert.ok(highDpi.snapshot.pixelRatio <= 1);
  assert.ok(highDpi.snapshot.pixelRatio < 1.1);
  assert.ok(highDpi.snapshot.backingWidth * highDpi.snapshot.backingHeight <= 1_505_000);

  await setViewport(pageCdp, 1280, 720, 1);
  const nonManager = {
    ...managerThemes[0],
    id: "preset-browser-control",
    image: managerThemes[0].image,
  };
  const nonManagerDataUrl = `data:image/jpeg;base64,${images.get(nonManager.image).toString("base64")}`;
  await evaluate(pageCdp, makeInjection(rendererTemplate, css, nonManagerDataUrl, nonManager), {
    awaitPromise: true,
    timeoutMs: 15_000,
  });
  const nonManagerState = await waitUntil(async () => evaluate(pageCdp, `(() => ({
    hasCanvas: Boolean(document.getElementById("codex-dream-skin-motion")),
    canvasCount: document.querySelectorAll("#codex-dream-skin-motion").length,
    managerClass: document.documentElement.classList.contains("dream-manager-motion"),
    motionIsNull: window.__CODEX_DREAM_SKIN_STATE__?.motion === null,
    themeId: window.__CODEX_DREAM_SKIN_STATE__?.config?.themeId || "",
  }))()`), "non-manager renderer state");
  assert.deepEqual(nonManagerState, {
    hasCanvas: false,
    canvasCount: 0,
    managerClass: false,
    motionIsNull: true,
    themeId: "preset-browser-control",
  });

  const domCleanup = await evaluate(pageCdp, `(() => {
    const result = window.__CODEX_DREAM_SKIN_STATE__?.cleanup?.();
    return {
      result,
      hasState: Boolean(window.__CODEX_DREAM_SKIN_STATE__),
      hasStyle: Boolean(document.getElementById("codex-dream-skin-style")),
      hasChrome: Boolean(document.getElementById("codex-dream-skin-chrome")),
      hasCanvas: Boolean(document.getElementById("codex-dream-skin-motion")),
      hasRootClass: document.documentElement.classList.contains("codex-dream-skin"),
    };
  })()`);
  assert.deepEqual(domCleanup, {
    result: true,
    hasState: false,
    hasStyle: false,
    hasChrome: false,
    hasCanvas: false,
    hasRootClass: false,
  });

  return {
    pass: true,
    test: "renderer-manager-motion-real-edge",
    edge: browserVersion.product,
    debugPort: activePort.port,
    protectedPortUntouched: activePort.port !== forbiddenPort,
    themes: themeResults,
    motionSamples,
    scrollPause,
    clickThrough: clickProbe,
    resize16x10: {
      size: [resized.snapshot.cssWidth, resized.snapshot.cssHeight],
      requestedDpr: 2,
      pixelRatio: resized.snapshot.pixelRatio,
      backing: [resized.snapshot.backingWidth, resized.snapshot.backingHeight],
      artCoverScale: resized.snapshot.artCover.scale,
    },
    highDpiBudget: {
      size: [highDpi.snapshot.cssWidth, highDpi.snapshot.cssHeight],
      requestedDpr: 3,
      pixelRatio: highDpi.snapshot.pixelRatio,
      backingPixels: highDpi.snapshot.backingWidth * highDpi.snapshot.backingHeight,
    },
    nonManager: nonManagerState,
    domCleanup,
  };
}

async function main() {
  await fs.access(edgePath);
  const [rendererTemplate, css, ...imageBuffers] = await Promise.all([
    fs.readFile(rendererPath, "utf8"),
    fs.readFile(cssPath, "utf8"),
    ...managerThemes.map((theme) => fs.readFile(path.join(assetRoot, theme.image))),
  ]);
  for (const placeholder of ["__DREAM_CSS_JSON__", "__DREAM_ART_JSON__", "__DREAM_THEME_JSON__"]) {
    assert.equal(rendererTemplate.split(placeholder).length - 1, 1, `Renderer placeholder ${placeholder} changed`);
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
  const images = new Map();
  managerThemes.forEach((theme, index) => {
    const buffer = imageBuffers[index];
    assert.ok(buffer.length > 16_000, `${theme.image} should be a substantive bundled image`);
    assert.equal(buffer[0], 0xff, `${theme.image} should start with JPEG SOI`);
    assert.equal(buffer[1], 0xd8, `${theme.image} should start with JPEG SOI`);
    images.set(theme.image, buffer);
  });

  const tempRoot = await fs.mkdtemp(path.join(os.tmpdir(), "codex-dream-motion-browser-"));
  const resources = {
    rendererTemplate,
    css,
    images,
    tempRoot,
    profilePath: path.join(tempRoot, "edge-profile"),
    fixturePath: null,
    edge: null,
    browserCdp: null,
    pageCdp: null,
    debugPort: null,
  };
  let result;
  let primaryError;
  let cleanupError;
  try {
    await fs.mkdir(resources.profilePath, { recursive: true });
    resources.fixturePath = await createFixture(tempRoot);
    result = await withTimeout(runBrowserTest(resources), totalTimeoutMs, "Browser self-test");
  } catch (error) {
    primaryError = error;
  } finally {
    try {
      if (resources.browserCdp && !resources.browserCdp.closed) {
        await resources.browserCdp.call("Browser.close", {}, 5_000).catch(() => {});
      }
      const exitedNormally = await waitForEdgeExit(resources.edge, 6_000);
      if (!exitedNormally) await terminateOwnEdge(resources.edge);
      resources.pageCdp?.close();
      resources.browserCdp?.close();
      await fs.rm(tempRoot, { recursive: true, force: true, maxRetries: 6, retryDelay: 150 });
      const removed = await fs.access(tempRoot).then(() => false, () => true);
      assert.equal(removed, true, "Temporary Edge profile and fixture should be removed");
      if (result) result.processCleanup = {
        edgeExited: resources.edge?.exitCode !== null || resources.edge?.signalCode !== null,
        tempRootRemoved: removed,
      };
    } catch (error) {
      cleanupError = error;
    }
  }
  if (primaryError && cleanupError) throw new AggregateError([primaryError, cleanupError], "Test and cleanup both failed");
  if (primaryError) throw primaryError;
  if (cleanupError) throw cleanupError;
  console.log(JSON.stringify(result, null, 2));
}

main().catch((error) => {
  console.error(error?.stack || error);
  process.exitCode = 1;
});
