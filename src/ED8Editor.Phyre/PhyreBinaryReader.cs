using System.Buffers.Binary;
using System.Text;

namespace ED8Editor.Phyre;

internal sealed class PhyreBinaryReader
{
    private readonly ReadOnlyMemory<byte> data;

    public PhyreBinaryReader(ReadOnlyMemory<byte> data, bool bigEndian)
    {
        this.data = data;
        IsBigEndian = bigEndian;
    }

    public bool IsBigEndian { get; }
    public int Position { get; set; }
    public int Length => data.Length;

    public uint ReadUInt32()
    {
        EnsureAvailable(4);
        var span = data.Span.Slice(Position, 4);
        Position += 4;
        return IsBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(span)
            : BinaryPrimitives.ReadUInt32LittleEndian(span);
    }

    public string ReadAsciiZ(int offset, int maximumLength)
    {
        if (offset < 0 || maximumLength <= 0 || offset > data.Length - maximumLength)
        {
            throw new InvalidPhyreException("Phyre string range lies outside the cluster.");
        }

        var span = data.Span.Slice(offset, maximumLength);
        var terminator = span.IndexOf((byte)0);
        if (terminator <= 0)
        {
            throw new InvalidPhyreException("Phyre string is empty or not null-terminated.");
        }

        for (var index = 0; index < terminator; index++)
        {
            if (span[index] is < 0x20 or > 0x7e)
            {
                throw new InvalidPhyreException("Phyre string contains non-ASCII data.");
            }
        }

        return Encoding.ASCII.GetString(span[..terminator]);
    }

    public void Seek(long position)
    {
        if (position < 0 || position > data.Length)
        {
            throw new InvalidPhyreException("Phyre offset lies outside the cluster.");
        }

        Position = checked((int)position);
    }

    public void Skip(long count) => Seek((long)Position + count);

    private void EnsureAvailable(int count)
    {
        if (Position < 0 || count < 0 || Position > data.Length - count)
        {
            throw new InvalidPhyreException("Unexpected end of Phyre cluster.");
        }
    }
}
