using System;

namespace QuantConnect.Brokerages.dYdX.Exceptions;

public class AuthenticatorKeyMismatchException() : Exception("No matching authenticator found for the provided API key.");