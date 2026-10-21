# 第三方组件

PCLPatcher 使用以下组件；本文件只说明第三方许可，不替项目本身选择许可证。

| 组件 | 版本 | 许可 | 随包文件 |
|---|---|---|---|
| dnlib | 4.5.0 | MIT | `licenses/dnlib-LICENSE.txt` |
| .NET Runtime（仅免运行时包内含） | 发布时解析的 .NET 8 补丁版本，见 `PACKAGE-SIZES.json` | MIT 及第三方组件许可 | `licenses/dotnet-LICENSE.txt`、`licenses/dotnet-THIRD-PARTY-NOTICES.txt` |

源码与许可来源：

- dnlib：https://github.com/0xd4d/dnlib
- .NET：https://github.com/dotnet/runtime

最小包由使用者另行安装 .NET 8 Runtime。发布包不包含 PCL、donor 或任何补丁后的 PCL EXE。
