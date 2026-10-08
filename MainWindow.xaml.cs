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
using System.Threading;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace CoreX.Loader;

public partial class MainWindow : Window
{
    private const string AppVersion = "3.5.1";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly HttpClient _fastHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _pollTimer;
    private readonly string _settingsPath;
    private string DeviceId { get; } = DeviceIdentity.Get();
    private bool _licensed;
    private bool _adminAuthed;
    private List<ApiKeyEntry> _cachedKeys = new();
    private bool _autoInjected;
    private bool _autoInjecting;
    private bool _updating;
    private readonly SemaphoreSlim _injectionLock = new(1, 1);
    private static Mutex? _singleInstanceMutex;
    private string _adminUser = "";
    private string _adminPass = "";
    private AppSettings _currentSettings = new();

    private static readonly string ExeDir = AppContext.BaseDirectory;
    private static readonly string CoffinBuild = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        @"Desktop\PremiumCoreX\mw2\mw2 working example\Build");
    private static readonly string DefaultSourceDll  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreX", "bin", "mw2.dll");
    private static readonly string EmbeddedBinDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreX", "bin");
    private static readonly string DefaultProxyDll   = Path.Combine(EmbeddedBinDir, "version.dll");
    private static readonly string DefaultStringTableDll = Path.Combine(ExeDir, "stringtable.dll");
    private static readonly string PayloadCacheDir   = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreX", "payload");
    private static readonly string CachedPayloadPath = Path.Combine(PayloadCacheDir, "cxpayload.dll");
    private const string DefaultDeployName = "cxpayload.dll";

    private static readonly string[] EmbeddedPayloads = { "mw2.dll", "PYTExample.exe", "SecureEngineSDK64.dll", "FixGame.exe", "version.dll", "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" };

    private static readonly List<string> _extractionWarnings = new();

    private static void ExtractEmbeddedPayloads()
    {
        Directory.CreateDirectory(EmbeddedBinDir);
        try
        {
            var binDir = new DirectoryInfo(EmbeddedBinDir);
            binDir.Attributes |= FileAttributes.Hidden;
            if (binDir.Parent != null)
                binDir.Parent.Attributes |= FileAttributes.Hidden;
        } catch { }
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in EmbeddedPayloads)
        {
            var dest = Path.Combine(EmbeddedBinDir, name);
            try
            {
                if (File.Exists(dest))
                {
                    try { File.SetAttributes(dest, FileAttributes.Normal); } catch { }
                    try { File.Delete(dest); } catch { }
                }
                using var stream = asm.GetManifestResourceStream(name);
                if (stream is null) continue;
                using var fs = File.Create(dest);
                stream.CopyTo(fs);
            }
            catch (Exception ex)
            {
                _extractionWarnings.Add($"{name}: {ex.Message}");
            }
            try { File.SetAttributes(dest, File.GetAttributes(dest) | FileAttributes.Hidden | FileAttributes.System); } catch { }
        }

        var critical = new[] { "mw2.dll", "PYTExample.exe" };
        var missing = critical.Where(f => !File.Exists(Path.Combine(EmbeddedBinDir, f))).ToList();
        if (missing.Count > 0)
        {
            var files = string.Join(", ", missing);
            _extractionWarnings.Add($"BLOCKED: {files}");
        }
    }

    private string SourceDll    => _currentSettings.SourceDll ?? DefaultSourceDll;
    private string ProxyDll     => _currentSettings.ProxyDll ?? DefaultProxyDll;
    private string DeployName   => _currentSettings.DeployFilename ?? DefaultDeployName;
    private string Api          => ApiBox.Text.TrimEnd('/');

    public MainWindow()
    {
        bool createdNew = true;
        try
        {
            _singleInstanceMutex = new Mutex(true, @"Local\CoreXLoaderSingleInstance", out createdNew);
        }
        catch
        {
            _singleInstanceMutex = null;
        }
        if (!createdNew)
        {
            MessageBox.Show("CoreX Loader is already running.", "CORE X", MessageBoxButton.OK, MessageBoxImage.Warning);
            Environment.Exit(0);
            return;
        }

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
        VersionLabel.Text = $"v{AppVersion}";

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

        if (_extractionWarnings.Count > 0)
        {
            foreach (var w in _extractionWarnings)
                AppendLog($"[WARNING] File extraction issue: {w}");

            if (_extractionWarnings.Any(w => w.Contains("BLOCKED")))
            {
                AppendLog("[!] Windows Defender or antivirus is blocking critical files.");
                AppendLog("[FIX] Add this folder to Defender exclusions:");
                AppendLog($"      {EmbeddedBinDir}");
                AppendLog("[FIX] Windows Security > Virus & Threat Protection > Manage Settings > Exclusions > Add Folder");
            }
        }

        if (!Environment.GetCommandLineArgs().Contains("--skip-update"))
            _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            if (FindGameProcess() != null)
            {
                AppendLog("[UPDATE] Game is running — skipping auto-update.");
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
                if (FindGameProcess() != null)
                {
                    AppendLog($"[UPDATE] v{version} available but game is running — update skipped.");
                    return;
                }
                _updating = true;
                ShowUpdateOverlay($"Updating to v{version}...");
                await DownloadUpdateAsync(url);
                HideUpdateOverlay();
                _updating = false;
            }
        }
        catch
        {
            HideUpdateOverlay();
            _updating = false;
        }
    }

    private void ShowUpdateOverlay(string message)
    {
        UpdateOverlay.Visibility = Visibility.Visible;
        UpdateMessage.Text = message;
        UpdateSubMessage.Text = "Do not close the loader. All features are locked until the update completes.";
        VersionLabel.Text = message;
    }

    private void HideUpdateOverlay()
    {
        UpdateOverlay.Visibility = Visibility.Collapsed;
        VersionLabel.Text = $"v{AppVersion}";
    }

    private async Task DownloadUpdateAsync(string url)
    {
        try
        {
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (currentExe is null) return;

            var updatePath = currentExe + ".update";

            Dispatcher.Invoke(() => UpdateMessage.Text = "Downloading update... Please wait.");

            using var dlClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await dlClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            await using var fs = File.Create(updatePath);
            await response.Content.CopyToAsync(fs);
            fs.Close();

            Dispatcher.Invoke(() =>
            {
                UpdateMessage.Text = "Installing update... Restarting loader.";
                UpdateSubMessage.Text = "The loader will restart automatically.";
            });

            var batchPath = Path.Combine(Path.GetTempPath(), "corex_update.cmd");
            File.WriteAllText(batchPath,
                "@echo off\r\n" +
                "setlocal\r\n" +
                "set retries=0\r\n" +
                ":retry\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                $"move /Y \"{updatePath}\" \"{currentExe}\" >nul 2>&1\r\n" +
                "if errorlevel 1 (\r\n" +
                "  set /a retries+=1\r\n" +
                "  if %retries% lss 10 goto retry\r\n" +
                $"  del \"{updatePath}\" >nul 2>&1\r\n" +
                $"  start \"\" \"{currentExe}\" --skip-update\r\n" +
                "  del \"%~f0\"\r\n" +
                "  exit /b\r\n" +
                ")\r\n" +
                $"start \"\" \"{currentExe}\" --skip-update\r\n" +
                "del \"%~f0\"");

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
            Dispatcher.Invoke(() => HideUpdateOverlay());
            MessageBox.Show($"Update failed: {ex.Message}", "Update Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DisableDebugFlag();
        Close();
    }

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
        try
        {
            string? foundName = FindGameProcess();
            if (foundName != null)
            {
                var procs = Process.GetProcessesByName(foundName);
                int pid = procs.Length > 0 ? procs[0].Id : 0;
                foreach (var p in procs) p.Dispose();

                GameDot.Fill = FindResource("SuccessBrush") as SolidColorBrush;
                GameStatusText.Text = "Game running";
                HomeGameStatus.Text = "Running";
                HomeGamePID.Text = pid > 0 ? $"PID: {pid}" : "PID: —";

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
        var procName = FindGameProcess();
        if (procName == null) return true;
        var procs = Process.GetProcessesByName(procName);
        foreach (var p in procs) p.Dispose();
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
        AppendLog("[INFO] Proxy DLL disabled — kernel mapper handles injection.");
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

    // ═══════════════ SYSTEM SCAN ═══════════════

    private static readonly (string process, string label)[] ConflictProcesses = new[]
    {
        ("cheatengine",       "Cheat Engine"),
        ("x64dbg",            "x64dbg Debugger"),
        ("x32dbg",            "x32dbg Debugger"),
        ("ProcessHacker",     "Process Hacker"),
        ("SystemInformer",    "System Informer"),
        ("ida64",             "IDA Pro"),
        ("HxD",               "HxD Hex Editor"),
        ("ReClass",           "ReClass"),
        ("GameBar",           "Xbox Game Bar"),
        ("GameBarPresenceWriter", "Xbox Game Bar Presence"),
        ("GameBarFTServer",   "Xbox Game DVR"),
        ("XboxApp",           "Xbox App"),
        ("EasyAntiCheat",     "EasyAntiCheat"),
        ("BEService",         "BattlEye Service"),
        ("vgc",               "Vanguard Anti-Cheat (Riot)"),
        ("faceit",            "FACEIT Anti-Cheat"),
        ("ESEA",              "ESEA Client"),
        ("wallpaper64",       "Wallpaper Engine"),
        ("wallpaper32",       "Wallpaper Engine"),
        ("obs64",             "OBS Studio"),
        ("obs32",             "OBS Studio"),
        ("StreamlabsOBS",     "Streamlabs OBS"),
    };

    private async void RunSystemScan_Click(object sender, RoutedEventArgs e)
    {
        ScanBtn.IsEnabled = false;
        ScanBtn.Content = "SCANNING...";
        ScanResultsPanel.Children.Clear();
        ScanResultsBorder.Visibility = Visibility.Visible;

        var procName = FindGameProcess();
        int passCount = 0, warnCount = 0, failCount = 0;

        // 1. Admin privileges
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                AddScanResult("PASS", "Administrator Privileges", "Running as admin.", null);
                passCount++;
            }
            else
            {
                AddScanResult("FAIL", "Administrator Privileges", "Not running as admin — kernel mapper will fail.",
                    "Right-click CoreX.Loader.exe → Run as administrator");
                failCount++;
            }
        }
        catch
        {
            AddScanResult("WARN", "Administrator Privileges", "Could not determine admin status.",
                "Try running as administrator");
            warnCount++;
        }

        // 2. Game process
        try
        {
            if (procName != null)
            {
                var procs = Process.GetProcessesByName(procName);
                if (procs.Length > 0)
                {
                    AddScanResult("PASS", "Game Process", $"{procName} detected (PID: {procs[0].Id})", null);
                    passCount++;
                    foreach (var p in procs) p.Dispose();
                }
                else
                {
                    AddScanResult("INFO", "Game Process", "Game is not running.",
                        "Launch the game first, or use the Loader tab launch buttons");
                    warnCount++;
                }
            }
            else
            {
                AddScanResult("INFO", "Game Process", "Game is not running.",
                    "Launch the game first, or use the Loader tab launch buttons");
                warnCount++;
            }
        }
        catch
        {
            AddScanResult("INFO", "Game Process", "Could not check game process.", null);
            warnCount++;
        }

        // 3. Overlay conflicts (only if game is running)
        try
        {
            var overlays = DetectOverlays(procName);
            if (overlays.Count == 0)
            {
                AddScanResult("PASS", "Overlay Conflicts", "No conflicting overlays detected.", null);
                passCount++;
            }
            else
            {
                foreach (var ov in overlays)
                {
                    string fix = ov switch
                    {
                        "NVIDIA Overlay (ShadowPlay)" or "NVIDIA Overlay (Camera)" =>
                            "Open GeForce Experience → Settings → General → disable In-Game Overlay",
                        "Discord Overlay" =>
                            "Open Discord → Settings → Game Overlay → disable overlay",
                        "MSI Afterburner / RivaTuner" =>
                            "Close MSI Afterburner and RivaTuner Statistics Server",
                        "Steam Overlay" =>
                            "Steam → Settings → In-Game → uncheck Enable Steam Overlay",
                        "Xbox Game Bar Overlay" or "Xbox Game Bar SDK" =>
                            "Windows Settings → Gaming → Xbox Game Bar → turn Off",
                        "OBS Game Capture" =>
                            "Close OBS or disable Game Capture source",
                        "Fraps" =>
                            "Close Fraps before launching",
                        "Medal.tv Overlay" =>
                            "Close Medal.tv or disable overlay in its settings",
                        "Battle.net Helper" =>
                            "Close Battle.net client before injecting — it loads a helper overlay",
                        _ => "Disable this overlay before injecting"
                    };
                    AddScanResult("FAIL", $"Overlay: {ov}", "Will conflict with CoreX rendering and cause crashes.", fix);
                    failCount++;
                }
            }
        }
        catch { }

        // 4. Required files
        try
        {
            var injector = FindFile("PYTExample.exe");
            if (injector != null)
            {
                AddScanResult("PASS", "Injector (PYTExample.exe)", "Found.", null);
                passCount++;
            }
            else
            {
                AddScanResult("FAIL", "Injector (PYTExample.exe)", "Missing — injection will fail.",
                    "Re-download CoreX.Loader.exe from the latest release");
                failCount++;
            }

            var payload = FindFile("mw2.dll", "cxpayload.dll");
            if (payload != null || File.Exists(CachedPayloadPath))
            {
                AddScanResult("PASS", "Payload DLL", "Found.", null);
                passCount++;
            }
            else
            {
                AddScanResult("FAIL", "Payload DLL", "Missing — nothing to inject.",
                    "Activate your license key first, or re-download the loader");
                failCount++;
            }
        }
        catch { }

        // 5. Conflicting software
        try
        {
            foreach (var (proc, label) in ConflictProcesses)
            {
                var found = Process.GetProcessesByName(proc);
                if (found.Length > 0)
                {
                    AddScanResult("WARN", $"Conflict: {label}", "Running — may trigger anti-cheat or interfere with injection.",
                        $"Close {label} before launching the game");
                    warnCount++;
                    foreach (var p in found) p.Dispose();
                }
            }
        }
        catch { }

        // 6. Windows Defender / AV exclusion check
        try
        {
            bool defenderRunning = Process.GetProcessesByName("MsMpEng").Length > 0;
            if (defenderRunning)
            {
                bool exclusionExists = false;
                try
                {
                    string binDir = EmbeddedBinDir.TrimEnd(System.IO.Path.DirectorySeparatorChar);
                    string parentDir = System.IO.Path.GetDirectoryName(binDir) ?? "";
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -Command \"$ep = (Get-MpPreference).ExclusionPath; ($ep -contains '{binDir}') -or ($ep -contains '{parentDir}')\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd().Trim();
                        proc.WaitForExit(5_000);
                        exclusionExists = output.Equals("True", StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch { }

                if (exclusionExists)
                {
                    AddScanResult("PASS", "Windows Defender", "Exclusion is set for CoreX folder.", null);
                    passCount++;
                }
                else
                {
                    AddScanResult("WARN", "Windows Defender", "Real-time protection is active — may quarantine CoreX files.",
                        "Add an exclusion: Windows Security → Virus & Threat Protection → Manage settings → Exclusions → Add the CoreX folder");
                    warnCount++;
                }
            }
            else
            {
                AddScanResult("PASS", "Antivirus", "No active interference detected.", null);
                passCount++;
            }
        }
        catch { }

        // 7. Memory check
        try
        {
            var memStatus = new NativeMemoryStatus();
            if (GlobalMemoryStatusEx(memStatus))
            {
                long availMB = (long)(memStatus.ullAvailPhys / (1024 * 1024));
                if (availMB < 2048)
                {
                    AddScanResult("WARN", "Available Memory", $"Only {availMB} MB free — may cause instability.",
                        "Close other applications to free up RAM");
                    warnCount++;
                }
                else
                {
                    AddScanResult("PASS", "Available Memory", $"{availMB} MB free.", null);
                    passCount++;
                }
            }
        }
        catch { }

        // 8. Xbox Game Bar (critical for Xbox PC users)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"(Get-AppxPackage Microsoft.XboxGamingOverlay -ErrorAction SilentlyContinue) -ne $null\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var pkgProc = Process.Start(psi);
            if (pkgProc != null)
            {
                var output = (await pkgProc.StandardOutput.ReadToEndAsync()).Trim();
                pkgProc.WaitForExit(10_000);
                bool gameBarInstalled = output.Equals("True", StringComparison.OrdinalIgnoreCase);
                bool gameBarRunning = Process.GetProcessesByName("GameBar").Length > 0 ||
                                      Process.GetProcessesByName("GameBarPresenceWriter").Length > 0;
                if (gameBarRunning)
                {
                    AddScanResult("WARN", "Xbox Game Bar", "Game Bar is running — can interfere with kernel injection on Xbox/Battle.net.",
                        "Windows Settings → Gaming → Xbox Game Bar → turn Off, then restart PC");
                    warnCount++;
                }
                else if (gameBarInstalled)
                {
                    AddScanResult("INFO", "Xbox Game Bar", "Installed but not running. Disable it if injection crashes.",
                        "Windows Settings → Gaming → Xbox Game Bar → turn Off");
                    warnCount++;
                }
                else
                {
                    AddScanResult("PASS", "Xbox Game Bar", "Not installed.", null);
                    passCount++;
                }
            }
        }
        catch { }

        // 9. Hypervisor / virtualization check (can block kernel mapper)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"(Get-CimInstance Win32_ComputerSystem).HypervisorPresent\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var hvProc = Process.Start(psi);
            if (hvProc != null)
            {
                var output = (await hvProc.StandardOutput.ReadToEndAsync()).Trim();
                hvProc.WaitForExit(10_000);
                if (output.Equals("True", StringComparison.OrdinalIgnoreCase))
                {
                    AddScanResult("WARN", "Hypervisor Active", "A hypervisor is running (Hyper-V, VBS, or VM) — may block kernel mapper.",
                        "Disable Hyper-V: Settings → Apps → Optional Features → More Windows Features → uncheck Hyper-V, then restart");
                    warnCount++;
                }
                else
                {
                    AddScanResult("PASS", "Hypervisor", "No hypervisor detected.", null);
                    passCount++;
                }
            }
        }
        catch { }

        // 10. Core isolation / memory integrity (blocks unsigned drivers)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"(Get-ItemProperty -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\DeviceGuard\\Scenarios\\HypervisorEnforcedCodeIntegrity' -Name 'Enabled' -ErrorAction SilentlyContinue).Enabled\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var ciProc = Process.Start(psi);
            if (ciProc != null)
            {
                var output = (await ciProc.StandardOutput.ReadToEndAsync()).Trim();
                ciProc.WaitForExit(10_000);
                if (output == "1")
                {
                    AddScanResult("FAIL", "Memory Integrity (HVCI)", "Core Isolation Memory Integrity is ON — this blocks the kernel mapper and WILL crash injection.",
                        "Windows Security → Device Security → Core Isolation → turn OFF Memory Integrity, then restart PC");
                    failCount++;
                }
                else
                {
                    AddScanResult("PASS", "Memory Integrity (HVCI)", "Core Isolation is off.", null);
                    passCount++;
                }
            }
        }
        catch { }

        // Summary
        AddScanSummary(passCount, warnCount, failCount);

        ScanBtn.Content = "SCAN";
        ScanBtn.IsEnabled = true;
    }

    private void AddScanResult(string level, string title, string detail, string? fix)
    {
        string icon = level switch
        {
            "PASS" => "✔",
            "FAIL" => "✘",
            "WARN" => "⚠",
            "INFO" => "ℹ",
            _ => "•"
        };
        var color = level switch
        {
            "PASS" => "#4CAF50",
            "FAIL" => "#F44336",
            "WARN" => "#FFA726",
            "INFO" => "#42A5F5",
            _ => "#AAAAAA"
        };

        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var wpfColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color);
        var colorBrush = new SolidColorBrush(wpfColor);

        var header = new TextBlock
        {
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono,Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        header.Inlines.Add(new System.Windows.Documents.Run($"{icon} ") { Foreground = colorBrush });
        header.Inlines.Add(new System.Windows.Documents.Run($"[{level}] {title}")
            { Foreground = colorBrush, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(header);

        var detailTb = new TextBlock
        {
            Text = $"  {detail}",
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono,Consolas"),
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("TextDimBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 0)
        };
        sp.Children.Add(detailTb);

        if (fix != null)
        {
            var fixColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#81D4FA");
            var fixTb = new TextBlock
            {
                Text = $"  FIX: {fix}",
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono,Consolas"),
                FontSize = 11,
                Foreground = new SolidColorBrush(fixColor),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0)
            };
            sp.Children.Add(fixTb);
        }

        ScanResultsPanel.Children.Add(sp);
    }

    private void AddScanSummary(int pass, int warn, int fail)
    {
        var sep = new Border
        {
            Height = 1,
            Background = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            Margin = new Thickness(0, 6, 0, 8)
        };
        ScanResultsPanel.Children.Add(sep);

        string verdict = fail > 0
            ? $"SCAN COMPLETE — {fail} issue(s) must be fixed before injecting."
            : warn > 0
                ? $"SCAN COMPLETE — {pass} passed, {warn} warning(s). Review warnings before injecting."
                : $"SCAN COMPLETE — All {pass} checks passed. Ready to inject.";

        var summaryColor = fail > 0 ? "#F44336" : warn > 0 ? "#FFA726" : "#4CAF50";
        var wpfSummary = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(summaryColor);
        var tb = new TextBlock
        {
            Text = verdict,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono,Consolas"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(wpfSummary),
            TextWrapping = TextWrapping.Wrap
        };
        ScanResultsPanel.Children.Add(tb);
    }

    [StructLayout(LayoutKind.Sequential)]
    private class NativeMemoryStatus
    {
        public uint dwLength = 64;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] NativeMemoryStatus lpBuffer);

    // ═══════════════ DEBUG FLAG (admin-only logging) ═══════════════

    private static readonly string DebugFlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreX", "cxdbg.flag");

    private static void EnableDebugFlag()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DebugFlagPath)!);
            File.WriteAllText(DebugFlagPath, "1");
        }
        catch { }
    }

    private static void DisableDebugFlag()
    {
        try { if (File.Exists(DebugFlagPath)) File.Delete(DebugFlagPath); } catch { }
    }

    // ═══════════════ MANUAL UPDATE CHECK ═══════════════

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates...";
        try
        {
            if (FindGameProcess() != null)
            {
                UpdateStatusText.Text = "Game is running — close the game before updating.";
                UpdateStatusText.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
                return;
            }

            var resp = await _fastHttp.GetAsync($"{Api}/api/releases/current");
            if (!resp.IsSuccessStatusCode)
            {
                UpdateStatusText.Text = "Could not reach update server. Try again later.";
                UpdateStatusText.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
                return;
            }

            var json = await resp.Content.ReadAsStringAsync();
            var version = TryGetField(json, "version");
            var url = TryGetField(json, "url");

            if (version is null)
            {
                UpdateStatusText.Text = "No update info available.";
                UpdateStatusText.Foreground = FindResource("MutedBrush") as SolidColorBrush;
                return;
            }

            if (Version.TryParse(version, out var remote) &&
                Version.TryParse(AppVersion, out var local) &&
                remote > local && url is not null)
            {
                if (FindGameProcess() != null)
                {
                    UpdateStatusText.Text = $"v{version} available — close the game to update.";
                    UpdateStatusText.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
                    return;
                }
                UpdateStatusText.Text = $"Update v{version} available! Downloading...";
                UpdateStatusText.Foreground = FindResource("AccentGlowBrush") as SolidColorBrush;
                _updating = true;
                ShowUpdateOverlay($"Updating to v{version}...");
                await DownloadUpdateAsync(url);
                HideUpdateOverlay();
                _updating = false;
            }
            else
            {
                UpdateStatusText.Text = $"You're up to date! (v{AppVersion})";
                UpdateStatusText.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
            }
        }
        catch
        {
            UpdateStatusText.Text = "Update check failed. Check your connection.";
            UpdateStatusText.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
            HideUpdateOverlay();
            _updating = false;
        }
        finally { CheckUpdateBtn.IsEnabled = true; }
    }

    // ═══════════════ AUTO INJECTION ═══════════════

    private static void EnsureFilesExtracted()
    {
        var critical = new[] { "mw2.dll", "PYTExample.exe" };
        bool anyMissing = critical.Any(f => !File.Exists(Path.Combine(EmbeddedBinDir, f)));
        if (anyMissing)
            ExtractEmbeddedPayloads();
    }

    private void AutoDetectGameDir(string platform)
    {
        var detected = DetectGameDirectory(platform);
        string resolvedPlatform = platform;

        if (detected == null)
        {
            foreach (var alt in new[] { "Steam", "Xbox (PC)" })
            {
                if (alt == platform) continue;
                detected = DetectGameDirectory(alt);
                if (detected != null) { resolvedPlatform = alt; break; }
            }
        }

        if (detected != null && GameDirBox != null)
        {
            var current = GameDirBox.Text?.Trim() ?? "";
            if (!string.Equals(current, detected, StringComparison.OrdinalIgnoreCase))
            {
                GameDirBox.Text = detected;
                AppendLog($"[*] Auto-detected {resolvedPlatform} game directory: {detected}");
            }
            if (resolvedPlatform != platform && GamePlatformCombo != null)
            {
                for (int i = 0; i < GamePlatformCombo.Items.Count; i++)
                {
                    if (GamePlatformCombo.Items[i] is System.Windows.Controls.ComboBoxItem ci &&
                        ci.Content?.ToString() == resolvedPlatform)
                    {
                        GamePlatformCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
        }
    }

    private static readonly string[] GameProcessNames = { "cod22-cod", "cod", "cod22", "ModernWarfare", "cod22-cod-ms", "cod22-cod-bnet" };

    private static string? FindGameProcess()
    {
        foreach (var name in GameProcessNames)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0)
            {
                foreach (var p in procs) p.Dispose();
                return name;
            }
        }
        return null;
    }

    private static async Task<string?> WaitForGameProcess(int timeoutSeconds = 120)
    {
        return await Task.Run(() =>
        {
            for (int i = 0; i < timeoutSeconds; i++)
            {
                var found = FindGameProcess();
                if (found != null) return found;
                Thread.Sleep(1000);
            }
            return (string?)null;
        });
    }

    private void DeployVCRuntime()
    {
        var gameDir = GameDirBox?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir)) return;

        var runtimeFiles = new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" };
        foreach (var file in runtimeFiles)
        {
            var src = Path.Combine(EmbeddedBinDir, file);
            var dst = Path.Combine(gameDir, file);
            try
            {
                if (File.Exists(src) && !File.Exists(dst))
                    File.Copy(src, dst, false);
            }
            catch { }
        }
    }

    private static bool IsDllAlreadyLoaded()
    {
        try
        {
            using var guard = EventWaitHandle.OpenExisting(@"Local\CoreXInitGuard");
            return true;
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch { return false; }
    }

    private async void TriggerAutoInject()
    {
        if (_autoInjecting || _autoInjected) return;
        if (!_injectionLock.Wait(0)) return;
        _autoInjecting = true;

        try
        {
            EnsureFilesExtracted();
            var platform = (GamePlatformCombo?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Steam";
            AutoDetectGameDir(platform);
            var injectorPath = FindFile("PYTExample.exe");
            var dllPath = FindFile("mw2.dll", "MW2.dll");

            if (injectorPath is null || dllPath is null)
            {
                if (injectorPath is null) AppendLog("[ERROR] PYTExample.exe not found — likely blocked by antivirus.");
                if (dllPath is null) AppendLog("[ERROR] mw2.dll not found — likely blocked by antivirus.");
                AppendLog("[FIX] Add this folder to Windows Defender exclusions:");
                AppendLog($"      {EmbeddedBinDir}");
                AppendLog("[FIX] Then restart the loader.");
                return;
            }

            AppendLog("[AUTO] Game detected — waiting for full screen...");

            var procName = FindGameProcess() ?? "cod22-cod";
            if (!await WaitForFullScreen(procName))
            {
                AppendLog("[AUTO] Game closed before injection.");
                return;
            }

            if (IsDllAlreadyLoaded())
            {
                AppendLog("[AUTO] Menu DLL already loaded — skipping injection.");
                _autoInjected = true;
                return;
            }

            if (!CheckOverlaysAndWarn(procName))
                return;

            var injDir = Path.GetDirectoryName(injectorPath) ?? ExeDir;
            await RunFixGameAsync();

            if (IsDllAlreadyLoaded())
            {
                AppendLog("[AUTO] Menu DLL already loaded — skipping injection.");
                _autoInjected = true;
                return;
            }

            AppendLog("[AUTO] Injecting via kernel mapper...");
            DeployVCRuntime();
            await RunInjectorWithRetry(injectorPath, injDir, maxAttempts: 1, targetProcess: procName);

            _autoInjected = true;
            await PostInjectionDiagnostics();
        }
        finally
        {
            _autoInjecting = false;
            _injectionLock.Release();
        }
    }

    private async Task<bool> WaitForFullScreen(string procName)
    {
        int result = await Task.Run(() =>
        {
            for (int i = 0; i < 120; i++)
            {
                Thread.Sleep(1000);
                try
                {
                    var ps = Process.GetProcessesByName(procName);
                    if (ps.Length == 0) { foreach (var p in ps) p.Dispose(); return -1; }
                    var hwnd = ps[0].MainWindowHandle;
                    foreach (var p in ps) p.Dispose();
                    if (hwnd != IntPtr.Zero && IsGameWindowReady(hwnd)) return 1;
                }
                catch { }
            }
            return 0;
        });

        if (result == -1) return false;

        if (result == 0)
        {
            AppendLog("[!] Game window not ready within 2 min — attempting injection...");
        }
        else
        {
            AppendLog("[*] Game window ready — waiting for D3D12 to initialize...");
        }

        await Task.Delay(10_000);
        return true;
    }

    private static bool IsGameWindowReady(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!GetClientRect(hwnd, out RECT client)) return false;
        int w = client.Right - client.Left;
        int h = client.Bottom - client.Top;
        return w >= 800 && h >= 600;
    }

    private async Task RunInjectorWithRetry(string injectorPath, string workingDir, int maxAttempts = 1, string? targetProcess = null)
    {
        if (!string.IsNullOrEmpty(targetProcess))
        {
            try { File.WriteAllText(Path.Combine(workingDir, "target.txt"), targetProcess); } catch { }
        }

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            bool exited = await Task.Run(() =>
            {
                try
                {
                    var pyt = Process.Start(new ProcessStartInfo
                    {
                        FileName = injectorPath,
                        Arguments = targetProcess ?? "",
                        WorkingDirectory = workingDir,
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Minimized
                    });
                    if (pyt == null) return false;
                    bool done = pyt.WaitForExit(60_000);
                    if (!done)
                    {
                        try { pyt.Kill(); } catch { }
                    }
                    return done;
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendLog($"[ERROR] Injector failed: {ex.Message}"));
                    return false;
                }
            });

            if (exited || IsDllAlreadyLoaded()) return;

            if (attempt < maxAttempts)
            {
                AppendLog("[!] Injection stalled — retrying in 10s...");
                await Task.Delay(10_000);
            }
            else
            {
                AppendLog("[ERROR] Injection timed out. Game may need to fully load first — try again.");
            }
        }
    }

    private async Task RunFixGameAsync()
    {
        var fixGamePath = FindFile("FixGame.exe");
        if (fixGamePath is null) return;

        try
        {
            AppendLog("[*] Running FixGame...");
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = fixGamePath,
                WorkingDirectory = Path.GetDirectoryName(fixGamePath) ?? ExeDir,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (proc is null) return;
            bool exited = await Task.Run(() => proc.WaitForExit(30_000));
            if (!exited) { try { proc.Kill(); } catch { } }
        }
        catch { }
    }

    // ═══════════════ DLL INJECTION ═══════════════

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private static bool IsWindowFullScreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(hwnd, out RECT rect)) return false;
        int screenW = GetSystemMetrics(0);
        int screenH = GetSystemMetrics(1);
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        return w >= screenW - 10 && h >= screenH - 10;
    }

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

        AppendLog($"[+] Game found (PID {target.Id}). Waiting for initialization (7s)...");
        await Task.Delay(7_000);

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

    private static readonly (string dll, string name)[] OverlayDlls = new[]
    {
        ("nvspcap64.dll",             "NVIDIA Overlay (ShadowPlay)"),
        ("NvCamera64.dll",            "NVIDIA Overlay (Camera)"),
        ("DiscordHook64.dll",         "Discord Overlay"),
        ("discord_overlay2.dll",      "Discord Overlay"),
        ("RTSSHooks64.dll",           "MSI Afterburner / RivaTuner"),
        ("GameOverlayRenderer64.dll", "Steam Overlay"),
        ("d3dcompiler_47.dll",        "Xbox Game Bar Overlay"),
        ("XGameBarSDK.dll",           "Xbox Game Bar SDK"),
        ("obs-graphics-hook64.dll",   "OBS Game Capture"),
        ("fraps64.dll",               "Fraps"),
        ("medal_hook.dll",            "Medal.tv Overlay"),
        ("BattleNetHelper.dll",       "Battle.net Helper"),
    };

    private List<string> DetectOverlays(string procName)
    {
        var found = new List<string>();
        try
        {
            var procs = Process.GetProcessesByName(procName);
            if (procs.Length == 0) return found;
            var proc = procs[0];
            try
            {
                foreach (ProcessModule mod in proc.Modules)
                {
                    var modName = mod.ModuleName ?? "";
                    foreach (var (dll, name) in OverlayDlls)
                    {
                        if (modName.Equals(dll, StringComparison.OrdinalIgnoreCase) && !found.Contains(name))
                            found.Add(name);
                    }
                }
            }
            catch { }
            foreach (var p in procs) p.Dispose();
        }
        catch { }
        return found;
    }

    private bool CheckOverlaysAndWarn(string procName)
    {
        var overlays = DetectOverlays(procName);
        if (overlays.Count == 0) return true;

        var list = string.Join("\n", overlays.Select(o => $"  - {o}"));
        AppendLog($"[WARNING] Overlay(s) detected:\n{list}");

        var tips = new System.Text.StringBuilder();
        tips.AppendLine($"The following overlay(s) were detected:\n\n{list}\n");
        tips.AppendLine("These can prevent the in-game menu from appearing or crash the game.\n");

        if (overlays.Any(o => o.Contains("Steam")))
            tips.AppendLine("STEAM: Settings > In-Game > uncheck 'Enable Steam Overlay while in-game'");
        if (overlays.Any(o => o.Contains("Discord")))
            tips.AppendLine("DISCORD: Settings > Game Overlay > toggle off");
        if (overlays.Any(o => o.Contains("NVIDIA")))
            tips.AppendLine("NVIDIA: GeForce Experience > Settings > In-Game Overlay > toggle off");
        if (overlays.Any(o => o.Contains("Xbox")))
            tips.AppendLine("XBOX: Settings > Gaming > Xbox Game Bar > toggle off");

        tips.AppendLine("\nDisable them and restart the game for best results.\nContinue anyway?");

        var result = System.Windows.MessageBox.Show(
            tips.ToString(),
            "CoreX - Overlay Conflict",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.No)
        {
            AppendLog("[*] Injection cancelled by user (overlay conflict).");
            return false;
        }
        AppendLog("[*] User chose to continue despite overlay warning.");
        return true;
    }

    private static string? FindFile(params string[] names)
    {
        string[] searchDirs = { EmbeddedBinDir, PayloadCacheDir, ExeDir, CoffinBuild };
        foreach (var dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
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

        EnsureFilesExtracted();
        AutoDetectGameDir("Steam");
        var injectorPath = FindFile("PYTExample.exe");
        var dllPath = FindFile("mw2.dll", "MW2.dll");
        var fixGamePath = FindFile("FixGame.exe");
        if (injectorPath is null) { AppendLog("[ERROR] PYTExample.exe not found — likely blocked by antivirus."); ShowLaunchError("PYTExample.exe blocked by antivirus.\nAdd the CoreX folder to Windows Defender exclusions and restart."); return; }
        if (dllPath is null) { AppendLog("[ERROR] mw2.dll not found — likely blocked by antivirus."); ShowLaunchError("mw2.dll blocked by antivirus.\nAdd the CoreX folder to Windows Defender exclusions and restart."); return; }

        if (!_injectionLock.Wait(0))
        {
            ShowLaunchError("Another injection is already in progress.");
            return;
        }
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

            AppendLog("[*] Waiting for game process...");
            var procName = await WaitForGameProcess();
            if (procName == null) { AppendLog("[ERROR] Game process not detected after 2 minutes."); return; }
            AppendLog($"[*] Game detected ({procName}) — waiting for full screen...");
            if (!await WaitForFullScreen(procName))
            {
                AppendLog("[ERROR] Game closed before injection.");
                return;
            }

            if (IsDllAlreadyLoaded())
            {
                AppendLog("[*] Menu DLL already loaded — skipping injection.");
                return;
            }

            if (!CheckOverlaysAndWarn(procName))
                return;

            await RunFixGameAsync();

            if (IsDllAlreadyLoaded())
            {
                AppendLog("[*] Menu DLL already loaded — skipping injection.");
                return;
            }

            AppendLog("[*] Injecting DLL via kernel mapper...");
            DeployVCRuntime();
            var injectorDir = Path.GetDirectoryName(injectorPath) ?? ExeDir;
            await RunInjectorWithRetry(injectorPath, injectorDir, maxAttempts: 1, targetProcess: procName);
            await PostInjectionDiagnostics();
        }
        catch (Exception ex) { AppendLog($"[ERROR] Steam launch failed: {ex.Message}"); }
        finally
        {
            _autoInjecting = false;
            _injectionLock.Release();
        }
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

        EnsureFilesExtracted();
        AutoDetectGameDir("Xbox (PC)");
        var injectorPath = FindFile("PYTExample.exe");
        var dllPath = FindFile("mw2.dll", "MW2.dll");
        if (injectorPath is null) { AppendLog("[ERROR] PYTExample.exe not found — likely blocked by antivirus."); ShowLaunchError("PYTExample.exe blocked by antivirus.\nAdd the CoreX folder to Windows Defender exclusions and restart."); return; }
        if (dllPath is null) { AppendLog("[ERROR] mw2.dll not found — likely blocked by antivirus."); ShowLaunchError("mw2.dll blocked by antivirus.\nAdd the CoreX folder to Windows Defender exclusions and restart."); return; }

        if (!_injectionLock.Wait(0))
        {
            ShowLaunchError("Another injection is already in progress.");
            return;
        }
        _autoInjected = true;
        _autoInjecting = true;

        try
        {
            bool launched = false;

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

            if (!launched)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "xbox://launch/?productId=9PGLFS9MB9CG",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    AppendLog($"[ERROR] All launch methods failed: {ex.Message}");
                    return;
                }
            }

            AppendLog("[*] Waiting for game process...");
            var procName = await WaitForGameProcess();
            if (procName == null) { AppendLog("[ERROR] Game process not detected after 2 minutes."); return; }
            AppendLog($"[*] Game detected ({procName}) — waiting for full screen...");
            if (!await WaitForFullScreen(procName))
            {
                AppendLog("[ERROR] Game closed before injection.");
                return;
            }

            if (IsDllAlreadyLoaded())
            {
                AppendLog("[*] Menu DLL already loaded — skipping injection.");
                return;
            }

            if (!CheckOverlaysAndWarn(procName))
                return;

            await RunFixGameAsync();

            if (IsDllAlreadyLoaded())
            {
                AppendLog("[*] Menu DLL already loaded — skipping injection.");
                return;
            }

            AppendLog("[*] Injecting DLL via kernel mapper...");
            DeployVCRuntime();
            var injectorDir = Path.GetDirectoryName(injectorPath) ?? ExeDir;
            await RunInjectorWithRetry(injectorPath, injectorDir, maxAttempts: 1, targetProcess: procName);
            await PostInjectionDiagnostics();
        }
        finally
        {
            _autoInjecting = false;
            _injectionLock.Release();
        }
    }

    private async void LaunchBattleNet_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) { ShowLaunchError("Update in progress — please wait."); return; }
        if (!RequireLicense()) return;

        EnsureFilesExtracted();
        AutoDetectGameDir("Battle.net");
        var injectorPath = FindFile("PYTExample.exe");
        var dllPath = FindFile("mw2.dll", "MW2.dll");
        if (injectorPath is null) { AppendLog("[ERROR] PYTExample.exe not found — likely blocked by antivirus."); ShowLaunchError("PYTExample.exe blocked by antivirus.\nAdd the CoreX folder to Windows Defender exclusions and restart."); return; }
        if (dllPath is null) { AppendLog("[ERROR] mw2.dll not found — likely blocked by antivirus."); ShowLaunchError("mw2.dll blocked by antivirus.\nAdd the CoreX folder to Windows Defender exclusions and restart."); return; }

        if (!_injectionLock.Wait(0))
        {
            ShowLaunchError("Another injection is already in progress.");
            return;
        }
        _autoInjected = true;
        _autoInjecting = true;

        try
        {
            bool launched = false;

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
                        AppendLog($"[ERROR] Bootstrapper launch failed: {ex.Message}");
                        return;
                    }
                }
                else
                {
                    AppendLog("[ERROR] Could not launch game. Make sure Battle.net client is running or game is installed.");
                    return;
                }
            }

            AppendLog("[*] Waiting for game process...");
            var procName = await WaitForGameProcess();
            if (procName == null) { AppendLog("[ERROR] Game process not detected after 2 minutes."); return; }
            AppendLog($"[*] Game detected ({procName}) — waiting for full screen...");
            if (!await WaitForFullScreen(procName))
            {
                AppendLog("[ERROR] Game closed before injection.");
                return;
            }

            if (IsDllAlreadyLoaded())
            {
                AppendLog("[*] Menu DLL already loaded — skipping injection.");
                return;
            }

            if (!CheckOverlaysAndWarn(procName))
                return;

            await RunFixGameAsync();

            if (IsDllAlreadyLoaded())
            {
                AppendLog("[*] Menu DLL already loaded — skipping injection.");
                return;
            }

            AppendLog("[*] Injecting DLL via kernel mapper...");
            DeployVCRuntime();
            var injectorDir = Path.GetDirectoryName(injectorPath) ?? ExeDir;
            await RunInjectorWithRetry(injectorPath, injectorDir, maxAttempts: 1, targetProcess: procName);
            await PostInjectionDiagnostics();
        }
        finally
        {
            _autoInjecting = false;
            _injectionLock.Release();
        }
    }

    private void QuickLaunch_Click(object sender, RoutedEventArgs e)
    {
        NavLoader.IsChecked = true;
    }

    private async Task PostInjectionDiagnostics()
    {
        await Task.Delay(3_000);
        var procName = FindGameProcess();
        if (procName == null)
        {
            AppendLog("[WARNING] Game process not found — the game may have crashed on injection.");
            AppendLog("[TIP] Try disabling Windows Defender real-time protection before launching.");
            AppendLog("[TIP] Close ALL overlays (Steam, Discord, NVIDIA) before launching.");
            _autoInjected = false;
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command \"(Get-MpPreference).DisableRealtimeMonitoring\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = (await proc.StandardOutput.ReadToEndAsync()).Trim();
                proc.WaitForExit(5_000);
                if (output.Equals("False", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog("[WARNING] Windows Defender real-time protection is ON.");
                    AppendLog("[TIP] If menu doesn't appear, add game folder + CoreX folder to Defender exclusions.");
                }
            }
        }
        catch { }

        var dllPath = FindFile("mw2.dll", "MW2.dll");
        if (dllPath != null && !File.Exists(dllPath))
        {
            AppendLog("[ERROR] mw2.dll was deleted — antivirus likely removed it.");
            AppendLog("[TIP] Restore the file and add it to your antivirus exclusions.");
        }

        var overlays = DetectOverlays(procName);
        if (overlays.Count > 0)
        {
            bool hasSteam = overlays.Any(o => o.Contains("Steam"));
            bool hasDiscord = overlays.Any(o => o.Contains("Discord"));
            if (hasSteam)
            {
                AppendLog("[WARNING] Steam Overlay is active — this can block the in-game menu.");
                AppendLog("[TIP] Disable Steam Overlay: Steam > Settings > In-Game > uncheck 'Enable Steam Overlay'.");
            }
            if (hasDiscord)
            {
                AppendLog("[WARNING] Discord Overlay is active — this can block the in-game menu.");
                AppendLog("[TIP] Disable Discord Overlay: Settings > Game Overlay > toggle off.");
            }
            foreach (var o in overlays.Where(o => !o.Contains("Steam") && !o.Contains("Discord")))
                AppendLog($"[WARNING] {o} is active — may conflict with menu overlay.");
        }

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Personal), "cxdebug.txt");
        if (File.Exists(logPath))
        {
            try
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var lines = (await sr.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                bool hasCrash = false;
                bool d3dFailed = false;
                foreach (var line in lines.TakeLast(20))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Contains("CRASH"))
                    {
                        AppendLog($"[WARNING] DLL issue: {trimmed}");
                        hasCrash = true;
                    }
                    if (trimmed.Contains("D3D12 init failed after all retries"))
                        d3dFailed = true;
                }
                if (d3dFailed)
                {
                    AppendLog("[WARNING] D3D12 initialization failed — menu will not render.");
                    AppendLog("[TIP] Disable ALL overlays (Steam, Discord, NVIDIA ShadowPlay, Xbox Game Bar).");
                    AppendLog("[TIP] Make sure the game is running in DirectX 12 mode (not DX11).");
                }
            }
            catch { }
        }

        AppendLog("[OK] Injection complete — press INSERT in game to open menu.");
        AppendLog("[TIP] If menu doesn't appear: disable Steam/Discord/NVIDIA overlays and try again.");
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

    private void GamePlatform_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (GameDirBox == null) return;
        if (GamePlatformCombo?.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        var platform = item.Content?.ToString() ?? "";
        var detected = DetectGameDirectory(platform);
        if (detected != null)
            GameDirBox.Text = detected;
    }

    private void AutoDetectGameDir_Click(object sender, RoutedEventArgs e)
    {
        var platform = (GamePlatformCombo?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Steam";
        var detected = DetectGameDirectory(platform);
        if (detected != null)
        {
            GameDirBox.Text = detected;
            AppendLog($"[*] Auto-detected {platform} game directory: {detected}");
        }
        else
        {
            AppendLog($"[!] Could not auto-detect game directory for {platform}. Use Browse to set it manually.");
            MessageBox.Show($"Could not find the game installation for {platform}.\nUse Browse to select the folder manually.",
                "Auto Detect", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private string? DetectGameDirectory(string platform)
    {
        switch (platform)
        {
            case "Steam":
                return DetectSteamGameDir();
            case "Battle.net":
                return DetectBattleNetGameDir();
            case "Xbox (PC)":
                return DetectXboxGameDir();
            default:
                return null;
        }
    }

    private string? DetectSteamGameDir()
    {
        var candidates = new List<string>();

        try
        {
            var steamPath = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string
                ?? Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string;

            if (steamPath != null)
            {
                candidates.Add(Path.Combine(steamPath, "steamapps", "common", "Call of Duty Modern Warfare II"));
                candidates.Add(Path.Combine(steamPath, "steamapps", "common", "Call of Duty HQ"));

                var libFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libFile))
                {
                    foreach (var line in File.ReadAllLines(libFile))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("\"path\""))
                        {
                            var parts = trimmed.Split('"');
                            if (parts.Length >= 4)
                            {
                                var libPath = parts[3].Replace("\\\\", "\\");
                                candidates.Add(Path.Combine(libPath, "steamapps", "common", "Call of Duty Modern Warfare II"));
                                candidates.Add(Path.Combine(libPath, "steamapps", "common", "Call of Duty HQ"));
                            }
                        }
                    }
                }
            }
        }
        catch { }

        var defaultPaths = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty Modern Warfare II",
            @"C:\Program Files\Steam\steamapps\common\Call of Duty Modern Warfare II",
            @"D:\SteamLibrary\steamapps\common\Call of Duty Modern Warfare II",
            @"E:\SteamLibrary\steamapps\common\Call of Duty Modern Warfare II",
            @"C:\Program Files (x86)\Steam\steamapps\common\Call of Duty HQ",
        };
        candidates.AddRange(defaultPaths);

        foreach (var path in candidates)
            if (Directory.Exists(path)) return path;

        return null;
    }

    private string? DetectBattleNetGameDir()
    {
        var candidates = new List<string>();

        try
        {
            var bnetPath = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Blizzard Entertainment\Battle.net", "InstallPath", null) as string;
            if (bnetPath != null)
            {
                var parent = Path.GetDirectoryName(bnetPath);
                if (parent != null)
                {
                    candidates.Add(Path.Combine(parent, "Call of Duty"));
                    candidates.Add(Path.Combine(parent, "Call of Duty Modern Warfare II"));
                }
            }
        }
        catch { }

        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Battle.net", "Battle.net.config");
            if (File.Exists(configPath))
            {
                var content = File.ReadAllText(configPath);
                var key = "\"DefaultInstallPath\":";
                var idx = content.IndexOf(key);
                if (idx >= 0)
                {
                    var start = content.IndexOf('"', idx + key.Length) + 1;
                    var end = content.IndexOf('"', start);
                    if (start > 0 && end > start)
                    {
                        var installRoot = content.Substring(start, end - start).Replace("\\\\", "\\").Replace("\\/", "/");
                        candidates.Add(Path.Combine(installRoot, "Call of Duty"));
                        candidates.Add(Path.Combine(installRoot, "Call of Duty Modern Warfare II"));
                    }
                }
            }
        }
        catch { }

        var defaultPaths = new[]
        {
            @"C:\Program Files (x86)\Call of Duty",
            @"C:\Program Files\Call of Duty",
            @"D:\Call of Duty",
            @"E:\Call of Duty",
            @"C:\Program Files (x86)\Call of Duty Modern Warfare II",
            @"D:\Games\Call of Duty",
        };
        candidates.AddRange(defaultPaths);

        foreach (var path in candidates)
            if (Directory.Exists(path)) return path;

        return null;
    }

    private string? DetectXboxGameDir()
    {
        var candidates = new List<string>();

        var modifiable = new[]
        {
            @"C:\Program Files\ModifiableWindowsApps\Call of Duty HQ",
            @"C:\Program Files\ModifiableWindowsApps\Call of Duty Modern Warfare II",
            @"D:\Program Files\ModifiableWindowsApps\Call of Duty HQ",
            @"E:\Program Files\ModifiableWindowsApps\Call of Duty HQ",
        };
        candidates.AddRange(modifiable);

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                var xgp = Path.Combine(drive.Name, "XboxGames", "Call of Duty HQ", "Content");
                candidates.Add(xgp);
                var xgp2 = Path.Combine(drive.Name, "XboxGames", "Call of Duty Modern Warfare II", "Content");
                candidates.Add(xgp2);
            }
        }
        catch { }

        try
        {
            var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (Directory.Exists(windowsApps))
            {
                try
                {
                    foreach (var dir in Directory.GetDirectories(windowsApps, "Activision*"))
                        candidates.Add(dir);
                }
                catch { }
            }
        }
        catch { }

        foreach (var path in candidates)
            if (Directory.Exists(path)) return path;

        return null;
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
        KeyBox.Text = savedKey;

        if (!string.IsNullOrEmpty(_currentSettings.LicensePlan) &&
            _currentSettings.LicenseExpiry.HasValue &&
            _currentSettings.LicenseExpiry.Value > DateTime.UtcNow)
        {
            ApplyLicense(
                new LicenseResponse(true, "License restored (cached).",
                    _currentSettings.LicensePlan, _currentSettings.LicenseExpiry),
                savedKey, save: false);
            ActivationResult.Text = "License restored (cached).";
            ActivationResult.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
            AppendLog("[OK] License restored from local cache — validating online...");
        }
        else
        {
            ActivationResult.Text = "Connecting to license server...";
        }

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var resp = await _http.PostAsJsonAsync($"{Api}/api/license/activate",
                new { Key = savedKey, DeviceId }, cts.Token);
            var json = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (resp.IsSuccessStatusCode)
            {
                var plan = root.TryGetProperty("plan", out var p) ? p.GetString() : null;
                DateTime? expiresAt = root.TryGetProperty("expiresAt", out var ex) && ex.ValueKind != JsonValueKind.Null
                    ? ex.GetDateTime() : null;

                ActivationResult.Text = "License verified.";
                ActivationResult.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
                ApplyLicense(new LicenseResponse(true, "License verified.", plan, expiresAt), savedKey);
                AppendLog("[OK] License verified with server.");
            }
            else
            {
                var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                ActivationResult.Text = message;
                ActivationResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
                if (!_licensed)
                    AppendLog($"[ERROR] License rejected: {message}");
            }
        }
        catch
        {
            if (_licensed)
            {
                AppendLog("[!] Server unreachable — using cached license.");
            }
            else
            {
                ActivationResult.Text = "Cannot reach license server — using cached data or enter key manually.";
                ActivationResult.Foreground = FindResource("ErrorBrush") as SolidColorBrush;
            }
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
                EnableDebugFlag();
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

        var plan = (GenPlanBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Monthly";
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
            var nameLabel = GenNameBox.Text.Trim();
            var generated = new List<string>();
            for (int i = 0; i < qty; i++)
            {
                var body = string.IsNullOrEmpty(nameLabel)
                    ? new { Days = days, Plan = plan, Name = (string?)null }
                    : new { Days = days, Plan = plan, Name = (string?)nameLabel };
                var req = AdminRequest(HttpMethod.Post, "/api/admin/keys",
                    new StringContent(JsonSerializer.Serialize(body),
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

    private async void AdminDeleteAllKeys_Click(object sender, RoutedEventArgs e)
    {
        if (!_adminAuthed) return;

        var confirm = MessageBox.Show("Delete ALL license keys?\n\nThis cannot be undone.",
            "CORE X — Delete All Keys", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var listReq = AdminRequest(HttpMethod.Get, "/api/admin/keys");
            var listResp = await _http.SendAsync(listReq);
            var listJson = await listResp.Content.ReadAsStringAsync();

            if (!listResp.IsSuccessStatusCode)
            {
                MessageBox.Show("Could not fetch key list.", "CORE X", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var allKeysRaw = JsonSerializer.Deserialize<List<ApiKeyEntry>>(listJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

            if (allKeysRaw.Count == 0)
            {
                AppendLog("[ADMIN] No keys to delete.");
                await RefreshKeyListAsync();
                return;
            }

            DeletedKeysTracker.MarkAllDeleted(allKeysRaw.Select(k => k.Key));

            int deleted = 0;
            foreach (var k in allKeysRaw)
            {
                try
                {
                    var delReq = AdminRequest(HttpMethod.Delete, $"/api/admin/keys/{Uri.EscapeDataString(k.Key)}");
                    var delResp = await _http.SendAsync(delReq);
                    if (delResp.IsSuccessStatusCode) deleted++;
                }
                catch { }
            }

            AppendLog($"[ADMIN] Deleted {allKeysRaw.Count} key(s) ({deleted} removed from server).");
            await RefreshKeyListAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"[ERROR] {ex.Message}");
            MessageBox.Show($"Delete failed: {ex.Message}", "CORE X", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AdminRefreshKeys_Click(object sender, RoutedEventArgs e)
    {
        if (!_adminAuthed) return;
        await RefreshKeyListAsync();
    }

    private async Task RefreshKeyListAsync()
    {
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

            var rawKeys = JsonSerializer.Deserialize<List<ApiKeyEntry>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            _cachedKeys = DeletedKeysTracker.FilterDeleted(rawKeys);
            RenderFilteredKeys();
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

    private void KeyFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (_cachedKeys.Count > 0) RenderFilteredKeys();
    }

    private void KeyFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_cachedKeys.Count > 0) RenderFilteredKeys();
    }

    private void RenderFilteredKeys()
    {
        KeyListPanel.Children.Clear();

        var search = KeySearchBox?.Text?.Trim() ?? "";
        var statusFilter = (KeyStatusFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Status";
        var typeFilter = (KeyTypeFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Types";

        var filtered = _cachedKeys.Where(k =>
        {
            string status;
            if (!k.Activated)                                                      status = "Not Activated";
            else if (k.ExpiresAt.HasValue && k.ExpiresAt < DateTime.UtcNow)        status = "Expired";
            else if (!string.IsNullOrEmpty(k.DeviceId))                            status = "Bound";
            else                                                                   status = "Active";

            if (statusFilter != "All Status" && !status.Equals(statusFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            if (typeFilter != "All Types")
            {
                if (!string.Equals(k.Plan, typeFilter, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!string.IsNullOrEmpty(search))
            {
                bool match = k.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
                          || (k.DeviceId ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
                          || (k.Plan ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
                          || (k.Name ?? "").Contains(search, StringComparison.OrdinalIgnoreCase);
                if (!match) return false;
            }

            return true;
        }).ToList();

        KeyCountLabel.Text = $"Showing {filtered.Count} of {_cachedKeys.Count} keys";

        if (filtered.Count == 0)
        {
            KeyListPanel.Children.Add(new TextBlock
            {
                Text = _cachedKeys.Count == 0 ? "No keys found." : "No keys match the current filters.",
                FontSize = 12,
                Foreground = FindResource("MutedBrush") as SolidColorBrush,
                Padding = new Thickness(10, 8, 10, 8)
            });
            return;
        }

        foreach (var k in filtered)
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
            if (!string.IsNullOrEmpty(k.Name))
            {
                info.Children.Add(new TextBlock
                {
                    Text = k.Name,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = FindResource("AccentGlowBrush") as SolidColorBrush
                });
            }
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
                       (!string.IsNullOrEmpty(k.DeviceId) ? $"  |  HWID: {k.DeviceId}" : ""),
                FontSize = 11,
                Foreground = FindResource(statusColor) as SolidColorBrush,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var nameBtn = new Button
            {
                Content = string.IsNullOrEmpty(k.Name) ? "NAME" : "RENAME",
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(4, 0, 0, 0),
                FontSize = 11
            };
            var nameKey = k.Key;
            var currentName = k.Name ?? "";
            nameBtn.Click += async (_, _) =>
            {
                var dialog = new InputDialog("Set Name", "Enter a label for this key (e.g. player name):", currentName);
                if (dialog.ShowDialog() == true)
                {
                    var newName = dialog.ResponseText.Trim();
                    try
                    {
                        var nr = AdminRequest(HttpMethod.Put, $"/api/admin/keys/{Uri.EscapeDataString(nameKey)}/name",
                            new StringContent(JsonSerializer.Serialize(new { Name = string.IsNullOrEmpty(newName) ? (string?)null : newName }),
                                Encoding.UTF8, "application/json"));
                        await _http.SendAsync(nr);
                        await RefreshKeyListAsync();
                    }
                    catch (Exception ex) { AppendLog($"[ERROR] {ex.Message}"); }
                }
            };
            actions.Children.Add(nameBtn);

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

            var delBtn = new Button
            {
                Content = "DELETE",
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(4, 0, 0, 0),
                FontSize = 11,
                Style = FindResource("DangerButton") as Style
            };
            var delKey = k.Key;
            delBtn.Click += async (_, _) =>
            {
                var c = MessageBox.Show($"Delete key?\n{delKey}", "CORE X", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (c != MessageBoxResult.Yes) return;
                try
                {
                    DeletedKeysTracker.MarkDeleted(delKey);
                    try
                    {
                        var dr = AdminRequest(HttpMethod.Delete, $"/api/admin/keys/{Uri.EscapeDataString(delKey)}");
                        await _http.SendAsync(dr);
                    }
                    catch { }
                    AppendLog($"[ADMIN] Deleted {delKey}");
                    await RefreshKeyListAsync();
                }
                catch (Exception ex) { AppendLog($"[ERROR] {ex.Message}"); }
            };
            actions.Children.Add(delBtn);

            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);

            row.Child = grid;
            KeyListPanel.Children.Add(row);
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

        DeletedKeysTracker.MarkDeleted(key);

        try
        {
            var req = AdminRequest(HttpMethod.Delete, $"/api/admin/keys/{Uri.EscapeDataString(key)}");
            await _http.SendAsync(req);
        }
        catch { }

        ActionKeyResult.Text = "Key deleted.";
        ActionKeyResult.Foreground = FindResource("SuccessBrush") as SolidColorBrush;
        await RefreshKeyListAsync();
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
            _currentSettings.GamePlatform       = (GamePlatformCombo?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Steam";

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
            GameDirectory      = "",
            SourceDll          = DefaultSourceDll,
            DeployFilename     = DefaultDeployName,
            SteamAppId         = "3595230",
            ProcessName        = "cod22-cod",
            DebugLogPath       = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "cxdebug.txt"),
            ApiEndpoint        = "https://corex-api-5tdf.onrender.com",
            ProxyDll           = DefaultProxyDll,
            CheatProjectPath   = "",
            MsBuildPath        = "",
            BuildConfiguration = "Release",
            BuildPlatform      = "x64",
            GamePlatform       = "Steam"
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
                        ProxyDll           = loaded.ProxyDll           ?? defaults.ProxyDll,
                        GamePlatform       = loaded.GamePlatform       ?? defaults.GamePlatform
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

        var savedPlatform = defaults.GamePlatform ?? "Steam";
        for (int i = 0; i < GamePlatformCombo.Items.Count; i++)
        {
            if (GamePlatformCombo.Items[i] is System.Windows.Controls.ComboBoxItem ci &&
                ci.Content?.ToString() == savedPlatform)
            {
                GamePlatformCombo.SelectedIndex = i;
                break;
            }
        }

        if (string.IsNullOrEmpty(GameDirBox.Text))
        {
            var detected = DetectGameDirectory("Steam")
                        ?? DetectGameDirectory("Battle.net")
                        ?? DetectGameDirectory("Xbox (PC)");
            if (detected != null)
            {
                GameDirBox.Text = detected;
                if (detected.Contains("Steam", StringComparison.OrdinalIgnoreCase))
                    GamePlatformCombo.SelectedIndex = 0;
                else if (detected.Contains("Battle", StringComparison.OrdinalIgnoreCase) || detected.Contains("Call of Duty", StringComparison.OrdinalIgnoreCase))
                    GamePlatformCombo.SelectedIndex = 1;
                else
                    GamePlatformCombo.SelectedIndex = 2;
            }
        }
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
        public string? GamePlatform       { get; set; }
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
    public string? Name { get; set; }
}

static class DeletedKeysTracker
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreX", "deleted_keys.dat");

    private static HashSet<string> Load()
    {
        if (!File.Exists(StorePath)) return new HashSet<string>();
        try
        {
            var decrypted = AuthGuard.Decrypt(File.ReadAllBytes(StorePath));
            var json = Encoding.UTF8.GetString(decrypted);
            return JsonSerializer.Deserialize<HashSet<string>>(json) ?? new HashSet<string>();
        }
        catch { return new HashSet<string>(); }
    }

    private static void Save(HashSet<string> keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var json = JsonSerializer.Serialize(keys);
        var encrypted = AuthGuard.Encrypt(Encoding.UTF8.GetBytes(json));
        File.WriteAllBytes(StorePath, encrypted);
    }

    public static void MarkDeleted(string key)
    {
        var set = Load();
        set.Add(key);
        Save(set);
    }

    public static void MarkAllDeleted(IEnumerable<string> keys)
    {
        var set = Load();
        foreach (var k in keys) set.Add(k);
        Save(set);
    }

    public static bool IsDeleted(string key) => Load().Contains(key);

    public static List<ApiKeyEntry> FilterDeleted(List<ApiKeyEntry> keys)
    {
        var deleted = Load();
        if (deleted.Count == 0) return keys;
        return keys.Where(k => !deleted.Contains(k.Key)).ToList();
    }

    public static void UnmarkDeleted(string key)
    {
        var set = Load();
        if (set.Remove(key)) Save(set);
    }
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
            new[]{(char)0x67,(char)0x68,(char)0x69,(char)0x64,(char)0x72,(char)0x61},
            new[]{(char)0x73,(char)0x63,(char)0x79,(char)0x6C,(char)0x6C,(char)0x61},
            new[]{(char)0x64,(char)0x65,(char)0x62,(char)0x75,(char)0x67,(char)0x67,(char)0x65,(char)0x72},
            new[]{(char)0x69,(char)0x6D,(char)0x6D,(char)0x75,(char)0x6E,(char)0x69,(char)0x74,(char)0x79},
            new[]{(char)0x68,(char)0x74,(char)0x74,(char)0x70,(char)0x64,(char)0x65,(char)0x62,(char)0x75,(char)0x67,(char)0x67,(char)0x65,(char)0x72},
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public static void ErasePEHeader()
    {
        try
        {
            var ba = GetModuleHandle(null);
            if (ba == IntPtr.Zero) return;
            if (VirtualProtect(ba, (UIntPtr)4096, 0x40, out var old))
            {
                var zero = new byte[4096];
                Marshal.Copy(zero, 0, ba, 4096);
                VirtualProtect(ba, (UIntPtr)4096, old, out _);
            }
        }
        catch { }
    }

    public static bool CheckIntegrity()
    {
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
            if (path == null || !File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            var hash = SHA256.HashData(bytes);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CoreX");
            var hashFile = Path.Combine(dir, ".cxh");
            Directory.CreateDirectory(dir);
            if (!File.Exists(hashFile))
            {
                File.WriteAllBytes(hashFile, hash);
                File.SetAttributes(hashFile, FileAttributes.Hidden | FileAttributes.System);
                return true;
            }
            var stored = File.ReadAllBytes(hashFile);
            if (stored.Length != hash.Length) { File.WriteAllBytes(hashFile, hash); return true; }
            return stored.SequenceEqual(hash);
        }
        catch { return true; }
    }

    public static bool DetectSandbox()
    {
        try
        {
            var names = new[] { "vmware", "virtualbox", "vbox", "qemu", "xen", "sandboxie", "cuckoo", "wine" };
            var sysDir = Environment.SystemDirectory;
            foreach (var n in names)
            {
                try
                {
                    foreach (var dll in Directory.GetFiles(sysDir, "*.dll"))
                    {
                        if (Path.GetFileName(dll).ToLowerInvariant().Contains(n)) return true;
                    }
                }
                catch { }
            }
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Disk\Enum");
                var val = key?.GetValue("0")?.ToString()?.ToLowerInvariant() ?? "";
                foreach (var n in names)
                    if (val.Contains(n)) return true;
            }
            catch { }
        }
        catch { }
        return false;
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
        try
        {
            if (ScanForTools())
            {
                Thread.Sleep(new Random().Next(800, 2500));
                Environment.Exit(1);
            }
        }
        catch { }
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

sealed class InputDialog : Window
{
    private readonly System.Windows.Controls.TextBox _input;
    public string ResponseText => _input.Text;

    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        Title = title;
        Width = 400;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 20, 20));

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock
        {
            Text = prompt,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        _input = new System.Windows.Controls.TextBox
        {
            Text = defaultValue,
            FontSize = 14,
            MaxLength = 60,
            Padding = new Thickness(6, 4, 6, 4)
        };
        panel.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };

        var okBtn = new System.Windows.Controls.Button { Content = "SAVE", Padding = new Thickness(16, 6, 16, 6), IsDefault = true };
        okBtn.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(okBtn);

        var cancelBtn = new System.Windows.Controls.Button { Content = "CANCEL", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(cancelBtn);

        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _input.SelectAll(); _input.Focus(); };
    }
}
