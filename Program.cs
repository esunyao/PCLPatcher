using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PCLPatcher;

internal static class Program
{
    private const string ExpectedVersion = "2.99.0.0";
    private const string ExpectedAssembly = "Plain Craft Launcher 2";
    private const string RestrictionMessage = "为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……";
    private const string SelectFileTitle = "选择整合包压缩文件";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            PrintUsage();
            return args.Length == 0 ? 2 : 0;
        }

        string input = null, donorPath = null, output = null;
        bool inPlace = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input": input = NextArg(args, ref i); break;
                case "--donor": donorPath = NextArg(args, ref i); break;
                case "--output": output = NextArg(args, ref i); break;
                case "--in-place": inPlace = true; break;
                default:
                    Console.Error.WriteLine($"未知参数：{args[i]}");
                    PrintUsage();
                    return 2;
            }
        }

        if (string.IsNullOrEmpty(input) || !File.Exists(input))
        {
            Console.Error.WriteLine("必须指定存在的 --input 文件。");
            return 2;
        }

        if (string.IsNullOrEmpty(donorPath) || !File.Exists(donorPath))
        {
            Console.Error.WriteLine("必须指定 --donor（当前已补丁的同版本 exe），P3/P4 需要从它移植方法体。");
            return 2;
        }

        if (string.IsNullOrEmpty(output))
        {
            output = inPlace ? input : input + ".patched.exe";
        }

        if (!inPlace && Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("--output 与 --input 相同；如需原地覆盖请使用 --in-place。");
            return 2;
        }

        try
        {
            using var target = ModuleDefMD.Load(input);
            using var donor = ModuleDefMD.Load(donorPath);

            if (!CheckAssembly(target, "input") || !CheckAssembly(donor, "donor"))
            {
                return 3;
            }

            Console.WriteLine($"输入：{Path.GetFullPath(input)}");
            Console.WriteLine($"供体：{Path.GetFullPath(donorPath)}");
            Console.WriteLine($"版本：{target.Assembly.Version}");

            bool changed = false;
            changed |= ApplyP1(target);
            changed |= ApplyP2(target);
            changed |= ApplyP3(target, donor);
            changed |= ApplyP4(target, donor);
            changed |= ApplyP5(target);
            changed |= ApplyP6(target);

            if (!changed)
            {
                Console.WriteLine("目标已是完整补丁版，未做修改。");
                return 0;
            }

            string temp = inPlace ? input + ".tmp" : output;
            target.Write(temp);
            Console.WriteLine($"已写出临时文件：{temp}");

            if (!Verify(temp))
            {
                Console.Error.WriteLine("校验失败，未覆盖原文件。");
                if (inPlace) TryDelete(temp);
                return 5;
            }

            if (inPlace)
            {
                string backup = MakeBackupPath(input);
                File.Copy(input, backup, false);
                File.Move(temp, input, true);
                Console.WriteLine($"已原地替换：{input}");
                Console.WriteLine($"备份：{backup}");
            }
            else
            {
                Console.WriteLine($"输出：{Path.GetFullPath(output)}");
            }

            Console.WriteLine("完成：P1–P6 全部校验通过。");
            return 0;
        }
        catch (PatchException ex)
        {
            Console.Error.WriteLine("补丁失败：" + ex.Message);
            return 4;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("异常：" + ex);
            return 4;
        }
    }

    private static string NextArg(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new PatchException($"参数 {args[i]} 缺少值");
        return args[++i];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("PCLPatcher 2.99.0.0");
        Console.WriteLine("用法：");
        Console.WriteLine("  PCLPatcher --input <官方 exe> --donor <已补丁 exe> [--output <输出 exe>]");
        Console.WriteLine("  PCLPatcher --input <官方 exe> --donor <已补丁 exe> --in-place");
        Console.WriteLine();
        Console.WriteLine("说明：");
        Console.WriteLine("  --donor 必须是同版本（2.99.0.0）且已包含 P3/P4 修改的 exe；P3/P4 的方法体从它移植。");
        Console.WriteLine("  不带 --output 且不带 --in-place 时，输出为 <input>.patched.exe。");
    }

    private static bool CheckAssembly(ModuleDef module, string label)
    {
        var asm = module.Assembly;
        if (asm == null)
        {
            Console.Error.WriteLine($"{label}: 不是程序集");
            return false;
        }
        if (!string.Equals(asm.Name, ExpectedAssembly, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"{label}: 程序集名不符：{asm.Name}");
            return false;
        }
        if (asm.Version == null || asm.Version.ToString() != ExpectedVersion)
        {
            Console.Error.WriteLine($"{label}: 版本不符：{asm.Version}（需要 {ExpectedVersion}）");
            return false;
        }
        return true;
    }

    // P1: FormMain.FileDrag —— 删除 catch 中的“法律法规”提示分支
    private static bool ApplyP1(ModuleDef target)
    {
        var formMain = FindType(target, "PCL.FormMain")
            ?? throw new PatchException("P1: 找不到 PCL.FormMain");

        // FileDrag 本体只是一个调度器；真正的拖拽逻辑在它内联的闭包里。
        var candidates = target.GetTypes()
            .Where(t => t == formMain || t.FullName.StartsWith("PCL.FormMain/"))
            .SelectMany(t => t.Methods)
            .Where(m => m.HasBody)
            .ToList();

        foreach (var method in candidates)
        {
            var ins = method.Body.Instructions;
            int k = FindInstruction(ins, IsRestrictionString);
            if (k < 0) continue;

            int start = k - 2, end = k + 10;
            if (start < 0 || end >= ins.Count) continue;
            if (!IsCall(ins[start + 1], "get_Message") ||
                !IsCall(ins[start + 4], "CompareString") ||
                ins[start + 5].OpCode.FlowControl != FlowControl.Cond_Branch ||
                !IsCall(ins[start + 10], "Hint") ||
                !IsCall(ins[start + 11], "ClearProjectError") ||
                (ins[start + 12].OpCode != OpCodes.Leave && ins[start + 12].OpCode != OpCodes.Leave_S))
            {
                continue; // 例如 ShowUpdateLog 里的更新日志字符串
            }

            EnsureNoExternalBranchesInto(ins, start, end, "P1");
            for (int i = end; i >= start; i--)
                ins.RemoveAt(i);

            method.Body.KeepOldMaxStack = true;
            Console.WriteLine($"[ok] P1：已删除限制提示分支（{method.FullName}）");
            return true;
        }

        Console.WriteLine("[skip] P1：已应用");
        return false;
    }

    // P2: PageDownloadInstall.LoaderInit —— 恢复真实版本列表 loader
    private static bool ApplyP2(ModuleDef target)
    {
        var type = FindType(target, "PCL.PageDownloadInstall")
            ?? throw new PatchException("P2: 找不到 PCL.PageDownloadInstall");
        var method = type.Methods.FirstOrDefault(m => m.Name == "LoaderInit")
            ?? throw new PatchException("P2: 找不到 LoaderInit");
        var loaderField = FindType(target, "PCL.ModDownload")?.Fields.FirstOrDefault(f => f.Name == "DlClientListLoader")
            ?? throw new PatchException("P2: 找不到 ModDownload.DlClientListLoader");
        var ins = method.Body.Instructions;

        if (ins.Any(i => i.OpCode == OpCodes.Ldsfld && i.Operand is IField f && f.Name == "DlClientListLoader"))
        {
            Console.WriteLine("[skip] P2：已应用");
            return false;
        }

        int k = FindInstruction(ins, i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "关闭下载");
        if (k < 0)
            throw new PatchException("P2: 找不到“关闭下载”LoaderTask");

        int end = -1;
        for (int i = k + 1; i < ins.Count; i++)
        {
            if (ins[i].OpCode == OpCodes.Newobj &&
                ins[i].Operand is IMethodDefOrRef m &&
                m.Name == ".ctor" && m.DeclaringType?.Name == "LoaderTask`2")
            {
                end = i;
                break;
            }
        }
        if (end < 0)
            throw new PatchException("P2: 找不到 LoaderTask 构造指令");

        EnsureNoExternalBranchesInto(ins, k, end, "P2");
        for (int i = end; i >= k; i--)
            ins.RemoveAt(i);
        ins.Insert(k, Instruction.Create(OpCodes.Ldsfld, loaderField));

        method.Body.KeepOldMaxStack = true;
        Console.WriteLine("[ok] P2：LoaderInit 已改用 DlClientListLoader");
        return true;
    }

    // P3: ModModpack.ModpackInstall() —— 恢复“选择整合包文件并后台安装”入口
    private static bool ApplyP3(ModuleDef target, ModuleDef donor)
    {
        var type = FindType(target, "PCL.ModModpack")
            ?? throw new PatchException("P3: 找不到 PCL.ModModpack");
        var method = type.Methods.FirstOrDefault(m => m.Name == "ModpackInstall" && m.MethodSig?.Params.Count == 0)
            ?? throw new PatchException("P3: 找不到无参 ModpackInstall");
        var ins = method.Body.Instructions;

        if (ins.Any(i => IsCall(i, "SelectFile")))
        {
            Console.WriteLine("[skip] P3：已应用");
            return false;
        }

        var closure = FindType(target, "PCL.ModModpack/_Closure$__0-0")
            ?? throw new PatchException("P3: 找不到闭包 _Closure$__0-0");
        var closureCtor = closure.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.MethodSig.Params.Count == 0)
            ?? throw new PatchException("P3: 找不到闭包构造函数");
        var closureField = closure.Fields.FirstOrDefault(f => f.Name == "$VB$Local_File")
            ?? throw new PatchException("P3: 找不到闭包字段 $VB$Local_File");
        var lambda = closure.Methods.FirstOrDefault(m => m.Name == "_Lambda$__0")
            ?? throw new PatchException("P3: 找不到闭包 lambda");
        var modBase = FindType(target, "PCL.ModBase")
            ?? throw new PatchException("P3: 找不到 PCL.ModBase");
        var runInThread = modBase.Methods.FirstOrDefault(m => m.Name == "RunInThread" && m.MethodSig.Params.Count == 1)
            ?? throw new PatchException("P3: 找不到 ModBase.RunInThread");

        var donorType = FindType(donor, "PCL.ModModpack")
            ?? throw new PatchException("P3: donor 中找不到 PCL.ModModpack");
        var donorMethod = donorType.Methods.FirstOrDefault(m => m.Name == "ModpackInstall" && m.MethodSig?.Params.Count == 0)
            ?? throw new PatchException("P3: donor 中找不到无参 ModpackInstall");
        var donorIns = donorMethod.Body.Instructions;

        int seqStart = FindInstruction(donorIns, i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == SelectFileTitle);
        if (seqStart < 0)
            throw new PatchException("P3: donor 中找不到选择文件调用");
        int seqEnd = -1;
        for (int i = seqStart; i < donorIns.Count; i++)
        {
            if (IsCall(donorIns[i], "FirstOrDefault"))
            {
                seqEnd = i;
                break;
            }
        }
        if (seqEnd < 0)
            throw new PatchException("P3: donor 中找不到 FirstOrDefault");

        var resolver = new OperandResolver(target, donor);
        var newBody = new CilBody
        {
            InitLocals = true,
            MaxStack = 11,
            KeepOldMaxStack = true
        };
        newBody.Variables.Add(new Local(new ClassSig(closure)));
        newBody.Variables.Add(new Local(target.CorLibTypes.String));

        var ret = Instruction.Create(OpCodes.Ret);
        newBody.Instructions.Add(Instruction.Create(OpCodes.Newobj, closureCtor));
        newBody.Instructions.Add(Instruction.Create(OpCodes.Stloc_0));

        for (int i = seqStart; i <= seqEnd; i++)
        {
            var src = donorIns[i];
            var ni = Instruction.Create(OpCodes.Nop);
            ni.OpCode = src.OpCode;
            ni.Operand = resolver.ResolveOperand(src.Operand, donorMethod.Body, newBody, method, null);
            newBody.Instructions.Add(ni);
        }

        var isNullOrEmpty = resolver.ResolveDonorCall(donorMethod, "IsNullOrEmpty");
        var actionCtor = resolver.ResolveDonorNewobj(donorMethod, "System.Action");

        newBody.Instructions.Add(Instruction.Create(OpCodes.Stloc_1));
        newBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc_1));
        newBody.Instructions.Add(Instruction.Create(OpCodes.Call, isNullOrEmpty));
        newBody.Instructions.Add(Instruction.Create(OpCodes.Brtrue_S, ret));
        newBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc_0));
        newBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc_1));
        newBody.Instructions.Add(Instruction.Create(OpCodes.Stfld, closureField));
        newBody.Instructions.Add(Instruction.Create(OpCodes.Ldloc_0));
        newBody.Instructions.Add(Instruction.Create(OpCodes.Ldftn, lambda));
        newBody.Instructions.Add(Instruction.Create(OpCodes.Newobj, actionCtor));
        newBody.Instructions.Add(Instruction.Create(OpCodes.Call, runInThread));
        newBody.Instructions.Add(ret);

        method.Body = newBody;
        Console.WriteLine("[ok] P3：已恢复整合包安装入口");
        return true;
    }

    // P4: ModModpack.ModpackInstall(string,string,string) —— 移植 donor 的完整安装调度
    private static bool ApplyP4(ModuleDef target, ModuleDef donor)
    {
        var type = FindType(target, "PCL.ModModpack")
            ?? throw new PatchException("P4: 找不到 PCL.ModModpack");
        var method = type.Methods.FirstOrDefault(m => m.Name == "ModpackInstall" && m.MethodSig?.Params.Count == 3)
            ?? throw new PatchException("P4: 找不到三参 ModpackInstall");
        var ins = method.Body.Instructions;

        if (ins.Any(i => IsCall(i, "InstallPackCurseForge")))
        {
            Console.WriteLine("[skip] P4：已应用");
            return false;
        }

        var donorType = FindType(donor, "PCL.ModModpack")
            ?? throw new PatchException("P4: donor 中找不到 PCL.ModModpack");
        var donorMethod = donorType.Methods.FirstOrDefault(m => m.Name == "ModpackInstall" && m.MethodSig?.Params.Count == 3)
            ?? throw new PatchException("P4: donor 中找不到三参 ModpackInstall");
        if (!donorMethod.Body.Instructions.Any(i => IsCall(i, "InstallPackCurseForge")))
            throw new PatchException("P4: donor 中该方法也没有安装调度，无法移植");

        var resolver = new OperandResolver(target, donor);
        method.Body = resolver.CloneBody(donorMethod.Body, method);
        Console.WriteLine("[ok] P4：已移植整合包安装调度");
        return true;
    }

    // P5: PageDownloadModify.LoadInstanceFinished —— 显示 MC 版本卡片
    private static bool ApplyP5(ModuleDef target)
    {
        var type = FindType(target, "PCL.PageDownloadModify")
            ?? throw new PatchException("P5: 找不到 PCL.PageDownloadModify");
        var method = type.Methods.FirstOrDefault(m => m.Name == "LoadInstanceFinished")
            ?? throw new PatchException("P5: 找不到 LoadInstanceFinished");
        var ins = method.Body.Instructions;

        for (int i = 0; i + 2 < ins.Count; i++)
        {
            if (IsCall(ins[i], "get_CardMinecraft") &&
                (ins[i + 1].OpCode == OpCodes.Ldc_I4_2 || ins[i + 1].OpCode == OpCodes.Ldc_I4_0) &&
                IsCall(ins[i + 2], "set_Visibility"))
            {
                if (ins[i + 1].OpCode == OpCodes.Ldc_I4_0)
                {
                    Console.WriteLine("[skip] P5：已应用");
                    return false;
                }
                ins[i + 1].OpCode = OpCodes.Ldc_I4_0;
                ins[i + 1].Operand = null;
                method.Body.KeepOldMaxStack = true;
                Console.WriteLine("[ok] P5：MC 版本卡片已设为 Visible");
                return true;
            }
        }

        throw new PatchException("P5: 目标指令模式不匹配");
    }

    // P6: PageDownloadResourceDetail.InitializeStackResourceVersions —— 恢复整合包“安装”按钮
    private static bool ApplyP6(ModuleDef target)
    {
        var type = FindType(target, "PCL.PageDownloadResourceDetail")
            ?? throw new PatchException("P6: 找不到 PCL.PageDownloadResourceDetail");
        var method = type.Methods.FirstOrDefault(m => m.Name == "InitializeStackResourceVersions")
            ?? throw new PatchException("P6: 找不到 InitializeStackResourceVersions");
        var ins = method.Body.Instructions;

        if (ins.Any(i => i.OpCode == OpCodes.Ldftn && i.Operand is IMethodDefOrRef m && m.Name == "_Lambda$__R18-5"))
        {
            Console.WriteLine("[skip] P6：已应用");
            return false;
        }

        int match = -1;
        for (int i = 0; i + 10 < ins.Count; i++)
        {
            if (!IsLdarg(ins[i], 1)) continue;
            if (!IsCall(ins[i + 1], "get_Children")) continue;
            if (ins[i + 2].OpCode != OpCodes.Ldloc_S || ins[i + 2].Operand is not Local l2 || l2.Index != 7) continue;
            if (!IsLdarg(ins[i + 3], 0)) continue;
            if (!IsLdftn(ins[i + 4], "Save_Click")) continue;
            if (ins[i + 5].OpCode != OpCodes.Newobj ||
                ins[i + 5].Operand is not IMethodDefOrRef ctor ||
                ctor.Name != ".ctor" || ctor.DeclaringType?.FullName != "PCL.MyListItem/ClickEventHandler") continue;
            if (ins[i + 6].OpCode != OpCodes.Ldnull) continue;
            if (ins[i + 7].OpCode != OpCodes.Ldloc_1) continue;
            if (!IsCall(ins[i + 8], "ToListItem")) continue;
            if (!IsCall(ins[i + 9], "Add")) continue;
            if (ins[i + 10].OpCode != OpCodes.Pop) continue;
            match = i;
            break;
        }

        if (match < 0)
            throw new PatchException("P6: 目标指令模式不匹配");

        var toListItemInstr = ins[match + 8];
        var addInstr = ins[match + 9];
        var clickCtorInstr = ins[match + 5];

        var toListItemRef = (IMethodDefOrRef)toListItemInstr.Operand;
        var addRef = (IMethodDefOrRef)addInstr.Operand;
        var clickCtorRef = (IMethodDefOrRef)clickCtorInstr.Operand;

        var fieldTarget = type.Fields.FirstOrDefault(f => f.Name == "TargetResourceType")
            ?? throw new PatchException("P6: 找不到 TargetResourceType");
        var saveMethod = type.Methods.FirstOrDefault(m => m.Name == "Save_Click")
            ?? throw new PatchException("P6: 找不到 Save_Click");
        var wrapper = type.Methods.FirstOrDefault(m => m.Name == "_Lambda$__R18-5")
            ?? throw new PatchException("P6: 找不到 _Lambda$__R18-5");
        var iconCtorRef = FindIconCtor(target)
            ?? throw new PatchException("P6: 找不到 MyIconButton/ClickEventHandler 构造函数");

        var oldStart = ins[match + 3];
        var oldEnd = ins[match + 10];

        var elseStart = Instruction.Create(OpCodes.Ldarg_0);
        var addTarget = Instruction.Create(OpCodes.Callvirt, addRef);
        var popInstr = Instruction.Create(OpCodes.Pop);
        var brToAdd = Instruction.Create(OpCodes.Br_S, addTarget);
        var bneElse = Instruction.Create(OpCodes.Bne_Un_S, elseStart);

        var newInstructions = new List<Instruction>
        {
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, fieldTarget),
            Instruction.Create(OpCodes.Ldc_I4_2),
            bneElse,

            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldftn, wrapper),
            Instruction.Create(OpCodes.Newobj, clickCtorRef),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldftn, saveMethod),
            Instruction.Create(OpCodes.Newobj, iconCtorRef),
            Instruction.Create(OpCodes.Ldloc_1),
            Instruction.Create(OpCodes.Callvirt, toListItemRef),
            brToAdd,

            elseStart,
            Instruction.Create(OpCodes.Ldftn, saveMethod),
            Instruction.Create(OpCodes.Newobj, clickCtorRef),
            Instruction.Create(OpCodes.Ldnull),
            Instruction.Create(OpCodes.Ldloc_1),
            Instruction.Create(OpCodes.Callvirt, toListItemRef),

            addTarget,
            popInstr,
        };

        int startIdx = ins.IndexOf(oldStart);
        int endIdx = ins.IndexOf(oldEnd);
        if (startIdx < 0 || endIdx < startIdx)
            throw new PatchException("P6: 指令索引定位失败");
        for (int i = endIdx; i >= startIdx; i--)
            ins.RemoveAt(i);
        for (int i = 0; i < newInstructions.Count; i++)
            ins.Insert(startIdx + i, newInstructions[i]);

        method.Body.UpdateInstructionOffsets();
        method.Body.KeepOldMaxStack = true;
        Console.WriteLine("[ok] P6：资源页整合包安装按钮已恢复");
        return true;
    }

    private static IMethodDefOrRef FindIconCtor(ModuleDef module)
    {
        foreach (var t in module.GetTypes())
        {
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var i in m.Body.Instructions)
                {
                    if (i.OpCode == OpCodes.Newobj && i.Operand is MemberRef mr &&
                        mr.Name == ".ctor" && mr.DeclaringType != null &&
                        mr.DeclaringType.FullName == "PCL.MyIconButton/ClickEventHandler")
                    {
                        return mr;
                    }
                }
            }
        }

        var outerType = FindType(module, "PCL.MyIconButton");
        var delegateType = FindType(module, "PCL.MyIconButton/ClickEventHandler");
        if (outerType == null || delegateType == null) return null;

        var outerRef = new TypeRefUser(module, outerType.Namespace, outerType.Name, module);
        var nestedRef = new TypeRefUser(module, "", delegateType.Name, outerRef);
        var sig = MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.Object, module.CorLibTypes.IntPtr);
        return new MemberRefUser(module, ".ctor", sig, nestedRef);
    }

    private static bool Verify(string path)
    {
        using var module = ModuleDefMD.Load(path);
        bool all = true;

        all &= Check("P1", () =>
        {
            var closure = FindType(module, "PCL.FormMain/_Closure$__25-0");
            var lambda = closure?.Methods.FirstOrDefault(x => x.Name == "_Lambda$__0");
            if (lambda?.Body == null) return false;
            return !lambda.Body.Instructions.Any(IsRestrictionString);
        });

        all &= Check("P2", () =>
        {
            var m = FindType(module, "PCL.PageDownloadInstall")?.Methods.FirstOrDefault(x => x.Name == "LoaderInit");
            if (m?.Body == null) return false;
            return m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldsfld && i.Operand is IField f && f.Name == "DlClientListLoader");
        });

        all &= Check("P3", () =>
        {
            var m = FindType(module, "PCL.ModModpack")?.Methods.FirstOrDefault(x => x.Name == "ModpackInstall" && x.MethodSig?.Params.Count == 0);
            if (m?.Body == null) return false;
            return m.Body.Instructions.Any(i => IsCall(i, "SelectFile")) &&
                   m.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is IField f && f.Name == "$VB$Local_File");
        });

        all &= Check("P4", () =>
        {
            var m = FindType(module, "PCL.ModModpack")?.Methods.FirstOrDefault(x => x.Name == "ModpackInstall" && x.MethodSig?.Params.Count == 3);
            if (m?.Body == null) return false;
            return m.Body.Instructions.Any(i => IsCall(i, "InstallPackCurseForge")) &&
                   m.Body.Instructions.Any(i => IsCall(i, "InstallPackCompress"));
        });

        all &= Check("P5", () =>
        {
            var m = FindType(module, "PCL.PageDownloadModify")?.Methods.FirstOrDefault(x => x.Name == "LoadInstanceFinished");
            if (m?.Body == null) return false;
            for (int i = 0; i + 2 < m.Body.Instructions.Count; i++)
            {
                if (IsCall(m.Body.Instructions[i], "get_CardMinecraft") &&
                    m.Body.Instructions[i + 1].OpCode == OpCodes.Ldc_I4_0 &&
                    IsCall(m.Body.Instructions[i + 2], "set_Visibility"))
                    return true;
            }
            return false;
        });

        all &= Check("P6", () =>
        {
            var m = FindType(module, "PCL.PageDownloadResourceDetail")?.Methods.FirstOrDefault(x => x.Name == "InitializeStackResourceVersions");
            if (m?.Body == null) return false;
            return m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldftn &&
                                                i.Operand is IMethodDefOrRef im && im.Name == "_Lambda$__R18-5");
        });

        return all;
    }

    private static bool Check(string name, Func<bool> check)
    {
        bool ok;
        try { ok = check(); }
        catch { ok = false; }
        Console.WriteLine($"[verify] {name}: {(ok ? "OK" : "FAIL")}");
        return ok;
    }

    internal static TypeDef FindType(ModuleDef module, string fullName)
    {
        foreach (var t in module.GetTypes())
            if (t.FullName == fullName)
                return t;
        return null;
    }

    private static int FindInstruction(IList<Instruction> ins, Func<Instruction, bool> predicate)
    {
        for (int i = 0; i < ins.Count; i++)
            if (predicate(ins[i]))
                return i;
        return -1;
    }

    private static bool IsRestrictionString(Instruction i) =>
        i.OpCode == OpCodes.Ldstr && i.Operand is string s && s.Contains("为遵守相关法律法规");

    private static string GetMethodName(object operand) => operand switch
    {
        MethodSpec ms => ms.Method?.Name,
        IMethodDefOrRef m => m.Name,
        _ => null
    };

    private static bool IsCall(Instruction i, string name) =>
        (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
        GetMethodName(i.Operand) == name;

    private static bool IsLdftn(Instruction i, string name) =>
        i.OpCode == OpCodes.Ldftn && GetMethodName(i.Operand) == name;

    private static bool IsLdarg(Instruction i, int index) => index switch
    {
        0 => i.OpCode == OpCodes.Ldarg_0,
        1 => i.OpCode == OpCodes.Ldarg_1,
        _ => false
    };

    private static void EnsureNoExternalBranchesInto(IList<Instruction> ins, int start, int end, string rule)
    {
        var range = new HashSet<Instruction>();
        for (int i = start; i <= end; i++)
            range.Add(ins[i]);
        for (int i = 0; i < ins.Count; i++)
        {
            if (i >= start && i <= end) continue;
            if (ins[i].Operand is Instruction target && range.Contains(target))
                throw new PatchException($"{rule}: 删除区间存在外部跳入指令");
        }
    }

    private static string MakeBackupPath(string input)
    {
        string basePath = input + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string path = basePath;
        int n = 1;
        while (File.Exists(path))
            path = basePath + "-" + (n++);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

internal sealed class PatchException : Exception
{
    public PatchException(string message) : base(message) { }
}

internal sealed class OperandResolver
{
    private readonly ModuleDef target;
    private readonly ModuleDef donor;
    private readonly Importer importer;
    private readonly SigComparer comparer = new SigComparer();

    public OperandResolver(ModuleDef target, ModuleDef donor)
    {
        this.target = target;
        this.donor = donor;
        importer = new Importer(target);
    }

    public object ResolveOperand(object operand, CilBody donorBody, CilBody targetBody, MethodDef targetMethod,
        Dictionary<Instruction, Instruction> map)
    {
        switch (operand)
        {
            case null:
                return null;
            case Instruction i:
                if (map == null || !map.TryGetValue(i, out var ni))
                    throw new PatchException("未映射的分支目标");
                return ni;
            case Instruction[] targets:
                if (map == null)
                    throw new PatchException("未映射的 switch 目标");
                return targets.Select(t => map[t]).ToArray();
            case Local l:
                return targetBody.Variables[l.Index];
            case Parameter p:
                return targetMethod.Parameters[p.Index];
            case MethodSpec ms:
                return ResolveMethod(ms);
            case IMethodDefOrRef m:
                return ResolveMethod(m);
            case IField f:
                return ResolveField(f);
            case ITypeDefOrRef t:
                return ResolveType(t);
            case TypeSig s:
                return ResolveTypeSig(s);
            case MethodSig ms:
                return importer.Import(ms);
            case GenericInstMethodSig g:
                return importer.Import(g);
            case string s:
                return s;
            default:
                return operand;
        }
    }

    public IMethod ResolveDonorCall(MethodDef donorMethod, string name)
    {
        var instr = donorMethod.Body.Instructions.FirstOrDefault(i =>
            (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
            (i.Operand is MethodSpec ms ? ms.Method?.Name : (i.Operand as IMethodDefOrRef)?.Name) == name);
        if (instr == null)
            throw new PatchException($"donor 中找不到调用 {name}");
        return ResolveMethod((IMethod)instr.Operand);
    }

    public IMethod ResolveDonorNewobj(MethodDef donorMethod, string declaringFullName)
    {
        var instr = donorMethod.Body.Instructions.FirstOrDefault(i =>
            i.OpCode == OpCodes.Newobj &&
            i.Operand is IMethodDefOrRef m &&
            m.DeclaringType?.FullName == declaringFullName);
        if (instr == null)
            throw new PatchException($"donor 中找不到 {declaringFullName} 构造");
        return ResolveMethod((IMethod)instr.Operand);
    }

    public CilBody CloneBody(CilBody donorBody, MethodDef targetMethod)
    {
        var body = new CilBody
        {
            InitLocals = donorBody.InitLocals,
            MaxStack = donorBody.MaxStack,
            KeepOldMaxStack = true
        };

        foreach (var local in donorBody.Variables)
            body.Variables.Add(new Local(ResolveTypeSig(local.Type)));

        var map = new Dictionary<Instruction, Instruction>();
        foreach (var src in donorBody.Instructions)
        {
            var ni = Instruction.Create(OpCodes.Nop);
            ni.OpCode = src.OpCode;
            ni.Operand = null;
            map[src] = ni;
            body.Instructions.Add(ni);
        }

        foreach (var src in donorBody.Instructions)
            map[src].Operand = ResolveOperand(src.Operand, donorBody, body, targetMethod, map);

        foreach (var eh in donorBody.ExceptionHandlers)
        {
            body.ExceptionHandlers.Add(new ExceptionHandler(eh.HandlerType)
            {
                TryStart = MapInstruction(eh.TryStart, map),
                TryEnd = MapInstruction(eh.TryEnd, map),
                HandlerStart = MapInstruction(eh.HandlerStart, map),
                HandlerEnd = MapInstruction(eh.HandlerEnd, map),
                FilterStart = MapInstruction(eh.FilterStart, map),
                CatchType = eh.CatchType == null ? null : ResolveType(eh.CatchType)
            });
        }

        return body;
    }

    private static Instruction MapInstruction(Instruction i, Dictionary<Instruction, Instruction> map) =>
        i == null ? null : map[i];

    private IMethod ResolveMethod(IMethod method)
    {
        if (method is MethodSpec spec)
        {
            var resolved = ResolveMethod(spec.Method);
            var args = spec.GenericInstMethodSig.GenericArguments.Select(ResolveTypeSig).ToList();
            return new MethodSpecUser((IMethodDefOrRef)resolved, new GenericInstMethodSig(args));
        }

        if (method is MethodDef md && md.Module == donor)
        {
            var targetType = Program.FindType(target, md.DeclaringType.FullName)
                ?? throw new PatchException($"无法解析类型：{md.DeclaringType.FullName}");
            var targetMethod = targetType.Methods.FirstOrDefault(m =>
                m.Name == md.Name && comparer.Equals(m.MethodSig, md.MethodSig));
            if (targetMethod == null)
                throw new PatchException($"无法解析方法：{md.FullName}");
            return targetMethod;
        }

        if (method is IMethodDefOrRef mdr)
            return importer.Import(mdr);
        throw new PatchException($"无法解析方法：{method}");
    }

    private IField ResolveField(IField field)
    {
        if (field is FieldDef fd && fd.Module == donor)
        {
            var targetType = Program.FindType(target, fd.DeclaringType.FullName)
                ?? throw new PatchException($"无法解析类型：{fd.DeclaringType.FullName}");
            var targetField = targetType.Fields.FirstOrDefault(f =>
                f.Name == fd.Name && comparer.Equals(f.FieldSig, fd.FieldSig));
            if (targetField == null)
                throw new PatchException($"无法解析字段：{fd.FullName}");
            return targetField;
        }
        return importer.Import(field);
    }

    private ITypeDefOrRef ResolveType(ITypeDefOrRef type)
    {
        if (type is TypeDef td && td.Module == donor)
        {
            return Program.FindType(target, td.FullName)
                ?? throw new PatchException($"无法解析类型：{td.FullName}");
        }
        return importer.Import(type);
    }

    private TypeSig ResolveTypeSig(TypeSig sig)
    {
        switch (sig)
        {
            case GenericInstSig gis:
            {
                var genericType = ResolveType(gis.GenericType.TypeDefOrRef);
                ClassOrValueTypeSig newGeneric = gis.GenericType is ValueTypeSig
                    ? new ValueTypeSig(genericType)
                    : new ClassSig(genericType);
                var args = gis.GenericArguments.Select(ResolveTypeSig).ToList();
                return new GenericInstSig(newGeneric, args);
            }
            case ClassSig cs:
                return new ClassSig(ResolveType(cs.TypeDefOrRef));
            case ValueTypeSig vs:
                return new ValueTypeSig(ResolveType(vs.TypeDefOrRef));
            case SZArraySig sz:
                return new SZArraySig(ResolveTypeSig(sz.Next));
            default:
                return importer.Import(sig);
        }
    }
}
