using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Starts, finds and stops Server~/fvp_server.py. The server is started without redirected pipes (nobody would
// drain them once Unity moves on) and logs to a file instead, which FvpLogTail echoes into the Console.
// It keeps running after Play ends, so the next Play skips the model load; it exits by itself after
// --idle-exit seconds without a client, and Unity stops it on quit.
public static class FvpServerProcess
{
    const string PidKey = "FasterVoxelPose.ServerPid";
#if !UNITY_EDITOR
    static int fallbackPid; // players have no SessionState
#endif

    public static string LogPath
    {
        get
        {
#if UNITY_EDITOR
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "fvp_server.log"));
#else
            return Path.Combine(Application.persistentDataPath, "fvp_server.log");
#endif
        }
    }

    // Where the server keeps the last configuration it was given (the next start builds its voxel grids from it).
    public static string CacheDir
    {
        get
        {
#if UNITY_EDITOR
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "FasterVoxelPose"));
#else
            return Path.Combine(Application.persistentDataPath, "FasterVoxelPose");
#endif
        }
    }

    public static string DefaultScriptPath =>
        Path.GetFullPath(Path.Combine(Application.dataPath, "Scripts", "FasterVoxelPose", "Server~", "fvp_server.py"));

    static int Pid
    {
        get
        {
#if UNITY_EDITOR
            return UnityEditor.SessionState.GetInt(PidKey, 0);
#else
            return fallbackPid;
#endif
        }
        set
        {
#if UNITY_EDITOR
            UnityEditor.SessionState.SetInt(PidKey, value);
#else
            fallbackPid = value;
#endif
        }
    }

    public static bool IsRunning => Find() != null;

    static Process Find()
    {
        int pid = Pid;
        if (pid == 0) return null;
        try
        {
            Process p = Process.GetProcessById(pid);
            if (!p.HasExited && p.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase)) return p;
        }
        catch (ArgumentException) { } // no such process
        catch (InvalidOperationException) { }
        Pid = 0;
        return null;
    }

    public static bool TryLaunch(string pythonExe, string script, string repo, string host, int port, int idleExitSeconds,
                                 string extraArgs, out string error)
    {
        error = null;
        if (IsRunning) return true;
        if (!File.Exists(pythonExe)) { error = $"Python not found: {pythonExe}"; return false; }
        if (!File.Exists(script)) { error = $"Server script not found: {script}"; return false; }
        if (!Directory.Exists(repo)) { error = $"Faster-VoxelPose repo not found: {repo}"; return false; }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            var args = new StringBuilder();
            args.Append("-u \"").Append(script).Append("\" --repo \"").Append(repo).Append('"');
            args.Append(" --host ").Append(host).Append(" --port ").Append(port);
            args.Append(" --idle-exit ").Append(idleExitSeconds);
            args.Append(" --log-file \"").Append(LogPath).Append('"');
            args.Append(" --cache-dir \"").Append(CacheDir).Append('"');
            if (!string.IsNullOrWhiteSpace(extraArgs)) args.Append(' ').Append(extraArgs);

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = args.ToString(),
                WorkingDirectory = Path.GetDirectoryName(script),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            Process p = Process.Start(psi);
            Pid = p.Id;
            Debug.Log($"[FasterVoxelPose] started server (pid {p.Id}): {pythonExe} {psi.Arguments}");
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    public static void Stop()
    {
        Process p = Find();
        if (p == null) return;
        try { p.Kill(); p.WaitForExit(2000); }
        catch (Exception e) { Debug.LogWarning("[FasterVoxelPose] could not stop the server: " + e.Message); }
        Pid = 0;
    }
}

// Follows the server's log file and hands out the lines written since it was created. Starts at the current
// end of the file so a new Play does not replay the previous session.
public sealed class FvpLogTail
{
    readonly string path;
    long offset;
    string partial = "";

    public FvpLogTail(string path)
    {
        this.path = path;
        try { offset = File.Exists(path) ? new FileInfo(path).Length : 0; } catch { offset = 0; }
    }

    public void Poll(Action<string> line)
    {
        try
        {
            if (!File.Exists(path)) return;
            long len = new FileInfo(path).Length;
            if (len < offset) { offset = 0; partial = ""; } // truncated or replaced
            if (len == offset) return;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                var buf = new byte[Math.Min(len - offset, 1 << 16)];
                int n = fs.Read(buf, 0, buf.Length);
                offset += n;
                partial += Encoding.UTF8.GetString(buf, 0, n);
            }
            int nl;
            while ((nl = partial.IndexOf('\n')) >= 0)
            {
                string l = partial.Substring(0, nl).TrimEnd('\r');
                partial = partial.Substring(nl + 1);
                if (l.Length > 0) line(l);
            }
        }
        catch (IOException) { } // the server holds the file; try again next poll
    }
}
