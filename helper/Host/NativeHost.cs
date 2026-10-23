using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Nightverb.Util;

namespace Nightverb.Host;

/// <summary>
/// What Firefox launches when the extension talks to "nightverb".
/// A thin relay: Firefox native-messaging frames on stdin/stdout  <->  JSON lines on the engine pipe.
/// Starts the engine (detached from Firefox) if it isn't running yet.
/// </summary>
internal static class NativeHost
{
    private static readonly object StdoutLock = new();

    public static int Run()
    {
        Log.Role = "host";
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        int firefoxPid = Native.FindFirefoxMainAncestor(Environment.ProcessId);

        NamedPipeClientStream? pipe = TryConnect(300);
        if (pipe == null)
        {
            try
            {
                // Two hops: the middle process exits right away, so the engine ends up
                // with no living parent and Windows doesn't count it as part of Firefox.
                Native.StartDetached(Environment.ProcessPath!, $"--spawn-engine --firefox {firefoxPid}");
            }
            catch (Exception e)
            {
                Log.Write("spawn failed: " + e.Message);
            }

            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (pipe == null && DateTime.UtcNow < deadline) pipe = TryConnect(250);
        }

        if (pipe == null)
        {
            Log.Write("could not reach engine");
            WriteFrame(stdout, new JsonObject { ["type"] = "error", ["error"] = "engine-unreachable" }.ToJsonString());
            return 1;
        }

        using (pipe)
        {
            WriteLine(pipe, new JsonObject { ["type"] = "hello", ["pid"] = firefoxPid, ["from"] = "host" }.ToJsonString());

            var toEngine = Task.Run(() => StdinToPipe(stdin, pipe));
            var toFirefox = Task.Run(() => PipeToStdout(pipe, stdout));
            Task.WaitAny(toEngine, toFirefox);
        }
        return 0;
    }

    private static NamedPipeClientStream? TryConnect(int timeoutMs)
    {
        var p = new NamedPipeClientStream(".", AppInfo.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            p.Connect(timeoutMs);
            return p;
        }
        catch
        {
            p.Dispose();
            Thread.Sleep(50);
            return null;
        }
    }

    private static void StdinToPipe(Stream stdin, Stream pipe)
    {
        var lenBuf = new byte[4];
        try
        {
            while (true)
            {
                if (!ReadExactly(stdin, lenBuf, 4)) return; // Firefox closed the port
                int len = BitConverter.ToInt32(lenBuf, 0);
                if (len <= 0 || len > 1024 * 1024) return;
                var payload = new byte[len];
                if (!ReadExactly(stdin, payload, len)) return;
                // JSON from the extension is always single-line (JSON.stringify).
                pipe.Write(payload, 0, len);
                pipe.WriteByte((byte)'\n');
                pipe.Flush();
            }
        }
        catch (Exception e)
        {
            Log.Write("stdin relay ended: " + e.Message);
        }
    }

    private static void PipeToStdout(Stream pipe, Stream stdout)
    {
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length > 0) WriteFrame(stdout, line);
            }
        }
        catch (Exception e)
        {
            Log.Write("pipe relay ended: " + e.Message);
        }
    }

    private static void WriteLine(Stream pipe, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        pipe.Write(bytes, 0, bytes.Length);
        pipe.Flush();
    }

    private static void WriteFrame(Stream stdout, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        lock (StdoutLock)
        {
            stdout.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
    }

    private static bool ReadExactly(Stream s, byte[] buf, int count)
    {
        int off = 0;
        while (off < count)
        {
            int n = s.Read(buf, off, count - off);
            if (n <= 0) return false;
            off += n;
        }
        return true;
    }
}
