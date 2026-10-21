using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace PCLPatcher.Tests;

// Execute the target's patched P4 IL in an isolated copy of this test assembly.
// Only external UI/logging/install calls are replaced. Detection and EH instructions
// are copied verbatim; real ZipArchive and JSON parsing exercise its control flow.
internal static class RuntimeRegression
{
    public static void Run(string original, string work)
    {
        using var source = ModuleDefMD.Load(File.ReadAllBytes(original));
        var p4 = new ModpackDispatchRule().Inspect(source); p4.Edit(); Il.Prepare(p4.Method);
        using var host = ModuleDefMD.Load(typeof(RuntimeRegression).Assembly.Location);
        var hostType = Il.Type(host, typeof(RuntimeHost).FullName!);
        var patched = new MethodDefUser("PatchedDispatch", MethodSig.CreateStatic(host.CorLibTypes.String,
            host.CorLibTypes.String, host.CorLibTypes.String, host.CorLibTypes.String),
            dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static);
        hostType.Methods.Add(patched);
        patched.Body = CopyBody(p4.Method, patched, host);
        Il.Prepare(patched);
        EntryRegression.Add(source, host);
        using var stream = new MemoryStream(); host.Write(stream);
        if (Environment.GetEnvironmentVariable("PCLPATCHER_PROBE_OUTPUT") is string probePath)
            File.WriteAllBytes(probePath, stream.ToArray());
        var context = new AssemblyLoadContext("PCL-dispatch-regression-" + Guid.NewGuid(), true);
        try
        {
            stream.Position = 0;
            var assembly = context.LoadFromStream(stream);
            var type = assembly.GetType(typeof(RuntimeHost).FullName!)!;
            var entry = type.GetMethod("PatchedDispatch")!;
            string Execute(string path, string instance = "Test instance", string logo = "logo.png")
            {
                try { return (string)entry.Invoke(null, [path, instance, logo])!; }
                catch (TargetInvocationException ex) { throw ex.InnerException!; }
            }
            string Zip(string name, params (string Name, string Text)[] entries)
            {
                string path = Path.Combine(work, name + ".zip");
                using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
                foreach (var item in entries)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(item.Name).Open()); writer.Write(item.Text);
                }
                return path;
            }
            void AssertDisposed()
            {
                var archive = (ZipArchive)type.GetField(nameof(RuntimeHost.Archive))!.GetValue(null)!;
                bool disposed = false;
                try { _ = archive.Entries; } catch (ObjectDisposedException) { disposed = true; }
                TestProgram.Assert(disposed, "P4 did not dispose archive");
            }
            (string Marker, string Json, string Installer)[] cases = [
                ("manifest.json", "{}", "CurseForge"), ("manifest.json", "{\"addons\":[]}", "MCBBS"),
                ("mcbbs.packmeta", "{}", "MCBBS"), ("mmc-pack.json", "{}", "MMC"),
                ("modrinth.index.json", "{}", "Modrinth"), ("modpack.json", "{}", "HMCL"),
                ("modpack.zip", "", "LauncherPack"), ("modpack.mrpack", "", "LauncherPack")
            ];
            int n = 0;
            foreach (var c in cases)
            foreach (string prefix in new[] { "", "nested/" })
            {
                string path = Zip("format-" + n++, (prefix + c.Marker, c.Json));
                string folder = prefix != "" && c.Marker == "manifest.json" && c.Installer == "MCBBS" ? "overrides/" : prefix;
                string result = Execute(path);
                TestProgram.Assert(result == c.Installer + "|" + folder +
                    (c.Installer is "CurseForge" or "Modrinth" ? "|Test instance|logo.png" : c.Installer == "MCBBS" ? "|Test instance" : ""), "Wrong dispatch: " + result);
                AssertDisposed();
            }
            TestProgram.Assert(Execute(Zip("unknown", ("readme.txt", ""))) == "Compress", "unknown zip fallback"); AssertDisposed();
            TestProgram.Assert(Execute(Zip("precedence", ("mcbbs.packmeta", "{}"), ("manifest.json", "{}"))).StartsWith("MCBBS|"), "root precedence"); AssertDisposed();
            foreach (string bad in new[] { Zip("invalid-json", ("manifest.json", "{")), Path.Combine(work, "broken.zip"), Path.Combine(work, "broken.rar") })
            {
                if (!File.Exists(bad)) File.WriteAllText(bad, "broken");
                try { Execute(bad); throw new Exception("Bad archive accepted"); }
                catch (Exception ex) when (ex.Message.Contains("打开整合包文件失败") || ex.Message.Contains("rar 格式")) { }
            }
            string valid = Zip("blocked", ("modpack.json", "{}"));
            foreach (string folder in new[] { "C:\\bad!\\", "C:\\bad;\\" })
            {
                type.GetField(nameof(RuntimeHost.GameFolder))!.SetValue(null, folder);
                bool canceled = false; try { Execute(valid); } catch (OperationCanceledException) { canceled = true; }
                TestProgram.Assert(canceled, "path validation bypassed");
            }
            type.GetField(nameof(RuntimeHost.GameFolder))!.SetValue(null, "C:\\game\\");
            type.GetField(nameof(RuntimeHost.FailInstall))!.SetValue(null, true);
            try { Execute(valid); throw new Exception("installer failure swallowed"); }
            catch (InvalidOperationException ex) when (ex.Message == "installer failed") { }
            AssertDisposed();
            EntryRegression.Execute(assembly);
            Console.WriteLine("  Executed original P4 IL: 16 format cases, fallback, precedence, invalid JSON/zip/rar, forbidden paths, installer failure and finally.");
        }
        finally { context.Unload(); }
    }

    private static CilBody CopyBody(MethodDef source, MethodDef target, ModuleDef host)
    {
        var importer = new Importer(host);
        var hostType = Il.Type(host, typeof(RuntimeHost).FullName!);
        TypeSig Type(TypeSig t) => t.FullName == "PCL.ModLoader/LoaderCombo`1<System.String>" ? host.CorLibTypes.String : importer.Import(t);
        var body = new CilBody { InitLocals = true };
        foreach (var local in source.Body.Variables) body.Variables.Add(new Local(Type(local.Type)));
        var map = source.Body.Instructions.ToDictionary(i => i, i => new Instruction(i.OpCode));
        foreach (var i in source.Body.Instructions)
        {
            object operand = i.Operand;
            if (operand is Instruction branch) operand = map[branch];
            else if (operand is IList<Instruction> branches) operand = branches.Select(b => map[b]).ToArray();
            else if (operand is Local local) operand = body.Variables[local.Index];
            else if (operand is Parameter p) operand = target.Parameters[p.Index];
            else if (operand is ITypeDefOrRef type)
                operand = type.FullName == "Newtonsoft.Json.Linq.JObject" ? Il.Type(host, typeof(TestManifest).FullName!) : importer.Import(type);
            else if (operand is IMethod method)
            {
                string owner = method.DeclaringType.FullName, name = method.Name;
                string substitute = owner switch
                {
                    "MeloongCore.Logger" => nameof(RuntimeHost.Info),
                    "PCL.ModMinecraft" => nameof(RuntimeHost.Folder),
                    "PCL.ModMain" => nameof(RuntimeHost.Hint),
                    "MeloongCore.FileUtils" => nameof(RuntimeHost.OpenZip),
                    "MeloongCore.PathUtils" => nameof(RuntimeHost.Extension),
                    "MeloongCore.Extensions.ExceptionExtensions" => nameof(RuntimeHost.Display),
                    "MeloongCore.Extensions.StringExtensions" => name,
                    "Microsoft.VisualBasic.CompilerServices.ProjectData" => nameof(RuntimeHost.ProjectError),
                    "Microsoft.VisualBasic.CompilerServices.Operators" => nameof(RuntimeHost.CompareString),
                    "PCL.ModModpack" => name,
                    _ => null
                };
                if (owner == "Newtonsoft.Json.Linq.JObject")
                    operand = Il.Type(host, typeof(TestManifest).FullName!).Methods.Single(m => m.Name == "get_Item");
                else operand = substitute == null ? importer.Import(method) : hostType.Methods.Single(m => m.Name == substitute);
            }
            map[i].Operand = operand;
            body.Instructions.Add(map[i]);
        }
        Instruction Map(Instruction i) => i == null ? null : map[i];
        foreach (var e in source.Body.ExceptionHandlers)
            body.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType)
            {
                TryStart = Map(e.TryStart), TryEnd = Map(e.TryEnd), HandlerStart = Map(e.HandlerStart), HandlerEnd = Map(e.HandlerEnd),
                FilterStart = Map(e.FilterStart), CatchType = e.CatchType == null ? null : importer.Import(e.CatchType)
            });
        return body;
    }
}

public sealed class TestManifest(string json)
{
    public object this[string key] => JsonDocument.Parse(json).RootElement.TryGetProperty(key, out _) ? new object() : null;
}

public static class RuntimeHost
{
    public static string GameFolder = "C:\\game\\";
    public static ZipArchive Archive;
    public static bool FailInstall;
    public static void Info(string text, int behavior, string source) { }
    public static string Folder() => GameFolder;
    public static void Hint(string text, int kind, bool replace) { }
    public static ZipArchive OpenZip(string file, CancellationToken token) => Archive = ZipFile.OpenRead(file);
    public static string ReadString(Stream stream, Encoding encoding) { using var reader = new StreamReader(stream, encoding); return reader.ReadToEnd(); }
    public static TestManifest DeserializeJson(string text) { _ = JsonDocument.Parse(text); return new TestManifest(text); }
    public static string[] Split(string value, string delimiter, bool ignoreEmpty) => value.Split(delimiter, ignoreEmpty ? StringSplitOptions.RemoveEmptyEntries : StringSplitOptions.None);
    public static int CompareString(string left, string right, bool ignoreCase) => string.Compare(left, right, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public static void ProjectError(Exception ex) { }
    public static string Display(Exception ex, bool multiline) => ex.ToString();
    public static string Extension(string file) => Path.GetExtension(file).TrimStart('.');
    private static string Install(string text) { if (FailInstall) throw new InvalidOperationException("installer failed"); return text; }
    public static string InstallPackCurseForge(string file, ZipArchive archive, string folder, string name, string logo) => Install($"CurseForge|{folder}|{name}|{logo}");
    public static string InstallPackModrinth(string file, ZipArchive archive, string folder, string name, string logo) => Install($"Modrinth|{folder}|{name}|{logo}");
    public static string InstallPackMCBBS(string file, ZipArchive archive, string folder, string name) => Install($"MCBBS|{folder}|{name}");
    public static string InstallPackHMCL(string file, ZipArchive archive, string folder) => Install("HMCL|" + folder);
    public static string InstallPackMMC(string file, ZipArchive archive, string folder) => Install("MMC|" + folder);
    public static string InstallPackLauncherPack(string file, ZipArchive archive, string folder) => Install("LauncherPack|" + folder);
    public static string InstallPackCompress(string file, ZipArchive archive) => Install("Compress");
}

internal static class EntryRegression
{
    public static void Add(ModuleDef source, ModuleDef host)
    {
        var entry = new ModpackEntryRule().Inspect(source); entry.Edit(); Il.Prepare(entry.Method);
        var importer = new Importer(host);
        var capture = Il.Type(host, typeof(EntryCapture).FullName!);
        var ui = Il.Type(host, typeof(EntryHost).FullName!);
        var target = new MethodDefUser("PatchedEntry", MethodSig.CreateStatic(host.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL,
            dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody { InitLocals = true } };
        ui.Methods.Add(target);
        foreach (var local in entry.Method.Body.Variables)
            target.Body.Variables.Add(new Local(local.Type.ElementType == ElementType.String ? host.CorLibTypes.String : new ClassSig(capture)));
        var map = entry.Method.Body.Instructions.ToDictionary(i => i, i => new Instruction(i.OpCode));
        foreach (var i in entry.Method.Body.Instructions)
        {
            object operand = i.Operand;
            if (operand is Instruction branch) operand = map[branch];
            else if (operand is Local local) operand = target.Body.Variables[local.Index];
            else if (i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Ldfld) operand = capture.Fields.Single(f => f.Name == nameof(EntryCapture.File));
            else if (operand is IMethod m)
            {
                if (m.DeclaringType.FullName.Contains("_Closure", StringComparison.Ordinal))
                    operand = capture.Methods.Single(c => c.Name == (m.Name == ".ctor" ? ".ctor" : nameof(EntryCapture.Run)));
                else if (m.DeclaringType.FullName.StartsWith("MeloongCore.OneOf", StringComparison.Ordinal)) operand = ui.Methods.Single(c => c.Name == nameof(EntryHost.Filter));
                else if (m.DeclaringType.FullName.StartsWith("System.Nullable", StringComparison.Ordinal)) operand = importer.Import(typeof(EntryFilter?).GetConstructor([typeof(EntryFilter)]));
                else if (m.Name == "SelectFile") operand = ui.Methods.Single(c => c.Name == nameof(EntryHost.Select));
                else if (m.Name == "RunInThread") operand = ui.Methods.Single(c => c.Name == nameof(EntryHost.Run));
                else operand = importer.Import(m);
            }
            map[i].Operand = operand;
            target.Body.Instructions.Add(map[i]);
        }
        Il.Prepare(target);
    }
    public static void Execute(Assembly assembly)
    {
        var ui = assembly.GetType(typeof(EntryHost).FullName!)!;
        var capture = assembly.GetType(typeof(EntryCapture).FullName!)!;
        var entry = ui.GetMethod("PatchedEntry")!;
        foreach (string[] selection in new[] { Array.Empty<string>(), new[] { "" }, new[] { "one.zip", "two.zip" } })
        {
            ui.GetField(nameof(EntryHost.Selection))!.SetValue(null, selection.ToList());
            ui.GetField(nameof(EntryHost.Runs))!.SetValue(null, 0);
            entry.Invoke(null, null);
            int count = (int)ui.GetField(nameof(EntryHost.Runs))!.GetValue(null)!;
            TestProgram.Assert(count == (selection.Length == 2 ? 1 : 0), "P3 cancellation/runner");
            if (count == 1) TestProgram.Assert((string)capture.GetField(nameof(EntryCapture.Installed))!.GetValue(null)! == "one.zip", "P3 captured wrong file");
        }
        TestProgram.Assert(!(bool)ui.GetField(nameof(EntryHost.Multi))!.GetValue(null)!, "P3 multi-select");
        TestProgram.Assert(((string)ui.GetField(nameof(EntryHost.LastFilter))!.GetValue(null)!).Contains("*.rar;*.zip;*.mrpack"), "P3 filter");
        Console.WriteLine("  Executed generated P3 IL: cancel, empty selection, first file, captured path, thread scheduling and file filter.");
    }
}

public struct EntryFilter { public string Text; }
public static class EntryHost
{
    public static List<string> Selection = [];
    public static int Runs;
    public static bool Multi;
    public static string LastFilter;
    public static EntryFilter Filter(string text) => new() { Text = text };
    public static List<string> Select(string title, bool multi, string directory, EntryFilter? filter)
    {
        Multi = multi; LastFilter = filter?.Text; return Selection;
    }
    public static void Run(Action action) { Runs++; action(); }
}
public sealed class EntryCapture
{
    public string File;
    public static string Installed;
    public void Run() => Installed = File;
}
