using GeekFlashCore.Android.Lp.Abstractions;

namespace GeekFlashCore.Android.Lp;

internal static class LpIntervalMath
{
    internal static List<LpSectorRange> Normalize(IEnumerable<LpSectorRange> ranges)
    {
        var ordered = ranges
            .Where(range => range.SectorCount != 0)
            .OrderBy(range => range.TargetSource)
            .ThenBy(range => range.StartSector)
            .ToArray();
        var result = new List<LpSectorRange>(ordered.Length);
        foreach (LpSectorRange range in ordered)
        {
            if (result.Count == 0)
            {
                result.Add(range);
                continue;
            }

            LpSectorRange previous = result[^1];
            if (previous.TargetSource == range.TargetSource &&
                range.StartSector <= previous.EndSector)
            {
                ulong end = Math.Max(previous.EndSector, range.EndSector);
                result[^1] = previous with { SectorCount = checked(end - previous.StartSector) };
            }
            else
            {
                result.Add(range);
            }
        }
        return result;
    }

    internal static List<LpSectorRange> CreateFreeRanges(
        ReadOnlySpan<LpBlockDevice> devices,
        IReadOnlySet<uint> allowedDevices,
        IEnumerable<LpSectorRange> occupied)
    {
        List<LpSectorRange> normalized = Normalize(occupied);
        var result = new List<LpSectorRange>();
        for (int index = 0; index < devices.Length; index++)
        {
            uint source = checked((uint)index);
            if (!allowedDevices.Contains(source))
            {
                continue;
            }

            LpBlockDevice device = devices[index];
            ulong end = device.Size / LpFormat.SectorSize;
            if (device.FirstLogicalSector >= end)
            {
                continue;
            }

            ulong cursor = device.FirstLogicalSector;
            foreach (LpSectorRange range in normalized)
            {
                if (range.TargetSource != source || range.EndSector <= cursor)
                {
                    continue;
                }
                if (range.StartSector >= end)
                {
                    break;
                }
                if (range.StartSector > cursor)
                {
                    result.Add(new LpSectorRange(
                        source,
                        cursor,
                        checked(Math.Min(range.StartSector, end) - cursor)));
                }
                cursor = Math.Max(cursor, Math.Min(range.EndSector, end));
                if (cursor >= end)
                {
                    break;
                }
            }
            if (cursor < end)
            {
                result.Add(new LpSectorRange(source, cursor, checked(end - cursor)));
            }
        }
        return result;
    }

    internal static List<LpSectorRange> Subtract(
        IEnumerable<LpSectorRange> ranges,
        IEnumerable<LpSectorRange> exclusions)
    {
        List<LpSectorRange> sourceRanges = Normalize(ranges);
        List<LpSectorRange> removed = Normalize(exclusions);
        var result = new List<LpSectorRange>();
        foreach (LpSectorRange source in sourceRanges)
        {
            ulong cursor = source.StartSector;
            foreach (LpSectorRange exclusion in removed)
            {
                if (exclusion.TargetSource != source.TargetSource || exclusion.EndSector <= cursor)
                {
                    continue;
                }
                if (exclusion.StartSector >= source.EndSector)
                {
                    break;
                }
                if (exclusion.StartSector > cursor)
                {
                    result.Add(new LpSectorRange(
                        source.TargetSource,
                        cursor,
                        checked(Math.Min(exclusion.StartSector, source.EndSector) - cursor)));
                }
                cursor = Math.Max(cursor, Math.Min(exclusion.EndSector, source.EndSector));
                if (cursor >= source.EndSector)
                {
                    break;
                }
            }
            if (cursor < source.EndSector)
            {
                result.Add(new LpSectorRange(
                    source.TargetSource,
                    cursor,
                    checked(source.EndSector - cursor)));
            }
        }
        return result;
    }

    internal static bool Overlaps(LpSectorRange left, LpSectorRange right) =>
        left.TargetSource == right.TargetSource &&
        left.StartSector < right.EndSector &&
        right.StartSector < left.EndSector;
}

internal sealed class LpSectorAllocator
{
    private readonly LpBlockDevice[] _devices;
    private readonly ulong _logicalBlockSectors;
    private List<LpSectorRange> _free;

    internal LpSectorAllocator(
        LpBlockDevice[] devices,
        uint logicalBlockSize,
        IEnumerable<LpSectorRange> free)
    {
        _devices = devices;
        _logicalBlockSectors = logicalBlockSize / LpFormat.SectorSize;
        if (_logicalBlockSectors == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalBlockSize));
        }
        _free = LpIntervalMath.Normalize(free);
    }

    internal void AddRanges(IEnumerable<LpSectorRange> ranges) =>
        _free = LpIntervalMath.Normalize(_free.Concat(ranges));

    internal bool TryAllocate(ulong sectorCount, out LpExtent[] extents)
    {
        if (sectorCount == 0)
        {
            extents = [];
            return true;
        }
        if (sectorCount % _logicalBlockSectors != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorCount));
        }

        List<LpSectorRange> working = _free.ToList();
        var allocated = new List<LpExtent>();
        ulong remaining = sectorCount;
        for (int index = 0; index < working.Count && remaining != 0; index++)
        {
            LpSectorRange range = working[index];
            ulong start = AlignStart(range.TargetSource, range.StartSector, range.EndSector);
            if (start >= range.EndSector)
            {
                continue;
            }

            ulong available = checked(range.EndSector - start);
            ulong usable = available - available % _logicalBlockSectors;
            if (usable == 0)
            {
                continue;
            }
            ulong take = Math.Min(remaining, usable);
            Consume(working, index, start, take);
            AddOrMerge(allocated, range.TargetSource, start, take);
            remaining -= take;
            index--;
        }

        if (remaining != 0)
        {
            extents = [];
            return false;
        }

        _free = LpIntervalMath.Normalize(working);
        extents = allocated.ToArray();
        return true;
    }

    internal bool TryAllocateAt(
        uint targetSource,
        ulong startSector,
        ulong maximumSectorCount,
        out LpExtent extent)
    {
        if (maximumSectorCount == 0)
        {
            extent = default;
            return false;
        }

        for (int index = 0; index < _free.Count; index++)
        {
            LpSectorRange range = _free[index];
            if (range.TargetSource != targetSource ||
                startSector < range.StartSector ||
                startSector >= range.EndSector)
            {
                continue;
            }

            ulong available = checked(range.EndSector - startSector);
            ulong take = Math.Min(maximumSectorCount, available);
            take -= take % _logicalBlockSectors;
            if (take == 0)
            {
                break;
            }

            Consume(_free, index, startSector, take);
            extent = new LpExtent(
                take,
                LpExtentTargetType.Linear,
                startSector,
                targetSource);
            return true;
        }

        extent = default;
        return false;
    }

    private ulong AlignStart(uint targetSource, ulong start, ulong end)
    {
        LpBlockDevice device = _devices[checked((int)targetSource)];
        ulong candidate = RoundUp(start, _logicalBlockSectors);
        if (device.Alignment == 0)
        {
            return candidate;
        }

        ulong alignmentSectors = device.Alignment / LpFormat.SectorSize;
        ulong offsetSectors = device.AlignmentOffset / LpFormat.SectorSize;
        ulong attempts = alignmentSectors / GreatestCommonDivisor(
            alignmentSectors,
            _logicalBlockSectors);
        for (ulong attempt = 0; attempt < attempts && candidate < end; attempt++)
        {
            if (candidate % alignmentSectors == offsetSectors % alignmentSectors)
            {
                return candidate;
            }
            candidate = checked(candidate + _logicalBlockSectors);
        }
        return end;
    }

    private static void Consume(
        List<LpSectorRange> ranges,
        int index,
        ulong start,
        ulong count)
    {
        LpSectorRange range = ranges[index];
        ulong end = checked(start + count);
        ranges.RemoveAt(index);
        if (end < range.EndSector)
        {
            ranges.Insert(index, new LpSectorRange(
                range.TargetSource,
                end,
                checked(range.EndSector - end)));
        }
        if (range.StartSector < start)
        {
            ranges.Insert(index, new LpSectorRange(
                range.TargetSource,
                range.StartSector,
                checked(start - range.StartSector)));
        }
    }

    private static void AddOrMerge(
        List<LpExtent> extents,
        uint targetSource,
        ulong start,
        ulong count)
    {
        if (extents.Count != 0)
        {
            LpExtent previous = extents[^1];
            if (previous.TargetSource == targetSource &&
                previous.TargetType == LpExtentTargetType.Linear &&
                checked(previous.TargetData + previous.SectorCount) == start)
            {
                extents[^1] = previous with
                {
                    SectorCount = checked(previous.SectorCount + count)
                };
                return;
            }
        }
        extents.Add(new LpExtent(count, LpExtentTargetType.Linear, start, targetSource));
    }

    private static ulong RoundUp(ulong value, ulong alignment)
    {
        ulong remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static ulong GreatestCommonDivisor(ulong left, ulong right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }
        return left;
    }
}
