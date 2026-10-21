using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PCLPatcher;

// P2: Replace only the disabled LoaderTask expression; retain PageLoaderInit wiring.
internal sealed class DownloadLoaderRule() : PatchRule("P2", "恢复原版游戏版本列表")
{
    protected override PatchAnalysis Analyze(ModuleDef module)
    {
        var method = Il.Method(Il.Type(module, "PCL.PageDownloadInstall"), "LoaderInit", "System.Void");
        var field = Il.One(Il.Type(module, "PCL.ModDownload").Fields.Where(f => f.Name == "DlClientListLoader" && f.IsStatic &&
            f.FieldType.FullName == "PCL.ModLoader/LoaderTask`2<System.String,PCL.ModDownload/DlClientListResult>"), "真实版本列表 loader 字段");
        var ins = method.Body.Instructions;
        if (ins.Count(i => Il.Call(i, "PCL.MyPageRight", "PageLoaderInit")) != 1)
            throw new UnsupportedException("PageLoaderInit 调用不唯一。");
        int loads = ins.Count(i => i.OpCode == OpCodes.Ldsfld && i.Operand is IField f && f.FullName == field.FullName);
        var starts = ins.Select((i, n) => (i, n)).Where(p => p.i.OpCode == OpCodes.Ldstr && (string)p.i.Operand == "关闭下载").ToArray();
        if (loads == 1 && starts.Length == 0) return Applied(method);
        if (loads != 0) throw new UnsupportedException("版本列表 loader 存在混合或重复状态。");
        int start = Il.One(starts, "关闭下载 loader").n;
        int end = Il.One(ins.Select((i, n) => (i, n)).Where(p => p.n > start && p.i.OpCode == OpCodes.Newobj &&
            p.i.Operand is IMethod m && m.DeclaringType.FullName == field.FieldType.FullName && m.Name == ".ctor"), "关闭下载 loader 构造").n;
        Il.CheckRange(method, start, end);
        return Change(method, () => Il.Replace(method, start, end, [Instruction.Create(OpCodes.Ldsfld, field)]));
    }
}
