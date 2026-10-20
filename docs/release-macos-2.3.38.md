# macOS 2.3.38

- 恢复 GitHub 的 Apple Silicon 下载，版本从 1.1.5 更新到与当前 Windows 相同的 2.3.38。
- 内置官方 CLI 从 0.144.1 更新至 0.154.0，网关的最低客户端版本标识同时更新。Electron 更新到 43.8.1；依赖锁文件同步安全修复。
- 新增 AT 与兼容 API 的在线模型目录同步。管理器启动、每 15 分钟和账号启动前检查；AT 先读取自身工作区身份再查询官方目录，兼容 API 只导入服务端列出的编码模型，不将内置名单当作授权证明。
- 保留模型默认值、用户手动目录与完整 AT 元数据，保留最后可用目录，对凭据变化后的旧请求拒绝写入。OAuth 继续由官方客户端管理目录。
- 发布工作流同时构建两个平台，生成含 windows/macos 的更新清单，新下载资产齐全后再清理旧资产，以后发布不会再丢弃 Mac 下载。
- 自动验证包括现有 Mac 回归、模型同步专项、固定上游载荷哈希、隐私扫描和安装包源码一致性。Apple Silicon runner 另验证真实安装、本机 ad-hoc 签名、CLI 执行与界面加载。

本版本仍面向 Apple Silicon，未添加 Intel 支持；无 Developer ID 签名或公证。macOS 保留其独立实现，Windows 的双登录、逐账号节点、轮换、侧栏增强未在此次移植。真实账号登录和生成未在用户 Mac 上实测。

构建平台依据：[GitHub 的 macOS arm64 runner](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)。
