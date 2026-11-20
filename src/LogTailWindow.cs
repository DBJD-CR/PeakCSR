using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using UnityEngine;

namespace PeakCSR
{
    // 实时日志窗口
    // 游戏进程本身没有控制台, BepInEx 的日志只写文件 (BepInEx/LogOutput.log)。
    // 这里额外拉一个独立终端窗口对日志文件做 tail -f, 方便边玩边看。
    // 使用 CreateProcess + CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_CONSOLE,
    // 让日志窗口彻底脱离游戏进程树, 并记录真实 PID 以便可靠关闭。
    public static class LogTailWindow
    {
        private const uint CREATE_NEW_CONSOLE = 0x00000010;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        private const int STARTF_USESHOWWINDOW = 0x00000001;
        private const short SW_SHOWNORMAL = 1;
        private const int ERROR_ACCESS_DENIED = 5;

        private static Process _tailProcess;
        private static int _tailPid;
        private static float _nextLaunchAttempt = -1f;
        private static string _scriptPath;
        private static bool _exitHookInstalled;

        // 日志文件绝对路径 (BepInEx 5 的默认输出位置)
        public static string LogFilePath
        {
            get
            {
                try
                {
                    string root = Paths.BepInExRootPath;
                    if (!string.IsNullOrEmpty(root))
                    {
                        return Path.Combine(root, "LogOutput.log");
                    }
                }
                catch
                {
                }
                try
                {
                    // 兜底: 相对插件目录往上找 BepInEx
                    string plugin = Paths.PluginPath;
                    if (!string.IsNullOrEmpty(plugin))
                    {
                        return Path.Combine(Path.GetDirectoryName(plugin) ?? plugin, "LogOutput.log");
                    }
                }
                catch
                {
                }
                return null;
            }
        }

        public static bool IsRunning
        {
            get
            {
                try
                {
                    return _tailProcess != null && !_tailProcess.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        // 打开日志窗口
        // filterPeakCsr = true 时只显示含 "[PeakCSR]" 的行, 便于在海量原版/RANKED 日志里定位我们自己的输出
        public static void Open(bool filterPeakCsr)
        {
            if (IsRunning)
            {
                Plugin.Log.LogInfo("[PeakCSR] 日志窗口已在运行中。");
                return;
            }
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Plugin.Log.LogWarning("[PeakCSR] 日志窗口仅支持 Windows。");
                return;
            }

            string logPath = LogFilePath;
            if (string.IsNullOrEmpty(logPath))
            {
                Plugin.Log.LogWarning("[PeakCSR] 找不到 BepInEx 日志文件路径, 无法打开日志窗口。");
                return;
            }
            if (!File.Exists(logPath))
            {
                // 文件可能还没被 BepInEx 创建, 稍后重试
                _nextLaunchAttempt = Time.realtimeSinceStartup + 5f;
                Plugin.Log.LogInfo("[PeakCSR] 日志文件尚未生成, 5 秒后重试: " + logPath);
                return;
            }

            EnsureExitHook();

            try
            {
                int gamePid = Process.GetCurrentProcess().Id;
                string script = WriteTailScript(gamePid, logPath, filterPeakCsr);
                if (script == null)
                {
                    Plugin.Log.LogWarning("[PeakCSR] 写入日志窗口脚本失败, 放弃打开。");
                    return;
                }

                string psExe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    Path.Combine("WindowsPowerShell", Path.Combine("v1.0", "powershell.exe")));

                string title = filterPeakCsr ? "PeakCSR Log (only [PeakCSR])" : "PeakCSR Log (full)";
                string args = "-NoProfile -NoLogo -ExecutionPolicy Bypass -File \"" + script + "\"";
                string workingDir = Path.GetDirectoryName(logPath) ?? Environment.CurrentDirectory;

                int pid;
                if (!CreateDetachedProcess(psExe, args, title, workingDir, out pid))
                {
                    Plugin.Log.LogWarning("[PeakCSR] 启动日志窗口进程失败, Win32 错误: " + Marshal.GetLastWin32Error());
                    return;
                }

                _tailPid = pid;
                _scriptPath = script;
                try
                {
                    _tailProcess = Process.GetProcessById(pid);
                }
                catch
                {
                    _tailProcess = null;
                }

                Plugin.Log.LogInfo("[PeakCSR] 已打开实时日志窗口: " + title + " (PID " + pid + ") -> " + logPath);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PeakCSR] 打开日志窗口失败: " + ex.Message);
            }
        }

        public static void OpenDefault()
        {
            Open(Plugin.Instance == null || Plugin.Instance.LogWindowFilterPeakCsr == null
                || Plugin.Instance.LogWindowFilterPeakCsr.Value);
        }

        public static void Close()
        {
            Process proc = _tailProcess;
            _tailProcess = null;
            _tailPid = 0;

            if (proc != null)
            {
                try
                {
                    if (!proc.HasExited) proc.Kill();
                }
                catch
                {
                }
                try
                {
                    proc.Dispose();
                }
                catch
                {
                }
            }

            string script = _scriptPath;
            _scriptPath = null;
            if (!string.IsNullOrEmpty(script))
            {
                try
                {
                    if (File.Exists(script)) File.Delete(script);
                }
                catch
                {
                }
            }
        }

        // Unity 侧的重试驱动: 由 Plugin 的 Update 调用
        public static void Tick()
        {
            if (_nextLaunchAttempt < 0f) return;
            if (Time.realtimeSinceStartup < _nextLaunchAttempt) return;
            _nextLaunchAttempt = -1f;
            OpenDefault();
        }

        // 游戏进程无论怎么退出(正常/强制), 日志窗口都必须跟着走, 否则 Steam 会一直等待
        private static void EnsureExitHook()
        {
            if (_exitHookInstalled) return;
            _exitHookInstalled = true;
            try
            {
                AppDomain.CurrentDomain.ProcessExit += (sender, e) => Close();
            }
            catch
            {
            }
        }

        // 生成 tail 脚本: 自己读文件而不是用 Get-Content -Wait, 这样能一边读一边盯游戏 PID
        private static string WriteTailScript(int gamePid, string logPath, bool filterPeakCsr)
        {
            try
            {
                string dir = Path.GetTempPath();
                string script = Path.Combine(dir, "peakcsr-logtail-" + gamePid + ".ps1");

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
                sb.AppendLine("$GamePid = " + gamePid);
                sb.AppendLine("$LogPath = '" + EscapeSingle(logPath) + "'");
                sb.AppendLine("$Filter  = " + (filterPeakCsr ? "'PeakCSR'" : "''"));
                sb.AppendLine("$Host.UI.RawUI.WindowTitle = " +
                    (filterPeakCsr ? "'PeakCSR Log (only [PeakCSR])'" : "'PeakCSR Log (full)'"));
                sb.AppendLine(@"
function Test-GameAlive {
    return [bool](Get-Process -Id $GamePid -ErrorAction SilentlyContinue)
}

while (-not (Test-Path -LiteralPath $LogPath)) {
    if (-not (Test-GameAlive)) { exit }
    Start-Sleep -Milliseconds 400
}

$fs = $null
$reader = $null
try {
    $fs = [System.IO.File]::Open($LogPath, 'Open', 'Read', 'ReadWrite')
    # 回放最后 200 行: 只往前 seek 256KB, 避免整文件载入内存
    if ($fs.Length -gt 262144) {
        [void]$fs.Seek($fs.Length - 262144, 'Begin')
    }
    $reader = [System.IO.StreamReader]::new($fs, [System.Text.Encoding]::UTF8, $true)

    $tail = New-Object System.Collections.Generic.Queue[string]
    while ($null -ne ($line = $reader.ReadLine())) {
        $tail.Enqueue($line)
        while ($tail.Count -gt 200) { [void]$tail.Dequeue() }
    }
    foreach ($l in $tail) {
        if ($Filter -eq '' -or $l -match $Filter) { Write-Host $l }
    }

    # 实时跟踪: 游戏一退出就结束, 控制台窗口自动关闭
    while (Test-GameAlive) {
        if ($fs.Length -lt $fs.Position) {
            # 日志被 BepInEx 重建/清空过, 回到开头继续跟
            [void]$fs.Seek(0, 'Begin')
            $reader.DiscardBufferedData()
        }
        $line = $reader.ReadLine()
        if ($null -eq $line) {
            Start-Sleep -Milliseconds 250
            continue
        }
        if ($Filter -eq '' -or $line -match $Filter) { Write-Host $line }
    }
}
finally {
    if ($reader -ne $null) { $reader.Dispose() }
    if ($fs -ne $null) { $fs.Dispose() }
}
");
                File.WriteAllText(script, sb.ToString(), new UTF8Encoding(true));
                return script;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PeakCSR] 生成日志窗口脚本失败: " + ex.Message);
                return null;
            }
        }

        // 用 CreateProcess 启动, 刻意脱离游戏进程树与 job object
        private static bool CreateDetachedProcess(
            string exePath, string arguments, string title, string workingDir, out int pid)
        {
            pid = 0;

            // 先带 BREAKAWAY 尝试; 若当前进程不在 job 里, 该 flag 可能被拒(ERROR_ACCESS_DENIED), 需要降级重试
            if (TryCreate(exePath, arguments, title, workingDir,
                    CREATE_NEW_CONSOLE | CREATE_UNICODE_ENVIRONMENT | CREATE_BREAKAWAY_FROM_JOB, out pid))
            {
                return true;
            }
            if (Marshal.GetLastWin32Error() != ERROR_ACCESS_DENIED) return false;

            return TryCreate(exePath, arguments, title, workingDir,
                CREATE_NEW_CONSOLE | CREATE_UNICODE_ENVIRONMENT, out pid);
        }

        private static bool TryCreate(
            string exePath, string arguments, string title, string workingDir, uint flags, out int pid)
        {
            pid = 0;

            STARTUPINFO si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.lpTitle = title;
            si.dwFlags = STARTF_USESHOWWINDOW;
            si.wShowWindow = SW_SHOWNORMAL;

            PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
            string cmdLine = "\"" + exePath + "\" " + arguments;

            // bInheritHandles = false: 不把游戏的句柄泄漏给日志窗口
            bool ok = CreateProcess(
                exePath, cmdLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                IntPtr.Zero, workingDir, ref si, out pi);

            if (!ok) return false;

            pid = pi.dwProcessId;
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            return true;
        }

        private static string EscapeSingle(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("'", "''");
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
