using System;
using System.Text;
using System.Security.Cryptography;
using Godot;

namespace LumoraVR.Source.Management
{
    public partial class LocalDatabase
    {
        private void GenerateEncryptionKey()
        {
            // Use only machine-specific identifiers, not process-specific ones as i kept breaking shit
            var machineId = OS.GetUniqueId();

            // Use a constant salt instead of process-specific data
            var salt = new byte[]
            {
                0xF3, 0x91, 0x5E, 0xD8, 0x2C, 0x7A, 0x4B, 0x9F,
                0x1D, 0x6E, 0xA0, 0xB5, 0x8C, 0x3F, 0x92, 0x4D,
                0xE7, 0x0B, 0x6C, 0xA8, 0x5F, 0x2D, 0x9E, 0x1B,
                0x7C, 0x4A, 0xD3, 0x8B, 0x0E, 0x6F, 0xA2, 0xC5
            };

            using var deriveBytes = new Rfc2898DeriveBytes(
                Encoding.UTF8.GetBytes(machineId),
                salt,
                10000,
                HashAlgorithmName.SHA256);

            _encryptionKey = deriveBytes.GetBytes(32);
            _iv = deriveBytes.GetBytes(16);
        }

        private byte[] EncryptData(string data)
        {
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            aes.IV = _iv;

            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(data);
            return encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
        }

        private string DecryptData(byte[] encryptedData)
        {
            using var aes = Aes.Create();
            aes.Key = _encryptionKey;
            aes.IV = _iv;

            using var decryptor = aes.CreateDecryptor();
            var decryptedBytes = decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
    }
}