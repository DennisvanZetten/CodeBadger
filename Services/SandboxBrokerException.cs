namespace CodeBadger.Services;

// Only fixed, credential-free messages may cross the sandbox boundary.
public sealed class SandboxBrokerException(string safeMessage, Exception? innerException = null)
    : Exception(safeMessage, innerException);
