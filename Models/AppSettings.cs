using System.IO;

namespace ClipperApp.Models;

public class AppSettings
{
    public int ClipDurationSeconds { get; set; } = 30;
    public int Fps { get; set; } = 30;
    public int SegmentDurationSeconds { get; set; } = 10;
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClipperApp");
    public string FfmpegPath { get; set; } = "ffmpeg.exe";

    // Hotkey: Alt + F9 als Standard
    public uint HotkeyModifiers { get; set; } = 0x0001; // MOD_ALT
    public uint HotkeyVirtualKey { get; set; } = 0x78;  // VK_F9

    public bool CaptureSystemAudio { get; set; } = false;
    public string SystemAudioDevice { get; set; } = "";
    public bool CaptureMicrophone { get; set; } = false;
    public string MicrophoneDevice { get; set; } = "";
}
