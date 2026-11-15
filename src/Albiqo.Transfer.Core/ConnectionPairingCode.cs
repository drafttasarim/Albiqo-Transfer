namespace Albiqo.Transfer.Core;

/// <summary>Authorization belongs to one live physical link, never a saved device identity.</summary>
public sealed class ConnectionPairingCode : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationToken connectionLost;
    private readonly CancellationTokenRegistration registration;
    private string? code;

    public ConnectionPairingCode(string code, CancellationToken connectionLost)
    {
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("Enter the six-digit code shown in Albiqo.", nameof(code));
        this.code = code;
        this.connectionLost = connectionLost;
        registration = connectionLost.Register(Clear);
    }

    public bool IsActive { get { lock (gate) return code is not null && !connectionLost.IsCancellationRequested; } }

    public bool TryGet(out string value)
    {
        lock (gate)
        {
            if (code is not null && !connectionLost.IsCancellationRequested) { value = code; return true; }
            code = null; value = ""; return false;
        }
    }

    public void Clear() { lock (gate) code = null; }

    public void Dispose() { Clear(); registration.Dispose(); }
}

public sealed class PairingCodeRejectedException(string message) : IOException(message);
