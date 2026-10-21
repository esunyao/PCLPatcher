using System.Text;

namespace PCLPatcher;

internal static class Program
{
    internal static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var options = CommandLine.Parse(args);
            if (options.Help)
            {
                Console.WriteLine(CommandLine.Usage);
                return args.Length == 0 ? 2 : 0;
            }
            if (options.Version)
            {
                Console.WriteLine($"PCLPatcher {CommandLine.ToolVersion}");
                return 0;
            }
            return PatchRunner.Run(options);
        }
        catch (UsageException ex) { return Fail(2, ex.Message); }
        catch (UnsupportedException ex) { return Fail(3, ex.Message); }
        catch (VerificationException ex) { return Fail(5, ex.Message); }
        catch (Exception ex) { return Fail(4, ex.Message); }
    }

    private static int Fail(int code, string message)
    {
        Console.Error.WriteLine($"失败：{message}");
        return code;
    }
}
