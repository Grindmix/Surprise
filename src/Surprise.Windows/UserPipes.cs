using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Surprise.Windows;

public static class UserPipes
{
    // CurrentUserOnly uses the token's Owner SID in .NET, which can differ from User
    // for restricted/elevated tokens. Explicit user ownership avoids that mismatch.
    public static NamedPipeServerStream Server(string name, PipeDirection direction)
    {
        var sid = WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity(); security.SetOwner(sid); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, direction, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            4096, 4096, security, HandleInheritability.None);
    }
    public static NamedPipeClientStream Client(string name, PipeDirection direction) => new(".", name, direction, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    public static void VerifyServerOwner(NamedPipeClientStream pipe)
    {
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (!WindowsIdentity.GetCurrent().User!.Equals(owner)) throw new UnauthorizedAccessException("Pipe server belongs to another Windows account.");
    }
}
