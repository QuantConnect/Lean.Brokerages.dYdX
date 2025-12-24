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
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using Newtonsoft.Json;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.dYdX.Api;

public class dYdXRestClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly RateGate _rateGate;

    public dYdXRestClient(string baseUrl, RateGate rateGate)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/")
        };
        _rateGate = rateGate;
    }

    public T Get<T>(string path)
    {
        _rateGate.WaitToProceed();

        if (!_httpClient.TryDownloadData(path, out var content, out var code))
        {
            throw new Exception("dYdXRestClient request failed: " +
                                $"[{(int)(code ?? HttpStatusCode.BadRequest)}], " +
                                $"Content: {content}");
        }

        var result = Parse<T>(content);

        if (Log.DebuggingEnabled)
        {
            Log.Debug(
                $"dYdX request for '{_httpClient.BaseAddress?.AbsoluteUri.TrimEnd('/')}/{path.TrimStart('/')}' executed successfully. Response: {content}");
        }

        return result;
    }

    /// <summary>
    /// Parse business object
    /// </summary>
    /// <param name="content">The content to parse</param>
    /// <typeparam name="T">The type of the response business object</typeparam>
    /// <returns>The parsed response business object</returns>
    /// <exception cref="Exception"></exception>
    [StackTraceHidden]
    private T Parse<T>(string content)
    {
        T responseObject = default;
        try
        {
            responseObject = JsonConvert.DeserializeObject<T>(content);
        }
        catch (Exception e)
        {
            throw new Exception("dYdXRestClient failed deserializing response: " +
                                $"Content: {content}", e);
        }

        return responseObject;
    }

    public void Dispose()
    {
        _rateGate?.DisposeSafely();
        _httpClient?.DisposeSafely();
    }
}