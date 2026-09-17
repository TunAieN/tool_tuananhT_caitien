namespace ToolTikTokV12.Models;

public enum TikTokLoginMode
{
    Normal,
    OAuthMail
}

public sealed record TikTokLoginAccount(
    TikTokLoginMode LoginMode,
    string Username,
    string Password,
    string Email = "",
    string EmailPassword = "",
    string RefreshToken = "",
    string ClientId = "",
    string MailKpi = "")
{
    public bool HasOAuthMailbox
        => LoginMode == TikTokLoginMode.OAuthMail
           && !string.IsNullOrWhiteSpace(Email)
           && !string.IsNullOrWhiteSpace(RefreshToken)
           && !string.IsNullOrWhiteSpace(ClientId);
}
