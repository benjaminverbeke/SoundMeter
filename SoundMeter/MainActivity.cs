using Android;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Media;
using Android.Views;
using Android.Widget;

namespace SoundMeter;

[Activity(Label = "Sound Meter", MainLauncher = true,
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout)]
public class MainActivity : Activity
{
    // Microphones report levels relative to digital full scale (dBFS, always <= 0).
    // Adding an offset gives an approximate dB SPL value. Where the offset comes from, best first:
    //  1. The phone reports its own mic sensitivity (MicrophoneInfo, Android 9+, often "unknown").
    //  2. The sensitivity the Android Compatibility Definition Document requires:
    //     - UNPROCESSED:       94 dB SPL @ 1 kHz -> RMS 520  (-36 dBFS)   => offset ~130 dB
    //     - VOICE_RECOGNITION: 90 dB SPL @ 1 kHz -> RMS 2500 (-22.3 dBFS) => offset ~112 dB
    // On top of that, the user can trim the value with the on-screen - / + buttons.
    const double UnprocessedOffsetDb = 94.0 + 36.0;
    const double VoiceRecognitionOffsetDb = 90.0 + 22.3;

    const int SampleRate = 44100;
    const int RequestMic = 1;
    const string PrefTrim = "trim";

    TextView _level = null!;
    TextView _minMax = null!;
    TextView _trimLabel = null!;
    TextView _calibInfo = null!;
    TextView _unit = null!;
    TextView _resetHint = null!;
    LinearLayout _trimRow = null!;
    FrameLayout _container = null!;
    LevelGraph _graph = null!;
    ISharedPreferences _prefs = null!;
    AudioRecord? _recorder;
    Thread? _thread;
    volatile bool _running;
    bool _sensitivityChecked;
    double _offsetDb, _trimDb;
    // Levels without the user trim, so changing the trim updates everything instantly.
    double _min = double.MaxValue, _max = double.MinValue, _smoothed = double.NaN;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _prefs = GetPreferences(FileCreationMode.Private)!;
        _trimDb = _prefs.GetInt(PrefTrim, 0);

        _level = Text("--", 96, bold: true);
        _unit = Text("dB(A)", 28);
        _graph = new LevelGraph(this) { Trim = _trimDb };
        _minMax = Text("min --   max --", 18);
        _resetHint = Text("Tap anywhere to reset min / max", 12);
        _resetHint.Alpha = 0.6f;

        var minus = new Button(this) { Text = "−" };
        var plus = new Button(this) { Text = "+" };
        minus.Click += (_, _) => SetTrim(_trimDb - 1);
        plus.Click += (_, _) => SetTrim(_trimDb + 1);
        _trimLabel = Text("", 18);
        _trimLabel.SetPadding(Dp(12), 0, Dp(12), 0);

        _trimRow = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
        _trimRow.SetGravity(GravityFlags.Center);
        _trimRow.AddView(minus);
        _trimRow.AddView(_trimLabel);
        _trimRow.AddView(plus);

        _calibInfo = Text("", 12);
        _calibInfo.Alpha = 0.6f;

        // Permanent container: handles "tap anywhere" and keeps content clear of the
        // status / navigation bars and camera cutout (the app is drawn edge-to-edge).
        _container = new FrameLayout(this);
        _container.SetFitsSystemWindows(true);
        _container.Click += (_, _) => { _min = double.MaxValue; _max = double.MinValue; };
        SetContentView(_container);

        Arrange(Resources!.Configuration!.Orientation == Android.Content.Res.Orientation.Landscape);
        UpdateTrimLabel();
    }

    // The activity is not recreated on rotation (see ConfigurationChanges), so measuring and
    // the graph history keep running; only the views are rearranged.
    public override void OnConfigurationChanged(Android.Content.Res.Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        Arrange(newConfig.Orientation == Android.Content.Res.Orientation.Landscape);
    }

    // Portrait: one column, graph under the value.
    // Landscape: value and controls on the left, graph filling the right half.
    void Arrange(bool landscape)
    {
        View[] views = [_level, _unit, _graph, _minMax, _resetHint, _trimRow, _calibInfo];
        foreach (var v in views) (v.Parent as ViewGroup)?.RemoveView(v);
        _container.RemoveAllViews();

        _level.SetTextSize(Android.Util.ComplexUnitType.Sp, landscape ? 72 : 96);
        _minMax.SetPadding(0, Dp(landscape ? 8 : 16), 0, 0);
        _resetHint.SetPadding(0, 0, 0, Dp(landscape ? 8 : 16));

        var info = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Vertical };
        info.SetGravity(GravityFlags.Center);
        info.AddView(_level);
        info.AddView(_unit);

        LinearLayout root;
        if (landscape)
        {
            info.AddView(_minMax);
            info.AddView(_resetHint);
            info.AddView(_trimRow);
            info.AddView(_calibInfo);

            root = new LinearLayout(this) { Orientation = Android.Widget.Orientation.Horizontal };
            root.SetGravity(GravityFlags.CenterVertical);
            root.AddView(info, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));
            var graphParams = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1.2f);
            graphParams.SetMargins(Dp(8), Dp(16), Dp(16), Dp(16));
            root.AddView(_graph, graphParams);
        }
        else
        {
            root = info;
            root.SetGravity(GravityFlags.Center);
            var graphParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(180));
            graphParams.SetMargins(Dp(16), Dp(16), Dp(16), 0);
            root.AddView(_graph, graphParams);
            root.AddView(_minMax);
            root.AddView(_resetHint);
            root.AddView(_trimRow);
            root.AddView(_calibInfo);
        }

        _container.AddView(root, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
    }

    int Dp(float dp) => (int)(dp * Resources!.DisplayMetrics!.Density + 0.5f);

    TextView Text(string text, float sp, bool bold = false)
    {
        var tv = new TextView(this) { Text = text, Gravity = GravityFlags.Center };
        tv.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        if (bold) tv.SetTypeface(null, TypefaceStyle.Bold);
        return tv;
    }

    void SetTrim(double value)
    {
        _trimDb = Math.Clamp(value, -60, 60);
        _prefs.Edit()!.PutInt(PrefTrim, (int)_trimDb)!.Apply();
        UpdateTrimLabel();
        _graph.Trim = _trimDb;
        if (!double.IsNaN(_smoothed)) ShowLevels(_smoothed, _min, _max);
    }

    void UpdateTrimLabel() => _trimLabel.Text = $"Adjust {_trimDb:+0;-0;0} dB";

    protected override void OnResume()
    {
        base.OnResume();
        if (CheckSelfPermission(Manifest.Permission.RecordAudio) == Permission.Granted)
            Start();
        else
            RequestPermissions([Manifest.Permission.RecordAudio], RequestMic);
    }

    protected override void OnPause()
    {
        Stop();
        base.OnPause();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != RequestMic) return;
        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
            Start();
        else
            _minMax.Text = "Microphone permission is required";
    }

    void Start()
    {
        if (_running) return;

        int minBuf = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        int bufSize = Math.Max(minBuf, SampleRate / 10 * 2); // ~100 ms of 16-bit samples

        // UNPROCESSED skips automatic gain control / noise suppression when the phone supports it.
        var am = (AudioManager)GetSystemService(AudioService)!;
        bool unprocessed = am.GetProperty(AudioManager.PropertySupportAudioSourceUnprocessed) == "true";
        var source = unprocessed ? AudioSource.Unprocessed : AudioSource.VoiceRecognition;
        _offsetDb = unprocessed ? UnprocessedOffsetDb : VoiceRecognitionOffsetDb;
        _calibInfo.Text = unprocessed
            ? "Calibration: Android standard (unprocessed mic)"
            : "Calibration: Android standard (voice mic)";

        _recorder = new AudioRecord(source, SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, bufSize);
        if (_recorder.State != State.Initialized)
        {
            _recorder.Release();
            _recorder = null;
            _minMax.Text = "Cannot open the microphone";
            return;
        }

        _running = true;
        _recorder.StartRecording();
        _sensitivityChecked = !unprocessed; // reported sensitivity only matches the raw (unprocessed) signal
        _thread = new Thread(() => ReadLoop(_recorder, bufSize / 2)) { IsBackground = true };
        _thread.Start();
    }

    // Uses the mic sensitivity the phone reports, if any: dBFS produced by a 94 dB SPL tone.
    void TryUseReportedSensitivity(AudioRecord recorder)
    {
        _sensitivityChecked = true;
        if (!OperatingSystem.IsAndroidVersionAtLeast(28)) return;
        try
        {
            var mics = recorder.ActiveMicrophones;
            if (mics == null) return;
            foreach (var mic in mics)
            {
                float s = mic.Sensitivity;
                if (s == MicrophoneInfo.SensitivityUnknown || s >= 0 || s < -80) continue;
                _offsetDb = 94.0 - s;
                RunOnUiThread(() => _calibInfo.Text = $"Calibration: reported by phone ({s:0.0} dBFS @ 94 dB)");
                return;
            }
        }
        catch (Java.Lang.Exception) { }
    }

    void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;
        if (_recorder != null)
        {
            try { _recorder.Stop(); } catch (Java.Lang.IllegalStateException) { }
            _recorder.Release();
            _recorder = null;
        }
    }

    void ReadLoop(AudioRecord recorder, int samples)
    {
        var buffer = new short[samples];
        // A-weighting also removes any DC bias of the microphone (it is a high-pass below ~20 Hz).
        var weighting = new AWeighting(SampleRate);
        int warmUp = 3; // skip the first buffers while the filter settles
        while (_running)
        {
            int read = recorder.Read(buffer, 0, buffer.Length);
            if (read <= 0) continue;
            if (!_sensitivityChecked) TryUseReportedSensitivity(recorder);

            double sum = 0;
            for (int i = 0; i < read; i++)
            {
                double s = weighting.Process(buffer[i]);
                sum += s * s;
            }
            if (warmUp > 0) { warmUp--; continue; }
            double rms = Math.Sqrt(sum / read);

            double db = 20 * Math.Log10(Math.Max(rms, 0.1) / 32768.0) + _offsetDb;

            _smoothed = double.IsNaN(_smoothed) ? db : _smoothed * 0.7 + db * 0.3;
            _min = Math.Min(_min, _smoothed);
            _max = Math.Max(_max, _smoothed);

            double cur = _smoothed, min = _min, max = _max;
            RunOnUiThread(() =>
            {
                _graph.Add(cur);
                ShowLevels(cur, min, max);
            });
        }
    }

    void ShowLevels(double cur, double min, double max)
    {
        double t = _trimDb;
        _level.Text = Math.Max(0, cur + t).ToString("0");
        if (max >= min)
            _minMax.Text = $"min {Math.Max(0, min + t):0}   max {Math.Max(0, max + t):0}";
    }
}
