using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace CoreX.Loader;

public partial class MainWindow : Window
{
    private const string AppVersion = "1.4.0";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly HttpClient _fastHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _pollTimer;
    private readonly string _settingsPath;
    private string DeviceId { get; } = DeviceIdentity.Get();
    private bool _licensed;
    private bool _adminAuthed;
    private bool _autoInjected;
    private bool _autoInjecting;
    private bool _updating;
    private string _adminUser = "";
    private string _adminPass = "";
    private AppSettings _currentSettings = new();

    private static readonly string ExeDir = AppContext.BaseDirectory;
    private static readonly string CoffinBuild = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        @"Desktop\coffin\coffin\mw2\mw2 working example\Build");
    private static readonly string DefaultSourceDll  = Path.Combine(CoffinBuild, "MW2.dll");
    private static readonly string DefaultProxyDll   = Path.Combine(ExeDir, "version.dll");
    private static readonly string DefaultStringTableDll = Path.Combine(ExeDir, "stringtable.dll");
    private static readonly string EmbeddedBinDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreX", "bin");
    private static readonly string PayloadCacheDir   = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreX", "payload");
    private static readonly string CachedPayloadPath = Path.Combine(PayloadCacheDir, "cxpayload.dll");
    private const string DefaultDeployName = "cxpayload.dll";

    private static readonly string[] EmbeddedPayloads = { "mw2.dll", "PYTExample.exe", "SecureEngineSDK64.dll", "FixGame.exe" };

    private static void ExtractEmbeddedPayloads()
    {
        Directory.CreateDirectory(EmbeddedBinDir);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in EmbeddedPayloads)
        {
            var dest = Path.Combine(EmbeddedBinDir, name);
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            if (File.Exists(dest) && new FileInfo(dest).Length == stream.Length) continue;
            using var fs = File.Create(dest);
            stream.CopyTo(fs);
        }
    }

    private string SourceDll    => _currentSettings.SourceDll ?? DefaultSourceDll;
    private string ProxyDll     => _currentSettings.ProxyDll ?? DefaultProxyDll;
    private string DeployName   => _currentSettings.DeployFilename ?? DefaultDeployName;
    private string Api          => ApiBox.Text.TrimEnd('/');

    public MainWindow()
    {
        ExtractEmbeddedPayloads();
        AuthGuard.InitProtection();
        InitializeComponent();

        if (AuthGuard.IsDebuggerAttached())
        {
            MessageBox.Show("Security violation detected.", "CORE X", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
            return;
        }

        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CoreX", "settings.json");

        DeviceLabel.Text = $"Device: {DeviceId[..8]}...";
        SubDeviceText.Text = DeviceId[..8] + "...";

        LoadSettings();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pollTimer.Tick += (_, _) =>
        {
            PollGameStatus();
            if (AuthGuard.IsDebuggerAttached() || AuthGuard.ScanForTools()) { Environment.Exit(1); }
        };
        _pollTimer.Start();
        PollGameStatus();
        CheckPayloadDeployed();

        if (!string.IsNullOrEmpty(_currentSettings.LicenseKey))
            _ = AutoActivateAsync(_currentSettings.LicenseKey);

        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var procName = ProcessNameBox?.Text?.Trim() ?? "cod22-cod";
            var gameRunning = Process.GetProcessesByName(procName);
            if (gameRunning.Length > 0)
            {
                foreach (var p in gameRunning) p.Dispose();
                return;
            }

            var resp = await _fastHttp.GetAsync($"{Api}/api/releases/current");
            if (!resp.IsSuccessStatusCode) return;

            var json = await resp.Content.ReadAsStringAsync();
            var version = TryGetField(json, "version");
            var url = TryGetField(json, "url");
            if (version is null || url is null) return;

            if (Version.TryParse(version, out var remote) &&
                Version.TryParse(AppVersion, out var local) &&
                remote > local)
            {
                _updating = true;
                VersionLabel.Text = $"Updating to v{version}...";
                await DownloadUpdateAsync(url);
                _updating = false;
            }
        }
        catch { }
    }

    private async Task DownloadUpdateAsync(string url)
    {
        try
        {
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (currentExe is null) return;

            var updatePath = currentExe + ".update";

            using var dlClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await dlClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            await using var fs = File.Create(updatePath);
            await response.Content.CopyToAsync(fs);
            fs.Close();

            var batchPath = Path.Combine(Path.GetTempPath(), "corex_update.cmd");
            File.WriteAllText(batchPath,
                $"@echo off\r\ntimeout /t 2 /nobreak >nul\r\nmove /Y \"{updatePath}\" \"{currentExe}\"\r\nstart \"\" \"{currentExe}\"\r\ndel \"%~f0\"");

            Process.Start(new ProcessStartInfo
            {
                FileName = batchPath,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Update failed: {ex.Message}", "Update Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ═══════════════ HIDDEN ADMIN ACCESS ═══════════════

    private int _versionClickCount;
    private DateTime _lastVersionClick = DateTime.MinValue;

    private void Version_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((DateTime.Now - _lastVersionClick).TotalSeconds > 2)
            _versionClickCount = 0;

        _lastVersionClick = DateTime.Now;
        _versionClickCount++;

        if (_versionClickCount >= 3)
        {
            _versionClickCount = 0;
            NavAdmin.Visibility = Visibility.Visible;
            NavAdmin.IsChecked = true;
        }
    }

    // ═══════════════ NAVIGATION ═══════════════

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHome is null) return;

        PageHome.Visibility        = Visibility.Collapsed;
        PageLoader.Visibility      = Visibility.Collapsed;
        PageLicense.Visibility     = Visibility.Collapsed;
        PageSettings.Visibility    = Visibility.Collapsed;
        PageBuildDeploy.Visibility = Visibility.Collapsed;
        PageAdmin.Visibility       = Visibility.Collapsed;

        if (NavHome.IsChecked == true)             { PageHome.Visibility        = Visibility.Visible; PageTitle.Text = "Home"; }
        else if (NavLoader.IsChecked == true)      { PageLoader.Visibility      = Visibility.Visible; PageTitle.Text = "Loader"; }
        else if (NavLicense.IsChecked == true)      { PageLicense.Visibility     = Visibility.Visible; PageTitle.Text = "License"; }
        else if (NavSettings.IsChecked == true)     { PageSettings.Visibility    = Visibility.Visible; PageTitle.Text = "Settings"; }
        else if (NavBuildDeploy.IsChecked == true)  { PageBuildDeploy.Visibility = Visibility.Visible; PageTitle.Text = "Build + Deploy"; RefreshBuildDeployInfo(); }
        else if (NavAdmin.IsChecked == true)        { PageAdmin.Visibility       = Visibility.Visible; PageTitle.Text = "Admin"; }
    }

    // ═══════════════ GAME DETECTION ═══════════════

    private void PollGameStatus()
    {
        var procName = ProcessNameBox?.Text?.Trim();
        if (string.IsNullOrEmpty(procName)) procName = "cod22-cod";

        try
        {
            var procs = Process.GetProcessesByName(procName);
            if (procs.Length > 0)
            {
                GameDot.Fill = FindResource("SuccessBrush") as SolidColorBrush;
                GameStatusText.Text = "Game running";
                HomeGameStatus.Text = "Running";
                HomeGamePID.Text = $"PID: {procs[0].Id}";

                if (!_autoInjected && !_autoInjecting && _licensed)
                    TriggerAutoInject();
            }
            else
            {
                GameDot.Fill = FindResource("ErrorBrush") as SolidColorBrush;
                GameStatusText.Text = "Game not detected";
                HomeGameStatus.Text = "Not Detected";
                HomeGamePID.Text = "PID: —";
            }
            QuickLaunchBtn.IsEnabled = _licensed;
        }
        catch
        {
            GameDot.Fill = FindResource("MutedBrush") as SolidColorBrush;
            GameStatusText.Text = "Detection error";
            HomeGameStatus.Text = "Error";
            HomeGamePID.Text = "PID: —";
        }
    }

    private void CheckPayloadDeployed()
    {
        try
        {
            var gameDir = GameDirBox?.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(gameDir)) return;

            var target = Path.Combine(gameDir, DeployName);
            if (File.Exists(target))
            {
                var info = new FileInfo(target);
                HomePayloadStatus.Text = "Deployed";
                HomePayloadInfo.Text = $"{info.Length / 1024} KB  {info.LastWriteTime:g}";
            }
            else
            {
                HomePayloadStatus.Text = "Not Deployed";
                HomePayloadInfo.Text = "—";
            }
        }
        catch { }
    }

    // ═══════════════ DEPLOY ═══════════════

    private void DeployAllDlls()
    {
        DeployProxy();
        DeployPayload();
        DeployStringTable();
    }

    private void DeployAll_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireLicense()) return;
        DeployAllDlls();
    }

    private void Deploy_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireLicense()) return;
        DeployPayload();
    }

    private bool WarnIfGameRunning()
    {
        var procName = ProcessNameBox?.Text?.Trim() ?? "cod22-cod";
        var procs = Process.GetProcessesByName(procName);
        if (procs.Length > 0)
        {
            AppendLog("[WARN] Game is running — DLLs may be locked.");
            var result = MessageBox.Show(
                "The game is running. DLLs may be locked.\nAttempt deployment anyway?",
                "CORE X", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return result == MessageBoxResult.Yes;
        }
        return true;
    }

    private void DeployPayload()
    {
        var source   = SourceDll;
        var gameDir  = GameDirBox.Text.Trim();
        var filename = DeployName;

        if (string.IsNullOrEmpty(gameDir))
        {
            AppendLog("[ERROR] Game directory is required.");
            return;
        }
        if (!File.Exists(source)) { AppendLog("[ERROR] Payload DLL not found."); return; }
        if (!Directory.Exists(gameDir)) { AppendLog($"[ERROR] Game dir not found: {gameDir}"); return; }
        if (!WarnIfGameRunning()) return;

        var target = Path.Combine(gameDir, filename);
        try
        {
            AppendLog($"[DEPLOY] Payload -> {filename}");
            File.Copy(source, target, overwrite: true);
            var info = new FileInfo(target);
            AppendLog($"[OK] Payload deployed ({info.Length / 1024} KB)");
            CheckPayloadDeployed();
        }
        catch (Exception ex) { AppendLog($"[ERROR] Deploy failed: {ex.Message}"); }
    }

    private void DeployProxy()
    {
        var proxySource = ProxyDll;
        var gameDir     = GameDirBox.Text.Trim();

        if (string.IsNullOrEmpty(proxySource))
        {
            AppendLog("[INFO] No proxy DLL configured — skipping.");
            return;
        }
        if (!File.Exists(proxySource)) { AppendLog("[ERROR] Proxy DLL not found."); return; }
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
        {
            AppendLog("[ERROR] Game directory not set.");
            return;
        }

        var target = Path.Combine(gameDir, "version.dll");
        try
        {
            AppendLog("[DEPLOY] Proxy → version.dll");
            File.Copy(proxySource, target, overwrite: true);
            AppendLog($"[OK] Proxy deployed ({new FileInfo(target).Length / 1024} KB)");
        }
        catch (Exception ex) { AppendLog($"[ERROR] Proxy deploy failed: {ex.Message}"); }
    }

    private void DeployStringTable()
    {
        var src     = DefaultStringTableDll;
        var gameDir = GameDirBox.Text.Trim();

        if (!File.Exists(src)) { AppendLog("[INFO] stringtable.dll not found — skipping."); return; }
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
        {
            AppendLog("[ERROR] Game directory not set."); return;
        }

        var target = Path.Combine(gameDir, "stringtable.dll");
        try
        {
            AppendLog("[DEPLOY] stringtable.dll → game dir");
            File.Copy(src, target, overwrite: true);
            AppendLog($"[OK] stringtable.dll deployed ({new FileInfo(target).Length / 1024} KB)");
        }
        catch (Exception ex) { AppendLog($"[ERROR] stringtable deploy failed: {ex.Message}"); }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        var logPath = DebugLogBox.Text.Trim();
        if (string.IsNullOrEmpty(logPath)) { AppendLog("[ERROR] Debug log path not set."); return; }
        try
        {
            if (File.Exists(logPath)) { File.Delete(logPath); AppendLog("[OK] Cleared debug log."); }
            else AppendLog("[INFO] Debug log does not exist.");
        }
        catch (Exception ex) { AppendLog($"[ERROR] {ex.Message}"); }
    }

    private void CleanDlls_Click(object sender, RoutedEventArgs e)
    {
        var gameDir = GameDirBox.Text.Trim();
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
        {
            AppendLog("[ERROR] Game directory not set or not found.");
            return;
        }

        var result = MessageBox.Show(
            "Delete cxpayload.dll, t10 workspace.dll, and dummy.dll from the game directory?",
            "CORE X — Clean", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        string[] toClean = ["cxpayload.dll", "t10 workspace.dll", "dummy.dll"];
        int removed = 0;
        foreach (var f in toClean)
        {
            var path = Path.Combine(gameDir, f);
            if (!File.Exists(path)) continue;
            try { File.Delete(path); AppendLog($"[OK] Deleted {f}"); removed++; }
            catch (Exception ex) { AppendLog($"[ERROR] {f}: {ex.Message}"); }
        }
        if (removed == 0) AppendLog("[INFO] No payload DLLs found.");
        CheckPayloadDeployed();
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e)
    {
        var logPath = DebugLogBox.Text.Trim();
        if (string.IsNullOrEmpty(logPath)) { AppendLog("[INFO] Debug log path not set."); return; }
        if (!File.Exists(logPath)) { AppendLog("[INFO] Debug log does not exist yet."); return; }
        try
        {
            var lines = File.ReadAllLines(logPath);
            var tail = lines.Length > 100 ? lines[^100..] : lines;
            DeployLog.Text = string.Join("\n", tail);
            LogScroll.ScrollToBottom();
        }
        catch (Exception ex) { AppendLog($"[ERROR] {ex.Message}"); }
    }

    private void ClearLogView_Click(object sender, RoutedEventArgs e) => DeployLog.Text = "Ready.";

    // ═══════════════ BUILD ═══════════════

    private async Task<bool> BuildAsync()
    {
        var project  = _currentSettings.CheatProjectPath ?? "";
        var msbuild  = _currentSettings.MsBuildPath ?? "";
        var config   = _currentSettings.BuildConfiguration ?? "Debug";
        var platform = _currentSettings.BuildPlatform ?? "x64";

        if (!File.Exists(project)) { AppendLog("[ERROR] Cheat project not found. Set path in Settings."); return false; }
        if (!File.Exists(msbuild)) { AppendLog("[ERROR] MSBuild not found. Set path in Settings."); return false; }

        AppendLog($"[BUILD] {Path.GetFileName(project)} — {config}|{platform}");

        var psi = new ProcessStartInfo
        {
            FileName               = msbuild,
            Arguments              = $"\"{project}\" /p:Configuration={config} /p:Platform={platform} /m /nologo /v:minimal",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var readOut = Task.Run(async () =>
        {
            while (!proc.StandardOutput.EndOfStream)
            {
                var line = await proc.StandardOutput.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(line))
                    Dispatcher.Invoke(() => AppendLog(line.TrimEnd()));
            }
        });

        var readErr = Task.Run(async () =>
        {
            while (!proc.StandardError.EndOfStream)
            {
                var line = await proc.StandardError.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(line))
                    Dispatcher.Invoke(() => AppendLog("[ERR] " + line.TrimEnd()));
            }
        });

        await Task.WhenAll(readOut, readErr);
        await Task.Run(() => proc.WaitForExit());

        if (proc.ExitCode != 0)
        {
            AppendLog($"[ERROR] Build failed (exit {proc.ExitCode}).");
            return false;
        }

        AppendLog("[OK] Build succeeded.");

        var projectDir = Path.GetDirectoryName(project)!;
        // vcxproj OutDir = $(Platform)\dll\  TargetName = dummy — no config folder in path
        var builtDll   = Path.Combine(projectDir, platform, "dll", "dummy.dll");
        if (File.Exists(builtDll))
            _currentSettings.SourceDll = builtDll;

        return true;
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
    {
        BuildBtn.IsEnabled = false;
        BuildDeployBtn.IsEnabled = false;
        try { await BuildAsync(); }
        finally { BuildBtn.IsEnabled = true; BuildDeployBtn.IsEnabled = true; }
    }

    private async void BuildDeployAll_Click(object sender, RoutedEventArgs e)
    {
        BuildBtn.IsEnabled = false;
        BuildDeployBtn.IsEnabled = false;
        try
        {
            if (await BuildAsync())
                DeployAllDlls();
        }
        finally { BuildBtn.IsEnabled = true; BuildDeployBtn.IsEnabled = true; }
    }

    private void BrowseCheatProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title  = "Select cheat project",
            Filter = "Visual C++ Project|*.vcxproj"
        };
        if (CheatProjectBox.Text.Trim().Length > 0)
            dlg.FileName = CheatProjectBox.Text.Trim();
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            CheatProjectBox.Text = dlg.FileName;
    }

    private void BrowseMsBuild_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.OpenFileDialog
        {
            Title  = "Select MSBuild.exe",
            Filter = "MSBuild|MSBuild.exe|All executables|*.exe"
        };
        if (MsBuildPathBox.Text.Trim().Length > 0)
            dlg.FileName = MsBuildPathBox.Text.Trim();
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            MsBuildPathBox.Text = dlg.FileName;
    }

    // ═══════════════ PAYLOAD DISTRIBUTION ═══════════════

    private async Task FetchPayloadAsync()
    {
        if (!_licensed || string.IsNullOrEmpty(_currentSettings.LicenseKey)) return;
        try
        {
            AppendLog("[*] Checking server for payload update...");
            var req = new HttpRequestMessage(HttpMethod.Get, $"{Api}/api/payload/info");
            req.Headers.Add("X-License-Key", _currentSettings.LicenseKey);
            req.Headers.Add("X-Device-Id", DeviceId);
            var resp = await _fastHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                AppendLog("[INFO] No server payload — using local DLL.");
                return;
            }

            var info = await resp.Content.ReadFromJsonAsync<PayloadInfo>();
            if (info?.Hash is null) return;

            string? localHash = null;
            if (File.Exists(CachedPayloadPath))
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                await using var fs = File.OpenRead(CachedPayloadPath);
                localHash = Convert.ToHexString(sha.ComputeHash(fs)).ToLower();
            }

            if (string.Equals(localHash, info.Hash.ToLower(), StringComparison.Ordinal))
            {
                AppendLog($"[OK] Payload up to date ({info.Size / 1024} KB).");
                _currentSettings.SourceDll = CachedPayloadPath;
                return;
            }

            AppendLog($"[*] Downloading payload update ({info.Size / 1024} KB)...");
            var dlReq = new HttpRequestMessage(HttpMethod.Get, $"{Api}/api/payload/download");
            dlReq.Headers.Add("X-License-Key", _currentSettings.LicenseKey);
            dlReq.Headers.Add("X-Device-Id", DeviceId);
            var dlResp = await _fastHttp.SendAsync(dlReq);
            if (!dlResp.IsSuccessStatusCode) { AppendLog("[ERROR] Payload download failed."); return; }

            Directory.CreateDirectory(PayloadCacheDir);
            var bytes = await dlResp.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(CachedPayloadPath, bytes);
            _currentSettings.SourceDll = CachedPayloadPath;
            AppendLog($"[OK] Payload downloaded ({bytes.Length / 1024} KB) — ready to deploy.");
        }
        catch (Exception ex)
        {
            AppendLog($"[WARN] Server payload check skipped: {ex.Message}");
        }
    }

    private async void UploadPayload_Click(object sender, RoutedEventArgs e)
    {
        if (!_adminAuthed) return;

        var dllPath = SourceDll;
        var project = _currentSettings.CheatProjectPath ?? "";
        if (!string.IsNullOrEmpty(project))
        {
            var projectDir = Path.GetDirectoryName(project)!;
            var builtDll   = Path.Combine(projectDir,
                                 _currentSettings.BuildPlatform ?? "x64",
                                 _currentSettings.BuildConfiguration ?? "Debug",
                                 "t10 workspace.dll");
            if (File.Exists(builtDll)) dllPath = builtDll;
        }

        if (!File.Exists(dllPath)) { AppendLog("[ERROR] Payload DLL not found. Build first."); return; }

        var size = new FileInfo(dllPath).Length;
        AppendLog($"[UPLOAD] {Path.GetFileName(dllPath)} ({size / 1024} KB) → server...");

        try
        {
            var bytes = await File.ReadAllBytesAsync(dllPath);
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "file", "cxpayload.dll");

            var req = new HttpRequestMessage(HttpMethod.Post, $"{Api}/api/admin/payload/upload");
            req.Headers.Add("X-Admin-User", _adminUser);
            req.Headers.Add("X-Admin-Pass", _adminPass);
            req.Content = content;

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
                AppendLog("[OK] Payload uploaded — all licensed users get it on next launch.");
            else
                AppendLog($"[ERROR] Upload failed: {TryGetMessage(body) ?? $"HTTP {(int)resp.StatusCode}"}");
        }
        catch (Exception ex) { AppendLog($"[ERROR] {ex.Message}"); }
    }

    private sealed record PayloadInfo(string? Hash, long Size, string? Version);

    private void RefreshBuildDeployInfo()
    {
        InfoCheatProject.Text = _currentSettings.CheatProjectPath ?? "(not set — configure in Settings)";
        InfoBuildConfig.Text  = $"{_currentSettings.BuildConfiguration ?? "Debug"} | {_currentSettings.BuildPlatform ?? "x64"}";

        InfoSourceDll.Text = SourceDll;
        InfoProxyDll.Text = ProxyDll;
        InfoDeployName.Text = DeployName;
        InfoGameDir.Text = GameDirBox.Text.Trim();

        InfoSourceStatus.Text = File.Exists(SourceDll) ? "Found" : "NOT FOUND";
        InfoSourceStatus.Foreground = FindResource(File.Exists(SourceDll) ? "SuccessBrush" : "ErrorBrush") as SolidColorBrush;

        InfoProxyStatus.Text = File.Exists(ProxyDll) ? "Found" : "NOT FOUND";
        InfoProxyStatus.Foreground = FindResource(File.Exists(ProxyDll) ? "SuccessBrush" : "ErrorBrush") as SolidColorBrush;

        var gameDir = GameDirBox.Text.Trim();
        var deployed = !string.IsNullOrEmpty(gameDir) && File.Exists(Path.Combine(gameDir, DeployName));
        InfoDeployedStatus.Text = deployed ? "Yes" : "No";
        InfoDeployedStatus.Foreground = FindResource(deployed ? "SuccessBrush" : "MutedBrush") as SolidColorBrush;
    }

    private void JoinDiscord_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://discord.gg/mZH9AEEYk",
                UseShellExecute = true
            });
        }
        catch { }
    }

    // ═══════════════ AUTO INJECTION ═══════════════

    private async void TriggerAutoInject()
    {
        if (_autoInjecting || _autoInjected) return;
        _autoInjecting = true;

        var injectorPath = FindFile("PYTExample.exe");
        var dllPath = FindFile("mw2.dll", "MW2.dll");

        if (injectorPath is null || dllPath is null)
        {
            AppendLog("[AUTO] Injector or DLL not found — skipping.");
            _autoInjecting = false;
            return;
        }

        AppendLog("[AUTO] Game detected — injecting in 10 seconds...");
        await Task.Delay(10_000);

        var procName = ProcessNameBox?.Text?.Trim() ?? "cod22-cod";
        var procs = Process.GetProcessesByName(procName);
        if (procs.Length == 0)
        {
            AppendLog("[AUTO] Game closed before injection.");
            _autoInjecting = false;
            return;
        }
        foreach (var p in procs) p.Dispose();

        var injDir = Path.GetDirectoryName(injectorPath) ?? ExeDir;
        AppendLog("[AUTO] Injecting via kernel mapper (requesting admin)...");
        await Task.Run(() =>
        {
            try
            {
                var pyt = Process.Start(new ProcessStartInfo
                {
                    FileName = injectorPath,
                    WorkingDirectory = injDir,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                pyt?.WaitForExit(60_000);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"[AUTO] Injector error: {ex.Message}"));
            }
        });

        AppendLog("[AUTO] Injection complete — press INSERT in game.");
        _autoInjected = true;
        _autoInjecting = false;
    }

    // ═══════════════ DLL INJECTION ═══════════════

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, uint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, uint size, out int written);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateRemoteThread(IntPtr proc, IntPtr attr, uint stack, IntPtr start, IntPtr param, uint flags, out int threadId);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandleA(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    private async Task InjectDllAsync(string dllPath, string processName)
    {
        AppendLog("[*] Waiting for game process...");

        Process? target = null;
        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(1000);
            var procs = Process.GetProcessesByName(processName);
            if (procs.Length > 0) { target = procs[0]; break; }
        }

        if (target is null)
        {
            AppendLog("[ERROR] Game process not found after 2 minutes.");
            return;
        }

        AppendLog($"[+] Game found (PID {target.Id}). Waiting for initialization...");
        await Task.Delay(10_000);

        var hProcess = OpenProcess(0x1F0FFF, false, target.Id);
        if (hProcess == IntPtr.Zero)
        {
            AppendLog($"[ERROR] Cannot open process (error {Marshal.GetLastWin32Error()}).");
            return;
        }

        try
        {
            var pathBytes = System.Text.Encoding.ASCII.GetBytes(dllPath + "\0");
            var remoteMem = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)pathBytes.Length, 0x3000, 0x04);
            if (remoteMem == IntPtr.Zero)
            {
                AppendLog("[ERROR] Memory allocation failed.");
                return;
            }

            if (!WriteProcessMemory(hProcess, remoteMem, pathBytes, (uint)pathBytes.Length, out _))
            {
                AppendLog("[ERROR] Failed to write DLL path into game.");
                return;
            }

            var kernel32 = GetModuleHandleA("kernel32.dll");
            var loadLibAddr = GetProcAddress(kernel32, "LoadLibraryA");

            var hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, loadLibAddr, remoteMem, 0, out _);
            if (hThread == IntPtr.Zero)
            {
                AppendLog($"[ERROR] Injection failed (error {Marshal.GetLastWin32Error()}).");
                return;
            }

            WaitForSingleObject(hThread, 10000);
            CloseHandle(hThread);
            AppendLog("[OK] Menu DLL injected — press INSERT in game.");
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static string? FindFile(params string[] names)
    {
        string[] searchDirs = { EmbeddedBinDir, ExeDir, CoffinBuild };
        foreach (var dir in searchDirs)
        {
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    private static void ShowLaunchError(string msg)
    {
        MessageBox.Show(msg, "CORE X — Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) { ShowLaunchError("Update in progress — please wait."); return; }
        if (!RequireLicense()) return;

        var appId = SteamAppIdBox.Text.Trim();
        if (string.IsNullOrEmpty(appId)) { AppendLog("[ERROR] Steam App ID not set."); return; }

        var injectorPath = FindFile("PYTExample.exe");
        var dllPath = FindFile("mw2.dll", "MW2.dll");
        var fixGamePath = FindFile("FixGame.exe");
        if (injectorPath is null) { AppendLog("[ERROR] Injector (PYTExample.exe) not found."); ShowLaunchError("PYTExample.exe not found next to the loader.\nMake sure all files are in the same folder."); return; }
        if (dllPath is null) { AppendLog("[ERROR] Menu DLL (mw2.dll) not found."); ShowLaunchError("mw2.dll not found next to the loader.\nMake sure all files are in the same folder."); return; }

        _autoInjected = true;
        _autoInjecting = true;

        try
        {
            AppendLog("[*] Launching game via Steam...");
            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://rungameid/{appId}",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { _autoInjected = false; _autoInjecting = false; AppendLog($"[ERROR] Steam launch failed: {ex.Message}"); return; }

        var procName = ProcessNameBox?.Text?.Trim() ?? "cod22-cod";
        AppendLog($"[*] Waiting for game process ({procName})...");
        bool found = await Task.Run(() =>
        {
            for (int i = 0; i < 120; i++)
            {
                var procs = Process.GetProcessesByName(procName);
                if (procs.Length > 0)
                {
                    foreach (var p in procs) p.Dispose();
                    return true;
                }
                Thread.Sleep(1000);
            }
            return false;
        });
        if (!found) { _autoInjected = false; _autoInjecting = false; AppendLog("[ERROR] Game process not detected after 2 minutes."); return; }

        AppendLog("[*] Game detected — waiting 10 seconds for full load...");
        await Task.Delay(10_000);

        AppendLog("[*] Injecting DLL via kernel mapper (requesting admin)...");
        var injectorDir = Path.GetDirectoryName(injectorPath) ?? ExeDir;
        await Task.Run(() =>
        {
            try
            {
                var pyt = Process.Start(new ProcessStartInfo
                {
                    FileName = injectorPath,
                    WorkingDirectory = injectorDir,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                pyt?.WaitForExit(60_000);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"[ERROR] Injector failed: {ex.Message}"));
            }
        });
        _autoInjecting = false;
        AppendLog("[OK] Injection complete — press INSERT in game to open menu.");
    }

    private string? FindBootstrapper()
    {
        var searchRoots = new[]
        {
            @"C:\XboxGames", @"D:\XboxGames", @"E:\XboxGames", @"F:\XboxGames",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ModifiableWindowsApps"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        foreach (var root in searchRoots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (name.IndexOf("Call", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("COD", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var candidate = Path.Combine(dir, "_retail_", "bootstrapper.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
        }
        return null;
    }

    private async void LaunchXbox_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) { ShowLaunchError("Update in progress — please wait."); return; }
        if (!RequireLicense()) return;

        var injectorPath = FindFile("PYTExample.exe");
        var dllPath = FindFile("mw2.dll", "MW2.dll");
        if (injectorPath is null) { AppendLog("[ERROR] Injector (PYTExample.exe) not found."); ShowLaunchError("PYTExample.exe not found."); return; }
        if (dllPath is null) { AppendLog("[ERROR] Menu DLL (mw2.dll) not found."); ShowLaunchError("mw2.dll not found."); return; }

        _autoInjected = true;
        _autoInjecting = true;

        bool launched = false;

        // Method 1: Appx shell launch (automatic)
        try
        {
            AppendLog("[*] Launching game via Appx shell...");
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"Get-AppxPackage | Where-Object { $_.Name -like '*Activision*' } | Select-Object -First 1 -ExpandProperty PackageFamilyName\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var pkgProc = Process.Start(psi);
            string pfn = (await pkgProc!.StandardOutput.ReadToEndAsync()).Trim();
            pkgProc.WaitForExit(10_000);

            if (!string.IsNullOrEmpty(pfn))
            {
                var aumid = pfn + "!App";
                AppendLog($"[*] Found package: {pfn}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{aumid}",
                    UseShellExecute = false
                });
                launched = true;
            }
            else
            {
                AppendLog("[!] No Activision Appx package found, trying bootstrapper...");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[!] Appx launch failed: {ex.Message}");
        }

        // Method 2: _retail_\bootstrapper.exe fallback
        if (!launched)
        {
            var bootstrapper = FindBootstrapper();
            if (bootstrapper != null)
            {
                try
                {
                    AppendLog($"[*] Launching via bootstrapper: {bootstrapper}");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = bootstrapper,
                        WorkingDirectory = Path.GetDirectoryName(bootstrapper)!,
                        UseShellExecute = true
                    });
                    launched = true;
                }
                catch (Exception ex)
                {
                    AppendLog($"[!] Bootstrapper launch failed: {ex.Message}");
                }
            }
            else
            {
                AppendLog("[!] bootstrapper.exe not found, trying Xbox URI...");
            }
        }

        // Method 3: Xbox URI last resort
        if (!launched)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xbox://launch/?productId=9PGLFS9MB9CG",
                    UseShellExecute = true
                });
                launched = true;
            }
            catch (Exception ex)
            {
                _autoInjected = false; _autoInjecting = false;
                AppendLog($"[ERROR] All launch methods failed: {ex.Message}");
                return;
            }
        }

        var procName = ProcessNameBox?.Text?.Trim() ?? "cod22-cod";
        AppendLog($"[*] Waiting for game process ({procName})...");
        bool found = await Task.Run(() =>
        {
            for (int i = 0; i < 120; i++)
            {
                var procs = Process.GetProcessesByName(procName);
                if (procs.Length > 0)
                {
                    foreach (var p in procs) p.Dispose();
                    return true;
                }
                Thread.Sleep(1000);
            }
            return false;
        });
        if (!found) { _autoInjected = false; _autoInjecting = false; AppendLog("[ERROR] Game process not detected after 2 minutes."); return; }

        AppendLog("[*] Game detected — waiting 10 seconds for full load...");
        await Task.Delay(10_000);

        AppendLog("[*] Injecting DLL via kernel mapper (requesting admin)...");
        var injectorDir = Path.GetDirectoryName(injectorPath) ?? ExeDir;
        await Task.Run(() =>
        {
            try
            {
                var pyt = Process.Start(new ProcessStartInfo
                {
                    FileName = injectorPath,
                    WorkingDirectory = injectorDir,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                pyt?.WaitForExit(60_000);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"[ERROR] Injector failed: {ex.Message}"));
            }
        });
        _autoInjecting = false;
        AppendLog("[OK] Injection complete — press INSERT in game to open menu.");
    }

    private async void LaunchBattleNet_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) { ShowLaunchError("Update in progress — please wait."); return; }
        if (!RequireLicense()) return;

        var injectorPath = FindFile("PYTExample.exe");
        var dllPath = FindFile("mw2.dll", "MW2.dll");
        if (injectorPath is null) { AppendLog("[ERROR] Injector (PYTExample.exe) not found."); ShowLaunchError("PYTExample.exe not found."); return; }
        if (dllPath is null) { AppendLog("[ERROR] Menu DLL (mw2.dll) not found."); ShowLaunchError("mw2.dll not found."); return; }

        _autoInjected = true;
        _autoInjecting = true;

        bool launched = false;

        // Method 1: Battle.net URI protocol (client must be running)
        try
        {
            AppendLog("[*] Launching game via Battle.net (battlenet://AUKS)...");
            Process.Start(new ProcessStartInfo
            {
                FileName = "battlenet://AUKS",
                UseShellExecute = true
            });
            launched = true;
        }
        catch (Exception ex)
        {
            AppendLog($"[!] Battle.net URI failed: {ex.Message}");
        }

        // Method 2: bootstrapper.exe fallback
        if (!launched)
        {
            var bootstrapper = FindBootstrapper();
            if (bootstrapper != null)
            {
                try
                {
                    AppendLog($"[*] Launching via bootstrapper: {bootstrapper}");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = bootstrapper,
                        WorkingDirectory = Path.GetDirectoryName(bootstrapper)!,
                        UseShellExecute = true
                    });
                    launched = true;
                }
                catch (Exception ex)
                {
                    _autoInjected = false; _autoInjecting = false;
                    AppendLog($"[ERROR] Bootstrapper launch failed: {ex.Message}");
                    return;
                }
            }
            else
            {
                _autoInjected = false; _autoInjecting = false;
                AppendLog("[ERROR] Could not launch game. Make sure Battle.net client is running or game is installed.");
                return;
            }
        }

        var procName = ProcessNameBox?.Text?.Trim() ?? "cod22-cod";
        AppendLog($"[*] Waiting for game process ({procName})...");
        bool found = await Task.Run(() =>
        {
            for (int i = 0; i < 120; i++)
            {
                var procs = Process.GetProcessesByName(procName);
                if (procs.Length > 0)
                {
                    foreach (var p in procs) p.Dispose();
                    return true;
                }
                Thread.Sleep(1000);
            }
            return false;
        });
        if (!found) { _autoInjected = false; _autoInjecting = false; AppendLog("[ERROR] Game process not detected after 2 minutes."); return; }

        AppendLog("[*] Game detected — waiting 10 seconds for full load...");
        await Task.Delay(10_000);

        AppendLog("[*] Injecting DLL via kernel mapper (requesting admin)...");
        var injectorDir = Path.GetDirectoryName(injectorPath) ?? ExeDir;
        await Task.Run(() =>
        {
            try
            {
                var pyt = Process.Start(new ProcessStartInfo
                {
                    FileName = injectorPath,
                    WorkingDirectory = injectorDir,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                pyt?.WaitForExit(60_000);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog($"[ERROR] Injector failed: {ex.Message}"));
            }
        });
        _autoInjecting = false;
        AppendLog("[OK] Injection complete — press INSERT in game to open menu.");
    }

    private void QuickLaunch_Click(object sender, RoutedEventArgs e)
    {
        NavLoader.IsChecked = true;
    }

    private bool RequireLicense()
    {
        if (_licensed) return true;
        MessageBox.Show("You must activate a valid license key first.\nGo to the License tab to enter your key.",
            "CORE X — License Required", MessageBoxButton.OK, MessageBoxImage.Warning);
        NavLicense.IsChecked = true;
        return false;
    }

    private void AppendLog(string line)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        if (DeployLog.Text == "Ready.")
            DeployLog.Text = $"[{ts}] {line}";
        else
            DeployLog.Text += $"\n[{ts}] {line}";
        LogScroll?.ScrollToBottom();
    }

    // ═══════════════ BROWSE DIALOGS ═══════════════

    private void BrowseGameDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select game directory",
            ShowNewFolderButton = false
        };
        if (GameDirBox.Text.Trim().Length > 0 && Directory.Exists(GameDirBox.Text.Trim()))
            dlg.SelectedPath = GameDirBox.Text.Trim();
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            GameDirBox.Text = dlg.SelectedPath;
    }

    // ═══════════════ LICENSE ═══════════════

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Text.Trim();
        if (string.IsNullOrEmpty(key)) { ActivationResult.Text = "Enter a license key."; return; }

        ActivateBtn.IsEnabled = false;
        ActivationResult.Text = "Checking key...";

        try
        {
            var resp = await _http.PostAsJsonAsync($"{Api}/api/license/activate",
                new { Key = key, DeviceId });
            var json = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var success = resp.IsSuccessStatusCode;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

            if (success)
            {
                var plan = root.TryGetProperty("plan", out var p) ? p.GetString() : null;
                DateTime? expiresAt = root.TryGetProperty("expiresAt", out var ex) && ex.ValueKind != JsonValueKind.Null
                    ? ex.GetDateTime() : null;

                ActivationResult.Text = message;
                ActivationResult.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
                ApplyLicense(new LicenseResponse(true, message, plan, expiresAt), key);
            }
            else
            {
                ActivationResult.Text = message;
                ActivationResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
            }
        }
        catch (HttpRequestException)
        {
            ActivationResult.Text = "Cannot reach license server. Check API URL in Settings.";
            ActivationResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
        catch (Exception ex)
        {
            ActivationResult.Text = $"Activation error: {ex.Message}";
            ActivationResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
        finally { ActivateBtn.IsEnabled = true; }
    }

    private async Task AutoActivateAsync(string savedKey)
    {
        try
        {
            KeyBox.Text = savedKey;
            ActivationResult.Text = "Restoring license...";

            var resp = await _http.PostAsJsonAsync($"{Api}/api/license/activate",
                new { Key = savedKey, DeviceId });
            var json = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (resp.IsSuccessStatusCode)
            {
                var plan = root.TryGetProperty("plan", out var p) ? p.GetString() : null;
                DateTime? expiresAt = root.TryGetProperty("expiresAt", out var ex) && ex.ValueKind != JsonValueKind.Null
                    ? ex.GetDateTime() : null;

                ActivationResult.Text = "License restored.";
                ActivationResult.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
                ApplyLicense(new LicenseResponse(true, "License restored.", plan, expiresAt), savedKey, save: false);
                AppendLog("[OK] License auto-activated from saved key.");
            }
            else
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                ActivationResult.Text = message;
                ActivationResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
            }
        }
        catch
        {
            ActivationResult.Text = "Cannot reach license server — enter key manually.";
            ActivationResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
    }

    private void ApplyLicense(LicenseResponse r, string key, bool save = true)
    {
        _licensed = true;
        PlanText.Text      = r.Plan ?? "—";
        SubExpiryText.Text = r.ExpiresAt?.ToString("g") ?? "—";
        SubDeviceText.Text = DeviceId[..8] + "...";
        SubStatusText.Text = "Active";
        SubStatusText.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
        LicenseDot.Fill      = FindResource("SuccessBrush") as SolidColorBrush;
        LicenseStatusText.Text = $"{r.Plan} — Active";
        HomeLicenseStatus.Text = "Active";
        HomeLicenseInfo.Text   = $"{r.Plan}  expires {r.ExpiresAt:d}";
        QuickLaunchBtn.IsEnabled = true;

        if (save)
            SaveLicenseToSettings(key, r.Plan, r.ExpiresAt);

        WriteLicenseFile(r.Plan, r.ExpiresAt);
    }

    private static void WriteLicenseFile(string? plan, DateTime? expiresAt)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreX");
            Directory.CreateDirectory(dir);
            var lines = new[]
            {
                plan ?? "Unknown",
                expiresAt?.ToString("o") ?? "",
                "Active"
            };
            File.WriteAllLines(Path.Combine(dir, "license.txt"), lines);
        }
        catch { }
    }

    private void SaveLicenseToSettings(string key, string? plan, DateTime? expiry)
    {
        try
        {
            _currentSettings.LicenseKey    = key;
            _currentSettings.LicensePlan   = plan;
            _currentSettings.LicenseExpiry = expiry;

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions { WriteIndented = true });
            var encrypted = AuthGuard.Encrypt(Encoding.UTF8.GetBytes(json));
            File.WriteAllBytes(_settingsPath, encrypted);
        }
        catch { }
    }

    // ═══════════════ ADMIN ═══════════════

    private HttpRequestMessage AdminRequest(HttpMethod method, string path, HttpContent? content = null)
    {
        var msg = new HttpRequestMessage(method, $"{Api}{path}");
        msg.Headers.Add("X-Admin-User", _adminUser);
        msg.Headers.Add("X-Admin-Pass", _adminPass);
        if (content is not null) msg.Content = content;
        return msg;
    }

    private void AdminSetup_Click(object sender, RoutedEventArgs e)
    {
        var user = AdminUserBox.Text.Trim();
        var pass = AdminPassBox.Password;
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            AdminLoginResult.Text = "Enter a username and password.";
            AdminLoginResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
            return;
        }

        AdminSetupBtn.IsEnabled = false;
        try
        {
            LocalAdmin.SaveCredentials(user, pass);
            AdminLoginResult.Text = "Admin account created. Click LOGIN to continue.";
            AdminLoginResult.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
        }
        catch (Exception ex)
        {
            AdminLoginResult.Text = $"Setup failed: {ex.Message}";
            AdminLoginResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
        finally { AdminSetupBtn.IsEnabled = true; }
    }

    private async void AdminLogin_Click(object sender, RoutedEventArgs e)
    {
        var user = AdminUserBox.Text.Trim();
        var pass = AdminPassBox.Password;
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            AdminLoginResult.Text = "Enter credentials.";
            AdminLoginResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
            return;
        }

        AdminLoginBtn.IsEnabled = false;
        AdminLoginResult.Text = "Connecting to server...";
        AdminLoginResult.Foreground = FindResource("MutedBrush") as SolidColorBrush;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"{Api}/api/admin/keys");
            req.Headers.Add("X-Admin-User", user);
            req.Headers.Add("X-Admin-Pass", pass);
            var resp = await _http.SendAsync(req);

            if (resp.IsSuccessStatusCode)
            {
                _adminUser = user;
                _adminPass = pass;
                _adminAuthed = true;
                AdminLoginPanel.Visibility = Visibility.Collapsed;
                AdminDashboard.Visibility = Visibility.Visible;
                AdminLoginResult.Text = "";
                NavBuildDeploy.Visibility = Visibility.Visible;
                NavSettings.Visibility = Visibility.Visible;
            }
            else
            {
                AdminLoginResult.Text = "Invalid credentials.";
                AdminLoginResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
            }
        }
        catch (TaskCanceledException)
        {
            AdminLoginResult.Text = "Server is waking up — try again in 30 seconds.";
            AdminLoginResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
        catch (HttpRequestException)
        {
            AdminLoginResult.Text = "Cannot reach server. Check API URL in Settings.";
            AdminLoginResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
        catch (Exception ex)
        {
            AdminLoginResult.Text = $"Login failed: {ex.Message}";
            AdminLoginResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
        finally { AdminLoginBtn.IsEnabled = true; }
    }

    private string _lastGeneratedKeys = "";

    private async void AdminGenKey_Click(object sender, RoutedEventArgs e)
    {
        if (!_adminAuthed) return;

        var plan = GenPlanBox.Text.Trim();
        if (string.IsNullOrEmpty(plan)) plan = "Standard";
        if (!int.TryParse(GenDaysBox.Text.Trim(), out var days) || days <= 0)
        {
            GenKeyResult.Text = "Enter a valid number of days.";
            GenKeyResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
            return;
        }
        if (!int.TryParse(GenQtyBox.Text.Trim(), out var qty) || qty <= 0 || qty > 50)
        {
            GenKeyResult.Text = "Quantity must be 1–50.";
            GenKeyResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
            return;
        }

        GenKeyBtn.IsEnabled = false;
        GenKeyResult.Text = $"Generating {qty} key(s)...";
        CopyKeysBtn.Visibility = Visibility.Collapsed;

        try
        {
            var generated = new List<string>();
            for (int i = 0; i < qty; i++)
            {
                var req = AdminRequest(HttpMethod.Post, "/api/admin/keys",
                    new StringContent(JsonSerializer.Serialize(new { Days = days, Plan = plan }),
                        Encoding.UTF8, "application/json"));
                var resp = await _http.SendAsync(req);
                var json = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                {
                    var key = TryGetField(json, "key");
                    if (key is not null) generated.Add(key);
                }
                else
                {
                    var msg = TryGetField(json, "message") ?? "Server error.";
                    GenKeyResult.Text = $"Error: {msg}";
                    GenKeyResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
                    return;
                }
            }

            _lastGeneratedKeys = string.Join("\n", generated);
            GenKeyResult.Text = _lastGeneratedKeys;
            GenKeyResult.Foreground = FindResource("AccentGlowBrush") as SolidColorBrush;
            CopyKeysBtn.Visibility = Visibility.Visible;
        }
        catch (TaskCanceledException)
        {
            GenKeyResult.Text = "Server is waking up — try again in 30 seconds.";
            GenKeyResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
        catch (HttpRequestException)
        {
            GenKeyResult.Text = "Cannot reach server. Check API URL in Settings.";
            GenKeyResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
        catch (Exception ex)
        {
            GenKeyResult.Text = $"Error: {ex.Message}";
            GenKeyResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
        finally { GenKeyBtn.IsEnabled = true; }
    }

    private void AdminCopyKeys_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastGeneratedKeys))
        {
            System.Windows.Clipboard.SetText(_lastGeneratedKeys);
            CopyKeysBtn.Content = "COPIED!";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { CopyKeysBtn.Content = "COPY ALL"; timer.Stop(); };
            timer.Start();
        }
    }

    private async void AdminRefreshKeys_Click(object sender, RoutedEventArgs e)
    {
        if (!_adminAuthed) return;

        KeyListPanel.Children.Clear();

        try
        {
            var req = AdminRequest(HttpMethod.Get, "/api/admin/keys");
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                KeyListPanel.Children.Add(new TextBlock
                {
                    Text = TryGetField(json, "message") ?? "Server error.",
                    FontSize = 12,
                    Foreground = FindResource("ErrorBrush") as SolidColorBrush,
                    Padding = new Thickness(10, 8, 10, 8)
                });
                return;
            }

            var allKeys = JsonSerializer.Deserialize<List<ApiKeyEntry>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

            if (allKeys.Count == 0)
            {
                KeyListPanel.Children.Add(new TextBlock
                {
                    Text = "No keys found.",
                    FontSize = 12,
                    Foreground = FindResource("MutedBrush") as SolidColorBrush,
                    Padding = new Thickness(10, 8, 10, 8)
                });
                return;
            }

            foreach (var k in allKeys)
            {
                string status;
                string statusColor;
                if (!k.Activated)                              { status = "NOT ACTIVATED"; statusColor = "MutedBrush"; }
                else if (k.ExpiresAt.HasValue && k.ExpiresAt < DateTime.UtcNow)
                                                               { status = "EXPIRED";       statusColor = "ErrorBrush"; }
                else if (!string.IsNullOrEmpty(k.DeviceId))    { status = "BOUND";         statusColor = "WarningBrush"; }
                else                                           { status = "ACTIVE";        statusColor = "SuccessBrush"; }

                var row = new Border
                {
                    BorderBrush = FindResource("BorderBrush") as SolidColorBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(10, 8, 10, 8)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel();
                info.Children.Add(new TextBlock
                {
                    Text = k.Key,
                    FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono,Consolas"),
                    FontSize = 12,
                    Foreground = FindResource("TextBrush") as SolidColorBrush
                });
                var expiryText = k.Activated && k.ExpiresAt.HasValue
                    ? $"Expires: {k.ExpiresAt.Value:d}"
                    : $"{k.DurationDays} day(s) — starts on activation";
                info.Children.Add(new TextBlock
                {
                    Text = $"{k.Plan}  |  {status}  |  {expiryText}" +
                           (!string.IsNullOrEmpty(k.DeviceId) ? $"  |  HWID: {k.DeviceId[..Math.Min(8, k.DeviceId.Length)]}..." : ""),
                    FontSize = 11,
                    Foreground = FindResource(statusColor) as SolidColorBrush,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(info, 0);
                grid.Children.Add(info);

                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var copyBtn = new Button { Content = "COPY", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4, 0, 0, 0), FontSize = 11 };
                var capturedKey = k.Key;
                copyBtn.Click += (_, _) =>
                {
                    System.Windows.Clipboard.SetText(capturedKey);
                    copyBtn.Content = "COPIED!";
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    t.Tick += (_, _) => { copyBtn.Content = "COPY"; t.Stop(); };
                    t.Start();
                };
                actions.Children.Add(copyBtn);
                Grid.SetColumn(actions, 1);
                grid.Children.Add(actions);

                row.Child = grid;
                KeyListPanel.Children.Add(row);
            }
        }
        catch (Exception ex)
        {
            KeyListPanel.Children.Clear();
            KeyListPanel.Children.Add(new TextBlock
            {
                Text = $"Error: {ex.Message}",
                FontSize = 12,
                Foreground = FindResource("ErrorBrush") as SolidColorBrush,
                Padding = new Thickness(10, 8, 10, 8)
            });
        }
    }

    private async void AdminResetHwid_Click(object sender, RoutedEventArgs e)
    {
        if (!_adminAuthed) return;
        var key = ActionKeyBox.Text.Trim();
        if (string.IsNullOrEmpty(key)) { ActionKeyResult.Text = "Enter a key."; return; }

        try
        {
            var req = AdminRequest(HttpMethod.Post, $"/api/admin/keys/{Uri.EscapeDataString(key)}/reset-hwid");
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            var message = TryGetField(json, "message") ?? (resp.IsSuccessStatusCode ? "HWID reset." : "Failed.");

            ActionKeyResult.Text = message;
            ActionKeyResult.Foreground = FindResource(resp.IsSuccessStatusCode ? "SuccessBrush" : "ErrorBrush") as SolidColorBrush;
        }
        catch (HttpRequestException)
        {
            ActionKeyResult.Text = "Cannot reach server.";
            ActionKeyResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
        catch (Exception ex)
        {
            ActionKeyResult.Text = $"Error: {ex.Message}";
            ActionKeyResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
    }

    private async void AdminDeleteKey_Click(object sender, RoutedEventArgs e)
    {
        if (!_adminAuthed) return;
        var key = ActionKeyBox.Text.Trim();
        if (string.IsNullOrEmpty(key)) { ActionKeyResult.Text = "Enter a key."; return; }

        var confirm = MessageBox.Show($"Delete key:\n{key}\n\nThis cannot be undone.",
            "CORE X — Delete Key", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var req = AdminRequest(HttpMethod.Delete, $"/api/admin/keys/{Uri.EscapeDataString(key)}");
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            var message = TryGetField(json, "message") ?? (resp.IsSuccessStatusCode ? "Key deleted." : "Failed.");

            ActionKeyResult.Text = message;
            ActionKeyResult.Foreground = FindResource(resp.IsSuccessStatusCode ? "SuccessBrush" : "ErrorBrush") as SolidColorBrush;
        }
        catch (HttpRequestException)
        {
            ActionKeyResult.Text = "Cannot reach server.";
            ActionKeyResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
        catch (Exception ex)
        {
            ActionKeyResult.Text = $"Error: {ex.Message}";
            ActionKeyResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
        }
    }

    private static string? TryGetMessage(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString();
        }
        catch { }
        return null;
    }

    private static string? TryGetField(string json, string field)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(field, out var m)) return m.GetString();
        }
        catch { }
        return null;
    }

    // ═══════════════ SETTINGS ═══════════════

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);

            _currentSettings.GameDirectory      = GameDirBox.Text.Trim();
            _currentSettings.SteamAppId         = SteamAppIdBox.Text.Trim();
            _currentSettings.ProcessName        = ProcessNameBox.Text.Trim();
            _currentSettings.DebugLogPath       = DebugLogBox.Text.Trim();
            _currentSettings.ApiEndpoint        = ApiBox.Text.Trim();
            _currentSettings.CheatProjectPath   = CheatProjectBox.Text.Trim();
            _currentSettings.MsBuildPath        = MsBuildPathBox.Text.Trim();
            _currentSettings.BuildConfiguration = BuildConfigBox.Text.Trim();
            _currentSettings.BuildPlatform      = BuildPlatformBox.Text.Trim();

            var json = JsonSerializer.Serialize(_currentSettings, new JsonSerializerOptions { WriteIndented = true });
            var encrypted = AuthGuard.Encrypt(Encoding.UTF8.GetBytes(json));
            File.WriteAllBytes(_settingsPath, encrypted);

            AppendLog("[OK] Settings saved.");
            MessageBox.Show("Settings saved.", "CORE X", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save: {ex.Message}", "CORE X", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadSettings()
    {
        var defaults = new AppSettings
        {
            GameDirectory      = @"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare II",
            SourceDll          = DefaultSourceDll,
            DeployFilename     = DefaultDeployName,
            SteamAppId         = "3595230",
            ProcessName        = "cod22-cod",
            DebugLogPath       = @"C:\Users\Germani Rosario\Desktop\cxdebug.txt",
            ApiEndpoint        = "https://corex-api-5tdf.onrender.com",
            ProxyDll           = DefaultProxyDll,
            CheatProjectPath   = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @"Desktop\coffin\coffin\mw2\mw2 working example\MW2Project\MW2Project.vcxproj"),
            MsBuildPath        = @"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",
            BuildConfiguration = "Release",
            BuildPlatform      = "x64"
        };

        try
        {
            if (File.Exists(_settingsPath))
            {
                string json;
                var raw = File.ReadAllBytes(_settingsPath);
                try
                {
                    var decrypted = AuthGuard.Decrypt(raw);
                    json = Encoding.UTF8.GetString(decrypted);
                }
                catch
                {
                    json = Encoding.UTF8.GetString(raw);
                }
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null)
                {
                    defaults = loaded with
                    {
                        CheatProjectPath   = loaded.CheatProjectPath   ?? defaults.CheatProjectPath,
                        MsBuildPath        = loaded.MsBuildPath        ?? defaults.MsBuildPath,
                        BuildConfiguration = loaded.BuildConfiguration ?? defaults.BuildConfiguration,
                        BuildPlatform      = loaded.BuildPlatform      ?? defaults.BuildPlatform,
                        SourceDll          = loaded.SourceDll          ?? defaults.SourceDll,
                        DeployFilename     = loaded.DeployFilename     ?? defaults.DeployFilename,
                        ProxyDll           = loaded.ProxyDll           ?? defaults.ProxyDll
                    };
                }
            }
        }
        catch { }

        _currentSettings = defaults;

        GameDirBox.Text       = defaults.GameDirectory ?? "";
        SteamAppIdBox.Text    = defaults.SteamAppId ?? "3595230";
        ProcessNameBox.Text   = defaults.ProcessName ?? "cod22-cod";
        DebugLogBox.Text      = defaults.DebugLogPath ?? "";
        ApiBox.Text           = defaults.ApiEndpoint ?? "https://corex-api-5tdf.onrender.com";
        KeyBox.Text           = defaults.LicenseKey ?? "";
        CheatProjectBox.Text  = defaults.CheatProjectPath ?? "";
        MsBuildPathBox.Text   = defaults.MsBuildPath ?? "";
        BuildConfigBox.Text   = defaults.BuildConfiguration ?? "Debug";
        BuildPlatformBox.Text = defaults.BuildPlatform ?? "x64";
    }

    private sealed record LicenseResponse(bool Success, string Message, string? Plan, DateTime? ExpiresAt);
    private sealed record AppSettings
    {
        public string? GameDirectory      { get; set; }
        public string? SourceDll          { get; set; }
        public string? DeployFilename     { get; set; }
        public string? SteamAppId         { get; set; }
        public string? ProcessName        { get; set; }
        public string? DebugLogPath       { get; set; }
        public string? ApiEndpoint        { get; set; }
        public string? ProxyDll           { get; set; }
        public string? LicenseKey         { get; set; }
        public string? LicensePlan        { get; set; }
        public DateTime? LicenseExpiry    { get; set; }
        public string? CheatProjectPath   { get; set; }
        public string? MsBuildPath        { get; set; }
        public string? BuildConfiguration { get; set; }
        public string? BuildPlatform      { get; set; }
    }
}

static class DeviceIdentity
{
    private static readonly string PathName =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CoreX", "device.id");

    public static string Get()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        if (File.Exists(PathName)) return File.ReadAllText(PathName).Trim();
        var id = Guid.NewGuid().ToString("N");
        File.WriteAllText(PathName, id);
        return id;
    }
}

static class LocalAdmin
{
    private static readonly string CredPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreX", "admin.dat");

    public static void SaveCredentials(string user, string pass)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CredPath)!);
        var hash = ComputeHash(user, pass);
        var data = AuthGuard.Encrypt(Encoding.UTF8.GetBytes($"{user}\n{hash}"));
        File.WriteAllBytes(CredPath, data);
    }

    public static bool Verify(string user, string pass)
    {
        if (!File.Exists(CredPath)) return false;
        var data = AuthGuard.Decrypt(File.ReadAllBytes(CredPath));
        var lines = Encoding.UTF8.GetString(data).Split('\n');
        if (lines.Length < 2) return false;
        return lines[0] == user && lines[1] == ComputeHash(user, pass);
    }

    public static bool HasAccount() => File.Exists(CredPath);

    private static string ComputeHash(string user, string pass)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes($"CXadm:{user}:{pass}:salt_9Xk2"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

sealed record LicenseKeyEntry
{
    public string Key { get; set; } = "";
    public string Plan { get; set; } = "Standard";
    public int DurationDays { get; set; }
    public bool Activated { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? DeviceId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

sealed record ApiKeyEntry
{
    public string Key { get; set; } = "";
    public string Plan { get; set; } = "Standard";
    public int DurationDays { get; set; }
    public bool Activated { get; set; }
    public bool Expired { get; set; }
    public bool Bound { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string? DeviceId { get; set; }
}

sealed record ActivationResult(bool Success, string Message, string? Plan = null, DateTime? ExpiresAt = null);

static class LocalKeyStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreX", "keys.dat");

    private static List<LicenseKeyEntry> Load()
    {
        if (!File.Exists(StorePath)) return new List<LicenseKeyEntry>();
        try
        {
            var decrypted = AuthGuard.Decrypt(File.ReadAllBytes(StorePath));
            var json = Encoding.UTF8.GetString(decrypted);
            return JsonSerializer.Deserialize<List<LicenseKeyEntry>>(json) ?? new List<LicenseKeyEntry>();
        }
        catch { return new List<LicenseKeyEntry>(); }
    }

    private static void Save(List<LicenseKeyEntry> keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var json = JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true });
        var encrypted = AuthGuard.Encrypt(Encoding.UTF8.GetBytes(json));
        File.WriteAllBytes(StorePath, encrypted);
    }

    public static string GenerateKey(string plan, int days)
    {
        var keys = Load();
        var key = $"CX-{RandomSegment()}-{RandomSegment()}-{RandomSegment()}-{RandomSegment()}";
        keys.Add(new LicenseKeyEntry { Key = key, Plan = plan, DurationDays = days });
        Save(keys);
        return key;
    }

    public static ActivationResult Activate(string key, string deviceId)
    {
        var keys = Load();
        var entry = keys.Find(k => k.Key == key);
        if (entry is null)
            return new ActivationResult(false, "Invalid license key.");

        if (entry.Activated && entry.ExpiresAt.HasValue && entry.ExpiresAt < DateTime.UtcNow)
            return new ActivationResult(false, "License key has expired.");

        if (!string.IsNullOrEmpty(entry.DeviceId) && entry.DeviceId != deviceId)
            return new ActivationResult(false, "Key is bound to a different device. Contact admin to reset HWID.");

        if (!entry.Activated)
        {
            entry.Activated = true;
            entry.ExpiresAt = DateTime.UtcNow.AddDays(entry.DurationDays);
            entry.DeviceId = deviceId;
            Save(keys);
        }
        else if (string.IsNullOrEmpty(entry.DeviceId))
        {
            entry.DeviceId = deviceId;
            Save(keys);
        }

        return new ActivationResult(true, "License activated.", entry.Plan, entry.ExpiresAt);
    }

    public static List<LicenseKeyEntry> GetAll() => Load();

    public static bool ResetHwid(string key)
    {
        var keys = Load();
        var entry = keys.Find(k => k.Key == key);
        if (entry is null) return false;
        entry.DeviceId = null;
        Save(keys);
        return true;
    }

    public static bool DeleteKey(string key)
    {
        var keys = Load();
        var removed = keys.RemoveAll(k => k.Key == key);
        if (removed == 0) return false;
        Save(keys);
        return true;
    }

    private static string RandomSegment()
    {
        var bytes = new byte[4];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToHexString(bytes).ToUpperInvariant();
    }
}

static class AuthGuard
{
    private static readonly byte[] _ek = { 0xE4, 0x64, 0x9C, 0xBD, 0xBC, 0x6C, 0xE4, 0xB7, 0x64, 0xCD, 0x7C, 0x1C, 0xAA, 0x97, 0x12, 0xC4, 0xB8, 0x51, 0xF3, 0x53, 0x8D, 0x78, 0xC2, 0x44 };
    private static readonly byte[] _em = { 0xA7, 0x3C, 0xF1, 0x8E, 0xD2, 0x19, 0xBB, 0xC4, 0x55, 0xAA, 0x12, 0x77, 0x99, 0xEE, 0x4D, 0xF6, 0x88, 0x63, 0xC7, 0x2B, 0xDF, 0x41, 0xB3, 0x1E };
    private static readonly byte[] _rk = { 0xF7, 0x4F, 0xA0, 0xE2, 0x59, 0xD9, 0x7B, 0x6B, 0x4D, 0xFF, 0xF3, 0x4E, 0xF5, 0x49, 0xB9, 0x45 };
    private static readonly byte[] _rm = { 0x37, 0x91, 0x5A, 0x2C, 0xE3, 0x74, 0x8B, 0x66, 0x93, 0x52, 0x4D, 0xA1, 0x3F, 0xB7, 0x69, 0x48 };

    private static byte[] HmacSecret
    {
        get
        {
            var r = new byte[_ek.Length];
            for (int i = 0; i < r.Length; i++) r[i] = (byte)(_ek[i] ^ _em[i]);
            return r;
        }
    }

    private static byte[] ResourceXorKey
    {
        get
        {
            var r = new byte[_rk.Length];
            for (int i = 0; i < r.Length; i++) r[i] = (byte)(_rk[i] ^ _rm[i]);
            return r;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool IsDebuggerPresent();

    [DllImport("kernel32.dll")]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, out bool isDebuggerPresent);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle, int processInformationClass,
        out IntPtr processInformation, int processInformationLength, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationThread(
        IntPtr threadHandle, int threadInformationClass,
        IntPtr threadInformation, int threadInformationLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern bool VirtualProtect(
        IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    public static bool IsDebuggerAttached()
    {
        if (Debugger.IsAttached) return true;
        if (IsDebuggerPresent()) return true;
        try
        {
            CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, out var remote);
            if (remote) return true;
        }
        catch { }
        try
        {
            NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0x1F,
                out var info, IntPtr.Size, out _);
            if (info != IntPtr.Zero) return true;
        }
        catch { }
        try
        {
            NtQueryInformationProcess(Process.GetCurrentProcess().Handle, 0x1E,
                out var dbgObj, IntPtr.Size, out _);
            if (dbgObj != IntPtr.Zero) return true;
        }
        catch { }
        return false;
    }

    public static bool ScanForTools()
    {
        var tools = new[]
        {
            new[]{(char)0x64,(char)0x6E,(char)0x73,(char)0x70,(char)0x79},
            new[]{(char)0x69,(char)0x6C,(char)0x73,(char)0x70,(char)0x79},
            new[]{(char)0x64,(char)0x6F,(char)0x74,(char)0x70,(char)0x65,(char)0x65,(char)0x6B},
            new[]{(char)0x64,(char)0x65,(char)0x34,(char)0x64,(char)0x6F,(char)0x74},
            new[]{(char)0x6D,(char)0x65,(char)0x67,(char)0x61,(char)0x64,(char)0x75,(char)0x6D,(char)0x70},
            new[]{(char)0x65,(char)0x78,(char)0x74,(char)0x72,(char)0x65,(char)0x6D,(char)0x65,(char)0x64,(char)0x75,(char)0x6D,(char)0x70},
            new[]{(char)0x78,(char)0x36,(char)0x34,(char)0x64,(char)0x62,(char)0x67},
            new[]{(char)0x78,(char)0x33,(char)0x32,(char)0x64,(char)0x62,(char)0x67},
            new[]{(char)0x77,(char)0x69,(char)0x6E,(char)0x64,(char)0x62,(char)0x67},
            new[]{(char)0x6F,(char)0x6C,(char)0x6C,(char)0x79,(char)0x64,(char)0x62,(char)0x67},
            new[]{(char)0x69,(char)0x64,(char)0x61,(char)0x36,(char)0x34},
            new[]{(char)0x63,(char)0x68,(char)0x65,(char)0x61,(char)0x74,(char)0x65,(char)0x6E,(char)0x67,(char)0x69,(char)0x6E,(char)0x65},
            new[]{(char)0x70,(char)0x72,(char)0x6F,(char)0x63,(char)0x65,(char)0x73,(char)0x73,(char)0x68,(char)0x61,(char)0x63,(char)0x6B,(char)0x65,(char)0x72},
            new[]{(char)0x66,(char)0x69,(char)0x64,(char)0x64,(char)0x6C,(char)0x65,(char)0x72},
            new[]{(char)0x77,(char)0x69,(char)0x72,(char)0x65,(char)0x73,(char)0x68,(char)0x61,(char)0x72,(char)0x6B},
            new[]{(char)0x68,(char)0x78,(char)0x64},
            new[]{(char)0x70,(char)0x72,(char)0x6F,(char)0x63,(char)0x6D,(char)0x6F,(char)0x6E},
            new[]{(char)0x72,(char)0x65,(char)0x73,(char)0x68,(char)0x61,(char)0x63,(char)0x6B,(char)0x65,(char)0x72},
        };
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var name = proc.ProcessName.ToLowerInvariant();
                    foreach (var tool in tools)
                        if (name.Contains(new string(tool))) return true;
                }
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }
        return false;
    }

    public static void HideThread()
    {
        try { NtSetInformationThread(GetCurrentThread(), 0x11, IntPtr.Zero, 0); } catch { }
    }

    public static void ErasePEHeader()
    {
        try
        {
            var mod = Process.GetCurrentProcess().MainModule;
            if (mod == null) return;
            var ba = mod.BaseAddress;
            if (VirtualProtect(ba, (UIntPtr)4096, 0x40, out var old))
            {
                Marshal.Copy(new byte[512], 0, ba, 512);
                VirtualProtect(ba, (UIntPtr)4096, old, out _);
            }
        }
        catch { }
    }

    public static bool TimingCheck()
    {
        var sw = Stopwatch.StartNew();
        int x = 0;
        for (int i = 0; i < 1000; i++) x += i;
        sw.Stop();
        return sw.ElapsedMilliseconds > 100;
    }

    public static byte[] DecryptResource(Stream encrypted)
    {
        var key = ResourceXorKey;
        using var ms = new MemoryStream();
        encrypted.CopyTo(ms);
        var data = ms.ToArray();
        for (int i = 0; i < data.Length; i++)
            data[i] ^= key[i % key.Length];
        return data;
    }

    public static void InitProtection()
    {
        HideThread();
        if (ScanForTools() || TimingCheck())
        {
            Thread.Sleep(new Random().Next(800, 2500));
            Environment.Exit(1);
        }
    }

    public static string ComputeHmac(string payload)
    {
        using var hmac = new HMACSHA256(HmacSecret);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<HttpResponseMessage> SignedPostAsync<T>(
        HttpClient client, string url, T body)
    {
        var json = JsonSerializer.Serialize(body);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeHmac(json + timestamp);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-CX-Timestamp", timestamp);
        request.Headers.Add("X-CX-Signature", signature);
        request.Headers.Add("X-CX-Device", DeviceIdentity.Get());

        return await client.SendAsync(request);
    }

    public static byte[] Encrypt(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(HmacSecret);
        aes.GenerateIV();

        using var ms = new MemoryStream();
        ms.Write(aes.IV, 0, aes.IV.Length);
        using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            cs.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    public static byte[] Decrypt(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = SHA256.HashData(HmacSecret);
        var iv = new byte[16];
        Array.Copy(data, 0, iv, 0, 16);
        aes.IV = iv;

        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
            cs.Write(data, 16, data.Length - 16);
        return ms.ToArray();
    }
}
