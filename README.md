# CodexQuotaTray

Win11 托盘/任务栏状态小工具，用于对接 CPA / CPAMC，实时显示 Codex 调用次数、token 累计消耗和 5h/7d 账号池额度。

## 当前能力

- 常驻托盘，支持开机启动。
- 透明任务栏状态条，可拖动，位置会保存到本地。
- 显示 `Calls`、失败数 `F`、累计 `Tok`。
- 聚合多个 Codex OAuth 账号的 `5h` / `7d` 剩余额度。
- Free 账号只参与 `7d`，Plus 账号参与 `5h` 和 `7d`。
- 右键菜单“打开 CPA”跳转到 `BaseUrl + ManagementPath`，默认是 `http://192.168.0.16:8317/management.html`。

## 数据来源

已测试：CPA / CPAMC。

- `/v0/management/api-key-usage`：代理 API Key 的成功/失败请求次数。
- `/v0/management/logs`：兜底统计真实 `/v1/responses`、`/v1/chat/completions`、`/v1/completions` 调用次数。
- `/v0/management/usage-queue`：读取近期 token 用量事件，并在本地 `usage-state.json` 中累计。
- `/v0/management/auth-files`：查找 Codex OAuth 凭据。
- `/v0/management/api-call`：查询 Codex `wham/usage` 限额窗口。

计划兼容但尚未测试：

- newapi
- sub2api

目前这两类只预留了 `ProviderMode` 和 `StatusPaths` 配置，后续需要拿真实接口返回再适配。

## 使用

```powershell
cd H:\desk\app5
.\build.ps1
.\run.ps1
```

## 配置

运行配置文件：

```text
H:\desk\app5\bin\config.json
```

JSON 不支持真正注释，所以配置里 `_` 开头字段是备注，程序会忽略。

不要把 CPA 管理密钥写入配置文件。请设置 Windows 用户环境变量：

```powershell
[Environment]::SetEnvironmentVariable("CPAMC_MANAGEMENT_KEY", "你的管理密钥", "User")
```

设置后右键托盘图标点“重新加载配置”，或重启程序。

## 额度说明

任务栏上 `F1` 表示失败请求 1 次。`92% x3` 表示该窗口账号池综合剩余额度为 92%，纳入统计的账号数为 3。

如果接口只返回百分比，程序按账号剩余百分比平均；如果后续接口返回真实 `limit/remaining`，程序会自动按总额度加权。

CLIProxyAPI v6.10 之后不内置历史 token 汇总，`usage-queue` 是短期队列且读取后会弹出。因此 token 统计从本工具接入后开始累计，历史 token 无法从 CPA 直接补齐。
