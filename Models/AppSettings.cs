using System.IO;

namespace ClipperApp.Models;

public class AppSettings
{
    public int ClipDurationSeconds { get; set; } = 30;
    public int Fps { get; set; } = 30;
    // Keep segments short so the currently-open (excluded) segment wastes
    // at most ~2 s of footage when the user triggers a clip save.
    public int SegmentDurationSeconds { get; set; } = 2;
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
