namespace Recall.Server;

/// <summary>
/// Tracks whether the current session has elevated privileges (can see restricted data).
/// HTTP/OAuth sessions are privileged; stdio sessions are unprivileged by default.
/// </summary>
public class PrivilegeContext
{
    public bool IsPrivileged { get; set; }
}
