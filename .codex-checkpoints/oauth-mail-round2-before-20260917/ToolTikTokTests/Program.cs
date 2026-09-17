using ToolTikTokV12.Models;
using ToolTikTokV12.Services;

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
        var old = Message("old", requestedAt.AddMinutes(-2), "noreply@account.tiktok.com", "111111 is your verification code");
        Assert(MailOtpService.SelectNewestCandidate([old], requestedAt) is null, "old message must be rejected");
    }),
    ("new TikTok message accepted", () =>
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var recent = Message("new", requestedAt.AddSeconds(1), "noreply@account.tiktok.com", "222222 is your verification code");
        Assert(MailOtpService.SelectNewestCandidate([recent], requestedAt)?.Id == "new", "new TikTok message must be accepted");
    }),
    ("wrong sender rejected", () =>
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var wrong = Message("wrong", requestedAt.AddSeconds(1), "attacker@example.com", "333333 is your verification code");
        Assert(MailOtpService.SelectNewestCandidate([wrong], requestedAt) is null, "wrong sender must be rejected");
    }),
    ("used message rejected", () =>
    {
        var requestedAt = DateTimeOffset.UtcNow;
        var used = Message("used", requestedAt.AddSeconds(1), "noreply@account.tiktok.com", "444444 is your verification code");
        Assert(MailOtpService.SelectNewestCandidate([used], requestedAt, new HashSet<string> { "used" }) is null, "used message must be rejected");
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
