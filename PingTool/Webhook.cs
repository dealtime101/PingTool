using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PingTool
{
    // One alert, as the webhooks receive it.
    internal sealed record WebhookEvent(string Host, HostChange Change, string Text, TimeSpan? Outage, DateTimeOffset Time);

    internal sealed record WebhookResult(string Label, bool Ok, string Detail);

    internal enum WebhookFormat { Json, Slack, Discord, Ntfy }

    // What each kind of service wants in the body. The format is read from the address (Slack, Discord and
    // ntfy have well-known ones); anything else gets a small JSON that also carries a "text" field, which
    // Teams (workflows), Mattermost and most other receivers accept as it is.
    internal static class WebhookPayload
    {
        public const int DiscordLimit = 2000;

        // Only http and https addresses with a host: nothing else is ever contacted.
        public static bool TryParseUrl(string? text, out Uri url)
        {
            url = null!;
            if (!Uri.TryCreate((text ?? "").Trim(), UriKind.Absolute, out var parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
            if (parsed.Host.Length == 0) return false;
            url = parsed;
            return true;
        }

        public static WebhookFormat Detect(Uri url)
        {
            string host = url.Host.ToLowerInvariant();
            if (host == "hooks.slack.com") return WebhookFormat.Slack;
            if ((host == "discord.com" || host.EndsWith(".discord.com", StringComparison.Ordinal) || host == "discordapp.com")
                && url.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal)) return WebhookFormat.Discord;
            if (host == "ntfy.sh" || host.StartsWith("ntfy.", StringComparison.Ordinal)) return WebhookFormat.Ntfy;
            return WebhookFormat.Json;
        }

        // Where a message went, WITHOUT the path or the query: a webhook address is a secret (whoever has it can post).
        public static string Label(Uri url) =>
            url.IsDefaultPort ? $"{url.Scheme}://{url.Host}" : $"{url.Scheme}://{url.Host}:{url.Port.ToString(CultureInfo.InvariantCulture)}";

        public static string EventName(HostChange change) => change switch
        {
            HostChange.Down => "down",
            HostChange.Up => "up",
            HostChange.Degraded => "degraded",
            HostChange.Recovered => "recovered",
            HostChange.Notice => "notice",
            _ => "test",   // HostChange.None: the "--test-webhooks" message
        };

        public static HttpRequestMessage Build(Uri url, WebhookEvent e, string version)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            switch (Detect(url))
            {
                case WebhookFormat.Slack:
                    request.Content = Json(new { text = e.Text });
                    break;
                case WebhookFormat.Discord:
                    request.Content = Json(new { content = e.Text.Length <= DiscordLimit ? e.Text : e.Text[..(DiscordLimit - 3)] + "..." });
                    break;
                case WebhookFormat.Ntfy:
                    // ntfy takes the message as the body; its headers must be plain ASCII, so the host stays in the body.
                    request.Content = new StringContent(e.Text, new UTF8Encoding(false), "text/plain");
                    request.Headers.TryAddWithoutValidation("Title", "PingTool alert");
                    request.Headers.TryAddWithoutValidation("Priority", e.Change == HostChange.Down ? "4" : e.Change is HostChange.Degraded or HostChange.Notice ? "3" : "2");
                    request.Headers.TryAddWithoutValidation("Tags", e.Change switch
                    {
                        HostChange.Down => "red_circle",
                        HostChange.Degraded or HostChange.Notice => "warning",
                        _ => "green_circle",
                    });
                    break;
                default:
                    request.Content = Json(new
                    {
                        text = e.Text,
                        host = e.Host,
                        @event = EventName(e.Change),
                        outageSeconds = e.Outage is TimeSpan d ? (long?)Math.Max(0, (long)d.TotalSeconds) : null,
                        time = e.Time.ToString("o", CultureInfo.InvariantCulture),
                        source = "PingTool",
                        version,
                    });
                    break;
            }

            request.Headers.UserAgent.ParseAdd("PingTool/" + version);
            return request;
        }

        private static StringContent Json(object body) =>
            new(JsonSerializer.Serialize(body), new UTF8Encoding(false), "application/json");
    }

    // Sends alerts to the configured webhooks WITHOUT ever holding up the monitoring: Send only puts the alert
    // in a queue and returns; one background worker per webhook delivers it, with a short time limit, a few
    // retries when the receiver or the network fails for a moment, and a bounded queue (the oldest alerts go
    // first if a receiver stays down for hours).
    internal sealed class WebhookSender : IDisposable
    {
        public const int MaxQueued = 100;
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan[] DefaultRetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4) };

        private sealed class Target
        {
            public required Uri Url { get; init; }
            public required Channel<WebhookEvent> Queue { get; init; }
        }

        private readonly List<Target> targets = new();
        private readonly HttpClient http;
        private readonly string version;
        private readonly TimeSpan timeout;
        private readonly TimeSpan[] retryDelays;
        private readonly CancellationTokenSource stop = new();
        private readonly List<Task> workers = new();

        // (label, reason) once a webhook has given up on an alert. Raised from a background thread.
        public event Action<string, string>? Failed;

        private readonly TimeSpan closeGrace;

        public WebhookSender(IEnumerable<Uri> urls, string version, HttpMessageHandler? handler = null,
                             TimeSpan[]? retryDelays = null, TimeSpan? timeout = null, TimeSpan? closeGrace = null)
        {
            this.version = version;
            this.closeGrace = closeGrace ?? DefaultCloseGrace;
            this.timeout = timeout ?? DefaultTimeout;
            this.retryDelays = retryDelays ?? DefaultRetryDelays;
            // No redirects: a POST that is redirected is turned into a GET by some servers, and the alert would be lost silently.
            http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

            foreach (var url in urls)
            {
                // A receiver that stays out of reach fills the queue and the oldest alerts are dropped: it is said (once per webhook and
                // run in the window, see MainForm), with the number so far, so that nobody reads a webhook history as complete.
                int dropped = 0;
                string label = WebhookPayload.Label(url);
                var target = new Target
                {
                    Url = url,
                    Queue = Channel.CreateBounded<WebhookEvent>(new BoundedChannelOptions(MaxQueued)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                    }, _ => RaiseFailed(label, $"{Interlocked.Increment(ref dropped)} alert(s) dropped: this webhook did not take them fast enough (queue of {MaxQueued} full), so what it received is incomplete")),
                };
                targets.Add(target);
                workers.Add(Task.Run(() => Work(target)));
            }
        }

        public int Count => targets.Count;

        // Never blocks, never throws.
        public void Send(WebhookEvent e)
        {
            foreach (var target in targets) target.Queue.Writer.TryWrite(e);
        }

        // One alert to every webhook, waiting for the answers (the "--test-webhooks" check). Same retries.
        public async Task<IReadOnlyList<WebhookResult>> SendNowAsync(WebhookEvent e, CancellationToken token = default)
        {
            var results = await Task.WhenAll(targets.Select(t => DeliverAsync(t.Url, e, token)));
            return results;
        }

        private async Task Work(Target target)
        {
            try
            {
                await foreach (var e in target.Queue.Reader.ReadAllAsync(stop.Token))
                {
                    var result = await DeliverAsync(target.Url, e, stop.Token);
                    // A delivery cut short because the sender is being closed did not fail: the user quit, nothing is wrong with the receiver.
                    if (!result.Ok && !stop.IsCancellationRequested) RaiseFailed(result.Label, result.Detail);
                }
            }
            catch (OperationCanceledException)
            {
                // Disposed.
            }
        }

        // Each subscriber on its own: one that throws (a console or a file that cannot be written) must not stop the worker, which
        // would leave every later alert of this webhook in the queue for ever, nor keep the other subscribers from hearing of it.
        private void RaiseFailed(string label, string detail)
        {
            foreach (var handler in Failed?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try { ((Action<string, string>)handler)(label, detail); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"A subscriber to the webhook failure raised: {ex}"); }
            }
        }

        private async Task<WebhookResult> DeliverAsync(Uri url, WebhookEvent e, CancellationToken token)
        {
            string label = WebhookPayload.Label(url);
            string last = "";
            for (int attempt = 0; attempt <= retryDelays.Length; attempt++)
            {
                if (attempt > 0)
                {
                    try { await Task.Delay(retryDelays[attempt - 1], token); }
                    catch (OperationCanceledException) { return new WebhookResult(label, false, "cancelled"); }
                }

                var (ok, retry, detail) = await TryOnce(url, e, token);
                if (ok) return new WebhookResult(label, true, detail);
                last = detail;
                if (!retry || token.IsCancellationRequested) break;
            }

            return new WebhookResult(label, false, last);
        }

        // ok, whether trying again could help, and what to tell.
        private async Task<(bool Ok, bool Retry, string Detail)> TryOnce(Uri url, WebhookEvent e, CancellationToken token)
        {
            try
            {
                using var request = WebhookPayload.Build(url, e, version);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(timeout);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                int code = (int)response.StatusCode;
                if (code is >= 200 and < 300) return (true, false, "HTTP " + code.ToString(CultureInfo.InvariantCulture));

                // A 4xx means the address or the request is wrong: trying again will not fix it (except too-many-requests
                // and request-timeout); a 5xx is the receiver's trouble and may pass.
                bool transient = code >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout;
                return (false, transient, "HTTP " + code.ToString(CultureInfo.InvariantCulture));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return (false, true, "no answer within " + timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
            }
            catch (OperationCanceledException)
            {
                return (false, false, "cancelled");
            }
            catch (HttpRequestException ex)
            {
                // The message of a connection error never contains the address' path, but keep it short anyway.
                return (false, true, ex.InnerException?.Message ?? ex.Message);
            }
            catch (Exception ex)
            {
                // Whatever else goes wrong must end THIS delivery only: an exception escaping would silently end the
                // worker, and every later alert to this webhook with it.
                return (false, false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // How long closing waits for the alerts still queued to be sent (a "down" raised just before the user quits is the one that
        // matters), before what is left is cut short.
        public static readonly TimeSpan DefaultCloseGrace = TimeSpan.FromSeconds(2);

        public void Dispose()
        {
            foreach (var target in targets) target.Queue.Writer.TryComplete();   // no new alert; the workers send what is queued, then end
            try { Task.WaitAll(workers.ToArray(), closeGrace); }
            catch (AggregateException) { /* a worker that ended badly has nothing more to say */ }

            stop.Cancel();   // only what the grace period did not finish
            try { Task.WaitAll(workers.ToArray(), TimeSpan.FromSeconds(1)); }
            catch (AggregateException) { /* workers end by cancellation */ }
            http.Dispose();
            stop.Dispose();
        }
    }
}
