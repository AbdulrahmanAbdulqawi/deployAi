namespace DeployAI.Core.Security;

public interface IEncryptionService
{
    byte[] Encrypt(string plaintext);
    string Decrypt(byte[] ciphertext);
}
