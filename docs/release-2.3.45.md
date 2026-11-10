# 2.3.45

- 修复 Windows Codex Desktop 通过 HTTP 中转时，两种无 `call_id` 的空页面状态引发 `function_call_output requires call_id` 的问题。兼容处理加入管理器本地网关，不依赖特定中转站部署补丁。
- 只识别 `agent.codex_apps_open_page` 的 `page_id:null` 与 `agent.codex_apps_selected_page_part_1_of_1` 的 `page_id:null/document_view:null`；仅作用于 Codex Desktop 的 Responses 请求。真实页面、真实工具、有效调用编号、未知字段和其他客户端不被清理。
- 不伪造调用编号；保留工具调用与回传配对、`previous_response_id`、模型与元数据，支持 identity/gzip/deflate/br/zstd 请求路径。日志仅记录清理数量。
- Windows API 账号无论是否轮换或绑定独立节点，启动时均使用本地网关，再转发至账号配置的上游。普通 API 登录及 API + ChatGPT 语音/手机功能登录均覆盖；未绑定代理且未启用网关路由的官方 OAuth 保持原生连接。
- 网关协议升级为 v15；保留 v14 状态读取，旧网关完成请求并安全升级前不允许新兼容启动。关闭网关时明确提示，不静默退回直连。
- 保留 2.3.44 的 Codex++ 集成移除、OAuth 额度回读，以及模型同步、文件/Session 导入、逐账号代理和 10%/3% 轮换规则。macOS 仅同步版本号；内置 CLI 仍为 0.163.0。

升级生效：自行重启管理器，等待本地网关就绪，再从管理器点击所用 API 账号的启动按钮，迁移旧版直连配置。无需重新导入账号或改动中转站。不修改历史聊天记录；本补丁不保证所有第三方中转的其他协议差异均兼容。

验证覆盖：新增单元回归及未修补 HTTP 中转集成测试（原请求直连复现 400，网关转发后完成工具调用和回传），五种编码、真实页面与其他客户端保留、旧直连配置迁移、API + OAuth 身份保留、v14/v15 升级检查及禁用网关检查。GitHub Windows 发布流程加入兼容集成测试门禁。

本地 Windows Release 构建、完整 `--self-test`、启动/模型/导入/轮换专项回归，以及模型目录、启动预检和原生 CLI Session 认证集成测试均通过。测试使用隔离账号和本机模拟服务，不调用用户实际中转消耗额度。历史静态脚本 `Test-CodexAccountSwitcher.ps1` 的旧源码定位问题见 2.3.44 更新说明；此次以完整程序自检和实际 HTTP 路径回归为准。
