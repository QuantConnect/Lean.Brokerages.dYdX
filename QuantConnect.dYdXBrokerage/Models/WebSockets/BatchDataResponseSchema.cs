using System.Collections.Generic;

namespace QuantConnect.Brokerages.dYdX.Models.WebSockets;

public class BatchDataResponseSchema<T> : DataResponseSchema<IEnumerable<T>>
{
}