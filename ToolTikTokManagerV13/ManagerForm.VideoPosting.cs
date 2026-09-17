using System.Text;
using System.Text.Json;
using ToolTikTokV11.Models;
using ToolTikTokV12.Controls;

namespace ToolTikTokManagerV13;

public sealed partial class ManagerForm
{
    sealed class VideoPostingSettings
    {
        public bool UploadVideo { get; set; }
        public string VideoFolder { get; set; } = "";
        public string Caption { get; set; } = "";
        public VideoCaptionMode CaptionMode { get; set; } = VideoCaptionMode.Empty;
        public TikTokVideoVisibility Visibility { get; set; } = TikTokVideoVisibility.Everyone;
        public bool HighQuality { get; set; } = true;
        public int VideoUploadTimeoutSeconds { get; set; } = 600;
        public int VideoCheckTimeoutSeconds { get; set; } = 180;
        public int VideoPostVerifyTimeoutSeconds { get; set; } = 90;
    }

    sealed class VideoAccountState
    {
        public bool ProfileCompleted { get; set; }
        public bool VideoRequired { get; set; }
        public bool VideoPosted { get; set; }
        public string VideoStatus { get; set; } = "WAITING";
        public string SelectedVideo { get; set; } = "";
        public string VideoError { get; set; } = "";
    }

    sealed class VideoAccountStateStore
    {
        public Dictionary<string, VideoAccountState> Accounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class VideoPostReply
    {
        public bool Success { get; set; }
        public string Stage { get; set; } = "VIDEO_FAILED";
        public string Error { get; set; } = "";
        public string VideoPath { get; set; } = "";
    }

    readonly object _videoPoolLock = new();
    readonly object _videoStateLock = new();
    string _lastSelectedVideo = "";
    string VideoPostingSettingsPath => Path.Combine(_baseDir, "tiktok_video_posting.json");
    string VideoAccountStatePath => Path.Combine(_baseDir, "tiktok_video_account_state.json");

    VideoPostingSettings LoadVideoPostingSettings()
    {
        try
        {
            if (!File.Exists(VideoPostingSettingsPath)) return new VideoPostingSettings();
            return JsonSerializer.Deserialize<VideoPostingSettings>(File.ReadAllText(VideoPostingSettingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new VideoPostingSettings();
        }
        catch (Exception ex)
        {
            _log.Warn("[VIDEO_SETTINGS_LOAD] fallback=defaults error=" + ex.Message);
            return new VideoPostingSettings();
        }
    }

    void SaveVideoPostingSettings(VideoPostingSettings settings)
        => File.WriteAllText(VideoPostingSettingsPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

    VideoAccountStateStore LoadVideoAccountStateStore()
    {
        lock (_videoStateLock)
        {
            try
            {
                if (!File.Exists(VideoAccountStatePath)) return new VideoAccountStateStore();
                var store = JsonSerializer.Deserialize<VideoAccountStateStore>(File.ReadAllText(VideoAccountStatePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new VideoAccountStateStore();
                store.Accounts = new Dictionary<string, VideoAccountState>(store.Accounts ?? new(), StringComparer.OrdinalIgnoreCase);
                return store;
            }
            catch (Exception ex)
            {
                _log.Warn("[VIDEO_STATE_LOAD] fallback=empty error=" + ex.Message);
                return new VideoAccountStateStore();
            }
        }
    }

    VideoAccountState GetVideoAccountState(string profileName)
    {
        var store = LoadVideoAccountStateStore();
        return store.Accounts.TryGetValue(profileName, out var state) ? state : new VideoAccountState();
    }

    void SaveVideoAccountState(string profileName, VideoAccountState state)
    {
        lock (_videoStateLock)
        {
            var store = LoadVideoAccountStateStore();
            store.Accounts[profileName] = state;
            var temp = VideoAccountStatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, VideoAccountStatePath, true);
        }
    }

    static List<string> GetValidVideos(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => new[] { ".mp4", ".mov", ".webm", ".m4v" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    void ValidateVideoPostingSettings(VideoPostingSettings settings)
    {
        if (!settings.UploadVideo) return;
        if (GetValidVideos(settings.VideoFolder).Count == 0)
            throw new InvalidOperationException("No valid videos found in Video Folder.");
    }

    string SelectRandomVideo(VideoPostingSettings settings, VideoAccountState state)
    {
        if (!string.IsNullOrWhiteSpace(state.SelectedVideo) && File.Exists(state.SelectedVideo))
            return Path.GetFullPath(state.SelectedVideo);

        lock (_videoPoolLock)
        {
            var videos = GetValidVideos(settings.VideoFolder);
            if (videos.Count == 0) throw new InvalidOperationException("No valid videos found in Video Folder.");
            var candidates = videos.Count > 1
                ? videos.Where(path => !path.Equals(_lastSelectedVideo, StringComparison.OrdinalIgnoreCase)).ToList()
                : videos;
            var selected = candidates[Random.Shared.Next(candidates.Count)];
            _lastSelectedVideo = selected;
            return selected;
        }
    }

    async Task<VideoPostReply> PostTikTokVideoAsync(
        ProfileContext ctx,
        VideoPostingSettings settings,
        VideoAccountState state,
        CancellationToken ct)
    {
        _log.Info($"[VIDEO] Selecting video profile={ctx.Profile.Name}");
        var video = SelectRandomVideo(settings, state);
        state.VideoRequired = true;
        state.SelectedVideo = video;
        state.VideoPosted = false;
        state.VideoStatus = "UPLOADING";
        state.VideoError = "";
        SaveVideoAccountState(ctx.Profile.Name, state);
        _log.Info($"[VIDEO] Selected: {Path.GetFileName(video)} profile={ctx.Profile.Name}");

        var request = new TikTokVideoPostOptions
        {
            VideoPath = video,
            Caption = settings.Caption,
            CaptionMode = settings.CaptionMode,
            Visibility = settings.Visibility,
            HighQuality = settings.HighQuality,
            UploadTimeoutSeconds = settings.VideoUploadTimeoutSeconds,
            CheckTimeoutSeconds = settings.VideoCheckTimeoutSeconds,
            PostVerifyTimeoutSeconds = settings.VideoPostVerifyTimeoutSeconds
        };
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)));
        using var registration = ct.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                try { await SendCommandAsync(ctx, "video_cancel", TimeSpan.FromSeconds(5)); } catch { }
            });
        });
        var totalTimeout = TimeSpan.FromSeconds(
            Math.Clamp(settings.VideoUploadTimeoutSeconds, 30, 3600)
            + Math.Clamp(settings.VideoCheckTimeoutSeconds, 10, 900)
            + Math.Clamp(settings.VideoPostVerifyTimeoutSeconds, 15, 600)
            + 60);
        var raw = await SendCommandAsync(ctx, "post_tiktok_video|" + payload, totalTimeout);
        ct.ThrowIfCancellationRequested();
        return JsonSerializer.Deserialize<VideoPostReply>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new VideoPostReply { Error = "Worker trả về kết quả video không hợp lệ." };
    }

    void ShowVideoPostingDialog()
    {
        var settings = LoadVideoPostingSettings();
        using var form = new Form
        {
            Text = "VIDEO POSTING",
            Width = 720,
            Height = 480,
            MinimumSize = new Size(650, 430),
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10F)
        };
        ModernDialog.Apply(form, fixedDialog: false);
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 3, RowCount = 9 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        var enabled = new CheckBox { Text = "Upload video after profile setup", Checked = settings.UploadVideo, AutoSize = true };
        var folder = new TextBox { Text = settings.VideoFolder, Dock = DockStyle.Fill };
        var browse = new Button { Text = "Browse...", Dock = DockStyle.Fill };
        var caption = new TextBox { Text = settings.Caption, Dock = DockStyle.Fill };
        var mode = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        mode.Items.AddRange(Enum.GetNames<VideoCaptionMode>()); mode.SelectedItem = settings.CaptionMode.ToString();
        var visibility = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        visibility.Items.Add("Everyone"); visibility.SelectedIndex = 0;
        var quality = new CheckBox { Text = "High-quality uploads", Checked = settings.HighQuality, AutoSize = true };
        var uploadTimeout = new NumericUpDown { Minimum = 30, Maximum = 3600, Value = Math.Clamp(settings.VideoUploadTimeoutSeconds, 30, 3600), Dock = DockStyle.Left, Width = 120 };
        var checkTimeout = new NumericUpDown { Minimum = 10, Maximum = 900, Value = Math.Clamp(settings.VideoCheckTimeoutSeconds, 10, 900), Dock = DockStyle.Left, Width = 120 };
        var verifyTimeout = new NumericUpDown { Minimum = 15, Maximum = 600, Value = Math.Clamp(settings.VideoPostVerifyTimeoutSeconds, 15, 600), Dock = DockStyle.Left, Width = 120 };
        var save = new Button { Text = "Lưu", Width = 110, Height = 38 };
        ModernDialog.StylePrimaryButton(save);
        table.Controls.Add(enabled, 0, 0); table.SetColumnSpan(enabled, 3);
        table.Controls.Add(new Label { Text = "Video Folder", AutoSize = true }, 0, 1); table.Controls.Add(folder, 1, 1); table.Controls.Add(browse, 2, 1);
        table.Controls.Add(new Label { Text = "Caption", AutoSize = true }, 0, 2); table.Controls.Add(caption, 1, 2); table.SetColumnSpan(caption, 2);
        table.Controls.Add(new Label { Text = "Caption Mode", AutoSize = true }, 0, 3); table.Controls.Add(mode, 1, 3);
        table.Controls.Add(new Label { Text = "Visibility", AutoSize = true }, 0, 4); table.Controls.Add(visibility, 1, 4);
        table.Controls.Add(quality, 1, 5);
        table.Controls.Add(new Label { Text = "Upload timeout (s)", AutoSize = true }, 0, 6); table.Controls.Add(uploadTimeout, 1, 6);
        table.Controls.Add(new Label { Text = "Checks timeout (s)", AutoSize = true }, 0, 7); table.Controls.Add(checkTimeout, 1, 7);
        table.Controls.Add(new Label { Text = "Verify timeout (s)", AutoSize = true }, 0, 8); table.Controls.Add(verifyTimeout, 1, 8); table.Controls.Add(save, 2, 8);
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Chọn thư mục Video Pool", SelectedPath = folder.Text };
            if (dialog.ShowDialog(form) == DialogResult.OK) folder.Text = dialog.SelectedPath;
        };
        save.Click += (_, _) =>
        {
            try
            {
                settings.UploadVideo = enabled.Checked;
                settings.VideoFolder = folder.Text.Trim();
                settings.Caption = caption.Text;
                settings.CaptionMode = Enum.TryParse<VideoCaptionMode>(Convert.ToString(mode.SelectedItem), out var parsed) ? parsed : VideoCaptionMode.Empty;
                settings.Visibility = TikTokVideoVisibility.Everyone;
                settings.HighQuality = quality.Checked;
                settings.VideoUploadTimeoutSeconds = (int)uploadTimeout.Value;
                settings.VideoCheckTimeoutSeconds = (int)checkTimeout.Value;
                settings.VideoPostVerifyTimeoutSeconds = (int)verifyTimeout.Value;
                ValidateVideoPostingSettings(settings);
                SaveVideoPostingSettings(settings);
                form.DialogResult = DialogResult.OK;
                form.Close();
            }
            catch (Exception ex) { ModernDialog.ShowMessage(form, ex.Message, "VIDEO POSTING", MessageBoxIcon.Warning); }
        };
        form.Controls.Add(table);
        form.ShowDialog(this);
    }
}
