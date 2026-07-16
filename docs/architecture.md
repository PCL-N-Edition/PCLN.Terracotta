# 架构

## 进程边界

PCL N 只在受控插件运行时中加载 `PCLN.Terracotta.Plugin.dll`。网络核心放在独立的 `terracotta-helper` 进程中，避免把 Rust 原生依赖或网络崩溃面带入启动器进程。

```text
PCL N
└─ Terracotta Plugin
   ├─ Avalonia 页面、诊断窗口与命令
   ├─ TerracottaController
   ├─ 游戏会话、输出和启动事件
   ├─ HelperProcessManager
   └─ HelperIpcClient
          │ 本地鉴权 IPC
          ▼
      Terracotta Helper
      ├─ 父进程监测
      ├─ IPC Session
      ├─ 并发安全 RoomService
      ├─ Scaffolding v1 兼容层
      └─ EasyTierRoomBackend（easytier-core sidecar）
```

## 插件端职责

- 通过公共 SDK 注册 `cn.pcln.terracotta.page`；
- 将页面、命令和游戏事件转换成控制器意图；
- 维护 `Idle → WaitingForGame/WaitingForLan → Creating/Joining → Connected → Leaving` 状态机；
- 从会话快照或 Minecraft 输出中只提取受信任的 loopback LAN 端口；
- 通过 `IPluginTaskService` 承载后台工作，通过 `IPluginProcessService` 承载 Helper；
- 停用插件或游戏退出时先发 IPC shutdown，再取消进程任务。
- 由用户主动运行网络诊断，并将脱敏报告写入插件隔离数据目录；报告不会自动上传。

页面不直接操作 Helper。所有入口都经过 `TerracottaController` 的串行操作门，因此按钮、命令和事件回调不会并发修改房间状态。

## Helper 端职责

- 验证平台相关 IPC 地址和绝对数据目录；
- 从 stdin 读取恰好 64 个小写十六进制字符，不从参数或环境变量读取 Secret；
- 监测 PCL N 父进程并在父进程退出时结束；
- Windows 使用本地 named pipe，Unix 使用权限为 `0600` 的 Unix socket；
- 执行协议握手、请求 ID 去重、有界帧处理和稳定错误返回；
- 在 `RoomService` 内串行化创建、加入、更新 LAN、诊断和退出，并拒绝不可信后端结果；
- 提供 Scaffolding v1 帧、协议协商、服务端口、玩家资料与心跳兼容层；
- 通过 `EasyTierRoomBackend` 管理 EasyTier 子进程、房间凭据、Scaffolding 与本机 TCP 转发；跨机端口映射仍在增强中。

## 依赖方向

`Contracts` 不依赖 SDK 或 Avalonia；`Plugin` 依赖 `Contracts` 与 PCL N Plugin SDK；Helper 使用协议等价的 Rust DTO，不链接 PCL N 代码。IPC 整数协议版本独立于插件版本。
