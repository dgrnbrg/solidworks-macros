using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using SolidWorksMcpServer.Protocol;

namespace SolidWorksMcpServer.Core
{
    /// <summary>
    /// Isolates SOLIDWORKS COM / Roslyn execution in a supervised --worker child process.
    /// Metadata requests (initialize, ping, tools/list, notifications) run in-process (&lt;1ms)
    /// with zero COM involvement. If a COM or native fault kills the worker process, the
    /// supervisor logs the crash to %APPDATA%\SolidWorksMcpServer\crash_journal.jsonl,
    /// returns a structured error to the MCP client, and auto-respawns a fresh worker on the
    /// next tool call without dropping the MCP stdio connection.
    /// </summary>
    public static class McpSupervisorHost
    {
        private sealed class WorkerSession : IDisposable
        {
            public Process Process;
            public StreamWriter StdinWriter;
            public StreamReader StdoutReader;
            public Thread StderrThread;
            private readonly object _stderrLock = new object();
            private readonly List<string> _stderrRing = new List<string>();
            private bool _disposed;

            public void AppendStderr(string line)
            {
                if (string.IsNullOrEmpty(line)) return;
                lock (_stderrLock)
                {
                    _stderrRing.Add(line);
                    if (_stderrRing.Count > 40)
                        _stderrRing.RemoveAt(0);
                }
            }

            public List<string> SnapshotStderr()
            {
                lock (_stderrLock)
                {
                    return new List<string>(_stderrRing);
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try { StdinWriter?.Close(); } catch { }
                try { StdoutReader?.Close(); } catch { }
                if (Process != null)
                {
                    try
                    {
                        if (!Process.HasExited)
                        {
                            if (!Process.WaitForExit(400))
                            {
                                Process.Kill();
                                Process.WaitForExit(500);
                            }
                        }
                    }
                    catch { }
                    try { Process.Dispose(); } catch { }
                }
            }
        }

        private static string GetCrashJournalPath()
        {
            string overrideDir = System.Environment.GetEnvironmentVariable("SOLIDWORKS_MCP_CRASH_JOURNAL_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                if (!Directory.Exists(overrideDir))
                    Directory.CreateDirectory(overrideDir);
                return Path.Combine(overrideDir, "crash_journal.jsonl");
            }

            string dir = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "SolidWorksMcpServer");
            try
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return Path.Combine(dir, "crash_journal.jsonl");
            }
            catch
            {
                string fallbackDir = Path.Combine(Path.GetTempPath(), "SolidWorksMcpServer");
                if (!Directory.Exists(fallbackDir))
                    Directory.CreateDirectory(fallbackDir);
                return Path.Combine(fallbackDir, "crash_journal.jsonl");
            }
        }

        public static void Run(McpStdioHost localMetadataHost)
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);

            WorkerSession worker = null;
            try
            {
                using (StreamReader reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                using (StreamWriter writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true })
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        string response = DispatchSupervisorLine(line, localMetadataHost, ref worker);
                        if (!string.IsNullOrEmpty(response))
                            writer.WriteLine(response);
                    }
                }
            }
            finally
            {
                if (worker != null)
                {
                    worker.Dispose();
                    worker = null;
                }
            }
        }

        private static string DispatchSupervisorLine(string rawJson, McpStdioHost metadataHost, ref WorkerSession worker)
        {
            object id = null;
            string method = string.Empty;
            Dictionary<string, object> paramsDict = null;

            try
            {
                Dictionary<string, object> req = JsonHelper.DeserializeDict(rawJson);
                req.TryGetValue("id", out id);
                method = JsonHelper.GetString(req, "method", string.Empty);
                paramsDict = JsonHelper.GetDict(req, "params");
            }
            catch (Exception ex)
            {
                McpStdioHost.Log("Supervisor JSON parse error: " + ex.Message);
                return metadataHost.ProcessRawLine(rawJson);
            }

            if (id == null ||
                string.IsNullOrEmpty(method) ||
                !string.Equals(method, "tools/call", StringComparison.Ordinal))
            {
                return metadataHost.ProcessRawLine(rawJson);
            }

            string toolName = paramsDict != null ? JsonHelper.GetString(paramsDict, "name", string.Empty) : string.Empty;
            Dictionary<string, object> toolArgs = paramsDict != null ? JsonHelper.GetDict(paramsDict, "arguments") : null;
            string argsPreview = BuildArgsPreview(toolArgs);

            // Up to 1 automatic retry ONLY if a pre-warmed worker had already died before accepting this call
            if (worker == null || IsWorkerDead(worker))
            {
                worker?.Dispose();
                worker = SpawnWorker();
            }

            try
            {
                worker.StdinWriter.WriteLine(rawJson);
                worker.StdinWriter.Flush();
            }
            catch (Exception writeEx)
            {
                McpStdioHost.Log("Supervisor failed writing to dead worker before '" + toolName + "': " + writeEx.Message + "; respawning.");
                worker.Dispose();
                worker = SpawnWorker();
                worker.StdinWriter.WriteLine(rawJson);
                worker.StdinWriter.Flush();
            }

            string outLine;
            while ((outLine = worker.StdoutReader.ReadLine()) != null)
            {
                if (outLine.StartsWith(McpStdioHost.WorkerFramePrefix, StringComparison.Ordinal))
                {
                    return outLine.Substring(McpStdioHost.WorkerFramePrefix.Length);
                }
                McpStdioHost.Log("[worker-stdout] " + outLine);
            }

            // Worker stdout reached EOF cleanly or via crash without writing our framed response!
            int exitCode = -1;
            try
            {
                if (!worker.Process.HasExited)
                    worker.Process.WaitForExit(500);
                if (worker.Process.HasExited)
                    exitCode = worker.Process.ExitCode;
            }
            catch { }

            // Briefly allow background stderr reader thread to flush last lines
            try { worker.StderrThread?.Join(150); } catch { }
            List<string> stderrTail = worker.SnapshotStderr();

            worker.Dispose();
            worker = null;

            string exitHex = "0x" + unchecked((uint)exitCode).ToString("X8", CultureInfo.InvariantCulture);
            string journalPath = RecordCrashEvent(toolName, argsPreview, exitCode, exitHex, stderrTail);

            McpToolResult errRes = McpToolResult.Error(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "SolidWorksMcpServer worker terminated unexpectedly during '{0}' (exit {1}, {2}) and self-recovered. The MCP server connection remains alive (no plugin reload needed); inspect crash_journal_path before retrying the failing API pattern.",
                    toolName, exitCode, exitHex),
                new Dictionary<string, object>
                {
                    { "worker_recovered", true },
                    { "tool_name", toolName },
                    { "exit_code", exitCode },
                    { "exit_code_hex", exitHex },
                    { "crash_journal_path", journalPath },
                    { "arguments_preview", argsPreview },
                    { "stderr_tail", stderrTail }
                });

            return McpStdioHost.MakeToolCallResultJson(id, errRes);
        }

        private static bool IsWorkerDead(WorkerSession worker)
        {
            if (worker == null || worker.Process == null) return true;
            try { return worker.Process.HasExited; }
            catch { return true; }
        }

        private static WorkerSession SpawnWorker()
        {
            string exePath = Assembly.GetExecutingAssembly().Location;
            UTF8Encoding utf8NoBom = new UTF8Encoding(false);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--worker",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = utf8NoBom,
                StandardErrorEncoding = utf8NoBom
            };

            Process proc = new Process { StartInfo = psi };
            if (!proc.Start())
                throw new InvalidOperationException("Failed to start SolidWorksMcpServer --worker subprocess.");

            WorkerSession session = new WorkerSession
            {
                Process = proc,
                StdinWriter = new StreamWriter(proc.StandardInput.BaseStream, utf8NoBom) { AutoFlush = true },
                StdoutReader = proc.StandardOutput
            };

            Thread errThread = new Thread(() =>
            {
                try
                {
                    string errLine;
                    while ((errLine = proc.StandardError.ReadLine()) != null)
                        session.AppendStderr(errLine);
                }
                catch { }
            })
            {
                IsBackground = true,
                Name = "McpWorkerStderrDrain"
            };
            errThread.Start();
            session.StderrThread = errThread;

            McpStdioHost.Log("Supervisor spawned worker PID=" + proc.Id);
            return session;
        }

        private static string BuildArgsPreview(Dictionary<string, object> toolArgs)
        {
            if (toolArgs == null || toolArgs.Count == 0) return "{}";
            try
            {
                string raw = JsonHelper.Serialize(toolArgs);
                return raw.Length <= 400 ? raw : (raw.Substring(0, 400) + "...");
            }
            catch
            {
                return "{}";
            }
        }

        private static string RecordCrashEvent(
            string toolName,
            string argsPreview,
            int exitCode,
            string exitHex,
            List<string> stderrTail)
        {
            string journalPath = GetCrashJournalPath();
            try
            {
                Dictionary<string, object> entry = new Dictionary<string, object>
                {
                    { "timestamp_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                    { "tool_name", toolName },
                    { "exit_code", exitCode },
                    { "exit_code_hex", exitHex },
                    { "arguments_preview", argsPreview },
                    { "stderr_tail", stderrTail ?? new List<string>() }
                };
                string jsonLine = JsonHelper.Serialize(entry);
                try
                {
                    File.AppendAllText(journalPath, jsonLine + System.Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    string fallbackDir = Path.Combine(Path.GetTempPath(), "SolidWorksMcpServer");
                    if (!Directory.Exists(fallbackDir))
                        Directory.CreateDirectory(fallbackDir);
                    journalPath = Path.Combine(fallbackDir, "crash_journal.jsonl");
                    File.AppendAllText(journalPath, jsonLine + System.Environment.NewLine, new UTF8Encoding(false));
                }
                McpStdioHost.Log("Worker crash recovered (" + exitHex + " in " + toolName + ") -> " + journalPath);
            }
            catch (Exception ex)
            {
                McpStdioHost.Log("Failed to append crash_journal.jsonl: " + ex.Message);
            }
            return journalPath;
        }

        public static object ReadCrashJournalSummary(int maxRecent = 5)
        {
            string journalPath = GetCrashJournalPath();
            if (!File.Exists(journalPath))
            {
                string fallback = Path.Combine(Path.GetTempPath(), "SolidWorksMcpServer", "crash_journal.jsonl");
                if (File.Exists(fallback))
                    journalPath = fallback;
                else
                    return null;
            }

            try
            {
                string[] lines = File.ReadAllLines(journalPath, new UTF8Encoding(false));
                if (lines.Length == 0)
                    return null;

                List<object> recent = new List<object>();
                int start = Math.Max(0, lines.Length - Math.Max(1, maxRecent));
                for (int i = lines.Length - 1; i >= start; i--)
                {
                    string l = (lines[i] ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(l)) continue;
                    try { recent.Add(JsonHelper.DeserializeDict(l)); }
                    catch { recent.Add(l); }
                }

                return new
                {
                    crash_journal_path = journalPath,
                    total_recorded_crashes = lines.Length,
                    recent_crashes = recent
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
