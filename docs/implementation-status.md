# 实现状态

## 已完成

- 独立解决方案、统一严格编译配置和 NuGet SDK 依赖；
- 完整 Manifest 基线与设计文档中的稳定 ID；
- Avalonia 主页面、命令注册和插件启停清理；
- 房间状态机、设置模型、运行会话选择、LAN 输出解析、游戏结束自动退出；
- IPC Envelope、little-endian framing、帧上限和协议测试；
- Helper 进程请求、stdin Secret、超时、正常/强制退出路径；
- Rust Helper 参数校验、父进程监测、Windows named pipe、Unix socket、握手、Idle 状态和 shutdown；
- Rust Helper 并发安全房间状态、可替换 `RoomBackend`、完整 create/join/leave/set-lan-address/diagnose IPC 路径；
- Scaffolding v1 兼容帧、协议协商、Minecraft 端口、玩家心跳/列表和本地双端集成测试；
- Windows named pipe 当前用户/SYSTEM DACL、客户端 PID 核对与真实子进程端到端测试；
- 已生成锁定依赖的 `Cargo.lock`，Rust 1.85 下 `fmt`、`clippy -D warnings` 和全部测试通过；
- `secure-storage` 权限白名单修复；
- SDK Build NuGet 工具目录修复；
- `.pnp` native mode 写入和 Unix 安装执行位恢复。
- `pcl.package-assets` 签名文件表解析、路径约束和 SHA-256 复核已在 SDK/宿主源码实现并有回归测试；
- 已升级到发布版 PCL N Plugin SDK `0.1.1`，移除相邻源码和 `0.1.0` 工具路径兼容；
- 可选诊断窗口、网络诊断命令、剪贴板复制和脱敏 JSON 报告导出；
- 身份初始化、secure storage fail-closed 状态处理，以及 Helper 会话内身份种子清零。
- **alpha.2：** `EasyTierRoomBackend` 默认生产后端、房间凭据派生、EasyTier 子进程生命周期（`--no-tun`）、Scaffolding 接入 create/join、成员本机 TCP 转发、同机 discovery 公告；
- **alpha.2：** 稳定错误码 `network.easytier-missing` / `network.peer-unreachable` 等，插件映射 `TC-NET-002`–`TC-NET-004`；
- **alpha.2：** 版本 `0.1.0-alpha.2`，文档 `docs/network.md`。

## 正在推进

- EasyTier 跨机用户态端口映射与成员发现（替换本机 discovery 依赖）；
- NAT 探测、直连/中继质量与 RPC 诊断细化。

## 后续里程碑

1. 跨机 EasyTier 端口映射与对端可达，替换 `network.peer-unreachable` 本机限制；
2. EasyTier NAT 探测、直连/中继与网络质量上报；
3. 六 RID `easytier-core` 制品纳入 `.pnp` 与 CI；
4. 解决公共 Contracts 的默认 ALC 共享机制并注册四个插件导出；
5. 跨 PCL N、PCL CE 和其他兼容启动器的端到端联机测试；
6. 完整许可证、SBOM、第三方许可、官方签名和商店审核发布。
