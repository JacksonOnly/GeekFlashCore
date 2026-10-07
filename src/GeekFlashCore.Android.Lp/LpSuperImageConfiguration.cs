using GeekFlashCore.Android.Lp.Abstractions;

namespace GeekFlashCore.Android.Lp;

/// <summary>Configuration for a new, single-device Super image layout; defaults follow lpmake.</summary>
public sealed record LpSuperImageConfiguration
{
    /// <summary>Physical Super capacity in bytes.</summary>
    public long DeviceSize { get; init; }
    /// <summary>Metadata size, slot count and logical block size.</summary>
    public LpGeometry Geometry { get; init; } = new(65536, 2, 4096);
    /// <summary>Physical block-device name.</summary>
    public string DeviceName { get; init; } = "super";
    /// <summary>Partition alignment in bytes; zero uses the logical block size.</summary>
    public uint Alignment { get; init; } = 1024 * 1024;
    /// <summary>Alignment offset within the parent device in bytes.</summary>
    public uint AlignmentOffset { get; init; }
    /// <summary>Whether the metadata carries the Virtual A/B device flag.</summary>
    public bool VirtualAb { get; init; }
}

/// <summary>A logical partition, including explicit empty partitions in the inactive slot.</summary>
public sealed record LpSuperPartitionDefinition(string Name, long Size, string GroupName = "default",
    LpPartitionAttributes Attributes = LpPartitionAttributes.ReadOnly);

/// <summary>A named partition group with a byte capacity; zero denotes an unlimited group.</summary>
public sealed record LpSuperGroupDefinition(string Name, ulong MaximumSize = 0);
