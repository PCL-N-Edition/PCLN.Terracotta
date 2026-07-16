# 网络后端（alpha.2）

Helper 通过可替换的 `RoomBackend` 接入生产网络。默认实现为 `EasyTierRoomBackend`：EasyTier 子进程 + Scaffolding + 本机 TCP 转发。

## 组件边界

```text
room.create / room.join
        │
        ▼
 EasyTierRoomBackend
  ├─ credentials     房间码 ↔ network-name / network-secret
  ├─ easytier-core   同目录 sidecar 子进程（--no-tun）
  ├─ Scaffolding     仅 loopback
  ├─ PortForward     成员侧 127.0.0.1 本地转发
  └─ local discovery 同机房主端点公告（临时目录）
```

## EasyTier 运行时

| 解析顺序 | 来源 |
|---|---|
| 1 | 环境变量 `TERRACOTTA_EASYTIER_PATH` |
| 2 | 与 `terracotta-helper` 同目录的 `easytier-core[.exe]` |

缺失时返回稳定错误码 `network.easytier-missing`，不会伪造 `Connected`。

启动策略：

- `--no-tun` + `--use-smoltcp`：不创建永久虚拟网卡、不要求管理员；
- `ET_NETWORK_NAME` / `ET_NETWORK_SECRET` 经环境变量注入，不写磁盘明文配置；
- RPC portal 仅绑定 loopback；
- 默认共享节点可被 `TERRACOTTA_EASYTIER_PEERS` 覆盖。

正式 `.pnp` 期望布局：

```text
runtimes/<rid>/native/
  terracotta-helper[.exe]
  easytier-core[.exe]
```

开发机可只放 Helper；无 EasyTier 时创建/加入会进入 `Faulted`。

## 房间凭据

- 房主用 CSPRNG 生成 12 位房间码（`XXXX-XXXX-XXXX`）；
- `network_name` / `network_secret` 仅由房间码经协议根密钥 HMAC/SHA-256 派生，成员无需房主私钥；
- 身份种子仅用于 Scaffolding `machine_id` 与本地玩家资料；
- 离房或进程退出后清零内存中的 secret。

## 本地发现（alpha.2 限制）

跨机 EasyTier 端口映射尚未完整。当前加入路径会：

1. 启动 EasyTier；
2. 在本机发现目录查找房主公告（默认 `%TEMP%/pcln-terracotta/rooms/`，可用 `TERRACOTTA_DISCOVERY_DIR` 覆盖）；
3. 将 Scaffolding 与 Minecraft 端口转发到成员 loopback；
4. 超时返回 `network.peer-unreachable`（可重试）。

因此 **同机双实例** 可用于端到端联调；跨机互通依赖后续用户态端口映射增强。

## 稳定错误码

| 代码 | 含义 |
|---|---|
| `network.easytier-missing` | 未找到 EasyTier sidecar |
| `network.easytier-start-failed` | 子进程启动失败或立即退出 |
| `network.easytier-stop-failed` | 停止子进程失败 |
| `network.scaffolding-failed` | Scaffolding 绑定/上下文失败 |
| `network.forward-failed` | 本地 TCP 转发失败 |
| `network.peer-unreachable` | 未发现房主端点 |
| `network.discovery-*` | 本机公告读写/校验失败 |

插件侧映射：

| Helper | 插件 |
|---|---|
| `network.easytier-missing` | `TC-NET-002` |
| `network.easytier-start-failed` | `TC-NET-003` |
| `network.peer-unreachable` | `TC-NET-004` |
| 其他网络失败 | `TC-NET-001` |
