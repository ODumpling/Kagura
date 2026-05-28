namespace Kagura.Core.Agents;

public sealed class RingBuffer
{
    public const int DefaultCapacityBytes = 256 * 1024;

    private readonly byte[] _buf;
    private readonly object _lock = new();
    private int _writePos;
    private long _totalWritten;

    public RingBuffer(int capacity = DefaultCapacityBytes)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buf = new byte[capacity];
    }

    public int Capacity => _buf.Length;

    public long TotalWritten { get { lock (_lock) return _totalWritten; } }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_lock)
        {
            if (data.Length >= _buf.Length)
            {
                data.Slice(data.Length - _buf.Length).CopyTo(_buf);
                _writePos = 0;
                _totalWritten += data.Length;
                return;
            }

            var firstChunk = Math.Min(data.Length, _buf.Length - _writePos);
            data.Slice(0, firstChunk).CopyTo(_buf.AsSpan(_writePos));
            if (firstChunk < data.Length)
                data.Slice(firstChunk).CopyTo(_buf.AsSpan(0));
            _writePos = (_writePos + data.Length) % _buf.Length;
            _totalWritten += data.Length;
        }
    }

    public byte[] Snapshot()
    {
        lock (_lock)
        {
            if (_totalWritten == 0) return Array.Empty<byte>();

            if (_totalWritten < _buf.Length)
            {
                var result = new byte[_totalWritten];
                Buffer.BlockCopy(_buf, 0, result, 0, (int)_totalWritten);
                return result;
            }

            var snap = new byte[_buf.Length];
            var tail = _buf.Length - _writePos;
            Buffer.BlockCopy(_buf, _writePos, snap, 0, tail);
            if (_writePos > 0)
                Buffer.BlockCopy(_buf, 0, snap, tail, _writePos);
            return snap;
        }
    }
}
