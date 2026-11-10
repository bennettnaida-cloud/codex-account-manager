# 2.3.44

- 移除 Windows 管理器的 Codex++ 启动按钮、启动器查找、桥接等待、进程处理、隐藏计划任务生成与修复命令。
- 新配置默认使用官方 Codex；旧版 WindowsClientMode=0 在读取和保存时迁移为官方 Codex。账号卡片重新分配按钮空间，保留官方 Codex、CLI 和“语音 / 手机”入口。
- 官方客户端启动日志使用 windows-client-launch-diagnostics.log；保留官方客户端的独立外观功能。
- 合并 2.3.43 的导入 OAuth 额度读取修复：对有效但缺少 last_refresh 的凭据，在 app-server 内存中绑定真实 Bearer 与所属工作区，不改写原始凭据，不伪造刷新时间；到期凭据仍走原生刷新路径。
- 保留启动前在线模型同步、网页版 Session/sub2api/CPA 导入、逐账号代理，以及 5h 剩余 ≤10% / 仅周额度剩余 ≤3% 的轮换规则。
- 本次移除的是管理器集成，不卸载用户单独安装的软件，也不删除其账号或聊天数据。旧版已注册的外部启动任务不再由管理器调用。
- macOS 同步版本号；内置 Codex CLI 保持 0.163.0。

验证：Windows Release 构建、完整 `--self-test`（含旧配置迁移和导入 OAuth 额度认证）、启动/导入/模型/轮换专项自测、三个本地 HTTP/原生 CLI 集成测试，以及安装包隐私扫描、隔离安装和安装后自检均通过。历史 `Test-CodexAccountSwitcher.ps1` 仍有此前遗留的源码定位断言：它在主文件查找已迁至 `CodexCliService.Startup.cs` 的官方启动方法，未完成全量运行；实际启动行为由通过的 `--startup-self-test` 覆盖。
