# 安全边界

## Secret 与凭据

- IPC Secret 使用系统 CSPRNG 生成，每次 Helper 启动重新生成；
- Secret 不进入命令行、环境变量、设置文件、日志或诊断导出；
- Helper stdin 只接受恰好 64 个小写十六进制字节，不接受换行或空白；
- 长期身份材料必须进入 `pcl.secure-storage`，不得回退成明文文件；
- 插件首次进入房间前生成 32 字节身份种子并写入宿主安全存储，后续复用；
- 安全存储缺失、不可用、写入失败或身份长度异常时 fail closed，并报告 `TC-SEC-001`；
- 身份种子仅在已鉴权 IPC 中传给 Helper，托管临时缓冲区与 Helper 会话状态在释放时清零。

## IPC

- 不监听 TCP；
- Windows pipe 拒绝远程客户端并限制单实例；
- Windows pipe DACL 仅授权当前用户与 `SYSTEM`，连接后要求客户端 PID 等于 PCL N 父进程；
- Unix socket 父目录为 `0700`，socket 为 `0600`，并核对 peer UID；
- Unix 清理守卫核对 device/inode，只删除本次创建的 socket，不追随替换后的符号链接；
- 请求帧、请求 ID和握手时间均有上限。

## 进程与包

- Manifest 只申请设计文档列出的 UI、会话、输出、进程、剪贴板、安全存储和网络权限；
- Helper 由签名 `.pnp` 的 `runtimes/<rid>/native/` 提供，CI 要求六个目标齐全；
- SDK 打包器写入 Unix executable mode，安装器依据签名文件路径独立恢复执行位；
- 插件停用时先正常 shutdown，3 秒内未退出则取消宿主进程任务并终止进程树；
- SDK `0.1.1` 与宿主已提供 `pcl.package-assets`，会在返回绝对路径前按签名 PNP 文件表复核大小和 SHA-256。

## 日志

插件只记录稳定错误码和脱敏消息。Bearer、token、private key、room credential 等模式进入日志前由 `SensitiveDataRedactor` 清理。Helper 禁止记录完整 Envelope 或 payload。

诊断报告由用户主动生成，默认仅写入插件隔离目录；完整房间码、玩家 ID 和玩家名不进入报告，Helper stdout/stderr 会先脱敏再截断到最近 16 KiB，且不会自动上传。

## Scaffolding 数据面

- 内置服务端只绑定 loopback，不直接暴露公网监听；
- 请求类型最多 128 字节、正文最多 64 KiB，长度在分配前验证；
- 玩家名、机器 ID、厂商字段均有长度和控制字符约束，远端声明的角色会被服务端覆盖为 Guest；
- 心跳失活成员在 10 秒后清理，房主身份不能被远端机器 ID 覆盖；
- 未知请求返回有界错误，不等待到连接超时。

## EasyTier 与房间凭据

- EasyTier 以 Helper 子进程运行，使用 `--no-tun`，不申请管理员、不创建永久虚拟网卡；
- `network-secret` 经环境变量注入子进程，不写入 Helper 参数列表或诊断日志；
- 房间网络密钥由房间码与协议根派生，离房后清零；
- 本机 discovery 公告仅含 loopback 端点，Unix 下目录/文件权限限制为当前用户；
- 缺失 sidecar 时 fail closed，返回 `network.easytier-missing`。
