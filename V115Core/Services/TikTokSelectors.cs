namespace ToolTikTokV11.Services;

/// <summary>Stable, semantic selector fallbacks for TikTok Studio upload.</summary>
public static class TikTokSelectors
{
    public const string UploadUrl = "https://www.tiktok.com/tiktokstudio/upload";

    public static class Upload
    {
        public static readonly string[] FileInput =
        [
            "input[type='file'][accept*='video']",
            "input[type='file'][data-e2e*='upload']",
            "input[type='file']"
        ];

        public static readonly string[] Description =
        [
            "[contenteditable='true'][data-e2e*='caption']",
            "[contenteditable='true'][aria-label*='caption' i]",
            "[contenteditable='true'][aria-label*='description' i]",
            "textarea[aria-label*='caption' i]",
            "textarea[placeholder*='caption' i]",
            "textarea[placeholder*='description' i]"
        ];

        public static readonly string[] PostButton =
        [
            "button[data-e2e*='post']",
            "button[type='submit']",
            "[role='button'][data-e2e*='post']"
        ];

        public static readonly string[] Progress =
        [
            "[role='progressbar']",
            "progress",
            "[data-e2e*='upload-progress']"
        ];
    }
}
