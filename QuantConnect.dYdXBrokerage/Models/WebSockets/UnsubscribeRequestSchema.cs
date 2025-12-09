namespace QuantConnect.Brokerages.dYdX.Models.WebSockets;

public class UnsubscribeRequestSchema : BaseRequestSchema
{
    public override string Type => "unsubscribe";
}