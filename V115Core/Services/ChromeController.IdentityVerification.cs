using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToolTikTokV11.Services;

public enum TikTokProfileIdentityUpdateStatus
{
    Success,
    PartialSuccess,
    SaveNotCommitted,
    Unverified,
    PreSaveValidationFailed,
    NameUnverifiedAfterSave,
    BioUnverified,
    AvatarUnverified,
    NameCooldown,
    AlreadyConfigured
}

public sealed partial class ChromeController
{
    sealed record TikTokProfileSnapshot(
        string DisplayName,
        string Bio,
        string AvatarSource,
        bool NameFound,
        bool BioFound,
        bool AvatarFound);

    sealed record TikTokIdentityEditorState(
        string DisplayName,
        string Bio,
        string AvatarSource,
        string NameReadSource,
        string BioReadSource,
        int NameCandidateCount,
        string NameCandidateDiagnostic,
        bool NameFound,
        bool BioFound,
        bool AvatarFound,
        bool CropOpen,
        bool AvatarPending,
        bool SaveReady,
        bool SaveVisible,
        bool SaveEnabled,
        bool EditorOpen,
        bool ProfileUpdatedToast);

    sealed record TikTokConfirmDialogState(
        bool Present,
        string Role,
        string Text,
        string Buttons,
        int ButtonCount,
        bool HasInput,
        bool HasTextarea,
        bool DialogVisible,
        bool EditorVisible,
        bool SaveVisible,
        bool NewOrChanged);

    sealed record TikTokSaveUiResult(bool SuccessToast, bool ErrorVisible, bool CooldownVisible, string Message);

    static string NormalizeTikTokIdentityText(string? value)
        => Regex.Replace((value ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim(), @"[\t\f\v ]+", " ");

    static string NormalizeTikTokAvatarSource(string? value)
    {
        var source = (value ?? "").Trim();
        if (source.Length == 0) return "";
        if (source.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return source;

        var path = Uri.TryCreate(source, UriKind.Absolute, out var uri)
            ? Uri.UnescapeDataString(uri.AbsolutePath)
            : source.Split('?', '#')[0];
        path = path.TrimEnd('/');

        // TikTok có thể đổi CDN shard p16/p19 và template resize mà object ảnh vẫn y hệt.
        // ID hex dài trong media path là fingerprint ổn định nhất khi có mặt.
        var objectId = Regex.Matches(path, @"(?i)(?<![a-f0-9])[a-f0-9]{24,}(?![a-f0-9])")
            .Select(match => match.Value)
            .OrderByDescending(value => value.Length)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(objectId))
            return "object:" + objectId.ToLowerInvariant();

        // Fallback không bao gồm hostname/query và bỏ phần ~tplv... chỉ mô tả transform.
        var templateMarker = path.IndexOf('~');
        if (templateMarker >= 0) path = path[..templateMarker];
        return "path:" + path.TrimEnd('/').ToLowerInvariant();
    }

    static string FormatTikTokNameInputDiagnostic(int candidateCount, string? candidatesJson)
    {
        static string Clean(string? value, int max = 160)
        {
            var text = Regex.Replace(value ?? "", @"\s+", " ").Trim();
            if (text.Length > max) text = text[..max] + "…";
            return text.Replace("|", "¦", StringComparison.Ordinal);
        }

        var parts = new List<string> { $"candidateCount={candidateCount}" };
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(candidatesJson) ? "[]" : candidatesJson);
            var index = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                index++;
                static string Property(JsonElement item, string name)
                    => item.TryGetProperty(name, out var value) ? value.ToString() : "";
                parts.Add(
                    $"candidate{index}.tag={Clean(Property(item, "tag"))} candidateType={Clean(Property(item, "candidateType"))} " +
                    $"type={Clean(Property(item, "type"))} name={Clean(Property(item, "name"))} " +
                    $"role={Clean(Property(item, "role"))} label={Clean(Property(item, "label"))} " +
                    $"placeholder={Clean(Property(item, "placeholder"))} ariaLabel={Clean(Property(item, "ariaLabel"))} " +
                    $"dataE2e={Clean(Property(item, "dataE2e"))} valueLength={Clean(Property(item, "valueLength"))} " +
                    $"valueMatchesCurrentDisplayName={Clean(Property(item, "valueMatchesCurrentDisplayName"))} " +
                    $"valueMatchesUsername={Clean(Property(item, "valueMatchesUsername"))} editable={Clean(Property(item, "editable"))} " +
                    $"visible={Clean(Property(item, "visible"))} disabled={Clean(Property(item, "disabled"))} readonly={Clean(Property(item, "readonly"))} " +
                    $"score={Clean(Property(item, "score"))} rejectReason={Clean(Property(item, "rejectReason"))}");
            }
        }
        catch (JsonException)
        {
            parts.Add("candidateDiagnostic=parse-failed");
        }
        return string.Join(" | ", parts);
    }

    async Task<TikTokProfileSnapshot> ReadTikTokProfileSnapshotAsync(
        string expectedHandle,
        CancellationToken ct)
    {
        var result = await EvalAsync($$"""
(() => {
  const wantedHandle = {{JsString(expectedHandle)}}.toLowerCase().replace(/^@/, '');
  const norm = s => (s || '').replace(/\s+/g, ' ').trim();
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };
  const firstText = selectors => {
    for (const selector of selectors) {
      const el = document.querySelector(selector);
      if (visible(el)) {
        const text = norm(el.innerText || el.textContent || '');
        if (text) return text;
      }
    }
    return '';
  };

  let displayName = firstText(['[data-e2e="user-title"]']);
  if (!displayName) {
    displayName = [...document.querySelectorAll('main h1,main h2,h1[data-e2e*="user"],h2[data-e2e*="user"]')]
      .filter(visible)
      .map(el => norm(el.innerText || el.textContent || ''))
      .find(text => text && text.length <= 100
        && (!wantedHandle || text.toLowerCase().replace(/^@/, '') !== wantedHandle)) || '';
  }

  const bio = firstText(['[data-e2e="user-bio"]','[data-e2e="profile-bio"]','[data-e2e*="bio"]']);
  const candidates = [
    document.querySelector('img[data-e2e="user-avatar"]'),
    document.querySelector('[data-e2e="user-avatar"] img'),
    document.querySelector('[data-e2e*="avatar"] img'),
    ...document.querySelectorAll('main img')
  ].filter((el, i, all) => el && all.indexOf(el) === i && visible(el));

  let avatar = null;
  let best = -1;
  for (const img of candidates) {
    const r = img.getBoundingClientRect();
    if (r.width < 42 || r.height < 42) continue;
    const meta = norm(`${img.currentSrc || img.src || ''} ${img.alt || ''} ${img.getAttribute('data-e2e') || ''}`).toLowerCase();
    const explicit = !!img.closest?.('[data-e2e*="avatar"]') || /avatar|profile photo|ảnh đại diện|ảnh hồ sơ/.test(meta);
    const headerLike = r.width <= 180 && r.height <= 180
      && Math.abs(r.width - r.height) <= Math.max(12, Math.min(r.width, r.height) * .22)
      && r.top >= 0 && r.top < 520;
    if (!explicit && !headerLike) continue;
    const score = Math.min(r.width, r.height) + (explicit ? 500 : 0) + (headerLike ? 100 : 0);
    if (score > best) { avatar = img; best = score; }
  }
  const avatarSource = avatar ? String(avatar.currentSrc || avatar.src || '') : '';
  return JSON.stringify({
    displayName, bio, avatarSource,
    nameFound: !!displayName,
    bioFound: !!document.querySelector('[data-e2e="user-bio"],[data-e2e="profile-bio"],[data-e2e*="bio"]') || !!bio,
    avatarFound: !!avatarSource
  });
})()
""", ct: ct);

        var json = result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "{}"
            : "{}";
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        return new TikTokProfileSnapshot(
            root.TryGetProperty("displayName", out var name) ? name.GetString() ?? "" : "",
            root.TryGetProperty("bio", out var bio) ? bio.GetString() ?? "" : "",
            root.TryGetProperty("avatarSource", out var avatar) ? avatar.GetString() ?? "" : "",
            root.TryGetProperty("nameFound", out var nf) && nf.ValueKind == JsonValueKind.True,
            root.TryGetProperty("bioFound", out var bf) && bf.ValueKind == JsonValueKind.True,
            root.TryGetProperty("avatarFound", out var af) && af.ValueKind == JsonValueKind.True);
    }

    async Task<TikTokIdentityEditorState> ReadTikTokIdentityEditorStateAsync(
        string expectedHandle,
        string currentDisplayName,
        CancellationToken ct,
        bool forceReacquire = false)
    {
        var result = await EvalAsync($$"""
(() => {
  const handle = {{JsString(expectedHandle)}}.toLowerCase().replace(/^@/, '');
  const forceReacquire = {{(forceReacquire ? "true" : "false")}};
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const currentDisplayName = norm({{JsString(currentDisplayName)}});
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };
  const dialogs = [...document.querySelectorAll('[role="dialog"],div[aria-modal="true"]')].filter(visible);
  const cropDialog = dialogs.find(d => {
    const t = norm(d.innerText || d.textContent || '');
    return (t.includes('chỉnh sửa ảnh') || t.includes('edit photo') || t.includes('edit image'))
      && (t.includes('thu phóng') || t.includes('zoom') || t.includes('hủy') || t.includes('cancel'));
  });
  const trackedName = window.__ttIdentityNameInput;
  const trackedBio = window.__ttIdentityBioInput;
  const trackedEditor = trackedName?.isConnected
    ? trackedName.closest?.('[role="dialog"],div[aria-modal="true"]')
    : trackedBio?.isConnected
      ? trackedBio.closest?.('[role="dialog"],div[aria-modal="true"]')
      : null;
  const editor = trackedEditor || dialogs.find(d => {
    if (d === cropDialog) return false;
    const t = norm(d.innerText || d.textContent || '');
    return (t.includes('sửa hồ sơ') || t.includes('chỉnh sửa hồ sơ') || t.includes('edit profile'))
      && !!d.querySelector('input,textarea');
  }) || (!cropDialog ? dialogs.at(-1) : null);
  const scope = editor || document;
  const nearby = el => {
    const parts = [];
    const add = value => {
      const text = norm(value);
      if (text && text.length <= 160 && !parts.includes(text)) parts.push(text);
    };
    try {
      if (el.id) {
        const escapedId = window.CSS?.escape ? CSS.escape(el.id) : el.id.replace(/["\\]/g, '\\$&');
        for (const label of document.querySelectorAll(`label[for="${escapedId}"]`))
          add(label.innerText || label.textContent || '');
      }
    } catch (_) {}
    add(el.closest?.('label')?.innerText || el.closest?.('label')?.textContent || '');
    const parent = el.parentElement;
    if (parent) {
      for (const child of [...parent.children]) {
        if (child === el || child.contains?.(el)) continue;
        add(child.innerText || child.textContent || '');
      }
    }
    let prev = parent?.previousElementSibling;
    for (let i = 0; i < 3 && prev; i++, prev = prev.previousElementSibling)
      add(prev.innerText || prev.textContent || '');
    // Chỉ dùng ancestor nhỏ. Không lấy toàn bộ dialog vì text Username/TikTok ID ở
    // field khác sẽ làm candidate Name hợp lệ bị loại nhầm.
    let ancestor = parent?.parentElement;
    for (let i = 0; i < 2 && ancestor && ancestor !== scope; i++, ancestor = ancestor.parentElement)
      add(ancestor.innerText || ancestor.textContent || '');
    return parts.join(' ');
  };
  const inputs = [...scope.querySelectorAll('input,[contenteditable="true"]')].filter(el => {
    const type = (el.type || '').toLowerCase();
    return visible(el) && !el.disabled && !el.readOnly && (el.getAttribute('contenteditable')==='true'||type === '' || type === 'text');
  });
  const readLiveValue = el => {
    if (!el) return { value: '', source: 'not-found' };
    try {
      if ('value' in el) return { value: String(el.value ?? ''), source: 'dom-property' };
    } catch (_) {}
    try {
      const attr = el.getAttribute('value');
      if (attr !== null) return { value: String(attr), source: 'value-attribute' };
    } catch (_) {}
    return { value: String(el.textContent || ''), source: 'text-content' };
  };
  const scoredNames = inputs.map(el => {
    const attrs = norm(`${el.name || ''} ${el.id || ''} ${el.placeholder || ''} ${el.getAttribute('aria-label') || ''} ${el.getAttribute('data-e2e') || ''}`);
    const near = nearby(el);
    const explicitLabels=[];
    try { if(el.id){const escaped=window.CSS?.escape?CSS.escape(el.id):el.id;for(const l of document.querySelectorAll(`label[for="${escaped}"]`))explicitLabels.push(norm(l.innerText||l.textContent||''))} } catch(_) {}
    explicitLabels.push(norm(el.closest?.('label')?.innerText||el.closest?.('label')?.textContent||''));
    const label=explicitLabels.filter(Boolean).join(' ');
    const negative=/username|user.?name|tiktok.?id|unique.?id|ten nguoi dung|tên người dùng/.test(attrs+' '+label);
    let score = 0;
    let source='';
    if(negative){score=-1000;source='REJECT_USERNAME_SEMANTIC'}
    else {
      if (/nickname|display.?name|edit.?name/.test(attrs)){score+=180;source='ATTRIBUTE'}
      if (/^(tên|name|biệt danh|nickname|display name)$/.test(label)){score+=240;source='LABEL_FOR'}
      if (/^tên$|^name$|biệt danh|nickname|display name/.test(near)){score+=100;if(!source)source='FIELD_CONTAINER'}
      if (/(^|\s)tên(\s|$)|(^|\s)name(\s|$)/.test(near))score+=40;
    }
    return [el, score, source, label, negative];
  }).sort((a,b) => b[1] - a[1]);
  const validTrackedName = !forceReacquire && trackedName?.isConnected && inputs.includes(trackedName) ? trackedName : null;
  const nameInput = validTrackedName
    || scoredNames.find(x => x[1] >= 120 && !x[4])?.[0]
    || null;
  const selectedMeta=scoredNames.find(x=>x[0]===nameInput)||null;
  if (nameInput) {window.__ttIdentityNameInput = nameInput;window.__ttIdentityNameConfidence=selectedMeta&&selectedMeta[1]>=220?'HIGH':'MEDIUM';window.__ttIdentityNameSource=validTrackedName?'TRACKED_EXACT_ELEMENT':selectedMeta?.[2]||'SEMANTIC'}
  const nameRead = readLiveValue(nameInput);
  const allFields=[...scope.querySelectorAll('input,textarea,[contenteditable="true"]')];
  const candidateDiagnostics = allFields.slice(0, 12).map((el,index) => {
    const live = readLiveValue(el);
    const scored=scoredNames.find(x=>x[0]===el);const attrs=norm(`${el.name||''} ${el.id||''} ${el.placeholder||''} ${el.getAttribute('aria-label')||''} ${el.getAttribute('data-e2e')||''}`);const near=nearby(el);const semantic=attrs+' '+near;
    const usernameSemantic=/username|user.?name|tiktok.?id|unique.?id|tên người dùng/.test(semantic);const bioSemantic=/bio|biography|tiểu sử/.test(semantic);const candidateType=usernameSemantic?'USERNAME':bioSemantic?'BIO':scored&&scored[1]>=120?'NAME':'UNKNOWN';
    const label=scored?.[3]||near.slice(0,160);const value=norm(live.value).replace(/^@/,'');
    return {
      index,tag: el.tagName || '', type: el.type || '', name: el.name || '',role:el.getAttribute('role')||'',label,
      placeholder: el.placeholder || '', ariaLabel: el.getAttribute('aria-label') || '',
      dataE2e: el.getAttribute('data-e2e') || '',valueLength:String(live.value||'').length,
      valueMatchesCurrentDisplayName:!!currentDisplayName&&value===currentDisplayName,valueMatchesUsername:!!handle&&value===handle,
      editable:el.getAttribute('contenteditable')==='true'||('value'in el),visible:visible(el),disabled:!!el.disabled,readonly:!!el.readOnly,
      candidateType,score:scored?.[1]||0,rejectReason:usernameSemantic?'USERNAME_SEMANTIC':(!visible(el)?'NOT_VISIBLE':(el.disabled?'DISABLED':(el.readOnly?'READONLY':candidateType==='NAME'?'':'INSUFFICIENT_NAME_SEMANTIC')))
    };
  });

  const areas = [...scope.querySelectorAll('textarea')].filter(el => visible(el) && !el.disabled && !el.readOnly);
  const scoredBios = areas.map(el => {
    const meta = norm(`${el.name || ''} ${el.id || ''} ${el.placeholder || ''} ${el.getAttribute('aria-label') || ''} ${el.getAttribute('data-e2e') || ''} ${nearby(el)}`);
    return [el, /bio|biography|signature|tiểu sử/.test(meta) ? 100 : 0];
  }).sort((a,b) => b[1] - a[1]);
  const validTrackedBio = !forceReacquire && trackedBio?.isConnected && areas.includes(trackedBio) ? trackedBio : null;
  const bioArea = validTrackedBio || scoredBios.find(x => x[1] > 0)?.[0] || (areas.length === 1 ? areas[0] : null);
  if (bioArea) window.__ttIdentityBioInput = bioArea;
  const bioRead = readLiveValue(bioArea);

  const images = [...scope.querySelectorAll('img')].filter(visible);
  const avatar = images.map(img => {
    const r = img.getBoundingClientRect();
    const meta = norm(`${img.currentSrc || img.src || ''} ${img.alt || ''} ${img.getAttribute('data-e2e') || ''} ${nearby(img)}`);
    let score = Math.min(r.width, r.height);
    if (/avatar|profile photo|ảnh đại diện|ảnh hồ sơ|thay ảnh|change photo/.test(meta)) score += 400;
    if (r.width < 40 || r.height < 40) score -= 500;
    return [img, score];
  }).sort((a,b) => b[1] - a[1])[0]?.[0] || null;

  const save = [...scope.querySelectorAll('button,[role="button"]')].filter(visible).find(b => {
    const t = norm(`${b.innerText || b.textContent || ''} ${b.getAttribute('aria-label') || ''}`);
    return t === 'lưu' || t === 'save' || t === 'save changes' || t === 'lưu thay đổi';
  });
  const avatarRegion = avatar?.parentElement?.parentElement || scope;
  const avatarPending = !!cropDialog
    || avatarRegion.getAttribute?.('aria-busy') === 'true'
    || !!avatarRegion.querySelector?.('[role="progressbar"],[aria-busy="true"],[data-e2e*="loading"],[class*="loading"]');
  const pageText = norm(document.body?.innerText || '');
  return JSON.stringify({
    displayName: nameRead.value,
    bio: bioRead.value,
    avatarSource: avatar ? String(avatar.currentSrc || avatar.src || '') : '',
    nameReadSource: nameRead.source,
    bioReadSource: bioRead.source,
    nameCandidateCount: inputs.length,
    nameCandidateDiagnostic: JSON.stringify(candidateDiagnostics),
    nameFound: !!nameInput,
    bioFound: !!bioArea,
    avatarFound: !!avatar,
    cropOpen: !!cropDialog,
    avatarPending,
    saveReady: !!save && !save.disabled && save.getAttribute('aria-disabled') !== 'true',
    saveVisible: !!save && visible(save),
    saveEnabled: !!save && !save.disabled && save.getAttribute('aria-disabled') !== 'true',
    editorOpen: !!editor,
    profileUpdatedToast: pageText.includes('profile updated') || pageText.includes('hồ sơ đã cập nhật')
  });
})()
""", ct: ct);

        var json = result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "{}"
            : "{}";
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        static bool Flag(JsonElement root, string name)
            => root.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
        return new TikTokIdentityEditorState(
            root.TryGetProperty("displayName", out var name) ? name.GetString() ?? "" : "",
            root.TryGetProperty("bio", out var bio) ? bio.GetString() ?? "" : "",
            root.TryGetProperty("avatarSource", out var avatar) ? avatar.GetString() ?? "" : "",
            root.TryGetProperty("nameReadSource", out var nameSource) ? nameSource.GetString() ?? "" : "",
            root.TryGetProperty("bioReadSource", out var bioSource) ? bioSource.GetString() ?? "" : "",
            root.TryGetProperty("nameCandidateCount", out var candidateCount) && candidateCount.TryGetInt32(out var count) ? count : 0,
            root.TryGetProperty("nameCandidateDiagnostic", out var candidateDiagnostic) ? candidateDiagnostic.GetString() ?? "[]" : "[]",
            Flag(root, "nameFound"), Flag(root, "bioFound"), Flag(root, "avatarFound"),
            Flag(root, "cropOpen"), Flag(root, "avatarPending"), Flag(root, "saveReady"),
            Flag(root, "saveVisible"), Flag(root, "saveEnabled"),
            Flag(root, "editorOpen"), Flag(root, "profileUpdatedToast"));
    }

    async Task<TikTokConfirmDialogState> ReadTikTokConfirmDialogStateAsync(CancellationToken ct)
    {
        var result = await EvalAsync("""
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };
  const dialogs = [...document.querySelectorAll('[role="dialog"],div[aria-modal="true"]')].filter(visible);
  const baseline = window.__ttIdentityPreSaveDialogs instanceof Map
    ? window.__ttIdentityPreSaveDialogs
    : new Map();
  let inspected = dialogs.at(-1) || null;
  let match = null;
  let changed = false;
  for (const dialog of dialogs) {
    const text = norm(dialog.innerText || dialog.textContent || '');
    const previousText = baseline.get(dialog);
    const newOrChanged = previousText === undefined || previousText !== text;
    const hasInput = !!dialog.querySelector('input');
    const hasTextarea = !!dialog.querySelector('textarea');
    const explicitNickname = text.includes('đặt biệt danh')
      || text.includes('biệt danh 7 ngày') || text.includes('7 ngày 1 lần')
      || text.includes('set nickname') || text.includes('nickname');
    const explicitSaveConfirm = (text.includes('lưu hồ sơ') || text.includes('save profile'))
      && (text.includes('bạn có chắc') || text.includes('are you sure')
        || text.includes('xác nhận') || text.includes('confirm'));
    const buttons = [...dialog.querySelectorAll('button,[role="button"]')].filter(visible);
    const confirmButton = buttons.find(button => {
      if (button.disabled || button.getAttribute('aria-disabled') === 'true') return false;
      const label = norm(`${button.innerText || button.textContent || ''} ${button.getAttribute('aria-label') || ''}`);
      return label === 'xác nhận' || label === 'confirm' || label === 'lưu' || label === 'save';
    });
    if (newOrChanged && !hasInput && !hasTextarea
        && (explicitNickname || explicitSaveConfirm) && confirmButton) {
      match = dialog;
      inspected = dialog;
      changed = newOrChanged;
      window.__ttIdentityConfirmDialog = dialog;
      window.__ttIdentityConfirmButton = confirmButton;
      break;
    }
  }
  const text = inspected ? norm(inspected.innerText || inspected.textContent || '').slice(0, 300) : '';
  const editorInput = window.__ttIdentityNameInput?.isConnected
    ? window.__ttIdentityNameInput
    : window.__ttIdentityBioInput?.isConnected ? window.__ttIdentityBioInput : null;
  const save = window.__ttIdentitySaveButton;
  return JSON.stringify({
    present: !!match,
    role: inspected?.getAttribute('role') || (inspected?.getAttribute('aria-modal') === 'true' ? 'aria-modal' : ''),
    text,
    buttons: inspected ? JSON.stringify([...inspected.querySelectorAll('button,[role="button"]')].filter(visible)
      .map(button => norm(`${button.innerText || button.textContent || ''} ${button.getAttribute('aria-label') || ''}`)).slice(0, 12)) : '[]',
    buttonCount: inspected ? [...inspected.querySelectorAll('button,[role="button"]')].filter(visible).length : 0,
    hasInput: !!inspected?.querySelector('input'),
    hasTextarea: !!inspected?.querySelector('textarea'),
    dialogVisible: !!match && visible(match),
    editorVisible: !!editorInput && visible(editorInput),
    saveVisible: !!save && save.isConnected && visible(save),
    newOrChanged: changed
  });
})()
""", ct: ct);
        var json = result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "{}"
            : "{}";
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        static bool Flag(JsonElement root, string name)
            => root.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
        return new TikTokConfirmDialogState(
            Flag(root, "present"),
            root.TryGetProperty("role", out var role) ? role.GetString() ?? "" : "",
            root.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
            root.TryGetProperty("buttons", out var buttons) ? buttons.GetString() ?? "[]" : "[]",
            root.TryGetProperty("buttonCount", out var count) && count.TryGetInt32(out var buttonCount) ? buttonCount : 0,
            Flag(root, "hasInput"), Flag(root, "hasTextarea"), Flag(root, "dialogVisible"),
            Flag(root, "editorVisible"), Flag(root, "saveVisible"), Flag(root, "newOrChanged"));
    }

    async Task<TikTokSaveUiResult> ReadTikTokSaveUiResultAsync(CancellationToken ct)
    {
        var result = await EvalAsync("""
(() => {
  const norm = s => (s || '').replace(/\s+/g, ' ').trim();
  const fold = s => norm(s).normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase();
  const visible = el => {
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const cs = getComputedStyle(el);
    return r.width > 2 && r.height > 2 && cs.display !== 'none' && cs.visibility !== 'hidden';
  };
  const candidates = [...document.querySelectorAll(
    '[role="alert"],[aria-live="assertive"],[aria-live="polite"],[data-e2e*="toast"],[data-e2e*="error"],[class*="toast"],[class*="error"]'
  )].filter(visible).map(el => norm(el.innerText || el.textContent || '')).filter(Boolean);
  const success = candidates.find(text => /profile updated|hồ sơ đã cập nhật/i.test(text)) || '';
  const error = candidates.find(text => {
    const f = fold(text);
    return /error|failed|invalid|unable|try again|too many/.test(f)
      || /loi|that bai|khong hop le|khong the|thu lai|qua nhieu/.test(f)
      || /7 ngay|cooldown|change your nickname again/.test(f);
  }) || '';
  const errorFolded = fold(error);
  return JSON.stringify({
    successToast: !!success,
    errorVisible: !!error,
    cooldownVisible: /7 ngay|cooldown|change your nickname again/.test(errorFolded),
    message: String(error || success || '').slice(0, 300)
  });
})()
""", ct: ct);
        var json = result.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "{}"
            : "{}";
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        static bool Flag(JsonElement root, string name)
            => root.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
        return new TikTokSaveUiResult(
            Flag(root, "successToast"), Flag(root, "errorVisible"), Flag(root, "cooldownVisible"),
            root.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "");
    }
}
