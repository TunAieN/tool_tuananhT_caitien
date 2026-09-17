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
    DateTimeOffset BeforeTriggerUtc,
    DateTimeOffset AfterTriggerUtc,
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
    // Graph timestamps and the local trigger clock can differ slightly. One second
    // covers timestamp precision without widening the window enough to accept an
    // OTP from a previous login attempt.
    static readonly TimeSpan TriggerClockTolerance = TimeSpan.FromSeconds(1);
    static readonly TimeSpan DefaultUsedMessageTtl = TimeSpan.FromMinutes(20);

    sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

    sealed class MailboxUsedMessageState
    {
        public object Gate { get; } = new();
        public Dictionary<string, DateTimeOffset> UsedAtByMessageId { get; } =
            new(StringComparer.Ordinal);
    }

    readonly HttpClient _http;
    readonly bool _ownsHttpClient;
    readonly Func<DateTimeOffset> _utcNow;
    readonly TimeSpan _usedMessageTtl;
    readonly ConcurrentDictionary<string, CachedToken> _tokenCache = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, SemaphoreSlim> _tokenRefreshLocks = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, MailboxUsedMessageState> _usedMessagesByMailbox = new(StringComparer.OrdinalIgnoreCase);

    public MailOtpService(
        HttpClient? httpClient = null,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? usedMessageTtl = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _usedMessageTtl = usedMessageTtl is { } ttl && ttl > TimeSpan.Zero
            ? ttl
            : DefaultUsedMessageTtl;
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
        var mailboxKey = BuildMailboxKey(request.Email, request.ClientId);
        var mailboxState = _usedMessagesByMailbox.GetOrAdd(
            mailboxKey,
            _ => new MailboxUsedMessageState());
        var deadline = _utcNow() + timeout;

        while (_utcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var fetched = await FetchRecentMessagesAsync(request, ct);
            if (fetched.ErrorCode != MailOtpErrorCode.None)
                return MailOtpResult.Fail(fetched.ErrorCode);

            MailOtpMessage? candidate;
            lock (mailboxState.Gate)
            {
                CleanupExpiredUsedMessages(mailboxState, _utcNow());
                candidate = SelectNewestCandidate(
                    fetched.Messages,
                    request.BeforeTriggerUtc,
                    request.AfterTriggerUtc,
                    mailboxState.UsedAtByMessageId.Keys.ToHashSet(StringComparer.Ordinal),
                    TriggerClockTolerance);

                // Reserve atomically inside the mailbox gate so concurrent callers
                // for the same mailbox cannot both consume the same message.
                if (candidate is not null)
                    mailboxState.UsedAtByMessageId[candidate.Id] = _utcNow();
            }

            if (candidate is not null)
            {
                var otp = TryExtractOtp(candidate.Subject)
                          ?? TryExtractOtp(candidate.BodyPreview);
                if (otp is null)
                    return MailOtpResult.Fail(MailOtpErrorCode.OtpInvalid);

                return new MailOtpResult(true, otp, MailOtpErrorCode.None, candidate.Id);
            }

            var remaining = deadline - _utcNow();
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
        DateTimeOffset beforeTriggerUtc,
        DateTimeOffset afterTriggerUtc,
        ISet<string>? usedMessageIds = null,
        TimeSpan? tolerance = null)
    {
        var triggerWindowStart = beforeTriggerUtc <= afterTriggerUtc
            ? beforeTriggerUtc
            : afterTriggerUtc;
        var minimumReceivedAt = triggerWindowStart - (tolerance ?? TimeSpan.Zero);

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
        var cacheKey = BuildMailboxKey(request.Email, request.ClientId);
        if (TryGetValidCachedToken(cacheKey, out var cachedToken))
            return cachedToken;

        var refreshLock = _tokenRefreshLocks.GetOrAdd(
            cacheKey,
            _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(ct);
        try
        {
            // Double-check after entering the per-mailbox lock. Another caller may
            // have completed the refresh while this caller was waiting.
            if (TryGetValidCachedToken(cacheKey, out cachedToken))
                return cachedToken;

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
                        _utcNow().AddSeconds(expiresIn));
                    return token;
                }
                catch (JsonException)
                {
                    throw new MailOtpException(MailOtpErrorCode.MailApiError);
                }
            }
        }
        finally
        {
            refreshLock.Release();
        }
    }

    bool TryGetValidCachedToken(string cacheKey, out string accessToken)
    {
        if (_tokenCache.TryGetValue(cacheKey, out var cached)
            && cached.ExpiresAt > _utcNow().AddMinutes(1))
        {
            accessToken = cached.AccessToken;
            return true;
        }

        accessToken = "";
        return false;
    }

    void CleanupExpiredUsedMessages(
        MailboxUsedMessageState state,
        DateTimeOffset now)
    {
        var expired = state.UsedAtByMessageId
            .Where(x => now - x.Value >= _usedMessageTtl)
            .Select(x => x.Key)
            .ToList();
        foreach (var messageId in expired)
            state.UsedAtByMessageId.Remove(messageId);
    }

    static string BuildMailboxKey(string email, string clientId)
        => email.Trim().ToLowerInvariant() + "|" + clientId.Trim().ToLowerInvariant();

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
        foreach (var refreshLock in _tokenRefreshLocks.Values)
            refreshLock.Dispose();
        if (_ownsHttpClient)
            _http.Dispose();
    }

    sealed class MailOtpException(MailOtpErrorCode errorCode) : Exception
    {
        public MailOtpErrorCode ErrorCode { get; } = errorCode;
    }
}
