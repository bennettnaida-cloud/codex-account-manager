# 2.3.23 Windows 启动兼容性修复

## 根因与修复

- 26.924 的 app-server 位于用户目录 `OpenAI/Codex/bin/<version>/codex.exe`。旧检测只接受 MSIX 目录，窗口已经就绪却被误报为 runtime-not-ready。新增只读检测按本次主进程 PID、启动时间、直接父子关系、限定目录和有效 OpenAI Authenticode 签名验证。关闭进程的白名单没有扩大。
- 管理器首次加载时无条件要求已有 Codex 重启；JSON 缩进变化也可能触发重启。现在只在可识别的当前身份分类内容实际改变时记录重载要求，更新其他身份不影响可见客户端，重载标记使用版本号式确认，避免并发同步被旧操作清掉。
- 普通与语音/手机入口统一使用一次系统激活，项目链接作为 initial argv 交给 Codex 官方队列。已有同账号窗口只激活，不另开聊天；后台观察器不再投递延迟新聊天、抢焦点或重启。尚在初始化的同账号进程也不重复激活。
- 首次 await 前建立点击排他与忙状态，并显示阶段/耗时。准备阶段和实际切号仍保留凭据校验、安全停机与原子投放。
- 窗口识别验证 Codex 包身份。删除同步 PrintWindow 探测，基础就绪不再依赖截图。进程退出、无法观测、仍在初始化分别记录，检测超时不授权重新启动。
- 超时降级保留原始 PID/start-time；不会用“开始补充等待的时刻”排除真正的客户端。

## 可选 Fast 与官方目录

复核 26.924.2738.0 的资源：visibility 和 serviceTierForRequest 仍在 app-initial；priority/default、配置读写和选项定义已经分拆至 app-shared。旧的五个替换点和四个语义锚点不再构成单资源的完整契约。因此没有简单改 hash、放宽校验或替换文件名；当前拆分结构安全跳过可选 Fast UI 补丁。

两种基础启动不再等待补丁，也不授权启动后的 Page.reload。已支持的完整契约可由被动桥接在后续自然资源加载时处理；不为了增强强制刷新当前页面。双登录身份、语音/手机配置、模型路由及原有服务档位配置不变。**不承诺 26.924 的旧版额外 Fast UI 已恢复**。

目录同步保留未知官方字段；遇到新的 sidebar schema 或字段类型变化时不写入，避免用旧格式覆盖新官方状态。计算期间发现官方更新了状态文件则跳过本次投放。未知结构可能暂不跨账号同步，但不会阻塞登录与启动。不能保证未来所有官方更新永远无需适配。

## 针对性验证

| 场景 | 验证方式与结论 |
| --- | --- |
| 两个入口的冷启动参数 | 自动测试：单次 initial-argv 项目链接，普通入口不启用 CDP |
| 已有窗口/先开 Codex 再开管理器 | 决策回归：保留同账号窗口，格式化差异不触发重载 |
| 切换账号 | 决策回归：实际配置变化仍走安全关闭和投放，不绕过凭据校验 |
| 连续点击 | 排他租约测试：第二次拒绝，不排队；释放后可再次操作 |
| 延迟/过期导航 | 测试：可见、已发送、已失效、PID 不可确认时禁止再投递 |
| 慢初始化/日志不可用 | 判据和日志夹具：pending/unknown 不当作退出；随后 ready 可恢复，未使用新的等待时间过滤原进程 |
| 新版外置 app-server | 本机实际 PID/启动时间/签名/父子链只读验证通过；runtimeHealthy=true，关闭白名单仍为 false |
| 当前官方主页面就绪 | 回放实际官方日志通过；进程启动 12:06:08.210 UTC，app-server connected 12:06:16.995，routes 12:06:41.737，ready 12:06:41.740 |
| 可选补丁不支持 | 结构识别与桥接契约回归：拒绝不完整契约，reload 不允许 |
| 目录未来结构与未知字段 | 临时目录测试：保留未知字段、缩进无变更、新 schema/字段类型安全跳过 |

当天旧日志的补丁拒绝不代表发生过 reload，不能把官方初始化时间全部算给补丁，也不能把后台观察期限算成窗口未显示时间。

未在其他电脑执行 UI 验收。为保护当前会话和网关，本轮没有关闭真实 Codex 执行完整 GUI 冷启动、真实跨账号登录或电脑重启测试；上述明确标记为自动/决策测试的项目不冒充真实按钮端到端测试。

## 构建与诊断

源码入口：Form1.cs、CodexCliService.cs、CodexCliService.Startup.cs、OfficialCodexLogReadiness.cs、CodexNativeFastBridge.cs、SignedOpenAiExecutable.cs、ChatSectionSynchronizationService.cs。

```powershell
# 可独立运行，不登录、不发送模型请求、不关闭 Codex
CodexAccountManager.exe --startup-self-test
CodexAccountManager.exe --audit-windows-startup <Codex主进程PID>
```

启动诊断日志 `codex-plus-plus-launch-diagnostics.log` 增加唯一 launch_id、阶段累计毫秒、PID/start-time、首次窗口可见、官方 routes/ready 时间和导航/reload/重试决策；不记录密钥。

版本 2.3.23，独立网关端口 8340。构建到新目录，不覆盖或关闭运行中的 2.3.22/8339。构建自检通过后同步桌面及任务栏快捷方式；下次主动退出旧管理器并从快捷方式打开才运行新版本。

保留此前本地未发布的按需代理启动与 GPT-6 Sol 模板改动。发布仅包含程序、公共说明和模型模板，不包含真实账号、节点凭据、聊天记录或本机私有脚本。
