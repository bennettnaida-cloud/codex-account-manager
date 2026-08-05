# macOS 构建说明

这是 Codex Account Manager 的独立 macOS Electron 构建。最终用户不需要源码、Node.js，也不需要另行安装 Codex CLI；安装包内置并优先使用固定版本的官方 Apple Silicon CLI。系统中已有的 CLI 仅作为开发版或内置载荷不可用时的后备。

macOS 1.1.5 版已补齐账号独立用量与额度、状态与凭据、聊天记录、项目与代理设置、官方 ChatGPT OAuth、ChatGPT（Codex）桌面应用/终端启动、Codex 主题和自动刷新。每个账号使用独立 `CODEX_HOME`，账号、凭据、额度与会话归属不会混用。桌面应用发现兼容新版 `ChatGPT.app` 与旧版 `Codex.app`，并校验官方 Codex Bundle ID、Apple 代码签名和 OpenAI Team ID，避免把普通或被修改的 ChatGPT 应用误启动成白屏。默认主题启动不会开放远程调试端口，只有主题操作使用独立调试配置。

Access Token 账号通过独立后台网关访问 `127.0.0.1:8317`。网关只转发到固定的 ChatGPT 后端，校验 PAT 对应的工作区并注入账号标识；它必须使用“系统配置”中验证成功的上游代理，代理缺失或规则回退为直连时会拒绝请求。后台网关与管理器窗口分离，因此管理器退出后，已启动的终端和 ChatGPT（Codex）桌面仍可继续使用；覆盖安装和卸载会先正常退出旧管理器，再精确停止旧网关。

## 版本固定

- Electron `43.1.1`
- Codex CLI `0.144.1`
- `@electron/get` `5.0.0`
- 目标架构：`darwin-arm64`（Apple Silicon）
- 最低系统：macOS 12

## Windows 构建

在 PowerShell 中运行：

```powershell
cd .\macos
.\scripts\Build-macOS.ps1 -ReleaseDate 20260715
```

也可以手动运行：

```powershell
npm install --no-package-lock
npm run build:mac -- --date 20260715
```

默认生成：

- `项目父目录\CodexAccountManager-macOS-一键安装版-YYYYMMDD.zip`
- `项目父目录\CodexAccountManager-macOS-一键安装版-YYYYMMDD.zip.sha256`

Windows 构建机不会展开 macOS `.app`，因此不需要 Windows 符号链接权限。发布包保留经过 Electron 官方 SHA256 校验的原始 macOS ZIP；一键安装脚本在 Mac 上用 `ditto` 原生解包，再注入 `app.asar` 和图标、修改 `Info.plist`、删除原 `ElectronAsarIntegrity`，最后执行本机 ad-hoc 签名。

## 发布内容

- `payload/electron-v43.1.1-darwin-arm64.zip`
- `payload/openai-codex-0.144.1-darwin-arm64.tgz`
- `payload/app.asar`
- `payload/AppIcon.icns`
- `一键安装.command`
- `卸载.command`
- `README.md`
- `SHA256SUMS.txt`

## 安全与隐私

构建会解开 `app.asar` 执行文本与文件名扫描，阻止账号文件、`auth.json`、API Key、Access Token、邮箱、Windows 用户路径、sessions/JSONL/SQLite 等内容进入发布包。默认配置必须为空白，应用首次启动时在当前 Mac 的用户目录中创建数据。构建还会固定校验官方 Codex CLI 的 SHA256 和精确文件清单；安装时将原生 CLI 放入应用资源目录并随整个应用执行本机 ad-hoc 签名。

构建结果未经过 Apple Developer ID 签名与公证；一键安装脚本只执行当前 Mac 可用的 ad-hoc 签名。发布前仍应在 Apple Silicon 真机上做一次安装、启动、登录、CLI 调用和卸载验证。正式分发建议在 Mac 构建机上使用 Developer ID Application 签名并提交 Apple 公证。

## 清理缓存

```powershell
npm run clean:mac
```

该命令只清理由异常中断留下、且名称严格匹配 `cam-macos-build-*` 的系统临时构建目录，不删除源码或用户数据。安装过程完全离线，不会再下载 Codex CLI。
