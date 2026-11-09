# 2.3.41 — 启动前在线模型同步与账号文件导入

## Windows

- 官方 ChatGPT OAuth、PAT、兼容 API 的桌面及终端启动均等待在线模型目录准备完成。PAT 不再因 15 分钟缓存而跳过启动查询；OAuth 使用该账号自己的 Codex 凭据及工作区查询原生模型接口；API 查询所配置站点的 `/models`。
- OAuth 的模型元数据投放到当前账号专属目录，启动客户端时使用；保留用户显式配置的自定义目录。模型查询失败不清空目录、不切换共享凭据、不关闭当前客户端，不将缓存命中报告为最新查询成功。
- OAuth 过期或导入凭据缺少 `id_token` 时，在启动前通过官方端点刷新；失败提示重新登录，刷新响应不写入日志。
- “新增账号 → 从文件导入”：支持 sub2api `sub2api-data` / `sub2api-bundle` 的 `accounts[].credentials`、CPA Codex 的扁平 OAuth JSON、原生 `auth.json`、PAT、带 `base_url` 的 API Key。支持数组、多选 JSON、ZIP 中的 JSON。预览只显示名称、类型与错误，不展示 Token。
- 重复凭据跳过，同名账号自动加后缀，创建独立随机目录，导入路径不能指定本机写入位置；ZIP 仅读取，不解压路径。清单保存失败会回滚本批新建目录。
- 缺省模型的 API 导入记录在第一次启动时从站点目录选择模型；已有明确默认模型不自动替换。其它平台、脱敏凭据、缺少工作区或中转站地址、非 Codex OAuth 客户端均明确跳过。代理、分组与轮换池不随第三方导出自动导入。
- 包含 2.3.40 的额度保留线：5h 剩余 ≤10%、仅周额度剩余 ≤3% 时暂停并换号。

## 兼容性与验证

新模型须已对该账号开放，并被所用 Codex 客户端支持。发现新模型不等同于自动选中最新模型。显式自定义 `model_catalog_json` 优先；不提供 `/models` 的中转站本次启动会明确失败，不能假称已同步。macOS 仅同步发布版本标识，保持现有功能，本次 Windows 功能尚未移植。

验证包含 PAT/OAuth 请求与工作区隔离、强制查询绕过缓存、未来模型字段保留、OAuth 刷新、目录投放、导入重复/混合平台/错误格式/ZIP 路径/大小限制，以及原有轮换与应用自测。

导出结构参考：sub2api `backend/internal/handler/admin/account_data.go`（ee58c0013c76ccd3f36439881bc848d342ec1c9d），CLIProxyAPI `internal/auth/codex/token.go`（681ce41b6e44c45b15b79378b003909d6ba29e68）。导入仅支持可识别的 Codex 凭据，不声称兼容所有第三方扩展字段。
