using System;

namespace QuantConnect.Brokerages.dYdX;

public partial class dYdXBrokerage
{
    private static uint RandomUInt32()
    {
        return (uint)(Random.Shared.Next(1 << 30)) << 2 | (uint)(Random.Shared.Next(1 << 2));
    }

}