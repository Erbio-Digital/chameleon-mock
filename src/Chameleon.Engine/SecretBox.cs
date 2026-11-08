using System.Security.Cryptography;
using System.Text;

namespace Chameleon.Engine;

/// AES-256-GCM for the secrets Chameleon must show again (issued connector credentials).
/// Key from CHAMELEON_SECRET_KEY (base64, 32 bytes), never a default in code.
/// Output: base64(nonce 12 | tag 16 | ciphertext).
public sealed class SecretBox
{
    public const string KeyVariable = "CHAMELEON_SECRET_KEY";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public SecretBox(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
            throw new InvalidOperationException($"{KeyVariable} is required (base64 of 32 bytes)");
        try
        {
            _key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{KeyVariable} is invalid: use base64 of 32 bytes");
        }
        if (_key.Length != 32) throw new InvalidOperationException($"{KeyVariable} is invalid: use base64 of 32 bytes");
    }

    public string Encrypt(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        var output = new byte[NonceSize + TagSize + data.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize));
        return Convert.ToBase64String(output);
    }

    public string Decrypt(string sealedValue)
    {
        var input = Convert.FromBase64String(sealedValue);
        var plain = new byte[input.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize), input.AsSpan(NonceSize, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
