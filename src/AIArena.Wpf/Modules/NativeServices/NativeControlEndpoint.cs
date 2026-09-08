using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AIArena.Wpf.Services;

internal sealed record NativeControlEndpoint(string PipeName, string TokenPath);

/// <summary>
/// Resolves the native app's protected endpoint for this user and interactive
/// logon. Resolution computes paths only; it never creates or reads a token.
/// </summary>
internal static class NativeControlEndpointResolver
{
    private const uint TokenQuery = 0x0008;
    private const int TokenLogonSid = 28;
    private const uint SeGroupLogonId = 0xC0000000;
    private const int ErrorInsufficientBuffer = 122;
    private const uint MaximumTokenInformationBytes = 65_536;

    // The APB1 protobuf preamble selects the wire format on the existing
    // principal-scoped native pipe; framing belongs to the client.
    internal static NativeControlEndpoint Resolve()
    {
        // Match the native host's FOLDERID_LocalAppData lookup. Profile test
        // overrides must not redirect this principal's protected credentials.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The Windows Local Application Data folder is unavailable; the protected native endpoint cannot be resolved.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows token has no user SID.");
        return FromPrincipal(userSid, CurrentLogonSid(), localAppData);
    }

    internal static NativeControlEndpoint FromPrincipal(
        string userSid,
        string logonSid,
        string localAppData)
    {
        ValidateSid(userSid, nameof(userSid));
        ValidateSid(logonSid, nameof(logonSid));
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);

        // Literal LF and UTF-8 without a BOM match Get-AIArenaControlEndpoint
        // and InteractivePrincipal.cpp, including the final newline.
        var material = $"ai_arena.interactive_principal.v2\nsid={userSid}\nlogon_sid={logonSid}\n";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        var principalNamespace = $"{userSid}-logon-{digest[..32]}";
        return new NativeControlEndpoint(
            "ai-arena-control-" + principalNamespace,
            Path.Combine(localAppData, "AI Arena", "ControlPlane", principalNamespace, "control.token"));
    }

    private static void ValidateSid(string sid, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sid, parameterName);
        if (!new SecurityIdentifier(sid).Value.Equals(sid, StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical Windows SID is required.", parameterName);
        }
    }

    private static string CurrentLogonSid()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using (token)
        {
            if (GetTokenInformation(token, TokenLogonSid, IntPtr.Zero, 0, out var required))
            {
                throw new InvalidOperationException("TokenLogonSid unexpectedly succeeded without storage.");
            }

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(error);
            }
            if (required < Marshal.SizeOf<TokenGroupsOne>() || required > MaximumTokenInformationBytes)
            {
                throw new InvalidOperationException("TokenLogonSid returned an invalid information size.");
            }

            var storage = Marshal.AllocHGlobal(checked((int)required));
            try
            {
                if (!GetTokenInformation(token, TokenLogonSid, storage, required, out var returned))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                if (returned < Marshal.SizeOf<TokenGroupsOne>() || returned > required)
                {
                    throw new InvalidOperationException("TokenLogonSid returned incomplete information.");
                }

                var groups = Marshal.PtrToStructure<TokenGroupsOne>(storage);
                if (groups.GroupCount != 1
                    || groups.Group.Sid == IntPtr.Zero
                    || !IsValidSid(groups.Group.Sid)
                    || (groups.Group.Attributes & SeGroupLogonId) != SeGroupLogonId)
                {
                    throw new InvalidOperationException("The current token has no single valid logon SID.");
                }

                return new SecurityIdentifier(groups.Group.Sid).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(storage);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        internal IntPtr Sid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroupsOne
    {
        internal uint GroupCount;
        internal SidAndAttributes Group;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(IntPtr sid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
