# PCLPatcher

> VibeCoding……

> [!note]
>
> 类似方法：https://linux.do/t/topic/3050889?tl=en
>
> ⬆️.bat实现，文件更小

基于 .NET 8 和 dnlib 4.5 的 PCL 静态 IL 补丁器。**只需要待修改的 PCL EXE，不需要旧版或已打补丁的 EXE。**

当前参考目标是 `Plain Craft Launcher 2 2.99.0.0`，补丁器版本是 `1.0.0`，两者独立。未知 PCL 版本只有六项结构检查全部通过才允许写出；这只表示结构兼容，功能未经验证。

## 下载哪个包

| 文件名（`<版本>` 为补丁器版本） | 使用条件 |
|---|---|
| `PCLPatcher-<版本>-win-x64-framework-dependent.zip` | Windows x64，已安装 .NET 8 Runtime；最小包 |
| `PCLPatcher-<版本>-win-x64-self-contained.zip` | Windows x64，无需另装运行时 |
| `PCLPatcher-<版本>-linux-x64-framework-dependent.tar.gz` | Linux x64，需要 .NET 8 Runtime；暂未运行验证 |
| `PCLPatcher-<版本>-linux-x64-self-contained.tar.gz` | Linux x64，内含运行时；暂未运行验证 |

免运行时包启用单文件压缩及原生库提取，不裁剪。第一次启动可能将运行时原生库提取到临时目录。Linux 包用于改写 **Windows PCL EXE**；PCL 本身仍需 Windows。Linux 的运行与功能验证状态单独标注。

下载后解压，命令行进入该目录。请保留包内的中文说明与许可文件。`SHA256SUMS.txt` 是四个包的 SHA-256 校验值，`PACKAGE-SIZES.json` 记录实际大小与内含运行时版本。

## 怎么用

先检查，不写文件：

```powershell
.\PCLPatcher.exe --input "D:\PCL\Plain Craft Launcher 2.exe" --check
```

生成新文件，默认文件名为 `Plain Craft Launcher 2.patched.exe`：

```powershell
.\PCLPatcher.exe --input "D:\PCL\Plain Craft Launcher 2.exe"
.\PCLPatcher.exe --input "D:\PCL\Plain Craft Launcher 2.exe" --output "D:\PCL\PCL-patched.exe"
```

原地替换，自动在原文件旁生成时间戳备份：

```powershell
.\PCLPatcher.exe --input "D:\PCL\Plain Craft Launcher 2.exe" --in-place
.\PCLPatcher.exe --version
```

Linux 命令将 `.\PCLPatcher.exe` 换成 `./PCLPatcher`，路径使用实际挂载路径。

- `--output` 与 `--in-place` 互斥；`--check` 不能与输出选项组合。
- 已有输出文件会拒绝覆盖；换一个输出名，或确认后自行处理旧输出。
- 原地模式发现六项均已应用时直接成功，不重复备份；输出模式仍生成约定输出，文件字节保持一致。
- 已移除 `--donor`，旧命令会显示迁移提示。路径有中文或空格时用引号包裹。
- PCL 自动更新可能覆盖补丁。更新后重新执行 `--check`；不支持时保留原文件，等待适配。

补丁前关闭要修改的 PCL。恢复备份时也先关闭 PCL，将 `原文件.exe.bak-时间戳` 复制回原文件路径即可。备份就是写入前的原始字节；不要把备份文件和补丁器一起上传到 Release。

## 六项规则

| 编号 | 修改目的 | 主要定位与验证 |
|---|---|---|
| P1 | 恢复拖入整合包后的错误处理 | 沿 FileDrag 委托找到整合包调用及取消、崩溃分析分支，移除特定提示分支 |
| P2 | 恢复原版游戏版本列表 | LoaderInit 中定位下载加载器与 PageLoaderInit 调用 |
| P3 | 恢复选择整合包并后台安装 | 从目标解析文件选择接口、闭包字段及 RunInThread，保留筛选与取消 |
| P4 | 恢复各整合包安装分派 | 保留输入自己的识别、错误处理与 finally，只恢复末尾分派；包含 MCBBS |
| P5 | 显示 Minecraft 版本卡片 | 完整方法签名、卡片 getter 与 Visibility setter |
| P6 | 恢复资源页整合包安装按钮 | 根据 Install_Click 调用定位事件适配器，按类型定位局部变量 |

每条规则报告“可应用（尚未打补丁） / 已打补丁 / 不支持此文件”。`--check` 仅检查，不会打补丁；检查通过后，去掉 `--check` 重新运行即可生成补丁文件。未匹配或多个候选均拒绝修改，不将未匹配视为成功。闭包编号与局部变量槽号变化可以通过结构定位适配；方法、签名、分支、数据流变化仍可能需要人工适配。

全部只读检查通过后才修改内存程序集。输出先写到目标目录的唯一临时文件，再重新加载、逐项验证、检查分支目标、异常边界及栈计算，最后提交。原地替换保留备份；提交前还会检查输入哈希是否变化。结构检查无法替代真实 PCL 功能测试。

| 退出码 | 含义 |
|---|---|
| 0 | 检查或补丁成功 |
| 2 | 参数、路径或输出冲突 |
| 3 | 程序集或补丁结构不支持 |
| 4 | 读写、构建程序集等操作失败 |
| 5 | 修改后的 IL 或临时输出验证失败 |

## 源码结构与验证

```text
Program.cs                  入口、退出码
CommandLine.cs              参数与迁移提示
Patching/PatchRule.cs        三种检查结果与只读检查约定
Patching/PatchRunner.cs      先检查、后修改、重载验证
Patching/AtomicOutput.cs     临时文件、备份与原子提交
Patching/Il.cs               签名定位、局部变量及 IL 安全检查
Patching/Rules/              六项独立规则
tests/                      无额外测试框架依赖的回归程序
scripts/release.ps1          四包发布脚本
```

```powershell
dotnet build -c Release
# 通用参数、IL、防写坏与事务测试，不需要 PCL
dotnet run --project tests/PCLPatcher.Tests.csproj -c Release
# 真实目标回归：仅在本地提供原版文件，不提交或上传目标 EXE
dotnet run --project tests/PCLPatcher.Tests.csproj -c Release -- "D:\PCL\原版.exe"
```

目标回归涵盖六项修改、重复执行、部分恢复、方法缺失、签名变化、歧义、闭包改名、局部变量重排、模拟未知版本、中文空格路径、备份恢复及输出校验失败。P3/P4 的实际 IL 还会提取到隔离程序集执行，替换 UI、日志和安装器外部调用，检查选择取消、格式识别、MCBBS 路径、损坏压缩包、路径限制与 finally。它不启动真实安装器，完整安装、下载及 UI 仍需人工验证。

## 生成 Release

需要 .NET 8 或更新 SDK 和 PowerShell 7.4+。从仓库运行：

```powershell
pwsh -File ./scripts/release.ps1
# 或指定一个空的产物目录
pwsh -File ./scripts/release.ps1 -OutputDirectory "D:\Release\PCLPatcher-1.0.0"
```

脚本生成四个包、`SHA256SUMS.txt` 和 `PACKAGE-SIZES.json`。独立临时目录避免混入旧文件；已有产物拒绝覆盖。每包只包含补丁器、此中文说明、第三方说明和许可，不包含测试目标或源码。GitHub 工作流在推送、PR 或手动触发时构建、跑通用测试并上传资产；`v*` 标签另外创建草稿 Release，由维护者确认后发布。

本地应额外解压两个 Windows 包，各验证启动、检查及修改，再做 PCL 功能冒烟。Linux 本轮只验证构建和归档内容，发布说明保留“暂未运行验证”。

单文件发布参数依据：https://learn.microsoft.com/dotnet/core/deploying/single-file/overview
第三方许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
