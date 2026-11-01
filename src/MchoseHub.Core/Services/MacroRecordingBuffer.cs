using MchoseHub.Core.Protocol;

namespace MchoseHub.Core.Services;

/// <summary>Captures keyboard events while reserving room to release every held key.</summary>
public sealed class MacroRecordingBuffer
{
    public const int MaxEvents = 15000;
    private readonly List<MacroStep> _steps = [];
    private readonly List<string> _held = [];
    private long _lastEventMs;
    public IReadOnlyList<MacroStep> Steps => _steps.AsReadOnly();
    public bool CapacityReached { get; private set; }

    public bool Record(string key, bool down, long elapsedMs)
    {
        if (string.IsNullOrEmpty(key) || !KeyboardFunctions.MacroKeys.ContainsKey(key))
            throw new ArgumentException("未收录的录制按键。", nameof(key));
        if (elapsedMs < 0 || elapsedMs < _lastEventMs) throw new ArgumentOutOfRangeException(nameof(elapsedMs));
        bool held = _held.Contains(key, StringComparer.Ordinal);
        if (down == held) return false; // Key repeat or a release captured without its press.
        if (down && _steps.Count + _held.Count + 2 > MaxEvents)
        {
            CapacityReached = true;
            return false;
        }

        if (_steps.Count > 0) _steps[^1] = _steps[^1] with { DelayMs = (int)Math.Clamp(elapsedMs - _lastEventMs, 0, 60000) };
        _steps.Add(new MacroStep(key, down, DelayMs: 0));
        _lastEventMs = elapsedMs;
        if (down) _held.Add(key); else _held.Remove(key);
        return true;
    }

    public void ReleaseHeld(long elapsedMs)
    {
        if (elapsedMs < 0 || elapsedMs < _lastEventMs) throw new ArgumentOutOfRangeException(nameof(elapsedMs));
        foreach (string key in _held.AsEnumerable().Reverse().ToArray()) Record(key, false, elapsedMs);
    }

    public void Clear()
    {
        _steps.Clear(); _held.Clear(); _lastEventMs = 0; CapacityReached = false;
    }
}
