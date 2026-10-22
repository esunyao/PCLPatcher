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
                ConsoleOutput.WriteLine(CommandLine.Usage);
                return args.Length == 0 ? 2 : 0;
            }
            if (options.Version)
            {
                ConsoleOutput.WriteLine($"PCLPatcher {CommandLine.ToolVersion}");
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
        ConsoleOutput.WriteLine($"失败：{message}", ConsoleColor.Red, error: true);
        return code;
    }
}
