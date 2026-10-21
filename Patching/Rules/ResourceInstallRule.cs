using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PCLPatcher;

// P6: Discover the install adapter by the method it calls. Match local operands by
// type, keeping their actual slot; unrelated resource rows keep Save_Click.
internal sealed class ResourceInstallRule() : PatchRule("P6", "恢复资源页整合包安装按钮")
{
    protected override PatchAnalysis Analyze(ModuleDef module)
    {
        var type = Il.Type(module, "PCL.PageDownloadResourceDetail");
        var method = Il.Method(type, "InitializeStackResourceVersions", "System.Void", "System.Windows.Controls.StackPanel");
        var save = Il.Method(type, "Save_Click", "System.Void", "System.Object", "System.EventArgs");
        var install = Il.Method(type, "Install_Click", "System.Void", "PCL.MyListItem", "System.EventArgs");
        var wrapper = Il.One(type.Methods.Where(m => !m.IsStatic && Il.Signature(m, "System.Void", "System.Object", "System.Windows.Input.MouseButtonEventArgs") &&
            Il.Calls(m, install)), "调用 Install_Click 的事件适配方法");
        var field = Il.One(type.Fields.Where(f => f.Name == "TargetResourceType" && !f.IsStatic && f.FieldType.FullName == "PCLCS.ResourceTypes"), "资源类型字段");
        var ins = method.Body.Instructions;
        if (ins.Any(i => Il.IsFunction(i, wrapper)))
        {
            var active = ins.Where(i => i.OpCode != OpCodes.Nop).ToArray();
            int k = Il.One(active.Select((i, n) => (i, n)).Where(p => p.n + 4 < active.Length &&
                p.i.OpCode == OpCodes.Ldfld && p.i.Operand is IField f && f.FullName == field.FullName &&
                Il.IsFunction(active[p.n + 4], wrapper)), "安装按钮的资源类型判断").n;
            if (k + 19 >= active.Length || !Il.Number(active[k + 1], 2) ||
                (active[k + 2].OpCode != OpCodes.Bne_Un && active[k + 2].OpCode != OpCodes.Bne_Un_S) ||
                !Il.IsFunction(active[k + 4], wrapper) || !Il.IsFunction(active[k + 7], save) ||
                !Il.Call(active[k + 10], "PCL.ResourceVersion", "ToListItem") ||
                !Il.IsFunction(active[k + 13], save) || !Il.Call(active[k + 17], "PCL.ResourceVersion", "ToListItem") ||
                active[k + 2].Operand != active[k + 12] || active[k + 11].Operand != active[k + 18])
                throw new UnsupportedException("安装按钮分支仅部分恢复或分支目标不匹配。");
            return Applied(method);
        }
        int match = Il.One(ins.Select((i, n) => (i, n)).Where(p =>
        {
            int n = p.n;
            return n + 10 < ins.Count && Il.Arg(ins[n], 1) && Il.Call(ins[n + 1], "System.Windows.Controls.Panel", "get_Children") &&
                Il.Load(ins[n + 2], method.Body)?.Type.FullName == "PCL.ResourceVersion" && Il.Arg(ins[n + 3], 0) && Il.IsFunction(ins[n + 4], save) &&
                ins[n + 5].OpCode == OpCodes.Newobj && ins[n + 5].Operand is IMethod c && c.DeclaringType.FullName == "PCL.MyListItem/ClickEventHandler" &&
                ins[n + 6].OpCode == OpCodes.Ldnull && Il.Load(ins[n + 7], method.Body)?.Type.ElementType == ElementType.Boolean &&
                Il.Call(ins[n + 8], "PCL.ResourceVersion", "ToListItem") &&
                Il.Signature((IMethod)ins[n + 8].Operand, "PCL.MyVirtualizingElement`1<PCL.MyListItem>", "PCL.MyListItem/ClickEventHandler", "PCL.MyIconButton/ClickEventHandler", "System.Boolean") &&
                Il.Call(ins[n + 9], "System.Windows.Controls.UIElementCollection", "Add") && ins[n + 10].OpCode == OpCodes.Pop;
        }), "资源列表的保存按钮构造").n;
        var listCtor = (IMethod)ins[match + 5].Operand;
        var iconCtor = Il.Reference(module, "PCL.MyIconButton/ClickEventHandler", ".ctor", m => Il.Signature(m, "System.Void", "System.Object", "System.IntPtr"));
        var toList = (IMethod)ins[match + 8].Operand;
        var add = (IMethod)ins[match + 9].Operand;
        var flag = Il.Load(ins[match + 7], method.Body);
        Il.CheckRange(method, match + 3, match + 10);
        return Change(method, () =>
        {
            var otherwise = Instruction.Create(OpCodes.Ldarg_0);
            var addInstruction = Instruction.Create(OpCodes.Callvirt, add);
            Instruction[] replacement = [
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, field), Instruction.Create(OpCodes.Ldc_I4_2),
                Instruction.Create(OpCodes.Bne_Un, otherwise),
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldftn, wrapper), Instruction.Create(OpCodes.Newobj, listCtor),
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldftn, save), Instruction.Create(OpCodes.Newobj, iconCtor),
                Instruction.Create(OpCodes.Ldloc, flag), Instruction.Create(OpCodes.Callvirt, toList), Instruction.Create(OpCodes.Br, addInstruction),
                otherwise, Instruction.Create(OpCodes.Ldftn, save), Instruction.Create(OpCodes.Newobj, listCtor), Instruction.Create(OpCodes.Ldnull),
                Instruction.Create(OpCodes.Ldloc, flag), Instruction.Create(OpCodes.Callvirt, toList), addInstruction, Instruction.Create(OpCodes.Pop)
            ];
            Il.Replace(method, match + 3, match + 10, replacement);
        });
    }
}
