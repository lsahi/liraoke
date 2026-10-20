using System.Diagnostics;
using System.Text;

namespace KaraokeFilmMaker.Infrastructure.Media;

public static class MediaProcess
{
    public static async Task<string> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default,
        Func<Stream, CancellationToken, Task>? writeInput = null,
        Action<string>? outputLine = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
            // FFmpeg/ffprobe emit UTF-8 regardless of the Windows console/system code page.
            // Decoding Japanese tags as GBK can consume a JSON closing quote.
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            RedirectStandardInput = writeInput is not null,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        cancellationToken.ThrowIfCancellationRequested();
        process.Start();
        using CancellationTokenRegistration registration = cancellationToken.Register(() => Kill(process));
        Task<string> errors = ReadAsync(process.StandardError, null);
        Task<string> output = ReadAsync(process.StandardOutput, outputLine);
        try
        {
            if (writeInput is not null)
            {
                try
                {
                    await writeInput(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    process.StandardInput.Close();
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string diagnostic = await errors.ConfigureAwait(false);
            string result = await output.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"媒体处理失败（{process.ExitCode}）：{diagnostic}");
            }

            return result;
        }
        catch (Exception exception)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            string diagnostic = await errors.ConfigureAwait(false);
            await output.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (exception is IOException && diagnostic.Length > 0)
            {
                throw new InvalidOperationException($"媒体处理失败：{diagnostic}", exception);
            }

            throw;
        }
    }

    private static async Task<string> ReadAsync(StreamReader reader, Action<string>? onLine)
    {
        var text = new StringBuilder();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            onLine?.Invoke(line);
            if (text.Length < 2_000_000)
            {
                text.AppendLine(line);
            }
        }

        return text.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
