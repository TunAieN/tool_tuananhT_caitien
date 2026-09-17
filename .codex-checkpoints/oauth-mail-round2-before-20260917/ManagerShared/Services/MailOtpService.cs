using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToolTikTokV12.Services;

public enum MailOtpErrorCode
{
    None,
    MailTokenInvalid,
    MailPermissionError,
    MailApiError,
    OtpTimeout,
    OtpNotFound,
    OtpInvalid
}

public sealed record MailOtpRequest(
    string Email,
    string RefreshToken,
    string ClientId,
    DateTimeOffset RequestedAt,
    TimeSpan Timeout,
    TimeSpan PollInterval);

public sealed record MailOtpResult(
    bool Success,
    string Otp,
    MailOtpErrorCode ErrorCode,
    string MessageId = "")
{
    public static MailOtpResult Fail(MailOtpErrorCode code)
        => new(false, "", code);
}

public sealed record MailOtpMessage(
    string Id,
    DateTimeOffset ReceivedDateTime,
    string Sender,
    string Subject,
    string BodyPreview);

public sealed class MailOtpService : IDisposable
{
    const string TokenEndpoint =
        "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    const string InboxEndpoint =
        "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages" +
        "?$select=id,receivedDateTime,sender,subject,bodyPreview" +
        "&$orderby=receivedDateTime%20desc&$top=25";
    const string TikTokSender = "noreply@account.tiktok.com";

    static readonly Regex OtpRegex = new(@"(?<!\d)\d{6}(?!\d)", RegexOptions.Compiled);
    static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(5);

    sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

    readonly HttpClient _http;
    readonly bool _ownsHttpClient;
    readonly ConcurrentDictionary<string, CachedToken> _tokenCache = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _usedMessageIds = new(StringComparer.Ordinal);
    readonly object _usedMessageGate = new();

    public MailOtpService(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<MailOtpResult> GetTikTokOtpAsync(
        MailOtpRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.RefreshToken)
            || string.IsNullOrWhiteSpace(request.ClientId))
        {
            return MailOtpResult.Fail(MailOtpErrorCode.MailTokenInvalid);
        }

        var interval = request.PollInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(2)
            : request.PollInterval;
        var timeout = request.Timeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(60)
            : request.Timeout;
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var fetched = await FetchRecentMessagesAsync(request, ct);
            if (fetched.ErrorCode != MailOtpErrorCode.None)
                return MailOtpResult.Fail(fetched.ErrorCode);

            HashSet<string> usedSnapshot;
            lock (_usedMessageGate)
                usedSnapshot = new HashSet<string>(_usedMessageIds, StringComparer.Ordinal);

            var candidate = SelectNewestCandidate(
                fetched.Messages,
                request.RequestedAt,
                usedSnapshot,
                ClockTolerance);

            if (candidate is not null)
            {
                var otp = TryExtractOtp(candidate.Subject)
                          ?? TryExtractOtp(candidate.BodyPreview);
                if (otp is null)
                    return MailOtpResult.Fail(MailOtpErrorCode.OtpInvalid);

                lock (_usedMessageGate)
                    _usedMessageIds.Add(candidate.Id);

                return new MailOtpResult(true, otp, MailOtpErrorCode.None, candidate.Id);
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                break;

            await Task.Delay(remaining < interval ? remaining : interval, ct);
        }

        return MailOtpResult.Fail(MailOtpErrorCode.OtpTimeout);
    }

    public static string? TryExtractOtp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = OtpRegex.Match(text);
        return match.Success ? match.Value : null;
    }

    public static MailOtpMessage? SelectNewestCandidate(
        IEnumerable<MailOtpMessage> messages,
        DateTimeOffset requestedAt,
        ISet<string>? usedMessageIds = null,
        TimeSpan? tolerance = null)
    {
        var minimumReceivedAt = requestedAt - (tolerance ?? TimeSpan.Zero);

        return messages
            .Where(x => x.Sender.Equals(TikTokSender, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.ReceivedDateTime >= minimumReceivedAt)
            .Where(x => usedMessageIds is null || !usedMessageIds.Contains(x.Id))
            .Where(x => TryExtractOtp(x.Subject) is not null || TryExtractOtp(x.BodyPreview) is not null)
            .OrderByDescending(x => x.ReceivedDateTime)
            .FirstOrDefault();
    }

    async Task<(IReadOnlyList<MailOtpMessage> Messages, MailOtpErrorCode ErrorCode)>
        FetchRecentMessagesAsync(MailOtpRequest request, CancellationToken ct)
    {
        string accessToken;
        try
        {
            accessToken = await GetAccessTokenAsync(request, ct);
        }
        catch (MailOtpException ex)
        {
            return ([], ex.ErrorCode);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, InboxEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ([], MailOtpErrorCode.MailApiError);
        }
        catch (HttpRequestException)
        {
            return ([], MailOtpErrorCode.MailApiError);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return ([], MailOtpErrorCode.MailPermissionError);
            if (!response.IsSuccessStatusCode)
                return ([], MailOtpErrorCode.MailApiError);

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!json.RootElement.TryGetProperty("value", out var values)
                    || values.ValueKind != JsonValueKind.Array)
                {
                    return ([], MailOtpErrorCode.MailApiError);
                }

                var messages = new List<MailOtpMessage>();
                foreach (var item in values.EnumerateArray())
                {
                    var id = ReadString(item, "id");
                    var receivedRaw = ReadString(item, "receivedDateTime");
                    if (id.Length == 0
                        || !DateTimeOffset.TryParse(receivedRaw, out var received))
                    {
                        continue;
                    }

                    var sender = "";
                    if (item.TryGetProperty("sender", out var senderObject)
                        && senderObject.TryGetProperty("emailAddress", out var emailAddress))
                    {
                        sender = ReadString(emailAddress, "address");
                    }

                    messages.Add(new MailOtpMessage(
                        id,
                        received,
                        sender,
                        ReadString(item, "subject"),
                        ReadString(item, "bodyPreview")));
                }

                return (messages, MailOtpErrorCode.None);
            }
            catch (JsonException)
            {
                return ([], MailOtpErrorCode.MailApiError);
            }
        }
    }

    async Task<string> GetAccessTokenAsync(MailOtpRequest request, CancellationToken ct)
    {
        var cacheKey = request.Email.Trim() + "|" + request.ClientId.Trim();
        if (_tokenCache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return cached.AccessToken;
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = request.ClientId,
            ["refresh_token"] = request.RefreshToken
        });

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(TokenEndpoint, content, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MailOtpException(MailOtpErrorCode.MailApiError);
        }
        catch (HttpRequestException)
        {
            throw new MailOtpException(MailOtpErrorCode.MailApiError);
        }

        using (response)
        {
            string payload;
            try
            {
                payload = await response.Content.ReadAsStringAsync(ct);
            }
            catch
            {
                throw new MailOtpException(MailOtpErrorCode.MailApiError);
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = ReadErrorCode(payload);
                throw new MailOtpException(
                    error.Equals("invalid_grant", StringComparison.OrdinalIgnoreCase)
                        ? MailOtpErrorCode.MailTokenInvalid
                        : response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                            ? MailOtpErrorCode.MailPermissionError
                            : MailOtpErrorCode.MailApiError);
            }

            try
            {
                using var json = JsonDocument.Parse(payload);
                var token = ReadString(json.RootElement, "access_token");
                var expiresIn = json.RootElement.TryGetProperty("expires_in", out var expiry)
                                && expiry.TryGetInt32(out var seconds)
                    ? Math.Max(60, seconds)
                    : 3600;
                if (token.Length == 0)
                    throw new MailOtpException(MailOtpErrorCode.MailTokenInvalid);

                _tokenCache[cacheKey] = new CachedToken(
                    token,
                    DateTimeOffset.UtcNow.AddSeconds(expiresIn));
                return token;
            }
            catch (JsonException)
            {
                throw new MailOtpException(MailOtpErrorCode.MailApiError);
            }
        }
    }

    static string ReadErrorCode(string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            return ReadString(json.RootElement, "error");
        }
        catch
        {
            return "";
        }
    }

    static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    sealed class MailOtpException(MailOtpErrorCode errorCode) : Exception
    {
        public MailOtpErrorCode ErrorCode { get; } = errorCode;
    }
}
