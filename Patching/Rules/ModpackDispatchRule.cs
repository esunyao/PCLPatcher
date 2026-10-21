using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PCLPatcher;

// P4: Keep target-owned format detection, validation and archive disposal intact.
// Restore only the final dispatch; leave exits the outer try and runs its finally.
internal sealed class ModpackDispatchRule() : PatchRule("P4", "恢复整合包类型安装分派")
{
    private static readonly (int Kind, string Name, int Count)[] Installers = [
        (0, "InstallPackCurseForge", 5), (1, "InstallPackHMCL", 3), (2, "InstallPackMMC", 3),
        (3, "InstallPackMCBBS", 4), (4, "InstallPackModrinth", 5), (9, "InstallPackLauncherPack", 3), (-1, "InstallPackCompress", 2)
    ];
    private const string Result = "PCL.ModLoader/LoaderCombo`1<System.String>";
    protected override PatchAnalysis Analyze(ModuleDef module)
    {
        var type = Il.Type(module, "PCL.ModModpack");
        var method = Il.Method(type, "ModpackInstall", Result, "System.String", "System.String", "System.String");
        var ins = method.Body.Instructions;
        var methods = Installers.Select(entry => Il.Method(type, entry.Name, Result,
            Enumerable.Range(0, entry.Count).Select(n => n == 1 ? "System.IO.Compression.ZipArchive" : "System.String").ToArray())).ToArray();
        bool anyDispatch = methods.Any(m => Il.Calls(method, m));
        foreach (string marker in new[] { "mcbbs.packmeta", "mmc-pack.json", "modrinth.index.json", "manifest.json", "modpack.json", "modpack.zip", "modpack.mrpack" })
            if (!ins.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == marker))
                throw new UnsupportedException($"输入已删除整合包识别逻辑：{marker}。");

        // Infer locals from their producers rather than slot numbers.
        var pack = Il.One(ins.Select((i, n) => (i, n)).Where(p => Il.Number(p.i, -1) && p.n + 1 < ins.Count)
            .Select(p => Il.Store(ins[p.n + 1], method.Body)).Where(l => l?.Type.ElementType == ElementType.I4), "整合包类型局部变量");
        foreach (int kind in new[] { 0, 1, 2, 3, 4, 9 })
            if (!ins.Select((i, n) => (i, n)).Any(p => Il.Number(p.i, kind) && p.n + 1 < ins.Count && Il.Store(ins[p.n + 1], method.Body) == pack))
                throw new UnsupportedException($"缺少类型 {kind} 的识别赋值。");
        var archive = Il.One(ins.Select((i, n) => (i, n)).Where(p => Il.Call(p.i, "MeloongCore.FileUtils", "OpenZip") && p.n + 1 < ins.Count)
            .Select(p => Il.Store(ins[p.n + 1], method.Body)).Where(l => l?.Type.FullName == "System.IO.Compression.ZipArchive"), "OpenZip 结果变量");
        var folder = Il.One(ins.Select((i, n) => (i, n)).Where(p => p.i.OpCode == OpCodes.Ldstr && (string)p.i.Operand == "" && p.n + 1 < ins.Count)
            .Select(p => Il.Store(ins[p.n + 1], method.Body)).Where(l => l?.Type.ElementType == ElementType.String), "整合包基目录变量");
        if (ins.Count(i => Il.Store(i, method.Body) == folder) < 2)
            throw new UnsupportedException("整合包基目录缺少识别赋值。");
        if (anyDispatch)
        {
            VerifyDispatch(method, methods, pack, archive, folder);
            return Applied(method);
        }
        int start = Il.One(ins.Select((i, n) => (i, n)).Where(p => Il.IsRestriction(p.i)), "最终限制异常").n;
        if (start + 2 >= ins.Count || ins[start + 1].OpCode != OpCodes.Newobj ||
            ins[start + 1].Operand is not IMethod exception || exception.DeclaringType.FullName != "System.Exception" ||
            !Il.Signature(exception, "System.Void", "System.String") || ins[start + 2].OpCode != OpCodes.Throw)
            throw new UnsupportedException("最终限制异常结构不符。");
        var outer = Il.One(method.Body.ExceptionHandlers.Where(e => e.HandlerType == ExceptionHandlerType.Finally &&
            ins.IndexOf(e.TryStart) < start && ins.IndexOf(e.TryEnd) == start + 3 && e.HandlerEnd == null), "包围分派的 finally");
        if (!ins.Skip(ins.IndexOf(outer.HandlerStart)).Any(i => Il.Call(i, "System.IO.Compression.ZipArchive", "Dispose")))
            throw new UnsupportedException("缺少 ZipArchive.Dispose。");
        Il.CheckRange(method, start, start + 2);
        return Change(method, () =>
        {
            var oldThrow = ins[start + 2];
            var result = new Local(method.ReturnType);
            method.Body.Variables.Add(result);
            var exit = Instruction.Create(OpCodes.Ldloc, result);
            var blocks = methods.Select(_ => Instruction.Create(OpCodes.Ldarg_0)).ToArray();
            var instructions = new List<Instruction>();
            for (int n = 0; n < Installers.Length - 1; n++)
            {
                instructions.Add(Instruction.Create(OpCodes.Ldloc, pack));
                instructions.Add(Instruction.CreateLdcI4(Installers[n].Kind));
                instructions.Add(Instruction.Create(OpCodes.Beq, blocks[n]));
            }
            instructions.Add(Instruction.Create(OpCodes.Br, blocks[^1]));
            for (int n = 0; n < methods.Length; n++)
            {
                instructions.Add(blocks[n]);
                instructions.Add(Instruction.Create(OpCodes.Ldloc, archive));
                if (Installers[n].Count >= 3) instructions.Add(Instruction.Create(OpCodes.Ldloc, folder));
                if (Installers[n].Count >= 4) instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
                if (Installers[n].Count >= 5) instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
                instructions.Add(Instruction.Create(OpCodes.Call, methods[n]));
                instructions.Add(Instruction.Create(OpCodes.Stloc, result));
                instructions.Add(Instruction.Create(OpCodes.Leave, exit));
            }
            Il.Replace(method, start, start + 2, instructions);
            // A try must not end in fall-through nops immediately before a finally,
            // even when the preceding dispatch leaves make those nops unreachable.
            oldThrow.OpCode = OpCodes.Leave;
            oldThrow.Operand = exit;
            outer.HandlerEnd = exit;
            method.Body.Instructions.Add(exit);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        });
    }
    // Accept only the complete dispatch emitted by this rule. Checking call names
    // alone would accept a wrong kind mapping or swapped installer arguments.
    private static void VerifyDispatch(MethodDef method, MethodDef[] methods, Local pack, Local archive, Local folder)
    {
        var ins = method.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).ToArray();
        void Require(bool value)
        {
            if (!value) throw new UnsupportedException("安装分派仅部分恢复、参数或分支映射不符。");
        }
        Require(!ins.Any(Il.IsRestriction));
        var blocks = new Instruction[methods.Length];
        Local result = null;
        Instruction exit = null;
        int first = -1;
        for (int n = 0; n < methods.Length; n++)
        {
            int call = Il.One(ins.Select((i, k) => (i, k)).Where(p =>
                (p.i.OpCode == OpCodes.Call || p.i.OpCode == OpCodes.Callvirt) &&
                p.i.Operand is IMethod m && m.FullName == methods[n].FullName), Installers[n].Name + " 分派调用").k;
            int count = Installers[n].Count, start = call - count;
            Require(start >= 0 && call + 2 < ins.Length);
            blocks[n] = ins[start];
            if (n == 0) first = start;
            Require(Il.Arg(ins[start], 0) && Il.Load(ins[start + 1], method.Body) == archive);
            if (count >= 3) Require(Il.Load(ins[start + 2], method.Body) == folder);
            if (count >= 4) Require(Il.Arg(ins[start + 3], 1));
            if (count >= 5) Require(Il.Arg(ins[start + 4], 2));
            var stored = Il.Store(ins[call + 1], method.Body);
            Require(stored?.Type.FullName == Result && Il.Leave(ins[call + 2]));
            if (n == 0) { result = stored; exit = ins[call + 2].Operand as Instruction; }
            Require(stored == result && ins[call + 2].Operand == exit);
        }
        int check = first - 19;
        Require(check >= 0);
        for (int n = 0; n < methods.Length - 1; n++)
        {
            int k = check + n * 3;
            Require(Il.Load(ins[k], method.Body) == pack && Il.Number(ins[k + 1], Installers[n].Kind) &&
                (ins[k + 2].OpCode == OpCodes.Beq || ins[k + 2].OpCode == OpCodes.Beq_S) && ins[k + 2].Operand == blocks[n]);
        }
        Require((ins[first - 1].OpCode == OpCodes.Br || ins[first - 1].OpCode == OpCodes.Br_S) && ins[first - 1].Operand == blocks[^1]);
        Require(exit != null && Il.Load(exit, method.Body) == result && ins[^2] == exit && ins[^1].OpCode == OpCodes.Ret);
        Require(method.Body.ExceptionHandlers.Any(e => e.HandlerType == ExceptionHandlerType.Finally && e.HandlerEnd == exit &&
            method.Body.Instructions.Skip(method.Body.Instructions.IndexOf(e.HandlerStart))
                .Take(method.Body.Instructions.IndexOf(exit) - method.Body.Instructions.IndexOf(e.HandlerStart))
                .Any(i => Il.Call(i, "System.IO.Compression.ZipArchive", "Dispose"))));
    }

}
