using System;

namespace QuantConnect.Brokerages.dYdX.Exceptions;

public class AuthenticatorNotFoundException() : Exception("No authenticators exist for this account. An authenticator must be created and registered on the dYdX network before trading.");