# Codex Account Manager for macOS

此安装包是 Apple Silicon (`arm64`) 原生完整版，不需要源码，不要求预装 Node.js 或 Codex CLI。包内保留经过固定 SHA256 校验的官方 Electron 与 Codex CLI payload；安装时由 macOS 原生组装应用、保留 Framework 符号链接与权限，并执行本机 ad-hoc 签名。应用优先使用包内固定版本的官方 CLI，避免不同电脑的全局安装状态影响登录、额度和终端功能。

应用包含账号独立登录、官方 ChatGPT OAuth、状态与凭据、逐账号用量和额度、聊天记录、项目与代理设置、ChatGPT（Codex）桌面应用/终端启动、Codex 主题和自动刷新。所有账号凭据与会话仍只保存在当前 Mac 的用户目录中。

Access Token 模式使用本机 `127.0.0.1:8317` 的独立后台网关，并要求“系统配置”中存在可用的 HTTP 或 SOCKS5 上游代理。网关会拒绝 DIRECT 回退，不会在代理失效时悄悄直连。它会在管理器退出后继续为已经启动的 Codex CLI 和桌面应用服务；再次打开管理器会复用同版本网关。覆盖安装和卸载脚本会先请求旧管理器正常退出，再只停止命令行与本应用路径严格匹配的网关进程，不会使用宽泛的 `pkill`。

新版 Codex 桌面应用可能显示为 `ChatGPT.app`，旧版显示为 `Codex.app`。管理器会校验应用包的 Bundle ID、Apple 代码签名和 OpenAI Team ID；普通 ChatGPT 应用（`com.openai.chat`）不包含这套 Codex 桌面启动接口，不能用于账号隔离启动。不要重命名或修改 `/Applications` 中的应用包；如果校验失败，请删除被修改的应用，更新 Codex CLI 后运行 `codex app` 重新安装。

## 一键安装

1. 解压整个 ZIP，不要只单独拖出脚本。
2. 双击 `一键安装.command`。脚本会确认当前 Mac 是 Apple Silicon 后安装应用。
3. 如果 macOS 第一次阻止脚本运行，请按住 Control 点击脚本，选择“打开”；也可以打开“终端”，把脚本拖入后按回车。

安装程序优先放到 `/Applications`；没有写入权限时会使用 `~/Applications`。它只替换应用本体，不会删除或覆盖账号、Codex 会话与历史记录。若旧管理器无法正常退出或端口 `8317` 仍被其它程序占用，安装会中止并保留旧应用。

## 未签名版本说明

这个离线包没有 Apple Developer ID 签名和公证。安装脚本只清除本应用的 `com.apple.quarantine` 下载隔离标记，并在当前 Mac 上执行一次本机 ad-hoc 签名；不会关闭 Gatekeeper，也不会修改系统安全策略。若公司设备由管理员策略管控，仍可能需要管理员批准。

## 卸载

双击 `卸载.command`。卸载会先正常退出管理器并停止本应用的后台网关，然后只删除应用程序；以下数据会保留：

- `~/Library/Application Support/Codex Account Manager`
- `~/.codex`
- `~/.codex-accounts`

## 完整性校验

`SHA256SUMS.txt` 记录 Electron、Codex CLI 原始 payload、`app.asar`、图标和安装脚本的 SHA256。ZIP 同目录的 `.sha256` 文件用于校验整个压缩包。

终端中可运行：

```bash
shasum -a 256 -c CodexAccountManager-macOS-一键安装版-*.zip.sha256
```

## 系统要求

- 管理器本体：macOS 12 Monterey 或更高版本
- 当前 ChatGPT（Codex）桌面应用：macOS 14 Sonoma 或更高版本；旧系统仍可使用终端启动
- Apple Silicon Mac（M1/M2/M3/M4/M5 或后续型号）
- 使用在线登录、API 或 Codex 服务时需要网络连接
