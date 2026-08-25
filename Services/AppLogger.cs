using System;
using System.IO;
using System.Text;

namespace CIS_WebInspector.Services
{
    /// <summary>
    /// 应用层统一日志入口。业务与算法模块只提交原始消息，不关心日志文件位置、时间戳或 UI 线程。
    /// </summary>
    public interface IAppLogger
    {
        /// <summary>业务状态、告警和工程异常：写入运行日志并发布到 UI。</summary>
        void Write(string message);
        /// <summary>高频算法诊断：仅输出到调试通道，不占用生产日志文件和 UI 队列。</summary>
        void WriteDiagnostic(string message);
    }

    /// <summary>
    /// 按日期持久化日志并发布已格式化消息。文件写入失败不会反向中断采集或检测流水线。
    /// </summary>
    public sealed class AppLogger : IAppLogger, IDisposable
    {
        private readonly object _fileSync = new object();
        private readonly string _logDirectory;
        private StreamWriter _writer;
        private string _writerDate;
        private bool _disposed;

        public AppLogger(string baseDirectory = null)
        {
            string root = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;
            _logDirectory = Path.Combine(root, "日志");
        }

        /// <summary>在调用线程同步触发；订阅方如需更新 WPF，必须自行调度到 UI Dispatcher。</summary>
        public event Action<string> MessageWritten;

        public void Write(string message)
        {
            if (_disposed || string.IsNullOrEmpty(message))
                return;

            DateTime now = DateTime.Now;
            string formattedMessage = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] {message}";

            try
            {
                WriteFileLine(now, formattedMessage);
            }
            catch
            {
                // 日志介质故障不能中断生产流程；关闭句柄，让下一条日志重新尝试打开文件。
                CloseWriter();
            }

            Publish(formattedMessage);
        }

        public void WriteDiagnostic(string message)
        {
            if (_disposed || string.IsNullOrEmpty(message))
                return;

            System.Diagnostics.Debug.WriteLine(message);
        }

        private void WriteFileLine(DateTime timestamp, string message)
        {
            string date = timestamp.ToString("yyyyMMdd");
            lock (_fileSync)
            {
                if (_disposed)
                    return;

                if (_writer == null || !string.Equals(_writerDate, date, StringComparison.Ordinal))
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(_logDirectory);
                    string logPath = Path.Combine(_logDirectory, $"SysRunLog_{date}.txt");
                    var stream = new FileStream(
                        logPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        4096,
                        FileOptions.SequentialScan);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false), 4096)
                    {
                        AutoFlush = true
                    };
                    _writerDate = date;
                }

                _writer.WriteLine(message);
            }
        }

        private void Publish(string message)
        {
            Delegate[] handlers = MessageWritten?.GetInvocationList();
            if (handlers == null)
                return;

            foreach (Action<string> handler in handlers)
            {
                try
                {
                    handler(message);
                }
                catch
                {
                    // 一个显示订阅方异常不能阻断其他订阅方，也不能影响视觉处理。
                }
            }
        }

        private void CloseWriter()
        {
            lock (_fileSync)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
                _writerDate = null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            CloseWriter();
            MessageWritten = null;
        }
    }

    /// <summary>供不需要日志输出的独立算法调用使用，避免在热路径反复判断 null。</summary>
    internal sealed class NullAppLogger : IAppLogger
    {
        public static readonly NullAppLogger Instance = new NullAppLogger();
        private NullAppLogger() { }
        public void Write(string message) { }
        public void WriteDiagnostic(string message) { }
    }

    /// <summary>保护性调用日志接口，确保自定义日志实现的异常不会改变检测结果。</summary>
    internal static class AppLog
    {
        public static void Write(IAppLogger logger, string message)
        {
            try
            {
                logger?.Write(message);
            }
            catch
            {
                // 日志失败不属于产品缺陷，也不应终止算法主线。
            }
        }

        public static void Diagnostic(IAppLogger logger, string message)
        {
            try
            {
                logger?.WriteDiagnostic(message);
            }
            catch
            {
                // 调试信息永远不能改变算法执行路径。
            }
        }
    }
}
