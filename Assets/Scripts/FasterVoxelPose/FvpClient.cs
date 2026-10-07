using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;

public sealed class FvpMessage
{
    public string type;                    // server message type, or "link_up" / "link_down" from the client itself
    public Dictionary<string, object> json;
    public byte[] bin;

    public double Num(string key, double fallback = 0)
    {
        return json != null && json.TryGetValue(key, out object v) && v is double d ? d : fallback;
    }

    public string Str(string key)
    {
        return json != null && json.TryGetValue(key, out object v) ? v as string : null;
    }
}

// Localhost link to fvp_server.py. Connects (and reconnects) on a background thread, reads replies on it,
// and writes on a second thread so a stalled server can never block the main thread. The owner polls Inbox.
public sealed class FvpClient : IDisposable
{
    sealed class Outgoing
    {
        public string json;
        public IList<ArraySegment<byte>> parts;
        public Action done; // always called: sent, or dropped because the link is down
    }

    readonly string host;
    readonly int port;
    readonly ConcurrentQueue<FvpMessage> inbox = new ConcurrentQueue<FvpMessage>();
    readonly BlockingCollection<Outgoing> outgoing = new BlockingCollection<Outgoing>(new ConcurrentQueue<Outgoing>(), 8);
    readonly object writeLock = new object();
    readonly Action<string> log;

    Thread ioThread, sendThread;
    volatile bool stop;
    TcpClient tcp;
    NetworkStream stream;
    int connected;

    public bool IsConnected => Volatile.Read(ref connected) == 1;
    public ConcurrentQueue<FvpMessage> Inbox => inbox;

    public FvpClient(string host, int port, Action<string> log = null)
    {
        this.host = host; this.port = port; this.log = log ?? (_ => { });
    }

    public void Start()
    {
        if (ioThread != null) return;
        ioThread = new Thread(IoLoop) { IsBackground = true, Name = "FVP io" };
        sendThread = new Thread(SendLoop) { IsBackground = true, Name = "FVP send" };
        ioThread.Start();
        sendThread.Start();
    }

    // Queues a packet. Returns false (and runs done) if the link is down or the queue is full.
    public bool Send(string json, IList<ArraySegment<byte>> parts, Action done)
    {
        if (!IsConnected || stop) { done?.Invoke(); return false; }
        var o = new Outgoing { json = json, parts = parts, done = done };
        try
        {
            if (outgoing.TryAdd(o)) return true;
        }
        catch (InvalidOperationException) { } // completed by Dispose
        done?.Invoke();
        return false;
    }

    void IoLoop()
    {
        while (!stop)
        {
            TcpClient c = null;
            try
            {
                c = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20, SendBufferSize = 1 << 22 };
                ConnectWithTimeout(c);
                NetworkStream s = c.GetStream();
                lock (writeLock) { tcp = c; stream = s; }
                Volatile.Write(ref connected, 1);
                inbox.Enqueue(new FvpMessage { type = "link_up" });

                while (!stop)
                {
                    if (!FvpProtocol.ReadPacket(s, out string json, out byte[] bin)) break;
                    var d = MiniJson.Parse(json) as Dictionary<string, object>;
                    string type = d != null && d.TryGetValue("type", out object t) ? t as string : null;
                    inbox.Enqueue(new FvpMessage { type = type ?? "unknown", json = d, bin = bin });
                }
            }
            catch (Exception e)
            {
                if (!stop && IsConnected) log("link lost: " + e.Message);
            }
            finally
            {
                bool wasUp = Interlocked.Exchange(ref connected, 0) == 1;
                lock (writeLock)
                {
                    stream = null;
                    tcp = null;
                }
                try { c?.Close(); } catch { }
                if (wasUp) inbox.Enqueue(new FvpMessage { type = "link_down" });
            }
            if (!stop) Thread.Sleep(500);
        }
    }

    // TcpClient.Connect to a closed port can take seconds on Windows, so it gets a timeout of its own.
    void ConnectWithTimeout(TcpClient c)
    {
        IAsyncResult ar = c.BeginConnect(host, port, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(2000))
        {
            try { c.Close(); } catch { }
            throw new TimeoutException("connect timeout");
        }
        c.EndConnect(ar);
    }

    void SendLoop()
    {
        try
        {
            foreach (Outgoing o in outgoing.GetConsumingEnumerable())
            {
                try
                {
                    NetworkStream s;
                    lock (writeLock) s = stream;
                    if (s != null && !stop)
                    {
                        try { FvpProtocol.WritePacket(s, o.json, o.parts); }
                        catch (Exception e)
                        {
                            log("send failed: " + e.Message);
                            try { tcp?.Close(); } catch { } // wakes the reader, which reconnects
                        }
                    }
                }
                finally { o.done?.Invoke(); }
            }
        }
        catch (Exception e) { log("send thread: " + e); }
    }

    public void Dispose()
    {
        stop = true;
        try { outgoing.CompleteAdding(); } catch { }
        lock (writeLock) { try { tcp?.Close(); } catch { } }
        ioThread?.Join(300);
        sendThread?.Join(300);
        // Anything still queued never reaches the wire: release it so frame buffers are not leaked.
        while (outgoing.TryTake(out Outgoing o)) o.done?.Invoke();
    }
}
