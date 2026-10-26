using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace AutopilotMod
{
    /// <summary>
    /// Mirrors the MFDs to a web page on the local network, e.g. a second computer at
    /// http://&lt;this PC&gt;:8080. Each MFD is drawn into a render texture, read back from the GPU
    /// without stalling, JPEG-encoded on a worker thread, and served by a tiny HTTP server. Clicks,
    /// drags and scrolls in the browser come back as mouse events on that MFD.
    /// Pages are only rendered while a browser is polling them.
    /// </summary>
    internal sealed class WebMirror : IDisposable
    {
        private struct Remote
        {
            public int Page;
            public EventType Type;
            public Vector2 Pos01;
            public float Wheel;
        }

        private readonly ApSettings cfg;
        private readonly ManualLogSource log;
        private readonly Action<string, bool> report;
        private readonly MfdFrame[] pages;
        private readonly RenderTexture[] rts;
        // Encoded frames live in a few reused buffers per page (no per-frame garbage: a GC pass over
        // the game's heap is a visible stutter). A buffer is rewritten only when it isn't the current
        // frame and no stream is still sending it.
        private sealed class Frame
        {
            public byte[] Data = new byte[0];
            public int Length;
            public int Readers;
        }
        private const int FramesPerPage = 4;
        private readonly Frame[][] frames;
        private readonly Frame[] current;
        private readonly NativeArray<byte>[] raw; // per-page readback copy (only one readback in flight per page)
        private readonly byte[][] rowTmp;         // per-page scratch row for the vertical flip
        private readonly int[] seq;        // bumped on each new frame; streams wait on `frameSignal` for it
        private readonly object frameSignal = new object();
        private readonly int[] busy;       // 1 while a readback/encode is in flight
        private readonly int[] lastPoll;   // Environment.TickCount of the last browser request
        private readonly ConcurrentQueue<Remote> input = new ConcurrentQueue<Remote>();
        private readonly ConcurrentQueue<string[]> targetActions = new ConcurrentQueue<string[]>(); // {action, arg}
        private volatile string targetsJson = "{\"filters\":[],\"selected\":[]}";
        private int lastTargetsPoll = Environment.TickCount - 60000;
        private float nextTargets;
        private float nextFrame;

        private TcpListener listener;
        private volatile bool running;

        // JPEG encoding runs on our own below-normal-priority threads (one per page), not the shared
        // thread pool, so it never competes with the game's worker threads.
        private readonly BlockingCollection<Action> encodeJobs = new BlockingCollection<Action>();

        public WebMirror(ApSettings cfg, ManualLogSource log, Action<string, bool> report, params MfdFrame[] pages)
        {
            this.cfg = cfg;
            this.log = log;
            this.report = report;
            this.pages = pages;
            rts = new RenderTexture[pages.Length];
            frames = new Frame[pages.Length][];
            current = new Frame[pages.Length];
            raw = new NativeArray<byte>[pages.Length];
            rowTmp = new byte[pages.Length][];
            seq = new int[pages.Length];
            busy = new int[pages.Length];
            lastPoll = new int[pages.Length];
            for (int i = 0; i < pages.Length; i++)
            {
                lastPoll[i] = Environment.TickCount - 60000;
                frames[i] = new Frame[FramesPerPage];
                for (int k = 0; k < FramesPerPage; k++) frames[i][k] = new Frame();
                new Thread(() => { foreach (Action job in encodeJobs.GetConsumingEnumerable()) job(); })
                    { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "WebMfdEncode" }.Start();
            }
        }

        /// <summary>True while a browser has asked for this page in the last few seconds.</summary>
        public bool Watching(int page) => running && unchecked(Environment.TickCount - Volatile.Read(ref lastPoll[page])) < 3000;

        // Page 1 is the radar map (see AutopilotPlugin), which gets its own, higher resolution.
        private int Size(int page) =>
            Mathf.Clamp(page == 1 ? cfg.MirrorMapResolution.Value : cfg.MirrorResolution.Value, 256, Mathf.Min(Screen.width, Screen.height));

        // =====================================================================
        // Main thread
        // =====================================================================
        /// <summary>Start / stop the server to follow the config. Call every frame.</summary>
        public void Update()
        {
            if (cfg.MirrorEnabled.Value && listener == null) Start();
            else if (!cfg.MirrorEnabled.Value && listener != null) Stop();
        }

        /// <summary>Call from OnGUI: applies browser input, and on Repaint renders watched pages.</summary>
        public void OnGUI(Action<ApCommand> execute)
        {
            if (!running) return;
            while (input.TryDequeue(out Remote r))
                pages[r.Page].RemoteMouse(r.Type, r.Pos01 * Size(r.Page), r.Wheel, Size(r.Page), execute);
            while (targetActions.TryDequeue(out string[] a))
            {
                TargetPanel.Apply(a[0], a[1]);
                nextTargets = 0f; // show the result on the next poll
            }
            if (unchecked(Environment.TickCount - Volatile.Read(ref lastTargetsPoll)) < 3000 && Time.unscaledTime >= nextTargets)
            {
                nextTargets = Time.unscaledTime + 0.25f;
                targetsJson = TargetPanel.Json();
            }

            if (Event.current.type != EventType.Repaint || Time.unscaledTime < nextFrame) return;
            nextFrame = Time.unscaledTime + 1f / Mathf.Clamp(cfg.MirrorFps.Value, 1, 60);
            bool flip = cfg.MirrorFlipVertical.Value;
            int quality = Mathf.Clamp(cfg.MirrorJpegQuality.Value, 10, 100);

            for (int i = 0; i < pages.Length; i++)
            {
                if (!Watching(i) || Volatile.Read(ref busy[i]) != 0) continue;
                int size = Size(i);
                if (rts[i] == null || rts[i].width != size)
                {
                    if (rts[i] != null) rts[i].Release();
                    rts[i] = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32) { hideFlags = HideFlags.HideAndDontSave };
                    rts[i].Create();
                }
                pages[i].RenderMirror(rts[i]);
                busy[i] = 1;
                int page = i;
                AsyncGPUReadback.Request(rts[i], 0, TextureFormat.RGBA32, req => OnReadback(req, page, size, flip, quality));
            }
        }

        private void OnReadback(AsyncGPUReadbackRequest req, int page, int size, bool flip, int quality)
        {
            if (req.hasError || !running)
            {
                Volatile.Write(ref busy[page], 0);
                return;
            }
            var data = req.GetData<byte>();
            if (!raw[page].IsCreated || raw[page].Length != data.Length)
            {
                if (raw[page].IsCreated) raw[page].Dispose();
                raw[page] = new NativeArray<byte>(data.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            NativeArray<byte> px = raw[page];
            px.CopyFrom(data);
            encodeJobs.Add(() =>
            {
                try
                {
                    if (flip) FlipRows(px, size * 4, size, page);
                    NativeArray<byte> enc = ImageConversion.EncodeNativeArrayToJPG(px, GraphicsFormat.R8G8B8A8_UNorm, (uint)size, (uint)size, 0, quality);
                    try
                    {
                        Frame f = FreeFrame(page);
                        if (f == null) return; // every buffer still being sent: drop this frame
                        if (f.Data.Length < enc.Length) f.Data = new byte[enc.Length + enc.Length / 4];
                        NativeArray<byte>.Copy(enc, 0, f.Data, 0, enc.Length);
                        f.Length = enc.Length;
                        lock (frameSignal)
                        {
                            current[page] = f;
                            seq[page]++;
                            Monitor.PulseAll(frameSignal);
                        }
                    }
                    finally
                    {
                        enc.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning($"Web MFD: JPEG encode failed: {ex.Message}");
                }
                finally
                {
                    Volatile.Write(ref busy[page], 0);
                }
            });
        }

        /// <summary>A buffer that is neither the current frame nor being sent. Encoder thread only.</summary>
        private Frame FreeFrame(int page)
        {
            foreach (Frame f in frames[page])
                if (f != current[page] && Volatile.Read(ref f.Readers) == 0) return f;
            return null;
        }

        /// <summary>Take the current frame for sending (null if none yet); pair with Release.</summary>
        private Frame Acquire(int page)
        {
            lock (frameSignal)
            {
                Frame f = current[page];
                if (f != null) Interlocked.Increment(ref f.Readers);
                return f;
            }
        }

        private static void Release(Frame f) => Interlocked.Decrement(ref f.Readers);

        private void FlipRows(NativeArray<byte> px, int stride, int rows, int page)
        {
            if (rowTmp[page] == null || rowTmp[page].Length != stride * 2) rowTmp[page] = new byte[stride * 2];
            byte[] tmp = rowTmp[page];
            for (int top = 0, bottom = rows - 1; top < bottom; top++, bottom--)
            {
                NativeArray<byte>.Copy(px, top * stride, tmp, 0, stride);
                NativeArray<byte>.Copy(px, bottom * stride, tmp, stride, stride);
                NativeArray<byte>.Copy(tmp, stride, px, top * stride, stride);
                NativeArray<byte>.Copy(tmp, 0, px, bottom * stride, stride);
            }
        }

        private void Start()
        {
            int port = cfg.MirrorPort.Value;
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
            }
            catch (Exception ex)
            {
                log.LogError($"Web MFD: can't listen on port {port}: {ex.Message}");
                cfg.MirrorEnabled.Value = false;
                listener = null;
                return;
            }
            running = true;
            new Thread(AcceptLoop) { IsBackground = true, Name = "WebMfd" }.Start();

            string ip = Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString() ?? "this-pc";
            string url = $"http://{ip}:{port}";
            log.LogInfo($"Web MFD mirror running at {url}");
            report($"WEB MFD {url}", false);
        }

        private void Stop()
        {
            running = false;
            try { listener?.Stop(); }
            catch (Exception) { /* already closed */ }
            listener = null;
        }

        public void Dispose()
        {
            Stop();
            foreach (RenderTexture rt in rts)
                if (rt != null) rt.Release();
            for (int i = 0; i < raw.Length; i++)
                if (raw[i].IsCreated && Volatile.Read(ref busy[i]) == 0) raw[i].Dispose(); // in use by an encode: leave it to process exit
        }

        // =====================================================================
        // Server threads
        // =====================================================================
        private void AcceptLoop()
        {
            TcpListener l = listener;
            while (running)
            {
                TcpClient client;
                try { client = l.AcceptTcpClient(); }
                catch (Exception) { break; } // listener stopped
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream ns = client.GetStream())
                {
                    client.ReceiveTimeout = client.SendTimeout = 5000;
                    var reader = new StreamReader(ns, Encoding.ASCII);
                    string[] request = (reader.ReadLine() ?? "").Split(' ');
                    while (!string.IsNullOrEmpty(reader.ReadLine())) { } // skip headers
                    if (request.Length < 2 || request[0] != "GET")
                    {
                        Send(ns, "405 Method Not Allowed", "text/plain", Encoding.ASCII.GetBytes("GET only"));
                        return;
                    }

                    string target = request[1];
                    int q = target.IndexOf('?');
                    string path = q < 0 ? target : target.Substring(0, q);
                    string query = q < 0 ? "" : target.Substring(q + 1);

                    switch (path)
                    {
                        case "/":
                            Send(ns, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Html));
                            break;
                        case "/frame":
                            int page = PageArg(query);
                            if (page < 0)
                            {
                                Send(ns, "400 Bad Request", "text/plain", Encoding.ASCII.GetBytes("bad page"));
                                break;
                            }
                            Volatile.Write(ref lastPoll[page], Environment.TickCount);
                            Frame one = Acquire(page);
                            if (one == null)
                            {
                                Send(ns, "503 Service Unavailable", "text/plain", Encoding.ASCII.GetBytes("no frame yet (not flying?)"));
                                break;
                            }
                            try { Send(ns, "200 OK", "image/jpeg", one.Data, one.Length); }
                            finally { Release(one); }
                            break;
                        case "/stream":
                            int sp = PageArg(query);
                            if (sp < 0) Send(ns, "400 Bad Request", "text/plain", Encoding.ASCII.GetBytes("bad page"));
                            else ServeStream(ns, sp);
                            break;
                        case "/targets":
                            Volatile.Write(ref lastTargetsPoll, Environment.TickCount);
                            Send(ns, "200 OK", "application/json", Encoding.UTF8.GetBytes(targetsJson));
                            break;
                        case "/target":
                            string act = Arg(query, "a");
                            bool known = act == "toggle" || act == "only" || act == "reset" || act == "clear" || act == "desel" || act == "sel";
                            if (known && targetActions.Count < 64) targetActions.Enqueue(new[] { act, Arg(query, "i") ?? "" });
                            Send(ns, known ? "204 No Content" : "400 Bad Request", "text/plain", new byte[0]);
                            break;
                        case "/input":
                            bool ok = EnqueueInput(query);
                            Send(ns, ok ? "204 No Content" : "400 Bad Request", "text/plain", new byte[0]);
                            break;
                        default:
                            Send(ns, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("not found"));
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // client went away mid-request; nothing to clean up
            }
        }

        /// <summary>
        /// MJPEG push: one connection per page, each new frame sent once as it is encoded (no
        /// per-frame request, no duplicate frames). Runs until the browser disconnects.
        /// </summary>
        private void ServeStream(Stream s, int page)
        {
            byte[] head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=mfdframe\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            s.Write(head, 0, head.Length);
            int sent = -1;
            while (running)
            {
                Volatile.Write(ref lastPoll[page], Environment.TickCount); // keeps the page rendering
                lock (frameSignal)
                {
                    if (seq[page] == sent) Monitor.Wait(frameSignal, 500);
                    if (seq[page] == sent) continue;
                    sent = seq[page];
                }
                Frame f = Acquire(page);
                if (f == null) continue;
                try
                {
                    byte[] part = Encoding.ASCII.GetBytes($"--mfdframe\r\nContent-Type: image/jpeg\r\nContent-Length: {f.Length}\r\n\r\n");
                    s.Write(part, 0, part.Length);
                    s.Write(f.Data, 0, f.Length);
                    s.Write(Crlf, 0, 2);
                    s.Flush();
                }
                finally
                {
                    Release(f);
                }
            }
        }

        private static readonly byte[] Crlf = { 13, 10 };

        private static void Send(Stream s, string status, string type, byte[] body, int length = -1)
        {
            if (length < 0) length = body.Length;
            byte[] head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            s.Write(head, 0, head.Length);
            s.Write(body, 0, length);
        }

        private static string Arg(string query, string key)
        {
            foreach (string kv in query.Split('&'))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0 && kv.Substring(0, eq) == key) return Uri.UnescapeDataString(kv.Substring(eq + 1));
            }
            return null;
        }

        private int PageArg(string query) =>
            int.TryParse(Arg(query, "p"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p >= 0 && p < pages.Length ? p : -1;

        private static float FloatArg(string query, string key, float fallback) =>
            float.TryParse(Arg(query, key), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && !float.IsNaN(v) ? v : fallback;

        /// <summary>Validate browser input (it comes from the network) and queue it for the main thread.</summary>
        private bool EnqueueInput(string query)
        {
            int page = PageArg(query);
            EventType type;
            switch (Arg(query, "t"))
            {
                case "down": type = EventType.MouseDown; break;
                case "drag": type = EventType.MouseDrag; break;
                case "up": type = EventType.MouseUp; break;
                case "wheel": type = EventType.ScrollWheel; break;
                default: return false;
            }
            if (page < 0 || input.Count > 256) return false;
            input.Enqueue(new Remote
            {
                Page = page,
                Type = type,
                Pos01 = new Vector2(Mathf.Clamp01(FloatArg(query, "x", 0.5f)), Mathf.Clamp01(FloatArg(query, "y", 0.5f))),
                Wheel = Mathf.Clamp(FloatArg(query, "w", 0f), -1f, 1f),
            });
            return true;
        }

        private const string Html = @"<!doctype html>
<html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>MFD Mirror</title>
<style>
html,body{margin:0;height:100%;background:#000;overflow:hidden}
body{display:flex;font:12px Consolas,monospace;color:#5cff73}
#mfds{display:flex;align-items:flex-start;gap:4px;flex:none}
img{--s:min(calc((100vw - 300px) / 2), 100vh);width:var(--s);height:var(--s);touch-action:none;user-select:none;-webkit-user-drag:none;background:#111}
#tp{flex:1;min-width:0;height:100%;overflow-y:auto;padding:44px 8px 8px;box-sizing:border-box}
@media (orientation:portrait){body{flex-direction:column}img{--s:calc(50vw - 2px)}#tp{height:auto;flex:1;padding-top:8px}}
.row{margin:0 0 4px}
.gh{display:block;margin:6px 0 2px;font-size:11px;color:#349442}
button{font:inherit;margin:1px;padding:3px 7px;min-height:28px;background:#111;color:#666;border:1px solid #333;border-radius:3px;cursor:pointer}
button.on{color:#5cff73;border-color:#5cff73}
button.act{color:#ffbd33;border-color:#ffbd33}
button.sel{color:#fff;border-color:#ff4a3d}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:3px}
.grid button{display:flex;align-items:center;gap:6px;width:100%;height:28px;margin:0;padding:0 6px;white-space:nowrap}
.grid .n{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;text-align:left}
.grid .d{flex:none;width:7ch;text-align:right;font-variant-numeric:tabular-nums}
.none{color:#349442}
#ex{position:fixed;top:6px;right:6px;z-index:1;color:#5cff73;border-color:#5cff73;background:#111c}
body.big #m0,body.big #tp{display:none}
body.big #mfds{flex:1;justify-content:center}
body.big #m1{--s:min(100vw,100vh)}
</style></head><body>
<button id=""ex"" title=""Expand / restore the map MFD (Esc restores)"">EXPAND MAP</button>
<div id=""mfds""><img id=""m0"" alt=""A/P MFD""><img id=""m1"" alt=""Radar coverage MFD""></div>
<div id=""tp""><div id=""flt""></div>
<span class=""gh"">SELECTED</span><div id=""sel"" class=""grid""></div>
<span class=""gh"">AVAILABLE (NEAREST FIRST)</span><div id=""con"" class=""grid""></div>
<p class=""none"">Filter: tap = toggle, right-click / long-press = only that one. Target: tap = add / remove.</p></div>
<script>
const act = (a, i = '') => fetch(`/target?a=${a}&i=${i}`).then(refresh).catch(() => {});
const btn = (text, cls, onclick, only) => {
  const b = document.createElement('button');
  b.textContent = text; b.className = cls; b.onclick = onclick;
  if (only) b.oncontextmenu = e => { e.preventDefault(); only(); };
  return b;
};
// fixed-size target cell: name (ellipsised) + distance in a fixed-width column
const tgt = (name, dist, cls, onclick) => {
  const b = btn('', cls, onclick), n = document.createElement('span'), d = document.createElement('span');
  n.className = 'n'; n.textContent = name; d.className = 'd'; d.textContent = dist;
  b.title = name; b.append(n, d);
  return b;
};
const none = t => Object.assign(document.createElement('span'), { className: 'none', textContent: t });
let last = '';
function refresh() {
  return fetch('/targets').then(r => r.text()).then(t => {
    if (t === last) return;
    last = t;
    const s = JSON.parse(t), flt = document.getElementById('flt');
    flt.replaceChildren();
    let row = null, g = null;
    for (const f of s.filters) {
      if (f.g !== g) {
        g = f.g; row = document.createElement('div'); row.className = 'row';
        row.append(Object.assign(document.createElement('span'), { className: 'gh', textContent: g }));
        flt.append(row);
      }
      row.append(btn(f.l, f.on ? 'on' : '', () => act('toggle', f.i), () => act('only', f.i)));
    }
    const ar = document.createElement('div'); ar.className = 'row';
    ar.append(btn('RESET FILTERS', 'act', () => act('reset')), btn('CLEAR SELECTION', 'act', () => act('clear')));
    flt.append(ar);
    document.getElementById('sel').replaceChildren(...(s.selected.length
      ? s.selected.map(u => tgt(u.n, '\u2715', 'sel', () => act('desel', u.u))) : [none('none')]));
    document.getElementById('con').replaceChildren(...(s.contacts.length
      ? s.contacts.map(c => tgt((c.s ? '\u2713 ' : '+ ') + c.n, c.d + ' km', c.s ? 'sel' : 'on', () => act(c.s ? 'desel' : 'sel', c.u)))
      : [none('no enemies shown on the map')]));
  }).catch(() => {});
}
const ex = document.getElementById('ex');
const loads = [], big = () => document.body.classList.contains('big');
// while expanded the A/P image is hidden: drop its stream so the game stops rendering and encoding it
const expand = on => {
  if (on === big()) return;
  document.body.classList.toggle('big', on); ex.textContent = on ? 'RESTORE' : 'EXPAND MAP';
  if (on) document.getElementById('m0').removeAttribute('src'); else loads[0]();
};
ex.onclick = () => expand(!big());
addEventListener('keydown', e => { if (e.key === 'Escape') expand(false); });
setInterval(refresh, 400);
refresh();
for (let p = 0; p < 2; p++) {
  const img = document.getElementById('m' + p);
  const load = loads[p] = () => { if (p === 0 && big()) return; img.src = '/stream?p=' + p + '&t=' + Date.now(); };
  img.onerror = () => setTimeout(load, 1000);
  load();
  const send = (t, e, w = 0) => {
    const r = img.getBoundingClientRect();
    const x = ((e.clientX - r.left) / r.width).toFixed(4), y = ((e.clientY - r.top) / r.height).toFixed(4);
    fetch(`/input?p=${p}&t=${t}&x=${x}&y=${y}&w=${w}`).catch(() => {});
  };
  let down = false, lastDrag = 0;
  img.addEventListener('pointerdown', e => { down = true; img.setPointerCapture(e.pointerId); send('down', e); e.preventDefault(); });
  img.addEventListener('pointermove', e => {
    if (!down || e.timeStamp - lastDrag < 33) return;
    lastDrag = e.timeStamp; send('drag', e);
  });
  const up = e => { if (down) { down = false; send('up', e); } };
  img.addEventListener('pointerup', up);
  img.addEventListener('pointercancel', up);
  img.addEventListener('wheel', e => { send('wheel', e, Math.sign(e.deltaY)); e.preventDefault(); }, { passive: false });
}
</script></body></html>";
    }
}
