using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace PCLPatcher;

internal static class Il
{
    public const string Restriction = "为遵守相关法律法规";
    public static bool IsRestriction(Instruction i) => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s.Contains(Restriction, StringComparison.Ordinal);
    public static T One<T>(IEnumerable<T> values, string description)
    {
        var matches = values.Take(2).ToArray();
        if (matches.Length != 1) throw new UnsupportedException($"{description}：需要唯一匹配，实际{(matches.Length == 0 ? "未找到" : "存在多个候选")}。");
        return matches[0];
    }
    public static TypeDef Type(ModuleDef module, string name) => One(module.GetTypes().Where(t => t.FullName == name), name);
    public static MethodDef Method(TypeDef type, string name, string result, params string[] parameters)
    {
        var method = One(type.Methods.Where(m => m.Name == name && Signature(m, result, parameters)), $"方法 {type.FullName}::{name}");
        if (!method.HasBody || method.Body.Instructions.Count == 0)
            throw new UnsupportedException($"方法体缺失：{method.FullName}");
        return method;
    }
    public static bool Signature(IMethod method, string result, params string[] parameters) =>
        method.MethodSig != null && method.MethodSig.RetType.FullName == result && method.MethodSig.Params.Select(t => t.FullName).SequenceEqual(parameters);
    public static bool Call(Instruction i, string type, string name) =>
        (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is IMethod m && m.DeclaringType.FullName == type && m.Name == name;
    public static bool Calls(MethodDef owner, MethodDef method) => owner.HasBody && owner.Body.Instructions.Any(i =>
        (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is IMethod m && m.FullName == method.FullName);
    public static bool IsFunction(Instruction i, MethodDef method) => i.OpCode == OpCodes.Ldftn && i.Operand is IMethod m && m.FullName == method.FullName;
    public static IEnumerable<IMethod> References(ModuleDef module) => module.GetTypes().SelectMany(t => t.Methods)
        .Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<IMethod>()
        .GroupBy(m => m.FullName).Select(g => g.First());
    public static IMethod Reference(ModuleDef module, string type, string name, Func<IMethod, bool> signature) =>
        One(References(module).Where(m => m.DeclaringType.FullName == type && m.Name == name && signature(m)), $"引用 {type}::{name}");
    public static Local Load(Instruction i, CilBody body) => i.OpCode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3 ? i.GetLocal(body.Variables) : null;
    public static Local Store(Instruction i, CilBody body) => i.OpCode.Code is Code.Stloc or Code.Stloc_S or Code.Stloc_0 or Code.Stloc_1 or Code.Stloc_2 or Code.Stloc_3 ? i.GetLocal(body.Variables) : null;
    public static bool Number(Instruction i, int number) => i.IsLdcI4() && i.GetLdcI4Value() == number;
    public static bool Arg(Instruction i, int index) => i.OpCode.Code switch
    {
        Code.Ldarg_0 => index == 0, Code.Ldarg_1 => index == 1, Code.Ldarg_2 => index == 2, Code.Ldarg_3 => index == 3,
        Code.Ldarg or Code.Ldarg_S => i.Operand is Parameter p && p.Index == index,
        _ => false
    };
    public static bool Leave(Instruction i) => i.OpCode == OpCodes.Leave || i.OpCode == OpCodes.Leave_S;
    public static IEnumerable<Instruction> Targets(Instruction i) => i.Operand switch
    {
        Instruction target => [target], IList<Instruction> targets => targets, _ => []
    };

    // Preserve the first instruction as a branch/EH anchor. Interior entry points
    // would make replacement unsafe, including switch targets and EH boundaries.
    public static void CheckRange(MethodDef method, int start, int end)
    {
        var body = method.Body;
        var inside = body.Instructions.Skip(start + 1).Take(end - start).ToHashSet();
        foreach (var i in body.Instructions.Where((_, n) => n < start || n > end))
            if (Targets(i).Any(inside.Contains)) throw new UnsupportedException("修改区间内部存在外部跳入（含 switch）。");
        foreach (var eh in body.ExceptionHandlers)
            if (new[] { eh.TryStart, eh.TryEnd, eh.HandlerStart, eh.HandlerEnd, eh.FilterStart }.Any(i => i != null && inside.Contains(i)))
                throw new UnsupportedException("修改区间内部存在异常处理边界。");
    }
    public static void Replace(MethodDef method, int start, int end, IEnumerable<Instruction> replacement)
    {
        CheckRange(method, start, end);
        for (int n = start; n <= end; n++)
        {
            method.Body.Instructions[n].OpCode = OpCodes.Nop;
            method.Body.Instructions[n].Operand = null;
        }
        int index = start + 1;
        foreach (var i in replacement) method.Body.Instructions.Insert(index++, i);
    }
    public static void Prepare(MethodDef method)
    {
        method.Body.SimplifyBranches();
        method.Body.OptimizeBranches();
        Validate(method);
        method.Body.KeepOldMaxStack = false;
    }
    public static void Validate(MethodDef method)
    {
        if (!method.HasBody) throw new VerificationException($"无方法体：{method.FullName}");
        var body = method.Body;
        var positions = body.Instructions.Select((i, n) => (i, n)).ToDictionary(p => p.i, p => p.n);
        foreach (var i in body.Instructions)
            if (Targets(i).Any(t => !positions.ContainsKey(t))) throw new VerificationException($"悬空分支：{method.FullName}");
        int Position(Instruction i, bool allowEnd) => i == null && allowEnd ? positions.Count :
            i != null && positions.TryGetValue(i, out int n) ? n : throw new VerificationException($"异常边界无效：{method.FullName}");
        foreach (var eh in body.ExceptionHandlers)
        {
            int ts = Position(eh.TryStart, false), te = Position(eh.TryEnd, true);
            int hs = Position(eh.HandlerStart, false), he = Position(eh.HandlerEnd, true);
            if (ts >= te || hs >= he || (eh.HandlerType == ExceptionHandlerType.Filter && Position(eh.FilterStart, false) >= hs))
                throw new VerificationException($"异常处理范围无效：{method.FullName}");
            if (hs > 0 && body.Instructions[hs - 1].OpCode.FlowControl is not (FlowControl.Branch or FlowControl.Return or FlowControl.Throw))
                throw new VerificationException($"异常处理入口前存在非法顺序落入：{method.FullName}");
        }
        if (!MaxStackCalculator.GetMaxStack(body.Instructions, body.ExceptionHandlers, out uint max) || max > ushort.MaxValue)
            throw new VerificationException($"IL 栈计算失败：{method.FullName}");
    }
}
