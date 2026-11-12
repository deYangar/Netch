using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.Threading;
using Netch.Enums;
using Netch.Models;
using Netch.Utils;
using Netch.Localization;

namespace Netch.Controllers;

public abstract class Guard
{
    private FileStream? _logFileStream;
    private StreamWriter? _logStreamWriter;

    /// <param name="mainFile">application path relative of Netch\bin</param>
    /// <param name="redirectOutput"></param>
    /// <param name="encoding">application output encode</param>
    protected Guard(string mainFile, bool redirectOutput = true, Encoding? encoding = null)
    {
        RedirectOutput = redirectOutput;

        var fileName = Path.GetFullPath($"bin\\{mainFile}");

        if (!File.Exists(fileName))
            throw new MessageException(
                I18N.TF("controller.kernelBinMissing", mainFile) + "\n" +
                I18N.T("controller.youCanDownloadSettingsKernel"));

        Instance = new Process
        {
            StartInfo =
            {
                FileName = fileName,
                WorkingDirectory = $"{Global.NetchDir}\\bin",
                CreateNoWindow = true,
                UseShellExecute = !RedirectOutput,
                RedirectStandardOutput = RedirectOutput,
                StandardOutputEncoding = RedirectOutput ? encoding : null,
                RedirectStandardError = RedirectOutput,
                StandardErrorEncoding = RedirectOutput ? encoding : null,
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };
    }

    public string LogPath => Path.Combine(Global.NetchDir, $"logging\\{Name}.log");

    protected virtual IEnumerable<string> StartedKeywords { get; } = new List<string>();

    protected virtual IEnumerable<string> FailedKeywords { get; } = new List<string>();

    public abstract string Name { get; }

    private State State { get; set; } = State.Waiting;

    private bool RedirectOutput { get; }

    public Process Instance { get; }

    /// <summary>
    ///     用户主动停止期间置位：Exited 由 StopGuardAsync 的 Kill 触发时不上报（M3-2 §2）
    /// </summary>
    private volatile bool _stoppingByUser;

    /// <summary>
    ///     运行中打出 FailedKeywords（如 sing-box FATAL）置位（C-4，2026-11-04 审查）：
    ///     配置错误/端口冲突类致命错误重启同配置必再挂，编排层据此跳过看门狗重启循环，
    ///     直接停止并给用户明确错误。volatile：ReadOutputAsync 线程写、Exited 事件线程读
    /// </summary>
    public volatile bool FatalAfterStart;

    /// <summary>
    ///     内核进程运行中意外退出（不含用户主动停止）
    /// </summary>
    public event Action<Guard>? InstanceExited;

    /// <summary>
    ///     订阅进程退出事件，交给编排层做重启与退避
    /// </summary>
    private void Watchdog()
    {
        try
        {
            Instance.EnableRaisingEvents = true;
            Instance.Exited += (_, _) => OnInstanceExited();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Watchdog subscribe failed for {Name}", Name);
        }
    }

    private void OnInstanceExited()
    {
        if (_stoppingByUser)
            return;

        Log.Warning("{Name} exited unexpectedly", Name);
        InstanceExited?.Invoke(this);
    }

    protected async Task StartGuardAsync(string argument, ProcessPriorityClass priority = ProcessPriorityClass.Normal)
    {
        State = State.Starting;
        _stoppingByUser = false;

        _logFileStream = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, true);
        _logStreamWriter = new StreamWriter(_logFileStream) { AutoFlush = true };

        Instance.StartInfo.Arguments = argument;
        Instance.Start();
        Global.Job.AddProcess(Instance);

        if (priority != ProcessPriorityClass.Normal)
            Instance.PriorityClass = priority;

        if (RedirectOutput)
        {
            ReadOutputAsync(Instance.StandardOutput).Forget();
            ReadOutputAsync(Instance.StandardError).Forget();

            if (!StartedKeywords.Any())
            {
                // Skip, No started keyword
                State = State.Started;
                Watchdog();
                return;
            }

            // wait ReadOutput change State（每秒留一行等待进度日志，最坏 50s 不静默——2026-11-04 审查 P-6③）
            for (var i = 0; i < 1000; i++)
            {
                await Task.Delay(50);
                if (i % 20 == 19)
                    Log.Information("Waiting {Name} start keyword... {Seconds}s", Name, (i + 1) / 20.0);

                switch (State)
                {
                    case State.Started:
                        Watchdog();
                        OnStarted();
                        return;
                    case State.Stopped:
                        await StopGuardAsync();
                        OnStartFailed();
                        throw new MessageException(I18N.TF("controller.kernel0StartFailed", Name));
                }
            }

            await StopGuardAsync();
            throw new MessageException(I18N.TF("controller.kernel0StartTimeout", Name));
        }
    }

    private async Task ReadOutputAsync(TextReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            await _logStreamWriter!.WriteLineAsync(line);
            OnReadNewLine(line);

            if (FailedKeywords.Any(s => line.Contains(s)))
            {
                if (State == State.Starting)
                {
                    OnStartFailed();
                    State = State.Stopped;
                }
                else if (State == State.Started)
                {
                    // C-4（2026-11-04 审查）：sing-box 无 StartedKeywords（warn 级日志无 started 行），
                    // 启动即刻放行后 FATAL 不再被检查——配置错误表现为看门狗 5 连重启的无意义循环。
                    // 运行中命中失败关键字即标记致命，进程退出后编排层跳过重启直接报错
                    FatalAfterStart = true;
                    Log.Error("{Name} fatal keyword after start: {Line}", Name, line);
                }
            }
            else if (State == State.Starting && StartedKeywords.Any(s => line.Contains(s)))
            {
                State = State.Started;
            }
        }
    }

    public virtual Task StopAsync()
    {
        return StopGuardAsync();
    }

    protected async Task StopGuardAsync()
    {
        // mark before Kill so the Exited handler does not report a user-initiated stop
        _stoppingByUser = true;

        try
        {
            try
            {
                Instance.EnableRaisingEvents = false;
            }
            catch (Exception)
            {
                // process object may already be gone
            }

            if (Instance is { HasExited: false })
            {
                Instance.Kill();
                await Instance.WaitForExitAsync();
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Stop {Name} failed", Instance.ProcessName);
        }
        finally
        {
            if (_logStreamWriter != null)
                await _logStreamWriter.DisposeAsync();

            if (_logFileStream != null)
                await _logFileStream.DisposeAsync();

            Instance.Dispose();

            State = State.Stopped;
        }
    }

    protected virtual void OnStarted()
    {
    }

    protected virtual void OnReadNewLine(string line)
    {
    }

    protected virtual void OnStartFailed()
    {
        Utils.Utils.Open(LogPath);
    }
}