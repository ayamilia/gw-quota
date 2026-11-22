# gw-quota

Windows 桌面悬浮窗，只读展示 LLM 网关的上游余额与包月额度。单文件 exe，不需要额外安装运行时（Win10 自带 .NET Framework 4.8）。

浮在所有窗口之上，半透明、可拖动，球形与条形两种外形，鼠标悬停看详情。

## 它解决什么

自建 LLM 网关通常会把多家上游收敛成一个端点。额度分散在几家供应商的控制台里，切来切去很麻烦。这个工具只做一件事：定时拉一次网关的额度接口，把结果画成一个一直看得见的小球。

它不代理请求、不改配置、不写远端，只发一个 GET。

## 与 llm-gateway 的关系

- gw-quota 是**独立客户端**，不是 [llm-gateway](https://github.com/inertias-1900/llm-gateway) 的子项目，不含它的代码，也不随它分发；两者各自独立选择许可证。
- 它调用的是 llm-gateway 中的只读接口 `GET /api/upstream/quota`（鉴权为 `Authorization: Bearer <key>`），读取返回 JSON 里的上游余额与包月用量。接口路径与数据结构由网关定义，客户端只做展示。
- 网关侧需要为它准备一把 key。如果所用网关版本支持只读作用域（`keys[].scope = "quota"`），建议用这种 key：按该作用域校验时，它能读模型清单与额度，不能对话或生成。**该字段并非所有版本都有**，以所用网关的配置文档为准；不支持时可用普通 key，此时只读约束来自客户端本身（只发一个 GET），而不是服务端。
- 只要能暴露相同路径与 JSON 结构的其他网关实现，也可以对接；程序不绑定某个域名。

## 功能

- 球形悬浮球（外环本月已用，中心本月剩余，内弧 5 小时窗口）与条形两种形态
- 逐像素透明，半透明只作用于背景，文字始终实心
- 悬停显示详情：各窗口已用/剩余/重置时刻、两家余额、网关估算明细、数据新鲜度
- 右键菜单：外形、色调、透明度、告急变色（≥90%）、立即刷新、连接设置、回归默认位置、关于、退出
- 全局热键 `Ctrl+Alt+Q` 随时唤起菜单
- 失败降级：401、403、429、503、断网各有状态提示与独立退避，不清零、不弹窗

## 系统要求

- Windows 10（19045 实测；Windows 7 及以上理论上可用，未测试）
- .NET Framework 4.8，Win10 自带
- 不需要 .NET SDK、不需要 NuGet、不需要管理员权限

## 使用

1. 按「构建」一节编译出 `GWQuota.exe`，双击运行。
2. 首次运行会提示连接设置：填网关根地址（例如 `https://gateway.example.com`）和只读 key。
   - 地址只填根地址即可，程序会自动补 `/api/upstream/quota`
   - 只读 key 在网关面板生成
3. 左键拖动换位置，双击立即刷新，右键出菜单。

配置保存在 `%APPDATA%\GWQuota\`（写不进去时回退 `%LOCALAPPDATA%\GWQuota\` 或 exe 同级 `data\`）：

- `key.txt`：只读 key
- `config.ini`：外形、坐标、色调、透明度、轮询间隔、网关地址
- `gwquota.log`：只记录错误，不含 key

## 配置项

```ini
endpoint=https://gateway.example.com/api/upstream/quota   ; 首次填写可只填根地址，程序自动补路径
form=ball        ; ball=球形，bar=条形
opacity=88       ; 40–100，只影响背景透明度
hue=190          ; 色相 0–359
sat=78           ; 饱和度 0–100
interval=300     ; 轮询秒数，60–3600
```

## 构建

用 Windows 自带的 csc 直接编译，产物是单个 exe：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

两个已知的构建坑：

- 目录名含中文时 `csc.exe` 会失败，脚本会先暂存到 `%TEMP%` 再编译
- `build.ps1` 必须存成 UTF-8 带 BOM，否则 Windows PowerShell 5.1 按 GBK 读中文会报错

## 数据与隐私

- 客户端只发一个 GET，不发送对话或生成请求；这把 key 本身能不能消费取决于网关授予的权限，建议使用只读作用域
- key 只存在本机 `key.txt`，不写进 exe，不上传
- 日志不含 key
- 泄漏的最坏后果：别人能看到你的余额数字

## 已知限制

1. **部分桌面的通知区域不受理新的 `Shell_NotifyIcon` 注册**。这是系统侧行为，与本程序无关；程序每分钟重试一次，同时提供右键悬浮球与 `Ctrl+Alt+Q` 两个入口。
2. **没有代码签名**。首次运行 SmartScreen 会提示「未知发布者」，点「更多信息 → 仍要运行」即可；每次重新编译哈希都会变，SmartScreen 会重新判定。
3. 多显示器只做坐标钳制与边缘吸附，没有做每屏独立记忆位置。

## 设计说明

`docs/01-设计说明.md` 记录了渲染管线、交互状态机与取舍；`docs/02-踩坑记录.md` 记录了 18 个实际踩过的坑（现象、根因、修法、验证）。

## 许可证

MIT，见 `LICENSE`。

---

# gw-quota (English)

A Windows desktop widget that shows upstream API balance and monthly quota from an LLM gateway. Single-file executable; no extra runtime needed (.NET Framework 4.8 ships with Windows 10).

It floats above other windows, is semi-transparent and draggable, offers a ball and a bar layout, and shows details on hover.

## What it does

A self-hosted LLM gateway usually consolidates several upstream providers behind one endpoint. Their quotas live in separate provider consoles, which makes checking them tedious. This tool does one thing: poll the gateway quota endpoint on a timer and draw the result as a small ball that stays visible.

It does not proxy requests, change configuration, or write anything remotely. It makes a single GET.

## Relationship to llm-gateway

- gw-quota is an **independent client**, not a subproject of [llm-gateway](https://github.com/inertias-1900/llm-gateway). It contains none of that project's code, is not distributed with it, and the two choose their licenses independently.
- It calls the read-only endpoint `GET /api/upstream/quota` in llm-gateway (authenticated with `Authorization: Bearer <key>`) and renders the balances and monthly usage found in the JSON response. The path and schema are defined by the gateway; the client only displays them.
- The gateway needs a key for this client. If the gateway version supports a read-only scope (`keys[].scope = "quota"`), prefer it: with server-side enforcement such a key can list models and read quotas but cannot chat or generate. **Not every version has this field**; check your gateway's configuration docs. Without it, a regular key still works, but the read-only guarantee then comes from the client (it only issues a GET), not from the server.
- Any other gateway implementation exposing the same path and JSON shape also works. The program is not bound to a specific domain.

## Features

- Ball mode (outer ring: this month used; centre: this month remaining; inner arc: 5-hour window) and bar mode
- Per-pixel transparency; only the background is translucent, text stays fully opaque
- Hover panel: used/remaining/reset time per window, two provider balances, gateway-side estimate, data freshness
- Right-click menu: layout, colour, opacity, warning colour at 90% or above, refresh now, connection settings, reset position, about, exit
- Global hotkey `Ctrl+Alt+Q` opens the same menu
- Graceful degradation: 401, 403, 429, 503 and offline each have their own status and backoff; previous data is kept, no popups

## Requirements

- Windows 10 (tested on 19045; Windows 7 and later should work in theory, untested)
- .NET Framework 4.8, included with Windows 10
- No .NET SDK, no NuGet, no administrator rights

## Usage

1. Build `GWQuota.exe` as described below and run it.
2. The first run asks for a connection: the gateway root URL (for example `https://gateway.example.com`) and a read-only key.
   - Enter the root URL only; the program appends `/api/upstream/quota`
   - The read-only key is created in the gateway dashboard
3. Drag with the left button to move, double-click to refresh, right-click for the menu.

Settings live in `%APPDATA%\GWQuota\` (falling back to `%LOCALAPPDATA%\GWQuota\` or `data\` next to the executable):

- `key.txt`: the read-only key
- `config.ini`: layout, position, colour, opacity, poll interval, gateway endpoint
- `gwquota.log`: errors only, never the key

## Configuration

```ini
endpoint=https://gateway.example.com/api/upstream/quota   ; a root URL is enough at first run; the path is appended
form=ball        ; ball or bar
opacity=88       ; 40-100, background only
hue=190          ; 0-359
sat=78           ; 0-100
interval=300     ; seconds, 60-3600
```

## Build

Compiled directly with the csc that ships with Windows; the output is a single executable:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Two known build pitfalls:

- `csc.exe` fails on non-ASCII directory names, so the script stages the sources in `%TEMP%` first
- `build.ps1` must be saved as UTF-8 with BOM; Windows PowerShell 5.1 otherwise reads Chinese text as GBK and fails

## Data and privacy

- The client issues a single GET and never sends chat or generation requests. Whether the key itself can spend depends on the permissions the gateway grants it; a read-only scope is recommended
- The key stays in the local `key.txt`; it is not compiled into the executable and is never uploaded
- Logs never contain the key
- Worst case if leaked: someone can read your balance numbers

## Known limitations

1. **Some desktops do not accept new `Shell_NotifyIcon` registrations.** This is a system-side behaviour unrelated to this program. The app retries once a minute and also offers right-click on the widget and the `Ctrl+Alt+Q` hotkey.
2. **No code signature.** SmartScreen shows "Unknown publisher" on first run; choose "More info → Run anyway". Every rebuild changes the hash and triggers the prompt again.
3. Multi-monitor support is limited to coordinate clamping and edge snapping; per-monitor position memory is not implemented.

## Docs

`docs/01-设计说明.md` covers the rendering pipeline, interaction state machine and trade-offs; `docs/02-踩坑记录.md` lists 18 real pitfalls (symptom, root cause, fix, verification). Both are in Chinese.

## License

MIT, see `LICENSE`.
