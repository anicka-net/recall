namespace Recall.Server;

/// <summary>
/// Registers reverse-proxy routes for external MCP servers.
/// Each configured proxy maps /{prefix}/* to the target URL,
/// inheriting Recall's auth middleware. Only active when
/// config.json contains "mcpProxies" entries.
/// </summary>
public static class McpProxy
{
    // Headers that must not be copied between proxy hops
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "transfer-encoding", "connection", "keep-alive", "upgrade",
        "proxy-authenticate", "proxy-authorization", "te", "trailer",
    };

    public static void MapProxies(WebApplication app, IReadOnlyList<McpProxyEntry> proxies)
    {
        foreach (var proxy in proxies)
        {
            if (string.IsNullOrWhiteSpace(proxy.Prefix) || string.IsNullOrWhiteSpace(proxy.Target))
                continue;

            var client = new HttpClient
            {
                BaseAddress = new Uri(proxy.Target.TrimEnd('/')),
                Timeout = Timeout.InfiniteTimeSpan,
            };

            var prefix = proxy.Prefix.Trim('/');

            app.Map($"/{prefix}/{{**path}}", async (HttpContext context, string? path) =>
            {
                try
                {
                    var targetPath = "/" + (path ?? "");
                    var query = context.Request.QueryString;

                    var request = new HttpRequestMessage(
                        new HttpMethod(context.Request.Method),
                        targetPath + query);

                    // Forward headers (skip hop-by-hop and Host)
                    foreach (var header in context.Request.Headers)
                    {
                        if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (HopByHopHeaders.Contains(header.Key))
                            continue;
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    }

                    // Forward body for POST/PUT/PATCH
                    if (context.Request.ContentLength > 0 || context.Request.ContentType != null)
                    {
                        request.Content = new StreamContent(context.Request.Body);
                        if (context.Request.ContentType != null)
                            request.Content.Headers.ContentType =
                                System.Net.Http.Headers.MediaTypeHeaderValue.Parse(context.Request.ContentType);
                    }

                    var response = await client.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

                    context.Response.StatusCode = (int)response.StatusCode;

                    // Copy response headers (skip hop-by-hop)
                    foreach (var header in response.Headers.Concat(response.Content.Headers))
                    {
                        if (HopByHopHeaders.Contains(header.Key))
                            continue;
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }

                    // Disable response buffering for SSE streaming
                    var bufferingFeature = context.Features
                        .Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
                    bufferingFeature?.DisableBuffering();

                    // Stream body with incremental flushing (critical for SSE)
                    await context.Response.StartAsync(context.RequestAborted);
                    var upstream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
                    var buffer = new byte[4096];
                    int read;
                    while ((read = await upstream.ReadAsync(buffer, context.RequestAborted)) > 0)
                    {
                        await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"MCP proxy error: {ex.Message}");
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = 502;
                        await context.Response.WriteAsync($"Proxy error: {ex.Message}");
                    }
                }
            });

            Console.Error.WriteLine($"MCP proxy: /{prefix}/* → {proxy.Target}");
        }
    }
}
