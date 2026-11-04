using System;
using System.Collections.Generic;

namespace Surprise.Core;

public sealed record ControllerSnapshot(List<ControllerIdentity> Controllers, string? Warning = null);
public sealed record RuntimeReply(bool Ok, string Status, bool Consumed = false);
public static class Protocol
{
    public const int Version = 3;
    public static string RuntimePipe(Guid installationId) => "Surprise.Runtime." + installationId.ToString("N");
}
