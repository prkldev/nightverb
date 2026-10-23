using System;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace Nightverb.Util;

internal static class AppInfo
{
    public const string Name = "Nightverb";
    public const string Version = "1.5.0";
    public const string HostName = "nightverb";                 // native messaging host name
    public const string ExtensionId = "nightverb@reverb.fx";    // Firefox add-on id

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    public static string InstalledExe => Path.Combine(DataDir, "nightverb.exe");
    public static string ManifestPath => Path.Combine(DataDir, "nightverb.json");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string LogPath => Path.Combine(DataDir, "nightverb.log");

    /// <summary>Pipe name unique per Windows user and login session.</summary>
    public static string PipeName
    {
        get
        {
            string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            return $"Nightverb.Engine.{sid}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
        }
    }

    public static string MutexName => @"Local\Nightverb.Engine";
}

internal static class Log
{
    private static readonly object Gate = new();
    public static string Role = "app";

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppInfo.DataDir);
                var fi = new FileInfo(AppInfo.LogPath);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    File.Copy(AppInfo.LogPath, AppInfo.LogPath + ".old", true);
                    File.WriteAllText(AppInfo.LogPath, "");
                }
                File.AppendAllText(AppInfo.LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Role} {Environment.ProcessId}] {msg}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch { /* logging must never break audio */ }
    }
}
