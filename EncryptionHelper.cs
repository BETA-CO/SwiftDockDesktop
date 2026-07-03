using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SwiftDock
{
    public static class EncryptionHelper
    {
        private static byte[] DeriveKey(string token)
        {
            using (var sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
            }
        }

        public static string Encrypt(string plainText, string token)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            if (string.IsNullOrEmpty(token)) return plainText; // Fallback if no token (e.g. initial discovery)

            try
            {
                byte[] key = DeriveKey(token);
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.GenerateIV();
                    byte[] iv = aes.IV;

                    using (var encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
                    using (var ms = new MemoryStream())
                    {
                        ms.Write(iv, 0, iv.Length); // Prepend IV
                        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        {
                            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                            cs.Write(plainBytes, 0, plainBytes.Length);
                            cs.FlushFinalBlock();
                        }
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Encryption error: {ex.Message}");
                return plainText; // Safe fallback
            }
        }

        public static string Decrypt(string cipherText, string token)
        {
            if (string.IsNullOrEmpty(cipherText)) return "";
            if (string.IsNullOrEmpty(token)) return cipherText; // Fallback if no token

            try
            {
                byte[] combined = Convert.FromBase64String(cipherText);
                if (combined.Length < 16) return cipherText; // Invalid payload

                byte[] iv = new byte[16];
                byte[] cipherBytes = new byte[combined.Length - 16];
                Buffer.BlockCopy(combined, 0, iv, 0, 16);
                Buffer.BlockCopy(combined, 16, cipherBytes, 0, cipherBytes.Length);

                byte[] key = DeriveKey(token);
                using (var aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using (var decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
                    using (var ms = new MemoryStream(cipherBytes))
                    using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                    using (var sr = new StreamReader(cs, Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Decryption error: {ex.Message}");
                return cipherText; // Safe fallback
            }
        }
    }
}
