# 2.3.47

- 新增账号和更新 Session 窗口显示完整只读地址 `https://chatgpt.com/api/auth/session`。点击可全选，使用 Ctrl+C 或右键复制；不再通过链接自动启动浏览器。
- 小额测试保留 HTTP 状态码，识别 Session 的 401/403，显示中文原因和“通过 ChatGPT 登录（官方）”的授权指引。支持顶层 `detail` 错误格式；限流及模型不支持错误保持独立说明。
- 导入完成、本地凭据状态和模型目录错误提示明确区分“格式及有效期有效”和“模型调用获得授权”。网页 Session 可能可以读取额度/模型目录，但在实际 Responses 模型请求中仍被服务端拒绝。
- 此版本不赋予任何额外权限，不伪造或兑换 OAuth 凭据，也不会为了导入验证自动发出模型请求。被官方拒绝的 Session 仍需用户通过官方登录完成 Codex 授权，或更新凭据并确认工作区权限。
- 保留 2.3.46 的表单布局、Session 下拉入口，以及 2.3.45 本地中转兼容等功能。macOS 仅同步版本号，内置 Codex CLI 保持 0.163.0。

验证覆盖：Session 的 HTTP 401/403、顶层 Unauthorized、429 不误判、错误内容不回显；既有 Session 导入、原生认证和布局回归继续运行。问题定位包含通过原账号配置代理的官方额度/模型目录只读查询，以及一次同模型、同凭据的直接模型请求，确认服务端拒绝可在绕过本地网关时复现。

认证模式参考：[OpenAI 官方 App Server 文档](https://learn.chatgpt.com/docs/app-server#authentication-modes)。官方 ChatGPT 登录管理 OAuth 授权与刷新；外部 Token 模式由宿主负责凭据生命周期，不保证任意网页 Token 都被模型端点接受。
