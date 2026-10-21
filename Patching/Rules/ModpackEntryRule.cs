using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PCLPatcher;

// P3: Build the file dialog from references already in the target. The dialog's
// existing string-filter overload avoids copying OneOf/tuple IL from another exe.
internal sealed class ModpackEntryRule() : PatchRule("P3", "恢复选择整合包并后台安装")
{
    private const string FilterType = "System.Nullable`1<MeloongCore.OneOf`2<System.Collections.Generic.IEnumerable`1<System.ValueTuple`2<System.String[],System.String>>,System.String>>";
    protected override PatchAnalysis Analyze(ModuleDef module)
    {
        var type = Il.Type(module, "PCL.ModModpack");
        var method = Il.Method(type, "ModpackInstall", "System.Void");
        var install = Il.Method(type, "ModpackInstall", "PCL.ModLoader/LoaderCombo`1<System.String>", "System.String", "System.String", "System.String");
        var lambda = Il.One(type.NestedTypes.SelectMany(t => t.Methods).Where(m => !m.IsStatic && Il.Signature(m, "System.Void") && Il.Calls(m, install)), "手动安装闭包委托");
        var closure = lambda.DeclaringType;
        var ctor = Il.Method(closure, ".ctor", "System.Void");
        var field = Il.One(closure.Fields.Where(f => !f.IsStatic && f.FieldType.ElementType == ElementType.String &&
            lambda.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldfld && i.Operand is IField r && r.FullName == f.FullName)), "闭包文件路径字段");
        var runner = Il.Method(Il.Type(module, "PCL.ModBase"), "RunInThread", "System.Void", "System.Action");
        var select = Il.Reference(module, "MeloongCore.Wpf.Dialogs", "SelectFile", m => !m.MethodSig.HasThis &&
            Il.Signature(m, "System.Collections.Generic.List`1<System.String>", "System.String", "System.Boolean", "System.String", FilterType));
        var ins = method.Body.Instructions;
        var selectCalls = ins.Where(i => Il.Call(i, "MeloongCore.Wpf.Dialogs", "SelectFile")).ToArray();
        if (selectCalls.Length > 0)
        {
            bool stored = ins.Count(i => i.OpCode == OpCodes.Stfld && i.Operand is IField f && f.FullName == field.FullName) == 1;
            bool cancel = ins.Select((i, n) => (i, n)).Any(p => Il.Call(p.i, "System.String", "IsNullOrEmpty") &&
                p.n + 1 < ins.Count && (ins[p.n + 1].OpCode == OpCodes.Brtrue || ins[p.n + 1].OpCode == OpCodes.Brtrue_S) &&
                ins[p.n + 1].Operand is Instruction end && end.OpCode == OpCodes.Ret);
            if (selectCalls.Length == 1 && ((IMethod)selectCalls[0].Operand).FullName == select.FullName && stored && cancel &&
                ins.Count(i => Il.IsFunction(i, lambda)) == 1 && Il.Calls(method, runner) && !ins.Any(Il.IsRestriction))
                return Applied(method);
            throw new UnsupportedException("手动安装入口仅部分恢复或调用结构不匹配。");
        }
        if (ins.Count(Il.IsRestriction) != 1 || !ins.Any(i => Il.Call(i, "PCL.ModMain", "Hint")) ||
            ins.Any(i => i.OpCode.FlowControl == FlowControl.Branch || i.OpCode.FlowControl == FlowControl.Cond_Branch) ||
            method.Body.ExceptionHandlers.Count != 0)
            throw new UnsupportedException("手动安装入口不是已知的限制提示桩。");
        string oneOfType = ((GenericInstSig)select.MethodSig.Params[3]).GenericArguments[0].FullName;
        var conversion = Il.Reference(module, oneOfType, "op_Implicit", m => m.MethodSig.Params.Count == 1 &&
            m.MethodSig.Params[0] is GenericVar v && v.Number == 1 && !m.MethodSig.HasThis);
        var nullableCtor = Il.Reference(module, FilterType, ".ctor", m => m.MethodSig.Params.Count == 1);
        var first = Il.Reference(module, "System.Linq.Enumerable", "FirstOrDefault", m => m is MethodSpec s &&
            s.GenericInstMethodSig.GenericArguments.Count == 1 && s.GenericInstMethodSig.GenericArguments[0].ElementType == ElementType.String && m.MethodSig.Params.Count == 1);
        var empty = Il.Reference(module, "System.String", "IsNullOrEmpty", m => Il.Signature(m, "System.Boolean", "System.String"));
        var action = Il.Reference(module, "System.Action", ".ctor", m => Il.Signature(m, "System.Void", "System.Object", "System.IntPtr"));
        return Change(method, () =>
        {
            var body = new CilBody { InitLocals = true };
            var capture = new Local(new ClassSig(closure));
            var file = new Local(module.CorLibTypes.String);
            body.Variables.Add(capture);
            body.Variables.Add(file);
            var ret = Instruction.Create(OpCodes.Ret);
            Instruction[] instructions = [
                Instruction.Create(OpCodes.Newobj, ctor), Instruction.Create(OpCodes.Stloc, capture),
                Instruction.Create(OpCodes.Ldstr, "选择整合包压缩文件"), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ldnull),
                Instruction.Create(OpCodes.Ldstr, "整合包文件 (*.rar;*.zip;*.mrpack)|*.rar;*.zip;*.mrpack"),
                Instruction.Create(OpCodes.Call, conversion), Instruction.Create(OpCodes.Newobj, nullableCtor),
                Instruction.Create(OpCodes.Call, select), Instruction.Create(OpCodes.Call, first), Instruction.Create(OpCodes.Stloc, file),
                Instruction.Create(OpCodes.Ldloc, file), Instruction.Create(OpCodes.Call, empty), Instruction.Create(OpCodes.Brtrue, ret),
                Instruction.Create(OpCodes.Ldloc, capture), Instruction.Create(OpCodes.Ldloc, file), Instruction.Create(OpCodes.Stfld, field),
                Instruction.Create(OpCodes.Ldloc, capture), Instruction.Create(OpCodes.Ldftn, lambda), Instruction.Create(OpCodes.Newobj, action),
                Instruction.Create(OpCodes.Call, runner), ret
            ];
            foreach (var i in instructions) body.Instructions.Add(i);
            method.Body = body;
        });
    }
}
