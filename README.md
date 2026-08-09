# OpenAI Codex CLI 一键安装程序（国内网络优化版）

Windows 上绕过网络限制，一键安装 OpenAI Codex CLI 并配置聚合 API（OpenAI 兼容端点）。

## 文件

| 文件 | 说明 |
|---|---|
| `CodexInstaller.exe` | 最终分发物（全 C# 自包含，双击即用，自动提权） |
| `CodexInstaller.cs` | 完整源码 |
| `build-exe.bat` | 重新编译脚本（本机 .NET Framework csc，无第三方依赖） |

## 用法

```bat
CodexInstaller.exe                        :: 交互：Node 检测 → npm 装 codex → 聚合 API 配置
CodexInstaller.exe -SkipApi               :: 只装 CLI，不配置 API
CodexInstaller.exe -ApiBaseUrl https://n.tokeness.io/v1 -ApiKey sk-xxxxxx
CodexInstaller.exe -ApiModel gpt-5.6-sol  :: 指定模型（默认 gpt-5.6-sol）
CodexInstaller.exe -h                     :: 帮助
```

## 工作原理

1. **Node.js**：检测（固定路径 / 注册表 App Paths / where.exe / PATH）；缺失时从 npmmirror 静默安装（默认 v24.19.0），node 目录注入进程 PATH
2. **Codex CLI**：`npm config set registry https://registry.npmmirror.com` + `npm install -g @openai/codex`（失败自动重试 3 次）
3. **聚合 API 配置**（交互默认 Y）：
   - 写入 `~/.codex/config.toml`（原配置备份为 `config.toml.bak`）：
     ```toml
     model = "gpt-5.6-sol"
     model_provider = "openai"
     openai_base_url = "https://n.tokeness.io/v1"          # 默认聚合端点（Tokeness OpenAI 兼容）
     [windows]
     sandbox = "elevated"
     ```
   - `setx OPENAI_API_KEY sk-xxxxxx`（用户环境变量，Codex 通过 `env_key` 读取）
4. **验证** `codex --version`

## 说明

- Codex 的 API Key 不写入配置文件（安全），通过环境变量 `OPENAI_API_KEY` 提供，`setx` 持久化后**新开终端生效**
- `openai_base_url` 指向的必须是与 OpenAI 兼容（Responses / Chat Completions 格式）的端点
- `~/.codex/config.toml` 已有配置时会先备份再重写（本程序管理的键：model / model_provider / openai_base_url / windows.sandbox）
- 登录 ChatGPT 账号使用无需 API Key；用聚合 API 时不需要登录
- 中文系统下 npm 输出按系统代码页解码（`Encoding.Default`）
