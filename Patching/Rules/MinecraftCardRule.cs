using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PCLPatcher;

// P5: Match the exact property pair and one visibility assignment.
internal sealed class MinecraftCardRule() : PatchRule("P5", "显示 Minecraft 版本卡片")
{
    protected override PatchAnalysis Analyze(ModuleDef module)
    {
        var method = Il.Method(Il.Type(module, "PCL.PageDownloadModify"), "LoadInstanceFinished", "System.Void",
            "PCL.ModLoader/LoaderTask`2<PCL.ModMinecraft/McInstance,System.Object>");
        var ins = method.Body.Instructions;
        int k = Il.One(ins.Select((i, n) => (i, n)).Where(p => p.n + 2 < ins.Count &&
            Il.Call(p.i, "PCL.PageDownloadInstall", "get_CardMinecraft") && p.i.Operand is IMethod getter && Il.Signature(getter, "PCL.MyCard") &&
            ins[p.n + 1].IsLdcI4() && Il.Call(ins[p.n + 2], "System.Windows.UIElement", "set_Visibility") &&
            ins[p.n + 2].Operand is IMethod setter && Il.Signature(setter, "System.Void", "System.Windows.Visibility")), "Minecraft 卡片可见性设置").n + 1;
        if (Il.Number(ins[k], 0)) return Applied(method);
        if (!Il.Number(ins[k], 2)) throw new UnsupportedException("卡片可见性不是预期的 Collapsed。");
        return Change(method, () => { ins[k].OpCode = OpCodes.Ldc_I4_0; ins[k].Operand = null; });
    }
}
