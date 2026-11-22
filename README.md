# RemoteMessage

ICCCE 插件：在本地开放一个 HTTP 接口，把收到的请求转换成 **灵动通知** 并播放提示音。内置 WebUI，可从浏览器或任意脚本远程推送消息。

## 功能

- 内置 WebUI：浏览器打开 `http://<主机>:23000/` 即可发送消息，支持标题、副标题、通知级别、提示音开关和最近记录。
- 两种调用方式：兼容原有的 `GET /notice/{内容}`，新增 `POST /notice`（JSON，支持更多参数）。
- 可选 **访问令牌鉴权**（`X-API-Key` / `Authorization: Bearer` / `?token=`）与按 IP 限流。
- 配置化端口与监听地址；无法绑定外网地址时自动降级为仅本地监听。
- 通知级别支持 `info` / `warning` / `error` / `success`。
- 健康检查 `GET /health`；内置页面无外部字体/脚本依赖，可离线使用。

## 安装

1. 下载 Releases 中的 `.icpx` 文件。
2. 放入 ICC-CE 的 `PluginPackages/` 目录。
3. 重启 ICC-CE，插件会自动安装并监听 `23000` 端口。

## 快速使用

浏览器打开：

```
http://<运行 ICC-CE 的电脑 IP>:23000/
```

最快发送方式：

```
http://<IP>:23000/notice/你好，世界
```

> 默认监听所有网卡（`*`）。如果绑定失败（例如没有管理员权限保留 URL ACL），插件会降级为只监听 `localhost`，此时只能在本机访问。

## HTTP API

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| `GET` | `/` 或 `/index.html` | 内置 WebUI |
| `GET` | `/notice/{内容}` | 发送通知（兼容旧版）；内容需 URL 编码 |
| `GET` | `/notice?content={内容}` | 同上，通过查询参数传递 |
| `POST` | `/notice` | 发送通知，JSON 请求体，支持完整参数 |
| `GET` | `/health` | 健康检查，返回版本、端口、是否要求令牌、运行时长 |
| `OPTIONS` | 任意 | CORS 预检 |

### POST 请求体

```json
{
  "content": "服务器 CPU 过高",
  "title": "监控告警",
  "message": "节点 A-03",
  "level": "error",
  "playSound": true
}
```

| 字段 | 类型 | 必填 | 默认值 | 说明 |
| --- | --- | --- | --- | --- |
| `content` | string | 是 | — | 通知正文（旧版行为中会作为标题显示；POST 可用 `title` 覆盖） |
| `title` | string | 否 | 同 `content` | 通知标题 |
| `message` | string | 否 | `Important` | 通知副标题 |
| `level` | string | 否 | `warning` | `info` / `warning` / `error` / `success` |
| `playSound` | bool | 否 | 配置项 `PlaySound` | 是否播放 `notify-normal-new.wav` |

示例：

```bash
curl 'http://127.0.0.1:23000/notice/%E4%BD%A0%E5%A5%BD'

curl -X POST http://127.0.0.1:23000/notice \
  -H 'Content-Type: application/json' \
  -d '{"content":"有人按门铃","title":"提醒","level":"info"}'
```

成功响应：

```json
{ "ok": true, "content": "有人按门铃", "level": "Info" }
```

失败响应统一为 `{ "ok": false, "error": "..." }`，状态码可能为 `400 / 401 / 405 / 413 / 429 / 500`。

## 配置

配置文件位于插件配置目录（参见 `PluginConfigFolder`），文件名为 `config.json`。仓库内提供 `config.example.json`，复制为 `config.json` 后修改即可；文件不存在时使用默认值。

```json
{
  "Port": 23000,
  "BindAddress": "*",
  "Token": "",
  "MaxContentLength": 2000,
  "RateLimitPerMinute": 0,
  "PlaySound": true
}
```

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `Port` | `23000` | 监听端口，范围 1–65535 |
| `BindAddress` | `*` | `*`、`+`、`any`、`0.0.0.0` 表示所有网卡；`localhost`、`127.0.0.1` 表示仅本机；也可填具体 IP |
| `Token` | 空 | 访问令牌。留空 = 不鉴权（兼容旧版） |
| `MaxContentLength` | `2000` | 单条消息最大字符数 |
| `RateLimitPerMinute` | `0` | 每个客户端每分钟请求上限，`0` = 不限 |
| `PlaySound` | `true` | 是否播放提示音 |

修改配置后需重启插件/宿主。配置读取失败时会回退到默认值，并在宿主日志中记录原因。

## 安全建议

默认配置为了兼容旧版本 **不启用鉴权**，且监听所有网卡——局域网内任何人都能推送通知。对外网或不可信网络开放时，请至少：

1. 设置 `Token` 为足够长的随机字符串，并通过 `X-API-Key` 请求头传递：
   ```bash
   curl -H 'X-API-Key: <你的令牌>' \
        -H 'Content-Type: application/json' \
        -d '{"content":"测试"}' http://127.0.0.1:23000/notice
   ```
   WebUI 可在「高级选项」中填写令牌，浏览器会保存在本地。
2. 设置 `RateLimitPerMinute`（例如 `30`）限制滥用。
3. 如只在本机使用，把 `BindAddress` 设为 `127.0.0.1`。
4. 不要直接把 23000 端口映射到公网；如需远程访问，优先使用 VPN 或反向代理 + HTTPS。

## 构建

需要 Windows + .NET 6 SDK，以及本机的 InkCanvas 插件 SDK 仓库。默认从 `E:\Develop\community` 读取，可覆盖：

```powershell
dotnet build RemoteMessagePlugin.csproj -p:InkCanvasPluginSdkDir=D:\path\to\community
```

GitHub Actions 复用 `InkCanvasForClass/community` 的共享构建工作流，推送到 `main` 或打 `v*` 标签时会自动构建并发布 `.icpx`。

## 版本

- `1.1.0`：POST API、令牌鉴权、限流、健康检查、可移植配置、全新 WebUI、嵌入资源。
- `1.0.0`：初始版本，`GET /notice/{内容}` + 内置 WebUI。
