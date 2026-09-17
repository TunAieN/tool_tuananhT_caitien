namespace ToolTikTokV11.Models;

public enum VideoCaptionMode { Empty, Fixed, Filename }
public enum TikTokVideoVisibility { Everyone }
public enum TikTokCheckState { Unknown, Checking, Passed, Warning, Blocked }

public sealed class TikTokVideoPostOptions
{
    public string VideoPath { get; set; } = "";
    public string Caption { get; set; } = "";
    public VideoCaptionMode CaptionMode { get; set; } = VideoCaptionMode.Empty;
    public TikTokVideoVisibility Visibility { get; set; } = TikTokVideoVisibility.Everyone;
    public bool HighQuality { get; set; } = true;
    public int UploadTimeoutSeconds { get; set; } = 600;
    public int CheckTimeoutSeconds { get; set; } = 180;
    public int PostVerifyTimeoutSeconds { get; set; } = 90;
}

public sealed class TikTokVideoPostResult
{
    public bool Success { get; set; }
    public string Stage { get; set; } = "VIDEO_FAILED";
    public string Error { get; set; } = "";
    public string VideoPath { get; set; } = "";
    public TikTokCheckState MusicCheck { get; set; }
    public TikTokCheckState ContentCheck { get; set; }
}
