using System;
using System.Linq;
using Nightverb.Diagnostics;
using Nightverb.Engine;
using Nightverb.Host;
using Nightverb.Setup;
using Nightverb.Util;

namespace Nightverb;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        try
        {
            // Firefox starts native hosts as: nightverb.exe <path-to-manifest.json> <extension-id>
            if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
                return NativeHost.Run();

            string mode = args.Length > 0 ? args[0] : "--install";
            bool quiet = args.Contains("--quiet");
            int firefoxPid = int.TryParse(ArgAfter(args, "--firefox"), out int p) ? p : 0;

            switch (mode)
            {
                case "--install":
                    return Installer.Install(quiet);

                case "--uninstall":
                    return Installer.Uninstall(quiet);

                case "--engine":
                    return EngineServer.Run(firefoxPid);

                case "--spawn-engine":
                    // Middle hop: start the engine and exit at once, so the engine is orphaned
                    // and no longer counts as a child of Firefox for audio capture.
                    Log.Role = "spawn";
                    Native.StartDetached(Environment.ProcessPath!, $"--engine --firefox {firefoxPid}");
                    return 0;

                case "--selftest":
                    Native.AttachConsole(-1);
                    return SelfTest.Run(ArgAfter(args, "--selftest"));

                case "--probe":
                    Native.AttachConsole(-1);
                    return SelfTest.Probe(int.TryParse(ArgAfter(args, "--probe"), out int s) ? s : 6,
                        int.TryParse(ArgAfter(args, "--pid"), out int probePid) ? probePid : 0);

                case "--level-table":
                    Native.AttachConsole(-1);
                    return SelfTest.LevelTable();

                case "--stop":
                    Installer.StopEngine();
                    return 0;

                default:
                    Native.AttachConsole(-1);
                    Console.WriteLine("nightverb.exe [--install | --uninstall | --stop | --selftest [wavDir] | --probe [seconds]]");
                    return 2;
            }
        }
        catch (Exception e)
        {
            Log.Write("fatal: " + e);
            return 1;
        }
    }

    private static string? ArgAfter(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null;
    }
}
