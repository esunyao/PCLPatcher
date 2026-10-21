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
            Console.WriteLine($"PCLPatcher {CommandLine.ToolVersion} | 目标 PCL {module.Assembly.Version}");
            Console.WriteLine($"输入：{options.Input}");
            Console.WriteLine(module.Assembly.Version?.ToString() == "2.99.0.0"
                ? "目标版本：2.99.0.0（仍需逐项结构检查）。"
                : "目标版本：未知版本；结构兼容不代表功能已验证。");
            var analyses = Inspect(module);
            foreach (var a in analyses)
                Console.WriteLine($"[{a.Id}] {a.Description}：{StateLabel(a.State)}\n  {a.Detail}");
            if (analyses.Any(a => a.State == PatchState.Unsupported)) return 3;
            if (options.Check)
            {
                Console.WriteLine("结构检查通过。未修改文件；未执行 PCL 功能验证。");
                return 0;
            }
            bool changed = analyses.Any(a => a.State == PatchState.NeedsPatch);
            if (options.InPlace && !changed)
            {
                Verify(module);
                Console.WriteLine("六项均已应用，无需替换或新增备份。");
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
            Console.WriteLine($"完成：{options.Output}。六项结构与 IL 栈校验通过；实际功能需在 PCL 中验证。");
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
        PatchState.NeedsPatch => "需要修改", PatchState.Applied => "已应用", _ => "不支持"
    };
}
