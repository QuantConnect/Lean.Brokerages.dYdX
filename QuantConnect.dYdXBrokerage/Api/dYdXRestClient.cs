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
using Newtonsoft.Json;
using QuantConnect.Logging;
using QuantConnect.Util;
using RestSharp;

namespace QuantConnect.Brokerages.dYdX.Api;

public class dYdXRestClient(string baseUrl) : IDisposable
{
    private readonly RestClient _restClient = new(baseUrl);
    private readonly RateGate _rateGate = new(250, TimeSpan.FromMinutes(1));

    public T Get<T>(string path)
    {
        _rateGate.WaitToProceed();
        var result = _restClient.Execute(new RestRequest(path));
        return EnsureSuccessAndParse<T>(result);
    }

    /// <summary>
    /// Ensures the request executed successfully and returns the parsed business object
    /// </summary>
    /// <param name="response">The response to parse</param>
    /// <typeparam name="T">The type of the response business object</typeparam>
    /// <returns>The parsed response business object</returns>
    /// <exception cref="Exception"></exception>
    [StackTraceHidden]
    private T EnsureSuccessAndParse<T>(IRestResponse response)
    {
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new Exception("dYdXRestClient request failed: " +
                                $"[{(int)response.StatusCode}] {response.StatusDescription}, " +
                                $"Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
        }

        T responseObject = default;
        try
        {
            responseObject = JsonConvert.DeserializeObject<T>(response.Content);
        }
        catch (Exception e)
        {
            throw new Exception("dYdXRestClient failed deserializing response: " +
                                $"[{(int)response.StatusCode}] {response.StatusDescription}, " +
                                $"Content: {response.Content}, ErrorMessage: {response.ErrorMessage}", e);
        }

        if (Log.DebuggingEnabled)
        {
            Log.Debug(
                $"dYdX request for {response.Request.Resource} executed successfully. Response: {response.Content}");
        }

        return responseObject;
    }

    public void Dispose()
    {
        _rateGate?.DisposeSafely();
    }
}