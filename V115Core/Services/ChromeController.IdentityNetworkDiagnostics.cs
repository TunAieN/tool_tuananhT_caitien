using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToolTikTokV11.Services;

public sealed partial class ChromeController
{
    sealed record TikTokIdentityNetworkRequest(
        string RequestId,
        string CorrelationId,
        string Url,
        string Method,
        string ResourceType,
        string PostData,
        long SentUnixMs,
        bool IsProfileUpdate,
        bool IsAvatarRequest);

    sealed record TikTokIdentityNetworkResponse(
        string RequestId,
        string CorrelationId,
        string Url,
        string Method,
        int StatusCode,
        long ReceivedUnixMs,
        bool IsProfileUpdate,
        bool IsAvatarRequest);

    sealed record TikTokIdentitySavePayloadDiagnostic(
        bool PayloadAvailable,
        bool HasNickname,
        int NicknameLength,
        bool NicknameMatchesExpected,
        bool HasBio,
        int BioLength,
        bool BioMatchesExpected,
        bool HasAvatarField,
        bool AvatarFieldNonEmpty,
        string FieldNames);

    sealed record TikTokIdentityBusinessResponseDiagnostic(
        bool? BusinessSuccess,
        string StatusCode,
        string ErrorCode,
        string Message,
        string MediaIdFingerprint);

    static string IdentityNetworkTimestamp(long unixMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    static bool IsTikTokProfileUpdateRequest(string url, string method)
    {
        if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.Equals("/api/update/profile/", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsTikTokAvatarRequest(string url, string method)
    {
        if (method is not ("POST" or "PUT" or "PATCH")) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();
        var tikTokHost = host.EndsWith("tiktok.com", StringComparison.Ordinal)
            || host.EndsWith("tiktokv.com", StringComparison.Ordinal)
            || host.EndsWith("tiktokcdn.com", StringComparison.Ordinal)
            || host.Contains("byteoversea", StringComparison.Ordinal);
        if (!tikTokHost) return false;
        return path.Contains("avatar", StringComparison.Ordinal)
            || path.Contains("upload", StringComparison.Ordinal)
            || path.Contains("upload/image", StringComparison.Ordinal)
            || path.Contains("image/upload", StringComparison.Ordinal)
            || path.Contains("imagex", StringComparison.Ordinal);
    }

    static bool ShouldObserveTikTokIdentityRequest(string url, string method)
        => IsTikTokProfileUpdateRequest(url, method) || IsTikTokAvatarRequest(url, method);

    static TikTokIdentitySavePayloadDiagnostic AnalyzeTikTokIdentitySavePayload(
        string? payload,
        string expectedNickname,
        string expectedBio)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        static string DecodeFormPart(string value)
        {
            try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
            catch { return value; }
        }

        void Add(string name, string value)
        {
            name = Regex.Replace(name ?? "", @"[^A-Za-z0-9_.\-\[\]]", "");
            if (name.Length == 0 || name.Length > 80) return;
            if (!fields.TryGetValue(name, out var values)) fields[name] = values = [];
            values.Add(value ?? "");
        }

        void VisitJson(JsonElement element, int depth)
        {
            if (depth > 7) return;
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        VisitJson(property.Value, depth + 1);
                    else
                        Add(property.Name, property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString() ?? ""
                            : property.Value.ToString());
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) VisitJson(item, depth + 1);
            }
        }

        var raw = payload ?? "";
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var parsed = false;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                VisitJson(doc.RootElement, 0);
                parsed = true;
            }
            catch (JsonException) { }

            if (!parsed && raw.Contains("Content-Disposition:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match match in Regex.Matches(
                    raw,
                    "name=\\\"(?<name>[^\\\"]+)\\\"[^\\r\\n]*\\r?\\n(?:Content-Type:[^\\r\\n]*\\r?\\n)?\\r?\\n(?<value>.*?)(?=\\r?\\n--|$)",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline))
                    Add(match.Groups["name"].Value, match.Groups["value"].Value.TrimEnd('\r', '\n'));
                parsed = fields.Count > 0;
            }

            if (!parsed)
            {
                foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = part.IndexOf('=');
                    var name = DecodeFormPart(separator >= 0 ? part[..separator] : part);
                    var value = DecodeFormPart(separator >= 0 ? part[(separator + 1)..] : "");
                    Add(name, value);
                }
            }
        }

        static string SemanticKey(string key)
            => Regex.Replace(key, @"[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        static bool IsNicknameKey(string key)
            => SemanticKey(key) is "nickname" or "displayname" or "displaynickname";
        static bool IsBioKey(string key)
            => SemanticKey(key) is "bio" or "biography" or "signature" or "description";
        static bool IsAvatarKey(string key)
        {
            var semantic = SemanticKey(key);
            return semantic.Contains("avatar", StringComparison.Ordinal)
                || semantic is "imageuri" or "imageurl" or "photoid" or "photouri";
        }

        var nicknameValues = fields.Where(pair => IsNicknameKey(pair.Key)).SelectMany(pair => pair.Value).ToList();
        var bioValues = fields.Where(pair => IsBioKey(pair.Key)).SelectMany(pair => pair.Value).ToList();
        var avatarValues = fields.Where(pair => IsAvatarKey(pair.Key)).SelectMany(pair => pair.Value).ToList();
        var nickname = nicknameValues.FirstOrDefault() ?? "";
        var biography = bioValues.FirstOrDefault() ?? "";
        var safeFieldNames = fields.Keys
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(40);

        return new TikTokIdentitySavePayloadDiagnostic(
            raw.Length > 0,
            nicknameValues.Count > 0,
            nickname.Length,
            nicknameValues.Any(value => string.Equals(
                NormalizeTikTokIdentityText(value),
                NormalizeTikTokIdentityText(expectedNickname),
                StringComparison.Ordinal)),
            bioValues.Count > 0,
            biography.Length,
            bioValues.Any(value => string.Equals(
                NormalizeTikTokIdentityText(value),
                NormalizeTikTokIdentityText(expectedBio),
                StringComparison.Ordinal)),
            avatarValues.Count > 0,
            avatarValues.Any(value => !string.IsNullOrWhiteSpace(value)),
            "[" + string.Join(',', safeFieldNames) + "]");
    }

    static TikTokIdentityBusinessResponseDiagnostic AnalyzeTikTokIdentityBusinessResponse(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return new(null, "", "", "body-unavailable", "");

        string statusCode = "";
        string errorCode = "";
        string genericCode = "";
        string message = "";
        bool? explicitSuccess = null;
        string mediaIdentifier = "";
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            void Visit(JsonElement element, int depth)
            {
                if (depth > 7) return;
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in element.EnumerateObject())
                    {
                        var key = Regex.Replace(property.Name, @"[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
                        if (statusCode.Length == 0 && key is "statuscode" or "status") statusCode = property.Value.ToString();
                        else if (errorCode.Length == 0 && key is "errorcode" or "errno") errorCode = property.Value.ToString();
                        else if (genericCode.Length == 0 && key == "code") genericCode = property.Value.ToString();
                        else if (message.Length == 0 && key is "statusmsg" or "message" or "errormessage" or "description")
                            message = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.ToString();
                        else if (explicitSuccess is null && key is "success" or "ok")
                        {
                            if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                                explicitSuccess = property.Value.GetBoolean();
                            else if (bool.TryParse(property.Value.ToString(), out var parsed)) explicitSuccess = parsed;
                        }
                        if (mediaIdentifier.Length == 0
                            && key is "avataruri" or "avatarid" or "imageuri" or "imageid" or "objectid" or "uploadid"
                            && property.Value.ValueKind == JsonValueKind.String)
                            mediaIdentifier = property.Value.GetString() ?? "";
                        if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                            Visit(property.Value, depth + 1);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray()) Visit(item, depth + 1);
                }
            }
            Visit(doc.RootElement, 0);
        }
        catch (JsonException)
        {
            return new(null, "", "", "non-json-response", "");
        }

        static bool? SuccessFromCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            return code.Trim().ToLowerInvariant() is "0" or "200" or "ok" or "success";
        }

        var businessSuccess = explicitSuccess
            ?? SuccessFromCode(errorCode)
            ?? SuccessFromCode(statusCode)
            ?? SuccessFromCode(genericCode);
        message = Regex.Replace(message, @"\s+", " ").Trim();
        if (message.Length > 240) message = message[..240] + "…";
        var fingerprint = "";
        if (!string.IsNullOrWhiteSpace(mediaIdentifier))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(mediaIdentifier));
            fingerprint = "sha256:" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
        }
        return new(businessSuccess, statusCode, errorCode.Length > 0 ? errorCode : genericCode, message, fingerprint);
    }
}
