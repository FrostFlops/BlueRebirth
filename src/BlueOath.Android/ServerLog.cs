using System.Collections.Concurrent;

namespace BlueOath.LocalServer;

/// <summary>
/// 轻量日志总线：把服务端生命周期/业务日志同时送到 logcat、内存环形缓冲与 UI 订阅者。
/// 内存缓冲让 MainActivity 打开时能看到「启动之前」已经产生的日志（含首次解压配置）。
/// </summary>
public static class ServerLog
{
    /// <summary>logcat 统一 Tag，便于 <c>adb logcat -s BO-SRV:V</c> 抓取。</summary>
    public const string Tag = "BO-SRV";

    private const int MaxLines = 2000;

    private static readonly object Gate = new();
    private static readonly LinkedList<string> Lines = new();

    /// <summary>追加一行时触发；订阅方需自行切回 UI 线程。</summary>
    public static event Action<string>? Appended;

    public static void Info(string message) => Add("I", message);

    public static void Warn(string message) => Add("W", message);

    public static void Error(string message) => Add("E", message);

    public static void Error(string message, Exception ex) =>
        Add("E", message + " :: " + ex.GetType().Name + ": " + ex.Message);

    private static void Add(string level, string message)
    {
        var line = string.Format("[{0:HH:mm:ss}] {1} {2}", DateTime.Now, level, message);

        switch (level)
        {
            case "E":
                Android.Util.Log.Error(Tag, message);
                break;
            case "W":
                Android.Util.Log.Warn(Tag, message);
                break;
            default:
                Android.Util.Log.Info(Tag, message);
                break;
        }

        lock (Gate)
        {
            Lines.AddLast(line);
            while (Lines.Count > MaxLines)
                Lines.RemoveFirst();
        }

        Appended?.Invoke(line);
    }

    /// <summary>当前缓冲的完整快照（用于 Activity 首次渲染）。</summary>
    public static string Snapshot()
    {
        lock (Gate)
        {
            return Lines.Count == 0 ? string.Empty : string.Join('\n', Lines);
        }
    }
}
