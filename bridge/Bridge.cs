using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace SpicetifyRemoteApi
{
    internal sealed class ApiException : Exception
    {
        public readonly int Status;

        public ApiException(int status, string message) : base(message)
        {
            Status = status;
        }
    }

    internal static class Json
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        public static object Parse(string text)
        {
            return Serializer.DeserializeObject(text);
        }

        public static string Write(object value)
        {
            return Serializer.Serialize(value);
        }

        public static Dictionary<string, object> Obj(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static object Get(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value : null;
        }

        public static double Num(object value)
        {
            try
            {
                return value == null ? 0 : Convert.ToDouble(value);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    internal sealed class HttpRequest
    {
        public string Method;
        public string Path;
        public readonly Dictionary<string, string> Query = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public string Header(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }

        public static HttpRequest Read(Stream input)
        {
            var head = new MemoryStream();
            int tail = 0;
            while (tail != 0x0D0A0D0A)
            {
                int b = input.ReadByte();
                if (b < 0) return null;
                head.WriteByte((byte)b);
                if (head.Length > 16384) throw new ApiException(431, "headers too large");
                tail = (tail << 8) | b;
            }
            string[] lines = Encoding.UTF8.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            string[] start = lines[0].Split(' ');
            if (start.Length < 2) throw new ApiException(400, "bad request");
            var request = new HttpRequest { Method = start[0].ToUpperInvariant() };
            string target = start[1];
            int mark = target.IndexOf('?');
            request.Path = mark >= 0 ? target.Substring(0, mark) : target;
            if (request.Path.Length > 1) request.Path = request.Path.TrimEnd('/');
            if (mark >= 0) ParseQuery(target.Substring(mark + 1), request.Query);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) request.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }
            int length;
            if (int.TryParse(request.Header("Content-Length"), out length) && length > 0)
            {
                if (length > 65536) throw new ApiException(413, "body too large");
                request.Body = ReadExact(input, length);
            }
            return request;
        }

        public static byte[] ReadExact(Stream input, long count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = input.Read(buffer, offset, (int)(count - offset));
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
            return buffer;
        }

        private static void ParseQuery(string query, Dictionary<string, string> into)
        {
            foreach (string part in query.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string key = Decode(eq >= 0 ? part.Substring(0, eq) : part);
                into[key] = eq >= 0 ? Decode(part.Substring(eq + 1)) : "";
            }
        }

        private static string Decode(string value)
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
    }

    internal static class HttpResponse
    {
        public static void Send(Stream output, int status, string contentType, byte[] body, string extraHeaders)
        {
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
            if (contentType != null) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            head.Append("Access-Control-Allow-Origin: *\r\nCache-Control: no-store\r\nConnection: close\r\n");
            if (extraHeaders != null) head.Append(extraHeaders);
            head.Append("\r\n");
            byte[] bytes = Encoding.ASCII.GetBytes(head.ToString());
            output.Write(bytes, 0, bytes.Length);
            output.Write(body, 0, body.Length);
            output.Flush();
        }

        public static void Json(Stream output, int status, object value)
        {
            Send(output, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(SpicetifyRemoteApi.Json.Write(value)), null);
        }

        public static string Reason(int status)
        {
            switch (status)
            {
                case 101: return "Switching Protocols";
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 413: return "Payload Too Large";
                case 431: return "Request Header Fields Too Large";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                default: return "Error";
            }
        }
    }

    internal sealed class WebSocketPeer
    {
        private readonly TcpClient client;
        private readonly Stream input;
        private readonly Stream output;
        private readonly object writeLock = new object();
        private volatile bool closed;

        public WebSocketPeer(TcpClient client, Stream input, Stream output)
        {
            this.client = client;
            this.input = input;
            this.output = output;
        }

        public void Send(string text)
        {
            Write(0x1, Encoding.UTF8.GetBytes(text));
        }

        public void Run(Action<string> onMessage)
        {
            var fragments = new MemoryStream();
            try
            {
                while (!closed)
                {
                    byte[] head = HttpRequest.ReadExact(input, 2);
                    bool fin = (head[0] & 0x80) != 0;
                    int opcode = head[0] & 0x0F;
                    bool masked = (head[1] & 0x80) != 0;
                    long length = head[1] & 0x7F;
                    if (length == 126)
                    {
                        byte[] ext = HttpRequest.ReadExact(input, 2);
                        length = (ext[0] << 8) | ext[1];
                    }
                    else if (length == 127)
                    {
                        byte[] ext = HttpRequest.ReadExact(input, 8);
                        length = 0;
                        for (int i = 0; i < 8; i++) length = (length << 8) | ext[i];
                    }
                    if (length > 16 * 1024 * 1024) break;
                    byte[] mask = masked ? HttpRequest.ReadExact(input, 4) : null;
                    byte[] payload = HttpRequest.ReadExact(input, length);
                    if (masked)
                    {
                        for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
                    }
                    if (opcode == 0x8) break;
                    if (opcode == 0x9)
                    {
                        Write(0xA, payload);
                        continue;
                    }
                    if (opcode == 0xA) continue;
                    if (opcode != 0x0) fragments.SetLength(0);
                    fragments.Write(payload, 0, payload.Length);
                    if (!fin) continue;
                    string text = Encoding.UTF8.GetString(fragments.ToArray());
                    fragments.SetLength(0);
                    onMessage(text);
                }
            }
            catch (Exception)
            {
            }
            Close();
        }

        public void Close()
        {
            closed = true;
            try
            {
                client.Close();
            }
            catch (Exception)
            {
            }
        }

        private void Write(int opcode, byte[] payload)
        {
            lock (writeLock)
            {
                if (closed) return;
                byte[] header;
                if (payload.Length < 126)
                {
                    header = new byte[] { (byte)(0x80 | opcode), (byte)payload.Length };
                }
                else if (payload.Length < 65536)
                {
                    header = new byte[] { (byte)(0x80 | opcode), 126, (byte)(payload.Length >> 8), (byte)payload.Length };
                }
                else
                {
                    header = new byte[10];
                    header[0] = (byte)(0x80 | opcode);
                    header[1] = 127;
                    for (int i = 0; i < 8; i++) header[9 - i] = (byte)((long)payload.Length >> (8 * i));
                }
                try
                {
                    output.Write(header, 0, header.Length);
                    output.Write(payload, 0, payload.Length);
                    output.Flush();
                }
                catch (Exception)
                {
                    Close();
                }
            }
        }
    }

    internal sealed class EventClient
    {
        private readonly Stream output;

        public EventClient(Stream output)
        {
            this.output = output;
        }

        public bool Write(string chunk)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(chunk);
                lock (output)
                {
                    output.Write(bytes, 0, bytes.Length);
                    output.Flush();
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    internal sealed class PendingCommand
    {
        public readonly ManualResetEvent Done = new ManualResetEvent(false);
        public bool Ok;
        public object Data;
        public string Error;
    }

    internal sealed class SpotifyLink
    {
        private const int CommandTimeout = 5000;
        private readonly object sync = new object();
        private readonly Dictionary<int, PendingCommand> pending = new Dictionary<int, PendingCommand>();
        private readonly List<EventClient> listeners = new List<EventClient>();
        private WebSocketPeer peer;
        private Dictionary<string, object> state;
        private DateTime receivedAt;
        private int sequence;

        public bool Connected
        {
            get
            {
                lock (sync) return peer != null;
            }
        }

        public void Attach(WebSocketPeer next)
        {
            WebSocketPeer old;
            lock (sync)
            {
                old = peer;
                peer = next;
            }
            if (old != null) old.Close();
            next.Run(Receive);
            Detach(next);
        }

        public object Request(string action, Dictionary<string, object> args)
        {
            var command = new PendingCommand();
            int id;
            WebSocketPeer target;
            lock (sync)
            {
                if (peer == null) throw new ApiException(503, "spotify is not connected");
                id = ++sequence;
                pending[id] = command;
                target = peer;
            }
            target.Send(Json.Write(new Dictionary<string, object> { { "type", "command" }, { "id", id }, { "action", action }, { "args", args ?? new Dictionary<string, object>() } }));
            bool answered = command.Done.WaitOne(CommandTimeout);
            lock (sync) pending.Remove(id);
            if (!answered) throw new ApiException(504, action + " timed out");
            if (!command.Ok) throw new ApiException(command.Error == "spotify disconnected" ? 503 : 400, command.Error);
            return command.Data;
        }

        public Dictionary<string, object> Snapshot()
        {
            lock (sync) return SnapshotLocked();
        }

        public void Subscribe(EventClient client)
        {
            string first;
            lock (sync)
            {
                listeners.Add(client);
                first = Frame(SnapshotLocked());
            }
            client.Write(first);
        }

        public void Unsubscribe(EventClient client)
        {
            lock (sync) listeners.Remove(client);
        }

        public void Heartbeat()
        {
            Broadcast(": ping\n\n");
        }

        private void Detach(WebSocketPeer old)
        {
            lock (sync)
            {
                if (peer != old) return;
                peer = null;
                foreach (PendingCommand command in pending.Values)
                {
                    command.Error = "spotify disconnected";
                    command.Done.Set();
                }
            }
            Emit();
        }

        private void Receive(string text)
        {
            Dictionary<string, object> message;
            try
            {
                message = Json.Obj(Json.Parse(text));
            }
            catch (Exception)
            {
                return;
            }
            string type = Json.Get(message, "type") as string;
            if (type == "state")
            {
                lock (sync)
                {
                    state = Json.Obj(Json.Get(message, "state"));
                    receivedAt = DateTime.UtcNow;
                }
                Emit();
            }
            else if (type == "result")
            {
                PendingCommand command;
                lock (sync)
                {
                    if (!pending.TryGetValue((int)Json.Num(Json.Get(message, "id")), out command)) return;
                }
                command.Ok = Json.Get(message, "ok") is bool && (bool)Json.Get(message, "ok");
                command.Data = Json.Get(message, "data");
                command.Error = Json.Get(message, "error") as string;
                command.Done.Set();
            }
        }

        private Dictionary<string, object> SnapshotLocked()
        {
            var snapshot = new Dictionary<string, object> { { "connected", peer != null } };
            if (state == null)
            {
                snapshot["state"] = null;
                return snapshot;
            }
            foreach (KeyValuePair<string, object> pair in state) snapshot[pair.Key] = pair.Value;
            if (Json.Get(state, "playing") is bool && (bool)Json.Get(state, "playing"))
            {
                double duration = Json.Num(Json.Get(state, "duration_ms"));
                double position = Json.Num(Json.Get(state, "position_ms")) + (DateTime.UtcNow - receivedAt).TotalMilliseconds;
                snapshot["position_ms"] = (long)(duration > 0 ? Math.Min(position, duration) : position);
            }
            return snapshot;
        }

        private void Emit()
        {
            string frame;
            lock (sync) frame = Frame(SnapshotLocked());
            Broadcast(frame);
        }

        private void Broadcast(string chunk)
        {
            EventClient[] targets;
            lock (sync) targets = listeners.ToArray();
            foreach (EventClient client in targets)
            {
                if (!client.Write(chunk)) Unsubscribe(client);
            }
        }

        private static string Frame(Dictionary<string, object> snapshot)
        {
            return "event: state\ndata: " + Json.Write(snapshot) + "\n\n";
        }
    }

    internal sealed class ApiServer
    {
        private const string SocketPath = "/spicetify";
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private static readonly Regex AllowedOrigin = new Regex(@"^https://([a-z0-9-]+\.)*spotify\.com$", RegexOptions.IgnoreCase);
        private static readonly string[] Actions = { "play", "pause", "toggle", "next", "previous", "seek", "volume", "mute", "shuffle", "repeat", "like" };

        private readonly SpotifyLink link = new SpotifyLink();
        private readonly Dictionary<string, Func<Dictionary<string, object>, object>> routes = new Dictionary<string, Func<Dictionary<string, object>, object>>();
        private readonly IPAddress host;
        private readonly int port;
        private readonly string token;
        private readonly string remotePage;
        private Timer heartbeat;

        public ApiServer(IPAddress host, int port, string token, string remotePage)
        {
            this.host = host;
            this.port = port;
            this.token = token;
            this.remotePage = remotePage;
            Register();
        }

        public void Run()
        {
            var listener = new TcpListener(host, port);
            listener.Start();
            heartbeat = new Timer(delegate { link.Heartbeat(); }, null, 15000, 15000);
            while (true)
            {
                TcpClient client = listener.AcceptTcpClient();
                var thread = new Thread(delegate () { Handle(client); });
                thread.IsBackground = true;
                thread.Start();
            }
        }

        private void Register()
        {
            foreach (string action in Actions)
            {
                string name = action;
                Func<Dictionary<string, object>, object> handler = delegate (Dictionary<string, object> args) { return link.Request(name, args); };
                routes["POST /" + name] = handler;
                routes["GET /" + name] = handler;
            }
            routes["GET /"] = delegate
            {
                var endpoints = new List<string>(routes.Keys) { "GET /events", "GET /remote" };
                return new Dictionary<string, object> { { "name", "spicetify-remote-api" }, { "connected", link.Connected }, { "endpoints", endpoints } };
            };
            routes["GET /status"] = delegate { return link.Snapshot(); };
            routes["GET /lyrics"] = delegate { return link.Request("lyrics", null); };
            routes["GET /lyrics/current"] = delegate { return CurrentLyric(); };
        }

        private object CurrentLyric()
        {
            Dictionary<string, object> lyrics = Json.Obj(link.Request("lyrics", null));
            double position = Json.Num(Json.Get(link.Snapshot(), "position_ms"));
            IList lines = Json.Get(lyrics, "lines") as IList ?? new object[0];
            int index = -1;
            if ((Json.Get(lyrics, "sync_type") as string) != "UNSYNCED")
            {
                for (int i = 0; i < lines.Count && Json.Num(Json.Get(Json.Obj(lines[i]), "start_ms")) <= position; i++) index = i;
            }
            return new Dictionary<string, object>
            {
                { "track_uri", Json.Get(lyrics, "track_uri") },
                { "sync_type", Json.Get(lyrics, "sync_type") },
                { "position_ms", (long)position },
                { "index", index },
                { "previous", index - 1 >= 0 ? lines[index - 1] : null },
                { "current", index >= 0 ? lines[index] : null },
                { "next", index + 1 < lines.Count ? lines[index + 1] : null }
            };
        }

        private void Handle(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                NetworkStream output = client.GetStream();
                var input = new BufferedStream(output);
                HttpRequest request = HttpRequest.Read(input);
                if (request == null) return;
                try
                {
                    Dispatch(client, request, input, output);
                }
                catch (ApiException error)
                {
                    HttpResponse.Json(output, error.Status, new Dictionary<string, object> { { "error", error.Message } });
                }
                catch (IOException)
                {
                }
                catch (Exception error)
                {
                    HttpResponse.Json(output, 500, new Dictionary<string, object> { { "error", error.Message } });
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                client.Close();
            }
        }

        private void Dispatch(TcpClient client, HttpRequest request, Stream input, Stream output)
        {
            if (string.Equals(request.Header("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase))
            {
                Upgrade(client, request, input, output);
                return;
            }
            if (request.Method == "OPTIONS")
            {
                HttpResponse.Send(output, 204, null, new byte[0], "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type, Authorization\r\n");
                return;
            }
            if (request.Method == "GET" && request.Path == "/remote")
            {
                if (!File.Exists(remotePage)) throw new ApiException(404, "remote page missing");
                HttpResponse.Send(output, 200, "text/html; charset=utf-8", File.ReadAllBytes(remotePage), null);
                return;
            }
            if (!Authorized(request)) throw new ApiException(401, "unauthorized");
            if (request.Method == "GET" && request.Path == "/events")
            {
                StreamEvents(input, output);
                return;
            }
            Func<Dictionary<string, object>, object> route;
            if (!routes.TryGetValue(request.Method + " " + request.Path, out route)) throw new ApiException(404, "not found");
            HttpResponse.Json(output, 200, route(Arguments(request)));
        }

        private bool Authorized(HttpRequest request)
        {
            if (string.IsNullOrEmpty(token)) return true;
            string query;
            return request.Header("Authorization") == "Bearer " + token || (request.Query.TryGetValue("token", out query) && query == token);
        }

        private static Dictionary<string, object> Arguments(HttpRequest request)
        {
            var args = new Dictionary<string, object>();
            foreach (KeyValuePair<string, string> pair in request.Query)
            {
                if (pair.Key != "token") args[pair.Key] = pair.Value;
            }
            string text = Encoding.UTF8.GetString(request.Body).Trim();
            if (text.Length == 0) return args;
            Dictionary<string, object> body;
            try
            {
                body = Json.Obj(Json.Parse(text));
            }
            catch (Exception)
            {
                throw new ApiException(400, "invalid json body");
            }
            if (body != null)
            {
                foreach (KeyValuePair<string, object> pair in body) args[pair.Key] = pair.Value;
            }
            return args;
        }

        private void Upgrade(TcpClient client, HttpRequest request, Stream input, Stream output)
        {
            string origin = request.Header("Origin");
            string key = request.Header("Sec-WebSocket-Key");
            if (request.Path != SocketPath || string.IsNullOrEmpty(key) || (!string.IsNullOrEmpty(origin) && !AllowedOrigin.IsMatch(origin)))
            {
                HttpResponse.Send(output, 403, null, new byte[0], null);
                return;
            }
            string accept;
            using (SHA1 sha = SHA1.Create()) accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
            output.Write(head, 0, head.Length);
            output.Flush();
            link.Attach(new WebSocketPeer(client, input, output));
        }

        private void StreamEvents(Stream input, Stream output)
        {
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: keep-alive\r\nAccess-Control-Allow-Origin: *\r\n\r\n");
            output.Write(head, 0, head.Length);
            output.Flush();
            var events = new EventClient(output);
            link.Subscribe(events);
            try
            {
                while (input.ReadByte() >= 0)
                {
                }
            }
            finally
            {
                link.Unsubscribe(events);
            }
        }
    }

    internal static class Program
    {
        private static int Main()
        {
            string port = Environment.GetEnvironmentVariable("SPICE_API_PORT");
            string host = Environment.GetEnvironmentVariable("SPICE_API_HOST");
            int number;
            var server = new ApiServer(
                IPAddress.Parse(string.IsNullOrEmpty(host) ? "127.0.0.1" : host),
                int.TryParse(port, out number) ? number : 7979,
                Environment.GetEnvironmentVariable("SPICE_API_TOKEN") ?? "",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "remote.html"));
            try
            {
                server.Run();
            }
            catch (SocketException)
            {
                return 1;
            }
            return 0;
        }
    }
}
