# 开发与发布

## 本地检查

```powershell
dotnet test PCLN.Terracotta.slnx -c Release --nologo
dotnet build src/PCLN.Terracotta.Plugin/PCLN.Terracotta.Plugin.csproj -c Release --nologo
```

项目使用已发布的 PCL N Plugin SDK `0.1.1`。该版本提供 `pcl.package-assets`，并修复 SDK Build 工具目录，因此不再需要相邻 SDK 源码或 `0.1.0` 的打包路径兼容逻辑。

Helper：

```powershell
cargo fmt --manifest-path src/Terracotta.Helper/Cargo.toml -- --check
cargo clippy --manifest-path src/Terracotta.Helper/Cargo.toml --locked --all-targets -- -D warnings
cargo test --manifest-path src/Terracotta.Helper/Cargo.toml --locked --all-targets
```

## 六平台产物

| RID | Rust target | 文件 |
|---|---|---|
| `win-x64` | `x86_64-pc-windows-msvc` | `terracotta-helper.exe` |
| `win-arm64` | `aarch64-pc-windows-msvc` | `terracotta-helper.exe` |
| `linux-x64` | `x86_64-unknown-linux-gnu` | `terracotta-helper` |
| `linux-arm64` | `aarch64-unknown-linux-gnu` | `terracotta-helper` |
| `osx-x64` | `x86_64-apple-darwin` | `terracotta-helper` |
| `osx-arm64` | `aarch64-apple-darwin` | `terracotta-helper` |

CI 把文件下载到 `native/<rid>/` 后以 `TerracottaRequireNativeHelpers=true` 打包。开发机可不放原生文件来编译和测试托管代码，但这种 `.pnp` 仅用于结构检查，不得分发。

## 发布门禁

1. .NET 测试在 Windows、Ubuntu、macOS 全绿；
2. Helper `fmt`、`clippy -D warnings`、测试在三系统全绿；
3. 六个 RID 都能 release build；
4. 完整 PNP 包含六个 Helper，文件表、Merkle root 与签名验证通过；
5. 安装测试确认 Unix Helper 可执行；
6. Windows DACL/PID 校验、package-asset API 与跨插件 Contracts 共享问题关闭；
7. 将六 RID 的 `easytier-core` 与 Helper 一并放入 `native/<rid>/` 后打包；
8. 完成跨机端口映射、崩溃/重连和跨启动器测试矩阵。

开发机可选放置 EasyTier sidecar：

```text
native/<rid>/easytier-core[.exe]
```

或设置 `TERRACOTTA_EASYTIER_PATH` 指向可执行文件。无 sidecar 时托管测试仍可通过；Helper 生产 create/join 会返回 `network.easytier-missing`。
