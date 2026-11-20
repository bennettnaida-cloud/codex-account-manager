# 2.4.9

Windows Codex Desktop 就绪检测修复与启动切号提速更新：

- 修复新版 Codex Desktop（26.1117+）一直未确认就绪的问题：新版 Codex 将本地 app-server 迁移至独立 utility 进程，主日志改由 `[electron-message-handler] remote_connections.connection_state_changed` / `manager_state_set`（`hostId=local`、`rendererWindowAppearance=primary`、`state=connected` / `nextState=connected`）上报本地连接状态，不再输出旧版 `[AppServerConnection] ... transport=stdio`。现已同时兼容新旧日志格式（含技能图标非致命 `WARN` 负载），主页面路由与 `ready` 完成握手后立即确认就绪并解锁按钮，并补充了对应的日志就绪自检用例。
- 启动与切号速度优化：
  - **模型目录本地缓存快路径**：交互启动时，若该账号（兼容 API / OAuth / Access Token）在 15 分钟内已有通过凭据哈希校验的本地模型目录，跳过阻塞式在线 `/models` 轮询直接启动（后台定时同步仍保持 15 分钟自动更新；首次未同步、凭据变更或 CLI 显式 `--launch-model-preflight` 仍强制在线校验）；官方兼容模型模板已有本地副本时改为后台刷新，不再阻塞启动。
  - **缩短旧客户端优雅关闭等待**：Electron 客户端在 Windows 下通常不响应 `WM_CLOSE` 自动退出，将优雅退出等待从 `8s` 调整为 `1.5s`，切换后交接休眠从 `300ms` 降至 `100ms`、静默轮询间隔从 `200ms` 降至 `100ms`，单次切号平均节省约 6.5 秒。
  - **插件清单防重复扫描**：缓存 `.codex\.tmp\plugins\plugins` 目录的文件数与最新修改时间，未变化时跳过逐个 JSON 解析。
- 保留 2.4.6–2.4.8 系统配置与模型价格页重构、官方 16 模型价格解析、额度展示去噪及浅色主题提亮，2.4.5 宇宙星空背景与 HTTP 413 压缩重试（`request-boundary-v19`）等既有能力。

以上修复与提速改动适用于 Windows；macOS 同步版本号。本地构建更新桌面和任务栏快捷方式，不自动关闭正在运行的 Codex 客户端。
