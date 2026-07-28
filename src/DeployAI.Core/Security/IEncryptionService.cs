namespace DeployAI.Core.Security;

/// <summary>
/// Encrypts secrets at rest - GitHub tokens, provider credential tokens - before storing them in
/// the database, and decrypts them back when a call actually needs to use them.
/// </summary>
public interface IEncryptionService
{
    /// <summary>Encrypts a plaintext secret for storage.</summary>
    byte[] Encrypt(string plaintext);

    /// <summary>Decrypts a previously encrypted secret.</summary>
    string Decrypt(byte[] ciphertext);
}
