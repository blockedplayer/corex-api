using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

ILicenseStore store;
IAdminStore adminStore;

if (!string.IsNullOrEmpty(dbUrl))
{
    var connStr = ConvertDatabaseUrl(dbUrl);
    await InitPostgresAsync(connStr);
    store = new PgLicenseStore(connStr);
    adminStore = new PgAdminStore(connStr);
    await SeedPostgresAsync(connStr);
}
else
{
    var dataDir = Environment.GetEnvironmentVariable("DATA_DIR")
        ?? Path.Combine(AppContext.BaseDirectory, "data");
    Directory.CreateDirectory(dataDir);

    var licensesFile = Path.Combine(dataDir, "licenses.json");
    var adminFile = Path.Combine(dataDir, "admin.json");
    await SeedFileDataAsync(licensesFile, adminFile);

    store = new FileLicenseStore(licensesFile);
    adminStore = new FileAdminStore(adminFile);
}

var releaseDir = Environment.GetEnvironmentVariable("DATA_DIR")
    ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(releaseDir);
var releaseFile = Path.Combine(releaseDir, "release.json");
{
    var latestRelease = new ReleaseInfo("6.3.1", "https://github.com/blockedplayer/corex-releases/releases/download/v6.3.1/CoreX.Loader.exe");
    await File.WriteAllTextAsync(releaseFile, JsonSerializer.Serialize(latestRelease, new JsonSerializerOptions { WriteIndented = true }));
}

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
    return Results.Ok(new ReleaseInfo("2.0.4", "https://github.com/blockedplayer/corex-api/releases/download/v2.0.4/CoreX.Loader.exe"));
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
    await store.CreateAsync(new License(key, req.Plan ?? $"{req.Days} Day", req.Days, null, null, req.Name?.Trim()));
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
        k.Name,
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

app.MapPut("/api/admin/keys/{key}/name", async (string key, UpdateNameRequest req, HttpRequest http) =>
{
    if (!await AuthorizeAdmin(http, adminStore))
        return Results.Json(new { message = "Unauthorized." }, statusCode: 401);

    var updated = await store.UpdateNameAsync(key, req.Name?.Trim());
    return updated
        ? Results.Ok(new { message = "Name updated." })
        : Results.NotFound(new { message = "Key not found." });
});

app.MapDelete("/api/admin/keys", async (HttpRequest http) =>
{
    if (!await AuthorizeAdmin(http, adminStore))
        return Results.Json(new { message = "Unauthorized." }, statusCode: 401);

    var count = await store.DeleteAllAsync();
    return Results.Ok(new { message = $"{count} key(s) deleted." });
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

// ─── helpers ───

static async Task<bool> AuthorizeAdmin(HttpRequest http, IAdminStore adminStore)
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

static string ConvertDatabaseUrl(string url)
{
    if (url.StartsWith("postgres://") || url.StartsWith("postgresql://"))
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':');
        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : 5432;
        var db = uri.AbsolutePath.TrimStart('/');
        var user = userInfo[0];
        var pass = userInfo.Length > 1 ? userInfo[1] : "";
        return $"Host={host};Port={port};Database={db};Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate=true";
    }
    return url;
}

static async Task InitPostgresAsync(string connStr)
{
    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS licenses (
            key TEXT PRIMARY KEY,
            plan TEXT NOT NULL DEFAULT 'Standard',
            duration_days INT NOT NULL DEFAULT 30,
            expires_at TIMESTAMPTZ,
            device_id TEXT,
            name TEXT
        );
        CREATE TABLE IF NOT EXISTS admin (
            id INT PRIMARY KEY DEFAULT 1,
            username TEXT NOT NULL,
            password_hash TEXT NOT NULL,
            salt TEXT NOT NULL
        );
        ALTER TABLE licenses ADD COLUMN IF NOT EXISTS name TEXT;
        """;
    await cmd.ExecuteNonQueryAsync();
}

static async Task SeedPostgresAsync(string connStr)
{
    var user = Environment.GetEnvironmentVariable("ADMIN_USER");
    var pass = Environment.GetEnvironmentVariable("ADMIN_PASS");
    if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)) return;

    var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
    var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(pass + salt)));

    await using var conn = new NpgsqlConnection(connStr);
    await conn.OpenAsync();

    await using var upsert = conn.CreateCommand();
    upsert.CommandText = """
        INSERT INTO admin (id, username, password_hash, salt) VALUES (1, @u, @h, @s)
        ON CONFLICT (id) DO UPDATE SET username = @u, password_hash = @h, salt = @s
        """;
    upsert.Parameters.AddWithValue("u", user);
    upsert.Parameters.AddWithValue("h", hash);
    upsert.Parameters.AddWithValue("s", salt);
    await upsert.ExecuteNonQueryAsync();
}

static async Task SeedFileDataAsync(string licensesFile, string adminFile)
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
            await File.WriteAllTextAsync(licensesFile, seed);
    }
}

// ─── records ───

record ActivateRequest(string Key, string DeviceId);
record CreateKeyRequest(int Days, string? Plan, string? Name);
record LicenseResponse(bool Success, string Message, string? Plan, DateTime? ExpiresAt);
record ReleaseInfo(string Version, string? Url);
record License(string Key, string Plan, int DurationDays, DateTime? ExpiresAt, string? DeviceId, string? Name);
record UpdateNameRequest(string? Name);
record AdminSetupRequest(string Username, string Password);
record AdminLoginRequest(string? Username, string? Password);
record AdminCredentials(string Username, string PasswordHash, string Salt);

// ─── interfaces ───

interface ILicenseStore
{
    Task<LicenseResponse> ActivateAsync(string key, string device);
    Task CreateAsync(License l);
    Task<List<License>> ListAllAsync();
    Task<bool> DeleteAsync(string key);
    Task<int> DeleteAllAsync();
    Task<bool> ResetHwidAsync(string key);
    Task<bool> UpdateNameAsync(string key, string? name);
}

interface IAdminStore
{
    Task<bool> ExistsAsync();
    Task SetCredentialsAsync(string username, string password);
    Task<bool> ValidateAsync(string username, string password);
}

// ─── PostgreSQL stores ───

sealed class PgLicenseStore : ILicenseStore
{
    private readonly string _connStr;
    public PgLicenseStore(string connStr) => _connStr = connStr;

    public async Task<LicenseResponse> ActivateAsync(string key, string device)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();

        await using var sel = conn.CreateCommand();
        sel.CommandText = "SELECT key, plan, duration_days, expires_at, device_id FROM licenses WHERE LOWER(key) = LOWER(@k)";
        sel.Parameters.AddWithValue("k", key);
        await using var r = await sel.ExecuteReaderAsync();
        if (!await r.ReadAsync())
            return new(false, "Invalid key.", null, null);

        var plan = r.GetString(1);
        var days = r.GetInt32(2);
        var expiresAt = r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3);
        var deviceId = r.IsDBNull(4) ? null : r.GetString(4);
        await r.CloseAsync();

        if (expiresAt is not null && expiresAt <= DateTime.UtcNow)
            return new(false, "This subscription has expired.", null, null);
        if (deviceId is not null && !string.Equals(deviceId, device, StringComparison.Ordinal))
            return new(false, "This key is already bound to another device.", null, null);

        if (deviceId is null)
        {
            var newExpiry = DateTime.UtcNow.AddDays(days);
            await using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE licenses SET device_id = @d, expires_at = @e WHERE LOWER(key) = LOWER(@k)";
            upd.Parameters.AddWithValue("d", device);
            upd.Parameters.AddWithValue("e", newExpiry);
            upd.Parameters.AddWithValue("k", key);
            await upd.ExecuteNonQueryAsync();
            expiresAt = newExpiry;
        }

        return new(true, "Subscription activated successfully.", plan, expiresAt?.ToLocalTime());
    }

    public async Task CreateAsync(License l)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO licenses (key, plan, duration_days, name) VALUES (@k, @p, @d, @n)";
        cmd.Parameters.AddWithValue("k", l.Key);
        cmd.Parameters.AddWithValue("p", l.Plan);
        cmd.Parameters.AddWithValue("d", l.DurationDays);
        cmd.Parameters.AddWithValue("n", (object?)l.Name ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<License>> ListAllAsync()
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, plan, duration_days, expires_at, device_id, name FROM licenses ORDER BY key";
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<License>();
        while (await r.ReadAsync())
        {
            list.Add(new License(
                r.GetString(0),
                r.GetString(1),
                r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetDateTime(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5)
            ));
        }
        return list;
    }

    public async Task<bool> UpdateNameAsync(string key, string? name)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE licenses SET name = @n WHERE LOWER(key) = LOWER(@k)";
        cmd.Parameters.AddWithValue("n", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("k", key);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> DeleteAsync(string key)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM licenses WHERE LOWER(key) = LOWER(@k)";
        cmd.Parameters.AddWithValue("k", key);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<int> DeleteAllAsync()
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM licenses";
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> ResetHwidAsync(string key)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE licenses SET device_id = NULL, expires_at = NULL WHERE LOWER(key) = LOWER(@k)";
        cmd.Parameters.AddWithValue("k", key);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }
}

sealed class PgAdminStore : IAdminStore
{
    private readonly string _connStr;
    public PgAdminStore(string connStr) => _connStr = connStr;

    public async Task<bool> ExistsAsync()
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM admin";
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    public async Task SetCredentialsAsync(string username, string password)
    {
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var hash = HashPassword(password, salt);

        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO admin (id, username, password_hash, salt) VALUES (1, @u, @h, @s)
            ON CONFLICT (id) DO UPDATE SET username = @u, password_hash = @h, salt = @s
            """;
        cmd.Parameters.AddWithValue("u", username);
        cmd.Parameters.AddWithValue("h", hash);
        cmd.Parameters.AddWithValue("s", salt);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<bool> ValidateAsync(string username, string password)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT username, password_hash, salt FROM admin WHERE id = 1";
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return false;

        var storedUser = r.GetString(0);
        var storedHash = r.GetString(1);
        var salt = r.GetString(2);

        if (!string.Equals(storedUser, username, StringComparison.OrdinalIgnoreCase)) return false;
        return HashPassword(password, salt) == storedHash;
    }

    private static string HashPassword(string password, string salt)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
}

// ─── file-based stores (local dev fallback) ───

sealed class FileAdminStore : IAdminStore
{
    private readonly string _file;
    public FileAdminStore(string file) => _file = file;

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
            return HashPassword(password, creds.Salt) == creds.PasswordHash;
        }
        catch { return false; }
    }

    private static string HashPassword(string password, string salt)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
}

sealed class FileLicenseStore : ILicenseStore
{
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileLicenseStore(string file) => _file = file;

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

    public async Task<int> DeleteAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var all = await ReadAsync();
            var count = all.Count;
            all.Clear();
            await WriteAsync(all);
            return count;
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

    public async Task<bool> UpdateNameAsync(string key, string? name)
    {
        await _gate.WaitAsync();
        try
        {
            var all = await ReadAsync();
            var i = all.FindIndex(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            all[i] = all[i] with { Name = name };
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
