namespace NgBubbles.Core;

public sealed record ConnectionProfile(string RemoteServer, string LocalServer, string ComputerId, bool LocalAvailable, bool RemoteManaged)
{
    public string PreviousRemote { get; init; } = "";
}
