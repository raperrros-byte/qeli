using System.Buffers.Binary;

namespace Qeli.Shared.Geo;

/// <summary>Minimal protobuf wire reader for v2ray geosite/geoip .dat files.</summary>
internal ref struct ProtoReader
{
    private ReadOnlySpan<byte> _data;
    private int _pos;

    public ProtoReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public bool HasMore => _pos < _data.Length;

    public bool TryReadTag(out int fieldNumber, out int wireType)
    {
        fieldNumber = 0;
        wireType = 0;
        if (!HasMore) return false;
        ulong key = ReadVarint();
        fieldNumber = (int)(key >> 3);
        wireType = (int)(key & 0x7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        while (_pos < _data.Length)
        {
            byte b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new InvalidDataException("varint too long");
        }
        throw new InvalidDataException("truncated varint");
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        int len = (int)ReadVarint();
        if (len < 0 || _pos + len > _data.Length) throw new InvalidDataException("bad length-delimited");
        var s = _data.Slice(_pos, len);
        _pos += len;
        return s;
    }

    public string ReadString() => System.Text.Encoding.UTF8.GetString(ReadBytes());

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarint(); break;
            case 1: _pos += 8; break;
            case 2: _ = ReadBytes(); break;
            case 5: _pos += 4; break;
            default: throw new InvalidDataException($"unsupported wire type {wireType}");
        }
    }
}
