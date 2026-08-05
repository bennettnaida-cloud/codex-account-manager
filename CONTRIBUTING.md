# 贡献指南

感谢你对 Codex Account Manager 的关注。

## 提交前

1. 从最新的 `main` 分支创建独立分支。
2. 不要提交真实账号、Token、API Key、聊天记录、额度快照、代理配置或本机路径。
3. 使用脱敏数据补充回归测试；不要把真实服务请求当作测试步骤。
4. 运行相关测试，并在 Pull Request 中说明测试命令和结果。

## 本地验证

macOS 子项目：

```powershell
cd macos
npm ci
npm test
```

Windows 构建和测试命令见根目录 README。Windows 构建输出、macOS payload 和安装包都不应进入 Git 历史。

## Pull Request 规则

- PR 描述应说明行为变化、兼容性影响和回滚方式。
- 涉及账号生命周期、额度、凭据、代理或进程控制的改动必须带回归测试。
- UI 改动请附脱敏截图或复现步骤；不要上传账号名称、邮箱或 Token。
- 不要在未确认第三方许可证的情况下新增或重新分发二进制文件。
## 发布自动化

合并到 `main` 会触发 `.github/workflows/build-latest.yml`。工作流会构建 Windows 与 Apple Silicon macOS 安装包，生成 SHA256 清单和 `update-manifest.json`，然后替换 GitHub 的 `latest` Release。不要把 ZIP、Electron payload、Codex CLI payload 或本机 `.tools` 目录提交到 Git。

客户端更新依赖公开仓库的 `latest` Release；如果工作流失败，不要手动替换 Release 中的单个平台文件，应先修复构建问题并重新运行工作流。
