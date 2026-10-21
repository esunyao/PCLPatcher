using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PCLPatcher;

// P1: Follow FileDrag's delegate instead of a compiler-generated closure number.
// Require the modpack call, cancellation catch, and crash-analysis fallback.
internal sealed class DragDropRule() : PatchRule("P1", "恢复拖入整合包后的错误处理")
{
    protected override PatchAnalysis Analyze(ModuleDef module)
    {
        var form = Il.Type(module, "PCL.FormMain");
        var entry = Il.Method(form, "FileDrag", "System.Void", "System.Collections.Generic.IEnumerable`1<System.String>");
        var install = Il.Method(Il.Type(module, "PCL.ModModpack"), "ModpackInstall", "PCL.ModLoader/LoaderCombo`1<System.String>", "System.String", "System.String", "System.String");
        var functions = entry.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldftn).Select(i => i.Operand)
            .OfType<MethodDef>().Where(m => m.HasBody && m.DeclaringType.DeclaringType == form && Il.Signature(m, "System.Void"));
        var method = Il.One(functions.Where(m => Il.Calls(m, install) &&
            m.Body.Instructions.Any(i => Il.Call(i, "MeloongCore.Extensions.ExceptionExtensions", "IsCanceled")) &&
            m.Body.Instructions.Any(i => Il.Call(i, "PCL.CrashAnalyzer", "Import"))), "FileDrag 的整合包处理委托");
        var ins = method.Body.Instructions;
        var restrictions = ins.Select((i, n) => (i, n)).Where(p => Il.IsRestriction(p.i)).ToArray();
        if (restrictions.Length == 0)
        {
            if (!method.Body.ExceptionHandlers.Any(e => e.HandlerType == ExceptionHandlerType.Catch) ||
                ins.Any(i => Il.Call(i, "System.Exception", "get_Message")))
                throw new UnsupportedException("拖入处理缺少预期的异常分支，或仍残留未知提示分支。");
            return Applied(method);
        }
        int k = Il.One(restrictions, "拖入处理的限制分支").n, start = k - 2, end = k + 10;
        if (start < 0 || end + 1 >= ins.Count ||
            Il.Load(ins[start], method.Body)?.Type.FullName != "System.Exception" ||
            !Il.Call(ins[k - 1], "System.Exception", "get_Message") || !Il.Number(ins[k + 1], 0) ||
            !Il.Call(ins[k + 2], "Microsoft.VisualBasic.CompilerServices.Operators", "CompareString") ||
            ins[k + 3].OpCode.FlowControl != FlowControl.Cond_Branch ||
            ins[k + 3].Operand != ins[end + 1] ||
            Il.Load(ins[k + 4], method.Body) != Il.Load(ins[start], method.Body) ||
            !Il.Call(ins[k + 5], "System.Exception", "get_Message") ||
            !Il.Number(ins[k + 6], 2) || !Il.Number(ins[k + 7], 1) ||
            !Il.Call(ins[k + 8], "PCL.ModMain", "Hint") ||
            !Il.Call(ins[k + 9], "Microsoft.VisualBasic.CompilerServices.ProjectData", "ClearProjectError") ||
            !Il.Leave(ins[k + 10]))
            throw new UnsupportedException("拖入限制分支结构与预期不符。");
        Il.CheckRange(method, start, end);
        return Change(method, () => Il.Replace(method, start, end, []));
    }
}
