using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KeeLiveSync
{
    /// <summary>
    /// Push-Verbindung zu einem OneDrive-Laufwerk: Graph-Abo über socket.io (Engine.IO v4) mit ClientWebSocket.
    /// Meldet „etwas hat sich geändert“ (Changed), nach jedem (Wieder-)Verbinden ebenfalls einmal, damit Änderungen
    /// aus einer Verbindungslücke nicht verloren gehen. Das Abo wird beim Start und danach täglich auf 29 Tage verlängert.
    /// </summary>
    internal sealed class PushChannel
    {
        private static readonly TimeSpan RenewEvery = TimeSpan.FromHours(24);
        private static readonly TimeSpan RenewTo = TimeSpan.FromDays(29);

        private readonly string _accountId;
        private readonly string _driveId;
        private CancellationTokenSource _cts;
        private PushSubscription _subscription;

        internal event Action<string> Changed;
        internal event Action<bool> ConnectedChanged;

        internal bool Connected { get; private set; }
        internal DateTime LastNotification { get; private set; }

        internal PushChannel(string accountId, string driveId)
        {
            _accountId = accountId;
            _driveId = driveId;
        }

        internal void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Task.Run(() => Run(token));
            Task.Run(() => RenewLoop(token));
        }

        internal void Stop()
        {
            if (_cts == null) return;
            _cts.Cancel();
            _cts = null;
            SetConnected(false);
            Log.Info("Push beendet für Laufwerk " + Log.Short(_driveId));
        }

        private void SetConnected(bool value)
        {
            if (Connected == value) return;
            Connected = value;
            var h = ConnectedChanged;
            if (h != null) h(value);
        }

        private async Task<PushSubscription> EnsureSubscription()
        {
            var sub = await Graph.Subscribe(_accountId, _driveId).ConfigureAwait(false);
            if (sub.ExpiresUtc < DateTime.UtcNow + RenewTo - RenewEvery)
                sub = await RenewNow(sub).ConfigureAwait(false);
            return sub;
        }

        private async Task<PushSubscription> RenewNow(PushSubscription sub)
        {
            var renewed = await Graph.Renew(_accountId, _driveId, sub.Id, DateTime.UtcNow + RenewTo).ConfigureAwait(false);
            Log.Info("Push-Abo verlängert bis " + renewed.ExpiresUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm") + " (Laufwerk " + Log.Short(_driveId) + ")");
            if (renewed.NotificationUrl == null) renewed.NotificationUrl = sub.NotificationUrl;
            return renewed;
        }

        private async Task RenewLoop(CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(RenewEvery, cancel).ConfigureAwait(false);
                    if (_subscription != null) _subscription = await RenewNow(_subscription).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("Push-Abo verlängern fehlgeschlagen", ex);
                }
            }
        }

        private async Task Run(CancellationToken cancel)
        {
            int failures = 0;
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    _subscription = await EnsureSubscription().ConfigureAwait(false);
                    await Session(_subscription, cancel).ConfigureAwait(false);
                    failures = 0;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SignInRequiredException)
                {
                    Log.Warn("Push pausiert: erneute Anmeldung nötig");
                    SetConnected(false);
                    return;
                }
                catch (Exception ex)
                {
                    failures++;
                    Log.Warn("Push-Verbindung unterbrochen (" + failures + "): " + Log.Describe(ex));
                }
                SetConnected(false);
                if (cancel.IsCancellationRequested) return;
                int seconds = Math.Min(300, 5 * (1 << Math.Min(failures, 6)));
                Log.Debug("Push verbindet neu in " + seconds + " s");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>Eine WebSocket-Sitzung bis zur Trennung</summary>
        private async Task Session(PushSubscription sub, CancellationToken cancel)
        {
            // notificationUrl: https://<host>/notifications?token=… → Namespace /notifications
            var uri = new Uri(sub.NotificationUrl);
            string ns = uri.AbsolutePath.Replace("/callback", "");
            string query = uri.Query.TrimStart('?');
            using (var ws = new ClientWebSocket())
            {
                await ws.ConnectAsync(new Uri("wss://" + uri.Host + "/socket.io/?EIO=4&transport=websocket&" + query), cancel).ConfigureAwait(false);
                Log.Debug("Push: WebSocket offen (" + uri.Host + ")");
                var sendLock = new SemaphoreSlim(1, 1);
                Func<string, Task> send = async s =>
                {
                    await sendLock.WaitAsync(cancel).ConfigureAwait(false);
                    try
                    {
                        await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(s)), WebSocketMessageType.Text, true, cancel).ConfigureAwait(false);
                    }
                    finally
                    {
                        sendLock.Release();
                    }
                };

                var buffer = new byte[64 * 1024];
                int pingTimeoutMs = 80000;
                while (ws.State == WebSocketState.Open)
                {
                    var sb = new StringBuilder();
                    WebSocketReceiveResult res;
                    do
                    {
                        // Server pingt regelmäßig; bleibt alles still, ist die Verbindung tot
                        var receive = ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancel);
                        if (await Task.WhenAny(receive, Task.Delay(pingTimeoutMs, cancel)).ConfigureAwait(false) != receive)
                        {
                            cancel.ThrowIfCancellationRequested();
                            throw new TimeoutException("kein Lebenszeichen vom Push-Server");
                        }
                        res = await receive.ConfigureAwait(false);
                        if (res.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                    } while (!res.EndOfMessage);
                    if (res.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("Server hat geschlossen: " + ws.CloseStatus + " " + ws.CloseStatusDescription);

                    string msg = sb.ToString();
                    if (msg == "2")
                    {
                        await send("3").ConfigureAwait(false);
                    }
                    else if (msg.StartsWith("0{", StringComparison.Ordinal))
                    {
                        var open = Json.Parse(msg.Substring(1));
                        pingTimeoutMs = (int)(open.Long("pingInterval") + open.Long("pingTimeout")) + 10000;
                        await send("40" + ns + ",").ConfigureAwait(false);
                    }
                    else if (msg.StartsWith("40" + ns, StringComparison.Ordinal))
                    {
                        Log.Info("Push verbunden (Laufwerk " + Log.Short(_driveId) + ", Abo bis " + sub.ExpiresUtc.ToLocalTime().ToString("dd.MM. HH:mm") + ")");
                        SetConnected(true);
                        Raise("verbunden");
                    }
                    else if (msg.StartsWith("44" + ns, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Push-Namespace abgelehnt: " + msg);
                    }
                    else if (msg.StartsWith("42" + ns, StringComparison.Ordinal))
                    {
                        LastNotification = DateTime.Now;
                        Log.Debug("Push-Meldung erhalten (Laufwerk " + Log.Short(_driveId) + ")");
                        Raise("push");
                    }
                    else
                    {
                        Log.Debug("Push: unbekannte Nachricht " + (msg.Length > 60 ? msg.Substring(0, 60) + "…" : msg));
                    }
                }
            }
        }

        private void Raise(string reason)
        {
            var h = Changed;
            if (h == null) return;
            try
            {
                h(reason);
            }
            catch (Exception ex)
            {
                Log.Error("Fehler bei Push-Verarbeitung", ex);
            }
        }
    }
}
