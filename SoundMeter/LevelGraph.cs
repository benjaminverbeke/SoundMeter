using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;

namespace SoundMeter;

/// <summary>
/// Scrolling line graph of the last 30 seconds of dB values (newest on the right).
/// Drawn directly on a Canvas: no chart library, so nothing is added to the APK.
/// </summary>
sealed class LevelGraph : View
{
    const int Capacity = 300;            // 30 s at 10 values per second
    const float MinDb = 20, MaxDb = 120; // fixed vertical scale
    const int GridStepDb = 20;
    const int ValuesPerSecond = 10;      // matches the 100 ms measurement blocks
    const int GridStepSeconds = 10;      // vertical time lines every 10 s

    readonly float[] _values = new float[Capacity]; // ring buffer, without user trim
    int _count, _next;
    readonly Paint _line, _grid, _label;
    readonly Android.Graphics.Path _path = new();
    readonly float _labelWidth, _pad, _axisHeight;
    double _trim;

    public LevelGraph(Context context) : base(context)
    {
        var dm = context.Resources!.DisplayMetrics!;
        _labelWidth = 32 * dm.Density;
        _pad = 6 * dm.Density;

        _line = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.Rgb(0x26, 0xA6, 0x9A),
            StrokeWidth = 2 * dm.Density,
            StrokeJoin = Paint.Join.Round,
        };
        _line.SetStyle(Paint.Style.Stroke);

        _grid = new Paint { Color = Color.Gray, StrokeWidth = 1 };
        _grid.Alpha = 90;

        _label = new Paint(PaintFlags.AntiAlias)
        {
            Color = Color.Gray,
            TextSize = TypedValue.ApplyDimension(ComplexUnitType.Sp, 11, dm),
        };
        _axisHeight = _label.TextSize + 4 * dm.Density; // room for the time labels below the plot
    }

    /// <summary>User calibration trim added at draw time, so changing it redraws the whole history.</summary>
    public double Trim
    {
        get => _trim;
        set { _trim = value; Invalidate(); }
    }

    public void Add(double db)
    {
        _values[_next] = (float)db;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float w = Width;

        for (int db = (int)MinDb; db <= MaxDb; db += GridStepDb)
        {
            float y = ToY(db);
            canvas.DrawLine(_labelWidth, y, w, y, _grid);
            canvas.DrawText(db.ToString(), 0, y + _label.TextSize / 3, _label);
        }

        float dx = (w - _labelWidth) / (Capacity - 1);

        // Time axis: vertical lines every 10 s, labelled "-30 s", "-20 s", "-10 s", "now".
        float top = ToY(MaxDb), bottom = ToY(MinDb);
        float labelY = bottom + _axisHeight;
        int span = Capacity / ValuesPerSecond;
        for (int s = 0; s <= span; s += GridStepSeconds)
        {
            float lx = w - 1 - s * ValuesPerSecond * dx;
            canvas.DrawLine(lx, top, lx, bottom, _grid);
            string text = s == 0 ? "now" : $"−{s} s";
            float tw = _label.MeasureText(text);
            float tx = Math.Clamp(lx - tw / 2, _labelWidth, w - tw);
            canvas.DrawText(text, tx, labelY, _label);
        }

        if (_count < 2) return;

        float x = w - (_count - 1) * dx;
        int start = (_next - _count + Capacity) % Capacity;

        _path.Reset();
        for (int i = 0; i < _count; i++, x += dx)
        {
            float y = ToY(_values[(start + i) % Capacity] + _trim);
            if (i == 0) _path.MoveTo(x, y);
            else _path.LineTo(x, y);
        }
        canvas.DrawPath(_path, _line);
    }

    float ToY(double db)
    {
        double clamped = Math.Clamp(db, MinDb, MaxDb);
        float h = Height - 2 * _pad - _axisHeight;
        return _pad + h - (float)((clamped - MinDb) / (MaxDb - MinDb)) * h;
    }
}
