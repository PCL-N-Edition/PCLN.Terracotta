# 本地 IPC 协议

## 传输

- Windows：`\\.\pipe\pcln-terracotta-<32 lowercase hex>`；
- Linux/macOS：绝对路径 `terracotta-<32 lowercase hex>.sock`；
- 每一帧由 4 字节 little-endian `u32` 长度和 UTF-8 JSON 组成；
- 长度必须在 `1..=1_048_576`，超限在分配 payload 前拒绝；
- 第一阶段每个 Helper 只允许一个本地客户端，串行处理请求形成自然背压。

## Envelope

```json
{
  "protocol": 1,
  "id": "request-1",
  "type": "room.status",
  "payload": {}
}
```

`id` 必须是 1–128 个可打印 ASCII 字符，且在连接内唯一。未知 JSON 字段被忽略，以允许同一协议版本增加可选字段。

## 握手

插件启动 Helper 前生成 32 字节随机数，并把 64 字节小写十六进制表示写入 Helper stdin。首帧必须在 5 秒内发送同一值：

```json
{
  "protocol": 1,
  "id": "request-1",
  "type": "hello",
  "payload": {
    "authToken": "<64 lowercase hex>",
    "client": "pcln",
    "clientVersion": "0.1.0"
  }
}
```

成功响应：

```json
{
  "protocol": 1,
  "id": "request-1",
  "type": "hello.accepted",
  "payload": {
    "helperVersion": "0.1.0",
    "capabilities": [
      "identity.initialize",
      "room.create",
      "room.join",
      "room.leave",
      "room.status",
      "room.set-lan-address",
      "network.diagnose",
      "events.push",
      "shutdown"
    ]
  }
}
```

鉴权使用恒定时间比较；原始 JSON 缓冲区和解析后的 Secret 会在使用后清零。失败只返回通用错误，不记录 payload。

## 已实现消息

| 请求 | 响应 | 状态 |
|---|---|---|
| `hello` | `hello.accepted` | 已实现 |
| `identity.initialize` | `identity.initialized` / `error` | 已实现；建房或入房前必须完成 |
| `room.status` | `room.status.result` | 已实现；会触发后端 refresh（成员/RTT） |
| `room.leave` | `room.left` | 已实现并清理后端 |
| `shutdown` | `shutdown.accepted` 后 EOF | 已实现 |
| `room.create` | `room.created` / `error` | mesh ingress + EasyTier host；缺 sidecar 时 `network.easytier-missing` |
| `room.join` | `room.joined` / `error` | 同机 discovery 优先，否则 EasyTier `--port-forward` 跨机路径 |
| `room.set-lan-address` | `room.state-changed` | 已实现，仅房主 Connected 状态可用 |
| `network.diagnose` | `diagnostic.updated` | 已实现，后端返回网络快照 |

### 推送事件（alpha.5，`events.push`）

Helper 可在任意时刻发送无关联请求的 Envelope（`id` 形如 `evt-N`）：

| 类型 | payload | 含义 |
|---|---|---|
| `peer.joined` | `{ "member": RoomMember }` | 新成员 |
| `peer.left` | `{ "id": "..." }` | 成员离开 |
| `peer.updated` | `{ "member": RoomMember }` | 成员质量/显示名变化 |
| `network.updated` | `NetworkStatus` | 网络质量/健康变化 |
| `room.state-changed` | `RoomSnapshot` | 生命周期变化（含 Connected↔Reconnecting） |

插件 IPC 客户端为双工：后台读循环按 `id` 匹配响应，其余推送进入事件通道。

未知消息返回 `ipc.unknown-message-type`；重复请求 ID 返回 `ipc.duplicate-request-id`；单连接最多记录 4096 个请求 ID。

房间服务会独立校验 Minecraft 地址必须为 loopback、端口非零、会话 ID 有界，并把房间码规范化为 `XXXX-XXXX-XXXX`。默认后端在同目录找不到 `easytier-core` 时进入 `Faulted` 并返回 `network.easytier-missing`；测试可通过注入 `RoomBackend` 验证完整成功路径。网络不健康时状态进入 `reconnecting`，恢复后回到 `connected`。
