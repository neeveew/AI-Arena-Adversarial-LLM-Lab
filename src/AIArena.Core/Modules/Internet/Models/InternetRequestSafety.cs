using System.Text.RegularExpressions;

namespace AIArena.Core.Models;

internal static class InternetRequestSafety
{
    internal const int MaximumOutboundUrlLength = 2048;

    private static readonly Regex ObviousSecretRegex = new(
        @"(?ix)(?:\b(?:api[_\s-]?key|access[_\s-]?token|auth(?:orization)?|bearer|client[_\s-]?secret|password|passwd|private[_\s-]?key|refresh[_\s-]?token)\b\s*(?::|=|\s)\s*[""']?[A-Za-z0-9_+./~=-]{8,}|\b(?:sk-(?:proj-)?[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[baprs]-[A-Za-z0-9-]{16,}|AKIA[A-Z0-9]{16}|AIza[A-Za-z0-9_-]{30,}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})\b)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveUrlParameterRegex = new(
        @"(?i)(?:[?&](?:api[_-]?key|access[_-]?token|auth|authorization|client[_-]?secret|code|credential|jwt|key|password|refresh[_-]?token|secret|session|sig|signature|token)=)[^&#\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EmailAddressRegex = new(
        @"(?i)(?<![A-Z0-9._%+-])[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,63}(?![A-Z0-9._%+-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CredentialTokenRegex = new(
        @"[A-Za-z0-9_+/=-]{24,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AbsoluteHttpUrlRegex = new(
        @"(?i)https?://[^\s<>""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> PublicDigestParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "checksum", "commit", "digest", "hash", "revision", "sha", "sha1", "sha256", "sha512"
    };

    internal static bool IsSafeOutboundRequest(InternetToolRequest request, out string error)
    {
        var tool = request.Tool ?? "";
        var url = request.Url ?? "";
        if (tool.Equals(InternetToolNames.FetchUrl, StringComparison.OrdinalIgnoreCase)
            && url.Length > MaximumOutboundUrlLength)
        {
            error = $"Internet request blocked because the URL exceeds {MaximumOutboundUrlLength} characters.";
            return false;
        }

        if (ContainsSensitivePayload(request.Query)
            || ContainsSensitivePayload(url)
            || ContainsSensitivePayload(request.Input)
            || ContainsSensitivePayload(request.Reason)
            || (request.Options ?? new Dictionary<string, System.Text.Json.JsonElement>())
                .Any(option => ContainsSensitivePayload(option.Key)
                    || (option.Value.ValueKind != System.Text.Json.JsonValueKind.Undefined
                        && ContainsSensitivePayload(option.Value.GetRawText()))))
        {
            error = "Internet request blocked because it may contain private information, a secret, or a credential.";
            return false;
        }

        error = "";
        return true;
    }

    internal static bool ContainsSensitivePayload(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value;
        try
        {
            text = Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
        }

        if (ObviousSecretRegex.IsMatch(text)
            || SensitiveUrlParameterRegex.IsMatch(text)
            || EmailAddressRegex.IsMatch(text))
        {
            return true;
        }

        foreach (Match match in CredentialTokenRegex.Matches(text))
        {
            var token = match.Value.Trim('=', '-', '_');
            if (LooksLikeHighEntropyCredential(token))
            {
                if (IsOrdinaryPublicUrlIdentifier(text, match))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeHighEntropyCredential(string token)
    {
        if (token.Length < 24)
        {
            return false;
        }

        var compact = token.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        if (compact.Length >= 32 && compact.All(Uri.IsHexDigit))
        {
            return true;
        }

        var categories = 0;
        categories += token.Any(char.IsLower) ? 1 : 0;
        categories += token.Any(char.IsUpper) ? 1 : 0;
        categories += token.Any(char.IsDigit) ? 1 : 0;
        categories += token.Any(character => character is '+' or '/' or '_' or '-' or '=') ? 1 : 0;
        return token.Length >= 28 && categories >= 3 && token.Distinct().Count() >= 12;
    }

    private static bool IsOrdinaryPublicUrlIdentifier(string text, Match credentialMatch)
    {
        foreach (Match urlMatch in AbsoluteHttpUrlRegex.Matches(text))
        {
            var urlText = TrimUrlForInspection(urlMatch.Value);
            var urlEnd = urlMatch.Index + urlText.Length;
            if (credentialMatch.Index < urlMatch.Index
                || credentialMatch.Index + credentialMatch.Length > urlEnd
                || !Uri.TryCreate(urlText, UriKind.Absolute, out var uri)
                || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                continue;
            }

            var relativeCredentialStart = credentialMatch.Index - urlMatch.Index;
            var queryStart = urlText.IndexOf('?');
            if (queryStart < 0 || relativeCredentialStart < queryStart)
            {
                var credentialLikeSegments = uri.AbsolutePath
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.UnescapeDataString)
                    .Where(LooksLikeHighEntropyCredential)
                    .ToArray();
                return credentialLikeSegments.Length > 0
                    && credentialLikeSegments.All(IsPublicDigestIdentifier);
            }

            var query = urlText[(queryStart + 1)..];
            var partStart = queryStart + 1;
            foreach (var part in query.Split('&'))
            {
                var partEnd = partStart + part.Length;
                var credentialEnd = relativeCredentialStart + credentialMatch.Length;
                if (relativeCredentialStart < partEnd && credentialEnd > partStart)
                {
                    var separator = part.IndexOf('=');
                    if (separator <= 0)
                    {
                        return false;
                    }

                    var name = Uri.UnescapeDataString(part[..separator]);
                    var value = Uri.UnescapeDataString(part[(separator + 1)..]);
                    return PublicDigestParameterNames.Contains(name)
                        && IsPublicDigestIdentifier(value);
                }

                partStart = partEnd + 1;
            }
        }

        return false;
    }

    private static bool IsPublicDigestIdentifier(string value)
    {
        var compact = value.Replace("-", "", StringComparison.Ordinal);
        return compact.Length is >= 32 and <= 128 && compact.All(Uri.IsHexDigit);
    }

    private static string TrimUrlForInspection(string value)
    {
        var candidate = value.TrimEnd('.', ',', ';', ':', '!', '?', ']', '}');
        while (candidate.EndsWith(')')
            && candidate.Count(character => character == ')') > candidate.Count(character => character == '('))
        {
            candidate = candidate[..^1];
        }

        return candidate;
    }
}
