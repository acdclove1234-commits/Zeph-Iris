// ==================================================
// PresentMonCsv.cs - PresentMon の CSV（1 行 = 1 フレーム）を読む
// Dependencies: .NET 8 (MIT License)
// See CREDITS.txt for full license details.
// ==================================================

using System.Globalization;

namespace ProFanController;

/// <summary>1 フレーム: どのアプリ（exe の名前・プロセス ID）が、前のフレームから何 ms で描いたか</summary>
public readonly record struct PresentFrame(string App, int ProcessId, double Ms);

/// <summary>
/// PresentMon の CSV の読み方。列の順番は版や指定で変わるので、1 行目の見出しで列を探す
/// （1.x の列名 msBetweenPresents と 2.x の MsBetweenPresents のどちらでも読む）
/// </summary>
public sealed class PresentMonCsv
{
    private int _app = -1, _pid = -1, _ms = -1, _columns;

    public bool HasHeader => _ms >= 0;

    /// <summary>見出しなら読み取って true（必要な列がなければ false）</summary>
    public bool TryHeader(string line)
    {
        if (!line.StartsWith("Application,", StringComparison.OrdinalIgnoreCase)) return false;
        var cols = line.Split(',');
        int Find(string name) => Array.FindIndex(cols, c => c.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        _app = Find("Application");
        _pid = Find("ProcessID");
        _ms = Find("MsBetweenPresents");
        _columns = cols.Length;
        if (_app < 0 || _pid < 0 || _ms < 0) _ms = -1;
        return HasHeader;
    }

    /// <summary>1 行を読む（見出し・警告・読めない行は false）</summary>
    public bool TryParse(string line, out PresentFrame frame)
    {
        frame = default;
        if (string.IsNullOrEmpty(line)) return false;
        if (TryHeader(line)) return false;
        if (!HasHeader) return false;
        var cols = line.Split(',');
        if (cols.Length < _columns || cols.Length <= _ms) return false;
        if (!int.TryParse(cols[_pid], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) return false;
        if (!double.TryParse(cols[_ms], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms)) return false;
        string app = cols[_app].Trim();
        if (app.Length == 0 || app.StartsWith('<')) return false;   // <unknown>・<error>
        frame = new PresentFrame(app, pid, ms);
        return true;
    }
}
