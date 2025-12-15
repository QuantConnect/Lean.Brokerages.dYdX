using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace QuantConnect.Brokerages.dYdX.Models.Converters;

public class OrderbookEntryArrayConverter : JsonConverter<List<OrderbookEntry>>
{
    public override List<OrderbookEntry> ReadJson(
        JsonReader reader, Type objectType, List<OrderbookEntry> existingValue,
        bool hasExistingValue, JsonSerializer serializer)
    {
        var result = new List<OrderbookEntry>();

        if (reader.TokenType == JsonToken.Null)
            return result;

        // Expecting the whole "bids"/"asks" array
        var array = JArray.Load(reader);
        foreach (var item in array)
        {
            if (item.Type == JTokenType.Array)
            {
                // on update
                // ["85747", "0.0001"]
                var a = (JArray)item;
                result.Add(new OrderbookEntry
                {
                    Price = ((string)a[0]).ToDecimal(),
                    Size = ((string)a[1]).ToDecimal()
                });
            }
            else if (item.Type == JTokenType.Object)
            {
                // on snapshot
                // { "price": "...", "size": "..." }
                result.Add(new OrderbookEntry
                {
                    Price = item["price"]!.ToObject<string>().ToDecimal(),
                    Size = item["size"]!.ToObject<string>().ToDecimal(),
                });
            }
        }

        return result;
    }

    public override void WriteJson(
        JsonWriter writer, List<OrderbookEntry> value, JsonSerializer serializer)
    {
        // If you only need deserialization, you can throw NotImplementedException here.
        // Otherwise, serialize back to the object form:
        serializer.Serialize(writer, value);
    }
}