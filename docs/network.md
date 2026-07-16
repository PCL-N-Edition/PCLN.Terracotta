# 网络后端

Helper 通过可替换的 `RoomBackend` 接入生产网络。默认实现为 `EasyTierRoomBackend`：EasyTier 子进程 + Scaffolding + 本机/ mesh TCP 转发。

当前版本：`0.1.0`。

## 组件边界

```text
room.create / room.join
        │
        ▼
 EasyTierRoomBackend
  ├─ credentials     房间码 ↔ network-name / network-secret
  ├─ mesh            确定性 host 虚拟端点与成员本地转发端口
  ├─ easytier-core   同目录 sidecar（默认 --no-tun）
  ├─ Scaffolding     仅 loopback 业务口
  ├─ mesh ingress    房主 0.0.0.0:mesh_port → loopback 服务
  ├─ PortForward     同机成员 loopback 转发
  └─ local discovery 同机房主端点公告（快速路径）
```

## EasyTier 运行时

| 解析顺序 | 来源 |
|---|---|
| 1 | 环境变量 `TERRACOTTA_EASYTIER_PATH` |
| 2 | 与 `terracotta-helper` 同目录的 `easytier-core[.exe]` |

缺失时返回 `network.easytier-missing`，不会伪造 `Connected`。

| 环境变量 | 作用 |
|---|---|
| `TERRACOTTA_EASYTIER_PATH` | 显式 sidecar 路径 |
| `TERRACOTTA_EASYTIER_PEERS` | 覆盖默认共享节点列表 |
| `TERRACOTTA_EASYTIER_ALLOW_TUN` | `1/true` 时允许创建 TUN（可能需要管理员），增强跨机可达 |
| `TERRACOTTA_DISCOVERY_DIR` | 覆盖同机 discovery 目录 |

诊断：

1. 若同目录存在 `easytier-cli`，对确定性 RPC portal 执行 `peer` 解析 NAT/延迟/中继；
2. 否则对 Minecraft/本地转发端口做 TCP RTT 探测。

启动策略：

- 默认 `--no-tun` + `--use-smoltcp`：不创建永久虚拟网卡；
- `ET_NETWORK_NAME` / `ET_NETWORK_SECRET` 经环境变量注入；
- RPC portal 仅绑定 loopback；
- 成员侧使用 `--port-forward tcp://127.0.0.1:<local>/<host-virtual>:<mesh>`。

## 房间凭据

- 房主 CSPRNG 生成 12 位房间码；
- `network_name` / `network_secret` 仅由房间码派生；
- 身份种子仅用于 Scaffolding `machine_id`。

## 跨机 mesh（alpha.3）

由房间码确定性派生：

| 端点 | 范围 |
|---|---|
| Host 虚拟 IP | `10.144.144.1` |
| Mesh Scaffolding 端口 | `41000–49999` |
| Mesh Minecraft 端口 | `51000–59999` |
| 成员本地 Scaffolding 转发 | `42000–50999` |
| 成员本地 Minecraft 转发 | `52000–60999` |

**房主 create**

1. 启动 EasyTier（`--ipv4 10.144.144.1`）；
2. Scaffolding 绑定 `127.0.0.1:0`；
3. Mesh ingress：`0.0.0.0:<mesh_port>` → loopback 服务；
4. 发布同机 discovery 公告。

**成员 join**

1. 短轮询同机 discovery（快速路径）；
2. 否则启动 EasyTier，并配置到 host 虚拟端点的 `--port-forward`；
3. 探测成员本地转发端口就绪后连接 Scaffolding；
4. 返回 `127.0.0.1:<member_local_minecraft>`。

若 userspace 路由不足以把虚拟 IP 流量送到房主 ingress，可在两侧设置 `TERRACOTTA_EASYTIER_ALLOW_TUN=1` 再试。

## 稳定错误码

| 代码 | 含义 |
|---|---|
| `network.easytier-missing` | 未找到 EasyTier sidecar |
| `network.easytier-start-failed` | 子进程启动失败 |
| `network.easytier-stop-failed` | 停止子进程失败 |
| `network.scaffolding-failed` | Scaffolding 绑定失败 |
| `network.forward-failed` | 本机 TCP 转发失败 |
| `network.mesh-ingress-failed` | 房主 mesh 入口绑定失败 |
| `network.peer-unreachable` | 本地与 mesh 路径均未发现房主 |
| `network.discovery-*` | 同机公告读写/校验失败 |

| Helper | 插件 |
|---|---|
| `network.easytier-missing` | `TC-NET-002` |
| `network.easytier-start-failed` | `TC-NET-003` |
| `network.peer-unreachable` | `TC-NET-004` |
| `network.mesh-ingress-failed` | `TC-NET-005` |
| 其他网络失败 | `TC-NET-001` |
