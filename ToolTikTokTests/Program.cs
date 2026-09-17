using ToolTikTokV12.Models;
using ToolTikTokV12.Services;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

var tests = new List<(string Name, Action Run)>
{
    ("legacy parser", () =>
    {
        var result = TikTokAccountParser.Parse("user01|pass01");
        Assert(result.Success, "legacy line must parse");
        Assert(result.Account?.LoginMode == TikTokLoginMode.Normal, "legacy mode must be NORMAL");
        Assert(result.Account?.Username == "user01", "legacy username mismatch");
    }),
    ("oauth mail parser", () =>
    {
        var result = TikTokAccountParser.Parse(
            "user02|pass02|mail02@outlook.com|mailpass02|REFRESH02|CLIENT02|KPI02");
        Assert(result.Success, "OAuth Mail line must parse");
        Assert(result.Account?.LoginMode == TikTokLoginMode.OAuthMail, "OAuth mode mismatch");
        Assert(result.Account?.Email == "mail02@outlook.com", "OAuth email mismatch");
        Assert(result.Account?.RefreshToken == "REFRESH02", "OAuth refresh token mismatch");
    }),
    ("invalid parser", () =>
    {
        var result = TikTokAccountParser.Parse("user|pass|unexpected");
        Assert(!result.Success, "invalid line must fail");
        Assert(result.ErrorCode == "INVALID_ACCOUNT_FORMAT", "invalid error code mismatch");
    }),
    ("OTP Vietnamese subject", () =>
        Assert(MailOtpService.TryExtractOtp("205773 là mã gồm 6 chữ số của bạn") == "205773", "Vietnamese OTP mismatch")),
    ("OTP English subject", () =>
        Assert(MailOtpService.TryExtractOtp("654676 is your verification code") == "654676", "English OTP mismatch")),
    ("subject without OTP", () =>
        Assert(MailOtpService.TryExtractOtp("Your TikTok verification code") is null, "missing OTP must return null")),
    ("old message rejected", () =>
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var old = Message("old", requestedAt.AddSeconds(-30), "noreply@account.tiktok.com", "111111 is your verification code");
        Assert(MailOtpService.SelectNewestCandidate([old], requestedAt, requestedAt.AddMilliseconds(700)) is null, "old message must be rejected");
    }),
    ("old OTP rejected while new OTP accepted", () =>
    {
        var trigger = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var old = Message("old", trigger.AddSeconds(-30), "noreply@account.tiktok.com", "111111 is your verification code");
        var current = Message("current", trigger.AddSeconds(1), "noreply@account.tiktok.com", "222222 is your verification code");
        var selected = MailOtpService.SelectNewestCandidate([old, current], trigger, trigger.AddMilliseconds(700));
        Assert(selected?.Id == "current", "current trigger must select the new OTP only");
    }),
    ("new TikTok message accepted", () =>
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var recent = Message("new", requestedAt.AddSeconds(1), "noreply@account.tiktok.com", "222222 is your verification code");
        Assert(MailOtpService.SelectNewestCandidate([recent], requestedAt, requestedAt.AddMilliseconds(700))?.Id == "new", "new TikTok message must be accepted");
    }),
    ("wrong sender rejected", () =>
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var wrong = Message("wrong", requestedAt.AddSeconds(1), "attacker@example.com", "333333 is your verification code");
        Assert(MailOtpService.SelectNewestCandidate([wrong], requestedAt, requestedAt.AddMilliseconds(700)) is null, "wrong sender must be rejected");
    }),
    ("used message rejected", () =>
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var used = Message("used", requestedAt.AddSeconds(1), "noreply@account.tiktok.com", "444444 is your verification code");
        Assert(MailOtpService.SelectNewestCandidate(
            [used],
            requestedAt,
            requestedAt.AddMilliseconds(700),
            new HashSet<string> { "used" }) is null, "used message must be rejected");
    }),
    ("trigger boundary accepts fast OTP", () =>
    {
        var before = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var after = before.AddMilliseconds(700);
        var duringClick = Message("boundary", before.AddMilliseconds(400), "noreply@account.tiktok.com", "555555 is your verification code");
        Assert(MailOtpService.SelectNewestCandidate([duringClick], before, after)?.Id == "boundary",
            "OTP received while Send Code completes must be accepted");
    }),
    ("mixed account list import", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "ToolTikTokTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "accounts.txt");
            File.WriteAllLines(source,
            [
                "legacy-user|legacy-pass",
                "oauth-user|oauth-pass|mail@example.com|mail-pass|refresh-value|client-value|kpi-value",
                "invalid|three|fields"
            ]);

            var service = new TikTokAccountPoolService(root);
            var imported = service.ImportExcel(source);
            var accounts = service.Load();
            Assert(imported.Added == 2, "mixed import valid count mismatch");
            Assert(imported.InvalidFormat == 1, "mixed import invalid count mismatch");
            Assert(accounts.Count == 2, "mixed import pool count mismatch");
            Assert(accounts[0].LoginMode == TikTokLoginMode.Normal, "mixed import legacy mode mismatch");
            Assert(accounts[1].LoginMode == TikTokLoginMode.OAuthMail, "mixed import OAuth mode mismatch");
            var catalogJson = File.ReadAllText(service.CatalogPath);
            Assert(!catalogJson.Contains("legacy-pass", StringComparison.Ordinal), "TikTok password must be protected at rest");
            Assert(!catalogJson.Contains("mail-pass", StringComparison.Ordinal), "mail password must be protected at rest");
            Assert(!catalogJson.Contains("refresh-value", StringComparison.Ordinal), "refresh token must be protected at rest");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }),
    ("XLSX OAuth Mail import", () =>
    {
        var item = ImportSingleXlsx(
            ["Tài khoản", "Mật khẩu", "2FA", "Email", "Mật khẩu Email", "Refresh Token", "Client ID", "Mail KPI"],
            ["xlsx-oauth", "tiktok-pass", "TOTP123", "mail@example.com", "mail-pass", "refresh-value", "client-value", "kpi-value"]);
        Assert(item.LoginMode == TikTokLoginMode.OAuthMail, "complete XLSX OAuth row must use OAuthMail mode");
        Assert(item.Email == "mail@example.com", "XLSX email was lost");
        Assert(item.EmailPassword == "mail-pass", "XLSX email password was lost");
        Assert(item.RefreshToken == "refresh-value", "XLSX refresh token was lost");
        Assert(item.ClientId == "client-value", "XLSX client ID was lost");
        Assert(item.MailKpi == "kpi-value", "XLSX mail KPI was lost");
    }),
    ("XLSX normal import", () =>
    {
        var item = ImportSingleXlsx(
            ["Tài khoản", "Mật khẩu"],
            ["xlsx-normal", "normal-pass"]);
        Assert(item.LoginMode == TikTokLoginMode.Normal, "username/password XLSX row must remain Normal");
    }),
    ("XLSX incomplete OAuth stays normal", () =>
    {
        var item = ImportSingleXlsx(
            ["Tài khoản", "Mật khẩu", "Email", "Refresh Token", "Client ID"],
            ["xlsx-incomplete", "normal-pass", "mail@example.com", "", ""]);
        Assert(item.LoginMode == TikTokLoginMode.Normal, "incomplete XLSX OAuth credentials must remain Normal");
        Assert(item.Email == "mail@example.com", "partial XLSX email should still be preserved");
    }),
    ("XLSX OAuth headers are position independent", () =>
    {
        var item = ImportSingleXlsx(
            ["Client ID", "Mail KPI", "Tài khoản", "Refresh Token", "Email Password", "Mật khẩu", "Email", "2FA"],
            ["client-shuffled", "kpi-shuffled", "xlsx-shuffled", "refresh-shuffled", "mail-pass-shuffled", "tiktok-pass", "shuffled@example.com", "TOTP456"]);
        Assert(item.LoginMode == TikTokLoginMode.OAuthMail, "shuffled XLSX OAuth headers must use OAuthMail mode");
        Assert(item.Email == "shuffled@example.com", "shuffled XLSX email mismatch");
        Assert(item.EmailPassword == "mail-pass-shuffled", "shuffled XLSX email password mismatch");
        Assert(item.RefreshToken == "refresh-shuffled", "shuffled XLSX refresh token mismatch");
        Assert(item.ClientId == "client-shuffled", "shuffled XLSX client ID mismatch");
        Assert(item.MailKpi == "kpi-shuffled", "shuffled XLSX mail KPI mismatch");
    }),
    ("mailbox isolation concurrent", () => RunMailboxIsolationTest(sameMessageId: false).GetAwaiter().GetResult()),
    ("same message id isolated by mailbox", () => RunMailboxIsolationTest(sameMessageId: true).GetAwaiter().GetResult()),
    ("used message TTL cleanup", () => RunUsedMessageTtlTest().GetAwaiter().GetResult()),
    ("same mailbox token refresh is single flight", () => RunSameMailboxTokenRefreshTest().GetAwaiter().GetResult()),
    ("different mailbox token refresh is parallel", () => RunDifferentMailboxTokenRefreshTest().GetAwaiter().GetResult()),
    ("legacy account does not invoke mail HTTP", () =>
    {
        var handler = new FakeGraphHandler((_, _, now) => []);
        using var http = new HttpClient(handler);
        using var service = new MailOtpService(http);
        var parsed = TikTokAccountParser.Parse("legacy-only|password");
        Assert(parsed.Account?.LoginMode == TikTokLoginMode.Normal, "legacy account mode changed");
        Assert(handler.TotalRequests == 0, "legacy parsing must not call Microsoft endpoints");
    }),
    ("OAuth account without verification does not invoke mail HTTP", () =>
    {
        var handler = new FakeGraphHandler((_, _, now) => []);
        using var http = new HttpClient(handler);
        using var service = new MailOtpService(http);
        var parsed = TikTokAccountParser.Parse("oauth|password|mail@example.com|mail-password|refresh|client|kpi");
        Assert(parsed.Account?.LoginMode == TikTokLoginMode.OAuthMail, "OAuth account mode mismatch");
        Assert(handler.TotalRequests == 0, "OAuth account alone must not call Microsoft endpoints");
    })
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"RESULT total={tests.Count} passed={tests.Count - failed} failed={failed}");
return failed == 0 ? 0 : 1;

static MailOtpMessage Message(
    string id,
    DateTimeOffset received,
    string sender,
    string subject)
    => new(id, received, sender, subject, "");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static TikTokAccountPoolItem ImportSingleXlsx(
    IReadOnlyList<string> headers,
    IReadOnlyList<string> values)
{
    var root = Path.Combine(Path.GetTempPath(), "ToolTikTokXlsxTests_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var source = Path.Combine(root, "accounts.xlsx");
        WriteMinimalXlsx(source, headers, values);
        var service = new TikTokAccountPoolService(root);
        var imported = service.ImportExcel(source);
        var items = service.Load();
        Assert(imported.Added == 1, "XLSX import must add exactly one account");
        Assert(items.Count == 1, "XLSX catalog must contain exactly one account");
        return items[0];
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void WriteMinimalXlsx(
    string path,
    IReadOnlyList<string> headers,
    IReadOnlyList<string> values)
{
    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    var sheetData = new XElement(ns + "sheetData");

    AddRow(headers, 1);
    AddRow(values, 2);

    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    var entry = zip.CreateEntry("xl/worksheets/sheet1.xml");
    using var stream = entry.Open();
    new XDocument(new XElement(ns + "worksheet", sheetData)).Save(stream);

    void AddRow(IReadOnlyList<string> cells, int rowNumber)
    {
        var row = new XElement(ns + "row", new XAttribute("r", rowNumber));
        for (var column = 0; column < cells.Count; column++)
        {
            row.Add(new XElement(
                ns + "c",
                new XAttribute("r", TestColumnName(column) + rowNumber),
                new XAttribute("t", "inlineStr"),
                new XElement(ns + "is", new XElement(ns + "t", cells[column] ?? ""))));
        }
        sheetData.Add(row);
    }
}

static string TestColumnName(int zeroBasedIndex)
{
    var value = zeroBasedIndex + 1;
    var result = "";
    while (value > 0)
    {
        value--;
        result = (char)('A' + value % 26) + result;
        value /= 26;
    }
    return result;
}

static MailOtpRequest Request(string email, string clientId, DateTimeOffset now)
    => new(
        email,
        "refresh-placeholder",
        clientId,
        now,
        now.AddMilliseconds(700),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromMilliseconds(10));

static async Task RunMailboxIsolationTest(bool sameMessageId)
{
    var now = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
    var handler = new FakeGraphHandler((clientId, _, current) =>
    {
        var isA = clientId == "client-a";
        return
        [
            Message(
                sameMessageId ? "abc" : isA ? "message-001" : "message-002",
                current.AddMilliseconds(400),
                "noreply@account.tiktok.com",
                isA ? "111111 is your verification code" : "222222 is your verification code")
        ];
    });
    using var http = new HttpClient(handler);
    using var service = new MailOtpService(http, () => now);

    var results = await Task.WhenAll(
        service.GetTikTokOtpAsync(Request("a@example.com", "client-a", now)),
        service.GetTikTokOtpAsync(Request("b@example.com", "client-b", now)));

    Assert(results[0].Otp == "111111", "mailbox A received wrong OTP");
    Assert(results[1].Otp == "222222", "mailbox B received wrong OTP");
}

static async Task RunUsedMessageTtlTest()
{
    var now = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
    var handler = new FakeGraphHandler((_, _, _) =>
        [Message("message-old", now.AddMilliseconds(400), "noreply@account.tiktok.com", "666666 is your verification code")]);
    using var http = new HttpClient(handler);
    using var service = new MailOtpService(
        http,
        () => now,
        usedMessageTtl: TimeSpan.FromMinutes(10));

    var first = await service.GetTikTokOtpAsync(Request("ttl@example.com", "client-ttl", now));
    Assert(first.Success, "first TTL message use must succeed");

    now = now.AddMinutes(11);
    var afterCleanup = await service.GetTikTokOtpAsync(Request("ttl@example.com", "client-ttl", now));
    Assert(afterCleanup.Success, "message ID must be reusable after TTL cleanup");
}

static async Task RunSameMailboxTokenRefreshTest()
{
    var now = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
    var handler = new FakeGraphHandler((clientId, graphCall, current) =>
        [Message($"same-{graphCall}", current.AddMilliseconds(400), "noreply@account.tiktok.com", "777777 is your verification code")],
        tokenDelay: TimeSpan.FromMilliseconds(100));
    using var http = new HttpClient(handler);
    using var service = new MailOtpService(http, () => now);

    var tasks = Enumerable.Range(0, 5)
        .Select(_ => service.GetTikTokOtpAsync(Request("same@example.com", "client-same", now)))
        .ToArray();
    var results = await Task.WhenAll(tasks);

    Assert(results.All(x => x.Success), "all same-mailbox callers must receive an OTP");
    Assert(handler.TokenCallsFor("client-same") == 1, "same mailbox must refresh token exactly once");
}

static async Task RunDifferentMailboxTokenRefreshTest()
{
    var now = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
    var handler = new FakeGraphHandler((clientId, graphCall, current) =>
        [Message($"{clientId}-{graphCall}", current.AddMilliseconds(400), "noreply@account.tiktok.com", "888888 is your verification code")],
        tokenDelay: TimeSpan.FromMilliseconds(120));
    using var http = new HttpClient(handler);
    using var service = new MailOtpService(http, () => now);

    var tasks = Enumerable.Range(1, 5)
        .Select(i => service.GetTikTokOtpAsync(Request($"mail{i}@example.com", $"client-{i}", now)))
        .ToArray();
    var results = await Task.WhenAll(tasks);

    Assert(results.All(x => x.Success), "all different-mailbox callers must receive an OTP");
    Assert(handler.MaxConcurrentTokenRequests > 1, "different mailbox token refreshes must not use a global lock");
}

sealed class FakeGraphHandler(
    Func<string, int, DateTimeOffset, IReadOnlyList<MailOtpMessage>> messagesFactory,
    TimeSpan? tokenDelay = null) : HttpMessageHandler
{
    readonly ConcurrentDictionary<string, int> _tokenCalls = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, int> _graphCalls = new(StringComparer.OrdinalIgnoreCase);
    int _totalRequests;
    int _activeTokenRequests;
    int _maxConcurrentTokenRequests;

    public int TotalRequests => Volatile.Read(ref _totalRequests);
    public int MaxConcurrentTokenRequests => Volatile.Read(ref _maxConcurrentTokenRequests);
    public int TokenCallsFor(string clientId) => _tokenCalls.TryGetValue(clientId, out var count) ? count : 0;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _totalRequests);
        if (request.Method == HttpMethod.Post)
        {
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            var clientId = ReadFormValue(form, "client_id");
            _tokenCalls.AddOrUpdate(clientId, 1, (_, count) => count + 1);
            var active = Interlocked.Increment(ref _activeTokenRequests);
            UpdateMaximum(ref _maxConcurrentTokenRequests, active);
            try
            {
                if (tokenDelay is { } delay && delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeTokenRequests);
            }

            return Json(HttpStatusCode.OK, new { access_token = "token-" + clientId, expires_in = 3600 });
        }

        var token = request.Headers.Authorization?.Parameter ?? "";
        var graphClientId = token.StartsWith("token-", StringComparison.Ordinal)
            ? token[6..]
            : "unknown";
        var graphCall = _graphCalls.AddOrUpdate(graphClientId, 1, (_, count) => count + 1);
        var now = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
        var messages = messagesFactory(graphClientId, graphCall, now);
        return Json(HttpStatusCode.OK, new
        {
            value = messages.Select(x => new
            {
                id = x.Id,
                receivedDateTime = x.ReceivedDateTime,
                sender = new { emailAddress = new { address = x.Sender } },
                subject = x.Subject,
                bodyPreview = x.BodyPreview
            })
        });
    }

    static HttpResponseMessage Json(HttpStatusCode status, object value)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };

    static string ReadFormValue(string form, string name)
    {
        foreach (var pair in form.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == name)
                return Uri.UnescapeDataString(parts[1].Replace('+', ' '));
        }
        return "";
    }

    static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }
}
