// Proof of Concept: prüft die Graph-Bausteine für ein KeePass-OneDrive-Sync-Plugin unter .NET Framework 4.8.
// - Anmeldung: Authorization Code + PKCE über http://localhost (öffentlicher Client, kein Geheimnis)
// - Hochladen mit If-Match (eTag) → erwartet 412 bei veraltetem Stand
// - Push über /subscriptions/socketIo mit eigener, abhängigkeitsfreier socket.io-Anbindung (ClientWebSocket)
// Testdaten liegen nur in /KeePass-Synctest im OneDrive des angemeldeten Kontos.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace SyncPoc
{
    internal static class Program
    {
        private const string ClientId = "d78cc91b-9433-45d9-b126-99ce4fa944ba";
        private const string Authority = "https://login.microsoftonline.com/common/oauth2/v2.0/";
        private const string Scopes = "Files.ReadWrite.All offline_access User.Read";
        private const string Graph = "https://graph.microsoft.com/v1.0";
        private const string TestFolder = "KeePass-Synctest";
        private const string TestFile = "iftest.bin";

        private static readonly HttpClient Http = new HttpClient();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length == 0)
            {
                Console.WriteLine("Befehle: login <profil> | whoami <profil> | prepare <profil> | ifmatch <profil>");
                Console.WriteLine("         shared <profil> | listen <profil> [driveId] | touch <profil> [driveId itemId]");
                return 1;
            }
            try
            {
                return Run(args).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FEHLER: " + ex.Message);
                return 2;
            }
        }

        private static async Task<int> Run(string[] args)
        {
            string cmd = args[0];
            string profile = args.Length > 1 ? args[1] : "michael";
            switch (cmd)
            {
                case "login":
                    await LoginInteractive(profile);
                    Console.WriteLine("Angemeldet: " + await WhoAmI(profile));
                    return 0;
                case "whoami":
                    Console.WriteLine(await WhoAmI(profile));
                    return 0;
                case "prepare":
                    await Prepare(profile);
                    return 0;
                case "ifmatch":
                    return await IfMatchTest(profile);
                case "shared":
                    await ListShared(profile);
                    return 0;
                case "listen":
                    await Listen(profile, args.Length > 2 ? args[2] : null);
                    return 0;
                case "get":
                    var g = await Send(profile, HttpMethod.Get, args[2]);
                    Console.WriteLine(g.Status + " " + g.Body);
                    return 0;
                case "upload":
                    // upload <profil> <lokale Datei> <Pfad unter KeePass-Synctest>
                    var ub = new ByteArrayContent(File.ReadAllBytes(args[2]));
                    ub.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    var ur = await Send(profile, HttpMethod.Put, "/me/drive/root:/" + TestFolder + "/" + args[3] + ":/content", ub);
                    Console.WriteLine(ur.Status + " id=" + Str(ur.Data, "id") + " cTag=" + Str(ur.Data, "cTag"));
                    return 0;
                case "download":
                    // download <profil> <Pfad unter KeePass-Synctest> <lokale Datei>
                    var tok = await AccessToken(profile);
                    var dreq = new HttpRequestMessage(HttpMethod.Get, Graph + "/me/drive/root:/" + TestFolder + "/" + args[2] + ":/content");
                    dreq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok);
                    var dresp = await Http.SendAsync(dreq);
                    File.WriteAllBytes(args[3], await dresp.Content.ReadAsByteArrayAsync());
                    Console.WriteLine((int)dresp.StatusCode + " → " + args[3]);
                    return 0;
                case "download-url":
                    // download-url <profil> <Graph-Pfad> <lokale Datei>
                    var utok = await AccessToken(profile);
                    var ureq = new HttpRequestMessage(HttpMethod.Get, Graph + args[2]);
                    ureq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", utok);
                    var uresp = await Http.SendAsync(ureq);
                    File.WriteAllBytes(args[3], await uresp.Content.ReadAsByteArrayAsync());
                    Console.WriteLine((int)uresp.StatusCode + " → " + args[3]);
                    return 0;
                case "patch":
                    var pb = new StringContent(args[3], Encoding.UTF8, "application/json");
                    var pr = await Send(profile, new HttpMethod("PATCH"), args[2], pb);
                    Console.WriteLine(pr.Status + " " + pr.Body);
                    return 0;
                case "ifmatch-item":
                    return await IfMatchItem(profile, args[2], args[3]);
                case "touch":
                    await Touch(profile, args.Length > 3 ? args[2] : null, args.Length > 3 ? args[3] : null);
                    return 0;
                default:
                    Console.WriteLine("Unbekannter Befehl: " + cmd);
                    return 1;
            }
        }

        // ---------- Anmeldung ----------

        private static string ProfileDir(string profile)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KeePassOneDriveSyncPoc", profile);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void SaveRefreshToken(string profile, string token)
        {
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Path.Combine(ProfileDir(profile), "token.bin"), data);
        }

        private static string LoadRefreshToken(string profile)
        {
            string file = Path.Combine(ProfileDir(profile), "token.bin");
            if (!File.Exists(file)) return null;
            byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }

        private static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static async Task LoginInteractive(string profile)
        {
            var rng = RandomNumberGenerator.Create();
            byte[] buf = new byte[32];
            rng.GetBytes(buf);
            string verifier = Base64Url(buf);
            string challenge;
            using (var sha = SHA256.Create())
                challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            rng.GetBytes(buf);
            string state = Base64Url(buf);

            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            string redirect = "http://localhost:" + port + "/";

            var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();

            string url = Authority + "authorize?client_id=" + ClientId
                + "&response_type=code&response_mode=query&prompt=select_account"
                + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                + "&scope=" + Uri.EscapeDataString(Scopes)
                + "&code_challenge=" + challenge + "&code_challenge_method=S256"
                + "&state=" + state;
            Console.WriteLine("Browser öffnet sich zur Anmeldung (Profil '" + profile + "') …");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            HttpListenerContext ctx = await listener.GetContextAsync();
            var query = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url.Query);
            byte[] page = Encoding.UTF8.GetBytes("<html><body><p>Anmeldung abgeschlossen. Dieses Fenster kann geschlossen werden.</p></body></html>");
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.OutputStream.Write(page, 0, page.Length);
            ctx.Response.Close();
            listener.Stop();

            if (query["error"] != null)
                throw new Exception("Anmeldung abgelehnt: " + query["error"] + " – " + query["error_description"]);
            if (query["state"] != state)
                throw new Exception("state passt nicht");

            var tokens = await TokenRequest(new Dictionary<string, string>
            {
                { "client_id", ClientId },
                { "grant_type", "authorization_code" },
                { "code", query["code"] },
                { "redirect_uri", redirect },
                { "code_verifier", verifier },
                { "scope", Scopes }
            });
            SaveRefreshToken(profile, (string)tokens["refresh_token"]);
        }

        private static async Task<Dictionary<string, object>> TokenRequest(Dictionary<string, string> form)
        {
            var resp = await Http.PostAsync(Authority + "token", new FormUrlEncodedContent(form));
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) throw new Exception("Token-Anfrage " + (int)resp.StatusCode + ": " + body);
            return Json.Deserialize<Dictionary<string, object>>(body);
        }

        private static readonly Dictionary<string, string> AccessTokens = new Dictionary<string, string>();

        private static async Task<string> AccessToken(string profile)
        {
            string at;
            if (AccessTokens.TryGetValue(profile, out at)) return at;
            string rt = LoadRefreshToken(profile);
            if (rt == null) throw new Exception("Profil '" + profile + "' nicht angemeldet – erst 'login " + profile + "'.");
            var tokens = await TokenRequest(new Dictionary<string, string>
            {
                { "client_id", ClientId },
                { "grant_type", "refresh_token" },
                { "refresh_token", rt },
                { "scope", Scopes }
            });
            if (tokens.ContainsKey("refresh_token")) SaveRefreshToken(profile, (string)tokens["refresh_token"]);
            at = (string)tokens["access_token"];
            AccessTokens[profile] = at;
            return at;
        }

        // ---------- Graph ----------

        private sealed class GraphResponse
        {
            public int Status;
            public string Body;
            public Dictionary<string, object> Data;
        }

        private static async Task<GraphResponse> Send(string profile, HttpMethod method, string url,
            HttpContent content = null, string ifMatch = null)
        {
            var req = new HttpRequestMessage(method, url.StartsWith("http") ? url : Graph + url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessToken(profile));
            if (ifMatch != null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            req.Content = content;
            var resp = await Http.SendAsync(req);
            var r = new GraphResponse { Status = (int)resp.StatusCode, Body = await resp.Content.ReadAsStringAsync() };
            if (r.Body.StartsWith("{")) r.Data = Json.Deserialize<Dictionary<string, object>>(r.Body);
            return r;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as string : null;
        }

        private static async Task<string> WhoAmI(string profile)
        {
            var r = await Send(profile, HttpMethod.Get, "/me?$select=displayName,userPrincipalName");
            if (r.Status != 200) return "Fehler " + r.Status + ": " + r.Body;
            var d = await Send(profile, HttpMethod.Get, "/me/drive?$select=id,driveType");
            return Str(r.Data, "displayName") + " <" + Str(r.Data, "userPrincipalName") + ">, Laufwerk "
                + Str(d.Data, "id") + " (" + Str(d.Data, "driveType") + ")";
        }

        private static ByteArrayContent RandomContent()
        {
            byte[] data = Encoding.UTF8.GetBytes("Testinhalt " + DateTime.Now.ToString("o") + " " + Guid.NewGuid());
            var c = new ByteArrayContent(data);
            c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return c;
        }

        private static async Task Prepare(string profile)
        {
            var folder = new StringContent(Json.Serialize(new Dictionary<string, object>
            {
                { "name", TestFolder }, { "folder", new Dictionary<string, object>() },
                { "@microsoft.graph.conflictBehavior", "fail" }
            }), Encoding.UTF8, "application/json");
            var f = await Send(profile, HttpMethod.Post, "/me/drive/root/children", folder);
            Console.WriteLine("Ordner anlegen: " + f.Status + (f.Status == 409 ? " (existiert schon)" : ""));
            var u = await Send(profile, HttpMethod.Put, "/me/drive/root:/" + TestFolder + "/" + TestFile + ":/content", RandomContent());
            Console.WriteLine("Testdatei hochladen: " + u.Status + " id=" + Str(u.Data, "id") + " eTag=" + Str(u.Data, "eTag") + " cTag=" + Str(u.Data, "cTag"));
            var p = await Send(profile, HttpMethod.Get, "/me/drive/root:/" + TestFolder + "?$select=id,parentReference");
            Console.WriteLine("Ordner-ID: " + Str(p.Data, "id"));
        }

        // ---------- Test 1: If-Match ----------

        private static async Task<int> IfMatchTest(string profile)
        {
            string path = "/me/drive/root:/" + TestFolder + "/" + TestFile;
            var item = await Send(profile, HttpMethod.Get, path + "?$select=id,eTag,cTag,parentReference");
            if (item.Status != 200) { Console.WriteLine("Testdatei fehlt – erst 'prepare'. " + item.Body); return 1; }
            string id = Str(item.Data, "id");
            string driveId = Str((Dictionary<string, object>)item.Data["parentReference"], "driveId");
            string etag0 = Str(item.Data, "eTag");
            string ctag0 = Str(item.Data, "cTag");
            Console.WriteLine("Ausgang: eTag=" + etag0 + " cTag=" + ctag0);
            string content = "/drives/" + driveId + "/items/" + id + "/content";

            var a = await Send(profile, HttpMethod.Put, content, RandomContent(), etag0);
            Console.WriteLine("A) Upload mit aktuellem eTag:  " + a.Status + " → eTag=" + Str(a.Data, "eTag"));

            var b = await Send(profile, HttpMethod.Put, content, RandomContent(), etag0);
            Console.WriteLine("B) Upload mit veraltetem eTag: " + b.Status + (b.Status == 412 ? " (abgelehnt – gewünscht)" : " ← NICHT abgelehnt!"));
            if (b.Status != 412) Console.WriteLine("   " + b.Body);

            var c = await Send(profile, HttpMethod.Put, content, RandomContent(), ctag0);
            Console.WriteLine("C) Upload mit veraltetem cTag: " + c.Status);

            var now = await Send(profile, HttpMethod.Get, "/drives/" + driveId + "/items/" + id + "?$select=eTag,cTag");
            var d = await Send(profile, HttpMethod.Put, content, RandomContent(), Str(now.Data, "cTag"));
            Console.WriteLine("D) Upload mit aktuellem cTag:  " + d.Status);

            // Metadatenänderung (Umbenennen hin und zurück) ändert eTag, aber nicht cTag
            now = await Send(profile, HttpMethod.Get, "/drives/" + driveId + "/items/" + id + "?$select=eTag,cTag");
            string etag1 = Str(now.Data, "eTag"), ctag1 = Str(now.Data, "cTag");
            await Rename(profile, driveId, id, "iftest-umbenannt.bin");
            await Rename(profile, driveId, id, TestFile);
            now = await Send(profile, HttpMethod.Get, "/drives/" + driveId + "/items/" + id + "?$select=eTag,cTag");
            Console.WriteLine("E) Nach Umbenennen: eTag " + (Str(now.Data, "eTag") == etag1 ? "gleich" : "geändert")
                + ", cTag " + (Str(now.Data, "cTag") == ctag1 ? "gleich" : "geändert"));
            var e = await Send(profile, HttpMethod.Put, content, RandomContent(), ctag1);
            Console.WriteLine("   Upload mit cTag von vor dem Umbenennen: " + e.Status);
            return b.Status == 412 ? 0 : 3;
        }

        private static async Task<int> IfMatchItem(string profile, string driveId, string id)
        {
            string itemUrl = "/drives/" + driveId + "/items/" + id;
            var item = await Send(profile, HttpMethod.Get, itemUrl + "?$select=id,eTag,cTag");
            Console.WriteLine("Element lesen: " + item.Status + (item.Status != 200 ? " " + item.Body : " cTag=" + Str(item.Data, "cTag")));
            if (item.Status != 200) return 1;
            string ctag0 = Str(item.Data, "cTag");
            var a = await Send(profile, HttpMethod.Put, itemUrl + "/content", RandomContent(), ctag0);
            Console.WriteLine("A) Upload mit aktuellem cTag:  " + a.Status + (a.Status >= 300 ? " " + a.Body : ""));
            var b = await Send(profile, HttpMethod.Put, itemUrl + "/content", RandomContent(), ctag0);
            Console.WriteLine("B) Upload mit veraltetem cTag: " + b.Status + (b.Status == 412 ? " (abgelehnt – gewünscht)" : " " + b.Body));
            return b.Status == 412 ? 0 : 3;
        }

        private static async Task Rename(string profile, string driveId, string id, string name)
        {
            var body = new StringContent(Json.Serialize(new Dictionary<string, object> { { "name", name } }), Encoding.UTF8, "application/json");
            var r = await Send(profile, new HttpMethod("PATCH"), "/drives/" + driveId + "/items/" + id, body);
            if (r.Status != 200) Console.WriteLine("   Umbenennen " + r.Status + ": " + r.Body);
        }

        // ---------- Freigaben ----------

        private static async Task ListShared(string profile)
        {
            var r = await Send(profile, HttpMethod.Get, "/me/drive/sharedWithMe");
            Console.WriteLine("sharedWithMe: " + r.Status);
            if (r.Status != 200) { Console.WriteLine(r.Body); return; }
            foreach (Dictionary<string, object> it in (System.Collections.ArrayList)r.Data["value"])
            {
                var remote = it.ContainsKey("remoteItem") ? (Dictionary<string, object>)it["remoteItem"] : it;
                var pr = remote.ContainsKey("parentReference") ? (Dictionary<string, object>)remote["parentReference"] : null;
                Console.WriteLine("  " + Str(it, "name") + "  driveId=" + Str(pr, "driveId") + "  itemId=" + Str(remote, "id"));
            }
        }

        // ---------- Test 2/3: Push über socket.io ----------

        private static async Task Listen(string profile, string driveId)
        {
            string root = driveId == null ? "/me/drive/root" : "/drives/" + driveId + "/root";
            var sub = await Send(profile, HttpMethod.Get, root + "/subscriptions/socketIo");
            Console.WriteLine("Abo " + root + ": " + sub.Status + " " + sub.Body);
            if (sub.Status != 200) return;
            string notificationUrl = Str(sub.Data, "notificationUrl");

            // https://host/namespace/callback?query  → Server, Namespace, Query (laut Graph-Doku)
            var uri = new Uri(notificationUrl);
            string ns = uri.AbsolutePath.Replace("/callback", "");
            string query = uri.Query.TrimStart('?');

            foreach (int eio in new[] { 4, 3 })
            {
                Console.WriteLine("--- Verbinde mit Engine.IO v" + eio + " (Namespace " + ns + ")");
                bool ok = await SocketIoSession(uri.Host, ns, query, eio, profile);
                if (ok) return;
            }
        }

        private static async Task<bool> SocketIoSession(string host, string ns, string query, int eio, string profile)
        {
            var ws = new ClientWebSocket();
            var wsUri = new Uri("wss://" + host + "/socket.io/?EIO=" + eio + "&transport=websocket&" + query);
            try { await ws.ConnectAsync(wsUri, CancellationToken.None); }
            catch (Exception ex) { Console.WriteLine("WebSocket-Verbindung fehlgeschlagen: " + ex.Message); return false; }
            Console.WriteLine("WebSocket offen.");

            var sendLock = new SemaphoreSlim(1, 1);
            Func<string, Task> send = async s =>
            {
                await sendLock.WaitAsync();
                try
                {
                    Console.WriteLine(Stamp() + " > " + s);
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(s)), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                finally { sendLock.Release(); }
            };

            bool connected = false;
            var buffer = new byte[64 * 1024];
            var cts = new CancellationTokenSource();
            while (ws.State == WebSocketState.Open)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                } while (!res.EndOfMessage);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine(Stamp() + " Server hat geschlossen: " + ws.CloseStatus + " " + ws.CloseStatusDescription);
                    break;
                }
                string msg = sb.ToString();
                if (msg != "2" && msg != "3") Console.WriteLine(Stamp() + " < " + msg);

                if (msg.StartsWith("0{"))
                {
                    var open = Json.Deserialize<Dictionary<string, object>>(msg.Substring(1));
                    int interval = Convert.ToInt32(open["pingInterval"]);
                    if (eio == 3)
                    {
                        // EIO v3: Client pingt
                        var token = cts.Token;
                        var _ = Task.Run(async () =>
                        {
                            while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
                            {
                                await Task.Delay(interval, token).ContinueWith(t => { });
                                if (!token.IsCancellationRequested && ws.State == WebSocketState.Open) await send("2");
                            }
                        });
                        await send("40" + ns + "?" + query + ",");
                    }
                    else
                    {
                        await send("40" + ns + ",");
                    }
                }
                else if (msg == "2")
                {
                    await send("3"); // EIO v4: Server pingt
                }
                else if (msg.StartsWith("40" + ns))
                {
                    connected = true;
                    Console.WriteLine(Stamp() + " Namespace verbunden – warte auf Änderungen (Strg+C beendet).");
                }
                else if (msg.StartsWith("44" + ns))
                {
                    Console.WriteLine("Namespace abgelehnt.");
                    break;
                }
                else if (msg.StartsWith("42" + ns))
                {
                    Console.WriteLine(Stamp() + " >>> PUSH-BENACHRICHTIGUNG erhalten");
                }
            }
            cts.Cancel();
            return connected;
        }

        private static string Stamp()
        {
            return DateTime.Now.ToString("HH:mm:ss.fff");
        }

        // ---------- Änderung auslösen ----------

        private static async Task Touch(string profile, string driveId, string itemId)
        {
            string content = driveId == null
                ? "/me/drive/root:/" + TestFolder + "/" + TestFile + ":/content"
                : "/drives/" + driveId + "/items/" + itemId + "/content";
            var r = await Send(profile, HttpMethod.Put, content, RandomContent());
            Console.WriteLine(Stamp() + " Upload: " + r.Status + " cTag=" + Str(r.Data, "cTag") + (r.Status >= 300 ? " " + r.Body : ""));
        }
    }
}
