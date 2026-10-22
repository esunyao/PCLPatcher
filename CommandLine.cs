namespace PCLPatcher;

internal sealed record Options(string Input, string Output, bool InPlace, bool Check, bool Help, bool Version);

internal static class CommandLine
{
    public const string ToolVersion = "1.0.0";
    public const string Usage = """
        PCLPatcher — 静态 IL 补丁器
        用法：
          PCLPatcher --input <PCL.exe> --check
          PCLPatcher --input <PCL.exe> [--output <输出.exe>]
          PCLPatcher --input <PCL.exe> --in-place
          PCLPatcher --version

        默认输出 <原文件名>.patched.exe，已有输出不会被覆盖。
        --check 只读检查；--in-place 校验通过后替换输入，并保留时间戳备份。
        结构检查通过不代表未知版本已经过功能验证。
        退出码：0 成功；2 参数错误；3 不支持；4 操作失败；5 输出校验失败。
        """;

    public static Options Parse(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
            return new(null, null, false, false, true, false);
        if (args.Length == 1 && args[0] == "--version")
            return new(null, null, false, false, false, true);
        string input = null, output = null;
        bool inPlace = false, check = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (!seen.Add(args[i])) throw new UsageException($"重复参数：{args[i]}");
            switch (args[i])
            {
                case "--input": input = Value(args, ref i); break;
                case "--output": output = Value(args, ref i); break;
                case "--in-place": inPlace = true; break;
                case "--check": check = true; break;
                case "--donor": throw new UsageException("已移除 --donor；现在只需 --input <PCL.exe>。");
                default: throw new UsageException($"未知参数：{args[i]}。使用 --help 查看用法。");
            }
        }
        if (string.IsNullOrWhiteSpace(input)) throw new UsageException("必须指定 --input。");
        if (inPlace && output != null) throw new UsageException("--output 与 --in-place 不能同时使用。");
        if (check && (inPlace || output != null)) throw new UsageException("--check 不能与输出选项同时使用。");
        try
        {
            input = Path.GetFullPath(input);
            if (!File.Exists(input)) throw new UsageException($"输入文件不存在：{input}");
            output = inPlace ? input : Path.GetFullPath(output ?? Path.Combine(
                Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input) + ".patched.exe"));
        }
        catch (ArgumentException ex) { throw new UsageException("路径无效：" + ex.Message); }
        if (!check && !inPlace)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (input.Equals(output, comparison)) throw new UsageException("输出与输入相同；原地修改请用 --in-place。");
            if (File.Exists(output) || Directory.Exists(output)) throw new UsageException($"输出已存在：{output}");
            if (!Directory.Exists(Path.GetDirectoryName(output))) throw new UsageException("输出目录不存在。");
        }
        return new(input, output, inPlace, check, false, false);
    }

    private static string Value(string[] args, ref int index)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new UsageException($"参数 {args[index]} 缺少值。");
        return args[++index];
    }
}

internal sealed class UsageException(string message) : Exception(message);
internal sealed class UnsupportedException(string message) : Exception(message);
internal sealed class VerificationException(string message) : Exception(message);
