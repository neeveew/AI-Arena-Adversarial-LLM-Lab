using System.Security.Cryptography;
using System.Text;

namespace AIArena.Wpf.Services;

/// <summary>
/// DPAPI (current user) at-rest protection for provider API tokens. Values are stored
/// as "dpapi:v1:&lt;base64&gt;". Legacy "dpapi:" envelopes remain readable, but an
/// invalid or cross-user envelope fails closed. Protected values cannot be read by
/// other Windows users or from a copied data folder.
/// </summary>
internal static class SecretProtection
{
    private const string Prefix = "dpapi:v1:";
    private const string LegacyPrefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIArena.ApiToken.v1");

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "";
        }

        if (TryUnprotectEnvelope(value, out _))
        {
            return value;
        }

        try
        {
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(protectedBytes);
        }
        catch (CryptographicException ex)
        {
            // Fail closed: silently returning the original value would write a
            // newly supplied provider credential to the snapshot as plaintext.
            throw new CryptographicException(
                "Windows could not protect the provider credential; no snapshot was written.",
                ex);
        }
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "";
        }

        if (value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // New envelopes are unambiguous. Corruption or a copied value from a
            // different Windows user fails closed instead of becoming a token.
            return TryUnprotectPayload(value[Prefix.Length..], out var plaintext)
                ? plaintext
                : "";
        }

        if (value.StartsWith(LegacyPrefix, StringComparison.Ordinal))
        {
            // Legacy protected values used only "dpapi:". Their representation is
            // ambiguous with plaintext, so any decryption failure must fail closed.
            // Newly entered tokens beginning with either prefix are wrapped by
            // Protect in the unambiguous v1 envelope before they are persisted.
            return TryUnprotectPayload(value[LegacyPrefix.Length..], out var plaintext)
                ? plaintext
                : "";
        }

        return value;
    }

    private static bool TryUnprotectEnvelope(string value, out string plaintext)
    {
        if (value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return TryUnprotectPayload(value[Prefix.Length..], out plaintext);
        }

        if (value.StartsWith(LegacyPrefix, StringComparison.Ordinal))
        {
            return TryUnprotectPayload(value[LegacyPrefix.Length..], out plaintext);
        }

        plaintext = "";
        return false;
    }

    private static bool TryUnprotectPayload(string payload, out string plaintext)
    {
        try
        {
            var bytes = Convert.FromBase64String(payload);
            plaintext = Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            plaintext = "";
            return false;
        }
    }
}
