using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Nightverb.Audio;
using Nightverb.Util;

namespace Nightverb.Engine;

/// <summary>
/// The long-lived engine process. Owns the audio engine and serves the
/// extension (through the native host relay) over a per-user named pipe.
/// Protocol: one JSON object per line, both directions.
/// </summary>
internal sealed class EngineServer
{
    public static int Run(int firefoxPid)
    {
        Log.Role = "engine";
        using var mutex = new Mutex(true, AppInfo.MutexName, out bool created);
        if (!created)
        {
            Log.Write("engine already running, exiting");
            return 0;
        }

        Log.Write($"engine start v{AppInfo.Version}, firefox pid {firefoxPid}");
        var settings = Settings.Load();
        using var engine = new AudioEngine();
        engine.Configure(settings.Enabled, settings.Params);
        if (firefoxPid == 0) firefoxPid = Native.FindAnyFirefoxMain();
        engine.SetTarget(firefoxPid);
        engine.Start();

        var server = new EngineServer(engine, settings);
        bool respawn = server.RunAsync().GetAwaiter().GetResult();

        Log.Write("engine stopping" + (respawn ? " (respawning)" : ""));
        settings.Save();
        engine.Dispose();
        mutex.ReleaseMutex();

        if (respawn)
        {
            try { Native.StartDetached(Environment.ProcessPath!, $"--spawn-engine --firefox {engine.TargetPid}"); }
            catch (Exception e) { Log.Write("respawn failed: " + e.Message); }
        }
        return 0;
    }

    private readonly AudioEngine _engine;
    private readonly Settings _settings;
    private readonly ConcurrentDictionary<int, Client> _clients = new();
    private readonly CancellationTokenSource _quit = new();
    private int _nextId;
    private bool _respawn;
    private int _saveVersion;

    private EngineServer(AudioEngine engine, Settings settings)
    {
        _engine = engine;
        _settings = settings;
        _engine.StatusChanged += () => Broadcast(StateMessage());
    }

    /// <returns>true if the engine should restart itself.</returns>
    private async Task<bool> RunAsync()
    {
        var accept = AcceptLoopAsync(_quit.Token);
        var meters = MeterLoopAsync(_quit.Token);
        var watch = WatchLoopAsync(_quit.Token);
        try { await Task.WhenAny(accept, watch); } catch { }
        _quit.Cancel();
        try { await Task.WhenAll(accept, meters, watch); } catch { }
        foreach (var c in _clients.Values) c.Dispose();
        return _respawn;
    }

    // ------------------------------------------------------------------ pipe

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(AppInfo.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception e)
            {
                Log.Write("pipe create failed: " + e.Message);
                await Task.Delay(1000, ct);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch
            {
                pipe.Dispose();
                if (ct.IsCancellationRequested) return;
                continue;
            }

            var client = new Client(Interlocked.Increment(ref _nextId), pipe);
            _clients[client.Id] = client;
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(Client client, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(client.Pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (line.Length == 0) continue;
                JsonObject? msg;
                try { msg = JsonNode.Parse(line) as JsonObject; }
                catch { client.Send(Error("bad-json")); continue; }
                if (msg == null) continue;
                try { Handle(client, msg); }
                catch (Exception e)
                {
                    Log.Write("bad message: " + e.Message);
                    client.Send(Error("bad-message"));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception e) { Log.Write("client error: " + e.Message); }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            client.Dispose();
        }
    }

    private void Handle(Client client, JsonObject msg)
    {
        string type = msg["type"]?.GetValue<string>() ?? "";
        switch (type)
        {
            case "hello":
                // Follow a new Firefox only if the one we're listening to is gone
                // (two Firefox profiles open at once: the first keeps the reverb).
                if (msg["pid"] is JsonValue pv && pv.TryGetValue<int>(out int pid) && pid > 0 &&
                    pid != _engine.TargetPid && !IsAlive(_engine.TargetPid))
                    _engine.SetTarget(pid);
                client.Send(StateMessage());
                break;

            case "get":
                client.Send(StateMessage());
                break;

            case "set":
                bool enabled = msg["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out bool e) ? e : _settings.Enabled;
                var p = _settings.Params.Merge(msg["params"] as JsonObject);
                _settings.Enabled = enabled;
                _settings.Params = p;
                _engine.Configure(enabled, p);
                ScheduleSave();
                Broadcast(StateMessage());
                break;

            case "meters":
                client.WantsMeters = msg["on"]?.GetValue<bool>() ?? true;
                break;

            case "ping":
                client.Send(new JsonObject { ["type"] = "pong" });
                break;

            case "quit":
                Log.Write("quit requested");
                _quit.Cancel();
                break;

            default:
                client.Send(Error("unknown-type"));
                break;
        }
    }

    private JsonObject StateMessage() => new()
    {
        ["type"] = "state",
        ["version"] = AppInfo.Version,
        ["enabled"] = _settings.Enabled,
        ["params"] = _settings.Params.ToJson(),
        ["status"] = _engine.State switch
        {
            AudioEngine.EngineState.Off => "off",
            AudioEngine.EngineState.Starting => "starting",
            AudioEngine.EngineState.Running => "running",
            AudioEngine.EngineState.WaitingForFirefox => "waiting",
            _ => "error",
        },
        ["error"] = _engine.Error,
        ["device"] = _engine.DeviceName,
        ["sampleRate"] = _engine.SampleRate,
        ["latencyMs"] = Math.Round(_engine.LatencyMs),
        ["firefoxPid"] = _engine.TargetPid,
    };

    private static JsonObject Error(string code) => new() { ["type"] = "error", ["error"] = code };

    private void Broadcast(JsonObject msg)
    {
        string line = msg.ToJsonString();
        foreach (var c in _clients.Values) c.SendRaw(line);
    }

    private void ScheduleSave()
    {
        int v = Interlocked.Increment(ref _saveVersion);
        _ = Task.Delay(600).ContinueWith(_ =>
        {
            if (v == Volatile.Read(ref _saveVersion)) _settings.Save();
        });
    }

    // ------------------------------------------------------------------ meters

    private async Task MeterLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                bool any = false;
                foreach (var c in _clients.Values) if (c.WantsMeters) { any = true; break; }
                if (!any) continue;
                var (input, output) = _engine.TakePeaks();
                string line = new JsonObject
                {
                    ["type"] = "meter",
                    ["in"] = Math.Round(input, 4),
                    ["out"] = Math.Round(output, 4),
                    ["underruns"] = _engine.Underruns,
                }.ToJsonString();
                foreach (var c in _clients.Values) if (c.WantsMeters) c.SendRaw(line);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ lifetime

    /// <summary>
    /// Follows Firefox: retargets if it restarts, quits when it's gone for good.
    /// Also makes sure Windows never sees this engine as part of Firefox's
    /// process tree (that would make it hear its own reverb).
    /// </summary>
    private async Task WatchLoopAsync(CancellationToken ct)
    {
        int ourParent = 0;
        var startSnapshot = Native.SnapshotProcesses();
        if (startSnapshot.TryGetValue(Environment.ProcessId, out var me)) ourParent = me.ParentPid;

        DateTime? goneSince = null;
        int knownTarget = 0;
        string? knownExe = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var procs = Native.SnapshotProcesses();

                // Remember which program the target PID belonged to, so a PID that gets
                // reused by something else after Firefox exits isn't mistaken for it.
                int target = _engine.TargetPid;
                if (target != knownTarget)
                {
                    knownTarget = target;
                    knownExe = procs.TryGetValue(target, out var first) ? first.Exe : null;
                }
                bool alive = target != 0 && knownExe != null && procs.TryGetValue(target, out var t) &&
                             t.Exe.Equals(knownExe, StringComparison.OrdinalIgnoreCase);
                if (!alive)
                {
                    int other = Native.FindAnyFirefoxMain();
                    if (other != 0)
                    {
                        _engine.SetTarget(other);
                        goneSince = null;
                    }
                    else
                    {
                        goneSince ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - goneSince > TimeSpan.FromSeconds(8))
                        {
                            Log.Write("Firefox closed, engine exits");
                            return;
                        }
                    }
                }
                else goneSince = null;

                // PID-reuse guard: our launcher process is gone; if its PID now
                // belongs to something inside Firefox, restart detached again.
                if (ourParent != 0 && target != 0 && IsInTree(procs, ourParent, target))
                {
                    Log.Write($"parent pid {ourParent} was reused inside Firefox's tree");
                    _respawn = true;
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static bool IsAlive(int pid)
    {
        if (pid == 0) return false;
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static bool IsInTree(System.Collections.Generic.Dictionary<int, Native.ProcInfo> procs, int pid, int root)
    {
        int cur = pid;
        for (int depth = 0; depth < 32 && procs.TryGetValue(cur, out var p); depth++)
        {
            if (cur == root) return true;
            if (p.ParentPid == cur || p.ParentPid == 0) break;
            cur = p.ParentPid;
        }
        return false;
    }

    // ------------------------------------------------------------------ client

    private sealed class Client : IDisposable
    {
        public readonly int Id;
        public readonly NamedPipeServerStream Pipe;
        public volatile bool WantsMeters;
        private readonly object _writeLock = new();
        private bool _dead;

        public Client(int id, NamedPipeServerStream pipe) { Id = id; Pipe = pipe; }

        public void Send(JsonObject msg) => SendRaw(msg.ToJsonString());

        public void SendRaw(string line)
        {
            lock (_writeLock)
            {
                if (_dead) return;
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    Pipe.Write(bytes, 0, bytes.Length);
                    Pipe.Flush();
                }
                catch { _dead = true; }
            }
        }

        public void Dispose()
        {
            lock (_writeLock) { _dead = true; }
            try { Pipe.Dispose(); } catch { }
        }
    }
}

/// <summary>Engine settings persisted between runs.</summary>
internal sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public ReverbParams Params { get; set; } = ReverbParams.Default;
    private readonly object _lock = new();

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (File.Exists(AppInfo.SettingsPath))
            {
                var o = JsonNode.Parse(File.ReadAllText(AppInfo.SettingsPath)) as JsonObject;
                if (o != null)
                {
                    if (o["enabled"] is JsonValue v && v.TryGetValue<bool>(out bool b)) s.Enabled = b;
                    s.Params = ReverbParams.Default.Merge(o["params"] as JsonObject);
                }
            }
        }
        catch (Exception e) { Log.Write("settings load failed: " + e.Message); }
        return s;
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(AppInfo.DataDir);
                var o = new JsonObject { ["enabled"] = Enabled, ["params"] = Params.ToJson() };
                string tmp = AppInfo.SettingsPath + ".tmp";
                File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, AppInfo.SettingsPath, true);
            }
            catch (Exception e) { Log.Write("settings save failed: " + e.Message); }
        }
    }
}
