using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Orika.Go.CodeAnalysis.Internal;

/// <summary>
/// 外部處理序的執行結果（內部使用）。
/// </summary>
internal sealed class ProcessResult
{
    public ProcessResult(int exitCode, string standardOutput, string standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public int ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }
}

/// <summary>
/// 以避免死結（deadlock-safe）的方式執行外部處理序並擷取 UTF-8 stdout／stderr（內部使用）。
/// </summary>
internal static class ProcessRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 執行指定的可執行檔並等待結束。stdout／stderr 以非同步方式排空後才呼叫
    /// <see cref="Process.WaitForExit()"/>，避免管線緩衝區塞滿造成的死結。
    /// </summary>
    /// <param name="fileName">可執行檔路徑。</param>
    /// <param name="arguments">命令列引數（逐一加入 <see cref="ProcessStartInfo.ArgumentList"/>，不做 shell 轉義）。</param>
    /// <param name="stdin">若非 <see langword="null"/>，以 UTF-8（無 BOM）寫入標準輸入後關閉。</param>
    /// <param name="workingDirectory">工作目錄；<see langword="null"/> 表示沿用目前目錄。</param>
    /// <param name="environment">要附加／覆寫的環境變數。</param>
    public static ProcessResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? stdin = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (stdin is not null)
        {
            psi.RedirectStandardInput = true;
            psi.StandardInputEncoding = Utf8NoBom;
        }

        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"無法啟動外部工具「{fileName}」：{ex.Message}", ex);
        }

        // 先啟動非同步讀取，再寫 stdin，最後才 WaitForExit —— 標準的防死結順序。
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (stdin is not null)
        {
            using var writer = process.StandardInput;
            writer.Write(stdin);
        }

        process.WaitForExit();
        Task.WaitAll(stdoutTask, stderrTask);

        return new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }
}
