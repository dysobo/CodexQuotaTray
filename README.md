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

## 刷新策略

状态条的显示、CPA 调用统计、token 累计和 Codex 额度查询是分开的。

- 窗口 keepalive：每 3 秒只维护任务栏状态条的窗口层级，不请求 CPA，也不请求 Codex 账号接口。
- Token 累计：每 10 秒读取一次 CPA `/v0/management/usage-queue`，只访问局域网 CPA，不直接访问 Codex 账号。
- Calls 活跃刷新：`RefreshSeconds` 控制，默认 60 秒。这个刷新只查 CPA 的 calls/logs，不请求 `wham/usage`。
- Calls 空闲刷新：如果一次 Calls 检查发现总次数没有变化，下一次 Calls 检查延长到 `CallsIdleRefreshSeconds`，默认 600 秒。
- Calls 活动唤醒：如果 Calls 已进入 600 秒空闲档，但 `usage-queue` 在 10 秒轮询中发现新 token 记录，会把下一次 Calls 检查提前到活跃间隔内，默认 60 秒内。
- Codex 额度刷新：`QuotaRefreshSeconds` 控制，默认 180 秒。只有这个刷新会通过 CPA 调用每个 Codex auth 的 `wham/usage`。
- 手动刷新：右键“立即刷新”或鼠标中键托盘图标会同时刷新 Calls 和 Codex 额度。

边界例子：

- 00:00 完整刷新，Calls 和额度都刷新。
- 03:00 额度到期，只刷新 `5h` / `7d` 额度；如果此时刚好也到了 Calls 检查，且 Calls 没变化，Calls 会进入 600 秒空闲档。
- 04:00 发生新的 Codex 调用，程序本身无法凭空知道远端 calls 已变化，但 10 秒 token 队列轮询通常会读到新 token 记录。
- 读到 token 活动后，会把下一次 Calls 检查提前到活跃间隔内，默认大约 05:00 检查，而不是继续等原来的 10 分钟空闲档。
- 如果这次调用没有进入 `usage-queue`，程序就没有活动信号，只能等下一次已安排的 Calls 检查。

## 额度说明

任务栏上 `F1` 表示失败请求 1 次。`92% x3` 表示该窗口账号池综合剩余额度为 92%，纳入统计的账号数为 3。

如果接口只返回百分比，程序按账号剩余百分比平均；如果后续接口返回真实 `limit/remaining`，程序会自动按总额度加权。

CLIProxyAPI v6.10 之后不内置历史 token 汇总，`usage-queue` 是短期队列且读取后会弹出。因此 token 统计从本工具接入后开始累计，历史 token 无法从 CPA 直接补齐。
