using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClipperApp.Models;
using ClipperApp.Services;
using Microsoft.Win32;

namespace ClipperApp;

public record ClipItem(string Name, string FullPath, string TimeText);

public partial class MainWindow : Window
{
    private AppSettings _settings;
    private RecordingService? _recording;
    private HotkeyService? _hotkey;
    private readonly ObservableCollection<ClipItem> _clips = new();

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();
        ClipsList.ItemsSource = _clips;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSettingsToUI();
        CheckFfmpeg();
        LoadClipsList();
        RegisterHotkey();
        StartRecording();
    }

    // ── Einstellungen ───────────────────────────────────────────────────────

    private void LoadSettingsToUI()
    {
        TxtClipDuration.Text = _settings.ClipDurationSeconds.ToString();
        TxtOutputFolder.Text = _settings.OutputFolder;
        TxtFfmpegPath.Text   = _settings.FfmpegPath;

        SelectComboByContent(CmbFps, _settings.Fps.ToString());
        if (CmbFps.SelectedItem == null) CmbFps.SelectedIndex = 1;

        SelectComboByTag(CmbModifier, _settings.HotkeyModifiers.ToString());
        if (CmbModifier.SelectedItem == null) CmbModifier.SelectedIndex = 0;

        SelectComboByTag(CmbKey, _settings.HotkeyVirtualKey.ToString());
        if (CmbKey.SelectedItem == null) CmbKey.SelectedIndex = 8;

        ChkSystemAudio.IsChecked = _settings.CaptureSystemAudio;
        ChkMicrophone.IsChecked  = _settings.CaptureMicrophone;

        RefreshAudioDevices();
        UpdateHotkeyDisplay();
    }

    private void RefreshAudioDevices()
    {
        var devices = RecordingService.GetMicrophoneDevices(_settings.FfmpegPath);

        CmbSystemAudio.Items.Clear();
        CmbMicrophone.Items.Clear();
        foreach (var d in devices)
        {
            CmbSystemAudio.Items.Add(d);
            CmbMicrophone.Items.Add(d);
        }

        // Gespeicherten Wert anhand Anzeigename wiederfinden
        string sysDisplay = RecordingService.DeviceDisplayName(_settings.SystemAudioDevice);
        string micDisplay = RecordingService.DeviceDisplayName(_settings.MicrophoneDevice);

        SelectComboByContent(CmbSystemAudio, sysDisplay);
        if (CmbSystemAudio.SelectedItem == null && CmbSystemAudio.Items.Count > 0)
        {
            int cable = Enumerable.Range(0, CmbSystemAudio.Items.Count)
                .FirstOrDefault(i => CmbSystemAudio.Items[i]?.ToString()?.Contains("CABLE Output") == true, 0);
            CmbSystemAudio.SelectedIndex = cable;
        }

        SelectComboByContent(CmbMicrophone, micDisplay);
        if (CmbMicrophone.SelectedItem == null && CmbMicrophone.Items.Count > 0)
            CmbMicrophone.SelectedIndex = 0;

        UpdateAudioPanels();
    }

    private void UpdateAudioPanels()
    {
        if (SysAudioPanel == null || MicPanel == null) return;
        SysAudioPanel.Visibility = ChkSystemAudio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MicPanel.Visibility      = ChkMicrophone.IsChecked  == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChkAudio_Changed(object sender, RoutedEventArgs e) => UpdateAudioPanels();
    private void BtnRefreshAudio_Click(object sender, RoutedEventArgs e) => RefreshAudioDevices();

    private static void SelectComboByContent(ComboBox box, string value)
    {
        foreach (var raw in box.Items)
        {
            string? content = raw is ComboBoxItem cbi ? cbi.Content?.ToString() : raw?.ToString();
            if (content == value) { box.SelectedItem = raw; return; }
        }
    }

    private static void SelectComboByTag(ComboBox box, string value)
    {
        foreach (var raw in box.Items)
        {
            if (raw is ComboBoxItem cbi && cbi.Tag?.ToString() == value)
            { box.SelectedItem = cbi; return; }
        }
    }

    private void ApplySettingsFromUI()
    {
        if (int.TryParse(TxtClipDuration.Text, out int dur) && dur > 0)
            _settings.ClipDurationSeconds = dur;

        if (CmbFps.SelectedItem is ComboBoxItem fpsItem &&
            int.TryParse(fpsItem.Content?.ToString(), out int fps))
            _settings.Fps = fps;

        _settings.OutputFolder       = TxtOutputFolder.Text;
        _settings.FfmpegPath         = TxtFfmpegPath.Text;
        _settings.CaptureSystemAudio = ChkSystemAudio.IsChecked == true;
        _settings.SystemAudioDevice  = CmbSystemAudio.SelectedItem?.ToString() ?? "";
        _settings.CaptureMicrophone  = ChkMicrophone.IsChecked == true;
        _settings.MicrophoneDevice   = CmbMicrophone.SelectedItem?.ToString() ?? "";
    }

    private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        ApplySettingsFromUI();
        SettingsService.Save(_settings);
        RegisterHotkey();
        CheckFfmpeg();

        // Aufnahme neu starten damit neue Audiogeräte aktiv werden
        bool wasRecording = _recording?.IsRecording ?? false;
        if (wasRecording)
        {
            StopRecording();
            StartRecording();
            ShowNotification("Einstellungen gespeichert — Aufnahme neu gestartet.");
        }
        else
        {
            ShowNotification("Einstellungen gespeichert.");
        }
    }

    private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Clips-Ordner auswählen" };
        if (dlg.ShowDialog() == true) TxtOutputFolder.Text = dlg.FolderName;
    }

    private void BtnBrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "ffmpeg.exe auswählen",
            Filter = "FFmpeg|ffmpeg.exe|Alle Dateien|*.*"
        };
        if (dlg.ShowDialog() == true) TxtFfmpegPath.Text = dlg.FileName;
    }

    private void CmbModifier_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbModifier?.SelectedItem is ComboBoxItem item &&
            uint.TryParse(item.Tag?.ToString(), out uint mod))
            _settings.HotkeyModifiers = mod;
        UpdateHotkeyDisplay();
    }

    private void CmbKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbKey?.SelectedItem is ComboBoxItem item &&
            uint.TryParse(item.Tag?.ToString(), out uint vk))
            _settings.HotkeyVirtualKey = vk;
        UpdateHotkeyDisplay();
    }

    private void UpdateHotkeyDisplay()
    {
        if (TxtHotkeyDisplay == null) return;
        TxtHotkeyDisplay.Text =
            $"{ModifierName(_settings.HotkeyModifiers)} + {KeyName(_settings.HotkeyVirtualKey)}";
    }

    // ── FFmpeg ──────────────────────────────────────────────────────────────

    private void CheckFfmpeg()
    {
        string path = _settings.FfmpegPath;
        if (!Path.IsPathRooted(path))
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);

        if (File.Exists(path))
        {
            FfmpegStatusLabel.Text = "✓ ffmpeg.exe gefunden";
            FfmpegStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(22, 163, 74));
        }
        else
        {
            FfmpegStatusLabel.Text = "✗ Nicht gefunden!\nffmpeg.exe ins selbe Verzeichnis wie ClipperApp.exe legen.";
            FfmpegStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
        }
    }

    // ── Hotkey ──────────────────────────────────────────────────────────────

    private void RegisterHotkey()
    {
        _hotkey?.Dispose();
        _hotkey = new HotkeyService();
        bool ok = _hotkey.Register(this, _settings.HotkeyModifiers, _settings.HotkeyVirtualKey, OnHotkey);
        HotkeyLabel.Text = ok
            ? $"Hotkey: {ModifierName(_settings.HotkeyModifiers)}+{KeyName(_settings.HotkeyVirtualKey)}"
            : $"Hotkey konnte nicht registriert werden (bereits belegt?)";
    }

    private void OnHotkey()
    {
        Dispatcher.Invoke(() =>
        {
            if (!(_recording?.IsRecording ?? false))
            {
                ShowNotification("Aufnahme ist noch nicht aktiv!", isError: true);
                return;
            }
            TriggerSaveClip();
        });
    }

    // ── Aufnahme ────────────────────────────────────────────────────────────

    private void BtnStartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_recording?.IsRecording ?? false) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        ApplySettingsFromUI();
        _recording?.Dispose();
        _recording = new RecordingService(_settings);
        _recording.ClipSaved    += path => Dispatcher.Invoke(() => OnClipSaved(path));
        _recording.ErrorOccurred += msg  => Dispatcher.Invoke(() => ShowNotification(msg, isError: true));

        if (_recording.StartRecording())
            SetRecordingState(true);
    }

    private void StopRecording()
    {
        _recording?.StopRecording();
        SetRecordingState(false);
    }

    private void SetRecordingState(bool active)
    {
        if (active)
        {
            StatusDot.Fill      = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            StatusLabel.Text    = "● Aufnahme läuft";
            StatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            BtnStartStop.Content = "⏹  Stoppen";
            BtnStartStop.Style  = (Style)FindResource("DangerButton");
            BtnSaveClip.IsEnabled = true;
        }
        else
        {
            StatusDot.Fill      = (SolidColorBrush)FindResource("SubtextBrush");
            StatusLabel.Text    = "Nicht aktiv";
            StatusLabel.Foreground = (SolidColorBrush)FindResource("SubtextBrush");
            BtnStartStop.Content = "▶  Starten";
            BtnStartStop.Style  = (Style)FindResource("PrimaryButton");
            BtnSaveClip.IsEnabled = false;
        }
    }

    private void BtnSaveClip_Click(object sender, RoutedEventArgs e) => TriggerSaveClip();

    private void TriggerSaveClip()
    {
        BtnSaveClip.IsEnabled = false;
        ShowNotification("Clip wird gespeichert...");
        _recording!.SaveClipAsync();
    }

    private void OnClipSaved(string path)
    {
        var fi = new FileInfo(path);
        _clips.Insert(0, new ClipItem(fi.Name, fi.FullName,
            fi.CreationTime.ToString("dd.MM.yyyy HH:mm:ss")));
        BtnSaveClip.IsEnabled = _recording?.IsRecording ?? false;
        ShowNotification($"✓ Clip gespeichert: {fi.Name}");
    }

    // ── Clips-Liste ─────────────────────────────────────────────────────────

    private void LoadClipsList()
    {
        _clips.Clear();
        if (!Directory.Exists(_settings.OutputFolder)) return;
        foreach (var fi in Directory.GetFiles(_settings.OutputFolder, "*.mp4")
                     .Select(f => new FileInfo(f))
                     .OrderByDescending(f => f.CreationTime))
        {
            _clips.Add(new ClipItem(fi.Name, fi.FullName,
                fi.CreationTime.ToString("dd.MM.yyyy HH:mm:ss")));
        }
    }

    private void ClipsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool sel = ClipsList.SelectedItem != null;
        BtnOpenClip.IsEnabled   = sel;
        BtnDeleteClip.IsEnabled = sel;
    }

    private void BtnOpenClip_Click(object sender, RoutedEventArgs e)
    {
        if (ClipsList.SelectedItem is ClipItem clip && File.Exists(clip.FullPath))
            Process.Start(new ProcessStartInfo(clip.FullPath) { UseShellExecute = true });
    }

    private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
    {
        string log = Path.Combine(Path.GetTempPath(), "ClipperApp", "ffmpeg.log");
        if (File.Exists(log))
            Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
        else
            ShowNotification("Noch kein Log vorhanden — erst Aufnahme starten.", isError: true);
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.OutputFolder);
        Process.Start(new ProcessStartInfo(_settings.OutputFolder) { UseShellExecute = true });
    }

    private void BtnDeleteClip_Click(object sender, RoutedEventArgs e)
    {
        if (ClipsList.SelectedItem is not ClipItem clip) return;
        if (MessageBox.Show($"'{clip.Name}' wirklich löschen?", "Bestätigung",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { File.Delete(clip.FullPath); _clips.Remove(clip); }
        catch (Exception ex) { ShowNotification($"Fehler: {ex.Message}", isError: true); }
    }

    // ── Benachrichtigung ────────────────────────────────────────────────────

    private void ShowNotification(string msg, bool isError = false)
    {
        NotificationLabel.Text = msg;
        NotificationLabel.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(220, 38, 38))
            : new SolidColorBrush(Color.FromRgb(22, 163, 74));
        NotificationBorder.Visibility = Visibility.Visible;
    }

    // ── Fenster ─────────────────────────────────────────────────────────────

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _recording?.StopRecording();
        _recording?.Dispose();
        _hotkey?.Dispose();
    }

    // ── Hilfsfunktionen ─────────────────────────────────────────────────────

    private static string ModifierName(uint m) => m switch
    {
        1 => "Alt", 2 => "Ctrl", 4 => "Shift", 5 => "Alt+Shift", 3 => "Ctrl+Alt", _ => m.ToString()
    };

    private static string KeyName(uint vk) => vk switch
    {
        112 => "F1",  113 => "F2",  114 => "F3",  115 => "F4",
        116 => "F5",  117 => "F6",  118 => "F7",  119 => "F8",
        120 => "F9",  121 => "F10", 122 => "F11", 123 => "F12",
        _ => $"0x{vk:X2}"
    };
}
