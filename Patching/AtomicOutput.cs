using System.Security.Cryptography;

namespace PCLPatcher;

internal static class AtomicOutput
{
    public static void Write(Options options, byte[] original, Action<FileStream> write, Action<string> verify)
    {
        string temp = Path.Combine(Path.GetDirectoryName(options.Output)!, $".{Path.GetFileName(options.Output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(true);
            }
            try { verify(temp); }
            catch (Exception ex) { throw new VerificationException("临时文件校验失败：" + ex.Message); }
            if (!SHA256.HashData(File.ReadAllBytes(options.Input)).SequenceEqual(SHA256.HashData(original)))
                throw new IOException("输入文件在检查后发生变化，已停止提交。");
            if (options.InPlace)
            {
                string backup = options.Input + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff");
                File.Replace(temp, options.Input, backup);
                Console.WriteLine($"备份：{backup}");
            }
            else File.Move(temp, options.Output, false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
