using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace MCPX
{
    /// <summary>
    /// Editor-side HTTP server (read-only, phase 1).
    /// Listens on 127.0.0.1 only. Requests are executed on the Unity main thread
    /// through EditorApplication.update.
    /// </summary>
    [InitializeOnLoad]
    public static class MCPXServer
    {
        public const int Port = 8765;

        static HttpListener _listener;
        static readonly ConcurrentQueue<PendingRequest> _queue = new ConcurrentQueue<PendingRequest>();

        class PendingRequest
        {
            public string Method;
            public string Path;
            public System.Collections.Specialized.NameValueCollection Query;
            public TaskCompletionSource<MCPXResult> Tcs;
        }

        static MCPXServer()
        {
            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            Application.logMessageReceivedThreaded += MCPXLogBuffer.Add;
            Start();
        }

        [MenuItem("Tools/MCPX/Restart Server")]
        static void Restart()
        {
            Stop();
            Start();
        }

        static void Start()
        {
            if (_listener != null) return;
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                _listener.BeginGetContext(OnContext, null);
                Debug.Log($"[MCPX] listening on http://127.0.0.1:{Port}/");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MCPX] failed to start on port {Port}: {e.Message}");
                _listener = null;
            }
        }

        static void Stop()
        {
            if (_listener == null) return;
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception) { }
            _listener = null;
        }

        static void OnContext(IAsyncResult ar)
        {
            var listener = _listener;
            if (listener == null) return;

            HttpListenerContext ctx;
            try
            {
                ctx = listener.EndGetContext(ar);
            }
            catch (Exception)
            {
                return;
            }

            try
            {
                listener.BeginGetContext(OnContext, null);
            }
            catch (Exception) { }

            Handle(ctx);
        }

        static void Handle(HttpListenerContext ctx)
        {
            var req = new PendingRequest
            {
                Method = ctx.Request.HttpMethod,
                Path = ctx.Request.Url.AbsolutePath,
                Query = ctx.Request.QueryString,
                Tcs = new TaskCompletionSource<MCPXResult>()
            };
            _queue.Enqueue(req);

            MCPXResult res;
            // This runs on a thread-pool thread, so blocking here is acceptable.
            if (req.Tcs.Task.Wait(TimeSpan.FromSeconds(10)))
                res = req.Tcs.Task.Result;
            else
                res = MCPXResult.Error(504, "timeout waiting for Unity main thread");

            try
            {
                var buf = Encoding.UTF8.GetBytes(res.Body);
                ctx.Response.StatusCode = res.Status;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = buf.Length;
                ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception) { }
        }

        static void Pump()
        {
            while (_queue.TryDequeue(out var req))
            {
                try
                {
                    req.Tcs.TrySetResult(MCPXRoutes.Dispatch(req.Method, req.Path, req.Query));
                }
                catch (Exception e)
                {
                    req.Tcs.TrySetResult(MCPXResult.Error(500, e.Message));
                }
            }
        }
    }

    public class MCPXResult
    {
        public int Status;
        public string Body;

        public static MCPXResult Ok(string json)
        {
            return new MCPXResult { Status = 200, Body = json };
        }

        public static MCPXResult Error(int status, string message)
        {
            return new MCPXResult { Status = status, Body = "{\"error\":" + MCPXJson.Str(message) + "}" };
        }
    }

    /// <summary>Ring buffer of recent console logs, exposed via /logs.</summary>
    public static class MCPXLogBuffer
    {
        const int Max = 300;

        struct Entry
        {
            public long Id;
            public string Type;
            public string Message;
            public string Stack;
        }

        static readonly object _lock = new object();
        static readonly Queue<Entry> _entries = new Queue<Entry>();
        static long _nextId = 1;

        /// <summary>Id of the most recently added entry (0 if none yet).</summary>
        public static long LastId
        {
            get { lock (_lock) return _nextId - 1; }
        }

        public static void Add(string condition, string stack, LogType type)
        {
            lock (_lock)
            {
                _entries.Enqueue(new Entry { Id = _nextId++, Type = type.ToString(), Message = condition, Stack = stack });
                while (_entries.Count > Max) _entries.Dequeue();
            }
        }

        /// <summary>Returns up to `count` entries with Id greater than `since`, oldest first.</summary>
        public static string ToJson(int count, long since)
        {
            var sb = new StringBuilder("{\"lastId\":");
            var list = new List<Entry>();
            lock (_lock)
            {
                sb.Append(_nextId - 1).Append(",\"logs\":[");
                foreach (var e in _entries)
                    if (e.Id > since) list.Add(e);
            }
            int start = Math.Max(0, list.Count - Math.Max(1, count));
            for (int i = start; i < list.Count; i++)
            {
                var e = list[i];
                if (i > start) sb.Append(',');
                sb.Append("{\"id\":").Append(e.Id)
                  .Append(",\"type\":").Append(MCPXJson.Str(e.Type))
                  .Append(",\"message\":").Append(MCPXJson.Str(e.Message))
                  .Append(",\"stack\":").Append(MCPXJson.Str(e.Stack))
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }
}
