using ToolTikTokV12.Models;

namespace ToolTikTokV12.Services;

public sealed record TikTokAccountParseResult(
    bool Success,
    TikTokLoginAccount? Account,
    string ErrorCode,
    string Message)
{
    public static TikTokAccountParseResult Invalid(string message)
        => new(false, null, "INVALID_ACCOUNT_FORMAT", message);
}

public static class TikTokAccountParser
{
    public static TikTokAccountParseResult Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return TikTokAccountParseResult.Invalid("Dòng tài khoản đang trống.");

        var parts = line.Split('|');
        if (parts.Length is not (2 or 7))
            return TikTokAccountParseResult.Invalid(
                "Dòng tài khoản phải có 2 trường (username|password) hoặc 7 trường OAuth Mail.");

        var username = parts[0].Trim();
        var password = parts[1].Trim();
        if (username.Length == 0 || password.Length == 0 || LooksLikeHeader(username, password))
            return TikTokAccountParseResult.Invalid("Username và password không được để trống.");

        if (parts.Length == 2)
        {
            return new TikTokAccountParseResult(
                true,
                new TikTokLoginAccount(TikTokLoginMode.Normal, username, password),
                "",
                "");
        }

        var email = parts[2].Trim();
        var emailPassword = parts[3].Trim();
        var refreshToken = parts[4].Trim();
        var clientId = parts[5].Trim();
        var mailKpi = parts[6].Trim();

        if (email.Length == 0
            || emailPassword.Length == 0
            || refreshToken.Length == 0
            || clientId.Length == 0
            || mailKpi.Length == 0)
        {
            return TikTokAccountParseResult.Invalid(
                "Account OAuth Mail phải có đủ email, email_password, refresh_token, client_id và mailKPI.");
        }

        return new TikTokAccountParseResult(
            true,
            new TikTokLoginAccount(
                TikTokLoginMode.OAuthMail,
                username,
                password,
                email,
                emailPassword,
                refreshToken,
                clientId,
                mailKpi),
            "",
            "");
    }

    public static bool LooksLikeAccountLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.Contains('|'))
            return false;

        var parts = line.Split('|');
        if (parts.Length is not (2 or 7))
            return false;

        return !LooksLikeHeader(parts[0].Trim(), parts[1].Trim());
    }

    public static bool IsHeaderLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var parts = line.Split('|');
        return parts.Length >= 2
               && LooksLikeHeader(parts[0].Trim(), parts[1].Trim());
    }

    static bool LooksLikeHeader(string username, string password)
    {
        static string Normalize(string value)
            => value.Trim().ToLowerInvariant();

        var user = Normalize(username);
        var pass = Normalize(password);
        return user is "username" or "user" or "account" or "tài khoản" or "tai khoan"
               && pass is "password" or "pass" or "passwd" or "mật khẩu" or "mat khau";
    }
}
