/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

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
                    Price = a[0].ToObject<decimal>(),
                    Size = a[1].ToObject<decimal>()
                });
            }
            else if (item.Type == JTokenType.Object)
            {
                // on snapshot
                // { "price": "...", "size": "..." }
                result.Add(item.ToObject<OrderbookEntry>());
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