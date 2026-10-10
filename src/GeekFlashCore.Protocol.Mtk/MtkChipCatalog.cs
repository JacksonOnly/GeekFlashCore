// SPDX-License-Identifier: AGPL-3.0-or-later
// Standard chip metadata: B. Kerler, mtkclient/config/brom_config.py and mtk_config.py, GPLv3.
// Watchdog groups mirror mtkclient brom_config; UART0 base groups follow acon
// (https://github.com/shomykohai/acon, Shomy, AGPL-3.0-or-later). Unknown hardware
// receives no guessed addresses.
namespace GeekFlashCore.Protocol.Mtk;

/// <summary>Known standard metadata, excluding exploit addresses and payloads. Uart0 is the
/// standard UART0 base used for loader diagnostics; it is not an exploit parameter.</summary>
public sealed record MtkChipDescriptor(string Name, string? Description, ushort DaHardwareCode,
    MtkChipProfile? Watchdog, uint? Uart0 = null);

/// <summary>Reference chip names, DA aliases and explicitly known ordinary watchdog registers.</summary>
public static class MtkChipCatalog
{
    // (hardware code, name, description, DA alias; a zero alias maps the code to itself).
    private static readonly (ushort Code, string Name, string? Description, ushort DaCode)[] Models =
    [
        (0x571, "MT0571", null, 0x571),
        (0x598, "ELBRUS/MT0598", null, 0x598),
        (0x992, "MT6880/MT6890", null, 0x992),
        (0x2601, "MT2601", null, 0x2601),
        (0x3967, "MT3967", null, 0x3967),
        (0x6255, "MT6255", null, 0x6255),
        (0x6261, "MT6261", null, 0x6261),
        (0x6280, "MT6280", null, 0x6280),
        (0x6516, "MT6516", null, 0x6516),
        (0x633, "MT6570/MT8321", null, 0x6570),
        (0x6571, "MT6571", null, 0x6571),
        (0x6572, "MT6572", null, 0x6572),
        (0x6573, "MT6573/MT6260", null, 0x6573),
        (0x6575, "MT6575/MT6577/MT8317", null, 0x6575),
        (0x6577, "MT6577", null, 0x6577),
        (0x6580, "MT6580", null, 0x6580),
        (0x6582, "MT6582/MT6574/MT8382", null, 0x6582),
        (0x6583, "MT6583/6589", null, 0x6589),
        (0x6592, "MT6592/MT8392", null, 0x6592),
        (0x6595, "MT6595", null, 0x6595),
        (0x321, "MT6735/T,MT8735A", null, 0x6735),
        (0x335, "MT6737M/MT6735G", null, 0x6735),
        (0x699, "MT6739/MT6731/MT8765", null, 0x6739),
        (0x601, "MT6750", null, 0x6755),
        (0x6752, "MT6752", null, 0x6752),
        (0x337, "MT6753", null, 0x6735),
        (0x326, "MT6755/MT6750/M/T/S", "Helio P10/P15/P18", 0x6755),
        (0x551, "MT6757/MT6757D", "Helio P20", 0x6757),
        (0x688, "MT6758", "Helio P30", 0x6758),
        (0x507, "MT6759", "Helio P30", 0x6758),
        (0x717, "MT6761/MT6762/MT3369/MT8766B", "Helio A20/P22/A22/A25/G25", 0x6761),
        (0x690, "MT6763", "Helio P23", 0x6763),
        (0x766, "MT6765/MT8768t", "Helio P35/G35", 0x6765),
        (0x707, "MT6768/MT6769", "Helio P65/G85 k68v1", 0x6768),
        (0x788, "MT6771/MT8385/MT8183/MT8666", "Helio P60/P70/G80", 0x6771),
        (0x725, "MT6779", "Helio P90 k79v1", 0x6779),
        (0x1066, "MT6781", "Helio G96", 0x6781),
        (0x813, "MT6785", "Helio G90", 0x6785),
        (0x6795, "MT6795", "Helio X10", 0x6795),
        (0x279, "MT6797/MT6767", "Helio X23/X25/X27", 0x6797),
        (0x562, "MT6799", "Helio X30/X35", 0x6799),
        (0x989, "MT6833", "Dimensity 700 5G k6833", 0x6833),
        (0x996, "MT6853", "Dimensity 720 5G", 0x6853),
        (0x886, "MT6873", "Dimensity 800/820 5G", 0x6873),
        (0x959, "MT6877/MT6877V", "Dimensity 900/1080", 0x6877),
        (0x816, "MT6885/MT6883/MT6889/MT6880/MT6890", "Dimensity 1000L/1000", 0x6885),
        (0x950, "MT6893", "Dimensity 1200", 0x6893),
        (0x907, "MT6983", "Dimensity 9000/9000+", 0x907),
        (0x1129, "MT6855", "Dimensity 8100", 0x1129),
        (0x1172, "MT6895", "Dimensity 8200", 0x1172),
        (0x1203, "MT6897", "Dimensity 8300 Ultra", 0x1203),
        (0x1208, "MT6789/MT8781V", "MTK Helio G99", 0x1208),
        (0x1229, "MT6886", "Dimensity 7200 Ultra", 0x1229),
        (0x1296, "MT6985", "Dimensity 9200/9200+", 0x1296),
        (0x8127, "MT8127/MT3367", null, 0x8127),
        (0x8135, "MT8135", null, 0x8135),
        (0x8163, "MT8163", null, 0x8163),
        (0x8167, "MT8167/MT8516/MT8362", null, 0x8167),
        (0x8168, "MT8168/MT6357", null, 0x8168),
        (0x8172, "MT8173", null, 0x8173),
        (0x8176, "MT8176", null, 0x8173),
        (0x930, "MT8195 Chromebook", null, 0x8195),
        (0x8512, "MT8512", null, 0x8512),
        (0x8518, "MT8518 VoiceAssistant", null, 0x8518),
        (0x8590, "MT8590/MT7683/MT8521/MT7623", null, 0x8590),
        (0x8695, "MT8695", null, 0x8695),
        (0x908, "MT8696", null, 0x8696),
        // Newer SoCs catalogued by acon (https://github.com/shomykohai/acon); the legacy
        // mtkclient-derived table above does not list them.
        (0x1209, "MT6835", "Dimensity 6100+/6300/6400", 0),
        (0x1585, "MT6858", "Dimensity 7100/7300e", 0),
        (0x1375, "MT6878", "Dimensity 7300/7300X/7360/7400/7400X", 0),
        (0x1007, "MT6879", "Dimensity 1050/7030", 0),
        (0x6899, "MT6899", "Dimensity 8400/8400-Ultra/8400-Turbo/8450/8500/8550", 0),
        (0x1236, "MT6989", "Dimensity 9300/9300+/9400e", 0),
        (0x1357, "MT6991", "Dimensity 9400/9400+/9500s", 0),
        (0x1471, "MT6993", "Dimensity 9500", 0),
        (0x1529, "MT6995", "Dimensity 9600", 0),
        (0x8188, "MT8188", "MediaTek Kompanio 838", 0),
    ];

    // Watchdog (TOPRGU) groups sharing one address/value/width.
    private static readonly (ushort[] Codes, uint Address, uint Value, int Width)[] Watchdogs =
    [
        ([0x571, 0x992, 0x2601, 0x633, 0x6572, 0x6580, 0x6582, 0x6595, 0x699, 0x601, 0x6752, 0x326, 0x551,
          0x717, 0x690, 0x766, 0x707, 0x788, 0x725, 0x1066, 0x813, 0x1208, 0x6795, 0x279, 0x989, 0x996,
          0x886, 0x959, 0x816, 0x950, 0x8127, 0x8163, 0x8167, 0x8168, 0x8172, 0x8176, 0x930, 0x8512,
          0x8590, 0x8695, 0x908], 0x10007000, 0x22000064, 32),
        ([0x598, 0x688, 0x562], 0x10211000, 0x22000064, 32),
        ([0x6516], 0x10003000, 0x22000064, 32),
        ([0x6571], 0x10007400, 0x22000000, 32),
        ([0x6573], 0x70025000, 0x22000064, 32),
        ([0x6575, 0x6577], 0xc0000000, 0x2264, 16),
        ([0x6583, 0x8135], 0x10000000, 0x22000064, 32),
        ([0x321, 0x335, 0x337], 0x10212000, 0x22000000, 32),
        ([0x507], 0x10210000, 0x22000064, 32),
        ([0x907, 0x1129, 0x1172, 0x1203, 0x1229, 0x1296], 0x1c007000, 0x22000064, 32),
        // Watchdog addresses for the newer SoCs follow acon's toprgu groups.
        ([0x6858, 0x1375, 0x6879], 0x1c00a000, 0x22000064, 32),
        ([0x6899, 0x1236], 0x1c00b000, 0x22000064, 32),
        ([0x1357, 0x1471, 0x1529], 0x1c010000, 0x22000064, 32),
    ];

    // UART0 base groups from acon. Hardware codes absent from acon keep no guessed address.
    private static readonly (ushort[] Codes, uint Base)[] Uarts =
    [
        ([0x633, 0x6572], 0x11005000),
        ([0x6575, 0x6577], 0xffffff00),
        ([0x6580, 0x6582, 0x6595, 0x321, 0x699, 0x337, 0x326, 0x551, 0x717, 0x690, 0x766, 0x707, 0x788,
          0x725, 0x1066, 0x813, 0x1208, 0x279, 0x562, 0x989, 0x996, 0x886, 0x959, 0x816, 0x950], 0x11002000),
        ([0x907, 0x1129, 0x1172, 0x1203, 0x1229, 0x1296, 0x1209, 0x1585, 0x1375, 0x1007, 0x6899, 0x1236],
          0x11001000),
        ([0x1357, 0x1471, 0x1529], 0x16000000),
        ([0x8188], 0x11001100),
        ([0x908], 0x11002400),
    ];

    private static readonly IReadOnlyDictionary<ushort, MtkChipDescriptor> Chips = Build();

    private static Dictionary<ushort, MtkChipDescriptor> Build()
    {
        var watchdogs = new Dictionary<ushort, (uint Address, uint Value, int Width)>();
        foreach (var (codes, address, value, width) in Watchdogs)
            foreach (ushort code in codes)
                watchdogs[code] = (address, value, width);
        var uarts = new Dictionary<ushort, uint>();
        foreach (var (codes, baseAddress) in Uarts)
            foreach (ushort code in codes)
                uarts[code] = baseAddress;
        var chips = new Dictionary<ushort, MtkChipDescriptor>(Models.Length);
        foreach (var (code, name, description, daCode) in Models)
            chips[code] = new(name, description, daCode == 0 ? code : daCode,
                watchdogs.TryGetValue(code, out var watchdog)
                    ? new(code, watchdog.Address, watchdog.Value,
                        SejBase: code switch { 0x1172 => 0x1c009000, 0x950 => 0x1000a000, _ => 0 },
                        WatchdogWidth: watchdog.Width)
                    : null,
                uarts.TryGetValue(code, out var uart) ? uart : null);
        return chips;
    }

    /// <summary>Returns null for unknown hardware; no register address is guessed.</summary>
    public static MtkChipDescriptor? Find(ushort hardwareCode) => Chips.GetValueOrDefault(hardwareCode);

    /// <summary>Resolves an explicit override, the probe snapshot, known metadata, then the raw hardware code.</summary>
    public static ushort GetDaHardwareCode(MtkTargetInfo target, ushort? explicitAlias = null) =>
        explicitAlias ?? target.DaHardwareCode ?? Find(target.HardwareCode)?.DaHardwareCode ?? target.HardwareCode;
}
