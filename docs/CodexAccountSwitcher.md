# Codex 账号切换客户端

这个本地客户端用于管理多个 Codex CLI 账号、检查登录状态、将所选账号凭据切换给官方 Codex Windows 客户端，并在你主动操作时更新 access token。
客户端不会展示 token，也不会把 token 写入日志、文档或配置文件。

## 当前界面能力

- 卡片式账号列表，支持搜索账号名或目录
- 统一的浅色 / 深色界面风格
- 支持三种外观模式：`跟随系统`、`浅色模式`、`深色模式`
- 主题选择会保存到根目录 `appsettings.json`，重启后仍生效
- 新增、编辑、移除账号
- 一键将所选账号用于 Codex Windows 客户端
- 保留 `CLI` 备用入口，可直接打开已设置所选 `CODEX_HOME` 的 PowerShell
- 检查所选账号的 `codex login status`
- 手动更新所选账号的 access token
- 编辑账号时可选填写新的 access token，一次保存账号信息和密钥
- 显示客户端可解析到的 token 到期时间

## 使用方式

1. 双击 `CodexAccountManager.cmd`，或直接打开 `dist\CodexAccountManager\CodexAccountManager.exe`
2. 在左侧选择外观模式：跟随系统、浅色模式或深色模式
3. 在顶部确认启动目录，默认是当前工具根目录
4. 点击账号卡片上的 `启动`、`CLI`、`状态`、`Token`、`编辑`、`删除`
5. 点 `启动` 会先预检所选账号并确认它能完成一次最小 Codex 请求，再提示是否重启 Codex Windows 客户端；点 `CLI` 会打开备用 PowerShell，里面已经设置好所选账号的 `CODEX_HOME`

## 启动方式

- `启动`：先使用所选账号目录执行 `codex login status`，再用同一个 `CODEX_HOME` 执行一次临时 `codex exec --ephemeral` 最小请求。只有两步都通过时，才会备份默认 `%USERPROFILE%\.codex\auth.json`、`.cockpit_codex_auth.json` 和 `config.toml`，然后把所选账号的 `auth.json` 复制到默认 `%USERPROFILE%\.codex\auth.json`，清理旧 `.cockpit_codex_auth.json`，并把默认 `config.toml` 的 provider 修正为 `openai`，移除旧的 `codex_local_access` / `experimental_bearer_token` 配置。随后会提示是否重启官方 Codex Windows 客户端，让它重新读取账号。
- `CLI`：打开备用 PowerShell，并设置当前账号自己的 `CODEX_HOME`，适合继续用 `codex -C .`。

`启动` 不会把所选账号目录里的 `config.toml`、`sessions`、`sqlite`、`logs` 等状态内容复制到默认 `.codex`，因此默认 Codex Windows 客户端里的聊天记录、设置和项目状态会继续保留。它只会在默认 `.codex\config.toml` 中移除会让客户端继续走旧 API key / 本地网关的 provider 配置。

每次切换前都会在 `%USERPROFILE%\.codex\account-switcher-backups\<timestamp>` 创建备份；如需回滚，可从对应备份目录恢复 `auth.json`、`.cockpit_codex_auth.json` 和 `config.toml`。

注意：access token 账号可能仍无法加载 Codex 客户端的个人资料或插件接口；这类接口由官方客户端访问 ChatGPT 后端，可能不接受当前 token 范围。账号能否用于实际对话以最小 Codex 请求预检为准。

CLI 窗口中启动 Codex 时输入：

```powershell
codex -C .
```

## 外观模式说明

- `跟随系统`：读取 Windows 应用主题设置，自动匹配浅色或深色
- `浅色模式`：始终使用亮色界面
- `深色模式`：始终使用暗色界面
- 主题配置写入根目录 `appsettings.json`

## 更新 Token

1. 在主窗口找到账号卡片
2. 点击 `Token`
3. 在弹出的隐藏输入框中粘贴新的 access token
4. 点击 `更新`

也可以点击账号卡片上的 `编辑`，在“新密钥（可选）”输入框中填写新的 access token。留空时只保存账号名称和 `CODEX_HOME`，不会更改当前密钥。

客户端会把 token 通过标准输入传给：

```powershell
codex login --with-access-token
```

登录完成后，客户端会自动执行：

```powershell
codex login status
```

## 验证

运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-CodexAccountSwitcher.ps1
```

也可以重新构建客户端：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-CodexAccountManager.ps1
```

如果本机没有安装 .NET SDK，则无法直接执行 `dotnet build`，但仍可运行仓库内已有脚本或现成可执行文件做基础验证。
