using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KeeLiveSync
{
    /// <summary>Ein angemeldetes Microsoft-Konto</summary>
    internal sealed class Account
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string DriveId { get; set; }
        public string DriveType { get; set; }

        public string Initials
        {
            get
            {
                var parts = (Name ?? Email ?? "?").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return "?";
                string s = parts[0].Substring(0, 1);
                if (parts.Length > 1) s += parts[parts.Length - 1].Substring(0, 1);
                return s.ToUpperInvariant();
            }
        }
    }

    /// <summary>
    /// Anmeldung bei Microsoft (Authorization Code + PKCE über http://localhost, öffentlicher Client ohne Geheimnis)
    /// und Ablage der Konten. Refresh-Tokens liegen DPAPI-verschlüsselt (nur dieser Windows-Benutzer) in accounts\*.bin.
    /// </summary>
    internal static class Accounts
    {
        internal const string ClientId = "d78cc91b-9433-45d9-b126-99ce4fa944ba";
        private const string Authority = "https://login.microsoftonline.com/common/oauth2/v2.0/";
        private const string Scopes = "Files.ReadWrite.All offline_access User.Read";

        private static readonly HttpClient Http = new HttpClient();
        private static readonly object Sync = new object();
        private static List<Account> _list;
        private static readonly Dictionary<string, Tuple<string, DateTime>> AccessTokens = new Dictionary<string, Tuple<string, DateTime>>();
        private static readonly Dictionary<string, SemaphoreSlim> RefreshLocks = new Dictionary<string, SemaphoreSlim>();

        private static string ListFile
        {
            get { return Path.Combine(Paths.Root, "accounts.json"); }
        }

        private static string TokenFile(string accountId)
        {
            return Path.Combine(Paths.Sub("accounts"), Hash(accountId) + ".bin");
        }

        internal static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        internal static List<Account> All
        {
            get
            {
                lock (Sync)
                {
                    if (_list == null)
                    {
                        try
                        {
                            _list = File.Exists(ListFile) ? Json.Parse<List<Account>>(File.ReadAllText(ListFile)) : new List<Account>();
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Kontenliste nicht lesbar", ex);
                            _list = new List<Account>();
                        }
                    }
                    return _list.ToList();
                }
            }
        }

        internal static Account Find(string id)
        {
            return All.FirstOrDefault(a => a.Id == id);
        }

        private static void Save()
        {
            lock (Sync) File.WriteAllText(ListFile, Json.Write(_list));
        }

        internal static void Remove(string id)
        {
            lock (Sync)
            {
                All.ToString();
                _list.RemoveAll(a => a.Id == id);
                AccessTokens.Remove(id);
                Save();
                if (File.Exists(TokenFile(id))) File.Delete(TokenFile(id));
            }
            Log.Info("Konto abgemeldet: " + Log.Short(id));
        }

        private static void SaveRefreshToken(string accountId, string token)
        {
            byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenFile(accountId), data);
        }

        private static string LoadRefreshToken(string accountId)
        {
            string file = TokenFile(accountId);
            if (!File.Exists(file)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser));
        }

        private static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>Öffnet den Browser zur Anmeldung und wartet auf die Rückmeldung (abbrechbar)</summary>
        internal static async Task<Account> SignIn(CancellationToken cancel)
        {
            byte[] buf = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(buf);
            string verifier = Base64Url(buf);
            string challenge;
            using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(buf);
            string state = Base64Url(buf);

            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            string redirect = "http://localhost:" + port + "/";

            var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            listener.Start();
            Log.Info("Anmeldung gestartet (Port " + port + ")");
            try
            {
                string url = Authority + "authorize?client_id=" + ClientId
                             + "&response_type=code&response_mode=query&prompt=select_account"
                             + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                             + "&scope=" + Uri.EscapeDataString(Scopes)
                             + "&code_challenge=" + challenge + "&code_challenge_method=S256&state=" + state;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                System.Collections.Specialized.NameValueCollection query = null;
                while (query == null)
                {
                    var contextTask = listener.GetContextAsync();
                    var done = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, cancel));
                    if (done != contextTask) throw new OperationCanceledException(cancel);
                    var ctx = contextTask.Result;
                    var q = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url.Query);
                    bool isAnswer = q["code"] != null || q["error"] != null;
                    Respond(ctx, isAnswer && q["error"] == null, q["error_description"]);
                    if (isAnswer) query = q;
                }
                // Browser fragt die Seite manchmal ein zweites Mal ab (Vorabruf, Neuladen): kurz weiter beantworten
                KeepAnswering(listener);

                if (query["error"] != null)
                    throw new InvalidOperationException(Text.T("Anmeldung abgelehnt: {0}", "Sign-in declined: {0}", query["error_description"]));
                if (query["state"] != state) throw new InvalidOperationException("state mismatch");

                var tokens = await TokenRequest(new Dictionary<string, string>
                {
                    { "client_id", ClientId }, { "grant_type", "authorization_code" }, { "code", query["code"] },
                    { "redirect_uri", redirect }, { "code_verifier", verifier }, { "scope", Scopes }
                });
                string access = tokens.Str("access_token");
                var account = await Graph.LoadAccount(access);
                SaveRefreshToken(account.Id, tokens.Str("refresh_token"));
                lock (Sync)
                {
                    All.ToString();
                    _list.RemoveAll(a => a.Id == account.Id);
                    _list.Add(account);
                    Save();
                    AccessTokens[account.Id] = Tuple.Create(access, DateTime.UtcNow.AddSeconds(tokens.Long("expires_in") - 300));
                }
                Log.Info("Angemeldet: " + account.Email + " (" + account.DriveType + ")");
                return account;
            }
            catch
            {
                listener.Close();
                throw;
            }
        }

        private static void KeepAnswering(HttpListener listener)
        {
            Task.Run(async () =>
            {
                var until = DateTime.UtcNow.AddSeconds(15);
                try
                {
                    while (DateTime.UtcNow < until)
                    {
                        var t = listener.GetContextAsync();
                        if (await Task.WhenAny(t, Task.Delay(until - DateTime.UtcNow)) != t) break;
                        Respond(t.Result, true, null);
                    }
                }
                catch (Exception)
                {
                    // Listener geschlossen
                }
                finally
                {
                    listener.Close();
                }
            });
        }

        private static void Respond(HttpListenerContext ctx, bool ok, string error)
        {
            string title = ok ? Text.T("Angemeldet", "Signed in") : Text.T("Anmeldung nicht abgeschlossen", "Sign-in not completed");
            string body = ok
                ? Text.T("KeeLiveSync ist jetzt mit deinem Microsoft-Konto verbunden. Du kannst dieses Fenster schließen und zu KeePass zurückkehren.",
                    "KeeLiveSync is now connected to your Microsoft account. You can close this window and return to KeePass.")
                : WebUtility.HtmlEncode(error ?? "");
            string html = "<!doctype html><html><head><meta charset='utf-8'><title>KeeLiveSync</title><style>"
                          + "body{margin:0;font-family:'Segoe UI',system-ui,sans-serif;background:#f3f6f9;color:#1f2933;display:flex;align-items:center;justify-content:center;height:100vh}"
                          + ".card{background:#fff;border-radius:14px;box-shadow:0 8px 30px rgba(15,40,70,.12);padding:36px 44px;max-width:440px;text-align:center}"
                          + ".dot{width:64px;height:64px;border-radius:50%;margin:0 auto 18px;display:flex;align-items:center;justify-content:center;font-size:34px;color:#fff;background:"
                          + (ok ? "#12805c" : "#b3261e") + "}h1{font-size:22px;margin:0 0 10px}p{margin:0;line-height:1.5;color:#52606d}"
                          + "</style></head><body><div class='card'><div class='dot'>" + (ok ? "&#10003;" : "!") + "</div><h1>" + title
                          + "</h1><p>" + body + "</p></div></body></html>";
            byte[] page = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.OutputStream.Write(page, 0, page.Length);
            ctx.Response.Close();
        }

        private static async Task<Dictionary<string, object>> TokenRequest(Dictionary<string, string> form)
        {
            var resp = await Http.PostAsync(Authority + "token", new FormUrlEncodedContent(form)).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var data = Json.Parse(body);
            if (!resp.IsSuccessStatusCode)
            {
                string err = data.Str("error");
                Log.Warn("Token-Anfrage " + (int)resp.StatusCode + ": " + err + " – " + data.Str("error_description"));
                if (err == "invalid_grant" || err == "interaction_required")
                    throw new SignInRequiredException();
                throw new HttpRequestException("Token " + (int)resp.StatusCode + " " + err);
            }
            return data;
        }

        internal static void ForgetAccessToken(string accountId)
        {
            lock (Sync) AccessTokens.Remove(accountId);
        }

        /// <summary>Gültiges Zugriffstoken; erneuert es still über den Refresh-Token, wenn es bald abläuft</summary>
        internal static async Task<string> AccessToken(string accountId)
        {
            SemaphoreSlim gate;
            lock (Sync)
            {
                Tuple<string, DateTime> cached;
                if (AccessTokens.TryGetValue(accountId, out cached) && cached.Item2 > DateTime.UtcNow) return cached.Item1;
                if (!RefreshLocks.TryGetValue(accountId, out gate)) RefreshLocks[accountId] = gate = new SemaphoreSlim(1, 1);
            }
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (Sync)
                {
                    Tuple<string, DateTime> cached;
                    if (AccessTokens.TryGetValue(accountId, out cached) && cached.Item2 > DateTime.UtcNow) return cached.Item1;
                }
                string rt = LoadRefreshToken(accountId);
                if (rt == null) throw new SignInRequiredException();
                var tokens = await TokenRequest(new Dictionary<string, string>
                {
                    { "client_id", ClientId }, { "grant_type", "refresh_token" }, { "refresh_token", rt }, { "scope", Scopes }
                }).ConfigureAwait(false);
                string newRt = tokens.Str("refresh_token");
                if (!string.IsNullOrEmpty(newRt)) SaveRefreshToken(accountId, newRt);
                string at = tokens.Str("access_token");
                lock (Sync) AccessTokens[accountId] = Tuple.Create(at, DateTime.UtcNow.AddSeconds(tokens.Long("expires_in") - 300));
                Log.Debug("Zugriffstoken erneuert für " + Log.Short(accountId) + ", gültig " + tokens.Long("expires_in") + " s");
                return at;
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>Refresh-Token fehlt, abgelaufen oder widerrufen: erneute Anmeldung nötig</summary>
    internal sealed class SignInRequiredException : Exception
    {
        public SignInRequiredException()
            : base(Text.T("Bitte erneut bei Microsoft anmelden.", "Please sign in to Microsoft again."))
        {
        }
    }
}
