using AIArena.Wpf.Services;

internal static partial class Program
{
    static void NativeControlEndpointMatchesCppPrincipalNamespace()
    {
        const string userSid = "S-1-5-21-100-200-300-1001";
        const string logonSid = "S-1-5-5-12345-67890";
        const string localAppData = @"C:\NativeEndpointFixture";
        // Independent SHA-256 vector for the C++/PowerShell LF-terminated,
        // UTF-8 interactive_principal.v2 material; never uses a live identity.
        const string principalNamespace = userSid + "-logon-f3d9336d0ec7db0d31916d12f9a418fb";

        var endpoint = NativeControlEndpointResolver.FromPrincipal(userSid, logonSid, localAppData);
        Require(endpoint.PipeName == "ai-arena-control-" + principalNamespace,
            "The protobuf client must resolve the existing C++ principal pipe; APB1 changes framing, not endpoint identity");
        Require(endpoint.TokenPath == Path.Combine(localAppData, "AI Arena", "ControlPlane", principalNamespace, "control.token"),
            "The protobuf client must reuse the native app's existing protected token path");

        var otherLogon = NativeControlEndpointResolver.FromPrincipal(userSid, "S-1-5-5-12345-67891", localAppData);
        var otherUser = NativeControlEndpointResolver.FromPrincipal("S-1-5-21-100-200-300-1002", logonSid, localAppData);
        Require(endpoint.PipeName != otherLogon.PipeName && endpoint.TokenPath != otherLogon.TokenPath,
            "Separate interactive logons must not share a native pipe or token path");
        Require(endpoint.PipeName != otherUser.PipeName && endpoint.TokenPath != otherUser.TokenPath,
            "Separate Windows users must not share a native pipe or token path");

        var otherRoot = NativeControlEndpointResolver.FromPrincipal(userSid, logonSid, @"D:\NativeEndpointFixture");
        Require(endpoint.PipeName == otherRoot.PipeName && endpoint.TokenPath != otherRoot.TokenPath,
            "The local app-data directory locates the token without changing principal identity");
    }

    static void NativeControlEndpointIgnoresLocalAppDataEnvironmentOverride()
    {
        var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var knownFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Require(!string.IsNullOrWhiteSpace(knownFolder), "Windows must provide its Local Application Data known folder");
        var expected = NativeControlEndpointResolver.Resolve();
        var isolatedProfile = Path.Combine(Path.GetTempPath(), "native-endpoint-profile-" + Guid.NewGuid().ToString("N"));

        try
        {
            // No directories, token reads, or native connections are needed.
            // Only the process environment is changed, and it is restored below.
            Environment.SetEnvironmentVariable("LOCALAPPDATA", isolatedProfile);
            Require(Environment.GetEnvironmentVariable("LOCALAPPDATA") == isolatedProfile,
                "The fixture must apply the isolated profile environment override");

            var actual = NativeControlEndpointResolver.Resolve();
            Require(actual == expected,
                "Overriding LOCALAPPDATA must not relocate the protected native token or change the principal pipe");
            Require(actual.TokenPath.StartsWith(
                    Path.Combine(knownFolder, "AI Arena", "ControlPlane") + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase),
                "The protected token must remain under the Windows Local Application Data known folder");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
        }
    }

    static void NativeControlEndpointRejectsInvalidPrincipalInputs()
    {
        const string userSid = "S-1-5-21-100-200-300-1001";
        const string logonSid = "S-1-5-5-12345-67890";

        RequireRejected(() => NativeControlEndpointResolver.FromPrincipal("", logonSid, @"C:\NativeEndpointFixture"));
        RequireRejected(() => NativeControlEndpointResolver.FromPrincipal(userSid, "", @"C:\NativeEndpointFixture"));
        RequireRejected(() => NativeControlEndpointResolver.FromPrincipal(userSid, logonSid, ""));
        RequireRejected(() => NativeControlEndpointResolver.FromPrincipal(userSid + "\nlogon_sid=other", logonSid, @"C:\NativeEndpointFixture"));
        RequireRejected(() => NativeControlEndpointResolver.FromPrincipal(userSid, "..\\other", @"C:\NativeEndpointFixture"));

        static void RequireRejected(Action resolve)
        {
            try
            {
                resolve();
            }
            catch (ArgumentException)
            {
                return;
            }

            throw new InvalidOperationException("Invalid principal input must not produce a native endpoint.");
        }
    }
}
