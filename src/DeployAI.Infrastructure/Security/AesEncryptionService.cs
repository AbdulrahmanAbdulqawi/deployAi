using System.Security.Cryptography;
using System.Text;
using DeployAI.Core.Security;
using DeployAI.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace DeployAI.Infrastructure.Security;

public sealed class AesEncryptionService : IEncryptionService
{
    private readonly byte[] _key;

    public AesEncryptionService(IOptions<EncryptionOptions> options)
    {
        var keyString = options.Value.Key;
        if (string.IsNullOrWhiteSpace(keyString))
        {
            throw new InvalidOperationException("Encryption key is not configured.");
        }

        _key = SHA256.HashData(Encoding.UTF8.GetBytes(keyString));
    }

    public byte[] Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = aes.EncryptCbc(plainBytes, aes.IV, PaddingMode.PKCS7);

        var result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);
        return result;
    }

    public string Decrypt(byte[] ciphertext)
    {
        if (ciphertext.Length < 17)
        {
            throw new CryptographicException("Invalid ciphertext.");
        }

        using var aes = Aes.Create();
        aes.Key = _key;

        var iv = new byte[16];
        Buffer.BlockCopy(ciphertext, 0, iv, 0, 16);
        var cipherBytes = new byte[ciphertext.Length - 16];
        Buffer.BlockCopy(ciphertext, 16, cipherBytes, 0, cipherBytes.Length);

        var plainBytes = aes.DecryptCbc(cipherBytes, iv, PaddingMode.PKCS7);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
