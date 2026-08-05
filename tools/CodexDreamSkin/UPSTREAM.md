# Codex Dream Skin runtime

This directory vendors the Windows runtime from:

- Project: https://github.com/Fei-Away/Codex-Dream-Skin
- Core Windows runtime commit: `a1c48b3a84cc64532196e624fdf33ee1277cb018`

Preset provenance is intentionally split from the runtime revision:

- `preset-midnight-aurora`, `preset-sakura-dawn`, `preset-amber-dusk`,
  `preset-forest-mist`, and `preset-cyber-neon` are the complete five-pack
  set pinned from the core/runtime snapshot above.
- `preset-arina-hashimoto` and `preset-gothic-void-crusade` are the two
  current installable upstream packs from commit
  `3af1d6d62f3a0388cc640d2f497ac3100998938e`.
- The current upstream UI screenshots are bundled only for the in-app preview;
  the corresponding `background.jpg` files are the runtime wallpapers. See
  `assets/PRESET-PROVENANCE.md` for exact path mapping and
  `assets/UPSTREAM-PRESETS-NOTICE.md` for third-party-art and redistribution
  terms. These current assets are included for this user's personal local
  installation; their inclusion does not grant redistribution rights.
Local changes provide the four Account Manager palettes, both upstream preset
sets, custom local-photo themes, Windows ten-color theme mapping, and a
no-shortcut installer used only by Codex Account Manager.
