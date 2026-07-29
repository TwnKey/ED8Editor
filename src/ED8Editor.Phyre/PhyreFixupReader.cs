using ED8Editor.Core;
using System.Text;

namespace ED8Editor.Phyre;

public sealed class PhyreFixupReader : IPhyreFixupReader
{
    /// <summary>
    /// How many blocks of each packing, and how many fixups they carried, have
    /// been read so far. Writing these tables back needs to know which packings
    /// are actually used and how much they save; counting what the game's files
    /// contain is surer than guessing what its writer would choose.
    ///
    /// Counters rather than a dictionary: clusters are read from several threads,
    /// and a diagnostic must not be able to break a load.
    /// </summary>
    public static IReadOnlyList<(int Blocks, int Fixups)> PackingCensus
        => Enumerable.Range(0, PackingKinds)
            .Select(index => (BlocksByPacking[index], FixupsByPacking[index]))
            .ToArray();

    /// <summary>
    /// Where each block started, how it was packed and what it covered. Filled
    /// only while <see cref="TraceBlocks"/> is on, to compare a table the game
    /// shipped with one this project writes.
    /// </summary>
    public static List<(long Offset, int Packing, uint Mask, uint Source, int Count)> Blocks { get; }
        = new();

    public static bool TraceBlocks { get; set; }

    private const int PackingKinds = 8;
    private static readonly int[] BlocksByPacking = new int[PackingKinds];
    private static readonly int[] FixupsByPacking = new int[PackingKinds];


    public PhyreFixupSet Read(ReadOnlyMemory<byte> data, PhyreClusterMetadata metadata)
    {
        var header = metadata.Header;
        var userDataOffset = checked(header.ObjectDataOffset + metadata.TotalDataSize);
        var userDescriptorOffset = checked(userDataOffset + header.UserFixupDataSize);
        var userFixups = ReadUserFixups(data, metadata, userDataOffset, userDescriptorOffset);
        var cursor = checked(userDescriptorOffset + (long)header.UserFixupCount * 12
            + (long)header.HeaderClassInstanceCount * 4
            + (long)header.HeaderClassChildCount * 16);

        var pointerArrayData = Slice(data, cursor, header.PointerArrayFixupSize, "pointer-array fixups");
        cursor += header.PointerArrayFixupSize;
        var pointerData = Slice(data, cursor, header.PointerFixupSize, "pointer fixups");
        cursor += header.PointerFixupSize;
        var arrayData = Slice(data, cursor, header.ArrayFixupSize, "array fixups");
        cursor += header.ArrayFixupSize;

        ValidateTotal(metadata.InstanceGroups.Sum(value => (long)value.PointerArrayFixupCount), header.PointerArrayFixupCount, "pointer-array");
        ValidateTotal(metadata.InstanceGroups.Sum(value => (long)value.PointerFixupCount), header.PointerFixupCount, "pointer");
        ValidateTotal(metadata.InstanceGroups.Sum(value => (long)value.ArrayFixupCount), header.ArrayFixupCount, "array");

        var pointerArrays = Decoder.DecodeArrays(pointerArrayData, metadata.InstanceGroups, value => value.PointerArrayFixupCount);
        var pointers = Decoder.DecodePointers(pointerData, metadata.InstanceGroups);
        var arrays = Decoder.DecodeArrays(arrayData, metadata.InstanceGroups, value => value.ArrayFixupCount);
        return new PhyreFixupSet(pointerArrays, pointers, arrays, userFixups, cursor);
    }

    private static IReadOnlyList<PhyreUserFixup> ReadUserFixups(
        ReadOnlyMemory<byte> data,
        PhyreClusterMetadata metadata,
        long userDataOffset,
        long descriptorOffset)
    {
        var reader = new PhyreBinaryReader(data, metadata.IsBigEndian);
        reader.Seek(descriptorOffset);
        var results = new PhyreUserFixup[metadata.Header.UserFixupCount];
        for (var index = 0; index < results.Length; index++)
        {
            var typeId = reader.ReadUInt32();
            var size = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            if ((ulong)offset + size > metadata.Header.UserFixupDataSize)
            {
                throw new InvalidPhyreException($"Phyre user fixup {index} lies outside its data block.");
            }

            var payload = data.Slice(checked((int)(userDataOffset + offset)), checked((int)size));
            var zero = payload.Span.IndexOf((byte)0);
            var text = zero >= 0 ? Encoding.ASCII.GetString(payload.Span[..zero]) : null;
            results[index] = new PhyreUserFixup(index, typeId, ResolveTypeName(typeId, metadata), size, offset, payload, text);
        }

        return results;
    }

    private static string? ResolveTypeName(uint typeId, PhyreClusterMetadata metadata)
    {
        if (typeId < metadata.Types.Count) return metadata.Types[checked((int)typeId)];
        var classIndex = (long)typeId - metadata.Types.Count - 1L;
        return classIndex >= 0 && classIndex < metadata.Classes.Count
            ? metadata.Classes[checked((int)classIndex)].Name
            : null;
    }

    private static ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> data, long offset, uint size, string name)
    {
        if (offset < 0 || offset > data.Length || size > data.Length - offset)
        {
            throw new InvalidPhyreException($"Phyre {name} lie outside the cluster.");
        }

        return data.Slice(checked((int)offset), checked((int)size));
    }

    private static void ValidateTotal(long groupTotal, uint headerTotal, string name)
    {
        if (groupTotal != headerTotal)
        {
            throw new InvalidPhyreException($"Phyre {name} fixup counts disagree ({groupTotal} != {headerTotal}).");
        }
    }

    private sealed class Decoder
    {
        private const int PackAll = 0;
        private const int PackGroupedTargets = 1;
        private const int PackInclusive = 2;
        private const int PackExclusive = 3;
        private const int PackBitmask = 4;
        private const int PackRaw = 5;
        private const int PackStrided = 6;
        private const uint ExcludeSource = 1;
        private const uint ExcludeSourceObject = 2;
        private const uint ExcludeArrayValue = 8;
        private const uint ExcludeUserFixup = 16;
        private const uint ExcludeDestinationList = 32;
        private const uint ExcludeDestinationOffset = 64;

        private readonly ReadOnlyMemory<byte> _data;
        private readonly bool _pointer;
        private int _position;
        private readonly List<Value> _values = new();

        private Decoder(ReadOnlyMemory<byte> data, bool pointer)
        {
            _data = data;
            _pointer = pointer;
        }

        public static IReadOnlyList<PhyreArrayFixup> DecodeArrays(
            ReadOnlyMemory<byte> data,
            IReadOnlyList<PhyreInstanceGroup> groups,
            Func<PhyreInstanceGroup, uint> countSelector)
        {
            var decoder = new Decoder(data, pointer: false);
            decoder.DecodeGroups(groups, countSelector);
            return decoder._values.Select(value => new PhyreArrayFixup(
                value.SourceListIndex, value.SourceObjectId, value.Source, value.Count, value.Offset)).ToArray();
        }

        public static IReadOnlyList<PhyrePointerFixup> DecodePointers(
            ReadOnlyMemory<byte> data,
            IReadOnlyList<PhyreInstanceGroup> groups)
        {
            var decoder = new Decoder(data, pointer: true);
            decoder.DecodeGroups(groups, value => value.PointerFixupCount);
            return decoder._values.Select(value => new PhyrePointerFixup(
                value.SourceListIndex,
                value.SourceObjectId,
                value.Source,
                value.DestinationList,
                value.DestinationObject,
                value.DestinationOffset,
                value.ArrayIndex,
                value.UserFixupId)).ToArray();
        }

        private void DecodeGroups(IReadOnlyList<PhyreInstanceGroup> groups, Func<PhyreInstanceGroup, uint> countSelector)
        {
            foreach (var group in groups)
            {
                var expectedEnd = checked(_values.Count + (int)countSelector(group));
                while (_values.Count < expectedEnd)
                {
                    DecodeBlock(group.Index, group.Count, expectedEnd);
                }

                if (_values.Count != expectedEnd)
                {
                    throw new InvalidPhyreException($"A Phyre fixup block overran instance list {group.Index}.");
                }
            }

            if (_position != _data.Length)
            {
                throw new InvalidPhyreException($"Phyre fixup stream has {_data.Length - _position} unread bytes.");
            }
        }

        private void DecodeBlock(int sourceListIndex, uint objectCount, int expectedEnd)
        {
            var packedTypeAndMask = ReadByte();
            var packType = packedTypeAndMask & 7;
            var before = _values.Count;
            Interlocked.Increment(ref BlocksByPacking[packType]);
            _censusType = packType;
            _censusBefore = before;
            _censusOffset = _position - 1;
            _censusMask = (uint)(packedTypeAndMask & ~7);
            var mask = (uint)(packedTypeAndMask & ~7);
            var fixupMask = mask | ExcludeSource;
            if (objectCount == 1) fixupMask |= ExcludeSourceObject;

            var template = new Value { SourceListIndex = sourceListIndex };
            template.Source = ReadSource();
            if (_pointer && (fixupMask & ExcludeDestinationList) != 0)
            {
                template.DestinationList = ReadVlq();
            }

            switch (packType)
            {
                case PackAll:
                    for (uint id = 0; id < objectCount; id++) AddWithPayload(template, id, fixupMask);
                    break;
                case PackInclusive:
                    DecodeSelected(template, objectCount, fixupMask, inclusive: true, payloadAfterIds: true);
                    break;
                case PackExclusive:
                    DecodeSelected(template, objectCount, fixupMask, inclusive: false, payloadAfterIds: true);
                    break;
                case PackBitmask:
                    DecodeBitmask(template, objectCount, fixupMask, idOnly: false);
                    break;
                case PackRaw:
                    var rawCount = ReadVlq();
                    for (uint index = 0; index < rawCount; index++)
                    {
                        var value = template.Clone();
                        UnpackBase(value, fixupMask);
                        UnpackPayload(value, fixupMask);
                        _values.Add(value);
                    }
                    break;
                case PackStrided:
                    DecodeStrided(template, fixupMask, idOnly: false);
                    break;
                case PackGroupedTargets:
                    var groupedEnd = checked(_values.Count + (int)objectCount);
                    if (groupedEnd > expectedEnd)
                    {
                        throw new InvalidPhyreException("A grouped Phyre fixup block exceeds its instance-list fixup count.");
                    }
                    while (_values.Count < groupedEnd)
                    {
                        var selectionType = ReadByte();
                        UnpackPayload(template, fixupMask);
                        switch (selectionType)
                        {
                            case PackAll:
                                for (uint id = 0; id < objectCount; id++) AddIdOnly(template, id);
                                break;
                            case PackInclusive:
                                DecodeSelected(template, objectCount, fixupMask, inclusive: true, payloadAfterIds: false);
                                break;
                            case PackExclusive:
                                DecodeSelected(template, objectCount, fixupMask, inclusive: false, payloadAfterIds: false);
                                break;
                            case PackBitmask:
                                DecodeBitmask(template, objectCount, fixupMask, idOnly: true);
                                break;
                            case PackStrided:
                                DecodeStrided(template, fixupMask, idOnly: true);
                                break;
                            default:
                                throw new InvalidPhyreException($"Unknown grouped Phyre fixup selection type {selectionType}.");
                        }
                    }
                    break;
                default:
                    throw new InvalidPhyreException($"Unknown Phyre fixup packing type {packType}.");
            }

            Interlocked.Add(ref FixupsByPacking[_censusType], _values.Count - _censusBefore);
            if (PhyreFixupReader.TraceBlocks)
            {
                PhyreFixupReader.Blocks.Add((
                    _censusOffset,
                    _censusType,
                    _censusMask,
                    template.Source,
                    _values.Count - _censusBefore));
            }
        }

        private int _censusType;
        private int _censusBefore;
        private long _censusOffset;
        private uint _censusMask;

        private void DecodeSelected(Value template, uint objectCount, uint mask, bool inclusive, bool payloadAfterIds)
        {
            var excludedOrIncludedCount = ReadVlq();
            var selected = new List<uint>();
            if (inclusive)
            {
                for (uint index = 0; index < excludedOrIncludedCount; index++) selected.Add(ReadObjectId(objectCount));
            }
            else
            {
                var excluded = new HashSet<uint>();
                for (uint index = 0; index < excludedOrIncludedCount; index++) excluded.Add(ReadObjectId(objectCount));
                for (uint id = 0; id < objectCount; id++) if (!excluded.Contains(id)) selected.Add(id);
            }

            var first = _values.Count;
            foreach (var id in selected) AddIdOnly(template, id);
            if (payloadAfterIds)
            {
                for (var index = first; index < _values.Count; index++) UnpackPayload(_values[index], mask);
            }
        }

        private void DecodeBitmask(Value template, uint objectCount, uint mask, bool idOnly)
        {
            var byteCount = checked((int)((objectCount + 7) / 8));
            EnsureAvailable(byteCount);
            var maskStart = _position;
            _position += byteCount;
            for (uint id = 0; id < objectCount; id++)
            {
                if ((_data.Span[maskStart + checked((int)(id / 8))] & (1 << checked((int)(id & 7)))) == 0) continue;
                if (idOnly) AddIdOnly(template, id); else AddWithPayload(template, id, mask);
            }
        }

        private void DecodeStrided(Value template, uint mask, bool idOnly)
        {
            var id = ReadVlq();
            var stride = ReadVlq();
            var count = ReadVlq();
            for (uint index = 0; index < count; index++, id = checked(id + stride))
            {
                if (idOnly) AddIdOnly(template, id); else AddWithPayload(template, id, mask);
            }
        }

        private void AddIdOnly(Value template, uint id)
        {
            var value = template.Clone();
            value.SourceObjectId = id;
            _values.Add(value);
        }

        private void AddWithPayload(Value template, uint id, uint mask)
        {
            var value = template.Clone();
            value.SourceObjectId = id;
            UnpackPayload(value, mask);
            _values.Add(value);
        }

        private void UnpackBase(Value value, uint mask)
        {
            if ((mask & ExcludeSource) == 0) value.Source = ReadSource();
            if ((mask & ExcludeSourceObject) == 0) value.SourceObjectId = ReadVlq();
        }

        private void UnpackPayload(Value value, uint mask)
        {
            if (!_pointer)
            {
                if ((mask & ExcludeArrayValue) == 0) value.Count = ReadVlq();
                value.Offset = ReadVlq();
                return;
            }

            var userFixup = false;
            if ((mask & ExcludeUserFixup) == 0)
            {
                var encodedUserFixup = ReadVlq();
                userFixup = encodedUserFixup != 0;
                value.UserFixupId = userFixup ? encodedUserFixup - 1 : null;
            }

            if (userFixup)
            {
                // A fixup that names a user fixup has no destination of its own.
                // The block may have hoisted a shared list into the template, and
                // letting that leak in here would say this fixup points at it.
                value.DestinationObject = 0;
                value.DestinationList = 0;
                value.DestinationOffset = 0;
            }
            else
            {
                value.DestinationObject = ReadVlq();
                if ((mask & ExcludeDestinationList) == 0) value.DestinationList = ReadVlq();
                if ((mask & ExcludeDestinationOffset) == 0) value.DestinationOffset = ReadVlq();
            }

            if ((mask & ExcludeArrayValue) == 0) value.ArrayIndex = ReadVlq();
        }

        private uint ReadSource()
        {
            var packed = ReadVlq();
            return (packed >> 1) | ((packed & 1) != 0 ? 0x80000000u : 0u);
        }

        private uint ReadObjectId(uint objectCount) => objectCount < 256 ? ReadByte() : ReadVlq();

        private byte ReadByte()
        {
            EnsureAvailable(1);
            return _data.Span[_position++];
        }

        private uint ReadVlq()
        {
            uint result = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var value = ReadByte();
                result |= (uint)(value & 0x7f) << shift;
                if ((value & 0x80) == 0) return result;
            }

            throw new InvalidPhyreException("Phyre fixup VLQ exceeds 32 bits.");
        }

        private void EnsureAvailable(int count)
        {
            if (count < 0 || _position > _data.Length - count)
            {
                throw new InvalidPhyreException("Phyre fixup stream is truncated.");
            }
        }

        private sealed class Value
        {
            public int SourceListIndex;
            public uint SourceObjectId;
            public uint Source;
            public uint Count;
            public uint Offset;
            public uint DestinationList;
            public uint DestinationObject;
            public uint DestinationOffset;
            public uint ArrayIndex;
            public uint? UserFixupId;

            public Value Clone() => (Value)MemberwiseClone();
        }
    }
}
