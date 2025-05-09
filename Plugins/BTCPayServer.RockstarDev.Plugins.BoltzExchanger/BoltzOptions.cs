#nullable enable
using System;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

/// <summary>
///     Holds configuration options for the Boltz Exchanger plugin, parsed from the connection string.
/// </summary>
public class BoltzOptions
{
    public required Uri ApiUrl { get; init; }
    public required string SwapTo { get; init; } // Asset user receives (e.g., L-BTC)
    public required string[] SwapAddresses { get; init; }

    public bool IsTestnet => ApiUrl?.ToString().Contains(".testnet.") ?? false;
}
