# CodexQuotaTray

Win11 托盘状态小工具，用于对接 CPAMC / CLI Proxy API Server。

## 架构

- 原生 C# WinForms 托盘程序。
- 无 Electron/Flutter 运行依赖。
- 通过 CPAMC 管理接口读取：
- `/v0/management/api-key-usage`：代理 API Key 的成功/失败请求次数。
- `/v0/management/logs`：兜底统计真实 `/v1/responses`、`/v1/chat/completions`、`/v1/completions` 调用次数，排除管理接口自查。
- `/v0/management/usage-queue`：读取最近请求的 token 用量事件，并在本地 `usage-state.json` 中累计。
- `/v0/management/auth-files`：查找 Codex OAuth 凭据。
- `/v0/management/api-call`：查询 Codex `wham/usage` 限额窗口。

## 使用

```powershell
cd H:\desk\app5
.\build.ps1
.\run.ps1
```

托盘图标含义：

- 托盘小图标会在调用次数和 Codex 当前最紧张额度窗口之间轮换。
- 右下角常显状态条显示 `Calls N | Tokens N`，并显示 `5h`、`7d` 剩余额度进度条。
- `ERR`：未配置管理密钥、管理接口不可用或请求失败。

说明：CLIProxyAPI v6.10 之后不内置历史 token 汇总，`usage-queue` 是短期队列且读取后会弹出。因此 token 统计从本工具接入后开始累计，历史 token 无法从 CPA 直接补齐。

## 配置

配置文件：

```text
H:\desk\app5\bin\config.json
```

默认配置：

```json
{
  "BaseUrl": "http://192.168.0.16:8317",
  "RefreshSeconds": 60,
  "ShowTaskbarWidget": true,
  "ManagementKeyEnvironmentVariable": "CPAMC_MANAGEMENT_KEY",
  "ApiKeyEnvironmentVariable": "",
  "StatusPaths": ["/"]
}
```

不要把管理密钥写入配置文件。请把 CPAMC 页面里的“管理密钥”设置到 Windows 用户环境变量：

```powershell
[Environment]::SetEnvironmentVariable("CPAMC_MANAGEMENT_KEY", "你的管理密钥", "User")
```

设置后右键托盘图标点“重新加载配置”或重启程序。
