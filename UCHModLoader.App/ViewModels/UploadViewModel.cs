using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UCHModLoader.App.Services;
using UCHModLoader.Core.Models;
using UCHModLoader.Core.Services;

namespace UCHModLoader.App.ViewModels;

public partial class MyModInfo : ObservableObject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool IsPrivate { get; set; }
    public int PendingCount { get; set; }
    public bool HasPending => PendingCount > 0;

    [ObservableProperty] private Bitmap? _icon;
    [ObservableProperty] private bool _isSelected;
}

public sealed class PendingItemViewModel
{
    public string ModId { get; init; } = "";
    public string ModName { get; init; } = "";
    public string Author { get; init; } = "";
    public string OwnerDiscordId { get; init; } = "";
    public string Version { get; init; } = "";
    public int Revision { get; init; }
    public string Changelog { get; init; } = "";
    public bool HasChangelog => Changelog.Length > 0;
    public string UploadedDisplay { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Display => $"{ModName} {Version} (r{Revision}) — by {Author}";
}

public partial class TagOptionViewModel : ObservableObject
{
    public string Name { get; }
    [ObservableProperty] private bool _isSelected;
    public TagOptionViewModel(string name) => Name = name;
}

public partial class PackModOptionViewModel : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    [ObservableProperty] private bool _isSelected;
}

public partial class UploadViewModel : ObservableObject
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);
    private readonly AppSettings _settings;
    private readonly IconCache _iconCache = new();

    /// <summary>Raised after a successful upload or details change so the app can refresh mod lists.</summary>
    public event EventHandler? UploadSucceeded;

    [ObservableProperty] private string _serverUrl;
    [ObservableProperty] private string _apiToken;
    [ObservableProperty] private string _modName = "";
    [ObservableProperty] private string _modFilePath = "";
    [ObservableProperty] private string _iconFilePath = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _uploadPreview = "";
    [ObservableProperty] private bool _isUploading;
    [ObservableProperty] private string _dependencyConstraint = ">=1.0.0";
    [ObservableProperty] private ModEntry? _selectedDependency;

    [ObservableProperty] private MyModInfo? _selectedMyMod;
    [ObservableProperty] private string _detailsName = "";
    [ObservableProperty] private string _detailsDescription = "";
    [ObservableProperty] private string _detailsIconPath = "";
    [ObservableProperty] private string _updateModFilePath = "";
    [ObservableProperty] private string _updatePreview = "";
    [ObservableProperty] private string _changelog = "";
    [ObservableProperty] private string _updateChangelog = "";
    [ObservableProperty] private string _currentUsername = "";
    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private bool _isPrivateMod;
    [ObservableProperty] private string _generatedKey = "";
    [ObservableProperty] private string _packName = "";
    [ObservableProperty] private string _packDescription = "";
    [ObservableProperty] private string _packIconPath = "";
    [ObservableProperty] private string _packModSearchText = "";
    [ObservableProperty] private bool _isPackPickerOpen;
    [ObservableProperty] private string _selectedSection = "Upload";

    public ObservableCollection<ModEntry> IndexMods { get; } = new();
    public ObservableCollection<string> Dependencies { get; } = new();
    public ObservableCollection<MyModInfo> MyMods { get; } = new();
    public ObservableCollection<TagOptionViewModel> UploadTags { get; } =
        new(ModTags.All.Select(t => new TagOptionViewModel(t)));
    public ObservableCollection<TagOptionViewModel> DetailsTags { get; } =
        new(ModTags.All.Select(t => new TagOptionViewModel(t)));
    public ObservableCollection<PendingItemViewModel> PendingItems { get; } = new();
    public bool HasPendingItems => PendingItems.Count > 0;
    public ObservableCollection<PackModOptionViewModel> PackMods { get; } = new();
    public ObservableCollection<PackModOptionViewModel> FilteredPackMods { get; } = new();

    public string SelectedPackModsDisplay
    {
        get
        {
            var count = PackMods.Count(m => m.IsSelected);
            return count == 0 ? "No mods selected"
                 : count == 1 ? "1 mod selected"
                 : $"{count} mods selected";
        }
    }

    public bool IsLoggedIn => !string.IsNullOrWhiteSpace(ApiToken);
    public bool HasMyMods => MyMods.Count > 0;
    public bool SelectedMyModIsPrivate => SelectedMyMod?.IsPrivate ?? false;

    public bool IsUploadSection => SelectedSection == "Upload";
    public bool IsMyModsSection => SelectedSection == "MyMods";
    public bool IsPacksSection => SelectedSection == "Packs";
    public bool IsReviewSection => SelectedSection == "Review";

    partial void OnSelectedSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsUploadSection));
        OnPropertyChanged(nameof(IsMyModsSection));
        OnPropertyChanged(nameof(IsPacksSection));
        OnPropertyChanged(nameof(IsReviewSection));
        if (value == "Review") _ = LoadPendingAsync();
    }

    [RelayCommand]
    private void SelectSection(string section)
    {
        // Packs and Review are admin-only; never land there without the flag.
        if ((section == "Packs" || section == "Review") && !IsAdmin) return;
        SelectedSection = section;
    }

    private async Task LoadPendingAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/admin/pending");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            PendingItems.Clear();
            foreach (var item in json.RootElement.EnumerateArray())
            {
                PendingItems.Add(new PendingItemViewModel
                {
                    ModId = item.GetProperty("modId").GetString() ?? "",
                    ModName = item.GetProperty("name").GetString() ?? "",
                    Author = item.GetProperty("author").GetString() ?? "",
                    OwnerDiscordId = item.GetProperty("ownerDiscordId").GetString() ?? "",
                    Version = item.GetProperty("version").GetString() ?? "",
                    Revision = item.GetProperty("revision").GetInt32(),
                    Changelog = item.GetProperty("changelog").GetString() ?? "",
                    UploadedDisplay = item.TryGetProperty("uploadedUtc", out var d) &&
                                      d.TryGetDateTime(out var dt)
                        ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "",
                    DownloadUrl = item.GetProperty("downloadUrl").GetString() ?? "",
                });
            }
            OnPropertyChanged(nameof(HasPendingItems));
        }
        catch { }
    }

    private async Task ReviewActionAsync(string endpoint, PendingItemViewModel item, bool verifyAuthor = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/admin/{endpoint}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["modId"] = item.ModId,
                ["revision"] = item.Revision.ToString(),
                ["verifyAuthor"] = verifyAuthor ? "true" : "false",
            });

            var response = await Http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                Status = endpoint == "reject"
                    ? $"Rejected {item.Display}"
                    : verifyAuthor
                        ? $"Approved {item.Display} and verified {item.Author}"
                        : $"Approved {item.Display}";
                await LoadPendingAsync();
                UploadSucceeded?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Status = $"Review action failed: {await response.Content.ReadAsStringAsync()}";
            }
        }
        catch (Exception ex)
        {
            Status = $"Review action failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task ApprovePending(PendingItemViewModel item) => ReviewActionAsync("approve", item);

    [RelayCommand]
    private Task ApproveAndVerify(PendingItemViewModel item) => ReviewActionAsync("approve", item, verifyAuthor: true);

    [RelayCommand]
    private Task RejectPending(PendingItemViewModel item) => ReviewActionAsync("reject", item);

    [RelayCommand]
    private async Task DownloadPendingAsync(PendingItemViewModel item)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, item.DownloadUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) { Status = "Download failed."; return; }

            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            var path = Path.Combine(downloads, $"{item.ModId}-r{item.Revision}.zip");
            await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync());
            Status = $"Saved to {path} for inspection.";
        }
        catch (Exception ex)
        {
            Status = $"Download failed: {ex.Message}";
        }
    }

    partial void OnApiTokenChanged(string value)
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        if (IsLoggedIn)
        {
            _ = LoadMyModsAsync();
            _ = LoadMeAsync();
        }
        else
        {
            MyMods.Clear();
            OnPropertyChanged(nameof(HasMyMods));
            CurrentUsername = "";
            Avatar = null;
            IsAdmin = false;
            if (SelectedSection == "Packs") SelectedSection = "Upload";
        }
    }

    partial void OnPackModSearchTextChanged(string value) => FilterPackMods();

    private void FilterPackMods()
    {
        FilteredPackMods.Clear();
        IEnumerable<PackModOptionViewModel> mods = PackMods;
        if (!string.IsNullOrWhiteSpace(PackModSearchText))
        {
            var query = PackModSearchText.Trim();
            mods = mods.Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var mod in mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            FilteredPackMods.Add(mod);
    }

    private async Task LoadMeAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/auth/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            CurrentUsername = json.RootElement.GetProperty("username").GetString() ?? "";
            IsAdmin = json.RootElement.TryGetProperty("isAdmin", out var admin) && admin.GetBoolean();
            var avatarUrl = json.RootElement.GetProperty("avatarUrl").GetString();

            if (!string.IsNullOrEmpty(avatarUrl))
            {
                var bytes = await Http.GetByteArrayAsync(avatarUrl);
                Avatar = new Bitmap(new MemoryStream(bytes));
            }
        }
        catch { /* identity display is decoration; failures are non-fatal */ }
    }

    partial void OnModFilePathChanged(string value) => UpdateUploadPreview();

    partial void OnModNameChanged(string value) => UpdateUploadPreview();

    partial void OnUpdateModFilePathChanged(string value) => UpdateUpdatePreview();

    partial void OnSelectedMyModChanged(MyModInfo? value)
    {
        foreach (var mod in MyMods) mod.IsSelected = ReferenceEquals(mod, value);
        OnPropertyChanged(nameof(SelectedMyModIsPrivate));
        GeneratedKey = "";
        DetailsName = value?.Name ?? "";
        DetailsDescription = value?.Description ?? "";
        foreach (var tag in DetailsTags)
            tag.IsSelected = value?.Tags.Contains(tag.Name, StringComparer.OrdinalIgnoreCase) ?? false;
        UpdateUpdatePreview();
    }

    public UploadViewModel(AppSettings settings)
    {
        _settings = settings;
        _serverUrl = string.IsNullOrWhiteSpace(settings.ServerUrl)
            ? AppSettings.DefaultServerUrl
            : settings.ServerUrl;
        _apiToken = settings.ApiToken;
        if (IsLoggedIn)
        {
            _ = LoadMyModsAsync();
            _ = LoadMeAsync();
        }
    }

    public string BaseUrl => string.IsNullOrWhiteSpace(ServerUrl)
        ? AppSettings.DefaultServerUrl
        : ServerUrl.TrimEnd('/');

    public void SetIndex(ModIndex index)
    {
        IndexMods.Clear();
        foreach (var mod in index.Mods) IndexMods.Add(mod);

        // Rebuild the pack mod picker, preserving current selection.
        var selected = new HashSet<string>(
            PackMods.Where(m => m.IsSelected).Select(m => m.Id),
            StringComparer.OrdinalIgnoreCase);
        PackMods.Clear();
        foreach (var mod in index.Mods)
        {
            var option = new PackModOptionViewModel
            {
                Id = mod.Id,
                Name = mod.Name,
                IsSelected = selected.Contains(mod.Id),
            };
            option.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PackModOptionViewModel.IsSelected))
                    OnPropertyChanged(nameof(SelectedPackModsDisplay));
            };
            PackMods.Add(option);
        }
        FilterPackMods();
        OnPropertyChanged(nameof(SelectedPackModsDisplay));

        _ = LoadMyModIconsAsync();
        UpdateUploadPreview();
    }

    private static PluginInfo? InspectFile(string path)
    {
        try
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(path);
                foreach (var entry in zip.Entries.Where(e =>
                    e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    using var ms = new MemoryStream();
                    entry.Open().CopyTo(ms);
                    ms.Position = 0;
                    var info = DllInspector.Inspect(ms);
                    if (info is not null) return info;
                }
                return null;
            }

            using var fs = File.OpenRead(path);
            return DllInspector.Inspect(fs);
        }
        catch
        {
            return null;
        }
    }

    private void UpdateUploadPreview()
    {
        UploadPreview = "";
        if (!File.Exists(ModFilePath)) return;

        var info = InspectFile(ModFilePath);
        if (info is null)
        {
            UploadPreview = "⚠ No BepInEx plugin metadata found in this file — the server will reject it.";
            return;
        }

        // Prefill the name field from the DLL as a suggestion, without
        // overwriting anything the creator has already typed.
        if (string.IsNullOrWhiteSpace(ModName) && !string.IsNullOrWhiteSpace(info.Name))
            ModName = info.Name;

        var existing = IndexMods.FirstOrDefault(m =>
            string.Equals(m.Id, info.Guid, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            var shownName = string.IsNullOrWhiteSpace(ModName) ? info.Name : ModName.Trim();
            UploadPreview = $"New mod: {shownName} {info.Version}";
            return;
        }

        var isMine = MyMods.Any(m => string.Equals(m.Id, info.Guid, StringComparison.OrdinalIgnoreCase));
        if (!isMine && IsLoggedIn)
        {
            UploadPreview = $"⚠ {existing.Name} is owned by another user — this upload will be rejected.";
            return;
        }

        var current = existing.Latest()?.Version ?? "0.0.0";
        UploadPreview = string.Equals(current, info.Version, StringComparison.OrdinalIgnoreCase)
            ? $"This will update {existing.Name} (still {info.Version} — players will receive the new build)"
            : $"This will update {existing.Name}: {current} → {info.Version}";
    }

    private void UpdateUpdatePreview()
    {
        UpdatePreview = "";
        if (SelectedMyMod is null || !File.Exists(UpdateModFilePath)) return;

        var info = InspectFile(UpdateModFilePath);
        if (info is null)
        {
            UpdatePreview = "⚠ No BepInEx plugin metadata found in this file.";
            return;
        }

        if (!string.Equals(info.Guid, SelectedMyMod.Id, StringComparison.OrdinalIgnoreCase))
        {
            UpdatePreview = $"⚠ This file is '{info.Name}' ({info.Guid}), not {SelectedMyMod.Name}.";
            return;
        }

        UpdatePreview = string.Equals(SelectedMyMod.Version, info.Version, StringComparison.OrdinalIgnoreCase)
            ? $"This will update {SelectedMyMod.Name} (still {info.Version} — players will receive the new build)"
            : $"This will update {SelectedMyMod.Name}: {SelectedMyMod.Version} → {info.Version}";
    }

    private async Task LoadMyModsAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/mods/mine");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;

            var mods = JsonSerializer.Deserialize<List<MyModInfo>>(
                await response.Content.ReadAsStringAsync(), JsonWeb) ?? new List<MyModInfo>();

            MyMods.Clear();
            foreach (var mod in mods) MyMods.Add(mod);
            OnPropertyChanged(nameof(HasMyMods));
            _ = LoadMyModIconsAsync();
        }
        catch { }
    }

    private async Task LoadMyModIconsAsync()
    {
        foreach (var mod in MyMods.Where(m => m.Icon is null).ToList())
        {
            var entry = IndexMods.FirstOrDefault(e =>
                string.Equals(e.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
            if (entry is null || string.IsNullOrEmpty(entry.IconUrl)) continue;
            mod.Icon = await _iconCache.GetAsync(mod.Id, entry.IconVersion, entry.IconUrl);
        }
    }

    [RelayCommand]
    private void SelectMyMod(MyModInfo mod) => SelectedMyMod = mod;

    [RelayCommand]
    private async Task LoginWithDiscordAsync()
    {
        var port = GetFreeLoopbackPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Status = $"Could not start local login listener: {ex.Message}";
            return;
        }

        UrlOpener.Open($"{BaseUrl}/api/auth/discord/login?port={port}");
        Status = "Complete the login in your browser…";

        var contextTask = listener.GetContextAsync();
        var finished = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(2)));
        if (finished != contextTask)
        {
            Status = "Login timed out. Try again.";
            return;
        }

        var context = contextTask.Result;
        var token = context.Request.QueryString["token"];

        var page = "<html><body style='font-family:sans-serif;margin:80px auto;max-width:420px'>" +
                   "<h2>Logged in</h2><p>You can close this tab and return to the loader.</p></body></html>";
        var bytes = System.Text.Encoding.UTF8.GetBytes(page);
        context.Response.ContentType = "text/html";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();

        if (string.IsNullOrWhiteSpace(token))
        {
            Status = "Login completed but no token was received. Try again.";
            return;
        }

        ApiToken = token;
        _settings.ServerUrl = BaseUrl;
        _settings.ApiToken = token;
        _settings.Save();
        Status = "Logged in — token saved.";
    }

    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [RelayCommand]
    private void LogOut()
    {
        ApiToken = "";
        _settings.ApiToken = "";
        _settings.Save();
        Status = "Logged out.";
    }

    [RelayCommand]
    private void AddDependency()
    {
        if (SelectedDependency is null) return;
        var entry = $"{SelectedDependency.Id}|{DependencyConstraint.Trim()}";
        if (!Dependencies.Contains(entry)) Dependencies.Add(entry);
    }

    [RelayCommand]
    private void RemoveDependency(string entry) => Dependencies.Remove(entry);

    [RelayCommand]
    private async Task UploadAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiToken)) { Status = "Login with Discord first."; return; }
        if (!File.Exists(ModFilePath)) { Status = "Choose a .dll or .zip mod file."; return; }

        var nameError = ModNaming.Validate(ModName);
        if (nameError is not null) { Status = nameError; return; }

        try
        {
            IsUploading = true;
            Status = "Uploading…";

            var deps = Dependencies
                .Select(d => d.Split('|', 2))
                .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? parts[1] : "*");

            using var form = new MultipartFormDataContent();
            var modContent = new ByteArrayContent(await File.ReadAllBytesAsync(ModFilePath));
            modContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(modContent, "modFile", Path.GetFileName(ModFilePath));

            if (File.Exists(IconFilePath))
            {
                var iconContent = new ByteArrayContent(await File.ReadAllBytesAsync(IconFilePath));
                iconContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(iconContent, "icon", Path.GetFileName(IconFilePath));
            }

            var selectedTags = UploadTags.Where(t => t.IsSelected).Select(t => t.Name).ToList();
            if (selectedTags.Count > ModTags.MaxTagsPerMod)
            {
                Status = $"Pick at most {ModTags.MaxTagsPerMod} tags.";
                return;
            }

            form.Add(new StringContent(ModName.Trim()), "name");
            form.Add(new StringContent(Description), "description");
            form.Add(new StringContent(JsonSerializer.Serialize(deps)), "dependencies");
            form.Add(new StringContent(JsonSerializer.Serialize(selectedTags)), "tags");
            form.Add(new StringContent(IsPrivateMod ? "true" : "false"), "isPrivate");
            form.Add(new StringContent(Changelog), "changelog");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/mods/upload");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            request.Content = form;

            var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                Status = body.Contains("\"pending\":true")
                    ? "Uploaded — submitted for review. It will appear in Browse once approved."
                    : $"Uploaded! {body}";
                ModName = "";
                ModFilePath = "";
                IconFilePath = "";
                Description = "";
                Dependencies.Clear();
                foreach (var tag in UploadTags) tag.IsSelected = false;
                IsPrivateMod = false;
                Changelog = "";
                await LoadMyModsAsync();
                UploadSucceeded?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Status = $"Upload rejected: {body}";
            }
        }
        catch (Exception ex)
        {
            Status = $"Upload failed: {ex.Message}";
        }
        finally
        {
            IsUploading = false;
        }
    }

    [RelayCommand]
    private async Task UpdateModAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiToken)) { Status = "Login with Discord first."; return; }
        if (SelectedMyMod is null) { Status = "Select one of your mods first."; return; }
        if (!File.Exists(UpdateModFilePath)) { Status = "Choose the new .dll or .zip for this mod."; return; }

        var info = InspectFile(UpdateModFilePath);
        if (info is null)
        {
            Status = "No BepInEx plugin metadata found in this file.";
            return;
        }
        if (!string.Equals(info.Guid, SelectedMyMod.Id, StringComparison.OrdinalIgnoreCase))
        {
            Status = $"This file is '{info.Name}' ({info.Guid}), not {SelectedMyMod.Name}. Update cancelled.";
            return;
        }

        try
        {
            IsUploading = true;
            Status = $"Updating {SelectedMyMod.Name}…";

            using var form = new MultipartFormDataContent();
            var modContent = new ByteArrayContent(await File.ReadAllBytesAsync(UpdateModFilePath));
            modContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(modContent, "modFile", Path.GetFileName(UpdateModFilePath));
            form.Add(new StringContent(UpdateChangelog), "changelog");
            // Deliberately no "name", "dependencies", or "description" fields:
            // the server keeps the existing values for all three.

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/mods/upload");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            request.Content = form;

            var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                Status = body.Contains("\"pending\":true")
                    ? "Update submitted for review — players stay on the current version until it's approved."
                    : $"Updated! {body}";
                UpdateModFilePath = "";
                UpdateChangelog = "";
                await LoadMyModsAsync();
                UploadSucceeded?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Status = $"Update rejected: {body}";
            }
        }
        catch (Exception ex)
        {
            Status = $"Update failed: {ex.Message}";
        }
        finally
        {
            IsUploading = false;
        }
    }

    [RelayCommand]
    private async Task GenerateKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiToken)) { Status = "Login with Discord first."; return; }
        if (SelectedMyMod is null) { Status = "Select one of your mods first."; return; }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{BaseUrl}/api/mods/{Uri.EscapeDataString(SelectedMyMod.Id)}/generatekey");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Status = $"Key generation rejected: {body}";
                return;
            }

            using var json = JsonDocument.Parse(body);
            GeneratedKey = json.RootElement.GetProperty("key").GetString() ?? "";
            Status = "Key generated — share it with one person. It can be used once.";
        }
        catch (Exception ex)
        {
            Status = $"Key generation failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void TogglePackPicker() => IsPackPickerOpen = !IsPackPickerOpen;

    [RelayCommand]
    private async Task CreatePackAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiToken)) { Status = "Login with Discord first."; return; }

        var name = PackName.Trim();
        if (name.Length == 0) { Status = "Pack name is required."; return; }
        if (name.Length > 60) { Status = "Pack name must be 60 characters or fewer."; return; }

        var modIds = PackMods.Where(m => m.IsSelected).Select(m => m.Id).ToList();
        if (modIds.Count == 0) { Status = "Select at least one mod for the pack."; return; }

        try
        {
            IsUploading = true;
            Status = $"Creating pack '{name}'…";

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(name), "name");
            form.Add(new StringContent(PackDescription), "description");
            form.Add(new StringContent(JsonSerializer.Serialize(modIds)), "modIds");

            if (File.Exists(PackIconPath))
            {
                var iconContent = new ByteArrayContent(await File.ReadAllBytesAsync(PackIconPath));
                iconContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(iconContent, "icon", Path.GetFileName(PackIconPath));
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/packs");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            request.Content = form;

            var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                Status = $"Pack '{name}' created.";
                PackName = "";
                PackDescription = "";
                PackIconPath = "";
                PackModSearchText = "";
                IsPackPickerOpen = false;
                foreach (var mod in PackMods) mod.IsSelected = false;
                UploadSucceeded?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Status = $"Pack creation rejected: {body}";
            }
        }
        catch (Exception ex)
        {
            Status = $"Pack creation failed: {ex.Message}";
        }
        finally
        {
            IsUploading = false;
        }
    }

    [RelayCommand]
    private async Task UpdateDetailsAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiToken)) { Status = "Login with Discord first."; return; }
        if (SelectedMyMod is null) { Status = "Select one of your mods first."; return; }

        var nameError = ModNaming.Validate(DetailsName);
        if (nameError is not null) { Status = nameError; return; }

        try
        {
            IsUploading = true;
            Status = "Updating details…";

            var selectedTags = DetailsTags.Where(t => t.IsSelected).Select(t => t.Name).ToList();
            if (selectedTags.Count > ModTags.MaxTagsPerMod)
            {
                Status = $"Pick at most {ModTags.MaxTagsPerMod} tags.";
                IsUploading = false;
                return;
            }

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(DetailsName.Trim()), "name");
            form.Add(new StringContent(DetailsDescription), "description");
            form.Add(new StringContent(JsonSerializer.Serialize(selectedTags)), "tags");

            if (File.Exists(DetailsIconPath))
            {
                var iconContent = new ByteArrayContent(await File.ReadAllBytesAsync(DetailsIconPath));
                iconContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(iconContent, "icon", Path.GetFileName(DetailsIconPath));
            }

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{BaseUrl}/api/mods/{Uri.EscapeDataString(SelectedMyMod.Id)}/details");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken);
            request.Content = form;

            var response = await Http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                Status = $"Details updated for {SelectedMyMod.Name}.";
                DetailsIconPath = "";
                await LoadMyModsAsync();
                UploadSucceeded?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Status = $"Update rejected: {body}";
            }
        }
        catch (Exception ex)
        {
            Status = $"Update failed: {ex.Message}";
        }
        finally
        {
            IsUploading = false;
        }
    }
}