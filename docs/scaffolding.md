# Scaffolding 兼容协议

Terracotta Helper 内置一份独立实现的 Scaffolding v1 兼容层，用于与 PCL CE 等兼容启动器交换 Minecraft 服务端口和玩家资料。它属于房间控制协议之上的数据交换层，不负责发现房间或建立 EasyTier 网络。

## 请求帧

```text
1 byte  request type length (1..=128)
N bytes printable ASCII request type
4 bytes big-endian body length (0..=65536)
N bytes body
```

响应帧：

```text
1 byte  status (0 = success)
4 bytes big-endian body length (0..=65536)
N bytes body
```

状态 `32` 表示请求内容无效，`255` 表示未知请求；`255` 的正文可以包含有界 UTF-8 错误说明。

## v1 请求

| 类型 | 请求正文 | 成功响应 |
|---|---|---|
| `c:ping` | 少于 32 字节的任意内容 | 原样返回 |
| `c:protocols` | NUL 分隔的能力列表 | NUL 分隔的交集 |
| `c:server_port` | 空 | 2 字节 big-endian Minecraft 端口 |
| `c:player_ping` | snake_case JSON 玩家资料 | 空 |
| `c:player_profiles_list` | 空 | snake_case JSON 玩家数组 |

玩家资料结构：

```json
{
  "name": "Player",
  "machine_id": "opaque-machine-id",
  "vendor": "PCL N Terracotta",
  "kind": "GUEST"
}
```

服务端不会信任客户端提交的 `kind`，所有远端资料都会覆盖为 `GUEST`。房主资料固定为 `HOST`；同一机器 ID 的心跳会刷新资料与最后在线时间，超过 10 秒未刷新时移除。

## 与 EasyTier 的边界

Scaffolding 服务端只监听 `127.0.0.1` 或 `::1`。`EasyTierRoomBackend` 在房主侧启动 Scaffolding，在成员侧通过本机 TCP 转发接入；跨机仍依赖 EasyTier 用户态连通（alpha.2 另有同机 discovery 路径）。Scaffolding 层本身不接收房间密钥、不启动外部进程，也不修改防火墙或系统网卡。

当前兼容语义参考 PCL CE 公开的 Scaffolding v1 实现；Terracotta 额外增加了帧上限、未知请求响应、资料约束和明确的协议交集检查。
