using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ClipperApp.Models;

namespace ClipperApp.Services;

public class RecordingService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private static (int w, int h) PrimaryMonitorSize() => (GetSystemMetrics(0), GetSystemMetrics(1));

    private readonly AppSettings _settings;
    private readonly string _tempDir;
    private readonly string _logFile;
    private Process? _ffmpegProcess;
    private int _maxSegments;
    // Tracks when the last clip was triggered so consecutive clips don't overlap
    private DateTime _lastClipSaveTime = DateTime.MinValue;

    public bool IsRecording { get; private set; }
    public event Action<string>? LogReceived;
    public event Action<string>? ClipSaved;
    public event Action<string>? ErrorOccurred;

    public RecordingService(AppSettings settings)
    {
        _settings = settings;
        _tempDir  = Path.Combine(Path.GetTempPath(), "ClipperApp", "buffer");
        _logFile  = Path.Combine(Path.GetTempPath(), "ClipperApp", "ffmpeg.log");
        Directory.CreateDirectory(_tempDir);
        _maxSegments = (int)Math.Ceiling((double)settings.ClipDurationSeconds / settings.SegmentDurationSeconds) + 2;
    }

    public bool StartRecording()
    {
        if (IsRecording) return true;

        string ffmpegPath = ResolveFfmpegPath();
        if (ffmpegPath == null) return false;

        foreach (var f in Directory.GetFiles(_tempDir, "seg*.ts"))
            try { File.Delete(f); } catch { }

        // Reset clip boundary so the first clip of a new session is always a full window.
        _lastClipSaveTime = DateTime.MinValue;

        string args = BuildArgs();

        _ffmpegProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            },
            EnableRaisingEvents = true
        };

        try { File.WriteAllText(_logFile, $"=== Start {DateTime.Now} ===\nArgs: {args}\n\n"); } catch { }
        _ffmpegProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            LogReceived?.Invoke(e.Data);
            try
            {
                using var fs = new FileStream(_logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs);
                sw.WriteLine(e.Data);
            }
            catch { }
        };
        _ffmpegProcess.Exited += (_, _) =>
        {
            if (IsRecording) { IsRecording = false; ErrorOccurred?.Invoke("FFmpeg wurde unerwartet beendet."); }
        };

        try
        {
            _ffmpegProcess.Start();
            _ffmpegProcess.BeginErrorReadLine();
            IsRecording = true;
            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"FFmpeg-Startfehler: {ex.Message}");
            return false;
        }
    }

    private string BuildArgs()
    {
        var (w, h) = PrimaryMonitorSize();
        string segPattern = Path.Combine(_tempDir, "seg%03d.ts");
        string segArgs = $"-f segment -segment_time {_settings.SegmentDurationSeconds} " +
                         $"-segment_wrap {_maxSegments} -reset_timestamps 1 \"{segPattern}\"";

        string videoInput = $"-f gdigrab -framerate {_settings.Fps} -draw_mouse 1 " +
                            $"-offset_x 0 -offset_y 0 -video_size {w}x{h} -i desktop";

        // -g forces a keyframe every SegmentDurationSeconds so the segment muxer can
        // always cut exactly on time. Without this libx264 defaults to 250 frames
        // (~8 s at 30 fps) and segments end up irregularly sized.
        int gop = _settings.Fps * _settings.SegmentDurationSeconds;
        string videoCodec = $"-c:v libx264 -preset ultrafast -tune zerolatency -crf 23 -pix_fmt yuv420p -g {gop} -keyint_min {gop}";

        bool hasSys = _settings.CaptureSystemAudio && !string.IsNullOrEmpty(_settings.SystemAudioDevice);
        bool hasMic = _settings.CaptureMicrophone  && !string.IsNullOrEmpty(_settings.MicrophoneDevice);

        // Gerät-ID ist "AnzeigeName|GUID-ID" → GUID verwenden (ASCII-sicher)
        string sysId = DeviceId(_settings.SystemAudioDevice);
        string micId = DeviceId(_settings.MicrophoneDevice);

        string audioFlags = "-thread_queue_size 512 -use_wallclock_as_timestamps 1 -f dshow";
        string muxFlags   = "-max_interleave_delta 0 -avoid_negative_ts make_zero";

        // Dasselbe Gerät darf nicht zweimal geöffnet werden → kein amix
        bool sameDevice = hasSys && hasMic && sysId == micId;
        if (sameDevice) hasMic = false;

        if (hasSys && hasMic)
        {
            return $"-y {videoInput} " +
                   $"{audioFlags} -i audio=\"{sysId}\" " +
                   $"{audioFlags} -i audio=\"{micId}\" " +
                   $"-filter_complex \"[1:a][2:a]amix=inputs=2:duration=longest:dropout_transition=2[aout]\" " +
                   $"-map 0:v -map \"[aout]\" " +
                   $"{videoCodec} -c:a aac -b:a 192k {muxFlags} " +
                   segArgs;
        }
        else if (hasSys)
        {
            return $"-y {videoInput} " +
                   $"{audioFlags} -i audio=\"{sysId}\" " +
                   $"-map 0:v -map 1:a " +
                   $"{videoCodec} -c:a aac -b:a 192k {muxFlags} " +
                   segArgs;
        }
        else if (hasMic)
        {
            return $"-y {videoInput} " +
                   $"{audioFlags} -i audio=\"{micId}\" " +
                   $"-map 0:v -map 1:a " +
                   $"{videoCodec} -c:a aac -b:a 192k {muxFlags} " +
                   segArgs;
        }
        else
        {
            return $"-y {videoInput} {videoCodec} -an " + segArgs;
        }
    }

    public void StopRecording()
    {
        if (!IsRecording || _ffmpegProcess == null) return;
        try
        {
            _ffmpegProcess.StandardInput.Write("q");
            _ffmpegProcess.StandardInput.Flush();
            if (!_ffmpegProcess.WaitForExit(5000)) _ffmpegProcess.Kill();
        }
        catch { try { _ffmpegProcess.Kill(); } catch { } }
        IsRecording = false;
    }

    public void SaveClipAsync()
    {
        // Capture the exact moment the user triggered the save.
        // This is used both for the overlap guard and for updating _lastClipSaveTime.
        DateTime triggerTime = DateTime.Now;

        Task.Run(() =>
        {
            try
            {
                // Refresh() forces the OS to flush cached metadata so LastWriteTime is accurate.
                var allSegs = Directory.GetFiles(_tempDir, "seg*.ts")
                    .Select(f => { var fi = new FileInfo(f); fi.Refresh(); return fi; })
                    .Where(fi => fi.Length > 1000)
                    .OrderBy(fi => fi.LastWriteTime)
                    .ToList();

                if (allSegs.Count == 0)
                {
                    ErrorOccurred?.Invoke("Keine Aufnahmedaten vorhanden.");
                    return;
                }

                // The last entry (most recent LastWriteTime) is the segment FFmpeg is currently
                // writing. It is incomplete/open, so exclude it from any clip.
                var complete = allSegs.Count > 1
                    ? allSegs.Take(allSegs.Count - 1).ToList()
                    : allSegs;

                int needed = (int)Math.Ceiling(
                    (double)_settings.ClipDurationSeconds / _settings.SegmentDurationSeconds);

                List<FileInfo> toMerge;

                bool withinClipWindow = _lastClipSaveTime != DateTime.MinValue
                    && (triggerTime - _lastClipSaveTime).TotalSeconds < _settings.ClipDurationSeconds;

                if (withinClipWindow)
                {
                    // Second (or later) clip triggered before the full clip window has elapsed.
                    // Only take segments completed AFTER the last clip was triggered so the
                    // two clips don't share any footage.
                    toMerge = complete
                        .Where(fi => fi.LastWriteTime > _lastClipSaveTime)
                        .ToList();

                    if (toMerge.Count == 0)
                    {
                        ErrorOccurred?.Invoke("Noch kein neues Material seit dem letzten Clip vorhanden.");
                        return;
                    }
                }
                else
                {
                    // Normal path: grab the most recent N complete segments (= ClipDuration seconds).
                    toMerge = complete.TakeLast(Math.Min(needed, complete.Count)).ToList();
                }

                string listFile = Path.Combine(_tempDir, "concat.txt");
                File.WriteAllLines(listFile,
                    toMerge.Select(f => $"file '{f.FullName.Replace("\\", "/")}'"));

                Directory.CreateDirectory(_settings.OutputFolder);
                string output = Path.Combine(_settings.OutputFolder,
                    $"Clip_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

                string ffmpegPath = ResolveFfmpegPath()!;
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{output}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                proc.Start();
                proc.WaitForExit(30000);

                if (proc.ExitCode == 0 && File.Exists(output))
                {
                    _lastClipSaveTime = triggerTime;
                    ClipSaved?.Invoke(output);
                }
                else
                {
                    ErrorOccurred?.Invoke("Fehler beim Speichern des Clips.");
                }
            }
            catch (Exception ex) { ErrorOccurred?.Invoke($"Clip-Fehler: {ex.Message}"); }
        });
    }

    // Listet alle dshow-Audiogeräte auf (Mikrofone)
    public static List<string> GetMicrophoneDevices(string ffmpegPath)
    {
        var devices = new List<string>();
        if (!Path.IsPathRooted(ffmpegPath))
            ffmpegPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ffmpegPath);
        if (!File.Exists(ffmpegPath)) return devices;

        try
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-list_devices true -f dshow -i dummy",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    StandardErrorEncoding = System.Text.Encoding.UTF8,
                }
            };
            proc.Start();
            string output = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            // Gerätename (audio) gefolgt von Alternative name → GUID verwenden
            string? pendingName = null;
            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("(audio)"))
                {
                    int s = line.IndexOf('"') + 1;
                    int e = line.IndexOf('"', s);
                    if (e > s) pendingName = line.Substring(s, e - s);
                }
                else if (pendingName != null && line.Contains("Alternative name"))
                {
                    int s = line.IndexOf('"') + 1;
                    int e = line.LastIndexOf('"');
                    // GUID-ID hat kein Unicode → ASCII-sicher
                    string id = e > s ? line.Substring(s, e - s) : pendingName;
                    devices.Add($"{pendingName}|{id}");
                    pendingName = null;
                }
                else if (pendingName != null && !line.Contains("Alternative"))
                {
                    // Kein Alternative name vorhanden → Anzeigename als Fallback
                    devices.Add($"{pendingName}|{pendingName}");
                    pendingName = null;
                }
            }
        }
        catch { }
        return devices;
    }

    // Gibt die GUID-ID zurück (Teil nach '|'), Fallback auf den ganzen Wert
    private static string DeviceId(string stored)
    {
        int pipe = stored.IndexOf('|');
        return pipe >= 0 ? stored[(pipe + 1)..] : stored;
    }

    // Gibt den Anzeigenamen zurück (Teil vor '|')
    public static string DeviceDisplayName(string stored)
    {
        int pipe = stored.IndexOf('|');
        return pipe >= 0 ? stored[..pipe] : stored;
    }

    private string? ResolveFfmpegPath()
    {
        string path = _settings.FfmpegPath;
        if (!Path.IsPathRooted(path))
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        if (!File.Exists(path))
        {
            ErrorOccurred?.Invoke($"ffmpeg.exe nicht gefunden:\n{path}");
            return null;
        }
        return path;
    }

    public void Dispose()
    {
        StopRecording();
        _ffmpegProcess?.Dispose();
    }
}
