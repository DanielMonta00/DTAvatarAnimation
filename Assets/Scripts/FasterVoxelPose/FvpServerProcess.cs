using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

// Starts, finds and stops the inference servers (Server~/fvp_server.py for Faster-VoxelPose, Server~/vitpose_server.py for ViTPose,
// each one named). A server is started without redirected pipes (nobody would drain them once Unity moves on) and logs to a file
// of its own, which FvpLogTail echoes into the Console. It keeps running after Play ends, so the next Play skips the model load;
// it exits by itself after --idle-exit seconds without a client, and Unity stops it on quit.
public static class FvpServerProcess
{
    public const string FvpName = "fvp_server", ViTPoseName = "vitpose_server";
    public static readonly string[] AllNames = { FvpName, ViTPoseName };
#if !UNITY_EDITOR
    static readonly System.Collections.Generic.Dictionary<string, int> fallbackPids = new System.Collections.Generic.Dictionary<string, int>(); // players have no SessionState
#endif

    // The key of the first server stays what it was, so a server started before this existed is still found.
    static string PidKey(string name) => name == FvpName ? "FasterVoxelPose.ServerPid" : "FasterVoxelPose.ServerPid." + name;

    public static string LogPath => LogPathOf(FvpName);

    public static string LogPathOf(string name)
    {
#if UNITY_EDITOR
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", name + ".log"));
#else
        return Path.Combine(Application.persistentDataPath, name + ".log");
#endif
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

    public static string ScriptPath(string file) =>
        Path.GetFullPath(Path.Combine(Application.dataPath, "Scripts", "FasterVoxelPose", "Server~", file));

    public static string DefaultScriptPath => ScriptPath("fvp_server.py");

    static int GetPid(string name)
    {
#if UNITY_EDITOR
        return UnityEditor.SessionState.GetInt(PidKey(name), 0);
#else
        return fallbackPids.TryGetValue(name, out int pid) ? pid : 0;
#endif
    }

    static void SetPid(string name, int pid)
    {
#if UNITY_EDITOR
        UnityEditor.SessionState.SetInt(PidKey(name), pid);
#else
        fallbackPids[name] = pid;
#endif
    }

    public static bool IsRunning => IsRunningOf(FvpName);
    public static bool IsRunningOf(string name) => Find(name) != null;

    static Process Find(string name)
    {
        int pid = GetPid(name);
        if (pid == 0) return null;
        try
        {
            Process p = Process.GetProcessById(pid);
            if (!p.HasExited && p.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase)) return p;
        }
        catch (ArgumentException) { } // no such process
        catch (InvalidOperationException) { }
        SetPid(name, 0);
        return null;
    }

    // Faster-VoxelPose
    public static bool TryLaunch(string pythonExe, string script, string repo, string host, int port, int idleExitSeconds,
                                 string extraArgs, out string error)
    {
        error = null;
        if (!Directory.Exists(repo)) { error = $"Faster-VoxelPose repo not found: {repo}"; return false; }
        return Launch(FvpName, pythonExe, script, $"--repo \"{repo}\"", host, port, idleExitSeconds, extraArgs, out error);
    }

    // Any server of this family: `fixedArgs` are the arguments that name its models (e.g. --repo, or --vitpose-dir and --yolo).
    public static bool Launch(string name, string pythonExe, string script, string fixedArgs, string host, int port, int idleExitSeconds,
                              string extraArgs, out string error)
    {
        error = null;
        if (IsRunningOf(name)) return true;
        if (!File.Exists(pythonExe)) { error = $"Python not found: {pythonExe}"; return false; }
        if (!File.Exists(script)) { error = $"Server script not found: {script}"; return false; }

        try
        {
            string log = LogPathOf(name);
            Directory.CreateDirectory(Path.GetDirectoryName(log));
            var args = new StringBuilder();
            args.Append("-u \"").Append(script).Append('"');
            if (!string.IsNullOrWhiteSpace(fixedArgs)) args.Append(' ').Append(fixedArgs);
            args.Append(" --host ").Append(host).Append(" --port ").Append(port);
            args.Append(" --idle-exit ").Append(idleExitSeconds);
            args.Append(" --log-file \"").Append(log).Append('"');
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
            SetPid(name, p.Id);
            Debug.Log($"[FasterVoxelPose] started {name} (pid {p.Id}): {pythonExe} {psi.Arguments}");
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    // Windows scheduling class of a server (CPU priority class and the class the GPU scheduler uses between processes).
    public enum Priority { Normal = 0, Above = 1, High = 2 }

    public static string PriorityArguments(Priority p) =>
        p == Priority.Normal ? "" : p == Priority.Above ? "--cpu-priority above --gpu-priority above" : "--cpu-priority high --gpu-priority high";

    public static void Stop() => StopNamed(FvpName);

    public static void StopAll()
    {
        foreach (string n in AllNames) StopNamed(n);
    }

    public static void StopNamed(string name)
    {
        Process p = Find(name);
        if (p == null) return;
        try { p.Kill(); p.WaitForExit(2000); }
        catch (Exception e) { Debug.LogWarning($"[FasterVoxelPose] could not stop {name}: " + e.Message); }
        SetPid(name, 0);
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
