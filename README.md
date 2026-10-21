# PCLPatcher

Plain Craft Launcher 2 `2.99.0.0` 的静态 IL 补丁器，基于 **.NET 8 + dnlib 4.5**，跨平台（Windows / Linux 均可运行并改写 Windows PE 程序集）。

## 补丁内容

| 编号 | 目标 | 效果 |
|---|---|---|
| P1 | `PCL.FormMain/_Closure$__25-0::_Lambda$__0` | 移除拖入整合包失败时的“法律法规”提示分支 |
| P2 | `PCL.PageDownloadInstall::LoaderInit` | 恢复 `ModDownload.DlClientListLoader`，原版游戏下载可用 |
| P3 | `PCL.ModModpack::ModpackInstall()` | 恢复“选择整合包文件并后台安装”入口 |
| P4 | `PCL.ModModpack::ModpackInstall(string,string,string)` | 从 donor 移植完整的整合包类型判定与安装调度 |
| P5 | `PCL.PageDownloadModify::LoadInstanceFinished` | 显示版本修改页的 MC 版本卡片 |
| P6 | `PCL.PageDownloadResourceDetail::InitializeStackResourceVersions` | 恢复资源详情页整合包的“安装”按钮 |

## 版本限制

只支持 `Plain Craft Launcher 2, Version=2.99.0.0`。输入或 donor 版本不符时会直接中止。

## 构建

```bash
dotnet build -c Release
```

## 运行

```bash
# 输出到新文件
PCLPatcher --input "Plain Craft Launcher 2.exe" --donor "PCL-patched.exe" --output "PCL-patched-new.exe"

# 原地替换（自动生成时间戳备份）
PCLPatcher --input "Plain Craft Launcher 2.exe" --donor "PCL-patched.exe" --in-place
```

参数：

- `--input`：待补丁的官方 exe。
- `--donor`：同版本且已包含 P3/P4 修改的 exe；P3 复用其选择文件调用模板，P4 直接移植其方法体。
- `--output`：输出路径；省略且未用 `--in-place` 时输出 `<input>.patched.exe`。
- `--in-place`：写临时文件并校验通过后，备份并替换输入文件。

补丁器会先检测每条规则是否已应用（幂等），然后写临时文件、反编译校验 P1–P6，全部通过才覆盖。

## 发布

Windows 单文件：

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Linux 单文件：

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```

产物位于 `bin/Release/net8.0/<rid>/publish/`。

> 注意：PCL 自动更新会用官方新版覆盖补丁；更新后需重新运行补丁器。
