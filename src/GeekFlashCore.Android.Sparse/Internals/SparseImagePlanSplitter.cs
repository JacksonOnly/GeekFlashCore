using System.Collections.ObjectModel;
using GeekFlashCore.Android.Sparse.Constants;
using GeekFlashCore.Android.Sparse.Models;
using GeekFlashCore.Android.Sparse.Types;

namespace GeekFlashCore.Android.Sparse.Internals;


internal static class SparseImagePlanSplitter
{
    public static IReadOnlyList<SparseImageWritePlan> Split(
        SparseImageWritePlan plan,
        long maximumEncodedLength)
    {
        if (plan.EncodedLength <= maximumEncodedLength)
            return Array.AsReadOnly([plan]);

        var builder = new SplitPlanBuilder(plan);
        foreach (SparseImageWriteChunk chunk in plan.ChunkSpan)
        {
            switch (chunk.Type)
            {
                case SparseChunkType.Raw:
                    builder.AddRaw(chunk, maximumEncodedLength);
                    break;
                case SparseChunkType.Fill:
                    builder.AddFill(chunk, maximumEncodedLength);
                    break;
                case SparseChunkType.DontCare:
                    break;
                default:
                    throw new InvalidDataException(Strings.UnsupportedChunkType);
            }
        }

        return builder.Complete(maximumEncodedLength);
    }

    private sealed class SplitPlanBuilder(SparseImageWritePlan sourcePlan)
    {
        private readonly List<SparseImageWritePlan> _parts = [];
        private readonly List<SparseImageWriteChunk> _chunks = [];
        private long _encodedLength = SparseConstant.HeaderLength;
        private uint _currentBlock;
        private bool _hasData;

        public void AddRaw(
            SparseImageWriteChunk chunk,
            long maximumEncodedLength)
        {
            uint startBlock = chunk.StartBlock;
            uint remainingBlocks = chunk.BlockCount;
            while (remainingBlocks != 0)
            {
                uint gapBlocks = checked(startBlock - _currentBlock);
                long fixedLength = checked(
                    _encodedLength +
                    (gapBlocks == 0 ? 0 : SparseConstant.ChunkLength) +
                    SparseConstant.ChunkLength);
                uint endBlock = checked(startBlock + remainingBlocks);
                long fullLength = checked(
                    fixedLength +
                    (long)remainingBlocks * sourcePlan.BlockSize +
                    (endBlock == sourcePlan.TotalBlocks ? 0 : SparseConstant.ChunkLength));

                if (fullLength <= maximumEncodedLength)
                {
                    AppendGap(gapBlocks);
                    AppendData(new SparseImageWriteChunk(
                        SparseChunkType.Raw,
                        startBlock,
                        remainingBlocks,
                        0));
                    return;
                }

                long available = maximumEncodedLength - fixedLength - SparseConstant.ChunkLength;
                long fittingBlocks = available / sourcePlan.BlockSize;
                if (fittingBlocks <= 0)
                {
                    if (_hasData)
                    {
                        FinishPart(maximumEncodedLength);
                        continue;
                    }

                    throw new ArgumentOutOfRangeException(nameof(maximumEncodedLength));
                }

                uint blockCount = (uint)Math.Min(remainingBlocks, fittingBlocks);
                AppendGap(gapBlocks);
                AppendData(new SparseImageWriteChunk(
                    SparseChunkType.Raw,
                    startBlock,
                    blockCount,
                    0));
                startBlock = checked(startBlock + blockCount);
                remainingBlocks -= blockCount;
                if (remainingBlocks != 0)
                    FinishPart(maximumEncodedLength);
            }
        }

        public void AddFill(
            SparseImageWriteChunk chunk,
            long maximumEncodedLength)
        {
            while (true)
            {
                uint gapBlocks = checked(chunk.StartBlock - _currentBlock);
                uint endBlock = checked(chunk.StartBlock + chunk.BlockCount);
                long length = checked(
                    _encodedLength +
                    (gapBlocks == 0 ? 0 : SparseConstant.ChunkLength) +
                    SparseConstant.ChunkLength + sizeof(uint) +
                    (endBlock == sourcePlan.TotalBlocks ? 0 : SparseConstant.ChunkLength));
                if (length <= maximumEncodedLength)
                {
                    AppendGap(gapBlocks);
                    AppendData(chunk);
                    return;
                }

                if (!_hasData)
                    throw new ArgumentOutOfRangeException(nameof(maximumEncodedLength));
                FinishPart(maximumEncodedLength);
            }
        }

        public ReadOnlyCollection<SparseImageWritePlan> Complete(long maximumEncodedLength)
        {
            if (_hasData)
            {
                FinishPart(maximumEncodedLength);
            }
            else if (_parts.Count == 0)
            {
                _chunks.Add(new SparseImageWriteChunk(
                    SparseChunkType.DontCare,
                    0,
                    sourcePlan.TotalBlocks,
                    0));
                _encodedLength = checked(_encodedLength + SparseConstant.ChunkLength);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(
                    _encodedLength,
                    maximumEncodedLength,
                    nameof(maximumEncodedLength));
                AddPlan();
            }

            return _parts.AsReadOnly();
        }

        private void AppendGap(uint blockCount)
        {
            if (blockCount == 0)
                return;
            _chunks.Add(new SparseImageWriteChunk(
                SparseChunkType.DontCare,
                _currentBlock,
                blockCount,
                0));
            _currentBlock = checked(_currentBlock + blockCount);
            _encodedLength = checked(_encodedLength + SparseConstant.ChunkLength);
        }

        private void AppendData(SparseImageWriteChunk chunk)
        {
            _chunks.Add(chunk);
            _currentBlock = checked(chunk.StartBlock + chunk.BlockCount);
            _encodedLength = checked(
                _encodedLength + SparseConstant.ChunkLength +
                (chunk.Type == SparseChunkType.Raw
                    ? (long)chunk.BlockCount * sourcePlan.BlockSize
                    : sizeof(uint)));
            _hasData = true;
        }

        private void FinishPart(long maximumEncodedLength)
        {
            uint trailingBlocks = sourcePlan.TotalBlocks - _currentBlock;
            AppendGap(trailingBlocks);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(
                _encodedLength,
                maximumEncodedLength,
                nameof(maximumEncodedLength));
            AddPlan();
            _chunks.Clear();

            _encodedLength = SparseConstant.HeaderLength;
            _currentBlock = 0;
            _hasData = false;
        }

        private void AddPlan()
        {
            if (_chunks.Count > ushort.MaxValue)
                throw new InvalidDataException(Strings.ChunkLimitExceeded);
            _parts.Add(new SparseImageWritePlan(
                sourcePlan.SourceLength,
                sourcePlan.RawLength,
                sourcePlan.BlockSize,
                sourcePlan.TotalBlocks,
                [.. _chunks],
                _encodedLength,
                false,
                0));
        }
    }
}
