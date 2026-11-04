using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace KeeLiveSync
{
    /// <summary>Datei oder Ordner in OneDrive</summary>
    internal sealed class DriveItem
    {
        public string Id;
        public string DriveId;
        public string Name;
        public string CTag;
        public long Size;
        public bool IsFolder;
        public int ChildCount;
        public DateTime Modified;
        public string ModifiedBy;
        public string ModifiedByEmail;
        public string OwnerName;

        public bool IsKdbx
        {
            get { return !IsFolder && Name != null && Name.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase); }
        }

        internal static DriveItem From(Dictionary<string, object> d)
        {
            // Verknüpfungen/Freigaben liefern die eigentlichen Daten unter remoteItem
            var remote = d.Obj("remoteItem");
            var src = remote ?? d;
            var parent = src.Obj("parentReference");
            var modifiedBy = src.Obj("lastModifiedBy").Obj("user");
            var owner = src.Obj("shared").Obj("owner").Obj("user");
            return new DriveItem
            {
                Id = src.Str("id"),
                DriveId = parent.Str("driveId"),
                Name = d.Str("name") ?? src.Str("name"),
                CTag = src.Str("cTag"),
                Size = src.Long("size"),
                IsFolder = src.Obj("folder") != null,
                ChildCount = (int)src.Obj("folder").Long("childCount"),
                Modified = src.Date("lastModifiedDateTime"),
                ModifiedBy = modifiedBy.Str("displayName"),
                ModifiedByEmail = modifiedBy.Str("email"),
                OwnerName = owner.Str("displayName")
            };
        }
    }

    internal sealed class UploadResult
    {
        public bool Conflict;
        public DriveItem Item;
    }

    internal sealed class PushSubscription
    {
        public string Id;
        public string NotificationUrl;
        public DateTime ExpiresUtc;
    }

    /// <summary>Aufrufe an Microsoft Graph (v1.0) für ein Konto</summary>
    internal static class Graph
    {
        private const string Base = "https://graph.microsoft.com/v1.0";
        private const string ItemSelect = "id,name,cTag,size,folder,file,parentReference,lastModifiedDateTime,lastModifiedBy,remoteItem,shared";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        static Graph()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        private sealed class Response
        {
            public HttpStatusCode Status;
            public string Body;
            public byte[] Bytes;
            public Dictionary<string, object> Data;

            public bool Ok
            {
                get { return (int)Status >= 200 && (int)Status < 300; }
            }
        }

        private static async Task<Response> Send(string accountId, HttpMethod method, string url, HttpContent content = null,
            string ifMatch = null, bool binary = false)
        {
            for (int attempt = 1; ; attempt++)
            {
                string token = await Accounts.AccessToken(accountId).ConfigureAwait(false);
                var req = new HttpRequestMessage(method, url.StartsWith("http", StringComparison.Ordinal) ? url : Base + url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (ifMatch != null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
                if (content != null) req.Content = await Clone(content).ConfigureAwait(false);
                var started = DateTime.UtcNow;
                using (var resp = await Http.SendAsync(req).ConfigureAwait(false))
                {
                    var r = new Response { Status = resp.StatusCode };
                    if (binary && resp.IsSuccessStatusCode) r.Bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    else r.Body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    r.Data = Json.Parse(r.Body);
                    Log.Debug("Graph " + method + " " + Trim(url) + " → " + (int)r.Status + " (" +
                              (int)(DateTime.UtcNow - started).TotalMilliseconds + " ms)");

                    bool retry = attempt < 4 && ((int)r.Status == 429 || (int)r.Status == 503 || (int)r.Status == 502 || (int)r.Status == 504);
                    if (r.Status == HttpStatusCode.Unauthorized && attempt == 1)
                    {
                        Accounts.ForgetAccessToken(accountId);
                        continue;
                    }
                    if (!retry) return r;
                    var wait = resp.Headers.RetryAfter != null && resp.Headers.RetryAfter.Delta.HasValue
                        ? resp.Headers.RetryAfter.Delta.Value
                        : TimeSpan.FromSeconds(2 * attempt);
                    Log.Warn("Graph " + (int)r.Status + ", neuer Versuch in " + wait.TotalSeconds + " s");
                    await Task.Delay(wait).ConfigureAwait(false);
                }
            }
        }

        private static async Task<HttpContent> Clone(HttpContent content)
        {
            byte[] data = await content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var c = new ByteArrayContent(data);
            foreach (var h in content.Headers) c.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return c;
        }

        private static string Trim(string url)
        {
            int q = url.IndexOf('?');
            return q > 0 ? url.Substring(0, q) : url;
        }

        private static GraphException Fail(Response r, string what)
        {
            var err = r.Data.Obj("error");
            Log.Warn(what + ": " + (int)r.Status + " " + err.Str("code") + " – " + err.Str("message"));
            return new GraphException(r.Status, what + " (" + (int)r.Status + " " + err.Str("code") + ")");
        }

        internal static async Task<Account> LoadAccount(string accessToken)
        {
            Func<string, Task<Dictionary<string, object>>> get = async url =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, Base + url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using (var resp = await Http.SendAsync(req).ConfigureAwait(false))
                {
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) throw new GraphException(resp.StatusCode, "GET " + url + " " + (int)resp.StatusCode);
                    return Json.Parse(body);
                }
            };
            var me = await get("/me?$select=id,displayName,userPrincipalName,mail").ConfigureAwait(false);
            var drive = await get("/me/drive?$select=id,driveType").ConfigureAwait(false);
            return new Account
            {
                Id = me.Str("id"),
                Name = me.Str("displayName"),
                Email = me.Str("mail") ?? me.Str("userPrincipalName"),
                DriveId = drive.Str("id"),
                DriveType = drive.Str("driveType")
            };
        }

        internal static async Task<List<DriveItem>> Children(string accountId, string driveId, string itemId)
        {
            string url = "/drives/" + driveId + "/items/" + (itemId ?? "root") + "/children?$top=200&$select=" + ItemSelect;
            var list = new List<DriveItem>();
            while (url != null)
            {
                var r = await Send(accountId, HttpMethod.Get, url).ConfigureAwait(false);
                if (!r.Ok) throw Fail(r, "Ordner lesen");
                foreach (var d in r.Data.Arr("value")) list.Add(DriveItem.From(d));
                url = r.Data.Str("@odata.nextLink");
            }
            return list;
        }

        internal static async Task<DriveItem> Item(string accountId, string driveId, string itemId)
        {
            var r = await Send(accountId, HttpMethod.Get, "/drives/" + driveId + "/items/" + itemId + "?$select=" + ItemSelect).ConfigureAwait(false);
            if (!r.Ok) throw Fail(r, "Datei abfragen");
            return DriveItem.From(r.Data);
        }

        /// <summary>Freigabelink → Datei (https://learn.microsoft.com/graph/api/shares-get)</summary>
        internal static async Task<DriveItem> ResolveLink(string accountId, string link)
        {
            string encoded = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(link.Trim())).TrimEnd('=').Replace('/', '_').Replace('+', '-');
            var r = await Send(accountId, HttpMethod.Get, "/shares/" + encoded + "/driveItem?$select=" + ItemSelect).ConfigureAwait(false);
            if (!r.Ok) throw Fail(r, "Freigabelink auflösen");
            return DriveItem.From(r.Data);
        }

        internal static async Task<byte[]> Download(string accountId, string driveId, string itemId)
        {
            var r = await Send(accountId, HttpMethod.Get, "/drives/" + driveId + "/items/" + itemId + "/content", binary: true).ConfigureAwait(false);
            if (!r.Ok) throw Fail(r, "Herunterladen");
            return r.Bytes;
        }

        /// <summary>Versionsverlauf einer Datei: Versions-ID und Änderungszeit (UTC), neueste zuerst; die erste ist die aktuelle Fassung</summary>
        internal static async Task<List<KeyValuePair<string, DateTime>>> Versions(string accountId, string driveId, string itemId)
        {
            var r = await Send(accountId, HttpMethod.Get, "/drives/" + driveId + "/items/" + itemId + "/versions?$select=id,lastModifiedDateTime").ConfigureAwait(false);
            if (!r.Ok) throw Fail(r, "Versionsverlauf lesen");
            var list = new List<KeyValuePair<string, DateTime>>();
            foreach (var v in r.Data.Arr("value"))
                list.Add(new KeyValuePair<string, DateTime>(v.Str("id"), v.Date("lastModifiedDateTime")));
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            return list;
        }

        /// <summary>Ältere Fassung aus dem OneDrive-Versionsverlauf; null, wenn es sie nicht (mehr) gibt</summary>
        internal static async Task<byte[]> DownloadVersion(string accountId, string driveId, string itemId, string versionId)
        {
            var r = await Send(accountId, HttpMethod.Get, "/drives/" + driveId + "/items/" + itemId + "/versions/" + versionId + "/content",
                binary: true).ConfigureAwait(false);
            if (r.Status == HttpStatusCode.NotFound) return null;
            if (!r.Ok) throw Fail(r, "Version " + versionId + " herunterladen");
            return r.Bytes;
        }

        /// <summary>Hochladen mit Versionsprüfung: weicht der cTag der Cloud-Datei ab, lehnt der Server mit 412 ab</summary>
        internal static async Task<UploadResult> Upload(string accountId, string driveId, string itemId, byte[] data, string ifMatchCTag)
        {
            var content = new ByteArrayContent(data);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var r = await Send(accountId, HttpMethod.Put, "/drives/" + driveId + "/items/" + itemId + "/content", content, ifMatchCTag).ConfigureAwait(false);
            if (r.Status == HttpStatusCode.PreconditionFailed) return new UploadResult { Conflict = true };
            if (!r.Ok) throw Fail(r, "Hochladen");
            return new UploadResult { Item = DriveItem.From(r.Data) };
        }

        /// <summary>Push-Abo des Laufwerks (https://learn.microsoft.com/graph/api/subscriptions-socketio)</summary>
        internal static async Task<PushSubscription> Subscribe(string accountId, string driveId)
        {
            var r = await Send(accountId, HttpMethod.Get, "/drives/" + driveId + "/root/subscriptions/socketIo").ConfigureAwait(false);
            if (!r.Ok) throw Fail(r, "Push-Abo");
            return ToSubscription(r.Data);
        }

        /// <summary>Abo verlängern, Token und Verbindung bleiben gleich (max. ~30 Tage)</summary>
        internal static async Task<PushSubscription> Renew(string accountId, string driveId, string subscriptionId, DateTime untilUtc)
        {
            var body = new StringContent(Json.Write(new Dictionary<string, object>
            {
                { "expirationDateTime", untilUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) }
            }), Encoding.UTF8, "application/json");
            var r = await Send(accountId, new HttpMethod("PATCH"), "/drives/" + driveId + "/root/subscriptions/" + subscriptionId, body).ConfigureAwait(false);
            if (!r.Ok) throw Fail(r, "Push-Abo verlängern");
            return ToSubscription(r.Data);
        }

        private static PushSubscription ToSubscription(Dictionary<string, object> d)
        {
            DateTime exp;
            DateTime.TryParse(d.Str("expirationDateTime"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out exp);
            return new PushSubscription { Id = d.Str("id"), NotificationUrl = d.Str("notificationUrl"), ExpiresUtc = exp };
        }
    }

    internal sealed class GraphException : Exception
    {
        public readonly HttpStatusCode Status;

        public GraphException(HttpStatusCode status, string message) : base(message)
        {
            Status = status;
        }
    }
}
