# Codex Account Manager

> 本项目是非官方的本地账号管理工具，与 OpenAI 没有隶属或背书关系。公开仓库只包含源代码、测试和脱敏示例配置，不包含真实账号、Token、聊天记录、额度快照或安装包。

## 开源仓库边界

- `accounts.json`、`appsettings.json`、Token 元数据、额度快照和本地日志均属于运行时私密数据，不应提交。
- Windows/macOS 安装包、Electron 和 Codex CLI 二进制载荷放在 GitHub Releases，不放在源码仓库中。
- 发布前请运行隐私扫描、测试和 SHA256 校验；不要把本机用户目录、代理凭据或 API Key 写进 Issue、PR 或日志。
- 使用第三方组件和官方 Codex CLI 载荷时，请同时遵守其各自的许可证和服务条款，详见 `THIRD_PARTY_LICENSES.md`。

开源协作规则见 `CONTRIBUTING.md`，安全问题请按 `SECURITY.md` 报告。

本项目是一个本地 Windows 工具，用于管理多个 Codex 账号的独立凭据目录，并把 Codex Windows 客户端和 CLI 的聊天记录统一集中到默认 `%USERPROFILE%\.codex`。

它适合已经拥有合法 access token 的 ChatGPT Business / Codex Business 账号使用。通过 access token 登录时，工具不会在本地要求接收短信或邮箱验证码；前提是该 token 本身已经由对应 Business 账号合法取得并仍然有效。个人版、Plus 或没有 Business 权限的账号不保证可用。

## 功能

- 管理多个账号名称和对应的凭据目录
- 使用 Business access token 添加或更新账号登录状态
- 支持兼容 OpenAI API 的 API Key 账号
- 检查所选账号的 `codex login status`
- 切换到不同凭据前验证账号；同凭据启动时跳过重复网络登录预检
- 每张账号卡都提供“Codex++ 启动”和“Codex 启动”两个独立入口；没有 Codex++ 时可直接使用官方 Codex
- 重复启动当前账号会复用已有凭据；只有真正切换账号时才关闭旧客户端、投放新凭据并重开所选客户端
- 同凭据启动不会退出登录、删除或重复投放凭据，也不会清空模型缓存
- 启动时把所选账号的模型配置和 API-Key 登录文件投射到默认 `%USERPROFILE%\.codex`；API Key/PAT 均由管理器自动写入，无需在官方 App 重复输入
- API 登录和 access token 登录的聊天记录统一保存到默认 `%USERPROFILE%\.codex`
- Access Token 账号的 App 登录采用 `auth_mode=apikey`，密钥由账号自己的 PAT 自动生成；模型请求仍经本机网关使用该账号的月额度
- 切换前会备份已有 ChatGPT OAuth，但 OAuth 不再是点击启动的前置条件；真正切号才关闭客户端，同账号重复启动直接复用
- 提供备用 `CLI` 入口，打开已设置共享 `CODEX_HOME` 的 PowerShell
- 在统一聊天记录页打开、归档、取消归档或永久删除本地任务
- 永久删除账号时同步删除其独立凭据目录；如果共享 `.codex` 正在使用同一份凭据，也会清理匹配的共享 `auth.json`，但不会删除共享聊天记录
- 自动识别 API、5h + 周额度和月额度账号，并显示各自的下次重置时间
- 启动后按账号独立只读刷新每个 Access Token 的官方额度；不会把当前选中账号的结果复用给其它账号
- 查询并使用官方可重置次数；可重置 0 次时禁止发送重置请求
- 额度页可按账号手动开启或关闭被动监测；每次开启从点击时刻建立新周期，并提供圆形仪表、逐模型 API 等值面积趋势图与 CSV 导出
- 支持浅色、深色和跟随系统主题
- 提供独立“主题设置”栏目：切换 Account Manager 外观，并可选安装、应用或恢复官方 Codex 背景主题
- 各页面使用同尺寸星空、星座、星云与流星横幅；顶部不再显示账号数量徽章。横幅展示 4 条错峰运行的动态流星、4 条远景流星和呼吸星芒，窗口失焦或最小化后暂停动画
- 页面切换和从任务栏恢复时复用已有界面，不触发整页重建，也不会短暂露出白色背景；顶层窗口与任务栏图标保持稳定

## 额度与本地用量

额度页面不再提供主动“测额度”。额度推断必须在详情页手动开启；每次开启都从点击时刻创建新的独立监测周期，关闭后停止更新，再次开启不会继承上一轮的正常/异常判断。美元额度只根据本轮开始后正常聊天产生的本地 Token 用量和官方整数百分比变化被动推断，不会为测量而额外调用模型：

- 启动后分别只读每个 Access Token 账号自己的官方额度窗口，不调用模型；每个请求使用该账号独立的 `CODEX_HOME` 和 PAT
- 详情中的“查询重置次数”只请求 `account/rateLimits/read`，显示官方百分比、下次重置时间、Credits 和可重置次数
- 周额度账号同时显示 5h 与周窗口；月额度账号显示月窗口
- 1h / 5h / 今天 / 本周 / 本月的 Token 和 API 等值消耗来自本地聊天日志
- 有 5h 窗口的周额度账号用 5h 主窗口推断容量，阈值为 `$10`；只有周窗口的账号按周窗口推断，阈值为 `$90`
- 月额度账号按最近被平滑结果采用的有效窗口主模型选择正常参考阈值：`gpt-5.6-sol` 为 `$200`、`gpt-5.6-terra` 为 `$100`、`gpt-5.6-luna` 为 `$80`；混合模型窗口按 API 等值消耗占比最高的模型选择阈值，未知模型继续回退到 `$200`
- 被动监测使用重叠的绝对 2 个百分点窗口；官方百分比每前进 1% 都会将最新窗口纳入实时平滑值并立即更新预测。数据不足时显示“数据收集中”，估算区间跨过阈值时显示“额度待确认”
- 推断结果明确标记为“API 等值估算”，不是 OpenAI 返回的官方美元余额，也不是实际账单金额
- 趋势图左上角只汇总所选范围的累计 API 等值，图形本身展示每个固定时间桶的非累计用量峰形，并按实际模型分色；“今天”使用 15 分钟数据点。1h、5h、今天、本周、本月都会裁掉首笔真实用量前的大段空白，同时分别保留约 5 分钟、30 分钟、1 小时、12 小时、2 天的零用量基线，之后的空桶仍可悬停查看。官方剩余百分比继续作为独立细线显示，并可导出 UTF-8 CSV
- 曲线下方的模型星系与其共用同一时间范围及“实时额度 / 本轮监测”数据源，不重复放切换按钮。星系区域采用左侧宏伟星轨主视觉、右侧模型占比与 Token / API 等值明细的布局；浅色外观使用白色星空画布，深色外观使用深空画布，没有用量时显示与主题一致的专属空态。中央星球大小固定，放大的多层轨道按错相节奏轻微呼吸；每个模型独占一条 360° 完整行星环，环长不表达真实占比，精确占比只在悬停信息和右侧明细中显示。同一环使用协调的同色系渐变，不同模型使用可区分的独立色系；每条环只有一颗沿轨道连续运行的彗星，并配有星尘与大气辉光。动画以轻量节拍局部更新，窗口失焦或最小化后暂停；默认突出 Token 最多的模型，所有数据只读现有日志，不会增加调用
- 额度仪表改为“额度星球”，百分比仍只决定液位，不改变原有额度语义。星球使用前后分层的土星环、大气辉光、云纹、星尘与少量气泡营造层次，取消全幅扫描光和横向扫描线；动画只做局部重绘，窗口失焦或最小化后暂停，0%/100% 时液位和数值保持固定
- 账号分组展开或收起时始终预留同一条窄滚动槽；滚轮按需显示，但列表宽度和右边界保持不变
- 旧版本已经真实产生的探针 Token 会继续作为历史用量计入本地统计，但不会参与新的被动容量校准；程序不会再创建或续接任何探针
- 额度统计页面的 Token/成本刷新只读取本地自然使用日志，不发送测试提示、不进行压力测试，也不会主动发起模型请求。程序会在本机 `.cache` 中维护增量日志索引：首次建立后只解析新增或变化的 JSONL 内容，并优先展示上次快照，避免每次进入额度页重新通读全部历史；索引损坏时会自动重建，不影响原始聊天记录。官方额度、重置次数、Credits 和套餐快照另按 `AuthKind + CODEX_HOME` 写入 `.cache/quota-snapshots-v1.json`，账号重命名不会串号，重新录入同一目录的新 PAT 会清掉旧快照
- 除 JSONL 外，程序会只读扫描默认 Codex 目录的 `logs_2.sqlite`，把本机 `response.completed.usage` 与自然 `token_count` 事件一一核对。精确匹配优先；有限容错匹配仅在模型、时间和至少 3 个 Token 计数共同吻合时使用。匹配成功后 input、cached input、cache write、output、reasoning 和 total 全部以原始响应 usage 为准，JSONL 差异会记录在 `.cache/cache-write-response-index-v1.json` 的 `reconciliation` 字段中
- 对每个非兼容 API 账号，程序按各自的账号键通过 Codex `app-server` 的只读 `account/rateLimits/read` 刷新官方整数百分比、窗口和重置时间：当前账号/当前查看账号每 15 秒，其它账号每 1 分钟。请求使用对应账号凭据，不发送模型请求，也不消耗 Token

### 模型价格口径

程序按每条 `token_count` 记录对应的模型分别计价，单位为美元 / 100 万 Token。当前内置的 OpenAI 标准处理价格如下；长上下文档位按单条记录输入 Token 是否超过 272K 判断：

| 模型 | 短上下文输入 / 缓存输入 / 输出 | 长上下文输入 / 缓存输入 / 输出 |
| --- | --- | --- |
| gpt-5.6-sol | 5 / 0.5 / 30 | 10 / 1 / 45 |
| gpt-5.6-terra | 2 / 0.2 / 12 | 4 / 0.4 / 18 |
| gpt-5.6-luna | 0.2 / 0.02 / 1.2 | 0.4 / 0.04 / 1.8 |
| gpt-5.5 | 5 / 0.5 / 30 | 10 / 1 / 45 |

额度容量监测按每条记录对应模型的真实 API 单价换算，再与官方整数百分比变化计算窗口总容量。Sol、Terra、Luna 的价格差异会完整保留；订阅额度百分比并不保证严格按公开 API 美元扣减，因此不同模型可能反推出不同的 API 等值总容量。月额度正常状态使用与最近有效窗口主模型对应的 `$200` / `$100` / `$80` 参考阈值。这里的推测仍是本地 API 等值估算，不是 OpenAI 返回的官方美元余额；如果不同模型测出的总量差异较大，还应检查模型切换窗口、官方百分比边界和本地日志是否完整。

日志会保留模型切换，因此同一个月混用多种模型时会分别计算后再求和。旧日志若没有模型字段，Access Token 账号按当前默认的 `gpt-5.6-terra` 价格回退；兼容 API 账号按其账号配置模型回退。这里显示的是“API 等值估算”，不代表订阅套餐的实际扣费或官方账单。

## 共享聊天记录

每个账号目录只用于保存该账号自己的登录凭据和账号配置。启动 Codex Windows 客户端或 CLI 时，管理器会把当前账号的凭据投射到默认 `%USERPROFILE%\.codex`，并把客户端的 `CODEX_HOME` 设置为这个共享目录。

这样做的效果是：

- API 账号和 access token 账号切换时，不再切换聊天记录库
- 旧的账号目录 `sessions` / `archived_sessions` / `state_5.sqlite` 会自动合并到默认 `%USERPROFILE%\.codex`
- 合并前会在用户目录下创建历史备份，凭据文件不会被作为聊天历史迁移
- 切换账号只替换共享目录中的当前登录凭据和配置
- 历史任务保留原来的模型信息；新任务才使用当前账号模型，避免跨提供商改写触发强制压缩
- Codex++ 启动会快速确认隐藏任务并立即释放界面，增强桥接和项目深链在后台完成；同时启用 Codex++ 快速启动并隐藏管理中间窗，避免 Account Manager 同步等待一两分钟
- 每次切换账号并启动 Codex++ 后，会通过 Codex 原生深链自动进入当前项目的新任务页，避免旧任务模型被发送给新账号
- 永久删除会记录防复活标记，后续历史合并不会把已删除任务重新导入

## Business Access Token 登录

1. 打开 `CodexAccountManager.cmd`
2. 点击添加账号
3. 填写账号显示名称
4. 填写该账号独立使用的凭据目录，例如 `C:\CodexHomes\work-account`
5. 在密钥输入框粘贴 Business access token
6. 保存后，管理器会把 token 通过标准输入传给 `codex login --with-access-token`，并由本机 PAT 网关补齐 `chatgpt-account-id` 后转发到 ChatGPT Codex 接口
7. 登录成功后，账号卡片会显示状态和 token 到期信息
8. 点击“Codex 启动”或“Codex++ 启动”；管理器会自动写入官方 App 可识别的 `auth_mode=apikey` 登录文件，不需要在 App 再输入密钥
9. 以后切走再切回只需点击对应启动按钮；API/PAT、额度查询、重置次数和被动监测均按账号独立保存

也可以先只添加账号名称和凭据目录，再点击账号卡片上的 `Token` 按钮粘贴 access token。

### 本机 PAT 网关与代理

Access Token 账号的模型、官方额度和其它 ChatGPT 后端请求都经过本机 `127.0.0.1:8317` 网关。网关会用当前请求携带的 PAT 查询 `whoami`，取得对应的 `chatgpt_account_id`，再把 `/backend-api/*` 和兼容旧版的 `/api/codex/*` 请求转发到 ChatGPT 后端；官方桌面端已有的 OAuth Bearer 也会原样通过同一代理，但网关不会创建、刷新或保存 OAuth 登录态，也不会把多个账号合并成一个凭据。端点不再逐项设白名单，但上游主机仍固定为 `chatgpt.com`，并保留本机监听、凭据校验和路径穿越防护。

程序加载账号时会把已有 PAT 账号的托管 `config.toml` 原子迁移到本机网关，同时设置顶层 `chatgpt_base_url`，让 `app-server` 的模型和额度接口都走同一条网关；不需要重新录入 Token。迁移过程不发网络请求，也不修改 `auth.json`。维护时也可运行发布程序的 `--migrate-local-pat-configs` 参数重复执行该迁移。

Account Manager 的“系统配置”把上游代理拆成“地址”和“端口”。地址默认 `127.0.0.1`；“自动检测”只探测本机回环监听并执行 HTTP 代理握手，检测到的端口会直接显示和回填（例如 v2rayN 常见的 `10808`）。手动编辑地址或端口后使用手动设置；上游代理端口可随代理软件变更，网关本地监听端口仍固定为 `8317`。

## 隐私边界

仓库不会提交真实账号配置或 token。

- `accounts.json` 被 `.gitignore` 忽略，用于保存本机账号名称和账号凭据目录
- `token-metadata.json` 被忽略，只在本机保存 token 到期时间等非密钥元数据
- `.codex/`、`dist/`、本地 dotnet SDK、发布 zip 和构建输出均不进入版本库
- token 只通过标准输入传给 `codex login --with-access-token`
- PAT 网关只监听 `127.0.0.1`，控制端点使用本机随机密钥挑战，不会把 token 写入网关日志；上游请求必须经过设置中的 HTTP/HTTPS 代理（如 v2rayN、Clash 或 Mihomo），不会静默改成直连

首次使用时可以从示例文件复制配置：

```powershell
Copy-Item .\accounts.example.json .\accounts.json
```

然后把 `accounts.json` 中的示例账号名和 `codexHome` 改成本机路径。

## 运行

```powershell
.\CodexAccountManager.cmd
```

也可以使用脚本版：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Start-CodexAccountSwitcher.ps1
```

## 构建

### 开发环境

- Windows 构建需要 PowerShell、.NET 10 SDK 和 Git。
- macOS 子项目需要 Node.js 20 或更高版本以及 npm；`macos/package-lock.json` 用于可重复安装依赖。
- 源码仓库不包含 `node_modules`、本机 .NET SDK、Electron/Codex CLI payload 或发布 ZIP；这些内容由开发者按本节说明准备。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-CodexAccountManager.ps1
```

构建输出会写入 `dist\CodexAccountManager`，该目录不会提交到 Git。正式发布使用 `win-x64` 自包含单文件构建；制作安装包的电脑需要项目内构建工具，但朋友安装和运行正式版不需要另行安装 .NET 10。

一键安装包通过以下脚本生成；默认只保留项目上级目录中的唯一一份一键安装 ZIP，并在发布前检查空白账号配置、聊天记录目录、凭据文件和本地 `.cache` 索引均未进入压缩包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-OneClickInstallerPackage.ps1
```

macOS Apple Silicon 发布前先安装依赖并运行测试：

```powershell
cd macos
npm ci
npm test
```

macOS 发布脚本会校验固定版本的 Electron 和 Codex CLI SHA256。由于它们不进入源码仓库，构建者需要从相应官方发布渠道取得载荷，并通过 `ELECTRON_PAYLOAD_PATH` 与 `CODEX_CLI_PAYLOAD_PATH` 指定本地文件；具体版本、文件名和校验值见 `scripts/build-macos.mjs` 与 `scripts/verify-release.mjs`。发布前还必须在 Apple Silicon 真机验证安装、签名、OAuth、额度和终端交互。

## 验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-CodexAccountSwitcher.ps1
```
## 自动更新与发布

推送到 GitHub 的 `main` 分支后，`.github/workflows/build-latest.yml` 会自动在 Windows 和 macOS runner 上构建安装包，并更新 `latest` Release。安装包不会进入源码仓库。

Windows 和 macOS 客户端启动后会检查该 Release 的 `update-manifest.json`。发现新版本时，客户端会下载对应平台的 ZIP、校验 SHA256，得到确认后关闭旧程序并运行内置安装器；账号、Token 元数据、额度快照和聊天记录不会被更新流程删除。

因此日常发布只需要：修改源码并推送到 `main`。GitHub Actions 完成后，已安装客户端会在下一次启动时自动提示更新。第一次安装仍需手动安装一次；macOS 当前发布包面向 Apple Silicon，且未配置 Apple Developer ID 公证。
