# Windows 2.3.37：Access Token 模型目录自动同步

## 变更

- Access Token 账号现在会读取该账号 `codex debug models` 返回的完整模型目录，并保存到账号目录下的管理器专用目录文件。
- 管理器启动时、运行期间每 15 分钟，以及启动或切换 Access Token 账号前，会检查并更新该目录；网络或授权失败时保留上一份可用目录，不影响其他账号。
- 账号配置会引用自己的模型目录，因此新增模型、推理档位或账号授权变化不需要重新打包管理器。
- 用户手动配置的 `model_catalog_json` 不会被管理器覆盖；官方 OAuth 账号继续使用 Codex 自己的目录。
- 增加了按账号和全量刷新的命令行入口：
  - `--refresh-access-token-model-catalog <账号名>`
  - `--refresh-access-token-model-catalogs`
  - `--sync-access-token-model-catalogs`

## 验证

- 增加模型目录解析、未来模型 ID、重复刷新、最后可用目录和用户自定义目录保护的自测。
- 现有兼容 API 模型目录刷新与配置投影测试继续保留。
