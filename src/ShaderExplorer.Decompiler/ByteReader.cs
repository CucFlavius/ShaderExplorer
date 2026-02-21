namespace ShaderExplorer.Decompiler;

/// <summary>
///     Little-endian binary reader over a byte span with position tracking.
/// </summary>
public ref struct ByteReader
{
    private readonly ReadOnlySpan<byte> _data;

    public ByteReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        Position = 0;
    }

    public int Position { get; set; }

    public int Length => _data.Length;
    public int Remaining => _data.Length - Position;

    public byte ReadByte()
    {
        return _data[Position++];
    }

    public ushort ReadUInt16()
    {
        var val = BitConverter.ToUInt16(_data.Slice(Position, 2));
        Position += 2;
        return val;
    }

    public uint ReadUInt32()
    {
        var val = BitConverter.ToUInt32(_data.Slice(Position, 4));
        Position += 4;
        return val;
    }

    public int ReadInt32()
    {
        var val = BitConverter.ToInt32(_data.Slice(Position, 4));
        Position += 4;
        return val;
    }

    public float ReadFloat()
    {
        var val = BitConverter.ToSingle(_data.Slice(Position, 4));
        Position += 4;
        return val;
    }

    public string ReadStringAtOffset(int baseOffset, int relativeOffset)
    {
        var absOffset = baseOffset + relativeOffset;
        var end = absOffset;
        while (end < _data.Length && _data[end] != 0)
            end++;
        return Encoding.ASCII.GetString(_data.Slice(absOffset, end - absOffset));
    }

    public string ReadNullTerminatedString()
    {
        var start = Position;
        while (Position < _data.Length && _data[Position] != 0)
            Position++;
        var str = Encoding.ASCII.GetString(_data.Slice(start, Position - start));
        if (Position < _data.Length) Position++; // skip null terminator
        return str;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        var span = _data.Slice(Position, count);
        Position += count;
        return span;
    }

    public void Skip(int count)
    {
        Position += count;
    }

    public void Align(int alignment)
    {
        var mod = Position % alignment;
        if (mod != 0)
            Position += alignment - mod;
    }

    public ReadOnlySpan<byte> Slice(int offset, int length)
    {
        return _data.Slice(offset, length);
    }

    public ByteReader SliceReader(int offset, int length)
    {
        return new ByteReader(_data.Slice(offset, length));
    }
}