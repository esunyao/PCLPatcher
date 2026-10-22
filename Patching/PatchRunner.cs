using dnlib.DotNet;

namespace PCLPatcher;

internal static class PatchRunner
{
    public static PatchRule[] Rules() => [new DragDropRule(), new DownloadLoaderRule(), new ModpackEntryRule(), new ModpackDispatchRule(), new MinecraftCardRule(), new ResourceInstallRule()];
    public static PatchAnalysis[] Inspect(ModuleDef module) => Rules().Select(rule => rule.Inspect(module)).ToArray();
    public static int Run(Options options)
    {
        byte[] original = File.ReadAllBytes(options.Input);
        ModuleDefMD module;
        try { module = ModuleDefMD.Load(original); }
        catch (BadImageFormatException) { throw new UnsupportedException("输入不是可读取的 .NET 程序集。"); }
        using (module)
        {
            if (module.Assembly?.Name != "Plain Craft Launcher 2") throw new UnsupportedException("程序集名必须为 Plain Craft Launcher 2。");
            bool testedVersion = module.Assembly.Version?.ToString() == "2.99.0.0";
            ConsoleOutput.WriteLine($"PCLPatcher {CommandLine.ToolVersion}");
            ConsoleOutput.WriteLine("目标版本：2.99.0.0（已经过测试）", ConsoleColor.Green);
            ConsoleOutput.WriteLine($"检测到的版本：{module.Assembly.Version}", testedVersion ? null : ConsoleColor.Yellow);
            if (!testedVersion)
                ConsoleOutput.WriteLine("未知版本：结构兼容，功能未经验证；仍需通过六项结构检查。", ConsoleColor.Yellow);
            ConsoleOutput.WriteLine($"输入：{options.Input}");
            var analyses = Inspect(module);
            foreach (var a in analyses)
            {
                var color = a.State switch
                {
                    PatchState.NeedsPatch => ConsoleColor.Yellow,
                    PatchState.Applied => ConsoleColor.Green,
                    _ => ConsoleColor.Red
                };
                ConsoleOutput.Write($"[{a.Id}] {a.Description}：");
                ConsoleOutput.WriteLine(StateLabel(a.State), color);
                ConsoleOutput.WriteLine($"  {a.Detail}", a.State == PatchState.Unsupported ? ConsoleColor.Red : ConsoleColor.DarkGray);
            }
            if (analyses.Any(a => a.State == PatchState.Unsupported)) return 3;
            if (options.Check)
            {
                int pending = analyses.Count(a => a.State == PatchState.NeedsPatch);
                ConsoleOutput.WriteLine(pending == 0
                    ? "检查通过：六项均已打补丁。本次仅检查，未修改文件。"
                    : $"检查通过：有 {pending} 项补丁可应用。本次仅检查，未修改文件。", ConsoleColor.Green);
                if (pending > 0)
                    ConsoleOutput.WriteLine("如需打补丁，请去掉命令中的 --check 后重新运行。", ConsoleColor.Yellow);
                ConsoleOutput.WriteLine("未执行 PCL 功能验证。", ConsoleColor.DarkGray);
                return 0;
            }
            bool changed = analyses.Any(a => a.State == PatchState.NeedsPatch);
            if (options.InPlace && !changed)
            {
                Verify(module);
                ConsoleOutput.WriteLine("六项均已打补丁，无需替换或新增备份。", ConsoleColor.Green);
                return 0;
            }
            foreach (var a in analyses.Where(a => a.State == PatchState.NeedsPatch))
            {
                a.Edit();
                Il.Prepare(a.Method);
            }
            AtomicOutput.Write(options, original, stream =>
            {
                if (changed) module.Write(stream);
                else stream.Write(original);
            }, path => { using var saved = ModuleDefMD.Load(File.ReadAllBytes(path)); Verify(saved); });
            ConsoleOutput.WriteLine($"完成：{options.Output}。六项结构与 IL 栈校验通过；实际功能需在 PCL 中验证。", ConsoleColor.Green);
            return 0;
        }
    }
    public static void Verify(ModuleDef module)
    {
        foreach (var a in Inspect(module))
        {
            if (a.State != PatchState.Applied) throw new VerificationException($"{a.Id} 输出校验失败：{a.Detail}");
            Il.Validate(a.Method);
        }
    }
    private static string StateLabel(PatchState state) => state switch
    {
        PatchState.NeedsPatch => "可应用（尚未打补丁）", PatchState.Applied => "已打补丁", _ => "不支持此文件"
    };
}
