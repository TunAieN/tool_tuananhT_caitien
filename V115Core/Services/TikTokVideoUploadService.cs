using System.Text.Json;
using System.Diagnostics;
using ToolTikTokV11.Models;
using ToolTikTokV11.Utils;

namespace ToolTikTokV11.Services;

public sealed class TikTokVideoUploadService
{
    const string VideoUploadDiagnosticBinaryMarker =
        "VIDEO_UPLOAD_PAGE|VIDEO_SELECT_VIDEO_BUTTON|VIDEO_FILE_CHOOSER_ARMED|" +
        "VIDEO_FILE_CHOOSER_OPENED|VIDEO_FILE_CHOOSER_FILE_SET|VIDEO_FILE_INPUT_FALLBACK|" +
        "VIDEO_FILE_ATTACH_VERIFY|VIDEO_UPLOAD_PROGRESS|VIDEO_VIDEO_EDITOR_READY|" +
        "VIDEO_POST_EDITOR_SETTLE|VIDEO_AUTO_CONTENT_CHECK_MODAL|VIDEO_DESCRIPTION_SEARCH|" +
        "VIDEO_PHONE_PREVIEW_TIP|VIDEO_DESCRIPTION_CANDIDATE_SCORE|VIDEO_DESCRIPTION_CLEAR_VERIFY|" +
        "VIDEO_CAPTION_CONFIG|VIDEO_CAPTION_SET|VIDEO_CAPTION_EDITOR_FOCUS|VIDEO_CAPTION_INPUT_ATTEMPT|" +
        "VIDEO_CAPTION_INPUT_RESULT|VIDEO_CAPTION_EVENT_DIAGNOSTIC|VIDEO_CAPTION_DOM_DIAGNOSTIC|VIDEO_CAPTION_CANONICAL_READ|" +
        "VIDEO_CAPTION_SETTLE|VIDEO_CAPTION_VERIFY|VIDEO_DESCRIPTION_GATE|" +
        "VIDEO_BLOCKING_MODAL_SCAN|VIDEO_BLOCKING_MODAL_CANDIDATE|VIDEO_VISIBILITY_INTERACTION_PROBE|" +
        "VIDEO_UNKNOWN_BLOCKING_MODAL|VIDEO_VISIBILITY_CONTAINER|VIDEO_VISIBILITY_STATE|VIDEO_VISIBILITY_GATE|" +
        "VIDEO_FINAL_POST_GATE|VIDEO_POST_CONFIRM_DIALOG|VIDEO_POST_CONFIRM_TARGET|" +
        "VIDEO_POST_CONFIRM_CLICKED|VIDEO_POST_CONFIRM_DIALOG_CLOSED|VIDEO_POST_OUTCOME|" +
        "VIDEO_POST_REQUEST|VIDEO_POST_RESPONSE|VIDEO_POST_SUCCESS_EVIDENCE|VIDEO_SECOND_POST_BLOCKED";
    readonly ChromeController _chrome;
    readonly Logger _log;

    public TikTokVideoUploadService(ChromeController chrome, Logger log)
    {
        _chrome = chrome;
        _log = log;
    }

    sealed record UploadSnapshot(
        bool FileInput,
        bool SelectVideoButton,
        bool UploadUiDetected,
        bool Description,
        bool Preview,
        bool Uploading,
        bool UploadComplete,
        string UploadError,
        bool PostExists,
        bool PostEnabled,
        int FilesLength,
        bool FilenameDetected,
        bool EditorDetected,
        string Progress,
        string Url,
        string Title,
        string ReadyState,
        string BodyText);

    sealed class VideoUploadFlowException : Exception
    {
        public string Reason { get; }
        public VideoUploadFlowException(string reason, string message) : base(message) => Reason = reason;
    }

    public async Task<TikTokVideoPostResult> PostAsync(
        TikTokVideoPostOptions options,
        CancellationToken ct)
    {
        var result = new TikTokVideoPostResult { VideoPath = options.VideoPath };
        var uploadPhase = true;
        var videoAttached = false;
        var videoEditorReady = false;
        var modalHandled = false;
        var mainPostClicked = false;
        var postConfirmClicked = false;
        var postCommitted = false;
        var whenToPostExpected = false;
        var visibilityExpected = false;
        try
        {
            ValidateOptions(options);
            _log.Info("[VIDEO][DIAGNOSTIC_BUILD] markers=" + VideoUploadDiagnosticBinaryMarker);
            State("VIDEO_SELECTED");
            _log.Info($"[VIDEO] Selected: {Path.GetFileName(options.VideoPath)}");
            var videoFile = new FileInfo(options.VideoPath);
            var videoSize = videoFile.Exists ? videoFile.Length : 0;
            _log.Info($"[VIDEO][VIDEO_FILE_CHECK] exists={videoFile.Exists.ToString().ToLowerInvariant()} sizeBytes={videoSize} extension={videoFile.Extension.ToLowerInvariant()}");
            if (!videoFile.Exists) throw new VideoUploadFlowException("VIDEO_FILE_NOT_FOUND", "Video đã chọn không còn tồn tại.");
            if (videoSize <= 0) throw new VideoUploadFlowException("VIDEO_FILE_EMPTY", "Video đã chọn có kích thước 0 byte.");

            State("VIDEO_UPLOADING");
            _log.Info("[VIDEO] Opening upload page");
            try { await _chrome.NavigateAndWaitAsync(TikTokSelectors.UploadUrl, 700, 20000, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new VideoUploadFlowException("UPLOAD_PAGE_NOT_REACHED", ex.Message);
            }
            await WaitForUploadPageAsync(options.VideoPath, ct);
            State("UPLOAD_PAGE_READY");
            State("SELECT_BUTTON_READY");

            var attach = await _chrome.SelectAndAttachVideoThroughUiAsync(options.VideoPath, ct);
            if (!attach.Success)
                throw new VideoUploadFlowException(attach.FailureReason, $"Không attach được video qua UI TikTok; source={attach.Source}.");
            State("FILE_CHOOSER_READY");
            State("FILE_ATTACHED");
            await VerifyFileAttachAsync(options.VideoPath, ct);
            videoAttached = true;
            State("VIDEO_PROCESSING");
            await WaitForVideoEditorReadyAsync(options, ct);
            videoEditorReady = true;
            await WaitForUploadAsync(options, ct);
            State("VIDEO_UPLOADED");
            _log.Info("[VIDEO] Upload completed");
            uploadPhase = false;

            State("VIDEO_CONFIGURING");
            _log.Info("[VIDEO][POST_EDITOR_SETTLE] milliseconds=1000");
            await Task.Delay(1000, ct);
            var autoContentCheckHandled = await HandleAutoContentCheckModalAsync(ct);
            var phonePreviewTipHandled = await HandlePhonePreviewTipAsync(ct);
            modalHandled = autoContentCheckHandled || phonePreviewTipHandled;
            var rescanned = await WaitForPostModalEditorRescanAsync(ct);
            _log.Info($"[VIDEO][POST_MODAL_EDITOR_RESCAN] result={(rescanned ? "OK" : "FAIL")}");
            if (!rescanned)
                throw new VideoUploadFlowException("VIDEO_EDITOR_TIMEOUT", "Editor không sẵn sàng lại sau khi xử lý popup.");
            var expectedDescription = await ConfigureCaptionAsync(options, modalHandled, ct);
            await ConfigureNowAsync(ct);
            whenToPostExpected = true;
            await EnsureNoBlockingDialogsBeforeVisibilityAsync(ct);
            visibilityExpected = await ConfigureVisibilityAsync(options.Visibility, ct);
            if (options.HighQuality) await EnsureHighQualityAsync(ct);

            State("VIDEO_CHECKING");
            var checks = await WaitForChecksAsync(options, ct);
            result.MusicCheck = checks.Music;
            result.ContentCheck = checks.Content;
            if (!string.IsNullOrWhiteSpace(checks.BlockingError))
                throw new InvalidOperationException(checks.BlockingError);

            var ready = await ReadSnapshotAsync(ct);
            if (!ready.UploadComplete) throw new VideoUploadFlowException("VIDEO_PROCESSING_FAILED", "Upload chưa được TikTok xác nhận hoàn tất.");
            if (!string.IsNullOrWhiteSpace(ready.UploadError)) throw new VideoUploadFlowException("VIDEO_PROCESSING_FAILED", ready.UploadError);
            var finalBlockingUi = await ReadBlockingUiStateAsync(ct);
            var blockingModalVisible = finalBlockingUi.HasActualBlocker;
            var currentDescription = await ReadDescriptionAsync(ct);
            var descriptionExpected = currentDescription != "\0"
                && string.Equals(NormalizeDescription(currentDescription), NormalizeDescription(expectedDescription), StringComparison.Ordinal);
            var finalGatePassed = videoAttached
                && videoEditorReady
                && !blockingModalVisible
                && descriptionExpected
                && visibilityExpected
                && whenToPostExpected
                && ready.PostExists
                && ready.PostEnabled;
            _log.Info(
                $"[VIDEO][FINAL_POST_GATE] videoAttached={videoAttached.ToString().ToLowerInvariant()} " +
                $"editorReady={videoEditorReady.ToString().ToLowerInvariant()} blockingModal={blockingModalVisible.ToString().ToLowerInvariant()} " +
                $"descriptionExpected={descriptionExpected.ToString().ToLowerInvariant()} visibilityExpected={visibilityExpected.ToString().ToLowerInvariant()} " +
                $"whenToPostExpected={whenToPostExpected.ToString().ToLowerInvariant()} " +
                $"postVisible={ready.PostExists.ToString().ToLowerInvariant()} postEnabled={ready.PostEnabled.ToString().ToLowerInvariant()} " +
                $"result={(finalGatePassed ? "PASS" : "FAIL")}");
            if (!finalGatePassed)
                throw new VideoUploadFlowException("FINAL_POST_GATE_FAILED", "Điều kiện an toàn trước Post chưa đạt; không click Post.");

            _log.Info("[VIDEO] Post enabled");
            State("VIDEO_POSTING");
            _log.Info("[VIDEO] POST_SUBMIT");
            var beforeUrl = ready.Url;
            if (mainPostClicked)
            {
                _log.Warn("[VIDEO][SECOND_POST_BLOCKED]");
                throw new VideoUploadFlowException("SECOND_POST_BLOCKED", "Main Post đã được click trong job hiện tại.");
            }

            using var postNetworkObserver = await VideoPostNetworkObserver.StartAsync(_chrome, _log, ct);
            postNetworkObserver.SetPhase("AFTER_MAIN_POST");
            var mainPost = await ClickPostAsync(ct);
            if (!mainPost.Found)
                throw new VideoUploadFlowException("POST_MAIN_BUTTON_NOT_FOUND", "Không tìm thấy main Post button trong upload editor.");
            if (!mainPost.Clicked)
                throw new VideoUploadFlowException("POST_MAIN_CLICK_FAILED", "Không click được main Post button.");
            mainPostClicked = true;

            var confirmDialog = await WaitForPostConfirmDialogAsync(ct);
            if (confirmDialog.Present)
            {
                LogPostOutcome("CONFIRM_REQUIRED");
                if (!confirmDialog.ConfirmButtonFound)
                    throw new VideoUploadFlowException("POST_CONFIRM_BUTTON_NOT_FOUND", "Modal Tiếp tục đăng không có nút Đăng ngay hợp lệ.");
                _log.Info(
                    $"[VIDEO][POST_CONFIRM_TARGET] text={confirmDialog.ConfirmButtonText} " +
                    $"visible={confirmDialog.ConfirmButtonVisible.ToString().ToLowerInvariant()} " +
                    $"enabled={confirmDialog.ConfirmButtonEnabled.ToString().ToLowerInvariant()} dialogScoped=true");
                if (postConfirmClicked)
                    throw new VideoUploadFlowException("POST_CONFIRM_CLICK_FAILED", "Nút xác nhận Đăng ngay đã được click trước đó.");
                postNetworkObserver.SetPhase("AFTER_CONFIRM");
                if (!await ClickPostConfirmAsync(ct))
                    throw new VideoUploadFlowException("POST_CONFIRM_CLICK_FAILED", "Không click được nút Đăng ngay trong đúng modal Tiếp tục đăng.");
                postConfirmClicked = true;
                _log.Info($"[VIDEO][POST_CONFIRM_CLICKED] action=POST_NOW timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                _log.Info(
                    $"[VIDEO][CHECK_STATUS] musicCheck={result.MusicCheck.ToString().ToUpperInvariant()} " +
                    $"contentCheck={result.ContentCheck.ToString().ToUpperInvariant()} userFlow=POST_ANYWAY");
                var dialogClosed = await WaitForPostConfirmDialogClosedAsync(ct);
                if (!dialogClosed.Closed)
                    throw new VideoUploadFlowException("POST_CONFIRM_DIALOG_NOT_CLOSED", "Modal Tiếp tục đăng vẫn mở sau khi click Đăng ngay.");
            }

            LogPostOutcome("SUBMITTING");

            State("VIDEO_VERIFYING");
            _log.Info("[VIDEO] VERIFYING");
            var postVerification = await VerifyPostAsync(beforeUrl, options, postNetworkObserver, ct);
            if (!postVerification.Success)
                throw new VideoUploadFlowException(postVerification.FailureReason, postVerification.Message);
            postCommitted = true;

            result.Success = true;
            result.Stage = "VIDEO_SUCCESS";
            _log.Info($"[VIDEO][POST_GUARDS] mainPostClicked={mainPostClicked.ToString().ToLowerInvariant()} postConfirmClicked={postConfirmClicked.ToString().ToLowerInvariant()} postCommitted={postCommitted.ToString().ToLowerInvariant()}");
            _log.Info("[VIDEO][VIDEO_POST_SUCCESS]");
            _log.Info("[VIDEO] SUCCESS");
            return result;
        }
        catch (VideoUploadFlowException ex)
        {
            result.Stage = "VIDEO_FAILED";
            result.Error = ex.Reason + ": " + ex.Message;
            _log.Error($"[VIDEO][VIDEO_FAILED] reason={ex.Reason} message={ex.Message}");
            _log.Error("[VIDEO][ERROR] " + result.Error);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result.Stage = "VIDEO_FAILED";
            result.Error = "Cancelled";
            _log.Warn("[VIDEO] VIDEO_FAILED | Cancelled");
            _log.Warn("[VIDEO][ERROR] Cancelled");
            return result;
        }
        catch (Exception ex)
        {
            result.Stage = "VIDEO_FAILED";
            result.Error = ex.Message;
            var reason = uploadPhase ? "VIDEO_PROCESSING_FAILED" : "POST_FLOW_FAILED";
            _log.Error($"[VIDEO][VIDEO_FAILED] reason={reason} message={ex.Message}");
            _log.Error("[VIDEO] VIDEO_FAILED | " + ex.Message);
            _log.Error("[VIDEO][ERROR] " + ex.Message);
            return result;
        }
    }

    void State(string value) => _log.Info("[VIDEO] " + value);

    static void ValidateOptions(TikTokVideoPostOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.VideoPath))
            throw new VideoUploadFlowException("VIDEO_FILE_NOT_FOUND", "Video không còn tồn tại.");
        var extension = Path.GetExtension(options.VideoPath);
        if (!new[] { ".mp4", ".mov", ".webm", ".m4v" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new VideoUploadFlowException("VIDEO_PROCESSING_FAILED", "Định dạng video không được hỗ trợ: " + extension);
    }

    async Task WaitForUploadPageAsync(string videoPath, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        UploadSnapshot? snapshot = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            snapshot = await ReadSnapshotAsync(ct, Path.GetFileName(videoPath));
            var correctPage = snapshot.Url.Contains("/tiktokstudio/upload", StringComparison.OrdinalIgnoreCase);
            if (correctPage && snapshot.UploadUiDetected) break;
            await Task.Delay(250, ct);
        }

        snapshot ??= await ReadSnapshotAsync(ct, Path.GetFileName(videoPath));
        _log.Info(
            $"[VIDEO][UPLOAD_PAGE] requestedUrl={TikTokSelectors.UploadUrl} finalUrl={snapshot.Url} " +
            $"title={snapshot.Title} readyState={snapshot.ReadyState} " +
            $"uploadUiDetected={snapshot.UploadUiDetected.ToString().ToLowerInvariant()} elapsedMs={watch.ElapsedMilliseconds}");

        if (!snapshot.Url.Contains("/tiktokstudio/upload", StringComparison.OrdinalIgnoreCase))
        {
            var reason = snapshot.Url.Contains("login", StringComparison.OrdinalIgnoreCase)
                ? "UPLOAD_PAGE_REDIRECTED"
                : "UPLOAD_PAGE_NOT_REACHED";
            throw new VideoUploadFlowException(reason, "TikTok không ở trang TikTok Studio Upload.");
        }
        if (!snapshot.UploadUiDetected)
            throw new VideoUploadFlowException("UPLOAD_UI_NOT_READY", "Trang upload đã mở nhưng UI Chọn video chưa sẵn sàng sau 30 giây.");
    }

    async Task VerifyFileAttachAsync(string videoPath, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        UploadSnapshot? snapshot = null;
        var confirmed = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            snapshot = await ReadSnapshotAsync(ct, Path.GetFileName(videoPath));
            confirmed = snapshot.FilesLength > 0
                || snapshot.FilenameDetected
                || snapshot.Preview
                || snapshot.Uploading
                || snapshot.EditorDetected;
            if (confirmed) break;
            await Task.Delay(300, ct);
        }
        snapshot ??= await ReadSnapshotAsync(ct, Path.GetFileName(videoPath));
        _log.Info(
            $"[VIDEO][FILE_ATTACH_VERIFY] filesLength={snapshot.FilesLength} " +
            $"filenameDetected={snapshot.FilenameDetected.ToString().ToLowerInvariant()} " +
            $"previewDetected={snapshot.Preview.ToString().ToLowerInvariant()} " +
            $"uploadProgressDetected={snapshot.Uploading.ToString().ToLowerInvariant()} " +
            $"editorDetected={snapshot.EditorDetected.ToString().ToLowerInvariant()} " +
            $"result={(confirmed ? "OK" : "FAIL")}");
        if (!confirmed)
            throw new VideoUploadFlowException("FILE_ATTACH_NOT_CONFIRMED", "Không có evidence cho thấy TikTok đã nhận file video.");
    }

    async Task WaitForVideoEditorReadyAsync(TikTokVideoPostOptions options, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(options.UploadTimeoutSeconds, 30, 3600));
        var previousState = "";
        var nextPeriodicLog = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await ReadSnapshotAsync(ct, Path.GetFileName(options.VideoPath));
            var state = !string.IsNullOrWhiteSpace(snapshot.UploadError)
                ? "FAILED"
                : snapshot.EditorDetected ? "READY"
                : snapshot.Uploading ? "UPLOADING"
                : "PROCESSING";
            if (state != previousState || DateTime.UtcNow >= nextPeriodicLog)
            {
                _log.Info($"[VIDEO][UPLOAD_PROGRESS] state={state} progress={snapshot.Progress}");
                previousState = state;
                nextPeriodicLog = DateTime.UtcNow.AddSeconds(10);
            }
            if (state == "FAILED")
                throw new VideoUploadFlowException("VIDEO_PROCESSING_FAILED", snapshot.UploadError);
            if (state == "READY")
            {
                State("VIDEO_EDITOR_READY");
                _log.Info($"[VIDEO][VIDEO_EDITOR_READY] elapsedMs={watch.ElapsedMilliseconds}");
                return;
            }
            await Task.Delay(400, ct);
        }
        throw new VideoUploadFlowException("VIDEO_EDITOR_TIMEOUT", "TikTok không hiển thị video editor/post form trong thời gian chờ.");
    }

    async Task WaitForUploadAsync(TikTokVideoPostOptions options, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(options.UploadTimeoutSeconds, 30, 3600));
        var nextProgressLog = DateTime.MinValue;
        var lastState = "";
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await ReadSnapshotAsync(ct, Path.GetFileName(options.VideoPath));
            if (!string.IsNullOrWhiteSpace(snapshot.UploadError))
                throw new VideoUploadFlowException("VIDEO_PROCESSING_FAILED", snapshot.UploadError);
            var state = snapshot.UploadComplete ? "READY" : snapshot.Uploading ? "UPLOADING" : "PROCESSING";
            if (state != lastState || DateTime.UtcNow >= nextProgressLog)
            {
                _log.Info($"[VIDEO][UPLOAD_PROGRESS] state={state} progress={snapshot.Progress}");
                lastState = state;
                nextProgressLog = DateTime.UtcNow.AddSeconds(10);
            }
            if (snapshot.UploadComplete) return;
            await Task.Delay(600, ct);
        }
        throw new VideoUploadFlowException("VIDEO_PROCESSING_FAILED", "Upload timeout");
    }

    sealed record AutoContentCheckModalState(
        bool Present, string Role, string Heading, string Buttons,
        bool CopyrightCheckPresent, bool QuickContentCheckPresent);

    async Task<AutoContentCheckModalState> ReadAutoContentCheckModalAsync(CancellationToken ct)
    {
        var response = await _chrome.EvalAsync("""
(() => {
 const fold=s=>String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
 const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),cs=getComputedStyle(e);return r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'};
 const dialogs=[...document.querySelectorAll('[role="dialog"],div[aria-modal="true"]')].filter(visible);
 const dialog=dialogs.find(d=>{const text=fold(d.innerText||d.textContent||'');const buttons=[...d.querySelectorAll('button,[role="button"]')].filter(visible);return text.includes('bat kiem tra noi dung tu dong')&&buttons.some(b=>fold(b.innerText||b.textContent||b.getAttribute('aria-label')||'')==='bat')})||null;
 if(!dialog)return JSON.stringify({present:false});
 const heading=[...dialog.querySelectorAll('h1,h2,h3,[role="heading"]')].find(visible);
 const text=fold(dialog.innerText||dialog.textContent||'');
 const buttonLabels=[...dialog.querySelectorAll('button,[role="button"]')].filter(visible).map(b=>fold(b.innerText||b.textContent||b.getAttribute('aria-label')||'')).filter(Boolean).slice(0,8);
 const enableButton=[...dialog.querySelectorAll('button,[role="button"]')].find(b=>visible(b)&&!b.disabled&&b.getAttribute('aria-disabled')!=='true'&&fold(b.innerText||b.textContent||b.getAttribute('aria-label')||'')==='bat')||null;
 window.__ttAutoContentCheckDialog=dialog;window.__ttAutoContentCheckEnable=enableButton;
 return JSON.stringify({present:!!enableButton,role:dialog.getAttribute('role')||'',heading:String(heading?.innerText||heading?.textContent||'').replace(/\s+/g,' ').trim().slice(0,160),buttons:JSON.stringify(buttonLabels),copyrightCheckPresent:text.includes('kiem tra ban quyen nhac'),quickContentCheckPresent:text.includes('kiem tra noi dung nhanh')});
})()
""", ct: ct);
        var json = response.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "{}" : "{}";
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new(
            ReadBoolean(root, "present"), ReadString(root, "role"), ReadString(root, "heading"),
            ReadString(root, "buttons"), ReadBoolean(root, "copyrightCheckPresent"),
            ReadBoolean(root, "quickContentCheckPresent"));
    }

    async Task<bool> HandleAutoContentCheckModalAsync(CancellationToken ct)
    {
        AutoContentCheckModalState state = new(false, "", "", "[]", false, false);
        var detectDeadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < detectDeadline)
        {
            state = await ReadAutoContentCheckModalAsync(ct);
            if (state.Present) break;
            await Task.Delay(250, ct);
        }
        _log.Info(
            $"[VIDEO][AUTO_CONTENT_CHECK_MODAL] present={state.Present.ToString().ToLowerInvariant()} " +
            $"role={state.Role} heading={state.Heading} buttons={state.Buttons} " +
            $"copyrightCheckPresent={state.CopyrightCheckPresent.ToString().ToLowerInvariant()} " +
            $"quickContentCheckPresent={state.QuickContentCheckPresent.ToString().ToLowerInvariant()}");
        if (!state.Present) return false;

        var targetResponse = await _chrome.EvalAsync("""
(() => {const d=window.__ttAutoContentCheckDialog,b=window.__ttAutoContentCheckEnable;const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),cs=getComputedStyle(e);return r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'};return {valid:!!d?.isConnected&&!!b?.isConnected&&d.contains(b)&&visible(d)&&visible(b)&&!b.disabled&&b.getAttribute('aria-disabled')!=='true',text:String(b?.innerText||b?.textContent||'').replace(/\s+/g,' ').trim(),visible:visible(b),enabled:!!b&&!b.disabled&&b.getAttribute('aria-disabled')!=='true',dialogScoped:!!d&&!!b&&d.contains(b)}})()
""", ct: ct);
        var target = ReadObject(targetResponse);
        var valid = ReadBoolean(target, "valid");
        _log.Info(
            $"[VIDEO][AUTO_CONTENT_CHECK_ENABLE_TARGET] text={ReadString(target, "text")} " +
            $"visible={ReadBoolean(target, "visible").ToString().ToLowerInvariant()} " +
            $"enabled={ReadBoolean(target, "enabled").ToString().ToLowerInvariant()} " +
            $"dialogScoped={ReadBoolean(target, "dialogScoped").ToString().ToLowerInvariant()}");
        if (!valid)
            throw new VideoUploadFlowException("AUTO_CONTENT_CHECK_ENABLE_NOT_READY", "Nút Bật trong modal không còn khả dụng.");

        var clickedResponse = await _chrome.EvalAsync("""
(() => {const d=window.__ttAutoContentCheckDialog,b=window.__ttAutoContentCheckEnable;if(!d?.isConnected||!b?.isConnected||!d.contains(b)||b.disabled||b.getAttribute('aria-disabled')==='true')return false;b.click();return true})()
""", ct: ct);
        if (!ReadBool(clickedResponse))
            throw new VideoUploadFlowException("AUTO_CONTENT_CHECK_ENABLE_CLICK_FAILED", "Không click được nút Bật trong modal.");
        _log.Info("[VIDEO][AUTO_CONTENT_CHECK_ENABLE_CLICKED]");

        var watch = Stopwatch.StartNew();
        var closeDeadline = DateTime.UtcNow.AddSeconds(7);
        var closed = false;
        while (DateTime.UtcNow < closeDeadline)
        {
            ct.ThrowIfCancellationRequested();
            var closedResponse = await _chrome.EvalAsync("""
(() => {const d=window.__ttAutoContentCheckDialog;if(!d?.isConnected)return true;const r=d.getBoundingClientRect(),cs=getComputedStyle(d);return !(r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden')})()
""", ct: ct);
            if (ReadBool(closedResponse)) { closed = true; break; }
            await Task.Delay(250, ct);
        }
        _log.Info($"[VIDEO][AUTO_CONTENT_CHECK_MODAL_CLOSED] result={closed.ToString().ToLowerInvariant()} elapsedMs={watch.ElapsedMilliseconds}");
        if (!closed)
            throw new VideoUploadFlowException("AUTO_CONTENT_CHECK_MODAL_NOT_CLOSED", "Modal vẫn hiển thị sau khi click Bật.");
        return true;
    }

    sealed record PhonePreviewTipState(bool Present, bool TextMatched, bool ButtonFound);

    async Task<PhonePreviewTipState> ReadPhonePreviewTipAsync(CancellationToken ct)
    {
        var response = await _chrome.EvalAsync("""
(() => {
 const fold=s=>String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
 const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),cs=getComputedStyle(e);return r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'};
 const phrase='xem truoc video cua ban tren dien thoai';
 const exactButton=b=>fold(b.innerText||b.textContent||b.getAttribute('aria-label')||'')==='da hieu';
 const semanticContainers=[...document.querySelectorAll('[role="dialog"],[aria-modal="true"],div')]
   .filter(e=>visible(e)&&fold(e.innerText||e.textContent||'').includes(phrase))
   .map(e=>{const r=e.getBoundingClientRect(),button=[...e.querySelectorAll('button,[role="button"]')].find(b=>visible(b)&&exactButton(b))||null;return {e,button,area:r.width*r.height,textLength:String(e.innerText||e.textContent||'').length}})
   .sort((a,b)=>(b.button?1:0)-(a.button?1:0)||a.area-b.area||a.textLength-b.textLength);
 const container=semanticContainers[0]?.e||null;
 const button=semanticContainers[0]?.button||null;
 window.__ttPhonePreviewTip=container;window.__ttPhonePreviewDismiss=button;
 return {present:!!container,textMatched:!!container,buttonFound:!!button};
})()
""", ct: ct);
        var value = ReadObject(response);
        return new(ReadBoolean(value, "present"), ReadBoolean(value, "textMatched"), ReadBoolean(value, "buttonFound"));
    }

    async Task<bool> HandlePhonePreviewTipAsync(CancellationToken ct)
    {
        PhonePreviewTipState state = new(false, false, false);
        var detectDeadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < detectDeadline)
        {
            state = await ReadPhonePreviewTipAsync(ct);
            if (state.Present) break;
            await Task.Delay(250, ct);
        }
        _log.Info(
            $"[VIDEO][PHONE_PREVIEW_TIP] present={state.Present.ToString().ToLowerInvariant()} " +
            $"textMatched={state.TextMatched.ToString().ToLowerInvariant()} buttonFound={state.ButtonFound.ToString().ToLowerInvariant()}");
        if (!state.Present) return false;
        if (!state.ButtonFound)
            throw new VideoUploadFlowException("PHONE_PREVIEW_TIP_DISMISS_NOT_READY", "Popup xem trước điện thoại không có nút Đã hiểu khả dụng.");

        var clickedResponse = await _chrome.EvalAsync("""
(() => {const d=window.__ttPhonePreviewTip,b=window.__ttPhonePreviewDismiss;if(!d?.isConnected||!b?.isConnected||!d.contains(b)||b.disabled||b.getAttribute('aria-disabled')==='true')return false;b.click();return true})()
""", ct: ct);
        if (!ReadBool(clickedResponse))
            throw new VideoUploadFlowException("PHONE_PREVIEW_TIP_DISMISS_FAILED", "Không click được nút Đã hiểu trong popup xem trước điện thoại.");
        _log.Info("[VIDEO][PHONE_PREVIEW_TIP_DISMISS_CLICKED]");

        var watch = Stopwatch.StartNew();
        var closeDeadline = DateTime.UtcNow.AddSeconds(7);
        var closed = false;
        while (DateTime.UtcNow < closeDeadline)
        {
            ct.ThrowIfCancellationRequested();
            var closedResponse = await _chrome.EvalAsync("""
(() => {const d=window.__ttPhonePreviewTip;if(!d?.isConnected)return true;const r=d.getBoundingClientRect(),cs=getComputedStyle(d);return !(r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden')})()
""", ct: ct);
            if (ReadBool(closedResponse)) { closed = true; break; }
            await Task.Delay(250, ct);
        }
        _log.Info($"[VIDEO][PHONE_PREVIEW_TIP_CLOSED] result={closed.ToString().ToLowerInvariant()} elapsedMs={watch.ElapsedMilliseconds}");
        if (!closed)
            throw new VideoUploadFlowException("PHONE_PREVIEW_TIP_NOT_CLOSED", "Popup xem trước điện thoại vẫn hiển thị sau khi click Đã hiểu.");
        return true;
    }

    sealed record EditorFeaturesTipState(bool Present, bool ButtonFound, string Text);

    async Task<EditorFeaturesTipState> ReadEditorFeaturesTipAsync(CancellationToken ct)
    {
        var response = await _chrome.EvalAsync("""
(() => {
 const fold=s=>String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
 const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),cs=getComputedStyle(e),opacity=Number.parseFloat(cs.opacity||'1');return r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'&&e.getAttribute('aria-hidden')!=='true'&&Number.isFinite(opacity)&&opacity>0.05};
 const isTipText=t=>t.includes('bo sung tinh nang chinh sua moi')||t.includes('new editing features');
 const isDismiss=b=>{const t=fold(b.innerText||b.textContent||b.getAttribute('aria-label')||'');return t==='da hieu'||t==='got it'};
 const dialogs=[...document.querySelectorAll('[role="dialog"],[role="alertdialog"],[aria-modal="true"]')]
   .filter(visible).map(e=>({e,text:fold(e.innerText||e.textContent||'')})).filter(x=>isTipText(x.text));
 const dialog=dialogs[0]?.e||null;
 const button=dialog?[...dialog.querySelectorAll('button,[role="button"]')].find(b=>visible(b)&&isDismiss(b))||null:null;
 window.__ttEditorFeaturesTip=dialog;window.__ttEditorFeaturesDismiss=button;
 return {present:!!dialog,buttonFound:!!button,text:String(dialog?.innerText||dialog?.textContent||'').replace(/\s+/g,' ').trim().slice(0,200)};
})()
""", ct: ct);
        var value = ReadObject(response);
        return new(ReadBoolean(value, "present"), ReadBoolean(value, "buttonFound"), ReadString(value, "text"));
    }

    async Task<bool> HandleEditorFeaturesTipAsync(CancellationToken ct)
    {
        var state = await ReadEditorFeaturesTipAsync(ct);
        _log.Info(
            $"[VIDEO][EDITOR_FEATURES_TIP] present={state.Present.ToString().ToLowerInvariant()} " +
            $"buttonFound={state.ButtonFound.ToString().ToLowerInvariant()} text={state.Text}");
        if (!state.Present) return false;
        if (!state.ButtonFound)
            throw new VideoUploadFlowException(
                "EDITOR_FEATURES_TIP_DISMISS_NOT_READY",
                "Popup tính năng chỉnh sửa mới không có nút Đã hiểu/Got it khả dụng.");

        var clickedResponse = await _chrome.EvalAsync("""
(() => {const d=window.__ttEditorFeaturesTip,b=window.__ttEditorFeaturesDismiss;if(!d?.isConnected||!b?.isConnected||!d.contains(b)||b.disabled||b.getAttribute('aria-disabled')==='true')return false;b.click();return true})()
""", ct: ct);
        if (!ReadBool(clickedResponse))
            throw new VideoUploadFlowException(
                "EDITOR_FEATURES_TIP_DISMISS_FAILED",
                "Không click được nút Đã hiểu/Got it trong popup tính năng chỉnh sửa mới.");
        _log.Info("[VIDEO][EDITOR_FEATURES_TIP_DISMISS_CLICKED]");

        var watch = Stopwatch.StartNew();
        var closeDeadline = DateTime.UtcNow.AddSeconds(7);
        var closed = false;
        while (DateTime.UtcNow < closeDeadline)
        {
            ct.ThrowIfCancellationRequested();
            var closedResponse = await _chrome.EvalAsync("""
(() => {const d=window.__ttEditorFeaturesTip;if(!d?.isConnected)return true;const r=d.getBoundingClientRect(),cs=getComputedStyle(d),opacity=Number.parseFloat(cs.opacity||'1');return !(r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'&&d.getAttribute('aria-hidden')!=='true'&&Number.isFinite(opacity)&&opacity>0.05)})()
""", ct: ct);
            if (ReadBool(closedResponse)) { closed = true; break; }
            await Task.Delay(250, ct);
        }
        _log.Info($"[VIDEO][EDITOR_FEATURES_TIP_CLOSED] result={closed.ToString().ToLowerInvariant()} elapsedMs={watch.ElapsedMilliseconds}");
        if (!closed)
            throw new VideoUploadFlowException(
                "EDITOR_FEATURES_TIP_NOT_CLOSED",
                "Popup tính năng chỉnh sửa mới vẫn hiển thị sau khi click Đã hiểu/Got it.");
        return true;
    }

    sealed record BlockingModalCandidate(
        int Index, string Tag, string Role, string AriaModal, string AriaHidden,
        bool Visible, string Display, string Visibility, string Opacity, string PointerEvents,
        double Width, double Height, string Text, string Buttons, bool HasBackdrop,
        string ZIndex, string KnownType, string BlockingReason, bool SelectedAsBlocker);

    sealed record BlockingUiState(
        bool HasActualBlocker, string KnownType, int CandidateCount, int ActualBlockingCount,
        string BlockingElementDescription, string BlockingRole, string Reason, string BlockingText, string BlockingButtons,
        IReadOnlyList<BlockingModalCandidate> Candidates);

    sealed record VisibilityInteractionProbe(
        bool TargetFound, bool TargetVisible, bool TargetEnabled,
        string HitTestTag, string HitTestRole, bool HitTestInsideTarget,
        string BlockedBy, string Result);

    async Task<BlockingUiState> ReadBlockingUiStateAsync(CancellationToken ct)
    {
        var response = await _chrome.EvalAsync("""
(() => {
 const fold=s=>String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
 const compact=s=>String(s||'').replace(/\s+/g,' ').trim();
 const control=window.__ttVisibilityControl?.isConnected?window.__ttVisibilityControl:null;
 if(control)control.scrollIntoView({block:'center',inline:'nearest'});
 const targetRect=control?.getBoundingClientRect()||null;
 const targetFound=!!control,targetX=targetRect?Math.max(0,Math.min(innerWidth-1,targetRect.left+targetRect.width/2)):0,targetY=targetRect?Math.max(0,Math.min(innerHeight-1,targetRect.top+targetRect.height/2)):0;
 const hit=targetFound?document.elementFromPoint(targetX,targetY):null;
 const hitInsideTarget=!!control&&!!hit&&(control===hit||control.contains(hit));
 const nodes=new Set([...document.querySelectorAll('[role="dialog"],div[aria-modal="true"]')]);
 const phonePhrase='xem truoc video cua ban tren dien thoai';
 for(const el of document.querySelectorAll('div')){const t=fold(el.innerText||el.textContent||'');if(t.includes(phonePhrase)&&[...el.querySelectorAll('button,[role="button"]')].some(b=>fold(b.innerText||b.textContent||'')==='da hieu'))nodes.add(el)}
 if(targetFound&&!hitInsideTarget&&hit){nodes.add(hit.closest('[role="dialog"],[aria-modal="true"],[class*="modal"],[class*="overlay"],[class*="backdrop"]')||hit)}
 const knownType=(text,strong)=>{
   let type='UNKNOWN';
   if(text.includes('bat kiem tra noi dung tu dong'))type='AUTO_CONTENT_CHECK_MODAL';
   else if(text.includes(phonePhrase))type='PHONE_PREVIEW_TIP';
   else if(text.includes('bo sung tinh nang chinh sua moi')||text.includes('new editing features'))type='EDITOR_FEATURES_TIP';
   else if(text.includes('tiep tuc dang')||text.includes('continue posting')||text.includes('post anyway'))type='POST_CONFIRM_DIALOG';
   return type!=='UNKNOWN'&&!strong?'STALE_KNOWN_MODAL':type;
 };
 const activeKnownDialogType=[...document.querySelectorAll('[role="dialog"],[role="alertdialog"],[aria-modal="true"]')].map(el=>{
   const r=el.getBoundingClientRect(),cs=getComputedStyle(el),opacity=Number.parseFloat(cs.opacity||'1'),strong=r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'&&el.getAttribute('aria-hidden')!=='true'&&Number.isFinite(opacity)&&opacity>0.05&&cs.pointerEvents!=='none';
   return knownType(fold(el.innerText||el.textContent||''),strong);
 }).find(type=>type!=='UNKNOWN'&&type!=='STALE_KNOWN_MODAL')||'UNKNOWN';
 const candidates=[...nodes].map((el,index)=>{
   const r=el.getBoundingClientRect(),cs=getComputedStyle(el),ariaHidden=el.getAttribute('aria-hidden')||'',opacity=Number.parseFloat(cs.opacity||'1');
   const baseVisible=r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'&&cs.visibility!=='collapse';
   const strongVisible=baseVisible&&ariaHidden!=='true'&&Number.isFinite(opacity)&&opacity>0.05&&cs.pointerEvents!=='none';
   const text=fold(el.innerText||el.textContent||'');
   const buttonLabels=[...el.querySelectorAll('button,[role="button"]')].map(b=>compact(b.innerText||b.textContent||b.getAttribute('aria-label')||'')).filter(Boolean).slice(0,10);
   const actionable=buttonLabels.length>0||!!el.querySelector('input,select,textarea,[contenteditable="true"]');
   const containsHit=!!hit&&(el===hit||el.contains(hit));
   const blocksVisibility=targetFound&&!hitInsideTarget&&strongVisible&&containsHit;
   const modalSemantic=el.getAttribute('role')==='dialog'||el.getAttribute('aria-modal')==='true';
   const selected=strongVisible&&(blocksVisibility||(!targetFound&&modalSemantic&&actionable));
   let known=knownType(text,strongVisible);
   if(known==='UNKNOWN'&&blocksVisibility&&activeKnownDialogType!=='UNKNOWN')known=activeKnownDialogType+'_BACKDROP';
   const backdrop=!!el.closest('[class*="backdrop"],[class*="overlay"]')||!!el.parentElement?.querySelector(':scope > [class*="backdrop"],:scope > [class*="overlay"]');
   let reason='NON_BLOCKING';
   if(!baseVisible)reason='HIDDEN_OR_ZERO_SIZE';else if(ariaHidden==='true')reason='ARIA_HIDDEN';else if(!(opacity>0.05))reason='TRANSPARENT';else if(cs.pointerEvents==='none')reason='POINTER_EVENTS_NONE';else if(blocksVisibility)reason='INTERCEPTS_VISIBILITY_TARGET';else if(!targetFound&&selected)reason='ACTIONABLE_MODAL_TARGET_NOT_FOUND';
   return {index,tag:el.tagName||'',role:el.getAttribute('role')||'',ariaModal:el.getAttribute('aria-modal')||'',ariaHidden,visible:strongVisible,display:cs.display||'',visibility:cs.visibility||'',opacity:cs.opacity||'',pointerEvents:cs.pointerEvents||'',width:Math.round(r.width*10)/10,height:Math.round(r.height*10)/10,text:compact(el.innerText||el.textContent||'').slice(0,200),buttons:buttonLabels,hasBackdrop:backdrop,zIndex:cs.zIndex||'',knownType:known,blockingReason:reason,selectedAsBlocker:selected};
 });
 const selected=candidates.find(x=>x.selectedAsBlocker)||null;
 return JSON.stringify({hasActualBlocker:!!selected,knownType:selected?.knownType||'NONE',candidateCount:candidates.length,actualBlockingCount:candidates.filter(x=>x.selectedAsBlocker).length,blockingElementDescription:selected?`${selected.tag}[role=${selected.role||'-'}]`:'',blockingRole:selected?.role||'',reason:selected?.blockingReason||'',blockingText:selected?.text||'',blockingButtons:selected?.buttons||[],candidates});
})()
""", ct: ct);
        var json = response.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "{}" : "{}";
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var candidateCount = ReadInteger(root, "candidateCount");
        var actualBlockingCount = ReadInteger(root, "actualBlockingCount");
        _log.Info($"[VIDEO][BLOCKING_MODAL_SCAN] candidateCount={candidateCount} actualBlockingCount={actualBlockingCount}");
        var candidates = new List<BlockingModalCandidate>();
        if (root.TryGetProperty("candidates", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var buttons = item.TryGetProperty("buttons", out var buttonArray) ? buttonArray.GetRawText() : "[]";
                var candidate = new BlockingModalCandidate(
                    ReadInteger(item, "index"), ReadString(item, "tag"), ReadString(item, "role"),
                    ReadString(item, "ariaModal"), ReadString(item, "ariaHidden"), ReadBoolean(item, "visible"),
                    ReadString(item, "display"), ReadString(item, "visibility"), ReadString(item, "opacity"),
                    ReadString(item, "pointerEvents"), ReadDouble(item, "width"), ReadDouble(item, "height"),
                    ReadString(item, "text"), buttons, ReadBoolean(item, "hasBackdrop"), ReadString(item, "zIndex"),
                    ReadString(item, "knownType"), ReadString(item, "blockingReason"), ReadBoolean(item, "selectedAsBlocker"));
                candidates.Add(candidate);
                _log.Info(
                    $"[VIDEO][BLOCKING_MODAL_CANDIDATE] index={candidate.Index} tag={candidate.Tag} role={candidate.Role} " +
                    $"ariaModal={candidate.AriaModal} ariaHidden={candidate.AriaHidden} visible={candidate.Visible.ToString().ToLowerInvariant()} " +
                    $"display={candidate.Display} visibility={candidate.Visibility} opacity={candidate.Opacity} pointerEvents={candidate.PointerEvents} " +
                    $"width={candidate.Width:0.0} height={candidate.Height:0.0} text={candidate.Text} buttons={candidate.Buttons} " +
                    $"hasBackdrop={candidate.HasBackdrop.ToString().ToLowerInvariant()} zIndex={candidate.ZIndex} knownType={candidate.KnownType} " +
                    $"blockingReason={candidate.BlockingReason} selectedAsBlocker={candidate.SelectedAsBlocker.ToString().ToLowerInvariant()}");
            }
        }
        var state = new BlockingUiState(
            ReadBoolean(root, "hasActualBlocker"), ReadString(root, "knownType"),
            candidateCount, actualBlockingCount,
            ReadString(root, "blockingElementDescription"), ReadString(root, "blockingRole"), ReadString(root, "reason"),
            ReadString(root, "blockingText"),
            root.TryGetProperty("blockingButtons", out var blockingButtons) ? blockingButtons.GetRawText() : "[]",
            candidates);
        return state;
    }

    async Task<VisibilityInteractionProbe> ProbeVisibilityInteractionAsync(CancellationToken ct)
    {
        var response = await _chrome.EvalAsync("""
(() => {
 const compact=s=>String(s||'').replace(/\s+/g,' ').trim();
 const target=window.__ttVisibilityControl;
 if(!target?.isConnected)return {targetFound:false,targetVisible:false,targetEnabled:false,hitTestTag:'',hitTestRole:'',hitTestInsideTarget:false,blockedBy:'',result:'NOT_FOUND'};
 target.scrollIntoView({block:'center',inline:'nearest'});
 const r=target.getBoundingClientRect(),cs=getComputedStyle(target),visible=r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'&&cs.opacity!=='0'&&target.getAttribute('aria-hidden')!=='true';
 const enabled=!target.disabled&&target.getAttribute('aria-disabled')!=='true';
 const x=Math.max(0,Math.min(innerWidth-1,r.left+r.width/2)),y=Math.max(0,Math.min(innerHeight-1,r.top+r.height/2)),hit=document.elementFromPoint(x,y);
 const inside=!!hit&&(target===hit||target.contains(hit));
 const blocker=!inside&&hit?(hit.closest('[role="dialog"],[aria-modal="true"],[class*="modal"],[class*="overlay"],[class*="backdrop"]')||hit):null;
 return {targetFound:true,targetVisible:visible,targetEnabled:enabled,hitTestTag:hit?.tagName||'',hitTestRole:hit?.getAttribute('role')||'',hitTestInsideTarget:inside,blockedBy:blocker?`${blocker.tagName||''}[role=${blocker.getAttribute('role')||'-'}] ${compact(blocker.innerText||blocker.textContent||'').slice(0,120)}`:'',result:visible&&enabled&&inside?'INTERACTABLE':'BLOCKED'};
})()
""", ct: ct);
        var value = ReadObject(response);
        var probe = new VisibilityInteractionProbe(
            ReadBoolean(value, "targetFound"), ReadBoolean(value, "targetVisible"),
            ReadBoolean(value, "targetEnabled"), ReadString(value, "hitTestTag"),
            ReadString(value, "hitTestRole"), ReadBoolean(value, "hitTestInsideTarget"),
            ReadString(value, "blockedBy"), ReadString(value, "result"));
        _log.Info(
            $"[VIDEO][VISIBILITY_INTERACTION_PROBE] targetFound={probe.TargetFound.ToString().ToLowerInvariant()} " +
            $"targetVisible={probe.TargetVisible.ToString().ToLowerInvariant()} targetEnabled={probe.TargetEnabled.ToString().ToLowerInvariant()} " +
            $"hitTestTag={probe.HitTestTag} hitTestRole={probe.HitTestRole} " +
            $"hitTestInsideTarget={probe.HitTestInsideTarget.ToString().ToLowerInvariant()} blockedBy={probe.BlockedBy} result={probe.Result}");
        return probe;
    }

    async Task EnsureNoBlockingDialogsBeforeVisibilityAsync(CancellationToken ct)
    {
        await HandleAutoContentCheckModalAsync(ct);
        await HandlePhonePreviewTipAsync(ct);
        await HandleEditorFeaturesTipAsync(ct);

        _ = await ReadVisibilityProbeAsync(ct);
        var blockingUi = await ReadBlockingUiStateAsync(ct);
        var interaction = await ProbeVisibilityInteractionAsync(ct);
        if (!blockingUi.HasActualBlocker || interaction.Result == "INTERACTABLE") return;

        if (blockingUi.KnownType == "UNKNOWN")
        {
            _log.Warn(
                $"[VIDEO][UNKNOWN_BLOCKING_MODAL] role={blockingUi.BlockingRole} element={blockingUi.BlockingElementDescription} " +
                $"text={blockingUi.BlockingText} buttons={blockingUi.BlockingButtons} blockingVisibility=true");
            throw new VideoUploadFlowException(
                "UNKNOWN_BLOCKING_MODAL_BEFORE_VISIBILITY",
                "Có modal/overlay lạ thực sự chặn Visibility; tool không tự click nút không rõ semantics.");
        }

        throw new VideoUploadFlowException(
            "BLOCKING_MODAL_BEFORE_VISIBILITY",
            $"UI blocker {blockingUi.KnownType} vẫn chặn Visibility: {blockingUi.Reason}.");
    }

    async Task<bool> WaitForPostModalEditorRescanAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var response = await _chrome.EvalAsync("""
(() => !!document.querySelector('textarea,[contenteditable="true"],[role="textbox"],button[data-e2e*="post"],button[type="submit"]'))()
""", ct: ct);
            if (ReadBool(response)) return true;
            await Task.Delay(250, ct);
        }
        return false;
    }

    sealed record DescriptionSearchResult(
        bool Found, int CandidateCount, string Source, string Tag, string Role,
        string ContentEditable, string DataE2E, string ContainerLabel, int Score,
        bool InViewport, bool ScrollPerformed, string Candidates);

    sealed record DescriptionDomSnapshot(
        bool Found, string TagName, string Role, string ContentEditable,
        string InnerText, string TextContent, string CanonicalText,
        int InnerHtmlLength, int ChildNodesCount, int MentionCount,
        string ChildElementTags, string ChildElementRoles,
        string DataAttributes, string AriaAttributes,
        string LogicalTextNodes, string SanitizedInnerHtml);

    async Task<DescriptionSearchResult> ProbeDescriptionEditorAsync(
        int attempt, bool scrollFound, string filenameStem, CancellationToken ct)
    {
        var filenameStemJson = JsonSerializer.Serialize(filenameStem);
        var script = """
(() => {
 const fold=s=>String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
 const semantic=/mo ta|description|caption|chu thich/;
 const filenameStem=fold(__FILENAME_STEM__);
 const candidates=[...document.querySelectorAll('textarea,[contenteditable="true"],[role="textbox"],input[type="text"]')].filter(el=>!el.disabled&&el.getAttribute('aria-disabled')!=='true');
 const labelName=folded=>folded.includes('mo ta')?'Mô tả':folded.includes('description')?'Description':folded.includes('caption')?'Caption':folded.includes('chu thich')?'Chú thích':'';
 const findContainerContext=el=>{let node=el.parentElement;for(let depth=1;node&&depth<=6;depth++,node=node.parentElement){const whole=String(node.innerText||node.textContent||'');const boundedWhole=whole.length<=500?whole:'';const local=[node.getAttribute('aria-label')||'',node.getAttribute('data-e2e')||'',node.getAttribute('title')||'',node.previousElementSibling?.innerText||'',boundedWhole,...[...node.querySelectorAll(':scope > label,:scope > h1,:scope > h2,:scope > h3,:scope > h4,:scope > [role="heading"],:scope > div > label,:scope > div > [role="heading"]')].slice(0,12).map(x=>x.innerText||x.textContent||'')].join(' ');const folded=fold(local);if(semantic.test(folded))return {label:labelName(folded),depth};}return {label:'',depth:0};};
 const scored=candidates.map((el,index)=>{
   const attrs=fold(`${el.getAttribute('aria-label')||''} ${el.getAttribute('placeholder')||''} ${el.getAttribute('data-e2e')||''} ${el.getAttribute('name')||''} ${el.id||''}`);
   const label=el.id?[...document.querySelectorAll('label')].find(l=>l.htmlFor===el.id):null;
   const nearbyNodes=[label,el.closest('label'),el.previousElementSibling,el.parentElement?.previousElementSibling].filter(Boolean);
   const near=fold(nearbyNodes.map(n=>n.innerText||n.textContent||'').join(' '));
   const containerMeta=fold(`${el.parentElement?.getAttribute('data-e2e')||''} ${el.parentElement?.getAttribute('aria-label')||''}`);
   const context=findContainerContext(el);const role=el.getAttribute('role')||'';const currentText=String('value' in el?el.value:el.innerText||el.textContent||'');const currentFold=fold(currentText);
   const filenameStemMatch=filenameStem.length>=4&&(currentFold===filenameStem||currentFold.includes(filenameStem));
   let rejectReason='';if(/tim kiem vi tri|location|vi tri/.test(attrs))rejectReason='LOCATION_INPUT';else if(/search|tim kiem|schedule|lich dang|comment|binh luan/.test(attrs))rejectReason='UNRELATED_INPUT';
   let score=0;if(semantic.test(attrs))score+=240;if(semantic.test(near))score+=190;if(semantic.test(containerMeta))score+=140;if(context.label)score+=320-Math.min(context.depth,6)*5;
   if(el.tagName==='TEXTAREA')score+=35;if(el.isContentEditable)score+=35;if(role==='textbox')score+=20;if(el.isContentEditable&&role==='combobox')score+=45;if(filenameStemMatch)score+=260;
   if(rejectReason)score-=700;
   const r=el.getBoundingClientRect(),cs=getComputedStyle(el);const rendered=cs.display!=='none'&&cs.visibility!=='hidden';if(!rendered)score-=500;const inViewport=rendered&&r.bottom>0&&r.right>0&&r.top<innerHeight&&r.left<innerWidth;
   return {el,score,inViewport,context,diag:{index,tag:el.tagName||'',role,contenteditable:el.getAttribute('contenteditable')||'',placeholder:String(el.getAttribute('placeholder')||'').slice(0,100),ariaLabel:String(el.getAttribute('aria-label')||'').slice(0,100),dataE2E:String(el.getAttribute('data-e2e')||'').slice(0,100),containerLabel:context.label,parentText:context.label,currentTextLength:currentText.replace(/[\u200B-\u200D\uFEFF]/g,'').trim().length,filenameStemMatch,ancestorDepthMatched:context.depth,score,rejectReason,selected:false}};
 }).sort((a,b)=>b.score-a.score);
 const best=scored.find(x=>x.score>=160&&!x.diag.rejectReason)||null;let scrollPerformed=false;
 if(best){best.diag.selected=true;window.__ttVideoDescriptionEditor=best.el;if(__SCROLL_FOUND__&&!best.inViewport){best.el.scrollIntoView({block:'center'});scrollPerformed=true}}
 const source=!best?'':best.el.isContentEditable&&best.el.getAttribute('role')==='combobox'?'contenteditable-combobox':best.el.tagName==='TEXTAREA'?'textarea':best.el.isContentEditable?'contenteditable':best.el.getAttribute('role')==='textbox'?'role-textbox':'input';
 return JSON.stringify({found:!!best,candidateCount:candidates.length,source,tag:best?.el.tagName||'',role:best?.el.getAttribute('role')||'',contenteditable:best?.el.getAttribute('contenteditable')||'',dataE2E:best?.el.getAttribute('data-e2e')||'',containerLabel:best?.context.label||'',score:best?.score||0,inViewport:best?.inViewport||false,scrollPerformed,candidates:scored.slice(0,8).map(x=>x.diag)});
})()
""".Replace("__SCROLL_FOUND__", scrollFound ? "true" : "false", StringComparison.Ordinal)
   .Replace("__FILENAME_STEM__", filenameStemJson, StringComparison.Ordinal);
        var response = await _chrome.EvalAsync(script, ct: ct);
        var json = response.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "{}" : "{}";
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var result = new DescriptionSearchResult(
            ReadBoolean(root, "found"), ReadInteger(root, "candidateCount"), ReadString(root, "source"),
            ReadString(root, "tag"), ReadString(root, "role"), ReadString(root, "contenteditable"),
            ReadString(root, "dataE2E"), ReadString(root, "containerLabel"), ReadInteger(root, "score"),
            ReadBoolean(root, "inViewport"), ReadBoolean(root, "scrollPerformed"),
            root.TryGetProperty("candidates", out var candidates) ? candidates.GetRawText() : "[]");
        if (root.TryGetProperty("candidates", out var candidateArray) && candidateArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in candidateArray.EnumerateArray())
            {
                _log.Info(
                    $"[VIDEO][DESCRIPTION_CANDIDATE_SCORE] index={ReadInteger(candidate, "index")} " +
                    $"tag={ReadString(candidate, "tag")} role={ReadString(candidate, "role")} " +
                    $"contenteditable={ReadString(candidate, "contenteditable")} containerLabel={ReadString(candidate, "containerLabel")} " +
                    $"parentText={ReadString(candidate, "parentText")} " +
                    $"currentTextLength={ReadInteger(candidate, "currentTextLength")} " +
                    $"currentTextMatchesFilenameStem={ReadBoolean(candidate, "filenameStemMatch").ToString().ToLowerInvariant()} " +
                    $"ancestorDepthMatched={ReadInteger(candidate, "ancestorDepthMatched")} score={ReadInteger(candidate, "score")} " +
                    $"rejectReason={ReadString(candidate, "rejectReason")} selected={ReadBoolean(candidate, "selected").ToString().ToLowerInvariant()}");
            }
        }
        _log.Info(
            $"[VIDEO][DESCRIPTION_SEARCH] attempt={attempt} found={result.Found.ToString().ToLowerInvariant()} " +
            $"source={result.Source} inViewport={result.InViewport.ToString().ToLowerInvariant()} " +
            $"scrollPerformed={result.ScrollPerformed.ToString().ToLowerInvariant()}");
        return result;
    }

    async Task<DescriptionSearchResult> FindDescriptionEditorAsync(string filenameStem, CancellationToken ct)
    {
        DescriptionSearchResult? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            last = await ProbeDescriptionEditorAsync(attempt, scrollFound: true, filenameStem, ct);
            if (last.Found && !last.ScrollPerformed) return last;
            if (!last.Found && attempt == 1)
                await _chrome.EvalAsync("window.scrollTo({top:0,behavior:'instant'}); true", ct: ct);
            await Task.Delay(400, ct);
        }
        if (last is { Found: true, InViewport: false }) last = last with { Found = false };
        return last ?? new(false, 0, "", "", "", "", "", "", 0, false, false, "[]");
    }

    async Task<DescriptionDomSnapshot> ReadDescriptionDomSnapshotAsync(CancellationToken ct)
    {
        var response = await _chrome.EvalAsync("""
(() => {
  const el=window.__ttVideoDescriptionEditor;
  if(!el?.isConnected)return JSON.stringify({found:false});
  const safe=s=>String(s||'').replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g,'').slice(0,240);
  const attrs=(node,prefix)=>Object.fromEntries([...node.attributes]
    .filter(a=>a.name.startsWith(prefix))
    .slice(0,24)
    .map(a=>[a.name,safe(a.value)]));
  const shown=node=>{
    const parent=node.nodeType===Node.ELEMENT_NODE?node:node.parentElement;
    if(!parent)return false;
    if(parent.closest('[aria-hidden="true"],[hidden]'))return false;
    const cs=getComputedStyle(parent);
    return cs.display!=='none'&&cs.visibility!=='hidden';
  };
  const walker=document.createTreeWalker(el,NodeFilter.SHOW_TEXT);
  const logicalTextNodes=[];
  for(let node=walker.nextNode();node&&logicalTextNodes.length<40;node=walker.nextNode()){
    const parent=node.parentElement;
    logicalTextNodes.push({
      text:safe(node.nodeValue),length:String(node.nodeValue||'').length,
      visible:shown(node),parentTag:parent?.tagName||'',role:parent?.getAttribute('role')||'',
      contenteditable:parent?.getAttribute('contenteditable')||'',ariaHidden:parent?.getAttribute('aria-hidden')||'',
      dataAttributes:parent?attrs(parent,'data-'):{}
    });
  }
  const elements=[...el.querySelectorAll('*')];
  const mentionLike=elements.filter(node=>{
    const text=String(node.innerText||node.textContent||'').trim();
    const meta=`${node.getAttribute('data-mention')||''} ${node.getAttribute('data-e2e')||''} ${node.getAttribute('class')||''} ${node.getAttribute('href')||''}`.toLowerCase();
    return /mention/.test(meta)||/\/@[^/?#]+/.test(meta)||(node.getAttribute('contenteditable')==='false'&&text.startsWith('@'));
  });
  const clone=el.cloneNode(true);
  clone.querySelectorAll('script,style').forEach(node=>node.remove());
  [clone,...clone.querySelectorAll('*')].forEach(node=>[...node.attributes].forEach(a=>{
    const keep=a.name==='role'||a.name==='contenteditable'||a.name==='aria-label'||a.name==='aria-hidden'||
      a.name==='data-e2e'||a.name==='data-mention'||a.name==='href'||a.name==='class';
    if(!keep)node.removeAttribute(a.name);else node.setAttribute(a.name,safe(a.value));
  }));
  const isValueEditor=el instanceof HTMLTextAreaElement||el instanceof HTMLInputElement;
  const innerText=String(el.innerText||'');
  const textContent=String(el.textContent||'');
  const canonicalText=String(isValueEditor?el.value:innerText);
  return JSON.stringify({
    found:true,tagName:el.tagName||'',role:el.getAttribute('role')||'',
    contenteditable:el.getAttribute('contenteditable')||'',innerText,textContent,canonicalText,
    innerHtmlLength:String(el.innerHTML||'').length,childNodesCount:el.childNodes.length,
    childElementTags:[...el.children].map(node=>node.tagName||''),
    childElementRoles:[...el.children].map(node=>node.getAttribute('role')||''),
    dataAttributes:attrs(el,'data-'),ariaAttributes:attrs(el,'aria-'),
    mentionCount:mentionLike.length,logicalTextNodes,
    sanitizedInnerHtml:safe(clone.innerHTML).slice(0,2000)
  });
})()
""", ct: ct);
        var json = response.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "{}"
            : "{}";
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new DescriptionDomSnapshot(
            ReadBoolean(root, "found"), ReadString(root, "tagName"), ReadString(root, "role"),
            ReadString(root, "contenteditable"), ReadString(root, "innerText"), ReadString(root, "textContent"),
            ReadString(root, "canonicalText"), ReadInteger(root, "innerHtmlLength"),
            ReadInteger(root, "childNodesCount"), ReadInteger(root, "mentionCount"),
            root.TryGetProperty("childElementTags", out var tags) ? tags.GetRawText() : "[]",
            root.TryGetProperty("childElementRoles", out var roles) ? roles.GetRawText() : "[]",
            root.TryGetProperty("dataAttributes", out var dataAttributes) ? dataAttributes.GetRawText() : "{}",
            root.TryGetProperty("ariaAttributes", out var ariaAttributes) ? ariaAttributes.GetRawText() : "{}",
            root.TryGetProperty("logicalTextNodes", out var textNodes) ? textNodes.GetRawText() : "[]",
            ReadString(root, "sanitizedInnerHtml"));
    }

    async Task<(DescriptionDomSnapshot Snapshot, long ElapsedMs, bool Stable)> WaitForCaptionEditorSettleAsync(CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        DescriptionDomSnapshot snapshot = await ReadDescriptionDomSnapshotAsync(ct);
        var previousSignature = "";
        var stableReads = 0;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(2))
        {
            ct.ThrowIfCancellationRequested();
            var signature = snapshot.Found
                ? $"{snapshot.InnerText}\0{snapshot.TextContent}\0{snapshot.InnerHtmlLength}\0{snapshot.ChildNodesCount}\0{snapshot.MentionCount}"
                : "NOT_FOUND";
            stableReads = string.Equals(signature, previousSignature, StringComparison.Ordinal)
                ? stableReads + 1
                : 0;
            if (stableReads >= 2)
                return (snapshot, stopwatch.ElapsedMilliseconds, true);
            previousSignature = signature;
            await Task.Delay(150, ct);
            snapshot = await ReadDescriptionDomSnapshotAsync(ct);
        }
        return (snapshot, stopwatch.ElapsedMilliseconds, false);
    }

    void LogCaptionDomDiagnostic(DescriptionDomSnapshot snapshot)
    {
        _log.Info(
            $"[VIDEO][CAPTION_DOM_DIAGNOSTIC] found={snapshot.Found.ToString().ToLowerInvariant()} " +
            $"tagName={snapshot.TagName} role={snapshot.Role} contenteditable={snapshot.ContentEditable} " +
            $"innerTextLength={snapshot.InnerText.Length} textContentLength={snapshot.TextContent.Length} " +
            $"innerHTMLLength={snapshot.InnerHtmlLength} childNodesCount={snapshot.ChildNodesCount} " +
            $"childElementTags={snapshot.ChildElementTags} childElementRoles={snapshot.ChildElementRoles} " +
            $"dataAttributes={snapshot.DataAttributes} ariaAttributes={snapshot.AriaAttributes} " +
            $"mentionCount={snapshot.MentionCount} logicalTextNodes={snapshot.LogicalTextNodes} " +
            $"sanitizedInnerHTML={snapshot.SanitizedInnerHtml}");
    }

    async Task<(bool Success, string Mechanism)> SetCaptionOnceAsync(
        string caption, string requestedStrategy, CancellationToken ct)
    {
        var captionJson = JsonSerializer.Serialize(caption);
        var token = "tt-caption-" + Guid.NewGuid().ToString("N");
        var tokenJson = JsonSerializer.Serialize(token);
        var targetResponse = await _chrome.EvalAsync("""
(() => {
  const el=window.__ttVideoDescriptionEditor,value=__CAPTION__,token=__TOKEN__;
  if(!el?.isConnected)return 'NOT_FOUND';
  if(el instanceof HTMLTextAreaElement||el instanceof HTMLInputElement){
    const proto=el instanceof HTMLTextAreaElement?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;
    const setter=Object.getOwnPropertyDescriptor(proto,'value')?.set;
    el.focus();
    if(setter)setter.call(el,value);else el.value=value;
    // The native value setter performs the only mutation. This event only notifies the framework.
    el.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:null}));
    return 'VALUE_NATIVE_SETTER';
  }
  if(!el.isContentEditable)return 'UNSUPPORTED';
  el.setAttribute('data-codex-caption-target',token);
  return __STRATEGY__;
})()
""".Replace("__CAPTION__", captionJson, StringComparison.Ordinal)
   .Replace("__TOKEN__", tokenJson, StringComparison.Ordinal)
   .Replace("__STRATEGY__", JsonSerializer.Serialize(requestedStrategy), StringComparison.Ordinal), ct: ct);
        var mechanism = targetResponse.TryGetProperty("value", out var targetValue)
            && targetValue.ValueKind == JsonValueKind.String
                ? targetValue.GetString() ?? ""
                : "";
        if (mechanism == "VALUE_NATIVE_SETTER")
            return (true, mechanism);
        if (mechanism is not ("DRAFTJS_KEYBOARD_TYPING" or "DRAFTJS_EXEC_COMMAND_FALLBACK"))
            return (false, mechanism.Length > 0 ? mechanism : "TARGET_PROBE_FAILED");

        try
        {
            var xpath = $"//*[@data-codex-caption-target='{token}']";
            await _chrome.ClickXPathAsync(xpath, ct: ct);
            var focusResponse = await _chrome.EvalAsync("""
(() => {
  const el=window.__ttVideoDescriptionEditor;
  if(!el?.isConnected)return {focused:false,activeElementTag:'',activeElementRole:'',activeInsideEditor:false,selectionRangeCount:0,selectionCollapsed:false,selectionAnchorInsideEditor:false};
  try{el.focus({preventScroll:true})}catch{el.focus()}
  const selection=getSelection();
  const range=document.createRange();
  range.selectNodeContents(el);
  range.collapse(false);
  selection.removeAllRanges();
  selection.addRange(range);
  const active=document.activeElement;
  const activeInsideEditor=active===el||el.contains(active);
  const anchor=selection.anchorNode;
  const selectionAnchorInsideEditor=anchor===el||!!anchor&&el.contains(anchor.nodeType===Node.ELEMENT_NODE?anchor:anchor.parentNode);
  return {
    focused:activeInsideEditor&&selection.rangeCount>0&&selectionAnchorInsideEditor,
    activeElementTag:active?.tagName||'',activeElementRole:active?.getAttribute?.('role')||'',
    activeInsideEditor,selectionRangeCount:selection.rangeCount,
    selectionCollapsed:selection.rangeCount>0&&selection.getRangeAt(0).collapsed,
    selectionAnchorInsideEditor
  };
})()
""", ct: ct);
            var focus = ReadObject(focusResponse);
            var focused = ReadBoolean(focus, "focused");
            var activeInsideEditor = ReadBoolean(focus, "activeInsideEditor");
            var selectionAnchorInsideEditor = ReadBoolean(focus, "selectionAnchorInsideEditor");
            var selectionRangeCount = ReadInteger(focus, "selectionRangeCount");
            var selectionCollapsed = ReadBoolean(focus, "selectionCollapsed");
            _log.Info(
                $"[VIDEO][CAPTION_EDITOR_FOCUS] focused={focused.ToString().ToLowerInvariant()} " +
                $"activeElementTag={ReadString(focus, "activeElementTag")} activeElementRole={ReadString(focus, "activeElementRole")} " +
                $"activeInsideEditor={activeInsideEditor.ToString().ToLowerInvariant()} selectionRangeCount={selectionRangeCount} " +
                $"selectionCollapsed={selectionCollapsed.ToString().ToLowerInvariant()} " +
                $"selectionAnchorInsideEditor={selectionAnchorInsideEditor.ToString().ToLowerInvariant()}");
            if (!focused || !activeInsideEditor || selectionRangeCount == 0 || !selectionCollapsed || !selectionAnchorInsideEditor)
                return (false, mechanism);

            await _chrome.EvalAsync("""
(() => {
  const el=window.__ttVideoDescriptionEditor;
  if(!el?.isConnected)return false;
  const counts={keydown:0,beforeinput:0,input:0,keyup:0,inputTypes:{}};
  const handlers={};
  for(const type of ['keydown','beforeinput','input','keyup']){
    handlers[type]=event=>{counts[type]++;if((type==='beforeinput'||type==='input')&&event.inputType)counts.inputTypes[event.inputType]=(counts.inputTypes[event.inputType]||0)+1};
    el.addEventListener(type,handlers[type],true);
  }
  window.__ttCaptionEventDiagnostic={el,counts,handlers};
  return true;
})()
""", ct: ct);

            bool dispatched;
            if (mechanism == "DRAFTJS_KEYBOARD_TYPING")
            {
                await _chrome.TypeFocusedTextByKeyboardAsync(caption, 15, ct);
                dispatched = true;
            }
            else
            {
                var captionJsonForFallback = JsonSerializer.Serialize(caption);
                var fallbackResponse = await _chrome.EvalAsync("""
(() => {
  const el=window.__ttVideoDescriptionEditor,value=__CAPTION__;
  if(!el?.isConnected||document.activeElement!==el)return false;
  try{return document.execCommand('insertText',false,value)}catch{return false}
})()
""".Replace("__CAPTION__", captionJsonForFallback, StringComparison.Ordinal), ct: ct);
                dispatched = ReadBool(fallbackResponse);
            }
            await Task.Delay(75, ct);
            var eventResponse = await _chrome.EvalAsync("""
(() => {
  const diagnostic=window.__ttCaptionEventDiagnostic;
  if(!diagnostic)return {keydown:0,beforeinput:0,input:0,keyup:0,inputTypes:{}};
  for(const [type,handler] of Object.entries(diagnostic.handlers))diagnostic.el.removeEventListener(type,handler,true);
  delete window.__ttCaptionEventDiagnostic;
  return diagnostic.counts;
})()
""", ct: ct);
            var events = ReadObject(eventResponse);
            _log.Info(
                $"[VIDEO][CAPTION_EVENT_DIAGNOSTIC] strategy={mechanism} " +
                $"keydown={ReadInteger(events, "keydown")} beforeinput={ReadInteger(events, "beforeinput")} " +
                $"input={ReadInteger(events, "input")} keyup={ReadInteger(events, "keyup")} " +
                $"inputTypes={(events.TryGetProperty("inputTypes", out var inputTypes) ? inputTypes.GetRawText() : "{}")}");
            return (dispatched, mechanism);
        }
        finally
        {
            await _chrome.EvalAsync("""
(() => {
  const el=window.__ttVideoDescriptionEditor;
  const diagnostic=window.__ttCaptionEventDiagnostic;
  if(diagnostic){for(const [type,handler] of Object.entries(diagnostic.handlers))diagnostic.el.removeEventListener(type,handler,true);delete window.__ttCaptionEventDiagnostic}
  if(el?.getAttribute('data-codex-caption-target')===__TOKEN__)
    el.removeAttribute('data-codex-caption-target');
  return true;
})()
""".Replace("__TOKEN__", tokenJson, StringComparison.Ordinal), ct: ct);
        }
    }

    async Task<bool> ClearDescriptionForCaptionFallbackAsync(string filenameStem, CancellationToken ct)
    {
        var search = await FindDescriptionEditorAsync(filenameStem, ct);
        if (!search.Found) return false;
        var clearResponse = await _chrome.EvalAsync("""
(() => {
  const el=window.__ttVideoDescriptionEditor;
  if(!el?.isConnected)return false;
  el.focus();
  const selection=getSelection(),range=document.createRange();
  range.selectNodeContents(el);selection.removeAllRanges();selection.addRange(range);
  el.dispatchEvent(new InputEvent('beforeinput',{bubbles:true,cancelable:true,inputType:'deleteContentBackward',data:null}));
  document.execCommand('delete',false);
  if((el.innerText||el.textContent||'').replace(/[\u200B-\u200D\uFEFF\u00A0\s]/g,'').length)el.replaceChildren();
  el.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'deleteContentBackward',data:null}));
  return true;
})()
""", ct: ct);
        var dispatched = ReadBool(clearResponse);
        await Task.Delay(400, ct);
        search = await FindDescriptionEditorAsync(filenameStem, ct);
        var actual = search.Found ? await ReadDescriptionAsync(ct) : "\0";
        var actualLength = actual == "\0" ? -1 : NormalizeDescription(actual).Length;
        var cleared = dispatched && actualLength == 0;
        _log.Info(
            $"[VIDEO][CAPTION_INPUT_FALLBACK_CLEAR] dispatched={dispatched.ToString().ToLowerInvariant()} " +
            $"actualLength={actualLength} result={(cleared ? "PASS" : "FAIL")}");
        return cleared;
    }

    async Task<string> ConfigureCaptionAsync(TikTokVideoPostOptions options, bool handledModal, CancellationToken ct)
    {
        var configuredCaption = options.Caption ?? "";
        var normalizedConfiguredCaption = NormalizeDescription(configuredCaption);
        var fixedCaptionConfigured = options.CaptionMode == VideoCaptionMode.Fixed
            && !string.IsNullOrWhiteSpace(configuredCaption);
        var expectedDescription = fixedCaptionConfigured ? configuredCaption : "";
        _log.Info(
            $"[VIDEO][CAPTION_CONFIG] mode={options.CaptionMode} " +
            $"configured={fixedCaptionConfigured.ToString().ToLowerInvariant()} length={normalizedConfiguredCaption.Length}");

        var filenameStem = Path.GetFileNameWithoutExtension(options.VideoPath);
        _log.Info("[VIDEO] Clearing auto description");
        var search = await FindDescriptionEditorAsync(filenameStem, ct);
        if (!search.Found)
        {
            _log.Warn($"[VIDEO][DESCRIPTION_CANDIDATES] count={search.CandidateCount} candidates={search.Candidates}");
            var snapshot = await ReadSnapshotAsync(ct);
            _log.Error($"[VIDEO][VIDEO_FAILED] reason=DESCRIPTION_EDITOR_NOT_FOUND finalUrl={snapshot.Url} modalHandled={handledModal.ToString().ToLowerInvariant()} candidateCount={search.CandidateCount} editorReady=true");
            throw new VideoUploadFlowException("DESCRIPTION_EDITOR_NOT_FOUND", "Không tìm thấy Description/Caption editor theo semantic selector.");
        }
        _log.Info($"[VIDEO][DESCRIPTION_EDITOR_FOUND] tag={search.Tag} role={search.Role} contenteditable={search.ContentEditable} dataE2E={search.DataE2E} source={search.Source} containerLabel={search.ContainerLabel}");

        var cleared = false;
        for (var clearAttempt = 1; clearAttempt <= 2; clearAttempt++)
        {
            if (clearAttempt > 1)
            {
                search = await FindDescriptionEditorAsync(filenameStem, ct);
                if (!search.Found) break;
            }
            var clearResponse = await _chrome.EvalAsync("""
(() => {const el=window.__ttVideoDescriptionEditor;if(!el?.isConnected)return false;el.focus();if(el instanceof HTMLTextAreaElement||el instanceof HTMLInputElement){const proto=el instanceof HTMLTextAreaElement?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;const setter=Object.getOwnPropertyDescriptor(proto,'value')?.set;if(setter)setter.call(el,'');else el.value='';el.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'deleteContentBackward',data:null}));el.dispatchEvent(new Event('change',{bubbles:true}));}else{const selection=getSelection();const range=document.createRange();range.selectNodeContents(el);selection.removeAllRanges();selection.addRange(range);el.dispatchEvent(new InputEvent('beforeinput',{bubbles:true,cancelable:true,inputType:'deleteContentBackward',data:null}));document.execCommand('delete',false);if((el.innerText||el.textContent||'').replace(/[\u200B-\u200D\uFEFF\u00A0\s]/g,'').length)el.replaceChildren();el.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'deleteContentBackward',data:null}));el.dispatchEvent(new Event('change',{bubbles:true}));}el.blur();return true})()
""", ct: ct);
            var clearDispatched = ReadBool(clearResponse);
            _log.Info($"[VIDEO][DESCRIPTION_CLEAR] attempt={clearAttempt} result={(clearDispatched ? "OK" : "FAIL")}");
            if (!clearDispatched) continue;
            await Task.Delay(400, ct);
            search = await FindDescriptionEditorAsync(filenameStem, ct);
            if (!search.Found) continue;
            var actual = await ReadDescriptionAsync(ct);
            var actualLength = actual == "\0" ? -1 : NormalizeDescription(actual).Length;
            cleared = actualLength == 0;
            _log.Info($"[VIDEO][DESCRIPTION_CLEAR_VERIFY] actualLength={actualLength} result={(cleared ? "OK" : "FAIL")}");
            if (cleared) break;
        }
        if (!cleared)
            throw new VideoUploadFlowException("DESCRIPTION_CLEAR_FAILED", "Description vẫn còn nội dung sau hai lần clear/reacquire.");

        if (fixedCaptionConfigured)
        {
            var strategies = new[] { "DRAFTJS_KEYBOARD_TYPING", "DRAFTJS_EXEC_COMMAND_FALLBACK" };
            var emptySnapshot = new DescriptionDomSnapshot(
                false, "", "", "", "", "", "", 0, 0, 0, "[]", "[]", "{}", "{}", "[]", "");
            var finalSnapshot = emptySnapshot;
            var finalMechanism = strategies[0];
            var inputDispatched = false;
            var exactCanonicalMatch = false;

            for (var attempt = 1; attempt <= strategies.Length; attempt++)
            {
                var strategy = strategies[attempt - 1];
                if (attempt > 1 && !await ClearDescriptionForCaptionFallbackAsync(filenameStem, ct))
                    break;

                _log.Info(
                    $"[VIDEO][CAPTION_INPUT_ATTEMPT] attempt={attempt}/{strategies.Length} strategy={strategy} " +
                    $"expectedLength={normalizedConfiguredCaption.Length}");
                var setResult = await SetCaptionOnceAsync(configuredCaption, strategy, ct);
                finalMechanism = setResult.Mechanism;
                inputDispatched = setResult.Success;

                search = await FindDescriptionEditorAsync(filenameStem, ct);
                var settle = search.Found
                    ? await WaitForCaptionEditorSettleAsync(ct)
                    : (emptySnapshot, 0L, false);
                finalSnapshot = settle.Item1;
                _log.Info(
                    $"[VIDEO][CAPTION_SETTLE] attempt={attempt}/{strategies.Length} " +
                    $"elapsedMs={settle.Item2} stable={settle.Item3.ToString().ToLowerInvariant()}");
                LogCaptionDomDiagnostic(finalSnapshot);
                var actualFoundForAttempt = finalSnapshot.Found;
                var normalizedActualForAttempt = actualFoundForAttempt
                    ? NormalizeDescription(finalSnapshot.CanonicalText)
                    : "";
                exactCanonicalMatch = setResult.Success
                    && actualFoundForAttempt
                    && string.Equals(normalizedActualForAttempt, normalizedConfiguredCaption, StringComparison.Ordinal);
                _log.Info(
                    $"[VIDEO][CAPTION_INPUT_RESULT] attempt={attempt}/{strategies.Length} strategy={setResult.Mechanism} " +
                    $"expectedLength={normalizedConfiguredCaption.Length} " +
                    $"actualLength={(actualFoundForAttempt ? normalizedActualForAttempt.Length : -1)} " +
                    $"exactMatch={exactCanonicalMatch.ToString().ToLowerInvariant()}");
                if (exactCanonicalMatch) break;
            }

            var actualCaptionFound = finalSnapshot.Found;
            var normalizedActualCaption = actualCaptionFound ? NormalizeDescription(finalSnapshot.CanonicalText) : "";
            var setFailureReason = exactCanonicalMatch
                ? "NONE"
                : !inputDispatched ? "FOCUS_OR_INPUT_DISPATCH_FAILED"
                : !actualCaptionFound || normalizedActualCaption.Length == 0 ? "TEXT_NOT_COMMITTED"
                : "CONTENT_MISMATCH";
            _log.Info(
                $"[VIDEO][CAPTION_SET] mode={options.CaptionMode} length={normalizedConfiguredCaption.Length} " +
                $"mechanism={finalMechanism} result={(exactCanonicalMatch ? "OK" : "FAIL")} reason={setFailureReason}");
            _log.Info(
                $"[VIDEO][CAPTION_CANONICAL_READ] found={actualCaptionFound.ToString().ToLowerInvariant()} " +
                $"rawInnerTextLength={finalSnapshot.InnerText.Length} rawTextContentLength={finalSnapshot.TextContent.Length} " +
                $"canonicalLength={(actualCaptionFound ? normalizedActualCaption.Length : -1)} mentionCount={finalSnapshot.MentionCount}");
            _log.Info(
                $"[VIDEO][CAPTION_VERIFY] mode={options.CaptionMode} expectedLength={normalizedConfiguredCaption.Length} " +
                $"actualLength={(actualCaptionFound ? normalizedActualCaption.Length : -1)} " +
                $"rawInnerTextLength={finalSnapshot.InnerText.Length} rawTextContentLength={finalSnapshot.TextContent.Length} " +
                $"canonicalLength={(actualCaptionFound ? normalizedActualCaption.Length : -1)} " +
                $"exactCanonicalMatch={exactCanonicalMatch.ToString().ToLowerInvariant()} mentionCount={finalSnapshot.MentionCount} " +
                $"result={(exactCanonicalMatch ? "PASS" : "FAIL")}");
            if (!exactCanonicalMatch)
                throw new VideoUploadFlowException("CAPTION_VERIFY_FAILED", "Caption trong Description editor không khớp Caption cấu hình.");
        }

        var gateValue = await ReadDescriptionAsync(ct);
        var descriptionFound = gateValue != "\0";
        var normalizedGateValue = descriptionFound ? NormalizeDescription(gateValue) : "";
        var actualEmpty = descriptionFound && normalizedGateValue.Length == 0;
        var actualMatch = descriptionFound
            && string.Equals(normalizedGateValue, NormalizeDescription(expectedDescription), StringComparison.Ordinal);
        var descriptionGatePassed = fixedCaptionConfigured ? actualMatch : actualEmpty;
        if (fixedCaptionConfigured)
        {
            _log.Info(
                $"[VIDEO][DESCRIPTION_GATE] mode={options.CaptionMode} expected=CONFIGURED_CAPTION " +
                $"found={descriptionFound.ToString().ToLowerInvariant()} actualMatch={actualMatch.ToString().ToLowerInvariant()} " +
                $"result={(descriptionGatePassed ? "PASS" : "FAIL")}");
        }
        else
        {
            _log.Info(
                $"[VIDEO][DESCRIPTION_GATE] mode={options.CaptionMode} expected=EMPTY " +
                $"found={descriptionFound.ToString().ToLowerInvariant()} actualEmpty={actualEmpty.ToString().ToLowerInvariant()} " +
                $"result={(descriptionGatePassed ? "PASS" : "FAIL")}");
        }
        if (!descriptionGatePassed)
            throw new VideoUploadFlowException("DESCRIPTION_GATE_FAILED", "Description không đạt trạng thái mong đợi; không tiếp tục Post configuration.");
        _log.Info("[VIDEO] Caption configured");
        return expectedDescription;
    }

    async Task ConfigureNowAsync(CancellationToken ct)
    {
        var response = await _chrome.EvalAsync("""
(async() => {
  const norm=s=>String(s||'').trim().toLowerCase();
  const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>1&&r.height>1&&s.display!=='none'&&s.visibility!=='hidden'};
  const nodes=[...document.querySelectorAll('label,button,[role="radio"],[role="button"]')].filter(visible);
  const hit=nodes.find(e=>/^(now|post now|bây giờ|đăng ngay)$/.test(norm(e.innerText||e.textContent||e.getAttribute('aria-label'))));
  if(!hit) return false;
  const checked=hit.getAttribute('aria-checked')==='true'||hit.querySelector('input')?.checked;
  if(!checked) { hit.click(); await new Promise(r=>setTimeout(r,300)); }
  return checked||hit.getAttribute('aria-checked')==='true'||hit.querySelector('input')?.checked===true;
})()
""", ct: ct);
        if (!ReadBool(response)) throw new InvalidOperationException("Không cấu hình được When to post = Now.");
        _log.Info("[VIDEO] When to post = Now");
    }

    sealed record VisibilityProbe(
        bool ContainerFound, bool ControlFound, string Label, int CandidateCount,
        string Tag, string Role, string AriaHasPopup, string CurrentText,
        bool Enabled, string Candidates);

    sealed record VisibilityMenuState(bool Present, string Role, string Options);

    static string NormalizeVisibilityValue(string value)
    {
        var lines = (value ?? "")
            .Split(new[] { '\r', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeText)
            .Where(text => text.Length > 0)
            .ToArray();
        if (lines.Any(text => text.Equals("Mọi người", StringComparison.OrdinalIgnoreCase)
                              || text.Equals("Everyone", StringComparison.OrdinalIgnoreCase)))
            return "EVERYONE";
        return lines.Length == 0 ? "UNKNOWN" : "OTHER";
    }

    async Task<VisibilityProbe> ReadVisibilityProbeAsync(CancellationToken ct)
    {
        var response = await _chrome.EvalAsync("""
(() => {
 const fold=s=>String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
 const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),cs=getComputedStyle(e);return r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'};
 const labelMatch=t=>{const x=fold(t);return x.includes('nguoi co the nhin thay bai dang nay')||x.includes('who can watch this video')||x.includes('who can view this post')||x==='visibility'};
 const valueOf=e=>{if(!e)return '';if(e instanceof HTMLSelectElement)return e.selectedOptions?.[0]?.textContent||e.value||'';return e.getAttribute('aria-valuetext')||e.innerText||e.textContent||e.getAttribute('aria-label')||''};
 const labels=[...document.querySelectorAll('label,h1,h2,h3,h4,[role="heading"],p,span,div')].filter(e=>{const t=String(e.innerText||e.textContent||'').trim();return visible(e)&&t.length>0&&t.length<=180&&labelMatch(t)}).sort((a,b)=>String(a.innerText||a.textContent||'').length-String(b.innerText||b.textContent||'').length);
 let label=labels[0]||null,section=label?.parentElement||null,controls=[];
 if(label){let node=label.parentElement;for(let depth=1;node&&depth<=6;depth++,node=node.parentElement){const found=[...node.querySelectorAll('select,[role="combobox"],button[aria-haspopup],[aria-haspopup="listbox"],[aria-haspopup="menu"],button')].filter(visible);if(found.length){section=node;controls=found;break}}}
 const scored=controls.map((el,index)=>{const text=String(valueOf(el)).replace(/\s+/g,' ').trim().slice(0,160),f=fold(text),role=el.getAttribute('role')||'',popup=el.getAttribute('aria-haspopup')||'';let score=0;if(f==='moi nguoi'||f==='everyone')score+=420;if(el instanceof HTMLSelectElement)score+=190;if(role==='combobox')score+=180;if(popup==='listbox'||popup==='menu'||popup==='true')score+=160;if(el.tagName==='BUTTON')score+=35;const meta=fold(`${el.getAttribute('aria-label')||''} ${el.getAttribute('data-e2e')||''}`);if(labelMatch(meta))score+=240;if(/description|caption|mo ta|location|vi tri|schedule|lich dang/.test(meta))score-=500;return {el,score,diag:{index,tag:el.tagName||'',role,text,ariaLabel:String(el.getAttribute('aria-label')||'').slice(0,120),ariaHasPopup:popup,dataE2E:String(el.getAttribute('data-e2e')||'').slice(0,120),nearbyText:String(label?.innerText||label?.textContent||'').replace(/\s+/g,' ').trim().slice(0,160),score}}}).sort((a,b)=>b.score-a.score);
 const best=scored[0]||null;window.__ttVisibilitySection=section;window.__ttVisibilityControl=best?.el||null;
 return JSON.stringify({containerFound:!!label,controlFound:!!best,label:String(label?.innerText||label?.textContent||'').replace(/\s+/g,' ').trim().slice(0,160),candidateCount:controls.length,tag:best?.el.tagName||'',role:best?.el.getAttribute('role')||'',ariaHasPopup:best?.el.getAttribute('aria-haspopup')||'',currentText:String(best?valueOf(best.el):'').replace(/\s+/g,' ').trim().slice(0,160),enabled:!!best&&!best.el.disabled&&best.el.getAttribute('aria-disabled')!=='true',candidates:scored.slice(0,10).map(x=>x.diag)});
})()
""", ct: ct);
        var json = response.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "{}" : "{}";
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new(
            ReadBoolean(root, "containerFound"), ReadBoolean(root, "controlFound"),
            ReadString(root, "label"), ReadInteger(root, "candidateCount"), ReadString(root, "tag"),
            ReadString(root, "role"), ReadString(root, "ariaHasPopup"), ReadString(root, "currentText"),
            ReadBoolean(root, "enabled"),
            root.TryGetProperty("candidates", out var candidates) ? candidates.GetRawText() : "[]");
    }

    async Task<VisibilityMenuState> ReadVisibilityMenuAsync(CancellationToken ct)
    {
        var response = await _chrome.EvalAsync("""
(() => {
 const fold=s=>String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
 const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),cs=getComputedStyle(e);return r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'};
 const exact=e=>{const x=fold(e.innerText||e.textContent||e.getAttribute('aria-label')||'');return x==='moi nguoi'||x==='everyone'};
 const control=window.__ttVisibilityControl,controlledId=control?.getAttribute('aria-controls')||'';
 const roleMenus=[...document.querySelectorAll('[role="listbox"],[role="menu"],[data-popper-placement],[data-radix-popper-content-wrapper]')].filter(visible),before=window.__ttVisibilityMenusBeforeClick||new Set(),newMenus=roleMenus.filter(m=>!before.has(m));
 let menu=controlledId?document.getElementById(controlledId):null;if(menu&&!visible(menu))menu=null;
 if(!menu)menu=newMenus.find(m=>[...m.querySelectorAll('[role="option"],[role="menuitem"],li,button,[data-value]')].some(o=>visible(o)&&exact(o)))||newMenus[0]||roleMenus.find(m=>[...m.querySelectorAll('[role="option"],[role="menuitem"],li,button,[data-value]')].some(o=>visible(o)&&exact(o)))||null;
 if(!menu){const option=[...document.querySelectorAll('[role="option"],[role="menuitem"],li')].find(o=>visible(o)&&exact(o));menu=option?.closest('[role="listbox"],[role="menu"],[data-popper-placement],[data-radix-popper-content-wrapper]')||null;}
 const optionNodes=menu?[...menu.querySelectorAll('[role="option"],[role="menuitem"],li,button,[data-value]')].filter(visible):[];
 const target=optionNodes.find(exact)||null;window.__ttVisibilityMenu=menu;window.__ttVisibilityEveryoneOption=target;
 const options=[...new Set(optionNodes.map(o=>String(o.innerText||o.textContent||o.getAttribute('aria-label')||'').replace(/\s+/g,' ').trim()).filter(Boolean))].slice(0,12);
 return JSON.stringify({present:!!menu,role:menu?.getAttribute('role')||'',options});
})()
""", ct: ct);
        var json = response.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "{}" : "{}";
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new(ReadBoolean(root, "present"), ReadString(root, "role"),
            root.TryGetProperty("options", out var options) ? options.GetRawText() : "[]");
    }

    async Task<bool> ConfigureVisibilityAsync(TikTokVideoVisibility visibility, CancellationToken ct)
    {
        _ = visibility; // V13 hiện chỉ hỗ trợ target an toàn EVERYONE.
        var state = await ReadVisibilityProbeAsync(ct);
        _log.Info($"[VIDEO][VISIBILITY_CONTAINER] found={state.ContainerFound.ToString().ToLowerInvariant()} label={state.Label} candidateCount={state.CandidateCount}");
        if (!state.ContainerFound)
            throw new VideoUploadFlowException("VISIBILITY_CONTAINER_NOT_FOUND", "Không tìm thấy section Visibility theo semantic label.");
        if (!state.ControlFound)
        {
            _log.Warn($"[VIDEO][VISIBILITY_CANDIDATES] count={state.CandidateCount} candidates={state.Candidates}");
            throw new VideoUploadFlowException("VISIBILITY_CONTROL_NOT_FOUND", "Không tìm thấy control Visibility trong section đã xác định.");
        }

        var normalized = NormalizeVisibilityValue(state.CurrentText);
        var alreadyExpected = normalized == "EVERYONE";
        _log.Info(
            $"[VIDEO][VISIBILITY_STATE] found=true tag={state.Tag} role={state.Role} ariaHasPopup={state.AriaHasPopup} " +
            $"currentText={state.CurrentText} normalizedValue={normalized} alreadyExpected={alreadyExpected.ToString().ToLowerInvariant()}");
        if (alreadyExpected)
        {
            _log.Info("[VIDEO][VISIBILITY_ALREADY_EXPECTED] value=EVERYONE");
            _log.Info("[VIDEO][VISIBILITY_GATE] expected=EVERYONE actual=EVERYONE result=PASS");
            _log.Info("[VIDEO] Visibility = Everyone");
            return true;
        }
        if (normalized == "UNKNOWN")
            throw new VideoUploadFlowException("VISIBILITY_VALUE_UNKNOWN", "Không đọc được giá trị hiện tại của Visibility control.");

        _log.Info(
            $"[VIDEO][VISIBILITY_CONTROL_TARGET] tag={state.Tag} role={state.Role} text={state.CurrentText} " +
            $"dialogScoped=false sectionScoped=true enabled={state.Enabled.ToString().ToLowerInvariant()}");
        if (!state.Enabled)
            throw new VideoUploadFlowException("VISIBILITY_CONTROL_NOT_FOUND", "Visibility control không khả dụng.");

        var clicked = await _chrome.EvalAsync("""
(() => {const e=window.__ttVisibilityControl,s=window.__ttVisibilitySection;if(!e?.isConnected||!s?.isConnected||!s.contains(e)||e.disabled||e.getAttribute('aria-disabled')==='true')return false;const visible=x=>{const r=x.getBoundingClientRect(),cs=getComputedStyle(x);return r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'};window.__ttVisibilityMenusBeforeClick=new Set([...document.querySelectorAll('[role="listbox"],[role="menu"],[data-popper-placement],[data-radix-popper-content-wrapper]')].filter(visible));e.scrollIntoView({block:'center'});e.click();return true})()
""", ct: ct);
        if (!ReadBool(clicked))
            throw new VideoUploadFlowException("VISIBILITY_SELECTION_FAILED", "Không click được Visibility control đã scope theo section.");

        VisibilityMenuState menu = new(false, "", "[]");
        var menuDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < menuDeadline)
        {
            menu = await ReadVisibilityMenuAsync(ct);
            if (menu.Present) break;
            await Task.Delay(250, ct);
        }
        _log.Info($"[VIDEO][VISIBILITY_MENU] present={menu.Present.ToString().ToLowerInvariant()} role={menu.Role} options={menu.Options}");
        if (!menu.Present)
            throw new VideoUploadFlowException("VISIBILITY_MENU_NOT_OPENED", "Menu/listbox Visibility không xuất hiện sau khi click control.");

        var optionResponse = await _chrome.EvalAsync("""
(() => {const m=window.__ttVisibilityMenu,o=window.__ttVisibilityEveryoneOption;const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),cs=getComputedStyle(e);return r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'};return {found:!!m?.isConnected&&!!o?.isConnected&&m.contains(o),text:String(o?.innerText||o?.textContent||o?.getAttribute('aria-label')||'').replace(/\s+/g,' ').trim(),visible:visible(o),enabled:!!o&&!o.disabled&&o.getAttribute('aria-disabled')!=='true',menuScoped:!!m&&!!o&&m.contains(o)}})()
""", ct: ct);
        var option = ReadObject(optionResponse);
        var optionFound = ReadBoolean(option, "found");
        _log.Info(
            $"[VIDEO][VISIBILITY_OPTION_TARGET] text={ReadString(option, "text")} normalizedValue=EVERYONE " +
            $"visible={ReadBoolean(option, "visible").ToString().ToLowerInvariant()} enabled={ReadBoolean(option, "enabled").ToString().ToLowerInvariant()} " +
            $"menuScoped={ReadBoolean(option, "menuScoped").ToString().ToLowerInvariant()}");
        if (!optionFound)
            throw new VideoUploadFlowException("VISIBILITY_EVERYONE_OPTION_NOT_FOUND", "Không tìm thấy option Mọi người/Everyone trong menu Visibility.");
        if (!ReadBoolean(option, "visible") || !ReadBoolean(option, "enabled"))
            throw new VideoUploadFlowException("VISIBILITY_SELECTION_FAILED", "Option Mọi người/Everyone không khả dụng.");

        var optionClicked = await _chrome.EvalAsync("""
(() => {const m=window.__ttVisibilityMenu,o=window.__ttVisibilityEveryoneOption;if(!m?.isConnected||!o?.isConnected||!m.contains(o)||o.disabled||o.getAttribute('aria-disabled')==='true')return false;o.click();return true})()
""", ct: ct);
        if (!ReadBool(optionClicked))
            throw new VideoUploadFlowException("VISIBILITY_SELECTION_FAILED", "Không click được option Mọi người/Everyone.");
        _log.Info("[VIDEO][VISIBILITY_OPTION_CLICKED] value=EVERYONE");

        VisibilityProbe verified = state;
        var verifyDeadline = DateTime.UtcNow.AddSeconds(5);
        var verifiedValue = "UNKNOWN";
        while (DateTime.UtcNow < verifyDeadline)
        {
            await Task.Delay(250, ct);
            verified = await ReadVisibilityProbeAsync(ct);
            verifiedValue = NormalizeVisibilityValue(verified.CurrentText);
            if (verified.ControlFound && verifiedValue == "EVERYONE") break;
        }
        var verifyOk = verified.ControlFound && verifiedValue == "EVERYONE";
        _log.Info($"[VIDEO][VISIBILITY_VERIFY] actualText={verified.CurrentText} normalizedValue={verifiedValue} result={(verifyOk ? "OK" : "FAIL")}");
        _log.Info($"[VIDEO][VISIBILITY_GATE] expected=EVERYONE actual={verifiedValue} result={(verifyOk ? "PASS" : "FAIL")}");
        if (!verifyOk)
            throw new VideoUploadFlowException("VISIBILITY_VERIFY_FAILED", "Visibility không giữ đúng giá trị Mọi người/Everyone sau selection.");
        _log.Info("[VIDEO] Visibility = Everyone");
        return true;
    }

    async Task EnsureHighQualityAsync(CancellationToken ct)
    {
        var response = await _chrome.EvalAsync("""
(async() => {
  const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>1&&r.height>1&&s.display!=='none'&&s.visibility!=='hidden'};
  const labels=[...document.querySelectorAll('label,div,span')].filter(visible);
  const label=labels.find(e=>/high-quality uploads|upload hd|tải lên chất lượng cao/i.test(e.innerText||e.textContent||''));
  if(!label) return false;
  let root=label;
  for(let i=0;i<5&&root;i++,root=root.parentElement){
    const toggle=root.querySelector('[role="switch"],input[type="checkbox"],button[aria-checked]');
    if(!toggle) continue;
    const on=toggle.checked===true||toggle.getAttribute('aria-checked')==='true'||toggle.getAttribute('data-state')==='checked';
    if(!on) { toggle.click(); await new Promise(r=>setTimeout(r,350)); }
    return on||toggle.checked===true||toggle.getAttribute('aria-checked')==='true'||toggle.getAttribute('data-state')==='checked';
  }
  return false;
})()
""", ct: ct);
        if (!ReadBool(response)) throw new InvalidOperationException("Không bật/xác minh được High-quality uploads.");
        _log.Info("[VIDEO] High Quality = ON");
    }

    async Task<(TikTokCheckState Music, TikTokCheckState Content, string BlockingError)> WaitForChecksAsync(
        TikTokVideoPostOptions options, CancellationToken ct)
    {
        _log.Info("[VIDEO] Checking TikTok checks");
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(options.CheckTimeoutSeconds, 10, 900));
        TikTokCheckState music = TikTokCheckState.Unknown, content = TikTokCheckState.Unknown;
        TikTokCheckState loggedMusic = (TikTokCheckState)(-1), loggedContent = (TikTokCheckState)(-1);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var response = await _chrome.EvalAsync("""
(() => {
 const text=(document.body?.innerText||'').replace(/\s+/g,' ').toLowerCase();
 const slice=k=>{const i=text.indexOf(k);return i<0?'':text.slice(i,i+260)};
 return { music:slice('music copyright check')||slice('copyright check'), content:slice('content check lite')||slice('content check'), body:text.slice(0,12000) };
})()
""", ct: ct);
            var value = ReadObject(response);
            var musicText = ReadString(value, "music");
            var contentText = ReadString(value, "content");
            var body = ReadString(value, "body");
            music = ParseCheck(musicText);
            content = ParseCheck(contentText);
            if (music != loggedMusic)
            {
                _log.Info($"[VIDEO] Music check = {music.ToString().ToUpperInvariant()}");
                loggedMusic = music;
            }
            if (content != loggedContent)
            {
                _log.Info($"[VIDEO] Content check = {content.ToString().ToUpperInvariant()}");
                loggedContent = content;
            }
            if (music == TikTokCheckState.Blocked) return (music, content, "copyright issue");
            if (content == TikTokCheckState.Blocked) return (music, content, "content check blocked");
            if (ContainsBlockingText(body)) return (music, content, "TikTok returned a blocking error");
            var snapshot = await ReadSnapshotAsync(ct);
            if (snapshot.PostEnabled && music != TikTokCheckState.Checking && content != TikTokCheckState.Checking)
                return (music, content, "");
            await Task.Delay(800, ct);
        }
        var final = await ReadSnapshotAsync(ct);
        return final.PostEnabled
            ? (music, content, "")
            : (music, content, "Post button disabled after checks timeout");
    }

    static TikTokCheckState ParseCheck(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TikTokCheckState.Unknown;
        if (text.Contains("checking") || text.Contains("processing") || text.Contains("đang kiểm tra")) return TikTokCheckState.Checking;
        if (text.Contains("no issues") || text.Contains("passed") || text.Contains("không có vấn đề")) return TikTokCheckState.Passed;
        if (text.Contains("blocked") || text.Contains("copyright issue") || text.Contains("not eligible") || text.Contains("violation")) return TikTokCheckState.Blocked;
        if (text.Contains("warning") || text.Contains("issue")) return TikTokCheckState.Warning;
        return TikTokCheckState.Unknown;
    }

    static bool ContainsBlockingText(string text)
        => new[] { "couldn't upload", "upload failed", "copyright violation", "content violation", "not eligible to post", "không thể tải", "vi phạm bản quyền" }
            .Any(text.Contains);

    sealed record MainPostClickResult(bool Found, bool Clicked);

    sealed record PostConfirmDialogProbe(
        bool Present, string Role, string Heading, string Buttons,
        bool TextMatched, bool NewOrChanged,
        bool ConfirmButtonFound, string ConfirmButtonText,
        bool ConfirmButtonVisible, bool ConfirmButtonEnabled);

    sealed record PostConfirmCloseResult(bool Closed, long ElapsedMs);

    sealed record PostVerificationResult(bool Success, string FailureReason, string Message);

    static void LogPostOutcome(Logger log, string state)
        => log.Info($"[VIDEO][POST_OUTCOME] state={state}");

    void LogPostOutcome(string state) => LogPostOutcome(_log, state);

    async Task<MainPostClickResult> ClickPostAsync(CancellationToken ct)
    {
        var selectors = JsonSerializer.Serialize(TikTokSelectors.Upload.PostButton);
        var response = await _chrome.EvalAsync($$"""
(() => {
 const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>1&&r.height>1&&s.display!=='none'&&s.visibility!=='hidden'};
 const enabled=e=>!e.disabled&&e.getAttribute('aria-disabled')!=='true';
 const selectors={{selectors}};
 const semantic=selectors.map(s=>[...document.querySelectorAll(s)].find(e=>visible(e)&&enabled(e))).find(Boolean);
 const byText=[...document.querySelectorAll('button,[role="button"]')].find(e=>visible(e)&&enabled(e)&&/^(post|đăng)$/.test((e.innerText||e.textContent||'').trim().toLowerCase()));
 const hit=semantic||byText;if(!hit)return {found:false,clicked:false};
 try{
   hit.click();return {found:true,clicked:true};
 }catch{
   return {found:true,clicked:false};
 }
})()
""", ct: ct);
        var value = ReadObject(response);
        return new MainPostClickResult(ReadBoolean(value, "found"), ReadBoolean(value, "clicked"));
    }

    async Task<PostConfirmDialogProbe> ProbePostConfirmDialogAsync(CancellationToken ct)
    {
        var response = await _chrome.EvalAsync("""
(() => {
 const fold=s=>String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
 const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),cs=getComputedStyle(e);return r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'&&cs.opacity!=='0'};
 const exactHeading=t=>['tiep tuc dang?','tiep tuc dang','continue posting?','continue posting','post anyway?','post anyway'].includes(fold(t));
 const exactCancel=t=>['huy','cancel'].includes(fold(t));
 const exactConfirm=t=>['dang ngay','post now','post anyway','continue posting'].includes(fold(t));
 window.__ttVideoPostConfirmDialog=null;window.__ttVideoPostConfirmButton=null;
 const dialogs=[...document.querySelectorAll('[role="dialog"],div[aria-modal="true"]')].filter(visible);
 let matched=null,meta=null;
 for(const dialog of dialogs){
   const text=String(dialog.innerText||dialog.textContent||'').replace(/\s+/g,' ').trim();
   const headings=[...dialog.querySelectorAll('h1,h2,h3,[role="heading"]')].filter(visible);
   const headingNode=headings.find(h=>exactHeading(h.innerText||h.textContent||''))||null;
   const textMatched=!!headingNode||/(^| )tiep tuc dang\??( |$)|continue posting\?|post anyway\?/i.test(fold(text));
   const buttons=[...dialog.querySelectorAll('button,[role="button"]')].filter(visible);
   const cancel=buttons.find(b=>exactCancel(b.innerText||b.textContent||b.getAttribute('aria-label')||''))||null;
   const confirm=buttons.find(b=>exactConfirm(b.innerText||b.textContent||b.getAttribute('aria-label')||''))||null;
   if(!textMatched||!cancel||!confirm)continue;
   const heading=String(headingNode?.innerText||headingNode?.textContent||text.match(/Tiếp tục đăng\?|Continue posting\?|Post anyway\?/i)?.[0]||'').replace(/\s+/g,' ').trim();
   const buttonTexts=buttons.map(b=>String(b.innerText||b.textContent||b.getAttribute('aria-label')||'').replace(/\s+/g,' ').trim()).filter(Boolean);
   const signature=fold(`${heading}|${buttonTexts.join('|')}|${text}`);
   const newOrChanged=dialog!==window.__ttVideoPostConfirmLastElement||signature!==String(window.__ttVideoPostConfirmSignature||'');
   window.__ttVideoPostConfirmLastElement=dialog;window.__ttVideoPostConfirmSignature=signature;window.__ttVideoPostConfirmDialog=dialog;window.__ttVideoPostConfirmButton=confirm;
   matched=dialog;meta={present:true,role:dialog.getAttribute('role')||'',heading,buttons:buttonTexts.join(','),textMatched:true,newOrChanged,confirmButtonFound:true,confirmButtonText:String(confirm.innerText||confirm.textContent||confirm.getAttribute('aria-label')||'').replace(/\s+/g,' ').trim(),confirmButtonVisible:visible(confirm),confirmButtonEnabled:!confirm.disabled&&confirm.getAttribute('aria-disabled')!=='true'};
   break;
 }
 return meta||{present:false,role:'',heading:'',buttons:'',textMatched:false,newOrChanged:false,confirmButtonFound:false,confirmButtonText:'',confirmButtonVisible:false,confirmButtonEnabled:false};
})()
""", ct: ct);
        var value = ReadObject(response);
        return new PostConfirmDialogProbe(
            ReadBoolean(value, "present"), ReadString(value, "role"), ReadString(value, "heading"),
            ReadString(value, "buttons"), ReadBoolean(value, "textMatched"), ReadBoolean(value, "newOrChanged"),
            ReadBoolean(value, "confirmButtonFound"), ReadString(value, "confirmButtonText"),
            ReadBoolean(value, "confirmButtonVisible"), ReadBoolean(value, "confirmButtonEnabled"));
    }

    async Task<PostConfirmDialogProbe> WaitForPostConfirmDialogAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        PostConfirmDialogProbe last = new(false, "", "", "", false, false, false, "", false, false);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try { last = await ProbePostConfirmDialogAsync(ct); }
            catch (Exception ex) when (!_chrome.IsCdpSessionLost(ex) && ex is not OperationCanceledException)
            {
                await Task.Delay(225, ct);
                continue;
            }
            if (last.Present) break;
            await Task.Delay(225, ct);
        }
        _log.Info(
            $"[VIDEO][POST_CONFIRM_DIALOG] present={last.Present.ToString().ToLowerInvariant()} role={last.Role} " +
            $"heading={last.Heading} buttons=[{last.Buttons}] textMatched={last.TextMatched.ToString().ToLowerInvariant()} " +
            $"newOrChanged={last.NewOrChanged.ToString().ToLowerInvariant()}");
        return last;
    }

    async Task<bool> ClickPostConfirmAsync(CancellationToken ct)
    {
        var response = await _chrome.EvalAsync("""
(() => {
 const fold=s=>String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
 const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),cs=getComputedStyle(e);return r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'};
 const dialog=window.__ttVideoPostConfirmDialog,button=window.__ttVideoPostConfirmButton;
 if(!dialog?.isConnected||!button?.isConnected||!dialog.contains(button)||!visible(dialog)||!visible(button)||button.disabled||button.getAttribute('aria-disabled')==='true')return false;
 if(!['dang ngay','post now','post anyway','continue posting'].includes(fold(button.innerText||button.textContent||button.getAttribute('aria-label')||'')))return false;
 button.click();return true;
})()
""", ct: ct);
        return ReadBool(response);
    }

    async Task<PostConfirmCloseResult> WaitForPostConfirmDialogClosedAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var closed = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct);
            try
            {
                var response = await _chrome.EvalAsync("""
(() => {
 const fold=s=>String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
 const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),cs=getComputedStyle(e);return r.width>2&&r.height>2&&cs.display!=='none'&&cs.visibility!=='hidden'&&cs.opacity!=='0'};
 const heading=t=>/(^| )tiep tuc dang\??( |$)|continue posting\?|post anyway\?/.test(fold(t));
 const cached=window.__ttVideoPostConfirmDialog;
 if(cached?.isConnected&&visible(cached)&&heading(cached.innerText||cached.textContent||''))return true;
 return [...document.querySelectorAll('[role="dialog"],div[aria-modal="true"]')].some(d=>visible(d)&&heading(d.innerText||d.textContent||''));
})()
""", ct: ct);
                if (!ReadBool(response)) { closed = true; break; }
            }
            catch (Exception ex) when (!_chrome.IsCdpSessionLost(ex) && ex is not OperationCanceledException)
            {
                // A navigation can destroy the old execution context; reacquire on the next poll.
            }
        }
        _log.Info($"[VIDEO][POST_CONFIRM_DIALOG_CLOSED] result={closed.ToString().ToLowerInvariant()} elapsedMs={watch.ElapsedMilliseconds}");
        return new PostConfirmCloseResult(closed, watch.ElapsedMilliseconds);
    }

    async Task<PostVerificationResult> VerifyPostAsync(
        string beforeUrl, TikTokVideoPostOptions options, VideoPostNetworkObserver networkObserver, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(options.PostVerifyTimeoutSeconds, 15, 600));
        var lastState = "SUBMITTING";
        var lastSuccessToast = false;
        var lastNavigationChanged = false;
        var lastEditorClosed = false;
        var lastPublishedState = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            JsonElement response;
            try
            {
                response = await _chrome.EvalAsync("""
(() => ({
 url:location.href,
 successToast:[...document.querySelectorAll('[role="alert"],[role="status"],[data-e2e*="toast"],[data-e2e*="success"]')]
   .filter(e=>{const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>1&&r.height>1&&s.display!=='none'&&s.visibility!=='hidden'})
   .some(e=>/video (has been )?(uploaded|posted|published)|post(ed|ing)? successfully|đã đăng|đăng thành công|tải lên thành công/i.test(e.innerText||e.textContent||'')),
 errorText:([...document.querySelectorAll('[role="alert"],[role="status"],[role="dialog"],[data-e2e*="toast"]')]
   .filter(e=>{const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>1&&r.height>1&&s.display!=='none'&&s.visibility!=='hidden'})
   .map(e=>String(e.innerText||e.textContent||'').replace(/\s+/g,' ').trim())
   .find(t=>/couldn.t post|post failed|unable to post|không thể đăng|đăng thất bại|lỗi khi đăng/i.test(t))||'').slice(0,180),
 editorVisible:[...document.querySelectorAll('input[type="file"],[contenteditable="true"],textarea')].some(e=>{const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>1&&r.height>1&&s.display!=='none'&&s.visibility!=='hidden'}),
 publishedStateDetected:/your video (has been|was) (posted|published)|video published|post published|video của bạn đã được đăng|đã đăng video/i.test(document.body?.innerText||''),
 submitting:/posting|publishing|đang đăng|đang xuất bản/i.test(document.body?.innerText||'')
}))()
""", ct: ct);
            }
            catch (Exception ex) when (!_chrome.IsCdpSessionLost(ex) && ex is not OperationCanceledException)
            {
                await Task.Delay(500, ct);
                continue;
            }
            var value = ReadObject(response);
            var url = ReadString(value, "url");
            var successToast = ReadBoolean(value, "successToast");
            var errorText = ReadString(value, "errorText");
            var editorClosed = !ReadBoolean(value, "editorVisible");
            var publishedStateDetected = ReadBoolean(value, "publishedStateDetected");
            var navigationChanged = !string.IsNullOrWhiteSpace(url)
                && !url.Equals(beforeUrl, StringComparison.OrdinalIgnoreCase)
                && !url.Contains("/upload", StringComparison.OrdinalIgnoreCase);
            lastSuccessToast = successToast;
            lastNavigationChanged = navigationChanged;
            lastEditorClosed = editorClosed;
            lastPublishedState = publishedStateDetected;

            if (errorText.Length > 0)
            {
                if (lastState != "ERROR") { LogPostOutcome("ERROR"); lastState = "ERROR"; }
                return new(false, "POST_SUBMIT_ERROR", "TikTok hiển thị lỗi sau khi submit: " + errorText);
            }
            if (networkObserver.ResponseRejected)
            {
                if (lastState != "ERROR") { LogPostOutcome("ERROR"); lastState = "ERROR"; }
                return new(false, "POST_RESPONSE_REJECTED", "Response của request publish/post bị TikTok từ chối.");
            }

            var success = successToast || publishedStateDetected || navigationChanged;
            if (success)
            {
                if (lastState != "SUCCESS") LogPostOutcome("SUCCESS");
                _log.Info(
                    $"[VIDEO][POST_SUCCESS_EVIDENCE] successToast={successToast.ToString().ToLowerInvariant()} " +
                    $"navigationChanged={navigationChanged.ToString().ToLowerInvariant()} editorClosed={editorClosed.ToString().ToLowerInvariant()} " +
                    $"publishedStateDetected={publishedStateDetected.ToString().ToLowerInvariant()} result=OK");
                return new(true, "", "");
            }
            await Task.Delay(700, ct);
        }
        LogPostOutcome("UNKNOWN");
        _log.Info(
            $"[VIDEO][POST_SUCCESS_EVIDENCE] successToast={lastSuccessToast.ToString().ToLowerInvariant()} " +
            $"navigationChanged={lastNavigationChanged.ToString().ToLowerInvariant()} editorClosed={lastEditorClosed.ToString().ToLowerInvariant()} " +
            $"publishedStateDetected={lastPublishedState.ToString().ToLowerInvariant()} result=FAIL");
        var failureReason = lastEditorClosed ? "POST_SUCCESS_NOT_VERIFIED" : "POST_VERIFY_TIMEOUT";
        return new(false, failureReason, "Hết thời gian chờ nhưng chưa có đủ evidence xác nhận đăng video thành công.");
    }

    sealed class VideoPostNetworkObserver : IDisposable
    {
        sealed record RequestMetadata(
            string CorrelationId, string Path, string Method, string Phase, bool CommitCandidate);

        readonly ChromeController _chrome;
        readonly Logger _log;
        readonly object _gate = new();
        readonly Dictionary<string, RequestMetadata> _requests = new(StringComparer.Ordinal);
        IDisposable? _subscription;
        string _phase = "AFTER_MAIN_POST";
        int _sequence;
        int _responseRejected;
        bool _capturing = true;

        VideoPostNetworkObserver(ChromeController chrome, Logger log)
        {
            _chrome = chrome;
            _log = log;
        }

        public bool ResponseRejected => Volatile.Read(ref _responseRejected) != 0;

        public static async Task<VideoPostNetworkObserver> StartAsync(
            ChromeController chrome, Logger log, CancellationToken ct)
        {
            var observer = new VideoPostNetworkObserver(chrome, log);
            try
            {
                await chrome.EnableVideoPostNetworkObservationAsync(ct);
                observer._subscription = chrome.SubscribeToVideoPostNetworkEvents(observer.Observe);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Warn("[VIDEO][POST_REQUEST_OBSERVER] available=false error=" + SafeLogText(ex.Message));
            }
            return observer;
        }

        public void SetPhase(string phase)
        {
            lock (_gate) _phase = phase;
        }

        void Observe(string eventName, JsonElement parameters)
        {
            if (!_capturing || parameters.ValueKind != JsonValueKind.Object) return;
            try
            {
                if (eventName == "Network.requestWillBeSent") ObserveRequest(parameters);
                else if (eventName == "Network.responseReceived") ObserveResponse(parameters);
            }
            catch
            {
                // Diagnostics must never interrupt the UI post flow.
            }
        }

        void ObserveRequest(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("requestId", out var requestIdElement)
                || !parameters.TryGetProperty("request", out var request)) return;
            var requestId = requestIdElement.GetString() ?? "";
            var method = request.TryGetProperty("method", out var methodElement)
                ? (methodElement.GetString() ?? "").ToUpperInvariant() : "";
            var rawUrl = request.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? "" : "";
            var resourceType = parameters.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? "" : "";
            if (requestId.Length == 0 || method != "POST"
                || !(resourceType.Equals("Fetch", StringComparison.OrdinalIgnoreCase)
                     || resourceType.Equals("XHR", StringComparison.OrdinalIgnoreCase))
                || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
                || !uri.Host.EndsWith("tiktok.com", StringComparison.OrdinalIgnoreCase)) return;

            var path = uri.AbsolutePath;
            var foldedPath = path.ToLowerInvariant();
            var commitCandidate = (foldedPath.Contains("publish", StringComparison.Ordinal)
                                   || foldedPath.Contains("/post", StringComparison.Ordinal)
                                   || foldedPath.Contains("post/", StringComparison.Ordinal))
                                  && !foldedPath.Contains("report", StringComparison.Ordinal)
                                  && !foldedPath.Contains("log", StringComparison.Ordinal)
                                  && !foldedPath.Contains("event", StringComparison.Ordinal)
                                  && !foldedPath.Contains("stat", StringComparison.Ordinal);
            RequestMetadata metadata;
            lock (_gate)
            {
                metadata = new RequestMetadata(
                    "P" + (++_sequence), path, method, _phase, commitCandidate);
                _requests[requestId] = metadata;
            }
            _log.Info(
                $"[VIDEO][POST_REQUEST] id={metadata.CorrelationId} method={metadata.Method} " +
                $"path={metadata.Path} phase={metadata.Phase} timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        }

        void ObserveResponse(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("requestId", out var requestIdElement)
                || !parameters.TryGetProperty("response", out var response)) return;
            var requestId = requestIdElement.GetString() ?? "";
            RequestMetadata metadata;
            lock (_gate)
            {
                if (!_requests.TryGetValue(requestId, out metadata!)) return;
            }
            var httpStatus = response.TryGetProperty("status", out var statusElement)
                && statusElement.TryGetDouble(out var status) ? (int)status : 0;
            _ = CompleteResponseAsync(requestId, metadata, httpStatus);
        }

        async Task CompleteResponseAsync(string requestId, RequestMetadata metadata, int httpStatus)
        {
            var businessSuccess = "unknown";
            int? statusCode = null, errorCode = null;
            var message = "";
            try
            {
                var body = await _chrome.ReadVideoPostResponseBodyAsync(requestId);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var document = JsonDocument.Parse(body);
                    var root = document.RootElement;
                    if (TryReadBoolean(root, "success", out var success)) businessSuccess = success ? "true" : "false";
                    statusCode = TryReadInteger(root, "status_code") ?? TryReadInteger(root, "statusCode");
                    errorCode = TryReadInteger(root, "error_code") ?? TryReadInteger(root, "errorCode");
                    message = TryReadString(root, "status_msg")
                              ?? TryReadString(root, "message")
                              ?? TryReadString(root, "error_message")
                              ?? "";
                    if (businessSuccess == "unknown" && statusCode.HasValue)
                        businessSuccess = statusCode.Value == 0 ? "true" : "false";
                    if (businessSuccess == "unknown" && errorCode.HasValue)
                        businessSuccess = errorCode.Value == 0 ? "true" : "false";
                }
            }
            catch
            {
                // Response body is optional diagnostic data; HTTP metadata remains usable.
            }

            var rejected = metadata.CommitCandidate
                           && (httpStatus >= 400
                               || businessSuccess == "false"
                               || statusCode is not null and not 0
                               || errorCode is not null and not 0);
            if (rejected) Interlocked.Exchange(ref _responseRejected, 1);
            _log.Info(
                $"[VIDEO][POST_RESPONSE] id={metadata.CorrelationId} path={metadata.Path} httpStatus={httpStatus} " +
                $"businessSuccess={businessSuccess} statusCode={(statusCode?.ToString() ?? "unknown")} " +
                $"errorCode={(errorCode?.ToString() ?? "unknown")} message={SafeLogText(message)} " +
                $"timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        }

        static bool TryReadBoolean(JsonElement root, string name, out bool value)
        {
            value = false;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(name, out var item)
                || item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            value = item.GetBoolean();
            return true;
        }

        static int? TryReadInteger(JsonElement root, string name)
            => root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(name, out var item)
               && item.TryGetInt32(out var value) ? value : null;

        static string? TryReadString(JsonElement root, string name)
            => root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(name, out var item)
               && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

        static string SafeLogText(string value)
        {
            var safe = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return safe.Length <= 140 ? safe : safe[..140] + "...";
        }

        public void Dispose()
        {
            _capturing = false;
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    async Task<UploadSnapshot> ReadSnapshotAsync(CancellationToken ct, string expectedFilename = "")
    {
        var fileSelectors = JsonSerializer.Serialize(TikTokSelectors.Upload.FileInput);
        var descriptionSelectors = JsonSerializer.Serialize(TikTokSelectors.Upload.Description);
        var postSelectors = JsonSerializer.Serialize(TikTokSelectors.Upload.PostButton);
        var progressSelectors = JsonSerializer.Serialize(TikTokSelectors.Upload.Progress);
        var expectedFilenameJson = JsonSerializer.Serialize((expectedFilename ?? "").ToLowerInvariant());
        var response = await _chrome.EvalAsync($$"""
(() => {
 const expectedFilename={{expectedFilenameJson}};
 const norm=s=>String(s||'').replace(/\s+/g,' ').trim().toLowerCase();
 const visible=e=>{if(!e)return false;const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>1&&r.height>1&&s.display!=='none'&&s.visibility!=='hidden'};
 const firstVisible=(selectors)=>selectors.map(s=>[...document.querySelectorAll(s)].find(visible)).find(Boolean)||null;
 const fileInputs=[...document.querySelectorAll('input[type="file"]')];
 const file=fileInputs.sort((a,b)=>(String(b.accept||'').toLowerCase().includes('video')?1:0)-(String(a.accept||'').toLowerCase().includes('video')?1:0))[0]||null;
 const description=firstVisible({{descriptionSelectors}});
 const post=firstVisible({{postSelectors}})||[...document.querySelectorAll('button,[role="button"]')].find(e=>visible(e)&&/^(post|đăng)$/.test(norm(e.innerText||e.textContent||'')));
 const selectVideo=[...document.querySelectorAll('button,[role="button"]')].find(e=>visible(e)&&!e.disabled&&e.getAttribute('aria-disabled')!=='true'&&['chọn video','select video','select file','upload video'].includes(norm(e.innerText||e.textContent||e.getAttribute('aria-label')||'')));
 const progress=firstVisible({{progressSelectors}}); const body=(document.body?.innerText||'').replace(/\s+/g,' ').trim(); const lower=body.toLowerCase();
 const n=progress ? Number(progress.getAttribute('aria-valuenow')||progress.value||NaN) : NaN;
 const uploading=!!progress&&(!Number.isFinite(n)||n<100)||/uploading\s*\d*%|đang tải lên/.test(lower);
 const preview=!!document.querySelector('video,canvas[data-e2e*="cover"],[data-e2e*="video-preview"]');
 const complete=(Number.isFinite(n)&&n>=100)||/upload complete|uploaded successfully|tải lên hoàn tất|đã tải lên/.test(lower)||(preview&&!!description&&!uploading);
 const error=(lower.match(/(?:upload failed|couldn't upload|failed to upload|không thể tải lên|tải lên thất bại)[^.!\n]*/)||[])[0]||'';
 const filesLength=fileInputs.reduce((max,input)=>Math.max(max,input.files?.length||0),0);
 const filenameDetected=!!expectedFilename&&lower.includes(expectedFilename);
 const editorDetected=!!description||!!post;
 const uploadUiDetected=!!selectVideo||!!file||lower.includes('chọn video để tải lên')||lower.includes('select video to upload')||lower.includes('kéo và thả')||lower.includes('drag and drop')||!!document.querySelector('[data-e2e*="upload"],[class*="upload"]');
 const progressText=Number.isFinite(n)?String(n):(progress?norm(progress.innerText||progress.textContent||progress.getAttribute('aria-valuetext')||''):'');
 return {fileInput:!!file,selectVideoButton:!!selectVideo,uploadUiDetected,description:!!description,preview,uploading,uploadComplete:complete,uploadError:error,postExists:!!post,postEnabled:!!post&&!post.disabled&&post.getAttribute('aria-disabled')!=='true',filesLength,filenameDetected,editorDetected,progress:progressText,url:location.href,title:document.title||'',readyState:document.readyState||'',bodyText:body.slice(0,12000)};
})()
""", ct: ct);
        var value = ReadObject(response);
        return new UploadSnapshot(
            ReadBoolean(value, "fileInput"), ReadBoolean(value, "selectVideoButton"), ReadBoolean(value, "uploadUiDetected"),
            ReadBoolean(value, "description"), ReadBoolean(value, "preview"),
            ReadBoolean(value, "uploading"), ReadBoolean(value, "uploadComplete"), ReadString(value, "uploadError"),
            ReadBoolean(value, "postExists"), ReadBoolean(value, "postEnabled"), ReadInteger(value, "filesLength"),
            ReadBoolean(value, "filenameDetected"), ReadBoolean(value, "editorDetected"), ReadString(value, "progress"),
            ReadString(value, "url"), ReadString(value, "title"), ReadString(value, "readyState"), ReadString(value, "bodyText"));
    }

    async Task<string> ReadDescriptionAsync(CancellationToken ct)
    {
        var response = await _chrome.EvalAsync("""
(() => {
 const fold=s=>String(s||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').replace(/đ/g,'d').replace(/Đ/g,'D').replace(/\s+/g,' ').trim().toLowerCase();
 const semantic=/mo ta|description|caption|chu thich/,reject=/location|vi tri|search|tim kiem|schedule|comment/;
 let el=window.__ttVideoDescriptionEditor;
 if(!el?.isConnected){el=[...document.querySelectorAll('textarea,[contenteditable="true"],[role="textbox"],input[type="text"]')].filter(x=>!x.disabled).map(x=>{const attrs=fold(`${x.getAttribute('aria-label')||''} ${x.getAttribute('placeholder')||''} ${x.getAttribute('data-e2e')||''} ${x.name||''}`);const near=fold(`${x.closest('label')?.innerText||''} ${x.previousElementSibling?.innerText||''} ${x.parentElement?.previousElementSibling?.innerText||''}`);let score=(semantic.test(attrs)?220:0)+(semantic.test(near)?180:0)+(x.tagName==='TEXTAREA'?35:0)+(x.isContentEditable?25:0);if(reject.test(attrs+' '+near))score-=500;return {x,score}}).sort((a,b)=>b.score-a.score).find(v=>v.score>=120)?.x||null;if(el)window.__ttVideoDescriptionEditor=el;}
 return el ? String('value' in el ? el.value : el.innerText||el.textContent||'') : null;
})()
""", ct: ct);
        return response.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "\0";
    }

    static string NormalizeText(string value)
        => string.Join(' ', (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static string NormalizeDescription(string value)
        => (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace('\u00A0', ' ')
            .Replace("\u200B", "", StringComparison.Ordinal)
            .Replace("\u200C", "", StringComparison.Ordinal)
            .Replace("\u200D", "", StringComparison.Ordinal)
            .Replace("\uFEFF", "", StringComparison.Ordinal)
            .Trim();

    static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct, string error, bool throwOnTimeout = true)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await condition()) return true;
            await Task.Delay(400, ct);
        }
        if (throwOnTimeout) throw new TimeoutException(error);
        return false;
    }

    static bool ReadBool(JsonElement response)
        => response.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.True;
    static JsonElement ReadObject(JsonElement response)
        => response.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
    static string ReadString(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    static bool ReadBoolean(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False && item.GetBoolean();
    static int ReadInteger(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.TryGetInt32(out var number) ? number : 0;
    static double ReadDouble(JsonElement value, string property)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.TryGetDouble(out var number) ? number : 0;
}
