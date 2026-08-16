namespace PoMode.Shared.Session;

public sealed record SessionInfo(string UserName, IReadOnlyList<string> Roles);
