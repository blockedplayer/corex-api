using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dataDir = Environment.GetEnvironmentVariable("DATA_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);

var licensesFile = Path.Combine(dataDir, "licenses.json");
var adminFile = Path.Combine(dataDir, "admin.json");

await SeedDataAsync(licensesFile, adminFile);

var releaseFile = Path.Combine(dataDir, "release.json");

var store = new LicenseStore(licensesFile);
var adminStore = new AdminStore(adminFile);

app.MapGet("/", () => Results.Ok(new { service = "CoreX License API", status = "online" }));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/license/activate", async (ActivateRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Key) || string.IsNullOrWhiteSpace(req.DeviceId))
        return Results.BadRequest(new LicenseResponse(false, "Key and device are required.", null, null));

    var result = await store.ActivateAsync(req.Key.Trim(), req.DeviceId.Trim());
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/releases/current", async () =>
{
    if (File.Exists(releaseFile))
    {
        var json = await File.ReadAllTextAsync(releaseFile);
        var info = JsonSerializer.Deserialize<ReleaseInfo>(json);
        if (info is not null) return Results.Ok(info);
    }
    return Results.Ok(new ReleaseInfo("1.0.0", null));
});

app.MapPost("/api/admin/release", async (ReleaseInfo info, HttpRequest http) =>
{
    if (!await AuthorizeAdmin(http, adminStore))
        return Results.Json(new { message = "Unauthorized." }, statusCode: 401);

    if (string.IsNullOrWhiteSpace(info.Version))
        return Results.BadRequest(new { message = "Version is required." });

    var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(releaseFile, json);
    return Results.Ok(new { message = $"Release updated to v{info.Version}." });
});

app.MapPost("/api/admin/setup", async (AdminSetupRequest req) =>
{
    if (await adminStore.ExistsAsync())
        return Results.BadRequest(new { message = "Admin already configured." });

    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { message = "Username and password are required." });

    await adminStore.SetCredentialsAsync(req.Username.Trim(), req.Password);
    return Results.Ok(new { message = "Admin credentials set." });
});

app.MapPost("/api/admin/login", async (AdminLoginRequest req) =>
{
    if (!await adminStore.ValidateAsync(req.Username ?? "", req.Password ?? ""))
        return Results.Json(new { success = false, message = "Invalid credentials." }, statusCode: 401);

    return Results.Ok(new { success = true, message = "Authenticated." });
});

app.MapPost("/api/admin/keys", async (CreateKeyRequest req, HttpRequest http) =>
{
    if (!await AuthorizeAdmin(http, adminStore))
        return Results.Json(new { message = "Unauthorized." }, statusCode: 401);

    if (req.Days <= 0) return Results.BadRequest(new { message = "Days must be greater than zero." });
    var key = "COREX-" + RandomKey(20);
    await store.CreateAsync(new License(key, req.Plan ?? $"{req.Days} Day", req.Days, null, null));
    return Results.Ok(new { key });
});

app.MapGet("/api/admin/keys", async (HttpRequest http) =>
{
    if (!await AuthorizeAdmin(http, adminStore))
        return Results.Json(new { message = "Unauthorized." }, statusCode: 401);

    var keys = await store.ListAllAsync();
    return Results.Ok(keys.Select(k => new
    {
        k.Key,
        k.Plan,
        k.DurationDays,
        ExpiresAt = k.ExpiresAt?.ToLocalTime(),
        k.DeviceId,
        Activated = k.ExpiresAt is not null,
        Expired = k.ExpiresAt is not null && k.ExpiresAt <= DateTime.UtcNow,
        Bound = k.DeviceId is not null
    }));
});

app.MapDelete("/api/admin/keys/{key}", async (string key, HttpRequest http) =>
{
    if (!await AuthorizeAdmin(http, adminStore))
        return Results.Json(new { message = "Unauthorized." }, statusCode: 401);

    var removed = await store.DeleteAsync(key);
    return removed
        ? Results.Ok(new { message = $"Key deleted." })
        : Results.NotFound(new { message = "Key not found." });
});

app.MapPost("/api/admin/keys/{key}/reset-hwid", async (string key, HttpRequest http) =>
{
    if (!await AuthorizeAdmin(http, adminStore))
        return Results.Json(new { message = "Unauthorized." }, statusCode: 401);

    var reset = await store.ResetHwidAsync(key);
    return reset
        ? Results.Ok(new { message = "HWID reset. Key can be activated on a new device." })
        : Results.NotFound(new { message = "Key not found." });
});

app.MapGet("/api/admin/backup", async (HttpRequest http) =>
{
    if (!await AuthorizeAdmin(http, adminStore))
        return Results.Json(new { message = "Unauthorized." }, statusCode: 401);

    var keys = await store.ListAllAsync();
    return Results.Ok(new { licenses = keys, exportedAt = DateTime.UtcNow });
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "5080";
app.Run($"http://0.0.0.0:{port}");

static async Task SeedDataAsync(string licensesFile, string adminFile)
{
    if (!File.Exists(adminFile))
    {
        var user = Environment.GetEnvironmentVariable("ADMIN_USER");
        var pass = Environment.GetEnvironmentVariable("ADMIN_PASS");
        if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
        {
            var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(pass + salt)));
            var creds = new AdminCredentials(user, hash, salt);
            var json = JsonSerializer.Serialize(creds, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(adminFile, json);
        }
    }

    if (!File.Exists(licensesFile))
    {
        var seed = Environment.GetEnvironmentVariable("SEED_LICENSES");
        if (!string.IsNullOrEmpty(seed))
        {
            await File.WriteAllTextAsync(licensesFile, seed);
        }
    }
}

static async Task<bool> AuthorizeAdmin(HttpRequest http, AdminStore adminStore)
{
    var user = http.Headers["X-Admin-User"].FirstOrDefault() ?? "";
    var pass = http.Headers["X-Admin-Pass"].FirstOrDefault() ?? "";

    if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
        return await adminStore.ValidateAsync(user, pass);

    var secret = Environment.GetEnvironmentVariable("COREX_ADMIN_SECRET");
    if (!string.IsNullOrEmpty(secret) && http.Headers["X-Admin-Secret"] == secret)
        return true;

    return false;
}

static string RandomKey(int length)
{
    const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    var bytes = RandomNumberGenerator.GetBytes(length);
    var sb = new StringBuilder(length);
    foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
    return sb.ToString();
}

record ActivateRequest(string Key, string DeviceId);
record CreateKeyRequest(int Days, string? Plan);
record LicenseResponse(bool Success, string Message, string? Plan, DateTime? ExpiresAt);
record ReleaseInfo(string Version, string? Url);
record License(string Key, string Plan, int DurationDays, DateTime? ExpiresAt, string? DeviceId);
record AdminSetupRequest(string Username, string Password);
record AdminLoginRequest(string? Username, string? Password);
record AdminCredentials(string Username, string PasswordHash, string Salt);

sealed class AdminStore
{
    private readonly string _file;

    public AdminStore(string file) => _file = file;

    public Task<bool> ExistsAsync() => Task.FromResult(File.Exists(_file));

    public async Task SetCredentialsAsync(string username, string password)
    {
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var hash = HashPassword(password, salt);
        var creds = new AdminCredentials(username, hash, salt);
        var json = JsonSerializer.Serialize(creds, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_file, json);
    }

    public async Task<bool> ValidateAsync(string username, string password)
    {
        if (!File.Exists(_file)) return false;
        try
        {
            var json = await File.ReadAllTextAsync(_file);
            var creds = JsonSerializer.Deserialize<AdminCredentials>(json);
            if (creds is null) return false;
            if (!string.Equals(creds.Username, username, StringComparison.OrdinalIgnoreCase)) return false;
            var hash = HashPassword(password, creds.Salt);
            return hash == creds.PasswordHash;
        }
        catch { return false; }
    }

    private static string HashPassword(string password, string salt)
    {
        var bytes = Encoding.UTF8.GetBytes(password + salt);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}

sealed class LicenseStore
{
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LicenseStore(string file) => _file = file;

    public async Task<LicenseResponse> ActivateAsync(string key, string device)
    {
        await _gate.WaitAsync();
        try
        {
            var all = await ReadAsync();
            var i = all.FindIndex(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return new(false, "Invalid key.", null, null);

            var l = all[i];
            if (l.ExpiresAt is not null && l.ExpiresAt <= DateTime.UtcNow)
                return new(false, "This subscription has expired.", null, null);
            if (l.DeviceId is not null && !string.Equals(l.DeviceId, device, StringComparison.Ordinal))
                return new(false, "This key is already bound to another device.", null, null);

            if (l.DeviceId is null)
            {
                l = l with { DeviceId = device, ExpiresAt = DateTime.UtcNow.AddDays(l.DurationDays) };
                all[i] = l;
                await WriteAsync(all);
            }

            return new(true, "Subscription activated successfully.", l.Plan, l.ExpiresAt?.ToLocalTime());
        }
        finally { _gate.Release(); }
    }

    public async Task CreateAsync(License l)
    {
        await _gate.WaitAsync();
        try
        {
            var all = await ReadAsync();
            all.Add(l);
            await WriteAsync(all);
        }
        finally { _gate.Release(); }
    }

    public async Task<List<License>> ListAllAsync()
    {
        await _gate.WaitAsync();
        try { return await ReadAsync(); }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string key)
    {
        await _gate.WaitAsync();
        try
        {
            var all = await ReadAsync();
            var i = all.FindIndex(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            all.RemoveAt(i);
            await WriteAsync(all);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ResetHwidAsync(string key)
    {
        await _gate.WaitAsync();
        try
        {
            var all = await ReadAsync();
            var i = all.FindIndex(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            all[i] = all[i] with { DeviceId = null, ExpiresAt = null };
            await WriteAsync(all);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<License>> ReadAsync()
    {
        if (!File.Exists(_file)) return [];
        await using var fs = File.OpenRead(_file);
        return await JsonSerializer.DeserializeAsync<List<License>>(fs) ?? [];
    }

    private async Task WriteAsync(List<License> data)
    {
        await using var fs = File.Create(_file);
        await JsonSerializer.SerializeAsync(fs, data, new JsonSerializerOptions { WriteIndented = true });
    }
}
