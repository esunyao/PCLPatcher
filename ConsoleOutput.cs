namespace PCLPatcher;

internal static class ConsoleOutput
{
    public static void WriteLine(string text, ConsoleColor? color = null, bool error = false) =>
        Write(text + Environment.NewLine, color, error);

    public static void Write(string text, ConsoleColor? color = null, bool error = false)
    {
        var writer = error ? Console.Error : Console.Out;
        if (color == null || (error ? Console.IsErrorRedirected : Console.IsOutputRedirected))
        {
            writer.Write(text);
            return;
        }
        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color.Value;
            writer.Write(text);
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }
}
