using Microsoft.AspNetCore.Http;

namespace Recall.Server;

/// <summary>
/// Tracks whether the current session has elevated privileges (can see restricted data).
/// HTTP mode: reads from HttpContext.Items set by auth middleware (survives DI scope changes).
/// Stdio mode: constructed without IHttpContextAccessor, always unprivileged.
/// </summary>
public class PrivilegeContext
{
    private readonly IHttpContextAccessor? _http;

    public PrivilegeContext(IHttpContextAccessor? http = null) => _http = http;

    public bool IsPrivileged => _http?.HttpContext?.Items["Privileged"] is true;
}
