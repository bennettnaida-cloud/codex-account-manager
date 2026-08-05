((cssText, artDataUrl, rawConfig) => {
  const STATE_KEY = "__CODEX_DREAM_SKIN_STATE__";
  const STYLE_ID = "codex-dream-skin-style";
  const CHROME_ID = "codex-dream-skin-chrome";
  const MOTION_CANVAS_ID = "codex-dream-skin-motion";
  const ROOT_CLASSES = [
    "codex-dream-skin",
    "dream-manager-motion",
    "dream-theme-light",
    "dream-theme-dark",
    "dream-art-wide",
    "dream-art-standard",
    "dream-focus-left",
    "dream-focus-center",
    "dream-focus-right",
    "dream-safe-left",
    "dream-safe-center",
    "dream-safe-right",
    "dream-safe-none",
    "dream-task-ambient",
    "dream-task-banner",
    "dream-task-off",
  ];
  const ROOT_PROPERTIES = [
    "--dream-art",
    "--dream-art-position",
    "--dream-focus-x",
    "--dream-focus-y",
    "--dream-accent",
    "--dream-accent-alt",
    "--dream-secondary",
    "--dream-highlight",
    "--dream-accent-ink",
    "--dream-canvas",
    "--dream-surface",
    "--dream-surface-raised",
    "--dream-sidebar",
    "--dream-text",
    "--dream-text-muted",
    "--dream-line",
    "--dream-line-soft",
    "--dream-image-contrast",
    "--dream-image-luma",
  ];
  const HOME_UTILITY_CLASS = "dream-home-utility";
  const installToken = {};
  let samplingNativeShell = false;
  let observer = null;
  let motion = null;
  window.__CODEX_DREAM_SKIN_DISABLED__ = false;

  const clamp = (value, min = 0, max = 1) => Math.min(max, Math.max(min, Number(value)));
  const luminance = (red, green, blue) => {
    const linear = [red, green, blue].map((value) => {
      const channel = value / 255;
      return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
    });
    return .2126 * linear[0] + .7152 * linear[1] + .0722 * linear[2];
  };
  const defaultProfile = {
    appearance: "dark",
    accent: [108, 131, 142],
    focusX: .5,
    focusY: .5,
    aspect: 1.6,
    luma: .32,
    safeArea: "center",
  };

  const normalizeConfig = (value) => {
    const config = value && typeof value === "object" ? value : {};
    const art = config.art && typeof config.art === "object" ? config.art : {};
    const rawColors = config.colors && typeof config.colors === "object" ? config.colors : {};
    const hasNumber = (candidate) =>
      (typeof candidate === "number" || (typeof candidate === "string" && candidate.trim() !== "")) &&
      Number.isFinite(Number(candidate));
    const colorPattern = /^(?:#[\da-f]{3,8}|(?:rgba?|hsla?|oklch|oklab)\([^;{}]{1,96}\))$/i;
    const safeColor = (candidate) => {
      const normalized = typeof candidate === "string" ? candidate.trim() : "";
      return colorPattern.test(normalized) ? normalized : null;
    };
    const colorKeys = [
      "background", "panel", "panelAlt", "accent", "accentAlt", "secondary",
      "highlight", "text", "muted", "line",
    ];
    const colors = Object.fromEntries(colorKeys
      .map((key) => [key, safeColor(rawColors[key])])
      .filter(([, color]) => color));
    const safeAccent = colors.accent || safeColor(config?.palette?.accent);
    const appearance = ["auto", "light", "dark"].includes(config.appearance)
      ? config.appearance
      : "auto";
    const safeArea = ["auto", "left", "right", "center", "none"].includes(art.safeArea)
      ? art.safeArea
      : "auto";
    const taskMode = ["auto", "ambient", "banner", "off"].includes(art.taskMode)
      ? art.taskMode
      : "auto";
    const metadataRatio = Number(config?.artMetadata?.ratio);
    const metadataWidth = Number(config?.artMetadata?.width);
    const metadataHeight = Number(config?.artMetadata?.height);
    const hasMetadataSize = Number.isFinite(metadataWidth) && Number.isFinite(metadataHeight) &&
      metadataWidth > 0 && metadataHeight > 0;
    const requestedContrast = Number(config?.contrast ?? config?.palette?.contrast);
    const themeId = typeof config.id === "string" ? config.id.trim() : "";
    const managerMotionThemeIds = new Set([
      "manager-light",
      "manager-porcelain-light",
      "manager-dark",
      "manager-nebula-dark",
    ]);
    return {
      themeId,
      // These four wallpapers deliberately share one 2560x1440 composition. Keeping
      // the allow-list explicit prevents a future manager-* theme with different art
      // geometry from inheriting misaligned orbital highlights.
      managerMotion: managerMotionThemeIds.has(themeId.toLowerCase()),
      appearance,
      safeArea,
      taskMode,
      focusX: hasNumber(art.focusX) ? clamp(art.focusX) : null,
      focusY: hasNumber(art.focusY) ? clamp(art.focusY) : null,
      accent: safeAccent,
      colors,
      contrast: Number.isFinite(requestedContrast) && requestedContrast >= 70 && requestedContrast <= 100
        ? requestedContrast
        : 100,
      initialAspect: Number.isFinite(metadataRatio) && metadataRatio > 0 ? metadataRatio : null,
      artWidth: hasMetadataSize ? metadataWidth : null,
      artHeight: hasMetadataSize ? metadataHeight : null,
    };
  };

  const previous = window[STATE_KEY];
  previous?.motion?.destroy?.();
  if (previous?.observer) previous.observer.disconnect();
  if (previous?.timer) clearInterval(previous.timer);
  if (previous?.scheduler?.timeout) clearTimeout(previous.scheduler.timeout);
  if (previous?.artUrl) URL.revokeObjectURL(previous.artUrl);
  const artUrl = (() => {
    const comma = artDataUrl.indexOf(",");
    const binary = atob(artDataUrl.slice(comma + 1));
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
    const mime = /^data:([^;,]+)/.exec(artDataUrl)?.[1] || "image/png";
    return URL.createObjectURL(new Blob([bytes], { type: mime }));
  })();
  const config = normalizeConfig(rawConfig);
  let profile = {
    ...defaultProfile,
    aspect: config.initialAspect ?? defaultProfile.aspect,
  };
  const existingStyle = document.getElementById(STYLE_ID);
  if (existingStyle) {
    existingStyle.textContent = cssText;
    existingStyle.dataset.dreamVersion = "4";
  }

  const analyzeArt = () => new Promise((resolve) => {
    if (typeof Image !== "function") {
      resolve(defaultProfile);
      return;
    }
    const image = new Image();
    image.onload = () => {
      try {
        const width = 48;
        const height = Math.max(12, Math.round(width * image.naturalHeight / image.naturalWidth));
        const canvas = document.createElement("canvas");
        canvas.width = width;
        canvas.height = height;
        const context = canvas.getContext?.("2d", { willReadFrequently: true });
        if (!context) throw new Error("Canvas is unavailable");
        context.drawImage(image, 0, 0, width, height);
        const pixels = context.getImageData(0, 0, width, height).data;
        let count = 0;
        let totalRed = 0;
        let totalGreen = 0;
        let totalBlue = 0;
        let totalBrightness = 0;
        const samples = [];
        const sampleMap = new Array(width * height);
        for (let offset = 0; offset < pixels.length; offset += 4) {
          if (pixels[offset + 3] < 96) continue;
          const red = pixels[offset];
          const green = pixels[offset + 1];
          const blue = pixels[offset + 2];
          const light = (.2126 * red + .7152 * green + .0722 * blue) / 255;
          const sample = { red, green, blue, light, index: offset / 4 };
          samples.push(sample);
          sampleMap[sample.index] = sample;
          totalRed += red;
          totalGreen += green;
          totalBlue += blue;
          totalBrightness += light;
          count += 1;
        }
        if (!count) throw new Error("Image contains no opaque pixels");
        const average = [totalRed / count, totalGreen / count, totalBlue / count];
        const averageBrightness = totalBrightness / count;
        const information = (start, end) => {
          let total = 0;
          let totalSquared = 0;
          let edges = 0;
          let edgeCount = 0;
          let sampleCount = 0;
          for (let y = 0; y < height; y += 1) {
            for (let x = start; x < end; x += 1) {
              const sample = sampleMap[y * width + x];
              if (!sample) continue;
              total += sample.light;
              totalSquared += sample.light * sample.light;
              sampleCount += 1;
              const previousSample = x > start ? sampleMap[y * width + x - 1] : null;
              const above = y > 0 ? sampleMap[(y - 1) * width + x] : null;
              if (previousSample) { edges += Math.abs(sample.light - previousSample.light); edgeCount += 1; }
              if (above) { edges += Math.abs(sample.light - above.light); edgeCount += 1; }
            }
          }
          const mean = sampleCount ? total / sampleCount : 0;
          const variance = sampleCount ? Math.max(0, totalSquared / sampleCount - mean * mean) : 1;
          return Math.sqrt(variance) * .58 + (edgeCount ? edges / edgeCount : 1) * .42;
        };
        const zoneWidth = Math.max(1, Math.floor(width * .38));
        const leftInformation = information(0, zoneWidth);
        const rightInformation = information(width - zoneWidth, width);
        let safeArea = "center";
        if (leftInformation < rightInformation * .86) safeArea = "left";
        else if (rightInformation < leftInformation * .86) safeArea = "right";
        let focusWeight = 0;
        let focusX = 0;
        let focusY = 0;
        let accentWeight = 0;
        let accent = [0, 0, 0];
        for (const sample of samples) {
          const x = sample.index % width;
          const y = Math.floor(sample.index / width);
          const difference = Math.sqrt(
            (sample.red - average[0]) ** 2 +
            (sample.green - average[1]) ** 2 +
            (sample.blue - average[2]) ** 2,
          ) / 441.7;
          const saliency = .03 + difference ** 1.35;
          focusX += (x / Math.max(1, width - 1)) * saliency;
          focusY += (y / Math.max(1, height - 1)) * saliency;
          focusWeight += saliency;
          const max = Math.max(sample.red, sample.green, sample.blue);
          const min = Math.min(sample.red, sample.green, sample.blue);
          const saturation = max ? (max - min) / max : 0;
          const usableLight = 1 - Math.min(1, Math.abs(sample.light - .46) / .54);
          const weight = saturation ** 2 * (.15 + usableLight);
          accent[0] += sample.red * weight;
          accent[1] += sample.green * weight;
          accent[2] += sample.blue * weight;
          accentWeight += weight;
        }
        const resolvedAccent = accentWeight > 1
          ? accent.map((channel) => Math.round(channel / accentWeight))
          : average.map((channel) => Math.round(channel));
        let resolvedFocusX = clamp(focusX / focusWeight);
        if (safeArea === "left") resolvedFocusX = Math.max(.64, resolvedFocusX);
        if (safeArea === "right") resolvedFocusX = Math.min(.36, resolvedFocusX);
        resolve({
          appearance: averageBrightness >= .58 ? "light" : "dark",
          accent: resolvedAccent,
          focusX: resolvedFocusX,
          focusY: clamp(focusY / focusWeight),
          aspect: image.naturalWidth / Math.max(1, image.naturalHeight),
          luma: clamp(averageBrightness),
          safeArea,
        });
      } catch {
        resolve(defaultProfile);
      }
    };
    image.onerror = () => resolve(defaultProfile);
    image.src = artUrl;
  });

  const detectShellAppearance = () => {
    const root = document.documentElement;
    const body = document.body;
    const classes = `${root?.className || ""} ${body?.className || ""}`
      .toLowerCase()
      .replace(/\bdream-theme-(?:dark|light)\b/g, "");
    if (/\b(dark|electron-dark|theme-dark|appearance-dark)\b/.test(classes)) return "dark";
    if (/\b(light|electron-light|theme-light|appearance-light)\b/.test(classes)) return "light";

    const dataTheme = (
      root?.getAttribute?.("data-theme") ||
      root?.getAttribute?.("data-appearance") ||
      root?.getAttribute?.("data-color-mode") ||
      body?.getAttribute?.("data-theme") ||
      body?.getAttribute?.("data-appearance") ||
      ""
    ).toLowerCase();
    if (dataTheme.includes("dark")) return "dark";
    if (dataTheme.includes("light")) return "light";

    try {
      const hadSkin = root?.classList?.contains?.("codex-dream-skin");
      const savedSkinClasses = hadSkin
        ? ROOT_CLASSES.filter((className) => root.classList.contains(className))
        : [];
      samplingNativeShell = true;
      if (hadSkin) root.classList.remove(...ROOT_CLASSES);
      try {
        const colorScheme = getComputedStyle(root).colorScheme || "";
        if (colorScheme.includes("dark") && !colorScheme.includes("light")) return "dark";
        if (colorScheme.includes("light") && !colorScheme.includes("dark")) return "light";
      } finally {
        if (hadSkin) root.classList.add(...savedSkinClasses);
        observer?.takeRecords?.();
        samplingNativeShell = false;
      }
    } catch {
      samplingNativeShell = false;
    }
    try {
      return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    } catch {}
    return "light";
  };

  const createManagerMotion = (host) => {
    let canvas = document.getElementById(MOTION_CANVAS_ID);
    if (!canvas || canvas.parentElement !== host) {
      canvas?.remove();
      canvas = document.createElement("canvas");
      canvas.id = MOTION_CANVAS_ID;
      canvas.setAttribute("aria-hidden", "true");
      canvas.dataset.dreamMotion = "manager";
      canvas.dataset.dreamThemeId = config.themeId;
      host.appendChild(canvas);
    }

    const context = canvas.getContext?.("2d", { alpha: true, desynchronized: true });
    const normalizedThemeId = config.themeId.toLowerCase();
    const managerLightMotion = normalizedThemeId === "manager-light" ||
      normalizedThemeId === "manager-porcelain-light";
    const managerDarkMotion = normalizedThemeId === "manager-dark" ||
      normalizedThemeId === "manager-nebula-dark";
    // The four bundled wallpapers now share the dashboard's blue/violet galaxy rings.
    // Keep their moving packets on the same colour rails even when the surrounding Account
    // Manager chrome is porcelain green or deep-sea cyan.
    const palette = managerLightMotion
      ? ["#2b7eff", "#4e46f4", "#845cf4", "#2db9d5"]
      : managerDarkMotion
        ? ["#3779ff", "#7c4cff", "#db4cda", "#37c4ee"]
        : [
            config.colors.accent || config.accent || "#7c5cfc",
            config.colors.accentAlt || config.colors.accent || config.accent || "#c084fc",
            config.colors.secondary || config.colors.accentAlt || config.accent || "#f472b6",
            config.colors.highlight || config.colors.accentAlt || config.accent || "#22d3ee",
          ];
    const reducedQuery = window.matchMedia?.("(prefers-reduced-motion: reduce)") ?? null;
    const frameInterval = 1000 / 12;
    const maxPixels = 1500000;
    const maxPixelRatio = 1;
    const meteors = [
      { cycle: 11.2, offset: .03, start: [1.10, .02], control: [.92, .18], end: [.58, .66], trail: .17, length: .095, intensity: .96, color: 0 },
      { cycle: 14.8, offset: .26, start: [1.08, .78], control: [.93, .53], end: [.64, .16], trail: .18, length: .078, intensity: .84, color: 2 },
      { cycle: 9.6, offset: .48, start: [1.12, -.05], control: [.86, .10], end: [.57, .44], trail: .14, length: .071, intensity: .78, color: 3 },
      { cycle: 17.4, offset: .67, start: [1.12, .37], control: [.90, .78], end: [.61, .82], trail: .21, length: .086, intensity: .88, color: 2 },
      { cycle: 13.1, offset: .81, start: [1.05, .86], control: [.87, .55], end: [.65, .26], trail: .16, length: .066, intensity: .72, color: 0 },
      { cycle: 20.0, offset: .44, start: [1.06, .19], control: [.91, .42], end: [.68, .93], trail: .19, length: .061, intensity: .68, color: 1 },
    ];
    const orbitTracks = [
      { radiusX: 520, radiusY: 218, rotation: -12, color: 0, phase: .12, speed: .36, width: 18 },
      { radiusX: 456, radiusY: 302, rotation: 17, color: 1, phase: .42, speed: .29, width: 15 },
      { radiusX: 398, radiusY: 256, rotation: -34, color: 2, phase: .68, speed: -.33, width: 13 },
      { radiusX: 572, radiusY: 332, rotation: 7, color: 3, phase: .86, speed: .25, width: 8 },
    ];
    let cssWidth = 1;
    let cssHeight = 1;
    let pixelRatio = 1;
    let frameCount = 0;
    let lastPaintAt = 0;
    let frameTimer = null;
    let animationFrame = null;
    let resizeTimer = null;
    let scrollResumeTimer = null;
    let scrolling = false;
    let destroyed = false;
    let focused = typeof document.hasFocus === "function" ? document.hasFocus() : true;
    let status = "initializing";

    const setStatus = (value) => {
      if (status === value) return;
      status = value;
      canvas.dataset.dreamMotionStatus = value;
    };

    const resolveArtCover = () => {
      const sourceWidth = config.artWidth || 2560;
      const sourceHeight = config.artHeight || 1440;
      const positionX = clamp(config.focusX ?? profile.focusX, 0, 1);
      const positionY = clamp(config.focusY ?? profile.focusY, 0, 1);
      const scale = Math.max(cssWidth / sourceWidth, cssHeight / sourceHeight);
      const renderedWidth = sourceWidth * scale;
      const renderedHeight = sourceHeight * scale;
      const offsetX = (cssWidth - renderedWidth) * positionX;
      const offsetY = (cssHeight - renderedHeight) * positionY;
      const sourceCenterX = sourceWidth * .765;
      const sourceCenterY = sourceHeight * .435;
      return {
        sourceWidth,
        sourceHeight,
        positionX,
        positionY,
        scale,
        renderedWidth,
        renderedHeight,
        offsetX,
        offsetY,
        centerX: offsetX + sourceCenterX * scale,
        centerY: offsetY + sourceCenterY * scale,
      };
    };

    const snapshot = () => {
      const artCover = resolveArtCover();
      return {
        enabled: Boolean(context) && !destroyed,
        themeId: config.themeId,
        status,
        running: Boolean(frameTimer || animationFrame),
        reducedMotion: Boolean(reducedQuery?.matches),
        documentVisible: !document.hidden,
        focused,
        frameRate: 12,
        frameCount,
        scrolling,
        cssWidth,
        cssHeight,
        pixelRatio: Number(pixelRatio.toFixed(3)),
        backingWidth: canvas.width || 0,
        backingHeight: canvas.height || 0,
        palette: [...palette],
        focus: { x: config.focusX ?? profile.focusX, y: config.focusY ?? profile.focusY },
        artCover: { ...artCover },
        orbitGeometry: orbitTracks.map((track) => ({
          centerX: artCover.centerX,
          centerY: artCover.centerY,
          radiusX: track.radiusX * artCover.scale,
          radiusY: track.radiusY * artCover.scale,
          rotation: track.rotation,
        })),
      };
    };

    if (!context) {
      setStatus("canvas-unavailable");
      return {
        snapshot,
        testFrame: snapshot,
        destroy: () => {
          destroyed = true;
          setStatus("destroyed");
          canvas.remove();
        },
      };
    }

    const clearCanvas = () => {
      context.save();
      context.setTransform(1, 0, 0, 1, 0, 0);
      context.clearRect(0, 0, canvas.width, canvas.height);
      context.restore();
    };

    const orbitPoint = (centerX, centerY, radiusX, radiusY, rotation, angle) => {
      const localX = Math.cos(angle) * radiusX;
      const localY = Math.sin(angle) * radiusY;
      const cosine = Math.cos(rotation);
      const sine = Math.sin(rotation);
      return {
        x: centerX + localX * cosine - localY * sine,
        y: centerY + localX * sine + localY * cosine,
      };
    };

    const drawOrbitTrail = (geometry, headAngle, color, width) => {
      const segments = 8;
      const trailLength = .62;
      for (let index = 0; index < segments; index += 1) {
        const from = headAngle - trailLength + trailLength * index / segments;
        const to = headAngle - trailLength + trailLength * (index + 1) / segments;
        const start = orbitPoint(...geometry, from);
        const end = orbitPoint(...geometry, to);
        context.beginPath();
        context.moveTo(start.x, start.y);
        context.lineTo(end.x, end.y);
        context.lineWidth = width * (.55 + .45 * index / segments);
        context.strokeStyle = color;
        context.globalAlpha = .035 + .42 * (index / segments) ** 2;
        context.stroke();
      }
      const head = orbitPoint(...geometry, headAngle);
      const glow = context.createRadialGradient(head.x, head.y, 0, head.x, head.y, width * 7);
      glow.addColorStop(0, color);
      glow.addColorStop(.24, color);
      glow.addColorStop(1, "rgba(0, 0, 0, 0)");
      context.beginPath();
      context.arc(head.x, head.y, width * 7, 0, Math.PI * 2);
      context.fillStyle = glow;
      context.globalAlpha = .58;
      context.fill();
    };

    const drawOrbit = (time, still) => {
      const cover = resolveArtCover();
      const seconds = time / 1000;
      const baseAlpha = config.appearance === "light" ? .085 : .065;

      for (const track of orbitTracks) {
        const rotation = track.rotation * Math.PI / 180;
        const radiusX = track.radiusX * cover.scale;
        const radiusY = track.radiusY * cover.scale;
        const geometry = [cover.centerX, cover.centerY, radiusX, radiusY, rotation];
        const color = palette[track.color];
        const width = Math.max(1, track.width * cover.scale * .16);
        context.save();
        context.globalCompositeOperation = "source-over";
        context.translate(cover.centerX, cover.centerY);
        context.rotate(rotation);
        context.beginPath();
        context.ellipse(0, 0, radiusX, radiusY, 0, 0, Math.PI * 2);
        context.strokeStyle = color;
        context.lineWidth = Math.max(.65, width * .38);
        context.globalAlpha = baseAlpha;
        context.stroke();
        context.restore();

        context.globalCompositeOperation = "lighter";
        const start = track.phase * Math.PI * 2;
        const head = still ? start : start + seconds * track.speed;
        drawOrbitTrail(geometry, head, color, width);
      }
    };

    const smoothStep = (start, end, value) => {
      const normalized = clamp((value - start) / Math.max(.0001, end - start));
      return normalized * normalized * (3 - 2 * normalized);
    };

    const meteorPoint = (definition, phase) => {
      const value = clamp(phase);
      const inverse = 1 - value;
      return {
        x: cssWidth * (inverse * inverse * definition.start[0] +
          2 * inverse * value * definition.control[0] + value * value * definition.end[0]),
        y: cssHeight * (inverse * inverse * definition.start[1] +
          2 * inverse * value * definition.control[1] + value * value * definition.end[1]),
      };
    };

    const drawMeteor = (definition, phase, color) => {
      const opacity = smoothStep(0, .09, phase) * (1 - smoothStep(.84, 1, phase)) * definition.intensity;
      if (opacity <= .004) return;
      const tailPhase = Math.max(0, phase - definition.trail);
      const segmentCount = 4;
      const visualScale = clamp(definition.length / .075, .76, 1.28);
      let previousPoint = meteorPoint(definition, tailPhase);
      for (let index = 0; index < segmentCount; index += 1) {
        const nextPhase = tailPhase + (phase - tailPhase) * (index + 1) / segmentCount;
        const nextPoint = meteorPoint(definition, nextPhase);
        const strength = ((index + 1) / segmentCount) ** 1.34;
        context.beginPath();
        context.moveTo(previousPoint.x, previousPoint.y);
        context.lineTo(nextPoint.x, nextPoint.y);
        context.strokeStyle = color;
        context.lineWidth = Math.max(1.35, 4.6 * visualScale * (.3 + .7 * strength));
        context.globalAlpha = opacity * (.018 + .16 * strength);
        context.stroke();

        context.beginPath();
        context.moveTo(previousPoint.x, previousPoint.y);
        context.lineTo(nextPoint.x, nextPoint.y);
        context.lineWidth = Math.max(.55, 1.15 * visualScale * (.55 + .45 * strength));
        context.globalAlpha = opacity * (.06 + .56 * strength);
        context.stroke();
        previousPoint = nextPoint;
      }

      const headX = previousPoint.x;
      const headY = previousPoint.y;
      const headRadius = 7 * visualScale;
      const glow = context.createRadialGradient(headX, headY, 0, headX, headY, headRadius);
      glow.addColorStop(0, color);
      glow.addColorStop(.22, color);
      glow.addColorStop(1, "rgba(0, 0, 0, 0)");
      context.beginPath();
      context.arc(headX, headY, headRadius, 0, Math.PI * 2);
      context.fillStyle = glow;
      context.globalAlpha = .78 * opacity;
      context.fill();
    };

    const drawMeteors = (time, still) => {
      context.globalCompositeOperation = "lighter";
      if (still) {
        drawMeteor(meteors[0], .56, palette[meteors[0].color]);
        return;
      }
      const seconds = time / 1000;
      for (const definition of meteors) {
        const phase = (seconds / definition.cycle + definition.offset) % 1;
        drawMeteor(definition, phase, palette[definition.color]);
      }
    };

    const drawScene = (time, still = false) => {
      if (destroyed || !canvas.isConnected) return;
      clearCanvas();
      context.save();
      context.globalCompositeOperation = "source-over";
      context.lineCap = "round";
      context.lineJoin = "round";
      drawOrbit(time, still);
      drawMeteors(time, still);
      context.restore();
      frameCount += 1;
    };

    const resize = () => {
      if (destroyed) return;
      cssWidth = Math.max(1, Math.round(Number(window.innerWidth) || 1));
      cssHeight = Math.max(1, Math.round(Number(window.innerHeight) || 1));
      const requestedRatio = Math.min(maxPixelRatio, Math.max(1, Number(window.devicePixelRatio) || 1));
      const pixelBudgetRatio = Math.sqrt(maxPixels / Math.max(1, cssWidth * cssHeight));
      pixelRatio = Math.max(.25, Math.min(requestedRatio, pixelBudgetRatio));
      canvas.style.width = `${cssWidth}px`;
      canvas.style.height = `${cssHeight}px`;
      canvas.width = Math.max(1, Math.round(cssWidth * pixelRatio));
      canvas.height = Math.max(1, Math.round(cssHeight * pixelRatio));
      context.setTransform(canvas.width / cssWidth, 0, 0, canvas.height / cssHeight, 0, 0);
      drawScene(2180, true);
    };

    const cancelScheduledFrame = () => {
      if (frameTimer !== null) clearTimeout(frameTimer);
      if (animationFrame !== null) window.cancelAnimationFrame?.(animationFrame);
      frameTimer = null;
      animationFrame = null;
    };

    const canAnimate = () =>
      !destroyed && !reducedQuery?.matches && !document.hidden && focused && !scrolling;
    const requestNextFrame = () => {
      if (!canAnimate() || frameTimer !== null || animationFrame !== null) return;
      const elapsed = (window.performance?.now?.() ?? Date.now()) - lastPaintAt;
      const delay = Math.max(0, frameInterval - elapsed);
      frameTimer = setTimeout(() => {
        frameTimer = null;
        if (!canAnimate()) return;
        animationFrame = window.requestAnimationFrame((time) => {
          animationFrame = null;
          if (!canAnimate()) return;
          lastPaintAt = time;
          drawScene(time, false);
          requestNextFrame();
        });
      }, delay);
    };

    const syncActivity = () => {
      cancelScheduledFrame();
      if (destroyed) {
        setStatus("destroyed");
        return;
      }
      if (reducedQuery?.matches) {
        setStatus("reduced-motion");
        drawScene(2180, true);
        return;
      }
      if (document.hidden) {
        setStatus("document-hidden");
        return;
      }
      if (!focused) {
        setStatus("window-blurred");
        return;
      }
      if (scrolling) {
        setStatus("scrolling");
        return;
      }
      setStatus("running");
      requestNextFrame();
    };

    const onFocus = () => { focused = true; syncActivity(); };
    const onBlur = () => { focused = false; syncActivity(); };
    const onVisibilityChange = () => syncActivity();
    const onReducedMotionChange = () => syncActivity();
    const onScroll = () => {
      if (destroyed || reducedQuery?.matches || document.hidden || !focused) return;
      if (!scrolling) {
        scrolling = true;
        cancelScheduledFrame();
        setStatus("scrolling");
      }
      if (scrollResumeTimer !== null) clearTimeout(scrollResumeTimer);
      scrollResumeTimer = setTimeout(() => {
        scrollResumeTimer = null;
        scrolling = false;
        syncActivity();
      }, 200);
    };
    const onResize = () => {
      if (resizeTimer !== null) clearTimeout(resizeTimer);
      resizeTimer = setTimeout(() => {
        resizeTimer = null;
        resize();
        syncActivity();
      }, 100);
    };

    window.addEventListener?.("focus", onFocus);
    window.addEventListener?.("blur", onBlur);
    window.addEventListener?.("resize", onResize);
    document.addEventListener?.("visibilitychange", onVisibilityChange);
    document.addEventListener?.("scroll", onScroll, { capture: true, passive: true });
    if (typeof reducedQuery?.addEventListener === "function") {
      reducedQuery.addEventListener("change", onReducedMotionChange);
    } else {
      reducedQuery?.addListener?.(onReducedMotionChange);
    }

    const destroy = () => {
      if (destroyed) return;
      destroyed = true;
      cancelScheduledFrame();
      if (resizeTimer !== null) clearTimeout(resizeTimer);
      resizeTimer = null;
      if (scrollResumeTimer !== null) clearTimeout(scrollResumeTimer);
      scrollResumeTimer = null;
      scrolling = false;
      window.removeEventListener?.("focus", onFocus);
      window.removeEventListener?.("blur", onBlur);
      window.removeEventListener?.("resize", onResize);
      document.removeEventListener?.("visibilitychange", onVisibilityChange);
      document.removeEventListener?.("scroll", onScroll, true);
      if (typeof reducedQuery?.removeEventListener === "function") {
        reducedQuery.removeEventListener("change", onReducedMotionChange);
      } else {
        reducedQuery?.removeListener?.(onReducedMotionChange);
      }
      setStatus("destroyed");
      canvas.remove();
    };

    resize();
    syncActivity();
    return {
      destroy,
      snapshot,
      testFrame: (time = 2180) => {
        drawScene(Number.isFinite(Number(time)) ? Number(time) : 2180, true);
        return snapshot();
      },
    };
  };

  const discardMotion = () => {
    motion?.destroy?.();
    motion = null;
    const state = window[STATE_KEY];
    if (state?.installToken === installToken) state.motion = null;
  };

  const clearSkinDom = () => {
    discardMotion();
    const root = document.documentElement;
    root?.classList.remove(...ROOT_CLASSES);
    for (const property of ROOT_PROPERTIES) root?.style.removeProperty(property);
    document.querySelectorAll(".dream-home").forEach((node) => node.classList.remove("dream-home"));
    document.querySelectorAll(".dream-task").forEach((node) => node.classList.remove("dream-task"));
    document.querySelectorAll(".dream-home-shell").forEach((node) => node.classList.remove("dream-home-shell"));
    document.querySelectorAll(`.${HOME_UTILITY_CLASS}`).forEach((node) => node.classList.remove(HOME_UTILITY_CLASS));
    document.getElementById(STYLE_ID)?.remove();
    document.getElementById(CHROME_ID)?.remove();
  };

  const applyProfile = (root) => {
    const focusX = config.focusX ?? profile.focusX;
    const focusY = config.focusY ?? profile.focusY;
    const appearance = config.appearance === "auto" ? detectShellAppearance() : config.appearance;
    const focus = focusX < .4 ? "left" : focusX > .6 ? "right" : "center";
    const safeArea = config.safeArea === "auto" ? (profile.safeArea ||
      (focus === "left" ? "right" : focus === "right" ? "left" : "center")) : config.safeArea;
    const taskMode = config.taskMode === "auto"
      ? profile.aspect >= 2.25 ? "banner" : "ambient"
      : config.taskMode;
    const accent = config.accent || `rgb(${profile.accent.join(" ")})`;
    const accentRgb = (() => {
      const match = /^#([\da-f]{6})$/i.exec(accent);
      if (!match) return profile.accent;
      return [
        Number.parseInt(match[1].slice(0, 2), 16),
        Number.parseInt(match[1].slice(2, 4), 16),
        Number.parseInt(match[1].slice(4, 6), 16),
      ];
    })();
    const accentInk = luminance(...accentRgb) > .42 ? "rgb(26 24 28)" : "rgb(250 248 251)";
    root.classList.toggle("dream-manager-motion", config.managerMotion);
    root.classList.toggle("dream-theme-light", appearance === "light");
    root.classList.toggle("dream-theme-dark", appearance === "dark");
    root.classList.toggle("dream-art-wide", profile.aspect >= 1.75);
    root.classList.toggle("dream-art-standard", profile.aspect < 1.75);
    for (const value of ["left", "center", "right"]) {
      root.classList.toggle(`dream-focus-${value}`, focus === value);
    }
    for (const value of ["left", "center", "right", "none"]) {
      root.classList.toggle(`dream-safe-${value}`, safeArea === value);
    }
    for (const value of ["ambient", "banner", "off"]) {
      root.classList.toggle(`dream-task-${value}`, taskMode === value);
    }
    root.style.setProperty("--dream-art", `url("${artUrl}")`);
    root.style.setProperty(
      "--dream-art-position",
      `${(focusX * 100).toFixed(2)}% ${(focusY * 100).toFixed(2)}%`,
    );
    root.style.setProperty("--dream-focus-x", String(focusX));
    root.style.setProperty("--dream-focus-y", String(focusY));
    root.style.setProperty("--dream-accent", accent);
    root.style.setProperty("--dream-accent-ink", accentInk);
    const variableMap = {
      background: "--dream-canvas",
      panel: "--dream-surface",
      panelAlt: "--dream-surface-raised",
      accentAlt: "--dream-accent-alt",
      secondary: "--dream-secondary",
      highlight: "--dream-highlight",
      text: "--dream-text",
      muted: "--dream-text-muted",
      line: "--dream-line",
    };
    for (const [colorName, propertyName] of Object.entries(variableMap)) {
      const color = config.colors[colorName];
      if (color) root.style.setProperty(propertyName, color);
      else root.style.removeProperty(propertyName);
    }
    if (config.colors.background) root.style.setProperty("--dream-sidebar", config.colors.background);
    else root.style.removeProperty("--dream-sidebar");
    if (config.colors.line) {
      root.style.setProperty(
        "--dream-line-soft",
        `color-mix(in oklab, ${config.colors.line} 72%, transparent)`,
      );
    } else {
      root.style.removeProperty("--dream-line-soft");
    }
    root.style.setProperty("--dream-image-contrast", (config.contrast / 100).toFixed(2));
    root.style.setProperty("--dream-image-luma", profile.luma.toFixed(3));
  };

  const ensure = () => {
    if (window.__CODEX_DREAM_SKIN_DISABLED__) return;
    const root = document.documentElement;
    if (!root || !document.body) return;

    const shellMain = document.querySelector("main.main-surface");
    const shellSidebar = document.querySelector("aside.app-shell-left-panel");
    if (!shellMain || !shellSidebar) {
      clearSkinDom();
      return;
    }

    root.classList.add("codex-dream-skin");
    applyProfile(root);

    let style = document.getElementById(STYLE_ID);
    if (!style) {
      style = document.createElement("style");
      style.id = STYLE_ID;
      (document.head || root).appendChild(style);
    }
    if (style.dataset.dreamVersion !== "4") {
      style.textContent = cssText;
      style.dataset.dreamVersion = "4";
    }

    const home = document.querySelector('[role="main"]:has([data-testid="home-icon"])');
    for (const candidate of document.querySelectorAll('[role="main"]')) {
      candidate.classList.toggle("dream-home", candidate === home);
      candidate.classList.toggle("dream-task", candidate !== home);
    }
    const utilityBars = new Set(home ? home.querySelectorAll('[class*="_homeUtilityBar_"]') : []);
    for (const candidate of document.querySelectorAll(`.${HOME_UTILITY_CLASS}`)) {
      if (!utilityBars.has(candidate)) candidate.classList.remove(HOME_UTILITY_CLASS);
    }
    for (const candidate of utilityBars) candidate.classList.add(HOME_UTILITY_CLASS);
    shellMain.classList.toggle("dream-home-shell", Boolean(home));

    let chrome = document.getElementById(CHROME_ID);
    if (!chrome || chrome.parentElement !== document.body) {
      chrome?.remove();
      chrome = document.createElement("div");
      chrome.id = CHROME_ID;
      chrome.setAttribute("aria-hidden", "true");
      document.body.appendChild(chrome);
    }
    chrome.classList.toggle("dream-home-shell", Boolean(home));
    if (config.managerMotion) {
      if (!motion) motion = createManagerMotion(chrome);
    } else {
      discardMotion();
      document.getElementById(MOTION_CANVAS_ID)?.remove();
    }
    const state = window[STATE_KEY];
    if (state?.installToken === installToken) state.motion = motion;
  };

  const cleanup = () => {
    const state = window[STATE_KEY];
    if (state?.installToken !== installToken) return false;
    window.__CODEX_DREAM_SKIN_DISABLED__ = true;
    discardMotion();
    clearSkinDom();
    state?.observer?.disconnect();
    if (state?.timer) clearInterval(state.timer);
    if (state?.scheduler?.timeout) clearTimeout(state.scheduler.timeout);
    if (state?.artUrl) URL.revokeObjectURL(state.artUrl);
    delete window[STATE_KEY];
    return true;
  };

  const scheduler = { timeout: null };
  const scheduleEnsure = () => {
    if (scheduler.timeout) clearTimeout(scheduler.timeout);
    scheduler.timeout = setTimeout(() => {
      scheduler.timeout = null;
      ensure();
    }, 180);
  };
  observer = new MutationObserver(() => {
    if (samplingNativeShell) return;
    scheduleEnsure();
  });
  observer.observe(document.documentElement, {
    childList: true,
    subtree: true,
    attributes: true,
    attributeFilter: ["class", "data-theme", "data-appearance", "data-color-mode"],
  });
  const timer = setInterval(ensure, 5000);
  window[STATE_KEY] = {
    ensure, cleanup, observer, timer, scheduler, artUrl, profile, config, motion, installToken, version: "1.2.0",
  };
  ensure();
  analyzeArt().then((result) => {
    const state = window[STATE_KEY];
    if (state?.installToken !== installToken || window.__CODEX_DREAM_SKIN_DISABLED__) return;
    profile = result;
    state.profile = result;
    ensure();
  });
  return { installed: true, version: "1.2.0", adaptive: true };
})(__DREAM_CSS_JSON__, __DREAM_ART_JSON__, __DREAM_THEME_JSON__)
