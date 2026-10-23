# macOS 构建说明

这是 Codex Account Manager 的独立 macOS Electron 构建。最终用户不需要源码、Node.js，也不需要另行安装 Codex CLI；安装包内置并优先使用固定版本的官方 Apple Silicon CLI。系统中已有的 CLI 仅作为开发版或内置载荷不可用时的后备。

macOS 当前版本为 2.3.39，保留账号独立用量与额度、状态与凭据、聊天记录、项目与代理设置、官方 OAuth、桌面/终端启动和主题功能。AT、兼容 API 在线模型目录在启动管理器及每 15 分钟检查，启动账号时复用短期有效目录或重新查询；客户端版本变化时重新验证目录，保留模型选择与手动目录。网络失败不覆盖旧目录。AT 查询不读取静态目录来判断服务器模型，按自己的凭据和工作区请求官方接口。macOS 使用系统配置中的代理，暂未移植 Windows 的逐账号节点管理、双登录入口、轮换和侧栏增强。

每个账号使用独立 `CODEX_HOME`。桌面应用发现兼容 `ChatGPT.app` 与旧版 `Codex.app`，并校验 Codex Bundle ID、Apple 代码签名和 OpenAI Team ID。默认主题启动不会开放远程调试端口，只有主题操作使用独立调试配置。

Access Token 账号通过独立后台网关访问 `127.0.0.1:8317`。网关只转发到固定的 ChatGPT 后端，校验 PAT 对应的工作区并注入账号标识；它必须使用“系统配置”中验证成功的上游代理，代理缺失或规则回退为直连时会拒绝请求。后台网关与管理器窗口分离，因此管理器退出后，已启动的终端和 ChatGPT（Codex）桌面仍可继续使用；覆盖安装和卸载会先正常退出旧管理器，再精确停止旧网关。

## 版本固定

- Electron `43.8.1`
- Codex CLI `0.163.0`
- `@electron/get` `5.0.0`
- 目标架构：`darwin-arm64`（Apple Silicon）
- 最低系统：macOS 12

## Windows 构建

在 PowerShell 中运行：

```powershell
cd .\macos
.\scripts\Build-macOS.ps1 -ReleaseDate 20261020
```

也可以手动运行：

```powershell
npm ci --ignore-scripts
npm run build:mac -- --date 20261020
```

默认生成：

- `项目父目录\CodexAccountManager-macOS-一键安装版-YYYYMMDD.zip`
- `项目父目录\CodexAccountManager-macOS-一键安装版-YYYYMMDD.zip.sha256`

Windows 构建机不会展开 macOS `.app`，因此不需要 Windows 符号链接权限。发布包保留经过 Electron 官方 SHA256 校验的原始 macOS ZIP；一键安装脚本在 Mac 上用 `ditto` 原生解包，再注入 `app.asar` 和图标、修改 `Info.plist`、删除原 `ElectronAsarIntegrity`，最后执行本机 ad-hoc 签名。

## 发布内容

- `payload/electron-v43.8.1-darwin-arm64.zip`
- `payload/openai-codex-0.163.0-darwin-arm64.tgz`
- `payload/app.asar`
- `payload/AppIcon.icns`
- `一键安装.command`
- `卸载.command`
- `README.md`
- `SHA256SUMS.txt`

## 安全与隐私

构建会解开 `app.asar` 执行文本与文件名扫描，阻止账号文件、`auth.json`、API Key、Access Token、邮箱、Windows 用户路径、sessions/JSONL/SQLite 等内容进入发布包。默认配置必须为空白，应用首次启动时在当前 Mac 的用户目录中创建数据。构建还会固定校验官方 Codex CLI 的 SHA256 和精确文件清单；安装时将原生 CLI 放入应用资源目录并随整个应用执行本机 ad-hoc 签名。

构建结果未经过 Apple Developer ID 签名与公证；一键安装脚本只执行当前 Mac 可用的 ad-hoc 签名。发布前仍应在 Apple Silicon 真机上做一次安装、启动、登录、CLI 调用和卸载验证。正式分发建议在 Mac 构建机上使用 Developer ID Application 签名并提交 Apple 公证。

GitHub Actions 使用 Apple Silicon 原生 runner 执行测试、安装包隐私及哈希检查、安装、签名检查、内置 CLI 和实际界面加载测试。真实账号登录与模型生成需要用户 Mac 验证。两平台都成功后才更新包含 `windows`、`macos` 两项的自动更新清单；旧 Mac 1.1.5 可以据此发现新版。

## 清理缓存

```powershell
npm run clean:mac
```

该命令只清理由异常中断留下、且名称严格匹配 `cam-macos-build-*` 的系统临时构建目录，不删除源码或用户数据。安装过程完全离线，不会再下载 Codex CLI。
