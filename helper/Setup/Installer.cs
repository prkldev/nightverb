using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Win32;
using Nightverb.Util;

namespace Nightverb.Setup;

/// <summary>
/// Per-user install, no admin rights:
///   %LOCALAPPDATA%\Nightverb\nightverb.exe + nightverb.json (native messaging manifest)
///   HKCU\Software\Mozilla\NativeMessagingHosts\nightverb  -> manifest path
///   HKCU\...\Uninstall\Nightverb                          -> entry in "Apps & features"
/// </summary>
internal static class Installer
{
    private const string FirefoxKey = @"Software\Mozilla\NativeMessagingHosts\" + AppInfo.HostName;
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Nightverb";

    public static int Install(bool quiet)
    {
        Log.Role = "setup";
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            string self = Environment.ProcessPath!;

            if (!SamePath(self, AppInfo.InstalledExe))
            {
                StopEngine();
                CopyWithRetry(self, AppInfo.InstalledExe);
            }

            var manifest = new JsonObject
            {
                ["name"] = AppInfo.HostName,
                ["description"] = "Nightverb reverb engine",
                ["path"] = AppInfo.InstalledExe,
                ["type"] = "stdio",
                ["allowed_extensions"] = new JsonArray(AppInfo.ExtensionId),
            };
            File.WriteAllText(AppInfo.ManifestPath,
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

            using (var k = Registry.CurrentUser.CreateSubKey(FirefoxKey))
                k.SetValue("", AppInfo.ManifestPath);

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Lang.T("Nightverb - аудиокомпонент для Firefox", "Nightverb Audio Component for Firefox"));
                k.SetValue("DisplayVersion", AppInfo.Version);
                k.SetValue("Publisher", "Nightverb");
                k.SetValue("DisplayIcon", AppInfo.InstalledExe);
                k.SetValue("InstallLocation", AppInfo.DataDir);
                k.SetValue("UninstallString", $"\"{AppInfo.InstalledExe}\" --uninstall");
                k.SetValue("QuietUninstallString", $"\"{AppInfo.InstalledExe}\" --uninstall --quiet");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(new FileInfo(AppInfo.InstalledExe).Length / 1024), RegistryValueKind.DWord);
            }

            Log.Write("installed to " + AppInfo.DataDir);
            if (!quiet)
            {
                // Nothing to click: the extension notices the component by itself,
                // this note just confirms it and disappears after a few seconds.
                Native.MessageBoxTimeoutW(IntPtr.Zero, Lang.T(
                    "Аудиокомпонент Nightverb установлен.\n\n" +
                    "Реверб в Firefox включится сам, можно возвращаться к музыке.\n\n" +
                    "Удалить компонент можно в Параметры → Приложения, как обычную программу.",
                    "The Nightverb audio component is installed.\n\n" +
                    "Reverb in Firefox turns on by itself, back to the music.\n\n" +
                    "To remove the component, use Settings → Apps like any other program."),
                    "Nightverb", Native.MB_OK | Native.MB_ICONINFO | Native.MB_SETFOREGROUND | Native.MB_TOPMOST, 0, 4000);
            }
            return 0;
        }
        catch (Exception e)
        {
            Log.Write("install failed: " + e);
            if (!quiet)
                Native.MessageBoxW(IntPtr.Zero, Lang.T("Не получилось установить:\n", "Installation failed:\n") + e.Message,
                    "Nightverb", Native.MB_OK | Native.MB_ICONERROR);
            return 1;
        }
    }

    public static int Uninstall(bool quiet)
    {
        Log.Role = "setup";
        if (!quiet && Native.MessageBoxW(IntPtr.Zero, Lang.T("Удалить аудиокомпонент Nightverb?", "Remove the Nightverb audio component?"), "Nightverb",
                Native.MB_YESNO | Native.MB_ICONQUESTION) != Native.IDYES)
            return 0;

        StopEngine();
        try { Registry.CurrentUser.DeleteSubKeyTree(FirefoxKey, false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

        // We may be running from the folder we're deleting: let cmd finish the job after we exit.
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{AppInfo.DataDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch { }

        if (!quiet)
            Native.MessageBoxW(IntPtr.Zero, Lang.T(
                    "Аудиокомпонент Nightverb удалён. Замедление в расширении продолжит работать.",
                    "The Nightverb audio component was removed. Slow-down in the extension keeps working."),
                "Nightverb", Native.MB_OK | Native.MB_ICONINFO);
        return 0;
    }

    /// <summary>Ask a running engine to quit, then make sure it's gone.</summary>
    public static void StopEngine()
    {
        try
        {
            using var p = new NamedPipeClientStream(".", AppInfo.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            p.Connect(500);
            var bytes = Encoding.UTF8.GetBytes("{\"type\":\"quit\"}\n");
            p.Write(bytes, 0, bytes.Length);
            p.Flush();
        }
        catch { }

        // Wait for the engine's single-instance mutex to be released.
        for (int i = 0; i < 30; i++)
        {
            if (!Mutex.TryOpenExisting(AppInfo.MutexName, out var m)) return;
            m.Dispose();
            Thread.Sleep(100);
        }

        foreach (var proc in Process.GetProcessesByName("nightverb"))
        {
            try
            {
                if (proc.Id != Environment.ProcessId && SamePath(proc.MainModule?.FileName ?? "", AppInfo.InstalledExe))
                    proc.Kill();
            }
            catch { }
            finally { proc.Dispose(); }
        }
    }

    private static void CopyWithRetry(string from, string to)
    {
        for (int i = 0; ; i++)
        {
            try { File.Copy(from, to, true); return; }
            catch (IOException) when (i < 20) { Thread.Sleep(150); }
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
