using System.Security.Cryptography;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using PCLPatcher;

namespace PCLPatcher.Tests;

internal static class TestProgram
{
    private static int count;
    internal static void Assert(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
    private static void Test(string name, Action run)
    {
        run(); count++;
        Console.WriteLine($"PASS {name}");
    }
    private static byte[] Hash(string path) => SHA256.HashData(File.ReadAllBytes(path));
    private static T Throws<T>(Action run) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static ModuleDefMD Load(string path) => ModuleDefMD.Load(File.ReadAllBytes(path));
    private static void Patch(ModuleDef module)
    {
        var all = PatchRunner.Inspect(module);
        Assert(all.All(a => a.State != PatchState.Unsupported), string.Join("; ", all.Where(a => a.State == PatchState.Unsupported).Select(a => a.Id + ":" + a.Detail)));
        foreach (var a in all.Where(a => a.State == PatchState.NeedsPatch)) { a.Edit(); Il.Prepare(a.Method); }
        PatchRunner.Verify(module);
    }

    public static int Main(string[] args)
    {
        try
        {
            Test("CLI missing value and migration diagnostics", () =>
            {
                Assert(PCLPatcher.Program.Main(["--input"]) == 2, "missing value exit");
                Assert(PCLPatcher.Program.Main(["--input", "--check"]) == 2, "option consumed as path");
                Assert(PCLPatcher.Program.Main(["--donor", "old.exe"]) == 2, "donor exit");
                Assert(PCLPatcher.Program.Main(["--unknown"]) == 2, "unknown exit");
                Assert(PCLPatcher.Program.Main(["--version"]) == 0, "version exit");
            });
            Test("Unsafe switch and exception-boundary edits rejected", () =>
            {
                var method = new MethodDefUser("M", MethodSig.CreateStatic(new CorLibTypes(new ModuleDefUser("m")).Void)) { Body = new CilBody() };
                var ins = method.Body.Instructions;
                ins.Add(Instruction.Create(OpCodes.Nop)); ins.Add(Instruction.Create(OpCodes.Nop)); ins.Add(Instruction.Create(OpCodes.Ret));
                ins.Insert(0, Instruction.Create(OpCodes.Switch, new[] { ins[1] }));
                Throws<UnsupportedException>(() => Il.CheckRange(method, 1, 2));
                ins[0].OpCode = OpCodes.Nop; ins[0].Operand = null;
                method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) { TryStart = ins[2] });
                Throws<UnsupportedException>(() => Il.CheckRange(method, 1, 2));
            });
            Test("Invalid IL stack and dangling targets rejected", () =>
            {
                var module = new ModuleDefUser("m");
                var method = new MethodDefUser("M", MethodSig.CreateStatic(module.CorLibTypes.Void)) { Body = new CilBody() };
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                Throws<VerificationException>(() => Il.Validate(method));
                method.Body.Instructions[0] = Instruction.Create(OpCodes.Br, Instruction.Create(OpCodes.Ret));
                Throws<VerificationException>(() => Il.Validate(method));
            });
            string work = Path.Combine(Path.GetTempPath(), "PCLPatcher-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                Test("Atomic write failure, verification failure and race leave files intact", () =>
                {
                    string input = Path.Combine(work, "事务测试 input.bin"), output = Path.Combine(work, "output.bin");
                    byte[] bytes = [1, 2, 3]; File.WriteAllBytes(input, bytes);
                    var opts = new Options(input, output, false, false, false, false);
                    Throws<IOException>(() => AtomicOutput.Write(opts, bytes, _ => throw new IOException("injected write failure"), _ => { }));
                    Throws<VerificationException>(() => AtomicOutput.Write(opts, bytes, s => s.Write(bytes), _ => throw new Exception("injected verification failure")));
                    Assert(!File.Exists(output) && File.ReadAllBytes(input).SequenceEqual(bytes), "failure damaged files");
                    Throws<IOException>(() => AtomicOutput.Write(opts, bytes, s => s.Write(bytes), _ => File.WriteAllText(output, "competitor")));
                    Assert(File.ReadAllText(output) == "competitor", "overwrote racing output");
                    Assert(!Directory.GetFiles(work, "*.tmp").Any(), "temporary file leak");
                    File.Delete(output);
                    Throws<IOException>(() => AtomicOutput.Write(opts, bytes, s => s.Write(bytes), _ => File.WriteAllBytes(input, [9])));
                    Assert(!File.Exists(output) && File.ReadAllBytes(input).SequenceEqual(new byte[] { 9 }), "input race overwritten");
                });
                if (args.Length == 0)
                    Console.WriteLine("SKIP real PCL regression: supply an original EXE path to enable it.");
                else Integration(Path.GetFullPath(args[0]), work);
            }
            finally { Directory.Delete(work, true); }
            Console.WriteLine($"PASS {count} test groups");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Integration(string original, string work)
    {
        byte[] originalHash = Hash(original);
        Test("Read-only check, naming, conflicts and invalid input", () =>
        {
            Assert(PCLPatcher.Program.Main(["--input", original, "--check"]) == 0, "check exit");
            var opts = CommandLine.Parse(["--input", original]);
            Assert(opts.Output.EndsWith(Path.GetFileNameWithoutExtension(original) + ".patched.exe"), "default output");
            Throws<UsageException>(() => CommandLine.Parse(["--input", original, "--in-place", "--output", "other.exe"]));
            Throws<UsageException>(() => CommandLine.Parse(["--input", original, "--check", "--in-place"]));
            Throws<UsageException>(() => CommandLine.Parse(["--input", original, "--output", original]));
            string invalid = Path.Combine(work, "invalid.exe"); File.WriteAllText(invalid, "not an assembly");
            Assert(PCLPatcher.Program.Main(["--input", invalid, "--check"]) == 3, "invalid input exit");
        });
        string patched = Path.Combine(work, "中文 空格.patched.exe");
        Test("Six rules patch without donor; output verification and idempotence", () =>
        {
            Assert(PCLPatcher.Program.Main(["--input", original, "--output", patched]) == 0, "patch exit");
            using var module = Load(patched); PatchRunner.Verify(module);
            byte[] before = Hash(patched);
            Assert(PCLPatcher.Program.Main(["--input", patched, "--in-place"]) == 0 && before.SequenceEqual(Hash(patched)), "idempotence");
            Assert(!Directory.GetFiles(work, "*.bak-*").Any(), "unneeded backup");
            string copy = Path.Combine(work, "already-patched-copy.exe");
            Assert(PCLPatcher.Program.Main(["--input", patched, "--output", copy]) == 0 && before.SequenceEqual(Hash(copy)), "no output for patched input");
            Assert(PCLPatcher.Program.Main(["--input", original, "--output", patched]) == 2 && before.SequenceEqual(Hash(patched)), "existing output overwritten");
        });
        Test("In-place transaction and backup restoration", () =>
        {
            string input = Path.Combine(work, "原地 input.exe"); File.Copy(original, input);
            Assert(PCLPatcher.Program.Main(["--input", input, "--in-place"]) == 0, "in-place exit");
            string backup = Directory.GetFiles(work, "原地 input.exe.bak-*").Single();
            Assert(Hash(backup).SequenceEqual(originalHash), "backup bytes");
            File.Copy(backup, input, true);
            Assert(Hash(input).SequenceEqual(originalHash), "restore bytes");
        });
        Test("Partial patch repaired and original P4 recognition instructions preserved", () =>
        {
            using var module = Load(original);
            var p5 = new MinecraftCardRule().Inspect(module); p5.Edit();
            Assert(PatchRunner.Inspect(module).Single(a => a.Id == "P5").State == PatchState.Applied, "partial state");
            var p4 = new ModpackDispatchRule().Inspect(module);
            int end = p4.Method.Body.Instructions.ToList().FindIndex(Il.IsRestriction);
            var before = p4.Method.Body.Instructions.Take(end).Select(i => (i.OpCode, i.Operand)).ToArray();
            Patch(module);
            Assert(before.SequenceEqual(p4.Method.Body.Instructions.Take(end).Select(i => (i.OpCode, i.Operand))), "P4 changed recognition code");
        });
        Test("Unknown version, renamed closures and reordered locals remain structurally compatible", () =>
        {
            using var module = Load(original);
            module.Assembly.Version = new Version(3, 0, 0, 0);
            foreach (var type in module.GetTypes().Where(t => t.Name.String.StartsWith("_Closure", StringComparison.Ordinal))) type.Name = type.Name + "_renamed";
            foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
            {
                method.Body.SimplifyMacros(method.Parameters);
                var locals = method.Body.Variables.ToArray();
                method.Body.Variables.Clear();
                foreach (var local in locals.Reverse()) method.Body.Variables.Add(local);
            }
            Patch(module);
        });
        Test("Missing methods, signature drift, ambiguity and incomplete dispatch rejected", () =>
        {
            using (var module = Load(original))
            {
                var type = Il.Type(module, "PCL.ModModpack");
                type.Methods.Remove(type.Methods.Single(m => m.Name == "InstallPackCompress"));
                Assert(new ModpackDispatchRule().Inspect(module).State == PatchState.Unsupported, "missing installer");
            }
            using (var module = Load(original))
            {
                var m = Il.Type(module, "PCL.ModModpack").Methods.Single(m => m.Name == "InstallPackCurseForge");
                m.MethodSig.Params[0] = module.CorLibTypes.Int32;
                Assert(new ModpackDispatchRule().Inspect(module).State == PatchState.Unsupported, "signature drift");
            }
            using (var module = Load(original))
            {
                var type = Il.Type(module, "PCL.ModModpack");
                var old = type.Methods.Single(m => m.Name == "InstallPackCurseForge");
                type.Methods.Add(new MethodDefUser(old.Name, old.MethodSig, old.ImplAttributes, old.Attributes) { Body = new CilBody() });
                Assert(new ModpackDispatchRule().Inspect(module).State == PatchState.Unsupported, "ambiguous method");
                string bad = Path.Combine(work, "unsupported.exe"), output = Path.Combine(work, "should-not-exist.exe");
                module.Write(bad); byte[] hash = Hash(bad);
                Assert(PCLPatcher.Program.Main(["--input", bad, "--output", output]) == 3 && !File.Exists(output) && hash.SequenceEqual(Hash(bad)), "unsupported wrote files");
            }
            using (var module = Load(patched))
            {
                var m = Il.Type(module, "PCL.ModModpack").Methods.Single(m => m.Name == "ModpackInstall" && m.MethodSig.Params.Count == 3);
                var call = m.Body.Instructions.Single(i => Il.Call(i, "PCL.ModModpack", "InstallPackCurseForge"));
                call.OpCode = OpCodes.Nop; call.Operand = null;
                Assert(new ModpackDispatchRule().Inspect(module).State == PatchState.Unsupported, "incomplete dispatch accepted");
            }
            using (var module = Load(original))
            {
                var p1 = new DragDropRule().Inspect(module);
                var i = p1.Method.Body.Instructions.Single(Il.IsRestriction); i.Operand = "unrecognized restriction";
                // Without the restriction there must still be a valid complete method;
                // weak 'string disappeared' checks must not accept the broken catch.
                Assert(new DragDropRule().Inspect(module).State == PatchState.Unsupported, "P1 false applied");
            }
        });
        Test("Execute extracted P3 cancellation/selection and P4 format/error/finally paths", () => RuntimeRegression.Run(original, work));
        Assert(Hash(original).SequenceEqual(originalHash), "original fixture changed");
    }
}
